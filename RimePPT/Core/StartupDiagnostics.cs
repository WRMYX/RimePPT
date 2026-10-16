using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;

namespace RimePPT.Core;

internal static class StartupDiagnostics
{
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly DateTime StartedAt = GetStartedAt();
    private static long _previous;
    private static int _writerScheduled;
    private static DateTime GetStartedAt()
    {
        try { using var process = Process.GetCurrentProcess(); return process.StartTime; }
        catch { return DateTime.Now; }
    }
    internal static void Mark(string stage)
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Exchange(ref _previous, now);
            double elapsed = (DateTime.Now - StartedAt).TotalMilliseconds;
            double delta = previous == 0 ? 0 : Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
            Pending.Enqueue($"{DateTimeOffset.Now:O} [pid{Environment.ProcessId}] {stage}; process={elapsed:F0}ms; interval={delta:F0}ms");
            if (Interlocked.CompareExchange(ref _writerScheduled, 1, 0) == 0)
                ThreadPool.QueueUserWorkItem(_ => Drain());
        }
        catch { /* Diagnostics must not prevent startup. */ }
    }

    private static void Drain()
    {
        try
        {
            using var writer = new StreamWriter(Path.Combine(Path.GetTempPath(), "rimeppt_startup.log"), append: true);
            while (Pending.TryDequeue(out var line)) writer.WriteLine(line);
        }
        catch
        {
            // A locked/unwritable log must not create an endless ThreadPool retry loop.
            while (Pending.TryDequeue(out _)) { }
        }
        finally
        {
            Interlocked.Exchange(ref _writerScheduled, 0);
            if (!Pending.IsEmpty && Interlocked.CompareExchange(ref _writerScheduled, 1, 0) == 0)
                ThreadPool.QueueUserWorkItem(_ => Drain());
        }
    }

    internal static void RecordIdentity() => Mark($"identity; path={Environment.ProcessPath}; runtime={Environment.Version}; " +
        $"winui={typeof(Microsoft.UI.Xaml.Application).Assembly.GetName().Version}; " +
        $"tray={typeof(H.NotifyIcon.TaskbarIcon).Assembly.GetName().Version}; build={typeof(StartupDiagnostics).Assembly.GetName().Version}");
}
