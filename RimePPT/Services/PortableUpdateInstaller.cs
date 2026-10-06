using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using RimePPT.Core;

namespace RimePPT.Services;

internal static class PortableUpdateInstaller
{
    public static string ResultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimePPT", "Updates", "last-update-result.txt");
    public static void Start(string payload, Version version)
    {
        if (GitHubUpdateService.Channel != GitHubUpdateChannel.Portable)
            throw new InvalidOperationException("此安装方式只支持便携版。");
        string target = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (AssemblyName.GetAssemblyName(Path.Combine(payload, "RimePPT.dll")).Version != version)
            throw new InvalidDataException("下载的程序版本与 Release 不一致。");
        string probe = Path.Combine(target, ".rimeppt-update-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, "write check"); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
        string folder = Path.GetDirectoryName(payload)!;
        string helper = Path.Combine(folder, "Update-Portable.ps1");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "Update-Portable.ps1"), helper, true);
        string plan = Path.Combine(folder, "update-plan.json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new
        {
            ParentPid = Environment.ProcessId, Target = target, Payload = payload,
            Backup = Path.Combine(folder, "backup"), NewVersion = version.ToString(4), ResultPath,
            MutexName = @"Local\RimePPT.SingleInstance"
        }));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper, "-Plan", plan })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新进程。");
    }
}
