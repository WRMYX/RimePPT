using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RimePPT.Helpers;
using RimePPT.Models;
using RimePPT.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.UI;
using WinRT.Interop;

namespace RimePPT
{
    public sealed partial class OverlayWindow : Window
    {
        // ── Services ──────────────────────────────────────────────────
        private readonly PptControlService _pptControl;

        // ── State ─────────────────────────────────────────────────────
        private bool _isLocked    = false;
        private bool _isAnnotating = false;
        private bool _isErasing   = false;
        private Color _currentColor     = Colors.Red;
        private double _currentThickness = 3.0;

        private readonly Dictionary<int, List<StrokeData>> _annotations = new();
        private readonly List<(Polyline Poly, StrokeData Stroke)> _pageElements = new();

        private Polyline?   _activePoly   = null;
        private StrokeData? _activeStroke = null;
        private bool        _isDrawing    = false;

        // ── Win32 Monitor info ────────────────────────────────────────
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool   GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        // ── Constructor ───────────────────────────────────────────────
        public OverlayWindow(PptControlService pptControl)
        {
            _pptControl = pptControl;
            InitializeComponent();
            InitializeOverlayWindow();
        }

        private void InitializeOverlayWindow()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId wid = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(wid);

            // Transparent, always-on-top, no taskbar entry
            WindowHelper.MakeTransparentOverlay(hwnd);
            WindowHelper.SetTopmost(hwnd);

            // No title bar
            appWindow.TitleBar.ExtendsContentIntoTitleBar = true;

            // Register selective click-through (toolbars are interactive, rest passes through)
            WindowHelper.EnableSelectiveClickThrough(hwnd, IsInInteractiveArea);

            // Stretch window to cover the PPT monitor
            appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }

        // ── Monitor positioning ───────────────────────────────────────

        /// <summary>
        /// Position this overlay to cover the same screen as the PowerPoint slideshow.
        /// </summary>
        public void CoverMonitorOf(IntPtr pptHwnd)
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId wid = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(wid);

