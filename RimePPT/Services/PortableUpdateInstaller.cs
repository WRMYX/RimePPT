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
    public static async System.Threading.Tasks.Task StartAsync(string payload, Version version)
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
        string updater = Path.Combine(folder, "RimePPT.Updater.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Updater", "RimePPT.Updater.exe"), updater, true);
        string authorization = Path.Combine(folder, "authorized.txt");
        if (File.Exists(authorization)) File.Delete(authorization);
        string ready = Path.Combine(folder, "ready.txt");
        if (File.Exists(ready)) File.Delete(ready);
        string status = Path.Combine(folder, "status.json");
        if (File.Exists(status)) File.Delete(status);
        string plan = Path.Combine(folder, "update-plan.json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new
        {
            ParentPid = Environment.ProcessId, Target = target, Payload = payload,
            Backup = Path.Combine(folder, "backup"), NewVersion = version.ToString(4), ResultPath,
            StatusPath = status, ReadyPath = ready, AuthorizationPath = authorization,
            HistoryPath = Path.Combine(UpdateHistory.DirectoryPath, Guid.NewGuid().ToString("N") + ".json"),
            MutexName = @"Local\RimePPT.SingleInstance"
        }));
        var start = new ProcessStartInfo(updater) { UseShellExecute = false, WorkingDirectory = folder };
        start.ArgumentList.Add(plan);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新进程。");
        for (int i = 0; i < 100; i++) {
            if (File.Exists(ready)) { File.WriteAllText(authorization, "install"); return; }
            if (process.HasExited) break;
            await System.Threading.Tasks.Task.Delay(100);
        }
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        throw new InvalidOperationException("安装窗口未能就绪，RimePPT 未退出。请查看更新窗口中的错误。");
    }
}
