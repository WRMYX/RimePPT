using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace RimePPT.Core;

/// <summary>Packaged apps use StartupTask; portable apps use HKCU Run.</summary>
public static class Startup
{
    public const string TaskId = "RimePPTStartup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "RimePPT";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Package? CurrentPackage()
    {
        try { return Package.Current; }
        catch (InvalidOperationException) { return null; }
    }
    public static async Task<StartupResult> GetStatusAsync()
    {
        await Gate.WaitAsync();
        try { return await ReadAsync(); }
        finally { Gate.Release(); }
    }
    private static async Task<StartupResult> ReadAsync()
    {
        try
        {
            if (CurrentPackage() is not null) return FromTask((await StartupTask.GetAsync(TaskId)).State);
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var command = key?.GetValue(ValueName) as string;
            if (command is null) return new(StartupStatus.Disabled, "未启用开机自启动。");
            if (!StartupPolicy.MatchesExecutable(command, Environment.ProcessPath))
                return new(StartupStatus.OtherLocation, "自启动项指向其他位置的 RimePPT。开启后将改为当前版本。");
            if (LegacyDisabledByUser()) return new(StartupStatus.DisabledByUser, "Windows 已禁用此启动项，请在系统的启动应用设置中启用。");
            return new(StartupStatus.Enabled, "已为当前程序启用登录自启动。");
        }
        catch (Exception ex) { return Failure(ex); }
    }
    public static async Task<StartupResult> SetEnabledAsync(bool enabled)
    {
        await Gate.WaitAsync();
        try { return await SetCoreAsync(enabled); }
        finally { Gate.Release(); }
    }
    private static async Task<StartupResult> SetCoreAsync(bool enabled)
    {
        try
        {
            if (CurrentPackage() is { } package)
            {
                var task = await StartupTask.GetAsync(TaskId);
                if (enabled)
                {
                    var result = FromTask(await task.RequestEnableAsync());
                    if (result.Enabled) CleanupLegacyRegistration(package);
                    return result;
                }
                if (task.State == StartupTaskState.EnabledByPolicy) return FromTask(task.State);
                task.Disable(); CleanupLegacyRegistration(package);
                return FromTask(task.State);
            }
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{exe}\"");
            else if (StartupPolicy.MatchesExecutable(key.GetValue(ValueName) as string, exe)) key.DeleteValue(ValueName, false);
            return await ReadAsync();
        }
        catch (Exception ex) { return Failure(ex); }
    }
    public static async Task InitializeAsync(AppSettings settings, bool fresh)
    {
        await Gate.WaitAsync();
        try
        {
            var package = CurrentPackage();
            string identity = package?.Id.FamilyName ?? "portable:" + Environment.ProcessPath;
            settings.StartupMigrationCompletedFor ??= new();
            bool migrated = settings.StartupMigrationCompletedFor.Contains(identity);
            if (migrated) return;
            var actual = await ReadAsync();
            using var legacyKey = Registry.CurrentUser.OpenSubKey(RunKey);
            if (package is not null && OwnsLegacyRegistration(package, legacyKey?.GetValue(ValueName) as string) && LegacyDisabledByUser())
                actual = new(StartupStatus.DisabledByUser, "旧启动项已被 Windows 禁用，请在设置中自行选择是否启用新的启动任务。");
            if (StartupPolicy.ShouldInitialize(fresh, package is not null, settings.RunAtStartup, migrated, actual))
                actual = await SetCoreAsync(settings.RunAtStartup);
            if (package is not null && actual.Status != StartupStatus.Error) CleanupLegacyRegistration(package);
            settings.StartupLastError = actual.Status == StartupStatus.Error || actual.Blocked ? actual.Message : "";
            settings.StartupMigrationCompletedFor.Add(identity);
            // Initialization updates registration metadata only, not toolbar settings.
            settings.Persist();
        }
        catch (Exception ex)
        {
            settings.StartupLastError = "自启动初始化失败：" + ex.Message;
            settings.Persist();
            CrashReporter.Report(ex, "startup-initialize");
        }
        finally { Gate.Release(); }
    }
    private static bool LegacyDisabledByUser()
    {
        // Read only: preserve Windows' user-controlled approval state.
        using var key = Registry.CurrentUser.OpenSubKey(ApprovalKey);
        return key?.GetValue(ValueName) is byte[] { Length: >= 4 } data
            && BitConverter.ToUInt32(data, 0) is 3 or 7;
    }
    private static void CleanupLegacyRegistration(Package package)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        string? command = key?.GetValue(ValueName) as string;
        if (OwnsLegacyRegistration(package, command)) key!.DeleteValue(ValueName, false);
    }
    private static bool OwnsLegacyRegistration(Package package, string? command)
    {
        if (StartupPolicy.MatchesExecutable(command, Environment.ProcessPath)) return true;
        if (command is null || command.Length < 3 || command[0] != '"' || command[^1] != '"') return false;
        string exe = command[1..^1];
        if (!StartupPolicy.MatchesExecutable(command, exe) || !string.Equals(Path.GetFileName(exe), "RimePPT.exe", StringComparison.OrdinalIgnoreCase)) return false;
        string? directory = Path.GetDirectoryName(exe);
        string? parent = directory is null ? null : Path.GetDirectoryName(directory);
        string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        string folder = directory is null ? "" : Path.GetFileName(directory);
        return string.Equals(parent, windowsApps, StringComparison.OrdinalIgnoreCase)
            && folder.StartsWith(package.Id.Name + "_", StringComparison.OrdinalIgnoreCase)
            && folder.EndsWith("__" + package.Id.PublisherId, StringComparison.OrdinalIgnoreCase);
    }
    private static StartupResult FromTask(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => new(StartupStatus.Enabled, "已启用 Windows 包自启动任务，应用更新后仍有效。"),
        StartupTaskState.Disabled => new(StartupStatus.Disabled, "未启用开机自启动。"),
        StartupTaskState.DisabledByUser => new(StartupStatus.DisabledByUser, "你已在 Windows 中禁用自启动，请在系统的启动应用设置中重新启用。"),
        StartupTaskState.DisabledByPolicy => new(StartupStatus.DisabledByPolicy, "系统策略禁止自启动，请联系设备管理员。"),
        StartupTaskState.EnabledByPolicy => new(StartupStatus.EnabledByPolicy, "系统策略已强制启用自启动。"),
        _ => new(StartupStatus.Error, "无法识别 Windows 自启动状态。")
    };
    private static StartupResult Failure(Exception ex)
    {
        CrashReporter.Report(ex, "startup-registration");
        return new(StartupStatus.Error, "无法设置或读取自启动状态：" + ex.Message);
    }
}
