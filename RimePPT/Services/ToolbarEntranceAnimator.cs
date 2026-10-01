using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RimePPT.Core;
using Windows.Graphics;

namespace RimePPT.Services;

public static class ToolbarEntranceAnimator
{
    private sealed record Motion(Window Window, PointInt32 Start, PointInt32 End, long Started, double Duration = 220, TaskCompletionSource? Completion = null);
    private static readonly Dictionary<Window, Motion> Motions = new();
    private static readonly Dictionary<Window, TaskCompletionSource> Entrances = new();
    internal static Task WaitForEntranceAsync(Window window) => Entrances.TryGetValue(window, out var completion)
        ? completion.Task : Task.CompletedTask;
    private static readonly DispatcherTimer Timer = CreateTimer();
    private static DispatcherTimer CreateTimer() { var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; timer.Tick += Frame; return timer; }
    public static void Start(Window window, ToolbarLayout layout, RectInt32 bounds, PointInt32 end)
    {
        Cancel(window);
        if (!new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled) { window.AppWindow.Move(end); return; }
        Entrances[window] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var size = window.AppWindow.Size;
        var start = layout switch
        {
            ToolbarLayout.LeftRail => new PointInt32(bounds.X - size.Width, end.Y),
            ToolbarLayout.RightRail => new PointInt32(bounds.X + bounds.Width, end.Y),
            _ => new PointInt32(end.X, bounds.Y + bounds.Height)
        };
        window.AppWindow.Move(start); Motions[window] = new(window, start, end, 0, 300);
        window.Closed += (_, _) => Cancel(window);
        Timer.Start();
    }
    public static bool Retarget(Window window, PointInt32 end)
    {
        if (!Motions.TryGetValue(window, out var motion)) return false;
        if (motion.Completion is not null) return true;
        Motions[window] = motion with { End = end }; return true;
    }
    public static void Cancel(Window window)
    {
        if (Entrances.Remove(window, out var entrance)) entrance.TrySetResult();
        if (Motions.Remove(window, out var motion)) motion.Completion?.TrySetResult();
        if (Motions.Count == 0) Timer.Stop();
    }
    public static Task ExitAsync(Window window, ToolbarLayout layout, RectInt32 bounds)
    {
        Cancel(window);
        if (!new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return Task.CompletedTask;
        var start = window.AppWindow.Position;
        var end = layout switch
        {
            ToolbarLayout.LeftRail => new PointInt32(bounds.X - window.AppWindow.Size.Width, start.Y),
            ToolbarLayout.RightRail => new PointInt32(bounds.X + bounds.Width, start.Y),
            _ => new PointInt32(start.X, bounds.Y + bounds.Height)
        };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Motions[window] = new(window, start, end, Stopwatch.GetTimestamp(), 180, completion);
        window.Closed += (_, _) => Cancel(window);
        Timer.Start();
        return completion.Task;
    }
    private static void Frame(object? sender, object args)
    {
        foreach (var motion in new List<Motion>(Motions.Values))
        {
            if (motion.Started == 0)
            {
                Motions[motion.Window] = motion with { Started = Stopwatch.GetTimestamp() };
                continue;
            }
            double t = Math.Clamp(Stopwatch.GetElapsedTime(motion.Started).TotalMilliseconds / motion.Duration, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3);
            motion.Window.AppWindow.Move(new((int)Math.Round(motion.Start.X + (motion.End.X - motion.Start.X) * eased), (int)Math.Round(motion.Start.Y + (motion.End.Y - motion.Start.Y) * eased)));
            if (t >= 1) Cancel(motion.Window);
        }
    }
}
