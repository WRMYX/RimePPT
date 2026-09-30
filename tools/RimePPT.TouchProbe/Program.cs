using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.Windows.Automation;

namespace RimePPT.TouchProbe;

internal static class Program
{
    private static readonly List<object> Results = new();
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "rimeppt_ink.jsonl");
    private static int _logOffset;
    private static int _pid;
    private static string _output = "touch-probe-results.json";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("Usage: RimePPT.TouchProbe <RimePPT.exe> [results.json]"); return 2; }
        if (args.Length > 1) _output = Path.GetFullPath(args[1]);
        Process? owned = null;
        try
        {
            if (Process.GetProcessesByName("RimePPT").Length > 0) throw new InvalidOperationException("Close existing RimePPT first; probe never replaces a user-owned process.");
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            if (!Native.InitializeTouchInjection(10, 3)) throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeTouchInjection failed");
            _logOffset = ReadLines().Length;
            var start = new ProcessStartInfo(Path.GetFullPath(args[0]), "--settings") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
            start.Environment["RIMEPPT_INK_DIAGNOSTICS"] = "1";
            owned = Process.Start(start) ?? throw new Exception("Launch failed"); _pid = owned.Id;
            var settings = Wait(() => FindWindow("RimePPT 设置"), "settings window");
            var navigation = Find(settings, "调试");
            ((SelectionItemPattern)navigation.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            var startButton = Wait(() => settings.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "启动"))
                .Cast<AutomationElement>().FirstOrDefault(e => !e.Current.IsOffscreen && e.Current.BoundingRectangle.Width > 0), "debug start button");
            ((InvokePattern)startButton.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var annotation = Wait(() => FindWindow("RimePPT Annotation"), "annotation window");
            var toolbar = Wait(() => FindWindow("RimePPT Toolbar"), "toolbar window");
            // Hide only the owned settings window so tests cannot hit it instead of the ink surface.
            Native.ShowWindow(new IntPtr(settings.Current.NativeWindowHandle), 0);
            Native.SetWindowPos(new IntPtr(toolbar.Current.NativeWindowHandle), new IntPtr(-1), 0, 0, 0, 0, 0x13);
            Thread.Sleep(500);
            var rect = annotation.Current.BoundingRectangle;
            int cx = (int)(rect.Left + rect.Width * .5), cy = (int)(rect.Top + rect.Height * .5);
            int radius = (int)Math.Min(180, Math.Min(rect.Width, rect.Height) * .2);
            Tap(Find(toolbar, "批注")); Thread.Sleep(300);
            int before = Ends().Count;
            DrawCircle(cx, cy, radius, 64, 4);
            ExpectTouchEnd(before, "touch-circle");
            before = Ends().Count;
            Native.Frame(Contact(1, cx - radius, cy, Native.Down)); Thread.Sleep(10);
            Native.Frame(Contact(1, cx - radius + 10, cy, Native.Update), Contact(2, cx + radius, cy + 80, Native.Down)); Thread.Sleep(10);
            Native.Frame(Contact(1, cx, cy, Native.Update), Contact(2, cx + radius, cy + 80, Native.Update)); Thread.Sleep(10);
            Native.Frame(Contact(1, cx, cy, Native.Update), Contact(2, cx + radius, cy + 80, Native.Up)); Thread.Sleep(10);
            Native.Frame(Contact(1, cx + radius, cy, Native.Update)); Thread.Sleep(10);
            Native.Frame(Contact(1, cx + radius, cy, Native.Up));
            ExpectTouchEnd(before, "second-contact-ignored");
            before = Ends().Count;
            Native.Frame(Contact(1, cx, cy - radius, Native.Down)); Thread.Sleep(20);
            Native.Frame(Contact(1, cx + 20, cy - radius, Native.Update)); Thread.Sleep(20);
            Native.Frame(Contact(1, cx + 20, cy - radius, Native.Up | 0x8000));
            ExpectTouchEnd(before, "cancel-cleans-session");
            before = Ends().Count; DrawCircle(cx, cy, radius / 2, 16, 3); ExpectTouchEnd(before, "restart-after-cancel");
            before = Ends().Count;
            Tap(Find(toolbar, "橡皮")); Thread.Sleep(250);
            StrokeLine(cx - radius - 30, cy, cx + radius + 30, cy, 12, 8);
            ExpectTouchEnd(before, "touch-eraser");
            // Real touch on toolbar verifies it remains above the overlay.
            Tap(Find(toolbar, "下一页")); Thread.Sleep(250);
            Tap(Find(toolbar, "批注")); Thread.Sleep(250);
            before = Ends().Count; DrawCircle(cx, cy, radius / 2, 32, 5); ExpectTouchEnd(before, "write-after-slide-change");
            Tap(Find(toolbar, "上一页")); Thread.Sleep(250);
            before = Ends().Count; StrokeLine(cx - radius, cy + radius, cx + radius, cy + radius, 24, 4); ExpectTouchEnd(before, "write-after-return");
            if (args.Contains("--stress"))
            {
                before = Ends().Count;
                DrawCircle(cx, cy, radius, 1200, 5);
                ExpectTouchEnd(before, "long-touch-stroke-1200");
                before = Ends().Count;
                for (int i = 0; i < 500; i++)
                {
                    int y = cy - radius + i % 100 * Math.Max(1, radius * 2 / 100);
                    int x = cx - radius + i / 100 * 40;
                    StrokeLine(x, y, x + 25, y + 5, 2, 5);
                }
                var wait = Stopwatch.StartNew();
                while (Ends().Count < before + 500 && wait.ElapsedMilliseconds < 5000) Thread.Sleep(100);
                var stress = Ends().Skip(before).ToList();
                if (stress.Count != 500 || stress.Any(e => e.GetProperty("dots").GetInt32() < 2))
                    throw new Exception($"500-stroke stress did not commit all strokes: observed={stress.Count}, empty={stress.Count(e => e.GetProperty("dots").GetInt32() < 2)}");
                Results.Add(new { test = "touch-500-strokes", status = "passed", strokes = stress.Count,
                    inputMs = stress.Sum(e => e.GetProperty("inputMs").GetDouble()), drawMs = stress.Sum(e => e.GetProperty("drawMs").GetDouble()),
                    draws = stress.Sum(e => e.GetProperty("draws").GetInt32()) });
                Console.WriteLine("PASS touch-500-strokes");
                Tap(Find(toolbar, "橡皮")); Thread.Sleep(250);
                before = Ends().Count;
                StrokeLine(cx - radius, cy - radius, cx - radius, cy + radius, 120, 5);
                ExpectTouchEnd(before, "erase-dense-page");
            }
            Thread.Sleep(500);
            var cache = Records().Where(e => e.GetProperty("kind").GetString() == "cache").ToList();
            if (cache.Count == 0 || cache.Any(e => e.GetProperty("bytes").GetInt64() > 64L * 1024 * 1024)) throw new Exception("Cache budget exceeded or cache missing");
            Results.Add(new { test = "cache-budget", status = "passed", maxBytes = cache.Max(e => e.GetProperty("bytes").GetInt64()) });
            Tap(Find(toolbar, "退出")); Thread.Sleep(300);
            Save("passed"); Console.WriteLine("PASS real injected touch scenarios; physical HID compatibility remains unverified."); return 0;
        }
        catch (Exception ex)
        {
            Results.Add(new { test = "probe", status = "failed", error = ex.Message }); Save("failed");
            Console.WriteLine("FAIL " + ex.Message); return 1;
        }
        finally
        {
            // Only the process created by this probe is eligible for cleanup.
            if (owned is not null && !owned.HasExited) { owned.Kill(); owned.WaitForExit(3000); }
            owned?.Dispose();
        }
    }

    private static void Save(string status) => File.WriteAllText(_output, JsonSerializer.Serialize(new { status, results = Results, diagnostics = Records() }, new JsonSerializerOptions { WriteIndented = true }));
    private static string[] ReadLines()
    {
        try
        {
            if (!File.Exists(LogPath)) return Array.Empty<string>();
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            // Ignore a concurrent writer's unfinished last line and retry it in the next snapshot.
            int end = text.LastIndexOf('\n');
            return end >= 0 ? text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        }
        catch (IOException) { return Array.Empty<string>(); }
    }
    private static List<JsonElement> Records() => ReadLines().Skip(_logOffset).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
    private static List<JsonElement> Ends() => Records().Where(e => e.GetProperty("kind").GetString() == "end").ToList();
    private static void ExpectTouchEnd(int before, string test)
    {
        var watch = Stopwatch.StartNew();
        while (Ends().Count <= before && watch.ElapsedMilliseconds < 3000) Thread.Sleep(50);
        var records = Ends().Skip(before).ToList();
        if (records.Count != 1 || records[0].GetProperty("device").GetString() != "Touch" || records[0].GetProperty("reason").GetString() == "capture-failed")
            throw new Exception(test + ": missing or unexpected touch completion: " + JsonSerializer.Serialize(records));
        var record = records[0];
        bool erase = test.Contains("eraser") || test == "erase-dense-page";
        if (erase && (record.GetProperty("tool").GetString() != "Eraser" || record.GetProperty("changes").GetInt32() == 0))
            throw new Exception(test + ": contact did not erase ink");
        if (!erase && record.GetProperty("dots").GetInt32() == 0) throw new Exception(test + ": no ink committed");
        if (test == "cancel-cleans-session" && record.GetProperty("reason").GetString() != "canceled") throw new Exception("cancel event missing");
        if (test == "write-after-slide-change" && record.GetProperty("slide").GetInt32() != 2) throw new Exception("toolbar did not change slide");
        if (test == "write-after-return" && record.GetProperty("slide").GetInt32() != 1) throw new Exception("toolbar did not return to slide 1");
        Results.Add(new { test, status = "passed", input = records[0] }); Console.WriteLine("PASS " + test);
    }
    private static AutomationElement? FindWindow(string title)
    {
        var element = AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, _pid), new PropertyCondition(AutomationElement.NameProperty, title)));
        if (element is not null) return element;
        IntPtr handle = Native.FindWindow(null, title);
        Native.GetWindowThreadProcessId(handle, out uint pid);
        if (handle == IntPtr.Zero || pid != _pid) return null;
        // STARTUPINFO Hidden can suppress the first Activate call; this is test setup, not app input.
        Native.ShowWindow(handle, 4);
        return AutomationElement.FromHandle(handle);
    }
    private static AutomationElement Find(AutomationElement parent, string name) => parent.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.NameProperty, name)) ?? throw new Exception("Element not found: " + name);
    private static AutomationElement Wait(Func<AutomationElement?> find, string description)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 15000) { if (find() is { } element) return element; Thread.Sleep(100); }
        throw new Exception("Timeout waiting for " + description);
    }
    private static void Tap(AutomationElement element)
    {
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty || rect.Width <= 0 || element.Current.IsOffscreen) throw new Exception("Element not visible: " + element.Current.Name);
        int x = (int)(rect.Left + rect.Width / 2), y = (int)(rect.Top + rect.Height / 2);
        IntPtr target = Native.GetAncestor(Native.WindowFromPoint(new() { X = x, Y = y }), 2);
        var title = new StringBuilder(128); Native.GetWindowText(target, title, title.Capacity);
        Console.WriteLine($"TAP {element.Current.Name} at {x},{y}; window={title}");
        Native.Frame(Contact(1, x, y, Native.Down)); Thread.Sleep(40);
        Native.Frame(Contact(1, x, y, Native.Update)); Thread.Sleep(10);
        Native.Frame(Contact(1, x, y, Native.Up)); Thread.Sleep(150);
    }
    private static void DrawCircle(int cx, int cy, int radius, int count, int delay)
    {
        Native.Frame(Contact(1, cx + radius, cy, Native.Down)); Thread.Sleep(delay);
        int x = cx + radius, y = cy;
        for (int i = 1; i <= count; i++) {
            x = cx + (int)Math.Round(radius * Math.Cos(i * Math.Tau / count)); y = cy + (int)Math.Round(radius * Math.Sin(i * Math.Tau / count));
            Native.Frame(Contact(1, x, y, Native.Update)); Thread.Sleep(delay);
        }
        Native.Frame(Contact(1, x, y, Native.Up));
    }
    private static void StrokeLine(int x1, int y1, int x2, int y2, int count, int delay)
    {
        Native.Frame(Contact(1, x1, y1, Native.Down)); Thread.Sleep(delay);
        for (int i = 1; i <= count; i++) { Native.Frame(Contact(1, x1 + (x2 - x1) * i / count, y1 + (y2 - y1) * i / count, Native.Update)); Thread.Sleep(delay); }
        Native.Frame(Contact(1, x2, y2, Native.Up));
    }
    private static Native.Touch Contact(uint id, int x, int y, uint flags) => new() {
        Info = new() { Type = 2, Id = id, Flags = flags, Pixel = new() { X = x, Y = y } },
        Mask = 1, Contact = new() { Left = x - 3, Top = y - 3, Right = x + 3, Bottom = y + 3 }
    };
}

