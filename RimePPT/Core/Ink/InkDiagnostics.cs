using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimePPT.Core.Ink;

/// <summary>Opt-in summaries; no document names or coordinates. Never writes on a move/draw callback.</summary>
public sealed class InkDiagnostics
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("RIMEPPT_INK_DIAGNOSTICS") == "1";
    public static string LogPath => Path.Combine(Path.GetTempPath(), "rimeppt_ink.jsonl");
    private static readonly object LogLock = new();
    private long _started, _inputTicks, _drawTicks, _allocated;
    private int _samples, _draws;
    private InkDevice _device;
    private uint _pointer;
    private int _slide, _changes;
    private InkTool _tool;
    public void Begin(uint pointer, InkDevice device, bool captured, int slide, InkTool tool)
    {
        if (!Enabled) return;
        _started = Stopwatch.GetTimestamp(); _inputTicks = _drawTicks = 0; _samples = _draws = 0;
        _allocated = GC.GetAllocatedBytesForCurrentThread(); _device = device; _pointer = pointer;
        _slide = slide; _tool = tool; _changes = 0;
        Write(new { kind = "begin", pointer, device = device.ToString(), captured, slide, tool = tool.ToString() });
    }
    public void Input(long start, int samples)
    {
        if (!Enabled) return;
        _inputTicks += Stopwatch.GetTimestamp() - start; _samples += samples;
    }
    public void Draw(long start) { if (Enabled) { _drawTicks += Stopwatch.GetTimestamp() - start; _draws++; } }
    public void Changed() { if (Enabled) _changes++; }
    public void End(string reason, int dots)
    {
        if (!Enabled) return;
        Write(new { kind = "end", pointer = _pointer, device = _device.ToString(), reason, samples = _samples, dots,
            slide = _slide, tool = _tool.ToString(), changes = _changes,
            inputMs = _inputTicks * 1000d / Stopwatch.Frequency, drawMs = _drawTicks * 1000d / Stopwatch.Frequency, draws = _draws,
            durationMs = (Stopwatch.GetTimestamp() - _started) * 1000d / Stopwatch.Frequency,
            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - _allocated });
    }
    public static void Write(object record)
    {
        if (!Enabled) return;
        string line = JsonSerializer.Serialize(record);
        _ = Task.Run(() => { lock (LogLock) { try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { } } });
    }
}
