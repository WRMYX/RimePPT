using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using RimePPT.Helpers;
using System;
using Windows.Graphics;
using WinRT.Interop;

namespace RimePPT
{
    public sealed partial class SidebarWindow : Window
    {
        private const int SIDEBAR_W  = 56;
        private const int SIDEBAR_H  = 228;   // 固定高度，适配 4 个按钮 + 分隔线
        private const int MARGIN     = 16;

        private readonly OverlayWindow _ctrl;
        private readonly bool          _isLeft;

        public SidebarWindow(OverlayWindow controller, bool isLeft)
        {
            _ctrl   = controller;
            _isLeft = isLeft;
            InitializeComponent();

            // 右侧工具栏：菜单向左弹出
            if (!isLeft) ToolsFlyout.Placement = FlyoutPlacementMode.Left;

            // 无边框，不进任务栏，始终置顶
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd))
                     .SetPresenter(OverlappedPresenter.CreateForContextMenu());
            WindowHelper.MakeToolWindow(hwnd);
            WindowHelper.SetTopmost(hwnd);

            // 计时器状态同步（更新菜单项文字）
            _ctrl.TimerStateChanged += isRunning =>
                MiTimer.Text = isRunning ? "停止计时" : "开始计时";
        }

        // ── 显示 / 定位 ─────────────────────────────────────────────
        public void ShowSidebar(RectInt32 monitor)
        {
            int x = _isLeft
                ? monitor.X + MARGIN
                : monitor.X + monitor.Width - SIDEBAR_W - MARGIN;
            int y = monitor.Y + (monitor.Height - SIDEBAR_H) / 2;

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            appWindow.MoveAndResize(new RectInt32(x, y, SIDEBAR_W, SIDEBAR_H));
            appWindow.Show();
            WindowHelper.ShowNoActivate(hwnd);
        }

        public void HideSidebar()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd)).Hide();
        }

        // ── 按钮 ─────────────────────────────────────────────────────
        private void BtnPrev_Click(object s, RoutedEventArgs e)     => _ctrl.OnPrev();
        private void BtnNext_Click(object s, RoutedEventArgs e)     => _ctrl.OnNext();
        private void BtnTimer_Click(object s, RoutedEventArgs e)    => _ctrl.OnTimer();
        private void BtnBlackout_Click(object s, RoutedEventArgs e) => _ctrl.OnBlackout();
        private void BtnSettings_Click(object s, RoutedEventArgs e) => _ctrl.OnSettings();
        private void BtnExit_Click(object s, RoutedEventArgs e)     => _ctrl.OnExit();
    }
}
