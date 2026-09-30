using System.Numerics;
using RimePPT.Core.Ink;

// CPU reproduction of backed-up AnnotationWindow interpolation and erase loops; not a GPU benchmark.
static class BaselineAlgorithms
{
    public static List<Vector2> Curve(IReadOnlyList<InkSample> samples, InkViewport viewport)
    {
        if (samples.Count == 0) return new();
        var size = new Vector2(viewport.WidthDip, viewport.HeightDip);
        var dots = new List<Vector2> { samples[0].Position / size };
        double step = Math.Sqrt(Math.Pow(6 / viewport.WidthDip, 2) + Math.Pow(6 / viewport.HeightDip, 2));
        for (int i = 1; i < samples.Count; i++)
        {
            var dot = samples[i].Position / size; var last = dots[^1]; double dist = Vector2.Distance(dot, last);
            int segments = dist <= step ? 1 : (int)Math.Ceiling(dist / step);
            for (int s = 1; s <= segments; s++) dots.Add(Vector2.Lerp(last, dot, (float)s / segments));
        }
        var points = dots.Select(d => d * size).ToArray();
        var result = new List<Vector2> { points[0] };
        for (int i = 1; i < points.Length; i++)
        {
            var p0 = points[Math.Max(0, i - 2)]; var p1 = points[i - 1]; var p2 = points[i]; var p3 = points[Math.Min(points.Length - 1, i + 1)];
            var c1 = p1 + (p2 - p0) / 6; var c2 = p2 - (p3 - p1) / 6;
            for (int s = 1; s <= 3; s++)
            {
                float t = s / 3f, u = 1 - t;
                result.Add(u * u * u * p1 + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * p2);
            }
        }
        return result;
    }
    public static void EraseNoHit(List<StrokeData> strokes, Vector2 center, InkViewport viewport)
    {
        double x = center.X / viewport.WidthDip, y = center.Y / viewport.HeightDip;
        foreach (var stroke in strokes)
        {
            var runs = new List<List<StrokeData.Dot>>(); var current = new List<StrokeData.Dot>();
            foreach (var dot in stroke.Dots)
            {
                if (Math.Abs(dot.X - x) <= 28 / viewport.WidthDip && Math.Abs(dot.Y - y) <= 36 / viewport.HeightDip)
                { if (current.Count > 0) { runs.Add(current); current = new(); } }
                else current.Add(dot);
            }
            if (current.Count > 0) runs.Add(current);
            GC.KeepAlive(runs);
        }
    }
}
