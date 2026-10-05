using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Linq;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RimePPT.Core;
using RimePPT.Core.Ink;
using RimePPT.Services;
using Windows.Graphics;

namespace RimePPT.Windows;

public enum AnnotationTool { Pen, Eraser }

/// <summary>Transparent, non-activating ink surface. All contacts use the same event-driven adapter.</summary>
public sealed class AnnotationWindow : Window
{
    // A transparent brush explicitly participates in XAML hit testing; null does not.
    private readonly Grid _root = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), ManipulationMode = ManipulationModes.None };
    private CanvasControl? _canvas;
    private readonly Dictionary<int, List<StrokeData>> _slides;
    private readonly InkContactManager _contacts = new();
    private readonly Dictionary<uint, InkDiagnostics> _logs = new();
    private readonly Dictionary<uint, Microsoft.UI.Xaml.Input.Pointer> _pointers = new();
    private readonly Dictionary<uint, (float Width, float Height)> _sizes = new();
    private readonly Dictionary<uint, Vector2> _eraserPositions = new();
    private readonly Dictionary<uint, long> _lastMotion = new();
    private readonly Dictionary<uint, Vector2> _holdPositions = new();
    private readonly Dictionary<uint, StrokeData> _shapePreviews = new();
    private readonly Dictionary<uint, InkDrawingOptions> _drawingOptions = new();
    private readonly HashSet<uint> _shapeChecked = new();
    private readonly DispatcherTimer _shapeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private StrokeData[]? _eraseBefore;
    private readonly InkHistory _history;
    private readonly StrokeEraser _eraser = new();
    
    private readonly List<InkSample> _batch = new();
    private InkRenderer? _renderer;
    private int _activeSlide = 1;
    private AnnotationTool _tool;
    private bool _enabled, _closed, _isDark;
    private Vector2? _eraserPosition;
    private readonly DispatcherTimer _animationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    public event EventHandler? InkChanged;
    public Action? CompanionsRaise;
    public AnnotationTool Tool
    {
        get => _tool;
        set { if (_tool != value) { FinishInput(); _tool = value; _eraserPosition = null; Invalidate(); } }
    }
    public bool AnnotationEnabled
    {
        get => _enabled;
        set
        {
            if (_closed) return;
            if (!value) FinishInput();
            WindowPlumbing.SetClickThrough(this, !value);
            _enabled = value;
            if (!value) _eraserPosition = null;
            Invalidate();
        }
    }

    public AnnotationWindow(Dictionary<int, List<StrokeData>> slides)
    {
        _slides = slides; _history = new(slides);
        _animationTimer.Tick += OnAnimationFrame;
        _shapeTimer.Tick += OnShapeFrame;
        _canvas = new CanvasControl { ClearColor = default, IsHitTestVisible = false };
        _canvas.CreateResources += OnCreateResources;
        _canvas.Draw += OnDraw;
        _root.PointerPressed += OnPressed;
        _root.PointerMoved += OnMoved;
        _root.PointerReleased += OnReleased;
        _root.PointerCanceled += OnCanceled;
        _root.PointerCaptureLost += OnCaptureLost;
        _root.SizeChanged += OnSizeChanged;
        _root.Children.Add(_canvas);
        Content = _root;
        Title = "RimePPT Annotation";
        SystemBackdrop = new TransparentBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false); presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false; presenter.IsMinimizable = false; presenter.IsMaximizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        _isDark = ThemeHelper.IsDarkTheme();
        AppSettings.SettingsChanged += OnSettingsChanged;
        Closed += OnClosed;
    }

    public void ShowOn(DisplayArea area)
    {
        WindowPlumbing.ApplyNoActivate(this); WindowPlumbing.EnableTransparency(this);
        WindowPlumbing.RemoveWindowBorder(this); WindowPlumbing.RemoveResizableFrame(this);
        var bounds = area.OuterBounds;
        AppWindow.Resize(new SizeInt32(bounds.Width, bounds.Height));
        AppWindow.Move(new PointInt32(bounds.X, bounds.Y));
        Activate();
        // 显示/恢复窗口不能覆盖当前笔状态，否则 UI 已选中画笔却穿透到 PPT。
        WindowPlumbing.SetClickThrough(this, !_enabled);
        Invalidate();
    }

    public void SetActiveSlide(int slideIndex)
    {
        FinishInput();
        if (slideIndex != _activeSlide && _renderer is not null)
        {
            var settings = AppSettings.Instance;
            var mode = new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled ? settings.InkPageAnimation : InkPageAnimationMode.None;
            IReadOnlyList<StrokeData> incoming = _slides.TryGetValue(slideIndex, out var strokes) ? strokes : Array.Empty<StrokeData>();
            _renderer.BeginSlideTransition(Viewport, incoming, mode, settings.InkFadeDurationMs, settings.InkReplayDurationMs);
            if (_renderer.IsAnimating) _animationTimer.Start();
        }
        else StopAnimation();
        _activeSlide = slideIndex; _eraser.Reset(); _eraserPosition = null;
        _renderer?.InvalidateSlide(); Invalidate();
    }

    private void OnAnimationFrame(object? sender, object args) { Invalidate(); if (_renderer?.IsAnimating != true) _animationTimer.Stop(); }
    private void StopAnimation() { _animationTimer.Stop(); _renderer?.CancelAnimation(); }

    private InkViewport Viewport => new((float)_root.ActualWidth, (float)_root.ActualHeight, (float)(_root.XamlRoot?.RasterizationScale ?? 1));
    private static InkDevice Device(PointerRoutedEventArgs e) => e.Pointer.PointerDeviceType switch
    {
        Microsoft.UI.Input.PointerDeviceType.Touch => InkDevice.Touch,
        Microsoft.UI.Input.PointerDeviceType.Pen => InkDevice.Pen,
        _ => InkDevice.Mouse,
    };
    private InkSample GetSample(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_root);
        return new(new Vector2((float)point.Position.X, (float)point.Position.Y), point.Timestamp, Device(e));
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_enabled || _closed) return;
        e.Handled = true;
        if (_contacts.Find(e.Pointer.PointerId) is not null) return;
        var point = e.GetCurrentPoint(_root);
        if (!point.IsInContact || (Device(e) == InkDevice.Mouse && !point.Properties.IsLeftButtonPressed)) return;
        if (!Viewport.IsValid) return;
        StopAnimation();
        bool captured = _root.CapturePointer(e.Pointer);
        var diagnostics = new InkDiagnostics();
        diagnostics.Begin(e.Pointer.PointerId, Device(e), captured, _activeSlide, _tool == AnnotationTool.Pen ? InkTool.Pen : InkTool.Eraser);
        if (!captured) { diagnostics.End("capture-failed", 0); return; }
        var settings = AppSettings.Instance;
        var tool = new InkToolSnapshot(_tool == AnnotationTool.Pen ? InkTool.Pen : InkTool.Eraser, _activeSlide,
            settings.GetPenArgb(), (float)settings.PenThickness);
        if (!_contacts.Begin(e.Pointer.PointerId, GetSample(e), tool, Viewport))
        {
            _root.ReleasePointerCapture(e.Pointer); diagnostics.End("begin-failed", 0); return;
        }
        _logs[e.Pointer.PointerId] = diagnostics;
        _pointers[e.Pointer.PointerId] = e.Pointer;
        _sizes[e.Pointer.PointerId] = ((float)settings.EraserWidthDip, (float)settings.EraserHeightDip);
        _drawingOptions[e.Pointer.PointerId] = new(settings.PenLineStyle, settings.PenShape);
        _lastMotion[e.Pointer.PointerId] = Stopwatch.GetTimestamp();
        _holdPositions[e.Pointer.PointerId] = GetSample(e).Position;
        if (tool.Tool == InkTool.Pen && settings.SmartShapesEnabled && settings.PenShape == InkShape.Freehand && settings.PenLineStyle == InkLineStyle.Solid) _shapeTimer.Start();
        if (tool.Tool == InkTool.Eraser)
        {
            _eraseBefore ??= _history.Snapshot(_activeSlide);
            _eraserPosition = GetSample(e).Position;
            _eraserPositions[e.Pointer.PointerId] = _eraserPosition.Value;
            Erase(e.Pointer.PointerId, _eraserPosition.Value, _eraserPosition.Value);
        }
        Invalidate();
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_enabled || _closed) return;
        e.Handled = true;
        var input = _contacts.Find(e.Pointer.PointerId);
        if (input is null)
        {
            if (_contacts.Count == 0 && _tool == AnnotationTool.Eraser && Device(e) != InkDevice.Touch)
            { _eraserPosition = GetSample(e).Position; Invalidate(); }
            return;
        }
        long start = Stopwatch.GetTimestamp();
        _batch.Clear();
        foreach (var point in e.GetIntermediatePoints(_root))
            _batch.Add(new(new Vector2((float)point.Position.X, (float)point.Position.Y), point.Timestamp, Device(e)));
        if (_batch.Count == 0) _batch.Add(GetSample(e));
        _batch.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        var previous = input.LastPosition;
        if (_holdPositions.TryGetValue(e.Pointer.PointerId, out var hold) && _batch.Any(sample => Vector2.Distance(sample.Position, hold) > SmartShapeRecognizer.HoldTolerance(sample.Device)))
        { _lastMotion[e.Pointer.PointerId] = Stopwatch.GetTimestamp(); _holdPositions[e.Pointer.PointerId] = _batch[^1].Position; _shapePreviews.Remove(e.Pointer.PointerId); _shapeChecked.Remove(e.Pointer.PointerId); }
        if (input.Tool.Tool == InkTool.Eraser && previous is { } from)
        {
            foreach (var sample in _batch)
            {
                if (sample.Timestamp < input.LastTimestamp) continue;
                Erase(e.Pointer.PointerId, from, sample.Position); from = sample.Position;
            }
            _eraserPosition = from; _eraserPositions[e.Pointer.PointerId] = from;
        }
        input.Move(e.Pointer.PointerId, _batch);
        _logs[e.Pointer.PointerId].Input(start, _batch.Count);
        Invalidate();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_contacts.Find(e.Pointer.PointerId) is null) return;
        e.Handled = true;
        OnMoved(sender, e); // A release can carry coalesced samples not seen by the last move.
        Finish(e.Pointer.PointerId, "released", GetSample(e));
    }
    private void OnCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_contacts.Find(e.Pointer.PointerId) is null) return;
        e.Handled = true; Finish(e.Pointer.PointerId, "canceled", null);
    }
    private void OnCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_contacts.Find(e.Pointer.PointerId) is not null) Finish(e.Pointer.PointerId, "capture-lost", null);
    }

    public void FinishInput()
    {
        foreach (var id in _contacts.Ids()) Finish(id, "transition", null);
    }
    public bool CanUndo => _history.CanUndo(_activeSlide);
    public bool CanRedo => _history.CanRedo(_activeSlide);
    public void Undo() { FinishInput(); if (_history.Undo(_activeSlide)) HistoryChanged(); }
    public void Redo() { FinishInput(); if (_history.Redo(_activeSlide)) HistoryChanged(); }
    public void ClearCurrentSlide() { FinishInput(); if (_history.Clear(_activeSlide)) HistoryChanged(); }
    private void HistoryChanged() { StopAnimation(); _eraser.Reset(); _renderer?.InvalidateSlide(); InkChanged?.Invoke(this, EventArgs.Empty); Invalidate(); }
    private void Finish(uint id, string reason, InkSample? finalSample)
    {
        var input = _contacts.Find(id); if (input is null) return;
        var tool = input.Tool; var diagnostics = _logs[id];
        if (tool.Tool == InkTool.Eraser && finalSample is { } final && input.LastPosition is { } last) Erase(id, last, final.Position);
        var before = tool.Tool == InkTool.Pen ? _history.Snapshot(tool.SlideIndex) : null;
        var stroke = _contacts.End(id, finalSample);
        if (_shapePreviews.Remove(id, out var shape) && AppSettings.Instance.SmartShapesEnabled && stroke is not null && (reason is "released" or "transition")) stroke = shape;
        if (stroke is not null && _drawingOptions.TryGetValue(id, out var options)) stroke = InkDrawing.Apply(stroke, input.Viewport, options);
        _drawingOptions.Remove(id);
        _shapeChecked.Remove(id);
        if (stroke is not null)
        {
            if (!_slides.TryGetValue(stroke.SlideIndex, out var list)) _slides[stroke.SlideIndex] = list = new();
            list.Add(stroke); _history.Record(stroke.SlideIndex, before!); _renderer?.Commit(stroke);
            diagnostics.Changed(); InkChanged?.Invoke(this, EventArgs.Empty);
        }
        if (tool.Tool == InkTool.Eraser && _contacts.Count == 0 && _eraseBefore is not null)
        { _history.Record(tool.SlideIndex, _eraseBefore); _eraseBefore = null; }
        diagnostics.End(reason, stroke?.Dots.Count ?? 0); _logs.Remove(id); _sizes.Remove(id); _eraserPositions.Remove(id);
        _lastMotion.Remove(id);
        _holdPositions.Remove(id);
        if (_contacts.Count == 0) _shapeTimer.Stop();
        if (_pointers.Remove(id, out var pointer)) _root.ReleasePointerCapture(pointer);
        if (!_closed) { CompanionsRaise?.Invoke(); Invalidate(); }
    }
    private void Erase(uint id, Vector2 from, Vector2 to)
    {
        var input = _contacts.Find(id); if (input is null) return;
        var size = _sizes[id];
        if (_slides.TryGetValue(input.Tool.SlideIndex, out var list) &&
            _eraser.EraseSweep(list, new(from, to, size.Width, size.Height), input.Viewport).Changed)
        { _renderer?.InvalidateSlide(); _logs[id].Changed(); InkChanged?.Invoke(this, EventArgs.Empty); }
    }
    public InkViewport CurrentViewport => Viewport;
    private void OnShapeFrame(object? sender, object args)
    {
        if (!AppSettings.Instance.SmartShapesEnabled) { _shapePreviews.Clear(); _shapeTimer.Stop(); Invalidate(); return; }
        foreach (var contact in _contacts.Contacts)
            if (_drawingOptions.TryGetValue(contact.Key, out var options) && options == new InkDrawingOptions(InkLineStyle.Solid, InkShape.Freehand)
                && !_shapeChecked.Contains(contact.Key) && contact.Value.Preview is { } preview && _lastMotion.TryGetValue(contact.Key, out long moved)
                && Stopwatch.GetElapsedTime(moved).TotalMilliseconds >= 600)
            {
                _shapeChecked.Add(contact.Key);
                if (SmartShapeRecognizer.Recognize(preview, contact.Value.Viewport) is { } shape)
                { _shapePreviews[contact.Key] = shape; Invalidate(); }
            }
    }
    private IReadOnlyList<StrokeData> Previews() => _contacts.Contacts.Where(c => c.Value.Preview is not null)
        .Select(c => InkDrawing.Apply(_shapePreviews.TryGetValue(c.Key, out var shape) ? shape : c.Value.Preview!, c.Value.Viewport, _drawingOptions[c.Key])).ToArray();

    private IReadOnlyList<StrokeData> CurrentStrokes() => _slides.TryGetValue(_activeSlide, out var list) ? list : Array.Empty<StrokeData>();
    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _animationTimer.Stop();
        _renderer?.Dispose();
        _renderer = new InkRenderer(sender.Device, CurrentStrokes, Previews) { IsDark = _isDark };
    }
    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_closed) return;
        long start = Stopwatch.GetTimestamp();
        _renderer ??= new InkRenderer(sender.Device, CurrentStrokes, Previews) { IsDark = _isDark };
        _renderer.EraserPosition = _enabled && _tool == AnnotationTool.Eraser ? _eraserPosition : null;
        _renderer.EraserSize = new((float)AppSettings.Instance.EraserWidthDip, (float)AppSettings.Instance.EraserHeightDip);
        _renderer.EraserPositions = _eraserPositions.Values.ToArray();
        _renderer.Draw(args.DrawingSession, Viewport);
        if (!_renderer.IsAnimating) _animationTimer.Stop();
        foreach (var log in _logs.Values) log.Draw(start);
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    { FinishInput(); StopAnimation(); _eraser.Reset(); _renderer?.InvalidateSlide(); Invalidate(); }
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        StopAnimation();
        _isDark = ThemeHelper.IsDarkTheme();
        if (_renderer is not null) _renderer.IsDark = _isDark;
        Invalidate();
    }
    private void Invalidate() { if (!_closed) _canvas?.Invalidate(); }
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true; FinishInput(); StopAnimation(); _animationTimer.Tick -= OnAnimationFrame; AppSettings.SettingsChanged -= OnSettingsChanged;
        _shapeTimer.Stop(); _shapeTimer.Tick -= OnShapeFrame; _shapePreviews.Clear(); _lastMotion.Clear(); _holdPositions.Clear();
        _root.PointerPressed -= OnPressed; _root.PointerMoved -= OnMoved; _root.PointerReleased -= OnReleased;
        _root.PointerCanceled -= OnCanceled; _root.PointerCaptureLost -= OnCaptureLost; _root.SizeChanged -= OnSizeChanged;
        _renderer?.Dispose(); _renderer = null; _eraser.Reset();
        if (_canvas is not null)
        {
            _canvas.CreateResources -= OnCreateResources; _canvas.Draw -= OnDraw;
            _canvas.RemoveFromVisualTree(); _canvas = null;
        }
        CompanionsRaise = null; InkChanged = null; Closed -= OnClosed;
    }
}
