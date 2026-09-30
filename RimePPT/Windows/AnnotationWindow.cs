using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
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
    private readonly InkInputSession _input = new();
    private readonly StrokeEraser _eraser = new();
    private readonly InkDiagnostics _diagnostics = new();
    private readonly List<InkSample> _batch = new();
    private InkRenderer? _renderer;
    private int _activeSlide = 1;
    private AnnotationTool _tool;
    private bool _enabled, _closed, _isDark;
    private Vector2? _eraserPosition;

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
        _slides = slides;
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
        WindowPlumbing.SetClickThrough(this, true);
        var bounds = area.OuterBounds;
        AppWindow.Resize(new SizeInt32(bounds.Width, bounds.Height));
        AppWindow.Move(new PointInt32(bounds.X, bounds.Y));
        Activate(); Invalidate();
    }

    public void SetActiveSlide(int slideIndex)
    {
        FinishInput(); _activeSlide = slideIndex; _eraser.Reset(); _eraserPosition = null;
        _renderer?.InvalidateSlide(); Invalidate();
    }

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
        if (_input.PointerId.HasValue) return;
        var point = e.GetCurrentPoint(_root);
        if (!point.IsInContact || (Device(e) == InkDevice.Mouse && !point.Properties.IsLeftButtonPressed)) return;
        _input.Viewport = Viewport;
        if (!_input.Viewport.IsValid) return;
        bool captured = _root.CapturePointer(e.Pointer);
        _diagnostics.Begin(e.Pointer.PointerId, Device(e), captured, _activeSlide, _tool == AnnotationTool.Pen ? InkTool.Pen : InkTool.Eraser);
        if (!captured) { _diagnostics.End("capture-failed", 0); return; }
        var settings = AppSettings.Instance;
        var tool = new InkToolSnapshot(_tool == AnnotationTool.Pen ? InkTool.Pen : InkTool.Eraser, _activeSlide,
            PenPalette.GetArgb(settings.PenColor), (float)settings.PenThickness);
        if (!_input.Begin(e.Pointer.PointerId, GetSample(e), tool))
        {
            _root.ReleasePointerCapture(e.Pointer); _diagnostics.End("begin-failed", 0); return;
        }
        if (tool.Tool == InkTool.Eraser)
        {
            _eraserPosition = GetSample(e).Position;
            Erase(_eraserPosition.Value, _eraserPosition.Value);
        }
        Invalidate();
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_enabled || _closed) return;
        e.Handled = true;
        if (_input.PointerId != e.Pointer.PointerId)
        {
            if (!_input.PointerId.HasValue && _tool == AnnotationTool.Eraser && Device(e) != InkDevice.Touch)
            { _eraserPosition = GetSample(e).Position; Invalidate(); }
            return;
        }
        long start = Stopwatch.GetTimestamp();
        _batch.Clear();
        foreach (var point in e.GetIntermediatePoints(_root))
            _batch.Add(new(new Vector2((float)point.Position.X, (float)point.Position.Y), point.Timestamp, Device(e)));
        if (_batch.Count == 0) _batch.Add(GetSample(e));
        _batch.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        var previous = _input.LastPosition;
        if (_input.Tool.Tool == InkTool.Eraser && previous is { } from)
        {
            foreach (var sample in _batch)
            {
                if (sample.Timestamp < _input.LastTimestamp) continue;
                Erase(from, sample.Position); from = sample.Position;
            }
            _eraserPosition = from;
        }
        _input.Move(e.Pointer.PointerId, _batch);
        _diagnostics.Input(start, _batch.Count);
        Invalidate();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_input.PointerId != e.Pointer.PointerId) return;
        e.Handled = true;
        OnMoved(sender, e); // A release can carry coalesced samples not seen by the last move.
        Finish("released", GetSample(e));
    }
    private void OnCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_input.PointerId != e.Pointer.PointerId) return;
        e.Handled = true; Finish("canceled", null);
    }
    private void OnCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_input.PointerId == e.Pointer.PointerId) Finish("capture-lost", null);
    }

    public void FinishInput() => Finish("transition", null);
    private void Finish(string reason, InkSample? finalSample)
    {
        if (_input.PointerId is not { } id) return;
        if (_input.Tool.Tool == InkTool.Eraser && finalSample is { } final && _input.LastPosition is { } last)
        { Erase(last, final.Position); _eraserPosition = final.Position; }
        var stroke = _input.End(id, finalSample);
        if (stroke is not null)
        {
            if (!_slides.TryGetValue(stroke.SlideIndex, out var list)) _slides[stroke.SlideIndex] = list = new();
            list.Add(stroke); _renderer?.Commit(stroke); _diagnostics.Changed(); InkChanged?.Invoke(this, EventArgs.Empty);
        }
        _diagnostics.End(reason, stroke?.Dots.Count ?? 0);
        // Clear session before release: CaptureLost can be synchronous and must not commit twice.
        _root.ReleasePointerCaptures();
        if (!_closed) { CompanionsRaise?.Invoke(); Invalidate(); }
    }

    private void Erase(Vector2 from, Vector2 to)
    {
        if (_slides.TryGetValue(_input.Tool.SlideIndex, out var list) &&
            _eraser.EraseSweep(list, new(from, to), _input.Viewport).Changed)
        { _renderer?.InvalidateSlide(); _diagnostics.Changed(); InkChanged?.Invoke(this, EventArgs.Empty); }
    }

    private IReadOnlyList<StrokeData> CurrentStrokes() => _slides.TryGetValue(_activeSlide, out var list) ? list : Array.Empty<StrokeData>();
    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _renderer?.Dispose();
        _renderer = new InkRenderer(sender.Device, CurrentStrokes, () => _input.Preview) { IsDark = _isDark };
    }
    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_closed) return;
        long start = Stopwatch.GetTimestamp();
        _renderer ??= new InkRenderer(sender.Device, CurrentStrokes, () => _input.Preview) { IsDark = _isDark };
        _renderer.EraserPosition = _enabled && _tool == AnnotationTool.Eraser ? _eraserPosition : null;
        _renderer.Draw(args.DrawingSession, Viewport);
        _diagnostics.Draw(start);
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    { FinishInput(); _eraser.Reset(); _renderer?.InvalidateSlide(); Invalidate(); }
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _isDark = ThemeHelper.IsDarkTheme();
        if (_renderer is not null) _renderer.IsDark = _isDark;
        Invalidate();
    }
    private void Invalidate() { if (!_closed) _canvas?.Invalidate(); }
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true; FinishInput(); AppSettings.SettingsChanged -= OnSettingsChanged;
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
