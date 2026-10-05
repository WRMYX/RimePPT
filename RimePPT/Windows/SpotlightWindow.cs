using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using RimePPT.Core;
using RimePPT.Services;
using Windows.Foundation;
using Windows.UI;

namespace RimePPT.Windows;

public sealed class SpotlightWindow : Window
{
    private readonly Microsoft.UI.Windowing.DisplayArea _area;
    private readonly Grid _root = new() { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), ManipulationMode = ManipulationModes.None };
    private readonly CanvasControl _canvas = new() { ClearColor = Microsoft.UI.Colors.Transparent, IsHitTestVisible = false };
    private readonly Slider _radius = new() { Minimum = 50, Maximum = 500, Value = 150, Width = 120 };
    private readonly Slider _dim = new() { Minimum = 0, Maximum = 0.9, Value = 0.65, Width = 120 };
    private readonly Slider _zoom = new() { Minimum = 1, Maximum = 4, Value = 2, Width = 100 };
    private CanvasBitmap? _snapshot;
    private CanvasGeometry? _mask;
    private Rect _maskBounds;
    private Vector2 _maskCenter;
    private float _maskRadius;
    private int _resourceVersion;
    private int _captureVersion;
    private byte[] _png;
    private uint? _pointer;
    private Vector2 _center = new(400, 300), _start, _last;
    private Rect _selection;
    private bool _magnify, _selecting, _closed;
    private bool _centered;
    private Border? _controlBorder;
    private Vector2 _pan;
    public SpotlightWindow(Microsoft.UI.Windowing.DisplayArea area)
    {
        _area = area; _png = Array.Empty<byte>();
        Title = "聚光与放大";
        SystemBackdrop = new TransparentBackdrop();
        _root.Children.Add(_canvas); Content = _root;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(12) };
        var mode = new ToggleButton { Content = "框选放大", MinHeight = 44 };
        var refresh = new Button { Content = "刷新画面", MinHeight = 44 };
        async System.Threading.Tasks.Task Refresh()
        {
            if (_closed || !refresh.IsEnabled) return;
            refresh.IsEnabled = false;
            int version = ++_captureVersion;
            try
            {
                byte[] png = await System.Threading.Tasks.Task.Run(() => ScreenCaptureService.Capture(_area.OuterBounds));
                if (_closed || !_magnify || version != _captureVersion) return;
                _png = png;
                int resourceVersion = _resourceVersion;
                using var stream = new MemoryStream(png);
                var next = await CanvasBitmap.LoadAsync(_canvas.Device, stream.AsRandomAccessStream());
                if (_closed || !_magnify || version != _captureVersion || resourceVersion != _resourceVersion) next.Dispose();
                else { _snapshot?.Dispose(); _snapshot = next; refresh.Content = "刷新画面"; _canvas.Invalidate(); WindowPlumbing.RaiseToTopmost(this); Activate(); mode.Focus(FocusState.Programmatic); }
            }
            catch (Exception ex) { CrashReporter.Log(ex.Message); refresh.Content = "刷新失败，重试"; }
            finally { if (!_closed) refresh.IsEnabled = _magnify; }
        }
        refresh.Click += async (_, _) => await Refresh();
        var reselect = new Button { Content = "重新框选", MinHeight = 44 };
        reselect.Click += (_, _) => { _selection = default; _pan = default; _canvas.Invalidate(); };
        mode.Click += async (_, _) =>
        {
            _magnify = mode.IsChecked == true; _selection = default; _pan = default;
            _radius.IsEnabled = !_magnify; _zoom.IsEnabled = refresh.IsEnabled = reselect.IsEnabled = _magnify;
            if (_magnify) await Refresh();
            else { _captureVersion++; _resourceVersion++; _snapshot?.Dispose(); _snapshot = null; _png = Array.Empty<byte>(); }
            _canvas.Invalidate();
        };
        _zoom.IsEnabled = refresh.IsEnabled = reselect.IsEnabled = false;
        var close = new Button { Content = "退出（Esc）", MinHeight = 44 }; close.Click += (_, _) => Close();
        var save = new Button { Content = "保存选区", MinHeight = 44 };
        save.Click += async (_, _) =>
        {
            if (_closed || _snapshot is null || _selection.Width < 2 || _selection.Height < 2) { save.Content = "请先框选区域"; return; }
            save.IsEnabled = false;
            try
            {
                var picker = new global::Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "RimePPT-选区-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
                picker.FileTypeChoices.Add("PNG 图片", new[] { ".png" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file is null || _closed || _snapshot is null) return;
                float sx = (float)(_snapshot.Size.Width / _canvas.ActualWidth), sy = (float)(_snapshot.Size.Height / _canvas.ActualHeight);
                var crop = new Rect(_selection.X * sx, _selection.Y * sy, _selection.Width * sx, _selection.Height * sy);
                using var target = new CanvasRenderTarget(_canvas.Device, (float)crop.Width, (float)crop.Height, 96);
                using (var draw = target.CreateDrawingSession()) { draw.Clear(Microsoft.UI.Colors.Transparent); draw.DrawImage(_snapshot, new Rect(0, 0, crop.Width, crop.Height), crop); }
                await target.SaveAsync(file.Path, CanvasBitmapFileFormat.Png);
                save.Content = "已保存选区";
            }
            catch (Exception ex) { CrashReporter.Report(ex, "spotlight-save"); if (!_closed) save.Content = "保存失败，重试"; }
            finally { if (!_closed) save.IsEnabled = true; }
        };
        controls.Children.Add(save);
        controls.Children.Add(mode); controls.Children.Add(new TextBlock { Text = "范围", VerticalAlignment = VerticalAlignment.Center }); controls.Children.Add(_radius);
        controls.Children.Add(new TextBlock { Text = "压暗", VerticalAlignment = VerticalAlignment.Center }); controls.Children.Add(_dim);
        controls.Children.Add(new TextBlock { Text = "倍数", VerticalAlignment = VerticalAlignment.Center }); controls.Children.Add(_zoom); controls.Children.Add(reselect); controls.Children.Add(refresh); controls.Children.Add(close);
        var border = new Border { Child = new ScrollViewer { Content = controls, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(12), CornerRadius = new(8) };
        _root.SizeChanged += (_, _) => border.MaxWidth = Math.Max(100, _root.ActualWidth - 24);
        _root.Children.Add(border); ThemeResources.Attach(this, _root, border);
        _controlBorder = border;
        foreach (var slider in new[] { _radius, _dim, _zoom }) slider.ValueChanged += (_, _) => _canvas.Invalidate();
        _canvas.CreateResources += async (sender, args) =>
        {
            _mask?.Dispose(); _mask = null;
            int version = ++_resourceVersion;
            if (_png.Length == 0 || !_magnify) return;
            _snapshot?.Dispose(); _snapshot = null;
            try { using var stream = new MemoryStream(_png); var image = await CanvasBitmap.LoadAsync(sender.Device, stream.AsRandomAccessStream()); if (_closed || !_magnify || version != _resourceVersion) image.Dispose(); else { _snapshot = image; sender.Invalidate(); } }
            catch (Exception ex) { CrashReporter.Log(ex.Message); refresh.Content = "刷新失败，重试"; }
        };
        _canvas.Draw += Draw;
        _canvas.SizeChanged += (_, _) =>
        {
            if (_centered || _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0) return;
            _centered = true;
            _center = new((float)_canvas.ActualWidth / 2, (float)_canvas.ActualHeight / 2);
            _canvas.Invalidate();
        };
        _root.PointerPressed += Press; _root.PointerMoved += Move;
        _root.PointerReleased += Release;
        _root.PointerCanceled += (_, e) => { if (_pointer == e.Pointer.PointerId) { _pointer = null; if (_selecting) _selection = default; _selecting = false; _root.ReleasePointerCapture(e.Pointer); _canvas.Invalidate(); } };
        _root.PointerCaptureLost += (_, e) => { if (_pointer == e.Pointer.PointerId) { _pointer = null; if (_selecting) _selection = default; _selecting = false; _canvas.Invalidate(); } };
        var escape = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { e.Handled = true; Close(); };
        _root.KeyboardAccelerators.Add(escape);
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) { presenter.SetBorderAndTitleBar(false, false); presenter.IsAlwaysOnTop = true; presenter.IsResizable = false; }
        AppWindow.IsShownInSwitchers = false;
        WindowPlumbing.EnableTransparency(this, false);
        WindowPlumbing.RemoveWindowBorder(this);
        WindowPlumbing.RemoveResizableFrame(this);
        Closed += (_, _) => { _closed = true; _captureVersion++; _resourceVersion++; _mask?.Dispose(); _mask = null; _snapshot?.Dispose(); _snapshot = null; _canvas.RemoveFromVisualTree(); _png = Array.Empty<byte>(); };
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    public void Show()
    {
        var bounds = _area.OuterBounds; AppWindow.MoveAndResize(bounds); Activate();
        // 排除整个聚光窗口，使刷新与导出的原始截图不含遮罩和控制按钮。
        SetWindowDisplayAffinity(WinRT.Interop.WindowNative.GetWindowHandle(this), 0x11);
    }
    private Vector2 Point(PointerRoutedEventArgs e) { var p = e.GetCurrentPoint(_canvas).Position; return new((float)Math.Clamp(p.X, 0, _canvas.ActualWidth), (float)Math.Clamp(p.Y, 0, _canvas.ActualHeight)); }
    private void Press(object sender, PointerRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, _controlBorder)) return;
        bool captured = !_pointer.HasValue && _root.CapturePointer(e.Pointer);
        RimePPT.Core.Ink.InkDiagnostics.Write(new { kind = "spot-input", action = "pressed", device = e.Pointer.PointerDeviceType.ToString(), captured });
        if (!captured) return;
        _pointer = e.Pointer.PointerId; _start = _last = _center = Point(e); _selecting = _magnify && _selection.Width < 2; e.Handled = true; _canvas.Invalidate();
    }
    private void Move(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId) return; var p = Point(e);
        if (_selecting) _selection = new(Math.Min(p.X, _start.X), Math.Min(p.Y, _start.Y), Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y));
        else if (_magnify) _pan += p - _last; else _center = p;
        _last = p; e.Handled = true; _canvas.Invalidate();
    }
    private void Release(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId) return;
        Move(sender, e);
        RimePPT.Core.Ink.InkDiagnostics.Write(new { kind = "spot-input", action = "released", device = e.Pointer.PointerDeviceType.ToString(), moved = Vector2.Distance(_last, _start) > 4 });
        _pointer = null; _selecting = false; _root.ReleasePointerCapture(e.Pointer); _canvas.Invalidate();
    }
    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession; var full = new Rect(0,0,sender.ActualWidth,sender.ActualHeight);
        if (full.Width <= 0 || full.Height <= 0) return;
        var shade = Color.FromArgb((byte)(_dim.Value * 255),0,0,0);
        if (_magnify)
        {
            if (_snapshot is null) { ds.FillRectangle(full, shade); return; }
            ds.DrawImage(_snapshot, full, new Rect(0,0,_snapshot.Size.Width,_snapshot.Size.Height));
            ds.FillRectangle(full, shade);
            if (_selection.Width > 2 && _selection.Height > 2)
            {
                if (_selecting) ds.DrawRectangle(_selection, Microsoft.UI.Colors.White, 2);
                else
                {
                    var source = new Rect(_selection.X * _snapshot.Size.Width / full.Width, _selection.Y * _snapshot.Size.Height / full.Height, _selection.Width * _snapshot.Size.Width / full.Width, _selection.Height * _snapshot.Size.Height / full.Height);
                    double w = _selection.Width * _zoom.Value, h = _selection.Height * _zoom.Value;
                    ds.DrawImage(_snapshot, new Rect((full.Width-w)/2+_pan.X,(full.Height-h)/2+_pan.Y,w,h), source);
                }
            }
        }
        else
        {
            if (_mask is null || _maskBounds != full || _maskCenter != _center || _maskRadius != (float)_radius.Value)
            {
                _mask?.Dispose();
                using var outside = CanvasGeometry.CreateRectangle(sender.Device, full);
                using var hole = CanvasGeometry.CreateCircle(sender.Device, _center, (float)_radius.Value);
                _mask = outside.CombineWith(hole, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
                _maskBounds = full; _maskCenter = _center; _maskRadius = (float)_radius.Value;
            }
            ds.FillGeometry(_mask, shade);
        }
    }
}
