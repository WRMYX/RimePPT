using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RimePPT.Services
{
    /// <summary>
    /// Win32 system tray icon with right-click context menu.
    /// Attach to a hidden WinUI 3 window HWND for message routing.
    /// </summary>
    public sealed class TrayService : IDisposable
    {
        // ── Win32 Constants ───────────────────────────────────────────
        private const uint NIM_ADD    = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON    = 0x00000002;
        private const uint NIF_TIP     = 0x00000004;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint MF_STRING  = 0x00000000;
        private const uint MF_GRAYED  = 0x00000001;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;

        // Tray callback message (WM_USER+1)
        private const uint WM_TRAY_CALLBACK = 0x0401;

        // Menu item IDs
        private const uint ID_SETTINGS = 1001;
        private const uint ID_EXIT     = 1002;

        // ── P/Invoke ──────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        // ── State ─────────────────────────────────────────────────────
        private readonly IntPtr _hwnd;
        private IntPtr _hIcon;
        private bool _added;
        private bool _disposed;

        public event Action? OpenSettingsRequested;
        public event Action? ExitRequested;

        // ── Constructor ───────────────────────────────────────────────
        public TrayService(IntPtr hwnd)
        {
            _hwnd = hwnd;

            // Load icon from exe
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            _hIcon = ExtractIcon(IntPtr.Zero, exe, 0);
            if (_hIcon == IntPtr.Zero)
                _hIcon = ExtractIcon(IntPtr.Zero, "shell32.dll", 1); // fallback

            // Register message handler via WindowHelper
            Helpers.WindowHelper.AddMessageHandler(hwnd, WM_TRAY_CALLBACK, OnTrayMessage);
        }

        // ── Public API ────────────────────────────────────────────────

        public void Show()
        {
            if (_added) return;
            var nid = BuildNid();
            Shell_NotifyIcon(NIM_ADD, ref nid);
            _added = true;
        }

        public void Hide()
        {
            if (!_added) return;
            var nid = BuildNid();
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _added = false;
        }

        // ── Tray message handler ──────────────────────────────────────
        private IntPtr OnTrayMessage(IntPtr wParam, IntPtr lParam)
        {
            uint msg = (uint)lParam.ToInt64();

            if (msg == WM_RBUTTONUP)
                ShowContextMenu();
            else if (msg == WM_LBUTTONDBLCLK)
                OpenSettingsRequested?.Invoke();

            return IntPtr.Zero;
        }

        private void ShowContextMenu()
        {
            SetForegroundWindow(_hwnd);
            GetCursorPos(out var pt);

            IntPtr hMenu = CreatePopupMenu();
            AppendMenu(hMenu, MF_STRING, ID_SETTINGS, "⚙ 打开设置");
            AppendMenu(hMenu, MF_SEPARATOR, 0, null);
            AppendMenu(hMenu, MF_STRING, ID_EXIT, "✕ 退出");

            uint cmd = TrackPopupMenu(hMenu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                pt.x, pt.y, 0, _hwnd, IntPtr.Zero);

            DestroyMenu(hMenu);

            if (cmd == ID_SETTINGS) OpenSettingsRequested?.Invoke();
            else if (cmd == ID_EXIT) ExitRequested?.Invoke();
        }

        // ── Helpers ───────────────────────────────────────────────────
        private NOTIFYICONDATA BuildNid() => new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd   = _hwnd,
            uID    = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY_CALLBACK,
            hIcon  = _hIcon,
            szTip  = "RimePPT"
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Hide();
            if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
        }
    }
}