            const uint MONITOR_DEFAULTTONEAREST = 2;
            IntPtr hMonitor = MonitorFromWindow(pptHwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right  - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top));
            }
        }

        // ── Interactive hit-test ──────────────────────────────────────
        // Called from Win32 WM_NCHITTEST — physical pixel coordinates (client-relative)
        private bool IsInInteractiveArea(int px, int py)
        {
            if (_isAnnotating) return true; // annotation mode: entire overlay interactive

            double scale = WindowHelper.GetDpiScale(WindowNative.GetWindowHandle(this));

            bool InElement(FrameworkElement el)
            {
                try
                {
                    var transform = el.TransformToVisual(null);
                    var bounds = transform.TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                    var phys = new Rect(bounds.X * scale, bounds.Y * scale,
                                        bounds.Width * scale, bounds.Height * scale);
                    return phys.Contains(new Point(px, py));
                }
                catch { return false; }
            }

            return InElement(LeftToolbar) || InElement(RightToolbar) ||
                   InElement(BottomToolbar) ||
                   (ColorPanel.Visibility == Visibility.Visible && InElement(ColorPanel));
        }

        // ── Layout change → update toolbar bounds ─────────────────────
        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e) { /* bounds recalculated on each hit-test call */ }

        // ── Annotation Storage ────────────────────────────────────────
        private void SaveCurrentAnnotations()
        {
            int slide = _pptControl.CurrentSlide();
            if (slide > 0)
                _annotations[slide] = _pageElements.Select(p => p.Stroke).ToList();
        }

        private void RestoreAnnotations(int slideIndex)
        {
            InkCanvas.Children.Clear();
            _pageElements.Clear();

            if (_annotations.TryGetValue(slideIndex, out var strokes))
                foreach (var s in strokes)
                    _pageElements.Add((BuildPolyline(s), s));
        }

        private Polyline BuildPolyline(StrokeData stroke)
        {
            var poly = new Polyline
            {
                Stroke = new SolidColorBrush(stroke.StrokeColor),
                StrokeThickness = stroke.Thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round,
                Points = new PointCollection()
            };
            foreach (var pt in stroke.Points) poly.Points.Add(pt);
            InkCanvas.Children.Add(poly);
            return poly;
        }

        // ── Ink Drawing ───────────────────────────────────────────────
        private void InkCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_isAnnotating || _isErasing) return;
            var pt = e.GetCurrentPoint(InkCanvas).Position;
            _activeStroke = new StrokeData
            {
                StrokeColor = _currentColor,
                Thickness   = _currentThickness,
                Points      = new List<Point> { pt }
            };
            _activePoly = BuildPolyline(_activeStroke);
            _pageElements.Add((_activePoly, _activeStroke));
            _isDrawing = true;
            InkCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void InkCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isAnnotating && !_isErasing && _isDrawing && _activePoly != null && _activeStroke != null)
            {
                var pt = e.GetCurrentPoint(InkCanvas).Position;
                _activeStroke.Points.Add(pt);
                _activePoly.Points.Add(pt);
                e.Handled = true;
            }
            else if (_isAnnotating && _isErasing)
            {
                EraseAtPoint(e.GetCurrentPoint(InkCanvas).Position);
            }
        }

        private void InkCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDrawing) return;
            _isDrawing    = false;
            _activePoly   = null;
            _activeStroke = null;
            try { InkCanvas.ReleasePointerCapture(e.Pointer); } catch { }
            e.Handled = true;
        }

        private void EraseAtPoint(Point pt)
        {
            const double R = 20.0;
            var toRemove = new List<(Polyline, StrokeData)>();
            foreach (var (poly, stroke) in _pageElements)
                foreach (var sp in stroke.Points)
                {
                    double dx = sp.X - pt.X, dy = sp.Y - pt.Y;
                    if (dx * dx + dy * dy <= R * R) { toRemove.Add((poly, stroke)); break; }
                }
            foreach (var item in toRemove)
            {
                InkCanvas.Children.Remove(item.Item1);
                _pageElements.Remove(item);
            }
        }

        // ── Navigation ────────────────────────────────────────────────
        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_isLocked) return;
            SaveCurrentAnnotations();
            _pptControl.PreviousSlide();
            RefreshPageInfo();
            RestoreAnnotations(_pptControl.CurrentSlide());
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_isLocked) return;
            SaveCurrentAnnotations();
            _pptControl.NextSlide();
            RefreshPageInfo();
            RestoreAnnotations(_pptControl.CurrentSlide());
        }

        private void RefreshPageInfo()
        {
            int cur = _pptControl.CurrentSlide();
            int tot = _pptControl.TotalSlides();
            TbPageInfo.Text = (cur > 0 && tot > 0) ? $"{cur} / {tot}" : "— / —";
        }

        // ── Toolbar Button Handlers ───────────────────────────────────
        private void BtnAnnotate_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnnotating && !_isErasing)
            {
                SetAnnotateMode(false, false);
                ColorPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                SetAnnotateMode(true, false);
                ColorPanel.Visibility = Visibility.Visible;
            }
        }

        private void BtnEraser_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnnotating && _isErasing) SetAnnotateMode(false, false);
            else
            {
                SetAnnotateMode(true, true);
                ColorPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void SetAnnotateMode(bool annotating, bool erasing)
        {
            _isAnnotating = annotating;
            _isErasing    = erasing;
            InkCanvas.IsHitTestVisible = annotating;

            var pen    = new SolidColorBrush(Color.FromArgb(0x44, 0x21, 0x96, 0xF3));
            var eraser = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0x98, 0x00));
            var none   = new SolidColorBrush(Colors.Transparent);
            var penBot = new SolidColorBrush(Color.FromArgb(0x55, 0x21, 0x96, 0xF3));
            var ersBot = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0x98, 0x00));
            var defBot = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

            BtnLeftAnnotate.Background   = (annotating && !erasing) ? pen    : none;
            BtnRightAnnotate.Background  = BtnLeftAnnotate.Background;
            BtnBottomAnnotate.Background = (annotating && !erasing) ? penBot : defBot;

            BtnLeftEraser.Background   = (annotating && erasing) ? eraser : none;
            BtnRightEraser.Background  = BtnLeftEraser.Background;
            BtnBottomEraser.Background = (annotating && erasing) ? ersBot : defBot;
        }

        private void BtnClearInk_Click(object sender, RoutedEventArgs e)
        {
            InkCanvas.Children.Clear();
            _pageElements.Clear();
            _annotations.Remove(_pptControl.CurrentSlide());
        }

        private void BtnLock_Click(object sender, RoutedEventArgs e)
        {
            _isLocked = !_isLocked;
            string g = _isLocked ? "\uE72F" : "\uE72E";
            IconLeftLock.Glyph   = g;
            IconRightLock.Glyph  = g;
            IconBottomLock.Glyph = g;
            BtnLeftPrev.IsEnabled  = !_isLocked;
            BtnLeftNext.IsEnabled  = !_isLocked;
            BtnRightPrev.IsEnabled = !_isLocked;
            BtnRightNext.IsEnabled = !_isLocked;
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            _pptControl.EndShow();
            HideOverlay();
        }

        // ── Color / Thickness ─────────────────────────────────────────
        private void BtnColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
                _currentColor = tag switch
                {
                    "Red"    => Colors.Red,
                    "Yellow" => Colors.Yellow,
                    "Green"  => Color.FromArgb(255, 76, 175, 80),
                    "Blue"   => Color.FromArgb(255, 33, 150, 243),
                    "White"  => Colors.White,
                    "Black"  => Colors.Black,
                    _        => Colors.Red
                };
        }

        private void BtnThin_Click(object sender, RoutedEventArgs e)   => _currentThickness = 2.0;
        private void BtnMedium_Click(object sender, RoutedEventArgs e) => _currentThickness = 4.0;
        private void BtnThick_Click(object sender, RoutedEventArgs e)  => _currentThickness = 8.0;

        // ── Show / Hide ───────────────────────────────────────────────
        public void ShowOverlay(IntPtr pptHwnd)
        {
            _pptControl.TryConnect();

            // Reset annotation state when new slideshow starts
            _annotations.Clear();
            _pageElements.Clear();
            InkCanvas.Children.Clear();
            SetAnnotateMode(false, false);
            ColorPanel.Visibility = Visibility.Collapsed;

            CoverMonitorOf(pptHwnd);
            this.Activate();

            RefreshPageInfo();
        }

        public void HideOverlay()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId wid = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow.GetFromWindowId(wid).Hide();
            _pptControl.Disconnect();
        }
    }
}
