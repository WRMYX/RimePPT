using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

        // ── 侧栏子窗口 ────────────────────────────────────────────────
        private SidebarWindow? _leftSidebar;
        private SidebarWindow? _rightSidebar;

        // ── 计时器 ────────────────────────────────────────────────────
        private DispatcherTimer? _timer;
        private TimeSpan _elapsed;
        public event Action<bool>? TimerStateChanged;
        public bool IsTimerRunning => _timer != null;

        // ── 状态 ──────────────────────────────────────────────────────
        private IntPtr    _pptHwnd    = IntPtr.Zero;
        private RectInt32 _pptMonitor;
#pragma warning disable CS0414
        private bool _isBlackout = false;   // reserved for future guard checks
#pragma warning restore CS0414

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
            // 侧栏在 ShowOverlay 时延迟创建（保证 UI 线程已就绪）
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
        private void ShowPill()
        {
            BlackoutOverlay.Visibility = Visibility.Collapsed;
            ToolbarPill.Visibility     = Visibility.Visible;
            RootGrid.Background = (Brush)Application.Current.Resources["SmokeFillColorDefaultBrush"];

            int x = _pptMonitor.X + (_pptMonitor.Width - PILL_WIDTH) / 2;
            int y = _pptMonitor.Y +  _pptMonitor.Height - PILL_HEIGHT - PILL_MARGIN_BOT;

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd))
                     .MoveAndResize(new RectInt32(x, y, PILL_WIDTH, PILL_HEIGHT));

            double scale = WindowHelper.GetDpiScale(hwnd);
            WindowHelper.SetRoundedRegion(hwnd,
                (int)(PILL_WIDTH  * scale),
                (int)(PILL_HEIGHT * scale),
                (int)(PILL_RADIUS * 2 * scale));
        }

        private void EnterBlackoutMode()
        {
            _isBlackout = true;
            // 先隐藏侧栏，再把自己铺满屏幕
            _leftSidebar?.HideSidebar();
            _rightSidebar?.HideSidebar();

            ToolbarPill.Visibility     = Visibility.Collapsed;
            BlackoutOverlay.Visibility = Visibility.Visible;
            RootGrid.Background = new SolidColorBrush(Colors.Black);

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd))
                     .MoveAndResize(_pptMonitor);
            WindowHelper.ClearRegion(hwnd);
        }

        private void ExitBlackoutMode()
        {
            _isBlackout = false;
            ShowPill();
            _leftSidebar?.ShowSidebar(_pptMonitor);
            _rightSidebar?.ShowSidebar(_pptMonitor);
        }

        // ── 公共控制器方法（供 SidebarWindow 回调）────────────────────
        public void OnPrev()      { _pptControl.PreviousSlide(); RefreshPageInfo(); }
        public void OnNext()      { _pptControl.NextSlide();     RefreshPageInfo(); }
        public void OnExit()      { _pptControl.EndShow();       HideOverlay(); }
        public void OnBlackout()  => EnterBlackoutMode();
        public void OnSettings()  => (Application.Current as App)?.ShowSettings();
        public void OnTimer()     => ToggleTimer();

        // ── 翻页（底部胶囊按钮） ──────────────────────────────────────
        private void BtnPrev_Click(object sender, RoutedEventArgs e) => OnPrev();
        private void BtnNext_Click(object sender, RoutedEventArgs e) => OnNext();

        private void RefreshPageInfo()
        {
            int cur = _pptControl.CurrentSlide();
            int tot = _pptControl.TotalSlides();
            TbPageInfo.Text = (cur > 0 && tot > 0) ? $"{cur} / {tot}" : "— / —";
        }

        // ── 退出 ──────────────────────────────────────────────────────
        private void BtnExit_Click(object sender, RoutedEventArgs e) => OnExit();

        // ── 计时器 ────────────────────────────────────────────────────
        private void BtnTimer_Click(object sender, RoutedEventArgs e) => ToggleTimer();

        private void ToggleTimer()
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
            TimerStateChanged?.Invoke(true);
        }

        private void StopTimer()
        {
            _timer?.Stop();
            _timer = null;
            TimerChip.Visibility = Visibility.Collapsed;
            MiTimer.Text = "开始计时";
            TimerStateChanged?.Invoke(false);
        }

        // ── 黑屏 ──────────────────────────────────────────────────────
        private void BtnBlackout_Click(object sender, RoutedEventArgs e) => EnterBlackoutMode();
        private void BlackoutOverlay_Tapped(object sender, TappedRoutedEventArgs e) => ExitBlackoutMode();

        // ── 设置 ──────────────────────────────────────────────────────
        private void BtnSettings_Click(object sender, RoutedEventArgs e) => OnSettings();

        // ── Show / Hide ───────────────────────────────────────────────
        public void ShowOverlay(IntPtr pptHwnd)
        {
            _pptHwnd    = pptHwnd;
            _pptMonitor = GetMonitorBounds(pptHwnd);
            _pptControl.TryConnect();
            _isBlackout = false;

            // 延迟创建侧栏（首次）
            _leftSidebar  ??= new SidebarWindow(this, isLeft: true);
            _rightSidebar ??= new SidebarWindow(this, isLeft: false);

            // 底部胶囊
            ShowPill();
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd)).Show();
            WindowHelper.ShowNoActivate(hwnd);

            // 左右侧栏
            _leftSidebar.ShowSidebar(_pptMonitor);
            _rightSidebar.ShowSidebar(_pptMonitor);

            // 还原焦点给 PPT
            if (pptHwnd != IntPtr.Zero) SetForegroundWindow(pptHwnd);
            RefreshPageInfo();
        }

        public void HideOverlay()
        {
            StopTimer();
            _leftSidebar?.HideSidebar();
            _rightSidebar?.HideSidebar();

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd)).Hide();
            _pptControl.Disconnect();
        }
    }
}
