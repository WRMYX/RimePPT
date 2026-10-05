using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using RimePPT.Core;
namespace RimePPT.Services;
internal static class QuickLaunchService
{
    public static async Task LaunchAsync(QuickLaunchEntry entry)
    {
        if (!File.Exists(entry.Path)) throw new FileNotFoundException("快捷启动文件不存在，请在设置中重新选择。", entry.Path);
        if (string.Equals(Path.GetExtension(entry.Path), ".exe", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(entry.Path)! });
        else if (!await global::Windows.System.Launcher.LaunchFileAsync(await global::Windows.Storage.StorageFile.GetFileFromPathAsync(entry.Path)))
            throw new InvalidOperationException("系统没有打开该文件，请检查默认应用。 ");
    }
}
