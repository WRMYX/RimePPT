using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace RimePPT.Services
{
    /// <summary>
    /// Win32 窗口管道辅助（仅窗口行为，不做任何外观绘制）：
    /// 为浮窗加上 WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW，使其置顶显示但不抢走放映焦点。
    /// </summary>
    public static class WindowPlumbing
    {
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_NOACTIVATE = 0x08000000;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_CAPTION = 0x00C00000;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private static readonly IntPtr HwndTopmost = new(-1);
        private const long WS_EX_TRANSPARENT = 0x00000020;
        private const long WS_EX_LAYERED = 0x00080000;

        /// <summary>把窗口重新提到所有置顶窗口之上（批注层启用后恢复工具条可点）。</summary>
        public static void RaiseToTopmost(Window window)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }

        /// <summary>
        /// 鼠标穿透开关：开启后该窗口对鼠标完全透明（点击落到下面的放映画面），
        /// 用于批注层未启用时不挡放映操作。依赖 WS_EX_LAYERED 使 WS_EX_TRANSPARENT 生效。
        /// </summary>
        public static void SetClickThrough(Window window, bool passthrough)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            int style = GetWindowLongW(hwnd, GWL_EXSTYLE);
            if (passthrough)
            {
                style = (int)((uint)style | WS_EX_TRANSPARENT | WS_EX_LAYERED);
            }
            else
            {
                style = (int)((uint)style & ~(WS_EX_TRANSPARENT | WS_EX_LAYERED));
            }
            Marshal.SetLastPInvokeError(0);
            int previous = SetWindowLongW(hwnd, GWL_EXSTYLE, style);
            int error = Marshal.GetLastPInvokeError();
            if (previous == 0 && error != 0)
                throw new Win32Exception(error, "Unable to change annotation input passthrough.");
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern bool SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_NCLBUTTONDOWN = 0x00A1;
        private static readonly IntPtr HtCaption = new(2);

        /// <summary>
        /// 开始拖动无边框窗口（在自定义标题行 PointerPressed 中调用）：
        /// 释放鼠标捕获并向系统发送 HTCAPTION 非客户区按下消息。
        /// </summary>
        public static void StartMoveDrag(Window window)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            ReleaseCapture();
            SendMessageW(hwnd, WM_NCLBUTTONDOWN, HtCaption, IntPtr.Zero);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        /// <summary>根据放映窗口 HWND 找到它所在的 DisplayArea（按左上角匹配）；找不到返回 null。</summary>
        public static Microsoft.UI.Windowing.DisplayArea? GetDisplayAreaFromHwnd(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            IntPtr monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfoW(monitor, ref info))
            {
                return null;
            }

            foreach (var area in Microsoft.UI.Windowing.DisplayArea.FindAll())
            {
                var rect = area.OuterBounds;
                if (rect.X == info.rcMonitor.Left && rect.Y == info.rcMonitor.Top)
                {
                    return area;
                }
            }

            return null;
        }

        [DllImport("dwmapi.dll")]
        private static extern void DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        [DllImport("dwmapi.dll")]
        private static extern void DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND pBlurBehind);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
        private const int DWMWCP_ROUND = 2;

        /// <summary>显式启用 Windows 11 圆角（无边框窗口在部分状态下会被方角渲染）。</summary>
        public static void SetRoundedCorners(Window window)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR - 1, ref preference, sizeof(int));
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int Left;
            public int Right;
            public int Top;
            public int Bottom;
        }

        private const int DWM_BB_ENABLE = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_BLURBEHIND
        {
            public int dwFlags;
            public int fEnable;
            public IntPtr hRgnBlur;
            public int fTransitionOnMaximized;
        }

        /// <summary>让窗口永不成为前台窗口（鼠标点击仍可接收，但不会把放映窗口切到后台）。</summary>
        public static void ApplyNoActivate(Window window)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            int style = GetWindowLongW(hwnd, GWL_EXSTYLE);
            SetWindowLongW(hwnd, GWL_EXSTYLE, (int)((uint)style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        }

        /// <summary>
        /// 打开 DWM 的 alpha 合成路径，使 XAML 里的 Transparent 背景真正透视到窗口后面
        /// （WinUI 3 窗口默认无分层支持，透明背景会被渲染成黑色）。
        /// </summary>
        public static void EnableTransparency(Window window)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            var blur = new DWM_BLURBEHIND
            {
                dwFlags = DWM_BB_ENABLE,
                fEnable = 1,
                hRgnBlur = CreateRectRgn(0, 0, 0, 0),
            };
            try { DwmEnableBlurBehindWindow(hwnd, ref blur); }
            finally { if (blur.hRgnBlur != IntPtr.Zero) DeleteObject(blur.hRgnBlur); }
        }

        /// <summary>
        /// 剥掉 WS_THICKFRAME 不可见 resize 边框：OverlappedPresenter 即使
        /// SetBorderAndTitleBar(false,false) 后仍保留它，导致 XAML 内容比窗口
        /// 矩形内缩一圈，四周露出 DWM 玻璃白边。改完需 FRAMECHANGED 重算。
        /// </summary>
        public static void RemoveResizableFrame(Window window)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            int style = GetWindowLongW(hwnd, GWL_STYLE);
            style &= ~(WS_THICKFRAME | WS_CAPTION);
            SetWindowLongW(hwnd, GWL_STYLE, style);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        /// <summary>
        /// 去掉 Windows 11 DWM 给无边框窗口默认绘制的那圈窗口边框
        /// （浮窗矩形与卡片重合，默认边框会看起来像卡片外包了一圈白边）。
        /// 属性 34 仅 Win11+ 支持，低版本返回失败被静默忽略（Win10 无此边框）。
        /// </summary>
        public static void RemoveWindowBorder(Window window)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            int value = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref value, sizeof(int));
        }
    }
}
