using System;
using System.IO;
using Microsoft.UI.Xaml;
namespace RimePPT.Windows;
public sealed partial class UpdateProgressWindow : Window
{
    private readonly string _log;
    private bool _closed;
    private bool _finished;
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    public UpdateProgressWindow()
    {
        InitializeComponent(); SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop(); Title = "RimePPT · 安装更新"; ExtendsContentIntoTitleBar = true; SetTitleBar(ProgressTitleBar);
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false; presenter.IsResizable = false; presenter.IsMinimizable = true;
            presenter.SetBorderAndTitleBar(true, false);
        }
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); if (!_closed) Close(); };
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(600, 480));
        _log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimePPT", "Updates", "install-" + Guid.NewGuid().ToString("N") + ".txt");
        Closed += (_, _) => { _closed = true; _closeTimer.Stop(); };
    }
    private void OnMinimize(object sender, RoutedEventArgs args) { (AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.Minimize(); }
    private void OnClose(object sender, RoutedEventArgs args) { if (CloseButton.IsEnabled) Close(); }
    public string LogPath => _log;
    public void ShowStatus(string title, string detail, double? percent = null, bool finished = false, bool succeeded = false, bool allowClose = false)
    {
        if (_closed || _finished) return;
        _finished = finished;
        CloseButton.IsEnabled = finished || allowClose;
        StageText.Text = title; DetailText.Text = detail;
        InstallProgress.IsIndeterminate = !finished && percent is null;
        InstallProgress.Value = Math.Clamp(percent ?? 0, 0, 100);
        PercentText.Text = percent is null ? "" : $"{percent:0}%";
        try { Directory.CreateDirectory(Path.GetDirectoryName(_log)!); File.AppendAllText(_log, $"{DateTimeOffset.Now:o} {title} {detail}\n"); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (finished && succeeded)
        {
            InstallProgress.Value = 100; PercentText.Text = "100% · 窗口将在 3 秒后自动关闭";
            _closeTimer.Start();
        }
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
