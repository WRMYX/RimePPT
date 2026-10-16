using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;

namespace RimePPT.Core
{
    /// <summary>
    /// PowerPoint COM 控制器：专用 STA 线程跑消息泵，全部 COM 访问收敛在该线程。
    /// 主程序不依赖 Office Interop 程序集，用 250ms 轮询作为后备；可选 COM 加载项通过命名事件唤醒检测线程。轮询
    /// SlideShowWindows.Count / 页码派生 ShowStarted/SlideChanged/ShowEnded。
    /// UI 线程调用 Next/Previous/Exit 时经线程消息 marshal 过来。
    /// </summary>
    public class PowerPointController : IPresentationController
    {
        private readonly string[] _programIds;
        public PowerPointController() : this(new[] { "PowerPoint.Application" }) { }
        protected PowerPointController(string[] programIds) => _programIds = programIds;
        private const uint WM_APP_WORK = 0x8001;  // WM_APP + 1：执行队列
        private const uint WM_QUIT = 0x0012;
        private const uint PM_REMOVE = 0x0001;
        private const uint PollIntervalMs = 250;
        private const int RescanIntervalPolls = 8; // ≈2s 重扫一次其他实例

        private Thread? _thread;
        private uint _threadId;
        private volatile bool _running;
        private readonly ConcurrentQueue<Action> _work = new();
        private EventWaitHandle? _officeWake;
        private PowerPointSlideShowWindowWatcher? _windowWatcher;
        private long _fastDetectionTimestamp;
        private readonly object _sessionGate = new();
        private long _sessionId;
        private System.IO.MemoryMappedFiles.MemoryMappedFile? _officeState;
        private long _officeSequence;
        private volatile bool _comReady;
        private sealed record DocumentSnapshot(int ProcessId, string Path, string Name, int Count, int Slide, bool Active);
        private sealed record CachedDocuments(List<DocumentSnapshot> Items, long UpdatedAt);
        // COM identities are only created/accessed on the owning STA. UI consumers read snapshots under the lock.
        private readonly Dictionary<object, CachedDocuments> _documents = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, long> _diagnosticTimes = new();
        private volatile bool _scanDocuments = true;
        internal long SessionVersion { get { lock (_sessionGate) return _sessionId; } }
        internal string ConnectionState => !IsPresenting ? "Idle" : _comReady ? "Ready" : "ComEnriching";

        // —— 以下状态仅 COM 线程访问 ——
        private object? _ppt;
        private bool _wasPresenting;
        private int _lastSlide = -1;
        private int _rescanCounter;
        private int _tick;

        // —— 供 UI 线程读取的快照 ——
        private volatile int _currentSlide;
        private volatile int _slideCount;
        private volatile IntPtr _hwnd;
        private volatile bool _isPresenting;
        private string? _showPath;

        public event EventHandler? ShowStarted;

        public event EventHandler<SlideEndedReason>? ShowEnded;

        public event EventHandler<int>? SlideChanged;

        public int CurrentSlide => _currentSlide;

        public int SlideCount => _slideCount;

        public IntPtr ShowWindowHandle => _hwnd;

        public string? ShowFilePath => _showPath;

        public bool IsPresenting => _isPresenting;

        public Task<string> GetOfficeAddinStatusAsync() => RunOnComThread(() =>
        {
            if (_ppt is null) return "尚未连接 PowerPoint；请打开 PowerPoint 后刷新。";
            try
            {
                dynamic addin = ((dynamic)_ppt).COMAddIns.Item("RimePPT.PowerPointAddin");
                return (bool)addin.Connect ? "PowerPoint 已加载并连接 RimePPT 加载项。" : "PowerPoint 已识别加载项，但当前未连接；请检查 COM 加载项是否勾选。";
            }
            catch (COMException) { return "PowerPoint 尚未识别加载项，或当前忙碌；注册后请重新打开 PowerPoint。"; }
        });

