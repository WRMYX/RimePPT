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

        private Task RunOnComThread(Action action)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Enqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            PostThreadMessage(_threadId, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero);
            return tcs.Task;
        }

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void CLSIDFromProgID(
            [MarshalAs(UnmanagedType.LPWStr)] string progId,
            out Guid clsid);

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void GetRunningObjectTable(uint reserved, out IRunningObjectTable prot);

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void CreateBindCtx(uint reserved, out IBindCtx bindCtx);

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
