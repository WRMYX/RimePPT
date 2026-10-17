using System;
using System.ComponentModel;
using System.Collections.Generic;
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
        // UI-thread owned. Only presentation input surfaces may return focus to Office.
        private static readonly HashSet<IntPtr> PresentationInputWindows = new();
        private static void TrackPresentationInputWindow(Window window, IntPtr hwnd)
        {
            if (window is not (RimePPT.Windows.ToolbarWindow or RimePPT.Windows.ToolArrowWindow or RimePPT.Windows.AnnotationWindow)) return;
            if (PresentationInputWindows.Add(hwnd))
                window.Closed += (_, _) => PresentationInputWindows.Remove(hwnd);
        }

        internal static IntPtr ForegroundWindow => GetForegroundWindow();
        internal static bool TryRestorePresentationFocus(IntPtr showWindow)
        {
            if (showWindow == IntPtr.Zero || !IsWindow(showWindow) || !IsWindowVisible(showWindow) || IsIconic(showWindow)) return false;
            var foreground = GetForegroundWindow();
            if (foreground == showWindow) return true;
            if (!PresentationInputWindows.Contains(foreground) &&
                !PresentationInputWindows.Contains(GetAncestor(foreground, 2 /* GA_ROOT: do not include owned dialogs */))) return false;
            GetWindowThreadProcessId(showWindow, out uint targetProcess);
            if (targetProcess == 0 || targetProcess == (uint)Environment.ProcessId) return false;
            // Never attach input queues or repeatedly steal focus when Windows refuses.
            return SetForegroundWindow(showWindow) && GetForegroundWindow() == showWindow;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);

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

        /// <summary>整窗透明度，用于只有纯色背景的引导遮罩。</summary>
        internal static void SetOpacity(Window window, byte opacity)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            int style = GetWindowLongW(hwnd, GWL_EXSTYLE);
            Marshal.SetLastPInvokeError(0);
            int previous = SetWindowLongW(hwnd, GWL_EXSTYLE, (int)((uint)style | WS_EX_LAYERED));
            int error = Marshal.GetLastWin32Error();
            if (previous == 0 && error != 0) throw new Win32Exception(error);
            if (!SetLayeredWindowAttributes(hwnd, 0, opacity, 2 /* LWA_ALPHA */))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

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
        public static void SetClickThrough(Window window, bool passthrough, bool preserveLayered = false)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            int style = GetWindowLongW(hwnd, GWL_EXSTYLE);
            if (passthrough)
            {
                style = (int)((uint)style | WS_EX_TRANSPARENT | WS_EX_LAYERED);
            }
            else
            {
                style = (int)((uint)style & ~(WS_EX_TRANSPARENT | (preserveLayered ? 0 : WS_EX_LAYERED)));
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

            return Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd),
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
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
            TrackPresentationInputWindow(window, hwnd);
            int style = GetWindowLongW(hwnd, GWL_EXSTYLE);
            SetWindowLongW(hwnd, GWL_EXSTYLE, (int)((uint)style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            if (!SetWindowSubclass(hwnd, PointerNoActivateProc, new UIntPtr(0x52494D45), UIntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure annotation input.");
        }

        // WS_EX_NOACTIVATE 会使部分新建浮窗忽略触摸；通过消息拒绝激活但保留输入。
        public static void ApplyPointerNoActivate(Window window)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            TrackPresentationInputWindow(window, hwnd);
            int style = GetWindowLongW(hwnd, GWL_EXSTYLE);
            SetWindowLongW(hwnd, GWL_EXSTYLE, (int)(((uint)style & ~(uint)WS_EX_NOACTIVATE) | WS_EX_TOOLWINDOW));
            if (!SetWindowSubclass(hwnd, PointerNoActivateProc, new UIntPtr(0x52494D45), UIntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure floating tool input.");
        }

        private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
        private static readonly SubclassProc PointerNoActivateProc = HandlePointerNoActivate;
        private static readonly SubclassProc TransparentBackgroundProc = HandleTransparentBackground;
        private static readonly UIntPtr TransparentBackgroundId = new(0x52494D46);

        // XAML's transparent island does not clear the HWND's GDI redirection surface.
        // DWM glass needs zeroed pixels here; the default light-theme erase paints white.
        internal static void UseTransparentNativeBackground(Window window)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (!SetWindowSubclass(hwnd, TransparentBackgroundProc, TransparentBackgroundId, UIntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure transparent window background.");
            // The HWND can already have received its first erase before XAML Content is attached.
            // Clear that surface too; waiting for another erase leaves the old white pixels behind.
            var hdc = GetDC(hwnd);
            if (hdc != IntPtr.Zero)
            {
                try { if (GetClientRect(hwnd, out var bounds)) FillRect(hdc, ref bounds, GetStockObject(4)); }
                finally { ReleaseDC(hwnd, hdc); }
            }
        }

        private static IntPtr HandleTransparentBackground(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
        {
            if (message == 0x0014 && GetClientRect(hwnd, out var bounds)) // WM_ERASEBKGND
            {
                if (FillRect(unchecked((nint)(nuint)wParam), ref bounds, GetStockObject(4)) != 0)
                    return new IntPtr(1); // BLACK_BRUSH writes the zero-alpha DWM background, not a XAML theme colour.
            }
            if (message == 0x0082) RemoveWindowSubclass(hwnd, TransparentBackgroundProc, id);
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hwnd, out RECT rectangle);
        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hdc, ref RECT rectangle, IntPtr brush);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int objectType);

        private static IntPtr HandlePointerNoActivate(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
        {
            if (message is 0x0021 or 0x024B) return new IntPtr(3); // MA_NOACTIVATE / PA_NOACTIVATE
            if (message == 0x0082) RemoveWindowSubclass(hwnd, PointerNoActivateProc, id);
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }
        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id, UIntPtr data);
        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id);
        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 打开 DWM 的 alpha 合成路径，使 XAML 里的 Transparent 背景真正透视到窗口后面
        /// （WinUI 3 窗口默认无分层支持，透明背景会被渲染成黑色）。
        /// </summary>
        public static void EnableTransparency(Window window, bool enableBlur = true)
        {
            UseTransparentNativeBackground(window);
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            var blur = new DWM_BLURBEHIND
            {
                dwFlags = DWM_BB_ENABLE,
                fEnable = enableBlur ? 1 : 0,
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
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
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
