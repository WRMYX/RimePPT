using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace RimePPT.Services;

public sealed class NativeTrayIcon : IDisposable
{
    private const uint Callback = 0x8001;
    private readonly WindowProc _proc;
    private readonly string _className = "RimePPT.Tray." + Environment.ProcessId;
    private readonly IntPtr _module;
    private readonly uint _taskbarCreated;
    private IntPtr _window, _icon;
    private bool _ownedIcon, _disposed, _version4;
    public event Action? OpenRequested;
    public event Action<PointInt32>? MenuRequested;
    public IntPtr Handle => _window;

    public NativeTrayIcon(string path)
    {
        _proc = ProcessMessage;
        _module = GetModuleHandle(null);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = _module,
            ClassName = _className, Procedure = Marshal.GetFunctionPointerForDelegate(_proc) };
        if (RegisterClassEx(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _window = CreateWindowEx(0, _className, "RimePPT tray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);
        if (_window == IntPtr.Zero) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        _icon = LoadImage(IntPtr.Zero, path, 1, 32, 32, 0x10);
        _ownedIcon = _icon != IntPtr.Zero;
        if (!_ownedIcon) _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        try { AddIcon(); } catch { Dispose(); throw; }
    }

    private IconData Data() => new() { Size = (uint)Marshal.SizeOf<IconData>(), Window = _window, Id = 1,
        Flags = 1 | 2 | 4 | 0x80, Message = Callback, Icon = _icon, Tip = "RimePPT 放映助手", Info = "", InfoTitle = "", Version = 4 };
    private void AddIcon()
    {
        var data = Data();
        if (!Shell_NotifyIcon(0, ref data)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _version4 = Shell_NotifyIcon(4, ref data);
    }
    private IntPtr ProcessMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        try
        {
            if (msg == _taskbarCreated && !_disposed) AddIcon();
            if (msg == Callback && !_disposed)
            {
                uint action = (uint)(lp.ToInt64() & 0xffff);
                if (action is 0x400 or 0x401 or 0x203) OpenRequested?.Invoke();
                else if (action == 0x7b || (!_version4 && action == 0x205))
                {
                    int x = (short)(wp.ToInt64() & 0xffff), y = (short)((wp.ToInt64() >> 16) & 0xffff);
                    if (!_version4 || (x == -1 && y == -1))
                    {
                        var id = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = _window, Id = 1 };
                        if (Shell_NotifyIconGetRect(ref id, out var rect) == 0) { x = rect.Left; y = rect.Top; }
                        else { GetCursorPos(out var p); x = p.X; y = p.Y; }
                    }
                    MenuRequested?.Invoke(new PointInt32(x, y));
                }
            }
        }
        catch (Exception ex) { RimePPT.Core.CrashReporter.Log("native tray callback: " + ex); }
        return DefWindowProc(hwnd, msg, wp, lp);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_window != IntPtr.Zero) { var data = Data(); Shell_NotifyIcon(2, ref data); DestroyWindow(_window); _window = IntPtr.Zero; }
        if (_ownedIcon) DestroyIcon(_icon);
        UnregisterClass(_className, _module);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName; public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct IconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Message; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref IconData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier id, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr module, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string cls, IntPtr instance);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr h, string name, uint type, int w, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
}
