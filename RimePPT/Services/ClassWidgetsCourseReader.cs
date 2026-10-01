using System;
using System.IO;
using System.Text.Json;
using RimePPT.Core;

namespace RimePPT.Services;

internal static class ClassWidgetsCourseReader
{
    internal static string? ReadCurrentSubject()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimePPT", "classwidgets-course.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1 || !root.GetProperty("active").GetBoolean()
                || root.GetProperty("entryType").GetString() != "class") return null;
            var updated = root.GetProperty("updatedAtUtc").GetDateTimeOffset();
            var age = DateTimeOffset.UtcNow - updated;
            if (age.TotalSeconds < -5 || age.TotalSeconds > 45) return null;
            string? name = root.GetProperty("subjectName").GetString()?.Trim();
            return string.IsNullOrWhiteSpace(name) || name.Length > 200 ? null : name;
        }
        catch (Exception ex) { CrashReporter.Report(ex, "classwidgets-course"); return null; }
    }
}
