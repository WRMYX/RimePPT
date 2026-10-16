using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
namespace RimePPT.Core;
internal sealed class PowerPointSlideShowWindowWatcher : IDisposable
{
    private readonly WinEventDelegate _callback;
    private readonly HashSet<IntPtr> _known = new();
    private Thread? _thread;
    private volatile bool _running;
    private uint _threadId;
    private Timer? _processTimer;
    private readonly object _processGate = new();
    private HashSet<int> _processIds = new();
    internal event Action? ProgramChanged;
    internal event Action<IntPtr>? WindowShown;
    internal event Action<IntPtr>? WindowHidden;
    internal PowerPointSlideShowWindowWatcher() => _callback = OnWinEvent;
    internal void Start()
    {
        if (_thread is not null) return;
        _running = true;
        _processTimer = new Timer(_ => ScanProcesses(), null, 0, 250);
        _thread = new Thread(Pump) { IsBackground = true, Name = "RimePPT.ShowWindowWatcher" };
        _thread.Start();
    }
    private void Pump()
    {
        IntPtr hook = IntPtr.Zero;
        try
        {
            _threadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            hook = SetWinEventHook(0x8000, 0x8003, IntPtr.Zero, _callback, 0, 0, 2 /* skip own process */);
            if (hook == IntPtr.Zero) { CrashReporter.Log("show-window watcher unavailable; using Office fallback"); return; }
            EnumWindows((window, _) => { if (IsPowerPointSlideShow(window)) PublishShown(window); return true; }, IntPtr.Zero);
            while (_running && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception ex) { CrashReporter.Report(ex, "show-window-watcher"); }
        finally { if (hook != IntPtr.Zero) UnhookWinEvent(hook); _threadId = 0; }
    }
    private void ScanProcesses()
    {
        lock (_processGate)
        {
            if (!_running) return;
            try
            {
                var ids = new HashSet<int>();
                foreach (var process in Process.GetProcessesByName("POWERPNT"))
                    using (process) ids.Add(process.Id);
                if (!_processIds.SetEquals(ids)) { _processIds = ids; ProgramChanged?.Invoke(); }
            }
            catch (Exception ex) { CrashReporter.Log("PowerPoint process scan: " + ex.Message); }
        }
    }
    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint timestamp)
    {
        if (!_running || window == IntPtr.Zero || objectId != 0 || childId != 0) return;
        try
        {
            if (eventType == 0x8002)
            {
                var kind = new StringBuilder(64);
                GetClassName(window, kind, kind.Capacity);
                if (kind.ToString() == "PPTFrameClass") ProgramChanged?.Invoke();
            }
            if (eventType == 0x8002 && IsPowerPointSlideShow(window)) PublishShown(window);
            else if ((eventType == 0x8001 || eventType == 0x8003) && _known.Contains(window))
            {
                WindowHidden?.Invoke(window);
                if (eventType == 0x8001) _known.Remove(window);
            }
        }
        catch (Exception ex) { CrashReporter.Report(ex, "show-window-callback"); }
    }
    private void PublishShown(IntPtr window)
    {
        _known.Add(window);
        WindowShown?.Invoke(window);
    }
    internal static bool IsPowerPointSlideShow(IntPtr window)
    {
        if (!IsWindowVisible(window)) return false;
        var kind = new StringBuilder(64);
        if (GetClassName(window, kind, kind.Capacity) == 0 || kind.ToString() != "screenClass") return false;
        GetWindowThreadProcessId(window, out uint pid);
        try { using var process = Process.GetProcessById((int)pid); return process.ProcessName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    public void Dispose()
    {
        _running = false;
        _processTimer?.Dispose(); _processTimer = null;
        uint id = _threadId;
        if (id != 0) PostThreadMessage(id, 0x0012, IntPtr.Zero, IntPtr.Zero);
    }
    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint timestamp);
    private delegate bool EnumWindowsDelegate(IntPtr window, IntPtr state);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Window; public uint Id; public IntPtr WParam; public IntPtr LParam; public uint Time; public int X; public int Y; public uint Private; }
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsDelegate callback, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
}
