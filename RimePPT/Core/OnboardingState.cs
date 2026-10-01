using System;
using System.IO;
using System.Text.Json;

namespace RimePPT.Core;

/// <summary>当前用户的工具栏引导状态，重置不修改软件设置。</summary>
internal static class OnboardingState
{
    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimePPT");

    internal static bool ShouldShow()
    {
        string path = Path.Combine(DataDirectory, "onboarding.json");
        if (!File.Exists(path)) return !File.Exists(Path.Combine(DataDirectory, "settings.json"));
        try
        {
            using var record = JsonDocument.Parse(File.ReadAllText(path));
            // 旧版只有 ShownAtUtc，按已展示处理，更新时不主动重放。
            return record.RootElement.TryGetProperty("Pending", out var pending)
                && pending.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex, "onboarding-read");
            return false;
        }
    }

    internal static bool TryReset() => TrySave(new DisplayRecord { Pending = true });

    internal static bool TryMarkShown() => TrySave(new DisplayRecord { ShownAtUtc = DateTime.UtcNow });

    private static bool TrySave(DisplayRecord record)
    {
        string path = Path.Combine(DataDirectory, "onboarding.json");
        string temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(temporary,
                JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex, "onboarding-state");
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) { CrashReporter.Report(ex, "onboarding-temp"); }
        }
    }

    private sealed class DisplayRecord
    {
        public int Version { get; set; } = 2;
        public bool Pending { get; set; }
        public DateTime? ShownAtUtc { get; set; }
    }
}