        public void Start()
        {
            if (_thread is not null)
            {
                return;
            }

            _running = true;
            _thread = new Thread(ThreadProc)
            {
                IsBackground = true,
                Name = "RimePPT.PowerPointCOM",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (Array.IndexOf(_programIds, "PowerPoint.Application") < 0) return;
            _windowWatcher = new PowerPointSlideShowWindowWatcher();
            _windowWatcher.WindowShown += OnSlideShowWindowShown;
            _windowWatcher.WindowHidden += OnSlideShowWindowHidden;
            _windowWatcher.ProgramChanged += () =>
            {
                _scanDocuments = true;
                try { _officeWake?.Set(); } catch (ObjectDisposedException) { }
            };
            _windowWatcher.Start();
        }

        private void OnSlideShowWindowShown(IntPtr window)
        {
            lock (_sessionGate)
            {
                if (_isPresenting) return;
                ++_sessionId;
                _hwnd = window;
                _showPath = null;
                _currentSlide = 1;
                _slideCount = 0;
                GetWindowThreadProcessId(window, out uint pid);
                var documents = new List<DocumentSnapshot>();
                foreach (var cached in _documents.Values)
                    if (Stopwatch.GetElapsedTime(cached.UpdatedAt) < TimeSpan.FromSeconds(10))
                        documents.AddRange(cached.Items.FindAll(d => d.ProcessId == 0 || d.ProcessId == (int)pid));
                if (documents.Count > 0)
                {
                    var title = new System.Text.StringBuilder(512);
                    GetWindowText(window, title, title.Capacity);
                    var matches = documents.FindAll(d => title.ToString().Contains(Path.GetFileNameWithoutExtension(d.Name), StringComparison.OrdinalIgnoreCase));
                    var document = matches.Count == 1 ? matches[0] : null;
                    if (document is not null) { _showPath = document.Path; _slideCount = document.Count; _currentSlide = document.Slide; }
                }
                _comReady = false;
                _isPresenting = _wasPresenting = true;
                _lastSlide = -1;
                _fastDetectionTimestamp = Stopwatch.GetTimestamp();
            }
            PresentationDiagnostics.Record("放映窗口", $"检测到 hwnd={window}; session={_sessionId}");
            Log($"show window detected: hwnd={window}");
            ShowStarted?.Invoke(this, EventArgs.Empty);
            try { _officeWake?.Set(); } catch (ObjectDisposedException) { }
        }

        private void OnSlideShowWindowHidden(IntPtr window)
        {
            if (!_isPresenting || _hwnd != window) return;
            // PowerPoint can briefly hide/re-show the same screenClass while changing display
            // mode. Debounce that transition so the toolbar session is not torn down.
            long session; lock (_sessionGate) session = _sessionId;
            _ = DebounceAsync();
            async Task DebounceAsync()
            {
                await Task.Delay(250);
                if (!_isPresenting || _hwnd != window || IsWindowVisible(window)) return;
                Log($"show window hidden: hwnd={window}");
                EndShow(SlideEndedReason.UserExit, session);
            }
        }

        // Clear stays unavailable until the Office probe confirms current-page scope.
        public virtual PresentationCapabilities Capabilities => new(IsPresenting && _comReady, IsPresenting && _comReady, NativeClearValidated && _comReady);
        public static bool NativeClearValidated { get; set; }
        public Task SetNativePointerAsync(NativePointerTool tool, byte[]? argb) => RunForShow(() =>
        {
            dynamic show = GetCurrentShow();
            dynamic view = show.View;
            if (tool == NativePointerTool.Eraser)
            {
                // 已选中的新墨迹橡皮不能重复执行切换命令，否则部分 Office 返回 E_FAIL。
                try { if ((bool)((dynamic)_ppt!).CommandBars.GetPressedMso("InkEraser")) return; }
                catch (COMException) { /* 旧版 Office 继续使用 PointerType。 */ }
            }
            // 仅换工具时激活放映；即时选色不能抢走颜色弹层的焦点。
            bool changingTool = (int)view.PointerType != (int)tool;
            if (changingTool) show.Activate();
            // PointerColor 属于笔工具。某些 Office 版本在写颜色时切回笔，不能用于橡皮。
            if (tool == NativePointerTool.Pen && argb is { Length: 4 })
                view.PointerColor.RGB = argb[1] | (argb[2] << 8) | (argb[3] << 16);
            if ((int)view.PointerType != (int)tool) view.PointerType = (int)tool;
            if (tool == NativePointerTool.Eraser && (int)view.PointerType != (int)tool)
            {
                // 新版 Office 的墨迹工具不一定回报旧版 PointerType=5。
                // 使用微软内置橡皮命令，并检查实际选中态，不能把 0/2 当作切换失败。
                dynamic commands = ((dynamic)_ppt!).CommandBars;
                if (!(bool)commands.GetEnabledMso("InkEraser"))
                    throw new InvalidOperationException("PowerPoint 当前无法使用橡皮，请先进入放映。");
                commands.ExecuteMso("InkEraser");
                if ((bool)commands.GetPressedMso("InkEraser")) return;
            }
            if ((int)view.PointerType != (int)tool) throw new InvalidOperationException("PowerPoint 未接受所选指针。请在 PowerPoint 中检查笔工具。" );
        });
        public Task ClearNativeInkAsync() => RunForShow(() =>
        {
            if (!NativeClearValidated) throw new NotSupportedException("原生清屏范围尚未通过当前 Office 验证，请使用 PowerPoint 的清除本页墨迹。" );
            GetCurrentShow().View.EraseDrawing();
        });
        public Task GoToSlideAsync(int slideIndex) => RunForShow(() =>
        {
            dynamic show = GetCurrentShow();
            if (slideIndex < 1 || slideIndex > (int)show.Presentation.Slides.Count) throw new ArgumentOutOfRangeException(nameof(slideIndex));
            // A named custom show can exclude slides from the full presentation.
            string name = (string)show.View.SlideShowName;
            if (!string.IsNullOrEmpty(name))
            {
                Array ids = (Array)show.Presentation.SlideShowSettings.NamedSlideShows.Item(name).SlideIDs;
                int targetId = (int)show.Presentation.Slides(slideIndex).SlideID;
                bool included = false; foreach (var id in ids) if (Convert.ToInt32(id) == targetId) included = true;
                if (!included) throw new InvalidOperationException("该页不在当前自定义放映范围内。" );
            }
            show.View.GotoSlide(slideIndex, -1);
        });
        public Task<string> ExportSlideThumbnailAsync(int slideIndex, string destinationPath, int pixelWidth, CancellationToken cancellationToken)
            => RunForShow(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic presentation = GetCurrentShow().Presentation;
                if (slideIndex < 1 || slideIndex > (int)presentation.Slides.Count) throw new ArgumentOutOfRangeException(nameof(slideIndex));
                int height = Math.Max(1, (int)(pixelWidth * (double)presentation.PageSetup.SlideHeight / (double)presentation.PageSetup.SlideWidth));
                presentation.Slides(slideIndex).Export(destinationPath, "PNG", pixelWidth, height);
                cancellationToken.ThrowIfCancellationRequested(); return destinationPath;
            });
        private Task RunForShow(Action action) => RunForShow(() => { action(); return true; });
        private Task<T> RunForShow<T>(Func<T> action)
        {
            long session = SessionVersion;
            return RunOnComThread(() =>
            {
                if (!_isPresenting || _ppt is null || SessionVersion != session)
                    throw new InvalidOperationException("放映已结束或切换，请重新打开工具。" );
                if (!_comReady) throw new InvalidOperationException("PowerPoint 正在连接，请稍后再试。");
                return action();
            });
        }
        private Task<T> RunOnComThread<T>(Func<T> action)
        {
            if (!_running || _threadId == 0) return Task.FromException<T>(new InvalidOperationException("PowerPoint 控制线程尚未就绪。"));
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Enqueue(() =>
            {
                if (completion.Task.IsCompleted) return;
                try
                {
                    if (!_running) throw new InvalidOperationException("PowerPoint 控制线程已关闭。" );
                    for (int attempt = 0; ; attempt++)
                    {
                        try { completion.TrySetResult(action()); break; }
                        catch (COMException ex) when (attempt < 2 && (ex.HResult == unchecked((int)0x80010001) || ex.HResult == unchecked((int)0x8001010A))) { Thread.Sleep(50); }
                    }
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            if (!PostThreadMessage(_threadId, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero)) completion.TrySetException(new InvalidOperationException("无法发送 PowerPoint 控制请求。"));
            try { _officeWake?.Set(); } catch (ObjectDisposedException) { }
            return AwaitCompletion();
            async Task<T> AwaitCompletion()
            {
                try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (TimeoutException ex) { completion.TrySetException(ex); throw; }
            }
        }

        public Task NextAsync() => RunForShow(() =>
        {
            if (_isPresenting && _ppt is not null)
            {
                GetCurrentShow().View.Next();
            }
        });

        public Task PreviousAsync() => RunForShow(() =>
        {
            if (_isPresenting && _ppt is not null)
            {
                GetCurrentShow().View.Previous();
            }
        });

        public Task ExitShowAsync() => RunForShow(() =>
        {
            if (_isPresenting && _ppt is not null)
            {
                GetCurrentShow().View.Exit();
            }
        });

        // ———— COM 线程 ————

        private void ThreadProc()
        {
            try
            {
                _threadId = GetCurrentThreadId();
                Log("com thread started");
                if (Array.IndexOf(_programIds, "PowerPoint.Application") >= 0)
                {
                    try
                    {
                        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
                        _officeWake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\RimePPT.Office." + sid);
                        _officeState = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen(@"Local\RimePPT.Office.State." + sid, 64);
                        Log("Office event bridge listening");
                    }
                    catch (Exception ex) { Log("Office event bridge unavailable: " + ex.Message); }
                }

                // PeekMessage + Sleep 泵：每轮先派发完所有消息（保持 STA 泵语义），
                // 再直接轮询一次。SetTimer 线程定时器实测偶发不派发 WM_TIMER，弃用。
                while (_running)
                {
                    while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                    {
                        if (msg.message == WM_QUIT)
                        {
                            Log("WM_QUIT received");
                            return;
                        }

                        if (msg.message == WM_APP_WORK)
                        {
                            while (_work.TryDequeue(out var action))
                            {
                                action();
                            }
                        }
                        else
                        {
                            TranslateMessage(ref msg);
                            DispatchMessage(ref msg);
                        }
                    }

                    ReadOfficeBridge();
                    Poll();
                    if (_officeWake is not null) _officeWake.WaitOne((int)PollIntervalMs);
                    else Thread.Sleep((int)PollIntervalMs);
                }

                Log("com thread exiting");
            }
            catch (Exception ex)
            {
                Log($"com thread FATAL: {ex.GetType().Name}: {ex}");
            }
            finally
            {
                _running = false; while (_work.TryDequeue(out var pending)) pending();
                var wake = _officeWake; _officeWake = null; wake?.Dispose();
                _officeState?.Dispose(); _officeState = null;
                _ppt = null;
            }
        }

        private static readonly object LogLock = new();

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "rimeppt_com.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n");
            }
            catch
            {
                // 日志失败不影响功能
            }
        }

