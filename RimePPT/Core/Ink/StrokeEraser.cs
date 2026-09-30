using System;
using System.Collections.Generic;
using System.Numerics;

namespace RimePPT.Core.Ink;

public readonly record struct EraserSweep(Vector2 From, Vector2 To, float WidthDip = 56, float HeightDip = 72);
public readonly record struct InkEraseResult(bool Changed, InkBounds Bounds);

/// <summary>Clips the displayed polylines against a convex swept rectangular eraser.</summary>
public sealed class StrokeEraser
{
    private readonly Dictionary<StrokeData, InkBounds> _bounds = new();
    private InkViewport _viewport;
    public void Reset() => _bounds.Clear();

    public InkEraseResult EraseSweep(List<StrokeData> strokes, EraserSweep sweep, InkViewport viewport)
    {
        if (!viewport.IsValid) return default;
        if (_viewport != viewport) { Reset(); _viewport = viewport; }
        float hw = sweep.WidthDip / 2, hh = sweep.HeightDip / 2;
        var region = new InkBounds(MathF.Min(sweep.From.X, sweep.To.X) - hw, MathF.Min(sweep.From.Y, sweep.To.Y) - hh,
            MathF.Max(sweep.From.X, sweep.To.X) + hw, MathF.Max(sweep.From.Y, sweep.To.Y) + hh);
        var polygon = Hull(sweep, hw, hh);
        bool changed = false;
        for (int i = strokes.Count - 1; i >= 0; i--)
        {
            var stroke = strokes[i];
            if (!_bounds.TryGetValue(stroke, out var bounds))
                _bounds[stroke] = bounds = StrokeGeometry.GetBounds(stroke, viewport.WidthDip, viewport.HeightDip);
            if (!bounds.Intersects(region) || !IsHit(stroke, polygon, viewport)) continue;
            var pieces = Split(stroke, polygon, viewport);
            strokes.RemoveAt(i); _bounds.Remove(stroke);
            strokes.InsertRange(i, pieces);
            changed = true;
        }
        return new(changed, region);
    }

    private static bool IsHit(StrokeData stroke, List<Vector2> polygon, InkViewport viewport)
    {
        if (stroke.Dots.Count == 0) return false;
        float radius = stroke.ThicknessDips / 2;
        if (stroke.Dots.Count == 1)
        {
            var p = viewport.ToDip(stroke.Dots[0]);
            return Clip(p, p, polygon, radius, out _, out _);
        }
        for (int i = 1; i < stroke.Dots.Count; i++)
            if (Clip(viewport.ToDip(stroke.Dots[i - 1]), viewport.ToDip(stroke.Dots[i]), polygon, radius, out _, out _)) return true;
        return false;
    }

    private static List<StrokeData> Split(StrokeData stroke, List<Vector2> polygon, InkViewport viewport)
    {
        var result = new List<StrokeData>();
        var run = new List<StrokeData.Dot>();
        void Add(Vector2 p)
        {
            var dot = viewport.Normalize(p);
            if (run.Count == 0 || Math.Abs(run[^1].X - dot.X) + Math.Abs(run[^1].Y - dot.Y) > 1e-8) run.Add(dot);
        }
        void Flush()
        {
            if (run.Count > 0)
                result.Add(new StrokeData { SlideIndex = stroke.SlideIndex, Argb = (byte[])stroke.Argb.Clone(), ThicknessDips = stroke.ThicknessDips, Dots = run });
            run = new();
        }
        for (int i = 1; i < stroke.Dots.Count; i++)
        {
            var a = viewport.ToDip(stroke.Dots[i - 1]); var b = viewport.ToDip(stroke.Dots[i]);
            if (!Clip(a, b, polygon, stroke.ThicknessDips / 2, out float enter, out float exit))
            {
                Add(a); Add(b); continue;
            }
            if (enter > 1e-6f) { Add(a); Add(Vector2.Lerp(a, b, enter)); }
            Flush();
            if (exit < 1 - 1e-6f) { Add(Vector2.Lerp(a, b, exit)); Add(b); }
        }
        Flush();
        return result;
    }

    // A half-plane offset includes the stroke radius, so visible edges participate in erasure.
    private static bool Clip(Vector2 a, Vector2 b, List<Vector2> polygon, float radius, out float enter, out float exit)
    {
        enter = 0; exit = 1;
        for (int i = 0; i < polygon.Count; i++)
        {
            Vector2 origin = polygon[i], edge = polygon[(i + 1) % polygon.Count] - origin;
            float offset = radius * edge.Length();
            float fa = Cross(edge, a - origin) + offset, fb = Cross(edge, b - origin) + offset;
            if (fa < 0 && fb < 0) return false;
            if (fa >= 0 && fb >= 0) continue;
            float t = fa / (fa - fb);
            if (fa < 0) enter = MathF.Max(enter, t); else exit = MathF.Min(exit, t);
            if (enter > exit) return false;
        }
        // Tangential contact with an already clipped endpoint must not keep splitting the same stroke.
        return Vector2.DistanceSquared(a, b) < 0.000001f || exit - enter > 1e-6f;
    }

    private static List<Vector2> Hull(EraserSweep sweep, float hw, float hh)
    {
        var points = new List<Vector2>(8);
        foreach (var center in new[] { sweep.From, sweep.To })
        {
            points.Add(center + new Vector2(-hw, -hh)); points.Add(center + new Vector2(hw, -hh));
            points.Add(center + new Vector2(hw, hh)); points.Add(center + new Vector2(-hw, hh));
        }
        points.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var hull = new List<Vector2>(8);
        foreach (var point in points)
        {
            while (hull.Count >= 2 && Cross(hull[^1] - hull[^2], point - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        int lower = hull.Count;
        for (int i = points.Count - 2; i >= 0; i--)
        {
            while (hull.Count > lower && Cross(hull[^1] - hull[^2], points[i] - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(points[i]);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
