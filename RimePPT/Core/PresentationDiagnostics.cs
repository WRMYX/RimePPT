using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimePPT.Services;
using System.Runtime.InteropServices;
namespace RimePPT.Core;
internal static class PresentationDiagnostics
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Events = new();
    internal static void Record(string stage, string detail)
    {
        string entry = $"{DateTimeOffset.Now:HH:mm:ss.fff} {stage}: {detail}";
        lock (Gate) { Events.Enqueue(entry); while (Events.Count > 120) Events.Dequeue(); }
        CrashReporter.Log(entry);
    }
    internal static string Snapshot()
    {
        DwmIsCompositionEnabled(out bool composition);
        string events; lock (Gate) events = string.Join(Environment.NewLine, Events);
        return $"RimePPT {GitHubUpdateService.InstalledVersion}\nWindows: {Environment.OSVersion}\nArchitecture: {RuntimeInformation.ProcessArchitecture}\nDWM: {composition}\nRemote session: {GetSystemMetrics(0x1000) != 0}\nSession: {Environment.GetEnvironmentVariable("SESSIONNAME")}\n\n{events}";
    }
    internal static string Export()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimePPT", "Diagnostics");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"presentation-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        writer.WriteLine(Snapshot());
        writer.WriteLine("\nDisplay adapters:");
        for (uint index = 0; index < 16; index++)
        {
            var display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref display, 0)) break;
            writer.WriteLine($"{display.Name}: {display.Description} | {display.DeviceId} | flags={display.Flags:X}");
            try
            {
                const string prefix = @"\Registry\Machine\";
                if (display.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(display.Key.Substring(prefix.Length));
                    writer.WriteLine($"Driver: {key?.GetValue("DriverVersion")} | {key?.GetValue("DriverDate")}");
                }
            }
            catch (System.Security.SecurityException) { writer.WriteLine("Driver registry unavailable"); }
        }
        foreach (string name in new[] { "rimeppt_app.log", "rimeppt_com.log" })
        {
            writer.WriteLine($"\n--- {name} ---");
            try { writer.Write(File.ReadAllText(Path.Combine(Path.GetTempPath(), name))); }
            catch (IOException ex) { writer.WriteLine(ex.Message); }
        }
        return path;
    }
    internal static void RecordWindow(Microsoft.UI.Xaml.Window window, string state)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        bool hasAlpha = GetLayeredWindowAttributes(handle, out _, out byte alpha, out uint flags);
        Record("批注窗口", $"{state}; hwnd={handle}; exstyle=0x{GetWindowLong(handle, -20):X8}; alpha={(hasAlpha ? alpha : -1)}; flags={flags}");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice display, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint key, out byte alpha, out uint flags);
}
