using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RimePPT.Helpers
{
    /// <summary>
    /// Win32 P/Invoke helpers for transparent overlay window management
    /// </summary>
    internal static class WindowHelper
    {
        // ── Win32 Constants ───────────────────────────────────────────
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED   = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const uint SWP_NOMOVE      = 0x0002;
        private const uint SWP_NOSIZE      = 0x0001;
        private const uint SWP_NOACTIVATE  = 0x0010;
        private const int  WM_NCHITTEST    = 0x0084;
        private const int  HTTRANSPARENT   = -1;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        // ── P/Invoke ──────────────────────────────────────────────────
        [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

        // Comctl32 subclassing
        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        // ── Structs ───────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS { public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }

        // ── Hit-test subclass (ID=1) ──────────────────────────────────
        private static readonly Dictionary<IntPtr, Func<int, int, bool>> _hitTestMap = new();
        private static readonly SUBCLASSPROC _hitTestProc = HitTestSubclassCallback;   // keep alive

        // ── Generic message handler subclass (ID=2) ───────────────────
        private static readonly Dictionary<IntPtr, Dictionary<uint, Func<IntPtr, IntPtr, IntPtr>>> _msgMap = new();
        private static readonly SUBCLASSPROC _msgProc = MsgSubclassCallback;           // keep alive

        // ── Public API ────────────────────────────────────────────────

        /// <summary>
        /// Apply WS_EX_LAYERED + DWM glass extension to make window background transparent.
        /// Call BEFORE showing the window; set XAML Grid Background="Transparent".
        /// </summary>
        public static void MakeTransparentOverlay(IntPtr hwnd)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE,
                ex | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            // Extend DWM glass into entire client area → enables true transparency
            var m = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
        }

        /// <summary>Pin the window above all others (HWND_TOPMOST).</summary>
        public static void SetTopmost(IntPtr hwnd)
            => SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        /// <summary>
        /// Register a hit-test callback. Areas where callback returns false become
        /// click-through (HTTRANSPARENT); areas that return true receive input normally.
        /// </summary>
        public static void EnableSelectiveClickThrough(IntPtr hwnd, Func<int, int, bool> isInteractive)
        {
            _hitTestMap[hwnd] = isInteractive;
            SetWindowSubclass(hwnd, _hitTestProc, new UIntPtr(1), UIntPtr.Zero);
        }

        public static void DisableSelectiveClickThrough(IntPtr hwnd)
        {
            _hitTestMap.Remove(hwnd);
            RemoveWindowSubclass(hwnd, _hitTestProc, new UIntPtr(1));
        }

        /// <summary>Register a handler for a specific Win32 message on this window.</summary>
        public static void AddMessageHandler(IntPtr hwnd, uint msg, Func<IntPtr, IntPtr, IntPtr> handler)
        {
            if (!_msgMap.TryGetValue(hwnd, out var map))
            {
                map = new Dictionary<uint, Func<IntPtr, IntPtr, IntPtr>>();
                _msgMap[hwnd] = map;
                SetWindowSubclass(hwnd, _msgProc, new UIntPtr(2), UIntPtr.Zero);
            }
            map[msg] = handler;
        }

        /// <summary>Get DPI scale factor (physical/logical) for the given window.</summary>
        public static double GetDpiScale(IntPtr hwnd)
            => GetDpiForWindow(hwnd) / 96.0;

        // ── Subclass callbacks ────────────────────────────────────────
        private static IntPtr HitTestSubclassCallback(
            IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
            UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (uMsg == WM_NCHITTEST && _hitTestMap.TryGetValue(hWnd, out var cb))
            {
                // lParam: screen coordinates (signed short per axis)
                int sx = (int)(short)(lParam.ToInt64() & 0xFFFF);
                int sy = (int)(short)((lParam.ToInt64() >> 16) & 0xFFFF);
                var pt = new POINT { x = sx, y = sy };
                ScreenToClient(hWnd, ref pt);

                if (!cb(pt.x, pt.y))
                    return new IntPtr(HTTRANSPARENT);
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private static IntPtr MsgSubclassCallback(
            IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
            UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (_msgMap.TryGetValue(hWnd, out var map) && map.TryGetValue(uMsg, out var handler))
                return handler(wParam, lParam);

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }
    }
}
