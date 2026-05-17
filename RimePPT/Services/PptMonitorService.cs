using System;
using System.Runtime.InteropServices;
using System.Timers;

namespace RimePPT.Services
{
    /// <summary>
    /// Monitors for a running PowerPoint slideshow by polling window classes.
    /// Fires SlideshowStarted / SlideshowEnded events on state transitions.
    /// </summary>
    public sealed class PptMonitorService : IDisposable
    {
        // PowerPoint full-screen slideshow window class (all modern Office versions)
        private const string ScreenClass = "screenClass";

        private readonly Timer _timer;
        private bool _wasShowing;
        private IntPtr _lastHwnd = IntPtr.Zero;
        private bool _disposed;

        public event Action<IntPtr>? SlideshowStarted;
        public event Action?         SlideshowEnded;

        public PptMonitorService(double intervalMs = 1000)
        {
            _timer = new Timer(intervalMs) { AutoReset = true };
            _timer.Elapsed += OnTick;
        }

        public void Start() => _timer.Start();
        public void Stop()  => _timer.Stop();

        // ── Win32 ─────────────────────────────────────────────────────
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

        // ── Polling ───────────────────────────────────────────────────
        private void OnTick(object? sender, ElapsedEventArgs e)
        {
            IntPtr hwnd = FindSlideshowWindow();
            bool isShowing = hwnd != IntPtr.Zero;

            if (isShowing && !_wasShowing)
            {
                _lastHwnd = hwnd;
                SlideshowStarted?.Invoke(hwnd);
            }
            else if (!isShowing && _wasShowing)
            {
                _lastHwnd = IntPtr.Zero;
                SlideshowEnded?.Invoke();
            }

            _wasShowing = isShowing;
        }

        private static IntPtr FindSlideshowWindow()
        {
            // Primary: PowerPoint full-screen slideshow
            IntPtr hwnd = FindWindow(ScreenClass, null);
            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
                return hwnd;

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
        }
    }
}
