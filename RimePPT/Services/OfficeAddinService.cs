using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace RimePPT.Services;

public sealed record OfficeAddinStatus(bool Registered, bool PowerPointRunning, bool PayloadAvailable,
    bool RecommendAllUsers, string Registration, string InstalledPath);

public static class OfficeAddinService
{
    private const string AddinKey = @"Software\Microsoft\Office\PowerPoint\Addins\RimePPT.PowerPointAddin";
    private const string ServerKey = @"Software\Classes\CLSID\{66E789D4-F1AF-4C5D-8F8B-7C4A560EE82C}\InprocServer32";
    private static string PayloadDirectory => Path.Combine(AppContext.BaseDirectory, "OfficeAddin");

    public static OfficeAddinStatus ReadStatus()
    {
        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        bool running = false;
        foreach (var process in Process.GetProcessesByName("POWERPNT")) { running = true; process.Dispose(); }
        bool registered = false;
        string registration = "尚未注册", path = "";
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            using var root = RegistryKey.OpenBaseKey(hive, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
            using var office = root.OpenSubKey(AddinKey);
            using var server = root.OpenSubKey(ServerKey);
            if (office is null || server is null) continue;
            string codebase = server.GetValue("CodeBase") as string ?? "";
            bool validPath = Uri.TryCreate(codebase, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath);
            if (!validPath) { registration = "注册信息存在，但加载项文件缺失。"; continue; }
            if (elevated && hive == RegistryHive.CurrentUser)
            {
                if (!registered) registration = "仅注册了当前用户；当前管理员运行环境需要全机注册。";
                continue;
            }
            registered = true;
            path = uri!.LocalPath;
            registration = (hive == RegistryHive.CurrentUser ? "已注册：当前用户" : "已注册：所有用户") +
                (Convert.ToInt32(office.GetValue("LoadBehavior", 0)) == 3 ? "（启动时加载）" : "（未设置为启动时加载，请检查 PowerPoint 加载项）");
        }
        bool payload = File.Exists(Path.Combine(PayloadDirectory, "RimePPT.OfficeAddin.dll")) &&
            File.Exists(Path.Combine(PayloadDirectory, "Register-OfficeAddin.ps1"));
        return new(registered, running, payload, elevated, registration, path);
    }

    public static async Task RegisterAsync(bool allUsers, bool uninstall)
    {
        var status = ReadStatus();
        if (status.PowerPointRunning) throw new InvalidOperationException("请先关闭全部 PowerPoint 窗口，再注册或卸载加载项。");
        if (!status.PayloadAvailable) throw new FileNotFoundException("缺少 Office 加载项组件，请使用完整的新版 RimePPT。");
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = PayloadDirectory,
        };
        if (allUsers) start.Verb = "runas";
        foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(PayloadDirectory, "Register-OfficeAddin.ps1") })
            start.ArgumentList.Add(argument);
        if (allUsers) start.ArgumentList.Add("-AllUsers");
        if (uninstall) start.ArgumentList.Add("-Uninstall");
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动加载项注册程序。");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("注册程序未成功完成，请确认 PowerPoint 已关闭并且具有所需权限。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new OperationCanceledException("已取消管理员授权，未完成注册操作。", ex); }
    }
}
