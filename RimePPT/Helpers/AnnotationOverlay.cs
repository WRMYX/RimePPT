using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace RimePPT.Helpers
{
    /// <summary>
    /// Full-screen transparent annotation canvas implemented as a pure Win32
    /// WS_EX_LAYERED window rendered with GDI+ / UpdateLayeredWindow.
    /// Completely independent of WinUI 3, so it has no transparency issues.
    /// </summary>
    public sealed class AnnotationOverlay : IDisposable
    {
        // ── P/Invoke ────────────────────────────────────────────────────
        private delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(int exStyle, string cls, string? title,
            int style, int x, int y, int cx, int cy,
            IntPtr parent, IntPtr menu, IntPtr hInst, IntPtr param);

        [DllImport("user32.dll")] private static extern bool  DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool  ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool  SetCapture(IntPtr h);
        [DllImport("user32.dll")] private static extern bool  ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool  SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
        [DllImport("user32.dll")] private static extern bool  RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] private static extern bool  UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] private static extern bool  UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
            ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int   ReleaseDC(IntPtr h, IntPtr hdc);
        [DllImport("gdi32.dll")]  private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]  private static extern bool   DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]  private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")]  private static extern bool   DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]  private static extern IntPtr CreateDIBSection(IntPtr hdc,
            ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        // ── Structs ─────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int CX, CY; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public int biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            [MarshalAs(UnmanagedType.FunctionPtr)]
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string  lpszClassName;
            public IntPtr hIconSm;
        }

        // ── Constants ───────────────────────────────────────────────────
        private const int WS_POPUP        = unchecked((int)0x80000000);
        private const int WS_EX_LAYERED   = 0x80000;
        private const int WS_EX_TOPMOST   = 0x8;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int SW_SHOW = 5, SW_HIDE = 0;
        private const int WM_LBUTTONDOWN  = 0x0201;
        private const int WM_MOUSEMOVE    = 0x0200;
        private const int WM_LBUTTONUP    = 0x0202;
        private const int WM_HOTKEY       = 0x0312;
        private const uint VK_ESCAPE      = 0x1B;
        private const uint SWP_NOMOVE     = 0x0002;
        private const uint SWP_NOSIZE     = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const string CLS_NAME = "RimePPT_AnnotationOverlay_v1";

        // ── Fields ──────────────────────────────────────────────────────
        private IntPtr _hwnd;
        private int _x, _y, _w, _h;

        // Delegate kept alive so GC doesn't collect the function pointer
        private readonly WndProcDelegate _proc;
        private static bool _classRegistered;

        private readonly List<(List<PointF> pts, Color color, float width)> _strokes = new();
        private List<PointF>? _current;
        private bool _dragging;

        public bool IsEraser  { get; set; } = false;
        public Color PenColor { get; set; } = Color.Red;
        public float PenWidth { get; set; } = 4f;
        public float EraserRadius { get; set; } = 24f;

        /// <summary>Fired when the user presses Escape to exit annotation mode.</summary>
        public event Action? ExitRequested;

        // ── Constructor ─────────────────────────────────────────────────
        public AnnotationOverlay() { _proc = WndProc; }

        // ── Public API ──────────────────────────────────────────────────

        public void Show(RectInt32 monitor)
        {
            _x = monitor.X; _y = monitor.Y;
            _w = monitor.Width; _h = monitor.Height;

            if (_hwnd == IntPtr.Zero)
                _hwnd = CreateOverlayWindow();

            if (_hwnd == IntPtr.Zero) return;

            ShowWindow(_hwnd, SW_SHOW);
            RegisterHotKey(_hwnd, 1, 0, VK_ESCAPE);
            Render();
        }

        public void Hide()
        {
            if (_hwnd == IntPtr.Zero) return;
            UnregisterHotKey(_hwnd, 1);
            ShowWindow(_hwnd, SW_HIDE);
        }

        public void Clear()
        {
            _strokes.Clear();
            _current = null;
            Render();
        }

        /// <summary>Re-assert topmost so toolbar windows can be raised above this overlay.</summary>
        public void PushBehind(IntPtr aboveHwnd)
        {
            if (_hwnd != IntPtr.Zero)
                SetWindowPos(_hwnd, aboveHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(_hwnd, 1);
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }

        // ── Win32 Window ────────────────────────────────────────────────

        private IntPtr CreateOverlayWindow()
        {
            if (!_classRegistered)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize        = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc   = _proc,
                    lpszClassName = CLS_NAME
                };
                RegisterClassExW(ref wc);
                _classRegistered = true;
            }

            return CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                CLS_NAME, null, WS_POPUP,
                _x, _y, _w, _h,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        // ── WndProc ─────────────────────────────────────────────────────

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
        {
            int lo = (short)(lp.ToInt64() & 0xFFFF);
            int hi = (short)((lp.ToInt64() >> 16) & 0xFFFF);

            switch (msg)
            {
                case WM_LBUTTONDOWN:
                    _current  = new List<PointF> { new(lo, hi) };
                    _dragging = true;
                    SetCapture(hwnd);
                    break;

                case WM_MOUSEMOVE when _dragging && (wp.ToInt64() & 1) != 0:
                    if (IsEraser)
                    {
                        EraseNear(lo, hi);
                        Render();
                    }
                    else
                    {
                        _current?.Add(new PointF(lo, hi));
                        Render();
                    }
                    break;

                case WM_LBUTTONUP when _dragging:
                    if (!IsEraser && _current?.Count > 0)
                        _strokes.Add((_current, PenColor, PenWidth));
                    _current  = null;
                    _dragging = false;
                    ReleaseCapture();
                    Render();
                    break;

                case WM_HOTKEY when wp.ToInt32() == 1:
                    ExitRequested?.Invoke();
                    break;
            }
            return DefWindowProcW(hwnd, msg, wp, lp);
        }

        private void EraseNear(float cx, float cy)
        {
            float r2 = EraserRadius * EraserRadius;
            _strokes.RemoveAll(s =>
                s.pts.Exists(p =>
                    (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy) < r2));
        }

        // ── Render ──────────────────────────────────────────────────────

        private void Render()
        {
            if (_hwnd == IntPtr.Zero || _w <= 0 || _h <= 0) return;

            using var bmp = new Bitmap(_w, _h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (var (pts, col, wid) in _strokes)
                    DrawStroke(g, pts, col, wid);
                if (!IsEraser && _current?.Count > 0)
                    DrawStroke(g, _current, PenColor, PenWidth);
            }

            // Lock bitmap pixels
            var rect = new Rectangle(0, 0, _w, _h);
            var bd   = bmp.LockBits(rect,
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            int rowW   = _w * 4;
            int stride = Math.Abs(bd.Stride);

            // Pre-multiply alpha and copy to flat buffer
            byte[] pixels = new byte[rowW * _h];
            byte[] row    = new byte[stride];
            for (int y = 0; y < _h; y++)
            {
                Marshal.Copy(bd.Scan0 + y * stride, row, 0, stride);
                for (int x = 0; x < rowW; x += 4)
                {
                    byte a = row[x + 3];
                    if (a is > 0 and < 255)
                    {
                        row[x]     = (byte)(row[x]     * a / 255);
                        row[x + 1] = (byte)(row[x + 1] * a / 255);
                        row[x + 2] = (byte)(row[x + 2] * a / 255);
                    }
                }
                Buffer.BlockCopy(row, 0, pixels, y * rowW, rowW);
            }
            bmp.UnlockBits(bd);

            // Create a DIB section and copy pixels into it
            var bi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize    = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth   = _w,
                    biHeight  = -_h,   // top-down
                    biPlanes  = 1,
                    biBitCount = 32,
                    biCompression = 0  // BI_RGB
                }
            };

            IntPtr screenDC = GetDC(IntPtr.Zero);
            IntPtr memDC    = CreateCompatibleDC(screenDC);
            IntPtr hDib     = CreateDIBSection(screenDC, ref bi, 0, out IntPtr dibBits, IntPtr.Zero, 0);
            IntPtr oldBmp   = SelectObject(memDC, hDib);

            Marshal.Copy(pixels, 0, dibBits, pixels.Length);

            var pDst = new POINT { X = _x, Y = _y };
            var pSrc = new POINT();
            var sz   = new SIZE { CX = _w, CY = _h };
            var bf   = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };

            UpdateLayeredWindow(_hwnd, screenDC, ref pDst, ref sz, memDC, ref pSrc, 0, ref bf, 2 /*ULW_ALPHA*/);

            SelectObject(memDC, oldBmp);
            DeleteObject(hDib);
            DeleteDC(memDC);
            ReleaseDC(IntPtr.Zero, screenDC);
        }

        private static void DrawStroke(Graphics g, List<PointF> pts, Color col, float w)
        {
            if (pts.Count == 1)
            {
                using var brush = new SolidBrush(col);
                g.FillEllipse(brush, pts[0].X - w / 2, pts[0].Y - w / 2, w, w);
                return;
            }
            using var pen = new Pen(col, w)
            {
                LineJoin   = LineJoin.Round,
                StartCap   = LineCap.Round,
                EndCap     = LineCap.Round
            };
            g.DrawLines(pen, pts.ToArray());
        }
    }
}
