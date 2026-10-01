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
    private static uint _tapId = 0;
    private static string _output = "touch-probe-results.json";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("Usage: RimePPT.TouchProbe <RimePPT.exe> [results.json]"); return 2; }
        if (args.Length > 1) _output = Path.GetFullPath(args[1]);
        Process? owned = null;
        bool keepPreview = false;
        System.Windows.Forms.Form? backplate = null;
        try
        {
            if (Process.GetProcessesByName("RimePPT").Length > 0) throw new InvalidOperationException("Close existing RimePPT first; probe never replaces a user-owned process.");
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            if (!Native.InitializeTouchInjection(10, 3)) throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeTouchInjection failed");
            _logOffset = ReadLines().Length;
            var start = new ProcessStartInfo(Path.GetFullPath(args[0]), args.Contains("--prompt-test") ? "--settings --prompt-preview=" + (args.Contains("--load") ? "load" : "save") : "--settings") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
            start.Environment["RIMEPPT_INK_DIAGNOSTICS"] = "1";
            owned = Process.Start(start) ?? throw new Exception("Launch failed"); _pid = owned.Id;
            var settings = Wait(() => FindWindow("RimePPT 设置"), "settings window");
            if (args.Contains("--prompt-test"))
            {
                VerifyPrompt(args.Contains("--load"), args.Contains("--delete"));
                Save("passed"); return 0;
            }
            var navigation = Find(settings, "调试");
            ((SelectionItemPattern)navigation.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            var startButton = Wait(() => settings.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "启动"))
                .Cast<AutomationElement>().FirstOrDefault(e => !e.Current.IsOffscreen && e.Current.BoundingRectangle.Width > 0), "debug start button");
            var entrance = args.Contains("--tools-test") ? System.Threading.Tasks.Task.Run(() =>
            {
                var frames = new List<Native.Rect>();
                var watch = Stopwatch.StartNew();
                IntPtr handle = IntPtr.Zero;
                long lastMove = 0;
                int lastLeft = int.MinValue;
                while (watch.ElapsedMilliseconds < 30000)
                {
                    if (handle == IntPtr.Zero) handle = Native.FindWindow(null, "RimePPT Toolbar");
                    if (handle != IntPtr.Zero && Native.IsWindowVisible(handle) && Native.GetWindowRect(handle, out var frame) && frame.Right - frame.Left > 20)
                    {
                        frames.Add(frame);
                        if (frame.Left != lastLeft) { lastMove = watch.ElapsedMilliseconds; lastLeft = frame.Left; }
                        if (frame.Left >= 0 && frame.Right <= System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width && watch.ElapsedMilliseconds - lastMove > 800) break;
                    }
                    Thread.Sleep(4);
                }
                return frames;
            }) : null;
            ((InvokePattern)startButton.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var annotation = Wait(() => FindWindow("RimePPT Annotation"), "annotation window");
            var toolbar = Wait(() => FindWindow("RimePPT Toolbar"), "toolbar window");
            WaitForToolbarPlacement(toolbar);
            if (args.Contains("--timer-drag"))
            {
                Native.ShowWindow(new IntPtr(settings.Current.NativeWindowHandle), 0);
                VerifyTimerDrag(toolbar);
                if (args.Contains("--keep")) { keepPreview = true; Console.WriteLine("Preview left running; PID=" + _pid); }
                Save("passed"); return 0;
            }
            if (args.Contains("--tools-test"))
            {
                var frames = entrance!.GetAwaiter().GetResult();
                Native.SystemParametersInfo(0x1042, 0, out bool animations, 0);
                Results.Add(new { test = "entrance-positions", animations, positions = frames.Select(f => f.Left).Distinct().ToArray() });
                if (animations) Assert(frames.Select(f => f.Left).Distinct().Count() > 3, "visible entrance moves across frames");
                Native.ShowWindow(new IntPtr(settings.Current.NativeWindowHandle), 0);
                Thread.Sleep(500);
                VerifyTools(toolbar);
                if (args.Contains("--keep")) { keepPreview = true; Native.ShowWindow(new IntPtr(settings.Current.NativeWindowHandle), 4); Console.WriteLine("Preview left running; PID=" + _pid); }
                Save("passed"); return 0;
            }
            if (args.Contains("--bottom-review"))
            {
                VerifyBottomReview(settings);
                if (args.Contains("--office-ui")) VerifyOfficeUi(settings);
                if (args.Contains("--keep")) { keepPreview = true; Console.WriteLine("Preview left running; PID=" + _pid); }
                Save("passed"); return 0;
            }
            if (args.Contains("--ui") || args.Contains("--flyout"))
            {
                if (args.Contains("--flyout")) VerifyFlyoutUi(toolbar, "side"); else VerifyToolbarUi(toolbar, "side");
                var starts = settings.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "启动"))
                    .Cast<AutomationElement>().Where(e => !e.Current.IsOffscreen).OrderBy(e => e.Current.BoundingRectangle.Top).ToArray();
                ((InvokePattern)Find(settings, "结束").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                Thread.Sleep(400);
                ((InvokePattern)starts[1].GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                Thread.Sleep(800);
                var center = Wait(() => AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, _pid))
                    .Cast<AutomationElement>().FirstOrDefault(w => w.Current.Name == "RimePPT Toolbar" && w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "批注")) is not null), "bottom center toolbar");
                if (args.Contains("--flyout")) VerifyFlyoutUi(center, "bottom"); else VerifyToolbarUi(center, "bottom");
                if (args.Contains("--keep"))
                {
                    ((InvokePattern)Find(settings, "结束").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    Thread.Sleep(300);
                    ((InvokePattern)starts[0].GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    Thread.Sleep(700);
                    Tap(Find(Wait(() => FindWindow("RimePPT Toolbar"), "preview toolbar"), "批注"));
                    if (args.Contains("--blue"))
            {
                backplate = new System.Windows.Forms.Form { FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                    ShowInTaskbar = false, BackColor = System.Drawing.Color.FromArgb(30,98,128),
                    Bounds = new System.Drawing.Rectangle(0,0,1920,1080), Text = "RimePPT test background" };
                backplate.Show(); backplate.Refresh(); System.Windows.Forms.Application.DoEvents();
            }
                    if (backplate is not null)
                    {
                        Native.ShowWindow(new IntPtr(settings.Current.NativeWindowHandle), 0);
                        Thread.Sleep(400); Capture("arrow");
                        var previewArrow = Wait(() => FindWindow("RimePPT 笔设置箭头"), "preview arrow");
                        Invoke(Find(previewArrow, "打开笔设置")); Thread.Sleep(400); Capture("pen-card");
                        Invoke(Find(previewArrow, "打开笔设置"));
                        Native.ShowWindow(new IntPtr(settings.Current.NativeWindowHandle), 4);
                    }
                    keepPreview = true;
                    Console.WriteLine("Preview left running; PID=" + _pid);
                }
                Save("passed"); Console.WriteLine("PASS compact toolbar / external native arrows / picker positioning"); return 0;
            }
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
            ExpectContacts(before, 2, "two-independent-contacts");
            MultiContacts(cx, cy, 5); MultiContacts(cx, cy, 10);
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
            if (!keepPreview && owned is not null && !owned.HasExited) { owned.Kill(); owned.WaitForExit(3000); }
            owned?.Dispose();
            backplate?.Close(); backplate?.Dispose();
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
    private static void ExpectContacts(int before, int count, string test)
    {
        var watch=Stopwatch.StartNew();while(Ends().Count<before+count&&watch.ElapsedMilliseconds<5000)Thread.Sleep(50);
        var records=Ends().Skip(before).ToList();
        if(records.Count!=count||records.Any(r=>r.GetProperty("dots").GetInt32()==0||r.GetProperty("device").GetString()!="Touch"))throw new Exception(test+": wrong contact count/empty stroke");
        Results.Add(new{test,status="passed",contacts=count});Console.WriteLine("PASS "+test);
    }
    private static void Capture(string suffix)
    {
        using var bitmap = new System.Drawing.Bitmap(1920,1080);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(0,0,0,0,bitmap.Size);
        bitmap.Save(Path.Combine(Path.GetDirectoryName(_output)!, "settings-blue-" + suffix + ".png"));
    }
    private static AutomationElement? FindOwnedNamed(string name)
        => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, _pid), new PropertyCondition(AutomationElement.NameProperty, name)));
    private static void VerifyTimerDrag(AutomationElement toolbar)
    {
        Invoke(Find(toolbar, "工具")); Thread.Sleep(300); Invoke(FindOwnedNamed("计时器")!);
        var timer = Wait(() => FindWindow("RimePPT 计时器"), "timer");
        Thread.Sleep(600);
        Invoke(Find(timer, "开始"));
        var original = timer.Current.BoundingRectangle;
        var heading = Find(timer, "计时器").Current.BoundingRectangle;
        int x = (int)(heading.Left + 20), y = (int)(heading.Top + heading.Height / 2);
        Native.SetCursorPos(x, y); Thread.Sleep(100);
        Native.mouse_event(2, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(1200);
        Assert(timer.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "TimerDisplay")).Current.Name != "00:00", "timer updates while mouse held on heading");
        for (int i = 1; i <= 10; i++) { Native.SetCursorPos(x + i * 8, y + i * 6); Thread.Sleep(35); }
        Native.mouse_event(4, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(150);
        var dropped = timer.Current.BoundingRectangle;
        Results.Add(new { test = "mouse-drag-positions", original = original.ToString(), dropped = dropped.ToString() });
        Assert(Math.Abs(dropped.Left - original.Left - 80) <= 4 && Math.Abs(dropped.Top - original.Top - 60) <= 4, "mouse drag follows pointer without drift");
        Native.SetCursorPos(x + 220, y + 180); Thread.Sleep(350);
        Assert(timer.Current.BoundingRectangle == dropped, "mouse release fixes window without second click");
        heading = Find(timer, "计时器").Current.BoundingRectangle;
        x = (int)(heading.Left + 20); y = (int)(heading.Top + heading.Height / 2);
        Native.Frame(Contact(6, x, y, Native.Down)); Thread.Sleep(50);
        for (int i = 1; i <= 8; i++) { Native.Frame(Contact(6, x - i * 5, y - i * 4, Native.Update)); Thread.Sleep(35); }
        Native.Frame(Contact(6, x - 40, y - 32, Native.Up)); Thread.Sleep(250);
        var touchDropped = timer.Current.BoundingRectangle;
        Assert(Math.Abs(touchDropped.Left - dropped.Left + 40) <= 4 && Math.Abs(touchDropped.Top - dropped.Top + 32) <= 4, "touch drag follows pointer");
        Native.SetCursorPos(400, 200); Thread.Sleep(300);
        Assert(timer.Current.BoundingRectangle == touchDropped, "touch release ends drag");
        Invoke(Find(timer, "暂停")); Capture("timer-drag-fixed");
    }
    private static void VerifyTools(AutomationElement toolbar)
    {
        void Open(string name) { Invoke(Find(toolbar, "工具")); Thread.Sleep(300); Invoke(FindOwnedNamed(name)!); Thread.Sleep(400); }
        string Clock(AutomationElement timer) => timer.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "TimerDisplay")).Current.Name;
        Open("计时器");
        var timer = Wait(() => FindWindow("RimePPT 计时器"), "timer");
        var rect = timer.Current.BoundingRectangle;
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        Assert(Math.Abs(rect.Left + rect.Width / 2 - screen.Width / 2) < 10 && Math.Abs(rect.Top + rect.Height / 2 - screen.Height / 2) < 10, "timer centered");
        Invoke(Find(timer, "开始")); Thread.Sleep(1200);
        Assert(Clock(timer) != "00:00", "stopwatch progresses");
        Invoke(Find(timer, "暂停")); string paused = Clock(timer); Thread.Sleep(1100);
        Assert(Clock(timer) == paused, "stopwatch pause");
        Invoke(Find(timer, "重置")); Assert(Clock(timer) == "00:00", "stopwatch reset");
        var combo = timer.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));
        ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        Thread.Sleep(150);
        ((SelectionItemPattern)FindOwnedNamed("倒计时")!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Thread.Sleep(200);
        Assert(!Find(timer, "开始").Current.IsOffscreen && Find(timer, "开始").Current.BoundingRectangle.Bottom <= timer.Current.BoundingRectangle.Bottom, "countdown buttons fit window");
        var inputs = timer.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().Where(e => !e.Current.IsOffscreen).OrderBy(e => e.Current.BoundingRectangle.Left).ToArray();
        Assert(inputs.Length == 2, "countdown duration inputs");
        ((ValuePattern)inputs[0].GetCurrentPattern(ValuePattern.Pattern)).SetValue("0");
        inputs[0].SetFocus(); System.Windows.Forms.SendKeys.SendWait("{ENTER}");
        ((ValuePattern)inputs[1].GetCurrentPattern(ValuePattern.Pattern)).SetValue("2");
        inputs[1].SetFocus(); System.Windows.Forms.SendKeys.SendWait("{ENTER}");
        Invoke(Find(timer, "开始")); Thread.Sleep(2300);
        Assert(Clock(timer) == "00:00" && Find(timer, "时间到") is not null, "countdown expires");
        Capture("timer-countdown");
        Find(timer, "重新开始").SetFocus();
        System.Windows.Forms.SendKeys.SendWait("{ESC}"); Thread.Sleep(300);
        Assert(FindWindow("RimePPT 计时器") is null, "timer closes");
        Open("计时器"); timer = Wait(() => FindWindow("RimePPT 计时器"), "reopened timer"); Assert(timer is not null, "timer reopens after close");
        Find(timer!, "开始").SetFocus();
        System.Windows.Forms.SendKeys.SendWait("{ESC}"); Thread.Sleep(300);
        using var backplate = new System.Windows.Forms.Form { FormBorderStyle = System.Windows.Forms.FormBorderStyle.None, ShowInTaskbar = false,
            Bounds = screen, BackColor = System.Drawing.Color.FromArgb(30, 98, 128), Text = "RimePPT tool test background" };
        backplate.Show(); backplate.Refresh(); System.Windows.Forms.Application.DoEvents();
        Open("聚光与放大");
        var spot = Wait(() => FindWindow("聚光与放大"), "spotlight");
        Assert(Find(spot, "退出（Esc）").Current.BoundingRectangle.Top > screen.Height / 2, "spotlight controls at bottom");
        System.Drawing.Color CenterPixel()
        {
            using var pixel = new System.Drawing.Bitmap(1, 1);
            using var graphics = System.Drawing.Graphics.FromImage(pixel);
            graphics.CopyFromScreen(screen.Width / 2, screen.Height / 2, 0, 0, new System.Drawing.Size(1, 1));
            return pixel.GetPixel(0, 0);
        }
        var blue = CenterPixel();
        Assert(Math.Abs(blue.B - 128) < 3 && Math.Abs(blue.G - 98) < 3, "spotlight hole transparent");
        backplate.BackColor = System.Drawing.Color.FromArgb(40, 180, 70);
        backplate.Refresh(); System.Windows.Forms.Application.DoEvents(); Thread.Sleep(200);
        var green = CenterPixel();
        Assert(Math.Abs(green.G - 180) < 3 && Math.Abs(green.B - 70) < 3, "spotlight shows live changes");
        Native.Frame(Contact(3, screen.Width / 2, screen.Height / 2, Native.Down)); Thread.Sleep(40);
        Native.Frame(Contact(3, 500, 300, Native.Update)); Thread.Sleep(40);
        Native.Frame(Contact(3, 500, 300, Native.Up)); Thread.Sleep(150);
        var renderWait = Stopwatch.StartNew();
        while (CenterPixel().G >= 100 && renderWait.ElapsedMilliseconds < 5000) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(50); }
        Capture("spotlight-injected-touch");
        Console.WriteLine("Spotlight center after drag: " + CenterPixel());
        Assert(CenterPixel().G < 100, "injected touch moves spotlight away from center");
        Capture("spotlight-bottom-controls");
        Tap(Find(spot, "框选放大"));
        Wait(() => spot.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "刷新画面")) is { } refresh && refresh.Current.IsEnabled ? refresh : null, "magnification capture completion");
        Assert(Find(spot, "刷新画面").Current.IsEnabled, "magnification capture ready");
        var inputTitle = new StringBuilder(128);
        Native.GetWindowText(Native.GetAncestor(Native.WindowFromPoint(new() { X = 55, Y = 360 }), 2), inputTitle, inputTitle.Capacity);
        Console.WriteLine("Selection input target: " + inputTitle);
        Native.Frame(Contact(4, 55, 360, Native.Down)); Thread.Sleep(50);
        Native.Frame(Contact(4, 75, 450, Native.Update)); Thread.Sleep(50);
        Native.Frame(Contact(4, 75, 450, Native.Up)); Thread.Sleep(200);
        renderWait.Restart();
        while (Math.Abs(CenterPixel().G - 180) >= 3 && renderWait.ElapsedMilliseconds < 5000) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(50); }
        Capture("magnification-selected-result");
        Console.WriteLine("Magnified selected center: " + CenterPixel());
        Assert(Math.Abs(CenterPixel().G - 180) < 3, "magnification excludes own toolbar and draws selected region");
        Capture("magnification-selection");
        Invoke(Find(spot, "重新框选")); Invoke(Find(spot, "退出（Esc）")); Thread.Sleep(300);
        Open("黑屏模式"); Assert(FindWindow("RimePPT 黑屏") is not null, "blackout opens");
        System.Windows.Forms.SendKeys.SendWait("{ESC}"); Thread.Sleep(300);
        Assert(FindWindow("RimePPT 黑屏") is null, "blackout Escape exits");
        Open("黑屏模式");
        Native.Frame(Contact(5, screen.Width / 2, screen.Height / 2, Native.Down)); Thread.Sleep(40);
        Native.Frame(Contact(5, screen.Width / 2, screen.Height / 2, Native.Up)); Thread.Sleep(300);
        Assert(FindWindow("RimePPT 黑屏") is null, "blackout injected touch exits");
        Results.Add(new { test = "tools-rewrite", passed = true });
    }
    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Results.Add(new { test = label, passed = true });
    }
    private static void WaitForToolbarPlacement(AutomationElement toolbar)
    {
        var watch = Stopwatch.StartNew();
        var last = System.Windows.Rect.Empty;
        long stableSince = 0;
        while (watch.ElapsedMilliseconds < 30000)
        {
            var rect = toolbar.Current.BoundingRectangle;
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            if (rect != last) { last = rect; stableSince = watch.ElapsedMilliseconds; }
            if (rect.Left >= 0 && rect.Top >= 0 && rect.Right <= screen.Right && rect.Bottom <= screen.Bottom && rect.Width > 20 && watch.ElapsedMilliseconds - stableSince > 400) return;
            Thread.Sleep(50);
        }
        throw new Exception("Toolbar did not finish entrance placement");
    }
    private static void Invoke(AutomationElement element) => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    private static void WaitGone(string name)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
        { var e = FindOwnedNamed(name); if (e is null || e.Current.IsOffscreen) return; Thread.Sleep(100); }
        throw new Exception("Flyout did not dismiss: " + name);
    }
    private static void VerifyPrompt(bool load, bool delete)
    {
        string title = load ? "打开已保存的墨迹" : "保存墨迹";
        Wait(() => FindOwnedNamed(title), "native ContentDialog");
        Thread.Sleep(700); Capture(load ? "load-dialog" : "save-dialog");
        var button = Wait(() => FindOwnedNamed(delete ? "删除墨迹" : load ? "不打开" : "保存"), "dialog response");
        Invoke(button);
        WaitGone(title);
        string expected = delete ? "Delete" : load ? "Secondary" : "Primary";
        var watch = Stopwatch.StartNew();
        var log = Path.Combine(Path.GetTempPath(), "rimeppt_app.log");
        while (!File.ReadAllText(log).Contains($"[pid{_pid}] prompt preview result: {expected}"))
        { if (watch.ElapsedMilliseconds > 5000) throw new Exception("Dialog response mapping failed"); Thread.Sleep(100); }
        Results.Add(new { test = "native-dialog-" + (load ? "load" : "save"), response = expected, status = "passed" });
    }
    private static AutomationElement[] ToolbarWindows() => AutomationElement.RootElement.FindAll(TreeScope.Children,
        new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, _pid), new PropertyCondition(AutomationElement.NameProperty, "RimePPT Toolbar")))
        .Cast<AutomationElement>().ToArray();
    private static void VerifyBottomReview(AutomationElement settings)
    {
        var starts = settings.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty,"启动"))
            .Cast<AutomationElement>().Where(e => !e.Current.IsOffscreen).OrderBy(e => e.Current.BoundingRectangle.Top).ToArray();
        Invoke(Find(settings,"结束")); Thread.Sleep(600); Invoke(starts[1]); Thread.Sleep(1000);
        var center = ToolbarWindows().Single(w => w.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"工具")) is not null);
        Native.ShowWindow(new IntPtr(settings.Current.NativeWindowHandle),0);
        var undo = Find(center,"撤销"); var redo = Find(center,"重做");
        if (undo.Current.IsEnabled || redo.Current.IsEnabled) throw new Exception("Empty history should disable undo/redo");
        Invoke(Find(center,"工具"));
        var spotlight = Wait(() => FindOwnedNamed("聚光与放大"), "tools flyout");
        var menuSettings = Wait(() => FindOwnedNamed("设置"), "tools settings");
        if (menuSettings.Current.BoundingRectangle.Bottom > center.Current.BoundingRectangle.Top + 1) throw new Exception("Bottom menu did not open above toolbar");
        if (FindOwnedNamed("清除当前页墨迹") is not null || FindOwnedNamed("页面导航") is { Current.ControlType: var ct } && ct != ControlType.Button) throw new Exception("Duplicate tools item remained");
        Capture("bottom-tools");
        Native.SetCursorPos(300,200); Thread.Sleep(150); Native.mouse_event(2,0,0,0,UIntPtr.Zero); Thread.Sleep(100); Native.mouse_event(4,0,0,0,UIntPtr.Zero);
        WaitGone("聚光与放大");
        var rails = ToolbarWindows().Where(w => w.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"页面导航")) is not null)
            .OrderBy(w => w.Current.BoundingRectangle.Left).ToArray();
        if (rails.Length != 2) throw new Exception("Page count missing from left/right bottom bars");
        foreach (var rail in rails)
        {
            var counter = Find(rail,"页面导航");
            var r = counter.Current.BoundingRectangle;
            if (!(Find(rail,"上一页").Current.BoundingRectangle.Right <= r.Left && r.Right <= Find(rail,"下一页").Current.BoundingRectangle.Left)) throw new Exception("Counter is not between arrows");
            Invoke(counter); var page = Wait(() => FindOwnedNamed("第 2 页"), "thumbnail page");
            Thread.Sleep(500);
            var p = page.Current.BoundingRectangle;
            Capture("compact-placement-check");
            if (p.Bottom > rail.Current.BoundingRectangle.Top || p.Left < 0 || p.Right > 1920) throw new Exception("Navigation popup positioning failed: page="+p+" toolbar="+rail.Current.BoundingRectangle);
            var list=AutomationElement.RootElement.FindFirst(TreeScope.Descendants,new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty,_pid),new PropertyCondition(AutomationElement.AutomationIdProperty,"Pages")));
            var visual=TreeWalker.RawViewWalker.GetParent(list);
            AutomationElement? presenter=null;
            while(visual is not null)
            {
                Console.WriteLine("NAV ancestor "+visual.Current.ClassName+" "+visual.Current.BoundingRectangle);
                if(visual.Current.ClassName is "FlyoutPresenter" or "Flyout") { presenter=visual; break; }
                visual=TreeWalker.RawViewWalker.GetParent(visual);
            }
            if(presenter is null || Math.Abs(presenter.Current.BoundingRectangle.Width-rail.Current.BoundingRectangle.Width)>6) throw new Exception("Visible preview width differs from toolbar");
            var first=FindOwnedNamed("第 1 页 · 当前页")!.Current.BoundingRectangle;
            if(p.Top<=first.Bottom || Math.Abs(p.Left-first.Left)>3) throw new Exception("Preview is not a single vertical column");
            Capture(rail == rails[0] ? "navigation-left" : "navigation-right");
            if(rail==rails[0])
            {
                var scroll=(ScrollPattern)list.GetCurrentPattern(ScrollPattern.Pattern);
                if(!scroll.Current.VerticallyScrollable) throw new Exception("Compact list cannot scroll");
                scroll.SetScrollPercent(ScrollPattern.NoScroll,100);
                var last=Wait(()=>FindOwnedNamed("第 5 页") is { } e && !e.Current.IsOffscreen ? e : null,"last page after scrolling");
                Thread.Sleep(400); MouseTap(last); WaitGone("第 5 页");
                Wait(()=>rail.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"5 / 5")),"last-page count");
                for(int i=0;i<4;i++) { Invoke(Find(rail,"上一页")); Thread.Sleep(150); }
                Invoke(counter); page=Wait(()=>FindOwnedNamed("第 2 页"),"reopened preview");
            }
            Thread.Sleep(400); MouseTap(page); WaitGone("第 2 页");
            Wait(() => rail.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"2 / 5")), "updated page count");
            Invoke(Find(rail,"上一页")); Thread.Sleep(200);
        }
        MouseTap(Find(center,"批注")); Thread.Sleep(300);
        Native.SetCursorPos(650,400); Thread.Sleep(150); Native.mouse_event(2,0,0,0,UIntPtr.Zero);
        for (int i=0;i<12;i++) { Native.SetCursorPos(650+i*12,400+i*3); Thread.Sleep(15); }
        Native.mouse_event(4,0,0,0,UIntPtr.Zero);
        Wait(() => undo.Current.IsEnabled ? undo : null,"undo after stroke");
        Invoke(undo); Wait(() => redo.Current.IsEnabled ? redo : null,"redo after undo");
        Invoke(redo); Wait(() => undo.Current.IsEnabled ? undo : null,"undo after redo");
        Thread.Sleep(400); Capture("bottom-history");
        Results.Add(new { test="bottom-menu-navigation-history", status="passed", input="UIA commands; injected mouse page selection and ink" });
        Native.ShowWindow(new IntPtr(settings.Current.NativeWindowHandle),4);
        var centerHandle=new IntPtr(center.Current.NativeWindowHandle);
        var tops=new List<int>(); Native.GetWindowRect(centerHandle,out var initial); tops.Add(initial.Top);
        Invoke(Find(settings,"结束")); var exitWatch=Stopwatch.StartNew();
        while(exitWatch.ElapsedMilliseconds<2000 && Native.GetWindowRect(centerHandle,out var frame)) { tops.Add(frame.Top); Thread.Sleep(4); }
        if(Native.GetWindowRect(centerHandle,out _)) throw new Exception("Toolbar did not close after exit animation");
        Native.SystemParametersInfo(0x1042,0,out bool animations,0);
        if(animations && tops.Distinct().Count()<2) throw new Exception("Toolbar disappeared without observed exit motion");
        Results.Add(new { test="toolbar-exit-motion",status="passed",animationsEnabled=animations,positions=tops.Distinct().ToArray() });
        Invoke(starts[1]); Thread.Sleep(1000);
    }
    private static void VerifyOfficeUi(AutomationElement settings)
    {
        bool existingOffice = Process.GetProcessesByName("POWERPNT").Length != 0;
        Invoke(Find(settings,"结束")); Thread.Sleep(700);
        dynamic? office = null, deck = null, show = null;
        string directory = Path.Combine(Path.GetTempPath(),"RimePPT.UIProbe",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            office = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!; office.Visible = -1;
            deck = office.Presentations.Add(-1);
            for(int i=1;i<=3;i++) { dynamic slide=deck.Slides.Add(i,12); slide.Shapes.AddTextbox(1,70,100,500,100).TextFrame.TextRange.Text="RimePPT preview page " + i; }
            deck.SaveAs(Path.Combine(directory,"ui-preview.pptx"),24); show=deck.SlideShowSettings.Run();
            Thread.Sleep(2000);
            var center=Wait(() => ToolbarWindows().FirstOrDefault(w => w.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"撤销")) is not null),"Office bottom toolbar");
            Native.ShowWindow(new IntPtr(settings.Current.NativeWindowHandle),0);
            var left=ToolbarWindows().Where(w => w.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"页面导航")) is not null).OrderBy(w=>w.Current.BoundingRectangle.Left).First();
            Invoke(Find(left,"页面导航"));
            Wait(() => FindOwnedNamed("第 2 页"),"real page preview");
            Thread.Sleep(1500); Capture("office-thumbnails");
            if (FindOwnedNamed("缩略图加载失败；点击仍可跳页。") is not null) throw new Exception("Office thumbnail failed to load");
            MouseTap(FindOwnedNamed("第 2 页")!); WaitGone("第 2 页");
            Wait(() => left.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"2 / 3")),"real page number");
            if ((int)show.View.Slide.SlideIndex!=2) throw new Exception("Office did not jump to page 2");
            MouseTap(Find(center,"批注")); Thread.Sleep(400);
            Native.SetCursorPos(600,420); Thread.Sleep(150); Native.mouse_event(2,0,0,0,UIntPtr.Zero);
            for (int i=0;i<12;i++) { Native.SetCursorPos(600+i*12,420+i*3); Thread.Sleep(15); }
            Native.mouse_event(4,0,0,0,UIntPtr.Zero);
            var undo=Find(center,"撤销"); var redo=Find(center,"重做");
            Wait(() => undo.Current.IsEnabled ? undo : null,"custom history before COM switch");
            var arrow=Wait(()=>FindWindow("RimePPT 笔设置箭头"),"real pen settings arrow");
            Invoke(Find(arrow,"打开笔设置"));
            var custom=Wait(()=>FindOwnedNamed("自定义颜色"),"real pen flyout");
            var combo=AutomationElement.RootElement.FindAll(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,_pid),new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ComboBox)))
                .Cast<AutomationElement>().First(e=>!e.Current.IsOffscreen);
            ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            var native=Wait(()=>FindOwnedNamed("PowerPoint 原生（COM）"),"COM mode option");
            ((SelectionItemPattern)native.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            var clock=Stopwatch.StartNew();
            while(undo.Current.IsEnabled || redo.Current.IsEnabled) { if(clock.ElapsedMilliseconds>5000) throw new Exception("COM undo/redo did not disable"); Thread.Sleep(100); }
            Thread.Sleep(500); Capture("office-com-disabled");
            // COM activation can light-dismiss the settings popup; reopen it before restoring.
            if (FindOwnedNamed("自定义颜色") is null) Invoke(Find(arrow,"打开笔设置"));
            Wait(()=>FindOwnedNamed("自定义颜色"),"pen settings after COM switch");
            combo=AutomationElement.RootElement.FindAll(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,_pid),new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ComboBox)))
                .Cast<AutomationElement>().First(e=>!e.Current.IsOffscreen);
            // Restore the user's custom backend before exiting the owned deck.
            ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            var rime=Wait(()=>FindOwnedNamed("RimePPT 自研"),"custom mode option");
            ((SelectionItemPattern)rime.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Wait(()=>undo.Current.IsEnabled ? undo : null,"custom history after mode restore");
            Invoke(Find(arrow,"打开笔设置"));
            Results.Add(new { test="real-office-navigation-and-com-disabled",status="passed" });
        }
        finally
        {
            try { if(show is not null) show.View.Exit(); } catch { }
            // The saved-ink question is genuine; choose the safe no-save response for this throwaway deck.
            var promptWait=Stopwatch.StartNew(); AutomationElement? noSave=null;
            while (promptWait.ElapsedMilliseconds<5000 && (noSave=FindOwnedNamed("不保存")) is null) Thread.Sleep(100);
            if(noSave is not null) { Invoke(noSave); WaitGone("不保存"); }
            try { if(deck is not null) { deck.Saved=-1; deck.Close(); } } catch { }
            try { if(office is not null && !existingOffice) office.Quit(); } catch { }
            if(show is not null) Marshal.FinalReleaseComObject(show);
            if(deck is not null) Marshal.FinalReleaseComObject(deck);
            if(office is not null) Marshal.FinalReleaseComObject(office);
            // Retain the throwaway deck as diagnostic evidence; existing user presentations stay open.
        }
    }
    private static void VerifyFlyoutUi(AutomationElement toolbar, string layout)
    {
        Thread.Sleep(600); Tap(Find(toolbar, "批注"));
        var arrow = Wait(() => FindWindow("RimePPT 笔设置箭头"), "pen arrow");
        var button = Find(arrow, "打开笔设置"); Invoke(button);
        var custom = Wait(() => FindOwnedNamed("自定义颜色"), "compact pen flyout");
        if (FindOwnedNamed("应用颜色") is not null || FindOwnedNamed("应用尺寸") is not null) throw new Exception("Apply button remained");
        if (FindOwnedNamed("选择笔颜色") is not null) throw new Exception("Colour picker is expanded before circle click");
        var width = Wait(() => FindOwnedNamed("笔粗细"), "pen slider");
        var pattern = (RangeValuePattern)width.GetCurrentPattern(RangeValuePattern.Pattern);
        double old = pattern.Current.Value; double value = old == 9 ? 8 : 9;
        pattern.SetValue(value);
        Wait(() => FindOwnedNamed($"{value:0} DIP"), "immediate pen preview");
        Thread.Sleep(400);
        var settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimePPT", "settings.json");
        string oldPenName;
        string oldColorHex;
        using (var snapshot = JsonDocument.Parse(File.ReadAllText(settingsFile)))
        {
            oldPenName = snapshot.RootElement.GetProperty("PenColor").GetString()!;
            oldColorHex = snapshot.RootElement.TryGetProperty("CustomPenArgb", out var customHex) ? customHex.GetString() ?? "FFE81123" : "FFE81123";
        }
        using (var json = JsonDocument.Parse(File.ReadAllText(settingsFile)))
            if (json.RootElement.GetProperty("PenThickness").GetDouble() != value) throw new Exception("Pen width did not persist without apply");
        pattern.SetValue(old);
        Invoke(custom);
        var picker = Wait(() => FindOwnedNamed("选择笔颜色"), "native colour picker in second flyout");
        var visibleEdits = picker.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().Where(e => !e.Current.IsOffscreen).ToArray();
        Assert(visibleEdits.Length == 1 && visibleEdits[0].Current.AutomationId == "HexTextBox", "native picker keeps only hex input");
        Assert(picker.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)).Cast<AutomationElement>().All(e => e.Current.IsOffscreen), "native picker hides RGB HSV selector");
        Console.WriteLine("Colour picker edits: " + string.Join(" | ", picker.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
            .Cast<AutomationElement>().Select(e => e.Current.Name + ":" + e.Current.AutomationId)));
        var hex = picker.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "HexTextBox"));
        hex.SetFocus(); ((ValuePattern)hex.GetCurrentPattern(ValuePattern.Pattern)).SetValue("#AA44CC");
        Native.keybd_event(0x0D,0,0,UIntPtr.Zero); Native.keybd_event(0x0D,0,2,UIntPtr.Zero); Thread.Sleep(500);
        var colorWait = Stopwatch.StartNew();
        while (true)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(settingsFile));
            if (json.RootElement.GetProperty("CustomPenArgb").GetString() == "FFAA44CC") break;
            if (colorWait.ElapsedMilliseconds > 5000) throw new Exception("Color did not apply immediately");
            Thread.Sleep(100);
        }
        {
            ((ValuePattern)hex.GetCurrentPattern(ValuePattern.Pattern)).SetValue("#" + oldColorHex[2..]);
            Native.keybd_event(0x0D,0,0,UIntPtr.Zero); Native.keybd_event(0x0D,0,2,UIntPtr.Zero); Thread.Sleep(300);
        }
        Native.keybd_event(0x1B,0,0,UIntPtr.Zero); Native.keybd_event(0x1B,0,2,UIntPtr.Zero); Thread.Sleep(400);
        // Close the parent via its arrow, then verify open/close toggling independently.
        if (FindOwnedNamed("自定义颜色") is { } current && !current.Current.IsOffscreen) Invoke(button);
        WaitGone("自定义颜色"); Console.WriteLine("PASS nested picker close"); Invoke(button); Wait(() => FindOwnedNamed("自定义颜色"), "reopen pen flyout");
        if (oldPenName != "custom") Invoke(FindOwnedNamed(oldPenName)!);
        Invoke(button); WaitGone("自定义颜色");
        Console.WriteLine("PASS arrow toggle");
        MouseTap(button); Wait(() => FindOwnedNamed("自定义颜色"), "mouse arrow open");
        MouseTap(button); WaitGone("自定义颜色");
        Console.WriteLine("PASS physical mouse arrow toggle");
        Invoke(button); Wait(() => FindOwnedNamed("自定义颜色"), "open before outside dismiss");
        Native.SetCursorPos(300, 200); Thread.Sleep(150); Native.mouse_event(2,0,0,0,UIntPtr.Zero); Thread.Sleep(100); Native.mouse_event(4,0,0,0,UIntPtr.Zero);
        WaitGone("自定义颜色");
        Console.WriteLine("PASS outside dismiss");
        Thread.Sleep(600);
        Tap(Find(toolbar, "橡皮"));
        var eraser = Wait(() => FindWindow("RimePPT 橡皮设置箭头"), "eraser arrow"); Invoke(Find(eraser, "打开橡皮设置"));
        var eraserWidth = Wait(() => FindOwnedNamed("橡皮宽度"), "eraser flyout");
        pattern = (RangeValuePattern)eraserWidth.GetCurrentPattern(RangeValuePattern.Pattern); old = pattern.Current.Value;
        value = old == 80 ? 72 : 80; pattern.SetValue(value); Thread.Sleep(400);
        using (var json = JsonDocument.Parse(File.ReadAllText(settingsFile)))
            if (json.RootElement.GetProperty("EraserWidthDip").GetDouble() != value) throw new Exception("Eraser size did not persist without apply");
        pattern.SetValue(old); Invoke(Find(eraser, "打开橡皮设置")); WaitGone("橡皮宽度");
        Tap(Find(toolbar, "橡皮"));
        Results.Add(new { test = "flyout-" + layout, status = "passed", input = "mouse toolbar/arrow toggle/outside; UIA colour/slider", immediatePen = true, immediateEraser = true, nestedColour = true });
        Console.WriteLine("PASS flyout " + layout);
    }
    private static void VerifyToolbarUi(AutomationElement toolbar, string layout)
    {
        Thread.Sleep(600);
        var pen = Find(toolbar, "批注"); var bounds = pen.Current.BoundingRectangle;
        if (bounds.Width > 90 || bounds.Height > 90) throw new Exception("Toolbar button is oversized: " + bounds);
        Tap(pen);
        var arrow = Wait(() => FindWindow("RimePPT 笔设置箭头"), "external pen arrow");
        var a = arrow.Current.BoundingRectangle; var t = toolbar.Current.BoundingRectangle;
        if (a.IntersectsWith(t)) throw new Exception("Arrow overlaps toolbar");
        var arrows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, _pid), new PropertyCondition(AutomationElement.NameProperty, "RimePPT 笔设置箭头")))
            .Cast<AutomationElement>().ToArray();
        var screen = FindWindow("RimePPT Annotation")!.Current.BoundingRectangle;
        foreach (var w in arrows)
        {
            string expected = layout == "bottom" ? "向上" : w.Current.BoundingRectangle.Left < screen.Left + screen.Width / 2 ? "向右" : "向左";
            if (Find(w, "打开笔设置").Current.HelpText != expected) throw new Exception("Arrow direction mismatch");
        }
        Tap(Find(arrow, "打开笔设置"));
        var picker = Wait(() => FindWindow("笔设置"), "pen picker");
        if (picker.Current.BoundingRectangle.Width < 200 || picker.Current.IsOffscreen) throw new Exception("Picker placement invalid");
        ((InvokePattern)Find(picker, "完成").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Tap(Find(toolbar, "橡皮"));
        var eraser = Wait(() => FindWindow("RimePPT 橡皮设置箭头"), "external eraser arrow");
        if (FindWindow("RimePPT 笔设置箭头") is not null) throw new Exception("Pen arrow remained in eraser mode");
        Tap(Find(eraser, "打开橡皮设置"));
        picker = Wait(() => FindWindow("橡皮设置"), "eraser picker");
        ((InvokePattern)Find(picker, "完成").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Tap(Find(toolbar, "橡皮")); Thread.Sleep(200);
        if (FindWindow("RimePPT 橡皮设置箭头") is not null) throw new Exception("Arrow remained after deselection");
        Results.Add(new { test = "toolbar-ui-" + layout, status = "passed", input = Environment.GetEnvironmentVariable("RIMEPPT_PROBE_ARROW_INVOKE") == "1" ? "mouse toolbar; UIA invoke arrow" : Environment.GetEnvironmentVariable("RIMEPPT_PROBE_MOUSE") == "1" ? "injected mouse" : "injected touch", penWidthPixels = bounds.Width, penHeightPixels = bounds.Height, arrowBounds = a.ToString(), directionsVerified = arrows.Length });
    }
    private static void MultiContacts(int cx,int cy,int count)
    {
        int before=Ends().Count;
        Native.Frame(Enumerable.Range(1,count).Select(i=>Contact((uint)i,cx-150+i*25,cy+160,Native.Down)).ToArray());Thread.Sleep(15);
        for(int step=1;step<=8;step++){Native.Frame(Enumerable.Range(1,count).Select(i=>Contact((uint)i,cx-150+i*25+step*4,cy+160+step*4,Native.Update)).ToArray());Thread.Sleep(10);}
        Native.Frame(Enumerable.Range(1,count).Select(i=>Contact((uint)i,cx-150+i*25+32,cy+192,Native.Up)).ToArray());
        ExpectContacts(before,count,$"simultaneous-{count}-contacts");
    }
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
        while (watch.ElapsedMilliseconds < 30000) { if (find() is { } element) return element; Thread.Sleep(100); }
        throw new Exception("Timeout waiting for " + description);
    }
    private static void Tap(AutomationElement element)
    {
        if (Environment.GetEnvironmentVariable("RIMEPPT_PROBE_ARROW_INVOKE") == "1" && element.Current.Name is "打开笔设置" or "打开橡皮设置")
        { ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); Thread.Sleep(250); return; }
        if (Environment.GetEnvironmentVariable("RIMEPPT_PROBE_MOUSE") == "1") { MouseTap(element); Thread.Sleep(250); return; }
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty || rect.Width <= 0 || element.Current.IsOffscreen) throw new Exception("Element not visible: " + element.Current.Name);
        int x = (int)(rect.Left + ((element.Current.Name is "批注" or "橡皮") ? 20 : rect.Width / 2)), y = (int)(rect.Top + rect.Height / 2);
        IntPtr target = Native.GetAncestor(Native.WindowFromPoint(new() { X = x, Y = y }), 2);
        var title = new StringBuilder(128); Native.GetWindowText(target, title, title.Capacity);
        Console.WriteLine($"TAP {element.Current.Name} at {x},{y}; window={title}");
        uint id = _tapId = (_tapId + 1) % 10;
        Native.Frame(Contact(id, x, y, Native.Down)); Thread.Sleep(40);
        Native.Frame(Contact(id, x, y, Native.Update)); Thread.Sleep(10);
        Native.Frame(Contact(id, x, y, Native.Up)); Thread.Sleep(150);
    }
    private static void MouseTap(AutomationElement element)
    {
        var r = element.Current.BoundingRectangle;
        int x = (int)(r.Left + r.Width / 2), y = (int)(r.Top + r.Height / 2);
        var title = new StringBuilder(128);
        Native.GetWindowText(Native.GetAncestor(Native.WindowFromPoint(new() { X=x, Y=y }), 2), title, title.Capacity);
        Console.WriteLine("MOUSE " + element.Current.Name + " bounds=" + r + " offscreen=" + element.Current.IsOffscreen + " target=" + title);
        Native.SetCursorPos((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        Thread.Sleep(150);
        Native.mouse_event(2,0,0,0,UIntPtr.Zero); Thread.Sleep(100); Native.mouse_event(4,0,0,0,UIntPtr.Zero);
        Thread.Sleep(150);
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
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
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
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW")] public static extern bool SystemParametersInfo(uint action,uint param,[MarshalAs(UnmanagedType.Bool)] out bool value,uint flags);
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