        private void Poll()
        {
            long session; lock (_sessionGate) session = _sessionId;
            long pollStarted = Stopwatch.GetTimestamp();
            if (_ppt is null && !TryConnect())
            {
                return;
            }

            if (++_tick % 8 == 0)
            {
                Log($"poll tick, attached={_ppt is not null}");
            }

            try
            {
                dynamic ppt = _ppt!;
                int showCount = ppt.SlideShowWindows.Count;
                if (showCount == 0 && (_scanDocuments || _tick % RescanIntervalPolls == 0))
                {
                    _scanDocuments = false;
                    RefreshDocuments(_ppt!);
                    foreach (var candidate in GetRunningPowerPointCandidates())
                    {
                        if (!ReferenceEquals(candidate, _ppt)) RefreshDocuments(candidate);
                    }
                    lock (_sessionGate)
                        foreach (var key in new List<object>(_documents.Keys))
                            if (Stopwatch.GetElapsedTime(_documents[key].UpdatedAt) > TimeSpan.FromMinutes(1)) _documents.Remove(key);
                }

                if (showCount == 0)
                {
                    // 当前关注的实例没有放映：定期重扫其他实例，
                    // 用户可能同时开了多个 PowerPoint，放映发生在另一个里
                    if (++_rescanCounter >= RescanIntervalPolls)
                    {
                        _rescanCounter = 0;
                        object? presenting = FindPresentingCandidate();
                        if (presenting is not null && !ReferenceEquals(presenting, _ppt))
                        {
                            _ppt = presenting;
                            ppt = presenting;
                            showCount = ppt.SlideShowWindows.Count;
                        }
                    }
                }
                else
                {
                    _rescanCounter = 0;
                }

                if (showCount > 0)
                {
                    if (!TrySelectShow(_ppt!, out var selectedShow))
                    {
                        var candidate = FindPresentingCandidate();
                        if (candidate is not null) _ppt = candidate;
                        return;
                    }
                    dynamic show = selectedShow!;
                    dynamic view = show.View;
                    int slide = (int)view.Slide.SlideIndex;
                    int count = (int)show.Presentation.Slides.Count;

                    // HWND/路径在放映窗口处于后台时可能抛异常，均属非关键信息
                    IntPtr hwnd = TryGetShowHandle((object)show);
                    // Office may expose a host HWND while WinEvent observes its slideshow child.
                    // Keep the observed screenClass as the canonical session window in that case.
                    if (_windowWatcher is not null && HasVisibleShowWindow() && SameWindowProcess(_hwnd, hwnd)) hwnd = _hwnd;

                    string? path = null;
                    try
                    {
                        path = (string)show.Presentation.FullName;
                    }
                    catch (Exception)
                    {
                    }

                    bool started;
                    bool changed;
                    lock (_sessionGate)
                    {
                        if (session != _sessionId) return;
                        if (_isPresenting && _hwnd != IntPtr.Zero && hwnd != IntPtr.Zero && _hwnd != hwnd)
                        { _scanDocuments = true; return; }
                        started = !_wasPresenting;
                        changed = slide != _lastSlide || !_comReady || count != _slideCount;
                        if (started) ++_sessionId;
                        _showPath = path;
                        _currentSlide = slide;
                        _slideCount = count;
                        if (hwnd != IntPtr.Zero) _hwnd = hwnd;
                        _wasPresenting = _isPresenting = true;
                        _comReady = true;
                        _lastSlide = slide;
                    }
                    if (changed) PresentationDiagnostics.Record("COM 信息就绪", $"slide={slide}/{count}; query={Stopwatch.GetElapsedTime(pollStarted).TotalMilliseconds:F0} ms");
                    if (started) ShowStarted?.Invoke(this, EventArgs.Empty);
                    else if (changed) SlideChanged?.Invoke(this, slide);
                }
                else if (_wasPresenting && !HasVisibleShowWindow())
                {
                    Log("show ended (count=0)");
                    EndShow(SlideEndedReason.UserExit, session);
                }
            }
            catch (COMException ce)
            {
                bool busy = ce.HResult == unchecked((int)0x80010001) || ce.HResult == unchecked((int)0x8001010A);
                LogStageFailure("poll-com", ce);
                if (!busy) _ppt = null;
                if (!busy && _wasPresenting && !HasVisibleShowWindow()) EndShow(SlideEndedReason.PresentationClosed, session);
            }
            catch (Exception ex)
            {
                // dynamic 绑定类异常（Office 版本差异等）：视为一次瞬时失败，
                // 记录后保持连接状态，避免后台线程未处理异常拖垮整个进程
                LogStageFailure("poll", ex);
            }
        }

