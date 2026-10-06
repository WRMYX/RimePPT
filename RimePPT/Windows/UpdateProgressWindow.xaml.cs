using System;
using System.IO;
using Microsoft.UI.Xaml;
namespace RimePPT.Windows;
public sealed partial class UpdateProgressWindow : Window
{
    private readonly string _log;
    private bool _closed;
    public UpdateProgressWindow()
    {
        InitializeComponent(); Title = "RimePPT · 安装更新";
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(600, 440));
        _log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimePPT", "Updates", "install-" + Guid.NewGuid().ToString("N") + ".txt");
        Closed += (_, _) => _closed = true;
    }
    public string LogPath => _log;
    public void ShowStatus(string title, string detail, double? percent = null, bool finished = false)
    {
        if (_closed) return;
        StageText.Text = title; DetailText.Text = detail;
        InstallProgress.IsIndeterminate = !finished && percent is null;
        InstallProgress.Value = Math.Clamp(percent ?? 0, 0, 100);
        PercentText.Text = percent is null ? "" : $"{percent:0}%";
        try { Directory.CreateDirectory(Path.GetDirectoryName(_log)!); File.AppendAllText(_log, $"{DateTimeOffset.Now:o} {title} {detail}\n"); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private void OnCopyDetails(object sender, RoutedEventArgs args)
    {
        var content = new global::Windows.ApplicationModel.DataTransfer.DataPackage(); content.SetText(StageText.Text + "\n" + DetailText.Text);
        global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(content);
    }
    private async void OnOpenLog(object sender, RoutedEventArgs args)
    {
        try { if (File.Exists(_log)) await global::Windows.System.Launcher.LaunchFileAsync(await global::Windows.Storage.StorageFile.GetFileFromPathAsync(_log)); }
        catch (Exception ex) { DetailText.Text = "无法打开日志：" + ex.Message; }
    }
}