internal static class Native
{
    private static long _lastFrame;
    public const uint Down = 0x10000 | 2 | 4, Update = 0x20000 | 2 | 4, Up = 0x40000;
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct PointerInfo {
        public uint Type, Id, FrameId, Flags; public IntPtr SourceDevice, Target;
        public Point Pixel, Himetric, PixelRaw, HimetricRaw;
        public uint Time, HistoryCount; public int InputData; public uint KeyStates;
        public ulong PerformanceCount; public uint ButtonChange;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Touch { public PointerInfo Info; public uint Flags, Mask; public Rect Contact, ContactRaw; public uint Orientation, Pressure; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool InitializeTouchInjection(uint maxCount, uint mode);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InjectTouchInput(uint count, [In] Touch[] contacts);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")] public static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    public static void Frame(params Touch[] contacts) {
        // Default system timestamps still require distinct injection frames.
        while ((Stopwatch.GetTimestamp() - _lastFrame) * 1000d / Stopwatch.Frequency < 5) Thread.Sleep(1);
        if (!InjectTouchInput((uint)contacts.Length, contacts))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"InjectTouchInput failed ({error}: {new Win32Exception(error).Message}; contacts={contacts.Length}, size={Marshal.SizeOf<Touch>()})");
        }
        _lastFrame = Stopwatch.GetTimestamp();
    }
}
