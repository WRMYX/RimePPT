using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;

namespace RimePPT.Core
{
    /// <summary>
    /// PowerPoint COM 控制器：专用 STA 线程跑消息泵，全部 COM 访问收敛在该线程。
    /// 晚绑定（dynamic）无法订阅 COM 事件，故用 250ms 轮询
    /// SlideShowWindows.Count / 页码派生 ShowStarted/SlideChanged/ShowEnded。
    /// UI 线程调用 Next/Previous/Exit 时经线程消息 marshal 过来。
    /// </summary>
    public sealed class PowerPointController : IPresentationController
    {
        private const uint WM_APP_WORK = 0x8001;  // WM_APP + 1：执行队列
        private const uint WM_QUIT = 0x0012;
        private const uint PM_REMOVE = 0x0001;
        private const uint PollIntervalMs = 250;
        private const int RescanIntervalPolls = 8; // ≈2s 重扫一次其他实例

        private Thread? _thread;
        private uint _threadId;
        private volatile bool _running;
        private readonly ConcurrentQueue<Action> _work = new();

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
        }

        // Clear stays unavailable until the Office probe confirms current-page scope.
        public PresentationCapabilities Capabilities => new(IsPresenting, IsPresenting, NativeClearValidated);
        public static bool NativeClearValidated { get; set; }
        public Task SetNativePointerAsync(NativePointerTool tool, byte[]? argb) => RunForShow(() =>
        {
            dynamic show = ((dynamic)_ppt!).SlideShowWindows(1);
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
            ((dynamic)_ppt!).SlideShowWindows(1).View.EraseDrawing();
        });
        public Task GoToSlideAsync(int slideIndex) => RunForShow(() =>
        {
            dynamic show = ((dynamic)_ppt!).SlideShowWindows(1);
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
                dynamic presentation = ((dynamic)_ppt!).SlideShowWindows(1).Presentation;
                if (slideIndex < 1 || slideIndex > (int)presentation.Slides.Count) throw new ArgumentOutOfRangeException(nameof(slideIndex));
                int height = Math.Max(1, (int)(pixelWidth * (double)presentation.PageSetup.SlideHeight / (double)presentation.PageSetup.SlideWidth));
                presentation.Slides(slideIndex).Export(destinationPath, "PNG", pixelWidth, height);
                cancellationToken.ThrowIfCancellationRequested(); return destinationPath;
            });
        private Task RunForShow(Action action) => RunForShow(() => { action(); return true; });
        private Task<T> RunForShow<T>(Func<T> action)
        {
            var handle = _hwnd; var path = _showPath;
            return RunOnComThread(() =>
            {
                if (!_isPresenting || _ppt is null || _hwnd != handle || _showPath != path)
                    throw new InvalidOperationException("放映已结束或切换，请重新打开工具。" );
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
            return AwaitCompletion();
            async Task<T> AwaitCompletion()
            {
                try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (TimeoutException ex) { completion.TrySetException(ex); throw; }
            }
        }

        public Task NextAsync() => RunOnComThread(() =>
        {
            if (_isPresenting && _ppt is not null)
            {
                ((dynamic)_ppt).SlideShowWindows(1).View.Next();
            }
        });

        public Task PreviousAsync() => RunOnComThread(() =>
        {
            if (_isPresenting && _ppt is not null)
            {
                ((dynamic)_ppt).SlideShowWindows(1).View.Previous();
            }
        });

        public Task ExitShowAsync() => RunOnComThread(() =>
        {
            if (_isPresenting && _ppt is not null)
            {
                ((dynamic)_ppt).SlideShowWindows(1).View.Exit();
            }
        });

        // ———— COM 线程 ————

        private void ThreadProc()
        {
            try
            {
                _threadId = GetCurrentThreadId();
                Log("com thread started");

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

                    Poll();
                    Thread.Sleep((int)PollIntervalMs);
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

                if (showCount == 0 && !_wasPresenting)
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
                    dynamic show = ppt.SlideShowWindows(1);
                    dynamic view = show.View;
                    int slide = (int)view.Slide.SlideIndex;
                    int count = (int)show.Presentation.Slides.Count;

                    // HWND/路径在放映窗口处于后台时可能抛异常，均属非关键信息
                    IntPtr hwnd = IntPtr.Zero;
                    try
                    {
                        hwnd = (IntPtr)(int)show.HWND;
                    }
                    catch (Exception)
                    {
                    }

                    string? path = null;
                    try
                    {
                        path = (string)show.Presentation.FullName;
                    }
                    catch (Exception)
                    {
                    }

                    if (hwnd == IntPtr.Zero)
                    {
                        // Some Office builds do not expose SlideShowWindow.HWND over IDispatch.
                        var matches = new List<IntPtr>();
                        EnumWindows((window, state) =>
                        {
                            var kind = new System.Text.StringBuilder(128); GetClassName(window, kind, kind.Capacity);
                            var title = new System.Text.StringBuilder(512); GetWindowText(window, title, title.Capacity);
                            if (kind.ToString() == "screenClass" && IsWindowVisible(window) && path is not null && title.ToString().Contains(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)) matches.Add(window);
                            return true;
                        }, IntPtr.Zero);
                        if (matches.Count == 1) hwnd = matches[0];
                    }
                    _showPath = path;
                    _currentSlide = slide;
                    _slideCount = count;
                    _hwnd = hwnd;

                    if (!_wasPresenting)
                    {
                        _wasPresenting = true;
                        _isPresenting = true;
                        _lastSlide = slide;
                        Log($"show started: slide={slide} count={count} hwnd={hwnd}");
                        ShowStarted?.Invoke(this, EventArgs.Empty);
                    }
                    else if (slide != _lastSlide)
                    {
                        _lastSlide = slide;
                        Log($"slide changed: {slide}");
                        SlideChanged?.Invoke(this, slide);
                    }
                }
                else if (_wasPresenting)
                {
                    Log("show ended (count=0)");
                    EndShow(SlideEndedReason.UserExit);
                }
            }
            catch (COMException ce)
            {
                // PowerPoint 被关闭 / COM 断连：按放映结束处理并回到重连循环
                Log($"COMException in poll: {ce.Message}");
                if (_wasPresenting)
                {
                    EndShow(SlideEndedReason.PresentationClosed);
                }
                _ppt = null;
            }
            catch (Exception ex)
            {
                // dynamic 绑定类异常（Office 版本差异等）：视为一次瞬时失败，
                // 记录后保持连接状态，避免后台线程未处理异常拖垮整个进程
                Log($"poll transient: {ex}");
            }
        }

        private void EndShow(SlideEndedReason reason)
        {
            _wasPresenting = false;
            _isPresenting = false;
            _lastSlide = -1;
            _currentSlide = 0;
            _hwnd = IntPtr.Zero;
            ShowEnded?.Invoke(this, reason);
        }

        private bool TryConnect()
        {
            var candidates = GetRunningPowerPointCandidates();
            if (candidates.Count == 0)
            {
                _ppt = null;
                return false;
            }
            Log($"candidates: {candidates.Count}");
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
                string version = (string)((dynamic)_ppt!).Version + "|" + Convert.ToString(((dynamic)_ppt!).Build);
                using var stamp = System.Text.Json.JsonDocument.Parse(File.ReadAllText(stampPath));
                NativeClearValidated = stamp.RootElement.GetProperty("version").GetString() == version && stamp.RootElement.GetProperty("scope").GetString() == "current-page";
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
                    if (((dynamic)candidate).SlideShowWindows.Count > 0)
                    {
                        return candidate;
                    }
                }
                catch (COMException)
                {
                }
            }

