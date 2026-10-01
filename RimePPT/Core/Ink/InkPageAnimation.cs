using System;
using System.Collections.Generic;
using System.Numerics;

namespace RimePPT.Core.Ink;

public enum InkPageAnimationMode { None, Fade, Replay }

public readonly record struct InkAnimationFrame(float OutgoingOpacity, float IncomingOpacity, double ReplayProgress, bool IsComplete);

/// <summary>Presentation-only timing; saved strokes and undo history are never modified.</summary>
public sealed class InkPageTransition
{
    public InkPageAnimationMode Mode { get; }
    private readonly double _fade, _replay;
    private readonly bool _outgoing, _incoming;

    public InkPageTransition(InkPageAnimationMode mode, double fadeMs, double replayMs, bool outgoing, bool incoming)
    {
        Mode = Enum.IsDefined(mode) ? mode : InkPageAnimationMode.None;
        _fade = double.IsFinite(fadeMs) ? Math.Clamp(fadeMs, 100, 1000) : 240;
        _replay = double.IsFinite(replayMs) ? Math.Clamp(replayMs, 300, 4000) : 1000;
        _outgoing = outgoing; _incoming = incoming;
    }

    public InkAnimationFrame At(double elapsedMs)
    {
        if (Mode == InkPageAnimationMode.None) return new(0, 1, 1, true);
        double elapsed = double.IsFinite(elapsedMs) ? Math.Max(0, elapsedMs) : 0;
        double fade = Math.Clamp(elapsed / _fade, 0, 1);
        float eased = (float)(fade * fade * (3 - 2 * fade));
        double replay = Mode == InkPageAnimationMode.Replay ? Math.Clamp(elapsed / _replay, 0, 1) : 1;
        double duration = Math.Max(_outgoing ? _fade : 0, _incoming ? (Mode == InkPageAnimationMode.Replay ? _replay : _fade) : 0);
        return new(_outgoing ? 1 - eased : 0, Mode == InkPageAnimationMode.Fade && _incoming ? eased : 1, replay, elapsed >= duration);
    }
}

public readonly record struct InkReplayFrame(int CompletedStrokes, StrokeData? PartialStroke, int VisiblePoints, Vector2? Tail);

/// <summary>Arc-length replay follows the stored pen direction at a constant visual speed.</summary>
public sealed class InkReplayPlan
{
    private readonly StrokeData[] _strokes;
    private readonly double[][] _lengths;
    private readonly double[] _ends;
    private readonly InkViewport _viewport;
    public IReadOnlyList<StrokeData> Strokes => _strokes;
    public double TotalLength { get; }

    public InkReplayPlan(IReadOnlyList<StrokeData> strokes, InkViewport viewport)
    {
        _viewport = viewport;
        _strokes = new StrokeData[strokes.Count]; _lengths = new double[strokes.Count][]; _ends = new double[strokes.Count];
        double total = 0;
        for (int i = 0; i < strokes.Count; i++)
        {
            var stroke = _strokes[i] = strokes[i]; var lengths = _lengths[i] = new double[stroke.Dots.Count];
            for (int j = 1; j < lengths.Length; j++)
                lengths[j] = lengths[j - 1] + Vector2.Distance(viewport.ToDip(stroke.Dots[j - 1]), viewport.ToDip(stroke.Dots[j]));
            total += lengths.Length == 0 ? 0 : Math.Max(lengths[^1], Math.Max(1, stroke.ThicknessDips));
            _ends[i] = total;
        }
        TotalLength = total;
    }

    public InkReplayFrame At(double progress)
    {
        progress = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        if (progress >= 1 || TotalLength <= 0) return new(_strokes.Length, null, 0, null);
        if (progress <= 0) return new(0, null, 0, null);
        double distance = TotalLength * progress;
        int completed = 0;
        while (completed < _ends.Length && _ends[completed] <= distance) completed++;
        if (completed == _strokes.Length) return new(completed, null, 0, null);
        var stroke = _strokes[completed]; var lengths = _lengths[completed];
        double local = distance - (completed == 0 ? 0 : _ends[completed - 1]);
        if (lengths.Length == 0) return new(completed, null, 0, null);
        int end = Array.BinarySearch(lengths, local);
        if (end >= 0) return new(completed, stroke, end + 1, null);
        end = ~end;
        if (end >= lengths.Length) return new(completed, stroke, lengths.Length, null);
        double span = lengths[end] - lengths[end - 1];
        var tail = Vector2.Lerp(_viewport.ToDip(stroke.Dots[end - 1]), _viewport.ToDip(stroke.Dots[end]), (float)((local - lengths[end - 1]) / span));
        return new(completed, stroke, end, tail);
    }
}
