using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RimePPT.Core.Ink;

public enum InkLineStyle { Solid, Dash, Dot, DashDot, Wave, Highlighter }
public enum InkShape { Freehand, Line, Arrow, Rectangle, Ellipse, Triangle }
public readonly record struct InkDrawingOptions(InkLineStyle Style, InkShape Shape);

public static class InkDrawing
{
    public static StrokeData Apply(StrokeData source, InkViewport viewport, InkDrawingOptions options)
    {
        var points = source.Dots.Select(viewport.ToDip).ToList();
        if (points.Count > 1 && options.Shape != InkShape.Freehand)
        {
            var a = points[0]; var b = points[^1];
            var min = Vector2.Min(a, b); var max = Vector2.Max(a, b);
            points = options.Shape switch
            {
                InkShape.Rectangle => new() { min, new(max.X, min.Y), max, new(min.X, max.Y), min },
                InkShape.Triangle => new() { new((min.X + max.X) / 2, min.Y), max, new(min.X, max.Y), new((min.X + max.X) / 2, min.Y) },
                InkShape.Ellipse => Enumerable.Range(0, 97).Select(i => (min + max) / 2 + (max - min) / 2 * new Vector2(MathF.Cos(i * MathF.Tau / 96), MathF.Sin(i * MathF.Tau / 96))).ToList(),
                InkShape.Arrow => Arrow(a, b, source.ThicknessDips),
                _ => new() { a, b }
            };
        }
        if (options.Style == InkLineStyle.Wave && points.Count > 1) points = Wave(points, source.ThicknessDips);
        return new StrokeData { Id = source.Id, SlideIndex = source.SlideIndex, Argb = (byte[])source.Argb.Clone(),
            ThicknessDips = source.ThicknessDips, LineStyle = options.Style, Dots = points.Select(viewport.Normalize).ToList() };
    }
    private static List<Vector2> Arrow(Vector2 a, Vector2 b, float width)
    {
        var delta = b - a; float length = delta.Length();
        if (length < 1) return new() { a, b };
        var direction = delta / length; var normal = new Vector2(-direction.Y, direction.X);
        float head = Math.Min(length * 0.4f, Math.Max(14, width * 4));
        return new() { a, b, b - direction * head + normal * head / 2, b, b - direction * head - normal * head / 2 };
    }
    private static List<Vector2> Wave(List<Vector2> points, float width)
    {
        var result = new List<Vector2> { points[0] }; float traveled = 0;
        float amplitude = Math.Max(3, width), period = Math.Max(24, width * 8);
        for (int i = 1; i < points.Count && result.Count < 100000; i++)
        {
            var delta = points[i] - points[i - 1]; float length = delta.Length();
            if (length < 0.01f) continue;
            var normal = new Vector2(-delta.Y, delta.X) / length;
            int count = Math.Clamp((int)MathF.Ceiling(length / 2), 1, 8192);
            for (int j = 1; j <= count && result.Count < 100000; j++)
            {
                float t = j / (float)count;
                result.Add(Vector2.Lerp(points[i - 1], points[i], t) + normal * amplitude * MathF.Sin((traveled + length * t) * MathF.Tau / period));
            }
            traveled += length;
        }
        return result;
    }
}
