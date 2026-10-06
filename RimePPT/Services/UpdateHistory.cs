using System;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace RimePPT.Services;
internal static class UpdateHistory
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimePPT", "Updates", "History");
    public static void Write(string version, string channel, string outcome, string detail, string log = "")
    {
        try { Directory.CreateDirectory(DirectoryPath); File.WriteAllText(Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(new { Version = version, Channel = channel, Time = DateTimeOffset.Now, Outcome = outcome, Detail = detail, Log = log })); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public static string Read()
    {
        if (!Directory.Exists(DirectoryPath)) return "暂无更新记录。";
        return string.Join("\n\n", Directory.GetFiles(DirectoryPath, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(20).Select(path => {
            try { using var doc = JsonDocument.Parse(File.ReadAllText(path)); var r = doc.RootElement;
                return $"{r.GetProperty("Time").GetDateTimeOffset().ToLocalTime():yyyy-MM-dd HH:mm} · {r.GetProperty("Channel").GetString()} · {r.GetProperty("Version").GetString()}\n{r.GetProperty("Outcome").GetString()}：{r.GetProperty("Detail").GetString()}\n日志：{r.GetProperty("Log").GetString()}";
            } catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException or System.Collections.Generic.KeyNotFoundException) { return "无法读取记录：" + Path.GetFileName(path); }
        }));
    }
}
