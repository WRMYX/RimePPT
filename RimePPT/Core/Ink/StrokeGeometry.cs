using System;
using System.Collections.Generic;
using System.Numerics;

namespace RimePPT.Core.Ink;

public enum InkDevice { Mouse, Touch, Pen }
public enum InkTool { Pen, Eraser }
public readonly record struct InkSample(Vector2 Position, ulong Timestamp, InkDevice Device);
public readonly record struct InkToolSnapshot(InkTool Tool, int SlideIndex, byte[] Argb, float ThicknessDip);
public readonly record struct InkViewport(float WidthDip, float HeightDip, float Scale = 1)
{
    public bool IsValid => WidthDip > 0 && HeightDip > 0 && float.IsFinite(WidthDip) && float.IsFinite(HeightDip);
    public Vector2 ToDip(StrokeData.Dot dot) => new((float)dot.X * WidthDip, (float)dot.Y * HeightDip);
    public StrokeData.Dot Normalize(Vector2 point) => new()
    {
        X = Math.Clamp(point.X / WidthDip, 0, 1), Y = Math.Clamp(point.Y / HeightDip, 0, 1),
    };
}
public readonly record struct InkBounds(float Left, float Top, float Right, float Bottom)
{
    public bool Intersects(InkBounds other) => Left <= other.Right && Right >= other.Left && Top <= other.Bottom && Bottom >= other.Top;
}

/// <summary>Geometry is evaluated in DIP, independently of WinUI and monitor scaling.</summary>
public static class StrokeGeometry
{
    public const float DefaultToleranceDip = 0.5f;

    public static List<Vector2> BuildCurve(IReadOnlyList<InkSample> samples, float toleranceDip = DefaultToleranceDip)
    {
        var result = new List<Vector2>();
        if (samples.Count == 0) return result;
        result.Add(samples[0].Position);
        for (int i = 0; i < samples.Count - 1; i++) AppendSegment(samples, i, result, toleranceDip);
        return result;
    }

    // Only a segment with a known next sample is stable; the final segment is a preview.
    public static void AppendSegment(IReadOnlyList<InkSample> samples, int index, List<Vector2> output, float toleranceDip = DefaultToleranceDip)
    {
        Vector2 p1 = samples[index].Position, p2 = samples[index + 1].Position;
        if (Vector2.DistanceSquared(p1, p2) < 0.000001f) return;
        Vector2 p0 = index > 0 ? samples[index - 1].Position : 2 * p1 - p2;
        Vector2 p3 = index + 2 < samples.Count ? samples[index + 2].Position : 2 * p2 - p1;
        float d01 = MathF.Max(0.001f, MathF.Sqrt(Vector2.Distance(p0, p1)));
        float d12 = MathF.Max(0.001f, MathF.Sqrt(Vector2.Distance(p1, p2)));
        float d23 = MathF.Max(0.001f, MathF.Sqrt(Vector2.Distance(p2, p3)));
        Vector2 m1 = p2 - p1 + d12 * ((p1 - p0) / d01 - (p2 - p0) / (d01 + d12));
        Vector2 m2 = p2 - p1 + d12 * ((p3 - p2) / d23 - (p3 - p1) / (d12 + d23));
        Flatten(p1, p1 + m1 / 3, p2 - m2 / 3, p2, MathF.Max(0.05f, toleranceDip), 0, output);
    }

    private static void Flatten(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float tolerance, int depth, List<Vector2> output)
    {
        if (depth >= 12 || (DistanceToSegment(b, a, d) <= tolerance && DistanceToSegment(c, a, d) <= tolerance))
        {
            output.Add(d);
            return;
        }
        Vector2 ab = (a + b) / 2, bc = (b + c) / 2, cd = (c + d) / 2;
        Vector2 abc = (ab + bc) / 2, bcd = (bc + cd) / 2, mid = (abc + bcd) / 2;
        Flatten(a, ab, abc, mid, tolerance, depth + 1, output);
        Flatten(mid, bcd, cd, d, tolerance, depth + 1, output);
    }

    public static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 delta = b - a;
        float length = delta.LengthSquared();
        float t = length > 0 ? Math.Clamp(Vector2.Dot(point - a, delta) / length, 0, 1) : 0;
        return Vector2.Distance(point, a + t * delta);
    }

    public static InkBounds GetBounds(StrokeData stroke, float widthDip, float heightDip)
    {
        float left = float.PositiveInfinity, top = left, right = float.NegativeInfinity, bottom = right;
        foreach (var dot in stroke.Dots)
        {
            float x = (float)dot.X * widthDip, y = (float)dot.Y * heightDip;
            left = MathF.Min(left, x); top = MathF.Min(top, y); right = MathF.Max(right, x); bottom = MathF.Max(bottom, y);
        }
        float radius = stroke.ThicknessDips / 2;
        return new(left - radius, top - radius, right + radius, bottom + radius);
    }
}
