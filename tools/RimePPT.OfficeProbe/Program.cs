using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RimePPT.Core;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var results = new List<object>(); dynamic? app = null; dynamic? presentation = null; dynamic? show = null;
        string directory = Path.Combine(Path.GetTempPath(), "RimePPT.OfficeProbe", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("office-probe-results.json");
        try
        {
            if (Process.GetProcessesByName("POWERPNT").Length > 0) throw new InvalidOperationException("Close existing PowerPoint before this probe. It only operates its own application and document.");
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            var type = Type.GetTypeFromProgID("PowerPoint.Application") ?? throw new NotSupportedException("PowerPoint is not installed.");
            app = Activator.CreateInstance(type)!; app.Visible = -1;
            presentation = app.Presentations.Add(-1);
            for (int i = 1; i <= 3; i++)
            {
                dynamic slide = presentation.Slides.Add(i, 12);
                slide.FollowMasterBackground = 0; slide.Background.Fill.ForeColor.RGB = 0xFFFFFF;
                dynamic shape = slide.Shapes.AddShape(1, 10, 10, 40, 40); shape.Fill.ForeColor.RGB = i * 0x220000;
                // Pre-existing content must survive native clear.
                slide.Shapes.AddLine(30, 50, 100, 50);
            }
            string path = Path.Combine(directory, "probe.pptx"); presentation.SaveAs(path, 24);
            show = presentation.SlideShowSettings.Run(); Thread.Sleep(800);
            using var controller = new ControllerLifetime(); controller.Controller.Start();
            var watch = Stopwatch.StartNew(); while (!controller.Controller.IsPresenting && watch.ElapsedMilliseconds < 8000) Thread.Sleep(100);
            Require(controller.Controller.IsPresenting, "controller did not attach");
            Require(Path.GetFullPath(controller.Controller.ShowFilePath!) == path, "attached wrong presentation");
            controller.Controller.SetNativePointerAsync(NativePointerTool.Pen, new byte[] { 255, 255, 0, 0 }).GetAwaiter().GetResult();
            Require((int)show.View.PointerType == 2 && (int)show.View.PointerColor.RGB == 255, "pen/colour readback");
            var initialClean = Capture(controller.Controller.ShowWindowHandle);
            show.Activate(); DrawNativeInk(show.View, controller.Controller.ShowWindowHandle, false);
            Console.WriteLine("Native drawing pixel changes=" + Difference(initialClean, Capture(controller.Controller.ShowWindowHandle)));
            try { controller.Controller.SetNativePointerAsync(NativePointerTool.Eraser, null).GetAwaiter().GetResult(); Require((int)show.View.PointerType == 5, "eraser readback"); }
            catch (Exception ex) { results.Add(new { test = "native-eraser", status = "failed", error = ex.Message, pointerReadback = (int)show.View.PointerType }); }
            controller.Controller.SetNativePointerAsync(NativePointerTool.Arrow, null).GetAwaiter().GetResult();
            show.View.EraseDrawing();
            results.Add(new { test = "native-pen-colour-arrow", status = "passed" });
            controller.Controller.GoToSlideAsync(2).GetAwaiter().GetResult(); Require((int)show.View.Slide.SlideIndex == 2, "goto");
            string thumb = controller.Controller.ExportSlideThumbnailAsync(2, Path.Combine(directory, "2.png"), 240, CancellationToken.None).GetAwaiter().GetResult();
            Require(File.Exists(thumb) && new FileInfo(thumb).Length > 100, "thumbnail missing"); results.Add(new { test = "goto-and-thumbnail", status = "passed" });
            show.View.GotoSlide(1); Thread.Sleep(400); var clean = Capture(controller.Controller.ShowWindowHandle);
            show.Activate(); show.View.PointerColor.RGB = 255; DrawNativeInk(show.View, controller.Controller.ShowWindowHandle, false); Thread.Sleep(300); var first = Capture(controller.Controller.ShowWindowHandle);
            Require(Difference(clean, first) > 0, "native ink was not visible");
            show.View.GotoSlide(2); show.Activate(); DrawNativeInk(show.View, controller.Controller.ShowWindowHandle, true); Thread.Sleep(300); var second = Capture(controller.Controller.ShowWindowHandle);
            show.View.GotoSlide(1); Thread.Sleep(300); show.View.EraseDrawing(); Thread.Sleep(300); var cleared = Capture(controller.Controller.ShowWindowHandle);
            show.View.GotoSlide(2); Thread.Sleep(300); var retained = Capture(controller.Controller.ShowWindowHandle);
            Require(Difference(clean, cleared) == 0, "clear did not restore current page");
            Require(Difference(second, retained) == 0, "clear affected another page");
            Require((int)presentation.Slides(1).Shapes.Count == 2 && (int)presentation.Slides(2).Shapes.Count == 2, "original shapes changed");
            results.Add(new { test = "native-clear-current-page-preserves-other-page-and-shapes", status = "passed", input = "native pen with injected mouse; physical touch not tested" });
            var stamp = new { version = (string)app.Version + "|" + Convert.ToString(app.Build), scope = "current-page", verifiedAtUtc = DateTime.UtcNow };
            string stampDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimePPT"); Directory.CreateDirectory(stampDirectory);
            File.WriteAllText(Path.Combine(stampDirectory, "native-clear-validation.json"), JsonSerializer.Serialize(stamp));
            File.WriteAllText(output, JsonSerializer.Serialize(new { status = "passed", results, officeVersion = (string)app.Version }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS Office COM capabilities and current-page clear; physical touch remains unverified."); return 0;
        }
        catch (Exception ex) { File.WriteAllText(output, JsonSerializer.Serialize(new { status = "failed", results, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine(ex.ToString()); return 1; }
        finally
        {
            try { if (show is not null) show.View.Exit(); } catch { }
            try { if (presentation is not null) { presentation.Saved = -1; presentation.Close(); } } catch { }
            try { if (app is not null) app.Quit(); } catch { }
            if (show is not null) Marshal.FinalReleaseComObject(show);
            if (presentation is not null) Marshal.FinalReleaseComObject(presentation);
            if (app is not null) Marshal.FinalReleaseComObject(app);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
    private sealed class ControllerLifetime : IDisposable { public PowerPointController Controller { get; } = new(); public void Dispose() { /* Background monitor ends with this probe process. */ } }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void DrawNativeInk(dynamic view, IntPtr window, bool reverse)
    {
        Require(window != IntPtr.Zero, "missing slideshow HWND"); SetForegroundWindow(window); view.PointerType = 2;
        GetWindowRect(window, out var rect);
        int x = rect.Left + (rect.Right - rect.Left) / 3, y = rect.Top + (rect.Bottom - rect.Top) / 3;
        SetCursorPos(x,y); Thread.Sleep(100); mouse_event(2,0,0,0,UIntPtr.Zero);
        for(int i=1;i<=30;i++) { SetCursorPos(x+i*8,y+(reverse?-1:1)*i*5); Thread.Sleep(8); }
        mouse_event(4,0,0,0,UIntPtr.Zero); SetCursorPos(rect.Left+5,rect.Top+5); Thread.Sleep(150);
        view.PointerType = 1;
    }
    private static int[] Capture(IntPtr window)
    {
        GetWindowRect(window, out var rect); IntPtr dc = GetDC(IntPtr.Zero);
        int width=rect.Right-rect.Left-300, height=rect.Bottom-rect.Top-160;
        Require(width>0 && height>0,"invalid slideshow capture bounds");
        IntPtr memory=CreateCompatibleDC(dc);
        var info=new BitmapInfo { Size=40, Width=width, Height=-height, Planes=1, Bits=32 };
        IntPtr bitmap=CreateDIBSection(dc,ref info,0,out var pixels,IntPtr.Zero,0);
        Require(bitmap!=IntPtr.Zero,"CreateDIBSection failed");
        IntPtr old=SelectObject(memory,bitmap);
        try
        {
            Require(BitBlt(memory,0,0,width,height,dc,rect.Left+150,rect.Top+80,0x00CC0020),"BitBlt failed");
            var values=new int[width*height];Marshal.Copy(pixels,values,0,values.Length);return values;
        }
        finally { SelectObject(memory,old);DeleteObject(bitmap);DeleteDC(memory);ReleaseDC(IntPtr.Zero, dc); }
    }
    private static int Difference(int[] a, int[] b) { Require(a.Length == b.Length, "capture size changed"); int count = 0; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) count++; return count; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width,Height; public ushort Planes,Bits; public uint Compression,SizeImage; public int XPels,YPels; public uint Colors,Important; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr pixels,IntPtr section,uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dc,int x,int y,int width,int height,IntPtr source,int sx,int sy,uint op);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
}
