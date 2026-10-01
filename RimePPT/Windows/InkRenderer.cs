using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using RimePPT.Core.Ink;
using Windows.Foundation;
using Windows.UI;

namespace RimePPT.Windows;

/// <summary>One historical bitmap for the current page. Live ink stays at native drawing DPI.</summary>
public sealed class InkRenderer : IDisposable
{
    private const double CacheBudgetBytes = 64 * 1024 * 1024;
    private readonly CanvasDevice _device;
    private readonly Func<IReadOnlyList<StrokeData>> _strokes;
    private readonly Func<IReadOnlyList<StrokeData>> _preview;
    private readonly List<StrokeData> _pending = new();
    private readonly Dictionary<StrokeData, CanvasGeometry> _geometries = new();
    private readonly CanvasStrokeStyle _style = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
    private CanvasRenderTarget? _history;
    private InkViewport _viewport;
    private bool _dirty = true;
    public bool IsDark { get; set; }
    public Vector2? EraserPosition { get; set; }
    public IReadOnlyList<Vector2> EraserPositions { get; set; } = Array.Empty<Vector2>();
    public Vector2 EraserSize { get; set; } = new(56, 72);

    public InkRenderer(CanvasDevice device, Func<IReadOnlyList<StrokeData>> strokes, Func<IReadOnlyList<StrokeData>> preview)
    {
        _device = device; _strokes = strokes; _preview = preview;
    }
    public void InvalidateSlide() { _dirty = true; _pending.Clear(); }
    public void Commit(StrokeData stroke) { if (!_dirty) _pending.Add(stroke); }

    public void Draw(CanvasDrawingSession session, InkViewport viewport)
    {
        if (!viewport.IsValid) return;
        if (_history is null || viewport != _viewport)
        {
            _history?.Dispose(); _history = null; _viewport = viewport;
            ClearGeometries();
            double pixels = (double)viewport.WidthDip * viewport.HeightDip * viewport.Scale * viewport.Scale;
            // Leave room for integer pixel rounding at the budget boundary.
            double reduction = Math.Min(1, Math.Sqrt((CacheBudgetBytes - 128 * 1024) / Math.Max(4, pixels * 4)));
            reduction = Math.Min(reduction, _device.MaximumBitmapSizeInPixels / (Math.Max(viewport.WidthDip, viewport.HeightDip) * viewport.Scale));
            float dpi = (float)(96 * viewport.Scale * reduction);
            _history = new CanvasRenderTarget(_device, viewport.WidthDip, viewport.HeightDip, dpi);
            _dirty = true;
            InkDiagnostics.Write(new { kind = "cache", widthPixels = _history.SizeInPixels.Width, heightPixels = _history.SizeInPixels.Height,
                bytes = (long)_history.SizeInPixels.Width * _history.SizeInPixels.Height * 4, dpi });
        }
        if (_dirty || _pending.Count > 0)
        {
            using var target = _history.CreateDrawingSession();
            if (_dirty)
            {
                target.Clear(default(Color));
                var strokes = _strokes();
                var retained = new HashSet<StrokeData>(strokes);
                var obsolete = new List<StrokeData>();
                foreach (var pair in _geometries)
                    if (!retained.Contains(pair.Key)) { pair.Value.Dispose(); obsolete.Add(pair.Key); }
                foreach (var stroke in obsolete) _geometries.Remove(stroke);
                foreach (var stroke in strokes) DrawStroke(target, stroke, viewport, true);
            }
            else foreach (var stroke in _pending) DrawStroke(target, stroke, viewport, true);
            _pending.Clear(); _dirty = false;
        }
        session.DrawImage(_history);
        foreach (var live in _preview()) DrawStroke(session, live, viewport, false);
        if (EraserPositions.Count > 0) { foreach (var position in EraserPositions) DrawEraser(session, position); }
        else if (EraserPosition is { } position) DrawEraser(session, position);
    }

    private void DrawStroke(CanvasDrawingSession session, StrokeData stroke, InkViewport viewport, bool cache)
    {
        if (stroke.Dots.Count == 0 || stroke.Argb.Length != 4) return;
        var color = Color.FromArgb(stroke.Argb[0], stroke.Argb[1], stroke.Argb[2], stroke.Argb[3]);
        float width = stroke.ThicknessDips;
        if (stroke.Dots.Count == 1)
        {
            session.FillCircle(viewport.ToDip(stroke.Dots[0]), width / 2, color);
            return;
        }
        if (!cache || !_geometries.TryGetValue(stroke, out var geometry))
        {
            using var builder = new CanvasPathBuilder(_device);
            builder.BeginFigure(viewport.ToDip(stroke.Dots[0]));
            for (int i = 1; i < stroke.Dots.Count; i++) builder.AddLine(viewport.ToDip(stroke.Dots[i]));
            builder.EndFigure(CanvasFigureLoop.Open);
            geometry = CanvasGeometry.CreatePath(builder);
            if (cache) _geometries.Add(stroke, geometry);
        }
        try { session.DrawGeometry(geometry, color, width, _style); }
        finally { if (!cache) geometry.Dispose(); }
    }

    private void DrawEraser(CanvasDrawingSession session, Vector2 point)
    {
        var rect = new Rect(point.X - EraserSize.X / 2, point.Y - EraserSize.Y / 2, EraserSize.X, EraserSize.Y);
        var body = IsDark ? Color.FromArgb(240, 40, 40, 40) : Color.FromArgb(247, 255, 255, 255);
        var line = IsDark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 27, 27, 27);
        session.FillRoundedRectangle(rect, 8, 8, body);
        session.DrawRoundedRectangle(rect, 8, 8, line, 1.5f);
        for (int i = 1; i <= 3; i++)
            session.DrawLine(new Vector2(point.X - 28 + 14 * i, point.Y - 20), new Vector2(point.X - 28 + 14 * i, point.Y + 20), line, 2);
    }

    private void ClearGeometries() { foreach (var geometry in _geometries.Values) geometry.Dispose(); _geometries.Clear(); }
    public void Dispose() { _history?.Dispose(); _history = null; _pending.Clear(); ClearGeometries(); _style.Dispose(); }
}