            return null;
        }

        /// <summary>
        /// 枚举 ROT 收集所有 PowerPoint.Application 运行实例。
        /// 单个 GetActiveObject 只会返回第一个注册的实例——用户先开了一个
        /// PowerPoint、后又开另一个放映时，盯错实例会导致工具条永远不出现。
        /// </summary>
        private static List<object> GetRunningPowerPointCandidates()
        {
            var result = new List<object>();
            try
            {
                CLSIDFromProgID("PowerPoint.Application", out Guid clsid);
                string targetName = "!" + clsid.ToString("B");

                GetRunningObjectTable(0, out IRunningObjectTable rot);
                rot.EnumRunning(out IEnumMoniker enumMoniker);
                CreateBindCtx(0, out IBindCtx bindCtx);

                var monikers = new IMoniker[1];
                while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    try
                    {
                        monikers[0].GetDisplayName(bindCtx, null, out string name);
                        if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase)
                            && rot.GetObject(monikers[0], out object obj) == 0)
                        {
                            result.Add(obj);
                        }
                    }
                    catch (COMException)
                    {
                    }
                }
            }
            catch (COMException)
            {
            }

            return result;
        }

        private Task RunOnComThread(Action action) => RunOnComThread(() => { action(); return true; });

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void CLSIDFromProgID(
            [MarshalAs(UnmanagedType.LPWStr)] string progId,
            out Guid clsid);

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void GetRunningObjectTable(uint reserved, out IRunningObjectTable prot);

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void CreateBindCtx(uint reserved, out IBindCtx bindCtx);

        private delegate bool WindowCallback(IntPtr window, IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);
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