        private bool HasVisibleShowWindow() => _windowWatcher is not null && _hwnd != IntPtr.Zero && PowerPointSlideShowWindowWatcher.IsPowerPointSlideShow(_hwnd);
        private void LogStageFailure(string stage, Exception exception)
        {
            string key = stage + ":" + exception.GetType().Name + ":" + exception.HResult;
            long now = Stopwatch.GetTimestamp();
            if (_diagnosticTimes.TryGetValue(key, out long previous) && Stopwatch.GetElapsedTime(previous) < TimeSpan.FromSeconds(10)) return;
            _diagnosticTimes[key] = now;
            Log($"{stage}: {exception}");
            PresentationDiagnostics.Record(stage, $"{exception.GetType().Name}: 0x{exception.HResult:X8}; {exception.Message}");
        }

        private void RefreshDocuments(object application)
        {
            try
            {
                dynamic ppt = application;
                string active = "";
                try { active = (string)ppt.ActivePresentation.FullName; } catch (Exception) { }
                var documents = new List<DocumentSnapshot>();
                int count = (int)ppt.Presentations.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic presentation = ppt.Presentations(i);
                    string path = (string)presentation.FullName;
                    int slides = (int)presentation.Slides.Count;
                    int slide = 1;
                    try { if (path == active) slide = (int)ppt.ActiveWindow.View.Slide.SlideIndex; } catch (Exception) { }
                    string name = (string)presentation.Name;
                    documents.Add(new(FindDocumentProcessId(name), path, name, slides, slide, path == active));
                }
                lock (_sessionGate) _documents[application] = new(documents, Stopwatch.GetTimestamp());
            }
            catch (Exception ex) { LogStageFailure("document-preparation", ex); }
        }

