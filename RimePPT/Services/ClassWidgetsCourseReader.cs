using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RimePPT.Core;

namespace RimePPT.Services;

internal static class ClassWidgetsCourseReader
{
    internal static string DataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimePPT", "classwidgets-course.json");
    internal sealed record ConnectionStatus(bool Connected, string Host, string Plugin, string Course, string Detail, DateTimeOffset? Updated);
    internal static ConnectionStatus ReadStatus(string? path = null)
    {
        path ??= DataPath;
        if (!File.Exists(path)) return new(false, "尚未收到插件心跳", "未连接", "—", "请启动 ClassWidgets 2，并启用 RimePPT 当前课程联动插件。", null);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path)); var root = document.RootElement;
            var updated = root.GetProperty("updatedAtUtc").GetDateTimeOffset();
            if (root.GetProperty("version").GetInt32() != 1) return new(false, "已收到数据", "数据版本不兼容", "—", "请更新当前课程联动插件。", updated);
            double age = (DateTimeOffset.UtcNow - updated).TotalSeconds;
            if (age < -5 || age > 45) return new(false, "心跳已过期", "连接中断", "—", "检查 ClassWidgets 2 是否运行、插件是否启用，以及系统时间是否正确。", updated);
            if (!root.GetProperty("active").GetBoolean()) return new(false, "已收到停用通知", "已停用", "—", "请在 ClassWidgets 2 中启用插件。", updated);
            string kind = root.GetProperty("entryType").GetString() ?? "unknown";
            string? name = root.GetProperty("subjectName").GetString()?.Trim();
            string course = kind == "class" ? string.IsNullOrWhiteSpace(name) ? "上课中，科目名称未提供" : name : "当前非上课时段";
            return new(true, "运行正常（心跳有效）", "连接正常", course, "当前课程联动可用。插件和 RimePPT 的安装目录不影响此连接。", updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return new(false, "状态暂不可用", "数据读取失败", "—", "插件数据尚未完整写入或格式不正确，请稍后刷新。", null); }
    }
    internal static string? ReadCurrentSubject()
    {
        string path = DataPath;
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
