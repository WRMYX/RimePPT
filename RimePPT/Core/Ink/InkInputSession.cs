using System;
using System.Collections.Generic;
using System.Numerics;

namespace RimePPT.Core.Ink;

/// <summary>Single contact state machine. Mutable preview never escapes into the saved document.</summary>
public sealed class InkInputSession
{
    private readonly List<InkSample> _samples = new();
    private readonly List<Vector2> _segment = new();
    private int _stableCount;
    public InkViewport Viewport { get; set; }
    public uint? PointerId { get; private set; }
    public InkToolSnapshot Tool { get; private set; }
    public StrokeData? Preview { get; private set; }
    public Vector2? LastPosition => _samples.Count > 0 ? _samples[^1].Position : null;
    public int SampleCount => _samples.Count;
    public ulong LastTimestamp => _samples.Count > 0 ? _samples[^1].Timestamp : 0;

    public bool Begin(uint pointerId, InkSample sample, InkToolSnapshot tool)
    {
        if (PointerId.HasValue || !Viewport.IsValid || !IsFinite(sample.Position)) return false;
        Tool = tool with { Argb = (byte[])tool.Argb.Clone() };
        PointerId = pointerId;
        _samples.Add(sample);
        if (tool.Tool == InkTool.Pen)
        {
            Preview = new StrokeData { SlideIndex = tool.SlideIndex, Argb = Tool.Argb, ThicknessDips = tool.ThicknessDip };
            Preview.Dots.Add(Viewport.Normalize(sample.Position));
            _stableCount = 1;
        }
        return true;
    }

    public void Move(uint pointerId, IReadOnlyList<InkSample> samples)
    {
        if (PointerId != pointerId) return;
        if (Preview is not null && Preview.Dots.Count > _stableCount)
            Preview.Dots.RemoveRange(_stableCount, Preview.Dots.Count - _stableCount);
        foreach (var sample in samples)
        {
            if (!IsFinite(sample.Position) || sample.Timestamp < _samples[^1].Timestamp ||
                Vector2.DistanceSquared(sample.Position, _samples[^1].Position) < 0.0025f) continue;
            _samples.Add(sample);
            if (Preview is not null && _samples.Count >= 3)
            {
                Append(_samples.Count - 3);
                _stableCount = Preview.Dots.Count;
            }
        }
        if (Preview is not null && _samples.Count >= 2) Append(_samples.Count - 2);
    }

    private void Append(int segmentIndex)
    {
        _segment.Clear();
        StrokeGeometry.AppendSegment(_samples, segmentIndex, _segment);
        foreach (var point in _segment) Preview!.Dots.Add(Viewport.Normalize(point));
    }

    public StrokeData? End(uint pointerId, InkSample? finalSample)
    {
        if (PointerId != pointerId) return null;
        if (finalSample is { } sample) Move(pointerId, new[] { sample });
        var committed = Preview;
        Cancel();
        return committed;
    }

    public void Cancel()
    {
        PointerId = null; Preview = null; _samples.Clear(); _segment.Clear(); _stableCount = 0;
    }

    private static bool IsFinite(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
}
