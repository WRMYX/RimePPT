using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RimePPT.Helpers;
using RimePPT.Services;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace RimePPT
{
    /// <summary>
    /// 小尺寸 Pill 工具栏窗口，停靠在 PPT 监视器底部居中。
    /// 不使用任何透明化技巧 —— 整个窗口本身就是工具栏可视区域。
    /// 黑屏模式：临时把窗口拉到全屏 + 切换 RootGrid 为黑底。
    /// </summary>
    public sealed partial class OverlayWindow : Window
    {
        // ── 常量 ──────────────────────────────────────────────────────
        private const int PILL_WIDTH      = 460;
        private const int PILL_HEIGHT     = 60;
        private const int PILL_MARGIN_BOT = 32;
        private const int PILL_RADIUS     = 30;

        // ── Services ──────────────────────────────────────────────────
        private readonly PptControlService _pptControl;

        // ── 计时器 ────────────────────────────────────────────────────
        private DispatcherTimer? _timer;
        private TimeSpan _elapsed;

        // ── 状态 ──────────────────────────────────────────────────────
        private IntPtr _pptHwnd = IntPtr.Zero;
        private bool   _isBlackout = false;
        private RectInt32 _pptMonitor;

        // ── Win32 ─────────────────────────────────────────────────────
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool   GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] private static extern bool   SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        // ── 构造器 ────────────────────────────────────────────────────
        public OverlayWindow(PptControlService pptControl)
        {
            _pptControl = pptControl;
            InitializeComponent();
            InitializeOverlayWindow();
        }

        private void InitializeOverlayWindow()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId wid = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(wid);

            // 无边框、无标题栏（context-menu 风格）
            appWindow.SetPresenter(OverlappedPresenter.CreateForContextMenu());

            // 不进任务栏 + 不抢焦点
            WindowHelper.MakeToolWindow(hwnd);
            WindowHelper.SetTopmost(hwnd);
        }

        // ── 监视器测量 ────────────────────────────────────────────────
        private RectInt32 GetMonitorBounds(IntPtr pptHwnd)
        {
            const uint MONITOR_DEFAULTTONEAREST = 2;
            IntPtr hMonitor = MonitorFromWindow(pptHwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref mi))
                return new RectInt32(0, 0, 1920, 1080);

            return new RectInt32(
                mi.rcMonitor.Left, mi.rcMonitor.Top,
                mi.rcMonitor.Right  - mi.rcMonitor.Left,
                mi.rcMonitor.Bottom - mi.rcMonitor.Top);
        }

        // ── Pill / Blackout 模式切换 ──────────────────────────────────
        private void EnterPillMode()
        {
            _isBlackout = false;
            BlackoutOverlay.Visibility = Visibility.Collapsed;
            ToolbarPill.Visibility    = Visibility.Visible;
            RootGrid.Background = (Microsoft.UI.Xaml.Media.Brush)
                Application.Current.Resources["SmokeFillColorDefaultBrush"];

            int x = _pptMonitor.X + (_pptMonitor.Width - PILL_WIDTH) / 2;
            int y = _pptMonitor.Y +  _pptMonitor.Height - PILL_HEIGHT - PILL_MARGIN_BOT;

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId wid = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow.GetFromWindowId(wid).MoveAndResize(new RectInt32(x, y, PILL_WIDTH, PILL_HEIGHT));

            // 物理像素圆角（DPI 缩放）
            double scale = WindowHelper.GetDpiScale(hwnd);
            int physW = (int)(PILL_WIDTH  * scale);
            int physH = (int)(PILL_HEIGHT * scale);
            int physR = (int)(PILL_RADIUS * 2 * scale);
            WindowHelper.SetRoundedRegion(hwnd, physW, physH, physR);
        }

        private void EnterBlackoutMode()
        {
            _isBlackout = true;
            ToolbarPill.Visibility    = Visibility.Collapsed;
            BlackoutOverlay.Visibility = Visibility.Visible;
            RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black);

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId wid = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow.GetFromWindowId(wid).MoveAndResize(_pptMonitor);
            WindowHelper.ClearRegion(hwnd);
        }

        // ── 翻页 ──────────────────────────────────────────────────────
        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            _pptControl.PreviousSlide();
            RefreshPageInfo();
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            _pptControl.NextSlide();
            RefreshPageInfo();
        }

        private void RefreshPageInfo()
        {
            int cur = _pptControl.CurrentSlide();
            int tot = _pptControl.TotalSlides();
            TbPageInfo.Text = (cur > 0 && tot > 0) ? $"{cur} / {tot}" : "— / —";
        }

        // ── 退出 ──────────────────────────────────────────────────────
        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            _pptControl.EndShow();
            HideOverlay();
        }

        // ── 计时器 ────────────────────────────────────────────────────
        private void BtnTimer_Click(object sender, RoutedEventArgs e)
        {
            if (_timer != null) { StopTimer(); return; }

            _elapsed = TimeSpan.Zero;
            TbTimer.Text = "00:00";
            TimerChip.Visibility = Visibility.Visible;
            MiTimer.Text = "停止计时";
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                _elapsed += TimeSpan.FromSeconds(1);
                TbTimer.Text = _elapsed.TotalHours >= 1
                    ? _elapsed.ToString(@"hh\:mm\:ss")
                    : _elapsed.ToString(@"mm\:ss");
            };
            _timer.Start();
        }

        private void StopTimer()
        {
            _timer?.Stop();
            _timer = null;
            TimerChip.Visibility = Visibility.Collapsed;
            MiTimer.Text = "开始计时";
        }

        // ── 黑屏 ──────────────────────────────────────────────────────
        private void BtnBlackout_Click(object sender, RoutedEventArgs e) => EnterBlackoutMode();

        private void BlackoutOverlay_Tapped(object sender, TappedRoutedEventArgs e) => EnterPillMode();

        // ── 设置 ──────────────────────────────────────────────────────
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
            => (Application.Current as App)?.ShowSettings();

        // ── Show / Hide ───────────────────────────────────────────────
        public void ShowOverlay(IntPtr pptHwnd)
        {
            _pptHwnd = pptHwnd;
            _pptMonitor = GetMonitorBounds(pptHwnd);
            _pptControl.TryConnect();

            EnterPillMode();

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId wid = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow.GetFromWindowId(wid).Show();

            // 不抢焦点
            WindowHelper.ShowNoActivate(hwnd);
            if (pptHwnd != IntPtr.Zero) SetForegroundWindow(pptHwnd);

            RefreshPageInfo();
        }

        public void HideOverlay()
        {
            StopTimer();
            if (_isBlackout) EnterPillMode();   // reset to pill state
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId wid = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow.GetFromWindowId(wid).Hide();
            _pptControl.Disconnect();
        }
    }
}