        private static int FindDocumentProcessId(string name)
        {
            // Window matching is optional metadata. Never query Application.HWND: some Office builds do not expose it.
            if (string.IsNullOrWhiteSpace(name)) return 0;
            var matches = new HashSet<int>();
            try
            {
                string stem = Path.GetFileNameWithoutExtension(name);
                EnumWindows((window, _) =>
                {
                    if (!IsWindowVisible(window)) return true;
                    var kind = new System.Text.StringBuilder(64); GetClassName(window, kind, kind.Capacity);
                    if (!kind.ToString().EndsWith("FrameClass", StringComparison.Ordinal)) return true;
                    var title = new System.Text.StringBuilder(512); GetWindowText(window, title, title.Capacity);
                    if (!title.ToString().Contains(stem, StringComparison.OrdinalIgnoreCase)) return true;
                    GetWindowThreadProcessId(window, out uint pid);
                    try
                    {
                        using var process = Process.GetProcessById((int)pid);
                        if (process.ProcessName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase)) matches.Add((int)pid);
                    }
                    catch (ArgumentException) { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { return 0; }
            foreach (int pid in matches) if (matches.Count == 1) return pid;
            return 0;
        }
        private void EndShow(SlideEndedReason reason, long expectedSession)
        {
            lock (_sessionGate)
            {
                if (!_isPresenting || expectedSession != _sessionId) return;
                ++_sessionId;
                _wasPresenting = _isPresenting = _comReady = false;
                _lastSlide = -1;
                _currentSlide = _slideCount = 0;
                _hwnd = IntPtr.Zero;
                _fastDetectionTimestamp = 0;
            }
            PresentationDiagnostics.Record("放映结束", reason.ToString());
            ShowEnded?.Invoke(this, reason);
        }
        private void ReadOfficeBridge()
        {
            if (_officeState is null) return;
            try
            {
                using var view = _officeState.CreateViewAccessor(0, 64, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                long sequence = view.ReadInt64(0);
                if (sequence == 0 || (sequence & 1) != 0 || sequence == _officeSequence) return;
                int kind = view.ReadInt32(8), pid = view.ReadInt32(12);
                long time = view.ReadInt64(16);
                Thread.MemoryBarrier();
                if (sequence != view.ReadInt64(0)) return;
                _officeSequence = sequence;
                _scanDocuments = true;
                PresentationDiagnostics.Record("Office 加载项事件", $"kind={kind}; pid={pid}; UTC ticks={time}");
                // The payload is only a wake-up hint. Window identity remains authoritative,
                // so another PowerPoint instance cannot end the active session.
            }
            catch (Exception ex) { Log("Office payload unavailable: " + ex.Message); }
        }
        internal void Stop()
        {
            _windowWatcher?.Dispose(); _windowWatcher = null;
            _running = false;
            lock (_sessionGate) _documents.Clear();
            try { _officeWake?.Set(); } catch (ObjectDisposedException) { }
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        private bool TryConnect()
        {
            var candidates = GetRunningPowerPointCandidates(preferActiveObject: true);
            if (candidates.Count == 0)
            {
                _ppt = null;
                return false;
            }
            Log($"candidates: {candidates.Count}");

            // 多实例时优先选“正在放映”的，其次选“有文档”的
            object? best = null;
            int bestScore = -1;
            foreach (var candidate in candidates)
            {
                try
                {
                    dynamic d = candidate;
                    int score = (int)d.SlideShowWindows.Count > 0 ? 2
                        : (int)d.Presentations.Count > 0 ? 1
                        : 0;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                    }
                }
                catch (COMException)
                {
                }
            }

            _ppt = best;
            try
            {
                string stampPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimePPT", "native-clear-validation.json");
                NativeClearValidated = false;
                if (File.Exists(stampPath))
                {
                    string version = (string)((dynamic)_ppt!).Version + "|" + Convert.ToString(((dynamic)_ppt!).Build);
                    using var stamp = System.Text.Json.JsonDocument.Parse(File.ReadAllText(stampPath));
                    NativeClearValidated = stamp.RootElement.GetProperty("version").GetString() == version && stamp.RootElement.GetProperty("scope").GetString() == "current-page";
                }
            }
            catch { NativeClearValidated = false; }
            Log($"attached, bestScore={bestScore}");
            return _ppt is not null;
        }

        private object? FindPresentingCandidate()
        {
            foreach (var candidate in GetRunningPowerPointCandidates())
            {
                try
                {
                    if (TrySelectShow(candidate, out _)) return candidate;
                }
                catch (Exception ex) { LogStageFailure("select-show", ex); }
            }

            return null;
        }

        private bool TrySelectShow(object application, out object? selected)
        {
            selected = null;
            object? unidentified = null;
            dynamic windows = ((dynamic)application).SlideShowWindows;
            int count = (int)windows.Count;
            for (int index = 1; index <= count; index++)
            {
                object show = ((dynamic)application).SlideShowWindows(index);
                if (_hwnd == IntPtr.Zero) { selected = show; return true; }
                IntPtr handle = TryGetShowHandle(show);
                if (handle == _hwnd) { selected = show; return true; }
                if (count == 1 && SameWindowProcess(handle, _hwnd)) { selected = show; return true; }
                if (handle == IntPtr.Zero && count == 1) unidentified = show;
            }
            // Missing optional window metadata must not disable a usable COM slideshow.
            selected = unidentified;
            return selected is not null;
        }

        private dynamic GetCurrentShow()
        {
            if (_ppt is not null && TrySelectShow(_ppt, out var show)) return show!;
            throw new InvalidOperationException("当前放映已结束或尚未连接，请稍后重试。");
        }

        private static IntPtr TryGetShowHandle(object show)
        {
            try
            {
                var handle = new IntPtr(Convert.ToInt64(((dynamic)show).HWND));
                if (GetWindowThreadProcessId(handle, out uint pid) != 0 && pid != 0) return handle;
            }
            catch (Exception) { }
            try
            {
                string name = (string)((dynamic)show).Presentation.Name;
                string stem = Path.GetFileNameWithoutExtension(name);
                if (string.IsNullOrEmpty(stem)) return IntPtr.Zero;
                var matches = new List<IntPtr>();
                EnumWindows((window, _) =>
                {
                    var kind = new System.Text.StringBuilder(64); GetClassName(window, kind, kind.Capacity);
                    var title = new System.Text.StringBuilder(512); GetWindowText(window, title, title.Capacity);
                    if (kind.ToString() == "screenClass" && IsWindowVisible(window)
                        && title.ToString().Contains(stem, StringComparison.OrdinalIgnoreCase)) matches.Add(window);
                    return true;
                }, IntPtr.Zero);
                return matches.Count == 1 ? matches[0] : IntPtr.Zero;
            }
            catch (Exception) { return IntPtr.Zero; }
        }

        private static bool SameWindowProcess(IntPtr left, IntPtr right)
        {
            if (left == IntPtr.Zero || right == IntPtr.Zero) return false;
            GetWindowThreadProcessId(left, out uint leftPid);
            GetWindowThreadProcessId(right, out uint rightPid);
            return leftPid != 0 && leftPid == rightPid;
        }

        /// <summary>
        /// 枚举 ROT 收集所有 PowerPoint.Application 运行实例。
        /// 单个 GetActiveObject 只会返回第一个注册的实例——用户先开了一个
        /// PowerPoint、后又开另一个放映时，盯错实例会导致工具条永远不出现。
        /// </summary>
        private List<object> GetRunningPowerPointCandidates(bool preferActiveObject = false)
        {
            var result = new List<object>();
            try
            {
                var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string programId in _programIds)
                    try
                    {
                        CLSIDFromProgID(programId, out Guid clsid); targetNames.Add("!" + clsid.ToString("B"));
                        // Initial attachment uses the active object directly; full ROT scanning
                        // remains available when checking other PowerPoint instances.
                        try { GetActiveObject(ref clsid, IntPtr.Zero, out object active); result.Add(active); }
                        catch (COMException) { }
                    }
                    catch (COMException) { }
                if (targetNames.Count == 0) return result;
                if (preferActiveObject && result.Count > 0) return result;

                GetRunningObjectTable(0, out IRunningObjectTable rot);
                rot.EnumRunning(out IEnumMoniker enumMoniker);
                CreateBindCtx(0, out IBindCtx bindCtx);

                var monikers = new IMoniker[1];
                while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    try
                    {
                        monikers[0].GetDisplayName(bindCtx, null, out string name);
                        if (targetNames.Contains(name)
                            && rot.GetObject(monikers[0], out object obj) == 0)
                        {
                            result.Add(obj);
                        }
                        else if (Array.IndexOf(_programIds, "PowerPoint.Application") >= 0
                            && (name.EndsWith(".ppt", StringComparison.OrdinalIgnoreCase)
                                || name.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)
                                || name.EndsWith(".pptm", StringComparison.OrdinalIgnoreCase)
                                || name.EndsWith(".ppsx", StringComparison.OrdinalIgnoreCase))
                            && rot.GetObject(monikers[0], out object document) == 0)
                        {
                            object application = ((dynamic)document).Application;
                            string applicationName = (string)((dynamic)application).Name;
                            if (applicationName.Contains("PowerPoint", StringComparison.OrdinalIgnoreCase)) result.Add(application);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (COMException)
            {
            }

            var unique = new List<object>();
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var application in result) if (seen.Add(application)) unique.Add(application);
            return unique;
        }

        private Task RunOnComThread(Action action) => RunOnComThread(() => { action(); return true; });

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void CLSIDFromProgID(
            [MarshalAs(UnmanagedType.LPWStr)] string progId,
            out Guid clsid);

        [DllImport("oleaut32.dll", PreserveSig = false)]
        private static extern void GetActiveObject(ref Guid clsid, IntPtr reserved,
            [MarshalAs(UnmanagedType.IUnknown)] out object active);

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void GetRunningObjectTable(uint reserved, out IRunningObjectTable prot);

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void CreateBindCtx(uint reserved, out IBindCtx bindCtx);

        private delegate bool WindowCallback(IntPtr window, IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);

        // ———— Win32 消息管道 ————

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
