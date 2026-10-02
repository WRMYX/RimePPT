using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RimePPT.Core.Ink;

/// <summary>仅整理足够大的直线、闭合矩形及圆，无法可靠识别时保持原始笔迹。</summary>
public static class SmartShapeRecognizer
{
    public static StrokeData? Recognize(StrokeData stroke, InkViewport viewport)
    {
        if (!viewport.IsValid || stroke.Dots.Count < 6) return null;
        var points = stroke.Dots.Select(viewport.ToDip).ToArray();
        if (points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y))) return null;
        float length = 0;
        for (int i = 1; i < points.Length; i++) length += Vector2.Distance(points[i - 1], points[i]);
        float chord = Vector2.Distance(points[0], points[^1]);
        List<Vector2>? result = null;
        if (chord >= 48 && length > 0 && chord / length > 0.94f)
        {
            var axis = (points[^1] - points[0]) / chord;
            float error = points.Max(p => MathF.Abs((p.X - points[0].X) * axis.Y - (p.Y - points[0].Y) * axis.X));
            if (error <= MathF.Max(4, chord * 0.025f)) result = new() { points[0], points[^1] };
        }
        if (result is null)
        {
            float left = points.Min(p => p.X), right = points.Max(p => p.X);
            float top = points.Min(p => p.Y), bottom = points.Max(p => p.Y);
            float width = right - left, height = bottom - top;
            if (width < 48 || height < 48 || chord > MathF.Min(width, height) * 0.2f) return null;
            float tolerance = MathF.Min(width, height) * 0.08f;
            var corners = new[] { new Vector2(left, top), new Vector2(right, top), new Vector2(right, bottom), new Vector2(left, bottom) };
            bool rectangle = points.All(p => MathF.Min(MathF.Min(p.X - left, right - p.X), MathF.Min(p.Y - top, bottom - p.Y)) <= tolerance)
                && corners.All(c => points.Any(p => Vector2.Distance(c, p) <= tolerance * 2));
            if (rectangle)
            {
                // 保持与书写起点及方向相近的角点顺序，便于翻页笔迹重播。
                int start = Enumerable.Range(0, 4).MinBy(i => Vector2.Distance(points[0], corners[i]));
                int next = Vector2.Distance(points[Math.Min(points.Length - 1, points.Length / 4)], corners[(start + 1) % 4])
                    < Vector2.Distance(points[Math.Min(points.Length - 1, points.Length / 4)], corners[(start + 3) % 4]) ? 1 : -1;
                result = Enumerable.Range(0, 5).Select(i => corners[(start + next * i + 8) % 4]).ToList();
            }
            else
            {
                if (width / height is < 0.8f or > 1.25f) return null;
                var center = new Vector2((left + right) / 2, (top + bottom) / 2);
                float radius = (width + height) / 4;
                double radialError = points.Average(p => Math.Pow((Vector2.Distance(p, center) - radius) / radius, 2));
                if (radialError > 0.012 || length < radius * 5 || length > radius * 7.8) return null;
                float angle = MathF.Atan2(points[0].Y - center.Y, points[0].X - center.X);
                float signedArea = 0;
                for (int i = 1; i < points.Length; i++) signedArea += (points[i - 1].X - center.X) * (points[i].Y - center.Y) - (points[i].X - center.X) * (points[i - 1].Y - center.Y);
                float direction = signedArea < 0 ? -1 : 1;
                result = Enumerable.Range(0, 65).Select(i => center + radius * new Vector2(MathF.Cos(angle + direction * i * MathF.Tau / 64), MathF.Sin(angle + direction * i * MathF.Tau / 64))).ToList();
            }
        }
        return result is null ? null : new StrokeData { Id = stroke.Id, SlideIndex = stroke.SlideIndex, Argb = (byte[])stroke.Argb.Clone(), ThicknessDips = stroke.ThicknessDips, Dots = result.Select(viewport.Normalize).ToList() };
    }
}
