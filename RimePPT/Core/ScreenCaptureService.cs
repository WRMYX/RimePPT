using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace RimePPT.Core;

public static class ScreenCaptureService
{
    public static byte[] Capture(RectInt32 bounds)
    {
        var hidden = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint process);
            if (process == Environment.ProcessId && IsWindowVisible(window)) { hidden.Add(window); ShowWindow(window, 0); }
            return true;
        }, IntPtr.Zero);
        try
        {
            DwmFlush();
            double ratio = Math.Min(1, Math.Sqrt((64.0 * 1024 * 1024 - 128 * 1024) / (bounds.Width * (double)bounds.Height * 4)));
            int w = Math.Max(1, (int)(bounds.Width * ratio)), h = Math.Max(1, (int)(bounds.Height * ratio));
            using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            IntPtr screen = GetDC(IntPtr.Zero), target = graphics.GetHdc();
            try
            {
                SetStretchBltMode(target, 4);
                if (!StretchBlt(target, 0, 0, w, h, screen, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x40CC0020))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法捕获放映画面。" );
            }
            finally { graphics.ReleaseHdc(target); ReleaseDC(IntPtr.Zero, screen); }
            using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
        }
        finally
        {
            // Restore bottom first so the original foreground overlay stays above its companions.
            for (int i = hidden.Count - 1; i >= 0; i--)
            {
                GetWindowThreadProcessId(hidden[i], out uint process);
                if (process == Environment.ProcessId) ShowWindow(hidden[i], 4);
            }
        }
    }
    private delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool StretchBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, int sw, int sh, uint operation);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
