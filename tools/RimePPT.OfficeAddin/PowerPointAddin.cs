using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.IO.MemoryMappedFiles;
using System.Diagnostics;

namespace RimePPT.OfficeAddin;

[ComVisible(true), Guid("B65AD801-ABAF-11D0-BB8B-00A0C90F2744"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IDTExtensibility2
{
    [DispId(1)] void OnConnection([MarshalAs(UnmanagedType.IDispatch)] object application, int connectMode,
        [MarshalAs(UnmanagedType.IDispatch)] object addin, [In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
    [DispId(2)] void OnDisconnection(int removeMode, [In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
    [DispId(3)] void OnAddInsUpdate([In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
    [DispId(4)] void OnStartupComplete([In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
    [DispId(5)] void OnBeginShutdown([In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
}

/// <summary>No UI, file writes or Office calls inside event callbacks. Polling remains the fallback.</summary>
[ComVisible(true), Guid("66E789D4-F1AF-4C5D-8F8B-7C4A560EE82C"), ProgId("RimePPT.PowerPointAddin")]
[ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IDTExtensibility2))]
public sealed class PowerPointAddin : IDTExtensibility2
{
    private static readonly Guid Events = new("914934C2-5A91-11CF-8700-00AA0060263B");
    private static readonly int[] EventIds = { 2011, 2013, 2014, 2006, 2007, 2004, 2008, 2009 };
    private object? _application;
    private readonly Delegate[] _handlers =
    {
        new Action<object>(_ => Signal(1)), new Action<object>(_ => Signal(2)), new Action<object>(_ => Signal(3)),
        new Action<object>(_ => Signal(4)), new Action<object>(_ => Signal(4)), new Action<object>(_ => Signal(4)),
        new Action<object>(_ => Signal(4)), new Action<object, object>((_, _) => Signal(4))
    };
    private int _connected;

    public void OnConnection(object application, int connectMode, object addin, ref Array custom)
    {
        Disconnect();
        _application = application;
        try
        {
            foreach (int id in EventIds)
            {
                ComEventsHelper.Combine(application, Events, id, _handlers[_connected]);
                _connected++;
            }
            Signal(0);
        }
        catch { Disconnect(); } // An optional bridge must never fail PowerPoint startup.
    }
    private static void Signal(int kind)
    {
        try
        {
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            using var signal = EventWaitHandle.OpenExisting(@"Local\RimePPT.Office." + sid);
            try
            {
                using var gate = new Mutex(false, @"Local\RimePPT.Office.Write." + sid);
                bool owned = false;
                try
                {
                    try { owned = gate.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                    if (owned)
                    {
                        using var map = MemoryMappedFile.OpenExisting(@"Local\RimePPT.Office.State." + sid);
                        using var view = map.CreateViewAccessor(0, 64);
                        long sequence = (view.ReadInt64(0) & ~1L) + 2;
                        view.Write(0, sequence - 1);
                        view.Write(8, kind);
                        using var process = Process.GetCurrentProcess();
                        view.Write(12, process.Id);
                        view.Write(16, DateTime.UtcNow.Ticks);
                        Thread.MemoryBarrier();
                        view.Write(0, sequence);
                    }
                }
                finally { if (owned) gate.ReleaseMutex(); }
            }
            catch { /* Older main versions only support the wake event. */ }
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { } // Main application is not running.
        catch (UnauthorizedAccessException) { }
        catch (Exception) { } // Nothing crosses the Office callback boundary.
    }
    private void Disconnect()
    {
        if (_application is not null)
            for (int i = _connected - 1; i >= 0; i--)
                try { ComEventsHelper.Remove(_application, Events, EventIds[i], _handlers[i]); } catch { }
        _connected = 0;
        // Do not force-release the Office-owned application RCW.
        _application = null;
    }
    public void OnDisconnection(int removeMode, ref Array custom) => Disconnect();
    public void OnAddInsUpdate(ref Array custom) { }
    public void OnStartupComplete(ref Array custom) => Signal(0);
    public void OnBeginShutdown(ref Array custom) => Disconnect();
}
