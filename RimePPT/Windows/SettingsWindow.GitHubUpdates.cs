using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RimePPT.Core;
using RimePPT.Services;

namespace RimePPT.Windows;

public sealed partial class SettingsWindow
{
    private readonly GitHubUpdateService _githubService = new();
    private CancellationTokenSource? _githubCancellation;
    private GitHubRelease? _githubRelease;
    private string? _githubPayload;
    private bool _githubBusy;
    private void InitializeGitHubUpdates()
    {
        GitHubVersionText.Text = "已安装版本：" + GitHubUpdateService.InstalledVersion;
        RefreshGitHubSources();
        var channel = GitHubUpdateService.Channel;
        GitHubChannelText.Text = channel switch
        {
            GitHubUpdateChannel.Store => "当前为商店安装版，请使用上方 Microsoft Store 更新。",
            GitHubUpdateChannel.Msix => "当前为非商店 MSIX。下载校验后交由 Windows 安装，证书需要由你手动信任。",
            _ => "当前为便携版。仅查询正式 Release，更新前会备份被替换的程序文件。"
        };
        GitHubCheckButton.IsEnabled = channel != GitHubUpdateChannel.Store;
        GitHubStartupCheck.IsEnabled = channel != GitHubUpdateChannel.Store;
        GitHubStartupCheck.IsOn = AppSettings.Instance.CheckGitHubUpdatesOnStartup;
        if (File.Exists(PortableUpdateInstaller.ResultPath))
        {
            try
            {
                string result = File.ReadAllText(PortableUpdateInstaller.ResultPath);
                bool succeeded = result.StartsWith("Update succeeded.", StringComparison.Ordinal);
                GitHubResult(succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                    succeeded ? "上次更新已完成" : "上次更新未完成", result);
            }
            catch (IOException) { /* 更新进程可能仍在写入结果，下次打开设置时读取。 */ }
        }
    }
    private void OnGitHubStartupCheckChanged(object sender, RoutedEventArgs args)
    {
        if (_suppress) return;
        AppSettings.Instance.CheckGitHubUpdatesOnStartup = GitHubStartupCheck.IsOn;
        AppSettings.Instance.Save();
    }
    private void GitHubResult(InfoBarSeverity severity, string title, string message)
    {
        GitHubStatusTitle.Text = title;
        GitHubUpdateInfo.Severity = severity; GitHubUpdateInfo.Title = title;
        GitHubUpdateInfo.Message = message; GitHubUpdateInfo.IsOpen = true;
    }
    private void GitHubBusy(bool busy)
    {
        _githubBusy = busy;
        GitHubCheckButton.IsEnabled = !busy && GitHubUpdateService.Channel != GitHubUpdateChannel.Store;
        GitHubDownloadButton.IsEnabled = !busy;
        GitHubInstallButton.IsEnabled = !busy;
        GitHubSourcePicker.IsEnabled = GitHubSourceAddress.IsEnabled = !busy;
        GitHubCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnCancelGitHubUpdate(object sender, RoutedEventArgs args) => _githubCancellation?.Cancel();
    private async void OnCheckGitHubUpdates(object sender, RoutedEventArgs args)
    {
        if (_closed || _githubBusy || GitHubUpdateService.Channel == GitHubUpdateChannel.Store) return;
        GitHubBusy(true);
        GitHubStatusTitle.Text = "正在检查更新…";
        _githubRelease = null; _githubPayload = null;
        GitHubDownloadButton.Visibility = GitHubInstallButton.Visibility = Visibility.Collapsed;
        GitHubReleaseNotes.Text = "";
        GitHubProgress.Visibility = Visibility.Visible; GitHubProgress.IsIndeterminate = true;
        using var cancellation = new CancellationTokenSource(); _githubCancellation = cancellation;
        try
        {
            var release = await _githubService.CheckAsync(cancellation.Token);
            if (_closed) return;
            _githubRelease = release;
            GitHubLastChecked.Text = $"上次成功检查：{DateTime.Now:yyyy-MM-dd HH:mm:ss} · 最新版本：{release.Tag}";
            GitHubReleaseNotes.Text = release.Notes;
            GitHubDownloadButton.Content = release.Version == GitHubUpdateService.InstalledVersion ? "重新下载" : "下载更新";
            GitHubDownloadButton.Visibility = release.Asset is not null && release.Version >= GitHubUpdateService.InstalledVersion
                ? Visibility.Visible : Visibility.Collapsed;
            if (release.Version <= GitHubUpdateService.InstalledVersion)
                GitHubResult(InfoBarSeverity.Success, "已是最新版本", $"GitHub 最新正式版本：{release.Tag}。同版本可重新下载；不提供降级安装。");
            else if (release.Asset is null)
                GitHubResult(InfoBarSeverity.Warning, "发现新版本，但没有匹配的安装包", "请在 GitHub 查看适用于当前安装方式与架构的文件。");
            else
            {
                GitHubResult(InfoBarSeverity.Informational, "发现新版本 " + release.Tag,
                    $"发布时间：{release.Published.ToLocalTime():yyyy-MM-dd HH:mm}。下载不会自动安装。");
                GitHubDownloadButton.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { if (!_closed) GitHubResult(InfoBarSeverity.Warning, "检查已取消或超时", "请稍后重试。"); }
        catch (Exception ex) { if (!_closed) GitHubResult(InfoBarSeverity.Error, "无法检查 GitHub 更新", ex.Message); }
        finally { _githubCancellation = null; if (!_closed) { GitHubProgress.Visibility = Visibility.Collapsed; GitHubBusy(false); } }
    }
    private async void OnDownloadGitHubUpdate(object sender, RoutedEventArgs args)
    {
        if (_closed || _githubBusy || _githubRelease?.Asset is not { } asset) return;
        GitHubBusy(true); _githubPayload = null; GitHubStatusTitle.Text = "正在下载更新…";
        GitHubProgress.Visibility = Visibility.Visible; GitHubProgress.IsIndeterminate = false; GitHubProgress.Value = 0;
        using var cancellation = new CancellationTokenSource(); _githubCancellation = cancellation;
        try
        {
            var progress = new Progress<double>(value => DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closed && _githubBusy) GitHubProgress.Value = value;
            }));
            string archive = await _githubService.DownloadAsync(asset, progress, cancellation.Token, AppSettings.Instance.GitHubDownloadSourceId, AppSettings.Instance.GitHubCustomDownloadSource);
            string payload = await Task.Run(() => GitHubUpdateService.Extract(archive, GitHubUpdateService.Channel, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            _githubPayload = payload;
            GitHubResult(InfoBarSeverity.Success, "下载和 SHA-256 校验通过", "请先保存板书并结束放映，再安装更新。");
            GitHubDownloadButton.Visibility = Visibility.Collapsed;
            GitHubInstallButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { if (!_closed) GitHubResult(InfoBarSeverity.Informational, "下载已取消或超时", "可以重新下载。"); }
        catch (Exception ex) { if (!_closed) GitHubResult(InfoBarSeverity.Error, "下载未完成", "当前线路：" + (GitHubSourcePicker.SelectedItem as ComboBoxItem)?.Content + "。可切换线路重试。 " + ex.Message); }
        finally { _githubCancellation = null; if (!_closed) { GitHubProgress.Visibility = Visibility.Collapsed; GitHubBusy(false); } }
    }
    private static bool UpdateBlocked => App.PowerPoint.IsPresenting || App.Wps.IsPresenting || App.Debug.IsPresenting || WhiteboardWindow.IsOpen;
    private async void OnInstallGitHubUpdate(object sender, RoutedEventArgs args)
    {
        if (_closed || _githubBusy || _githubPayload is null || _githubRelease is null) return;
        if (UpdateBlocked) { GitHubResult(InfoBarSeverity.Warning, "请先结束放映和关闭画板", "先保存板书，再安装更新。"); return; }
        GitHubBusy(true);
        UpdateProgressWindow? progressWindow = null;
        try
        {
            bool portable = GitHubUpdateService.Channel == GitHubUpdateChannel.Portable;
            var dialog = new ContentDialog
            {
                XamlRoot = WindowRoot.XamlRoot, RequestedTheme = WindowRoot.ActualTheme,
                Title = "安装 " + _githubRelease.Tag + "？",
                Content = portable ? "RimePPT 将退出并替换程序文件，保留旧文件备份；更新后重新打开设置。请先保存所有板书。"
                    : "将打开安装文件夹。先手动信任附带证书，再双击 MSIX，由 Windows 验证和安装。程序不会自动添加证书信任。",
                PrimaryButtonText = portable ? "退出并更新" : "打开安装文件夹", CloseButtonText = "暂不安装",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closed) return;
            if (UpdateBlocked) { GitHubResult(InfoBarSeverity.Warning, "请先结束放映和关闭画板", "未启动安装。"); return; }
            if (portable)
            {
                await PortableUpdateInstaller.StartAsync(_githubPayload, _githubRelease.Version);
                App.ExitForGitHubUpdate();
            }
            else
            {
                if (Directory.GetFiles(_githubPayload, "*.msix").Length != 1 || Directory.GetFiles(_githubPayload, "*.cer").Length != 1)
                    throw new InvalidDataException("更新包必须包含一个 MSIX 和一个公开证书。");
                progressWindow = new UpdateProgressWindow(); progressWindow.Activate();
                progressWindow.ShowStatus("等待 Windows 安装", "请在安装文件夹中手动信任证书并打开 MSIX。安装进度由 Windows 应用安装程序显示，RimePPT 不会将打开文件夹视为安装成功。", allowClose: true);
                UpdateHistory.Write(_githubRelease.Tag, "GitHub MSIX", "交由 Windows 安装", "已打开安装文件夹，安装结果请查看 Windows 应用安装程序。", progressWindow.LogPath);
                await global::Windows.System.Launcher.LaunchFolderAsync(await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(_githubPayload));
            }
        }
        catch (Exception ex) { if (progressWindow is null) { progressWindow = new UpdateProgressWindow(); progressWindow.Activate(); }
            progressWindow.ShowStatus("无法启动安装", ex.Message, finished: true);
            UpdateHistory.Write(_githubRelease?.Tag ?? "", "GitHub", "error", ex.Message, progressWindow?.LogPath ?? "");
            if (!_closed) GitHubResult(InfoBarSeverity.Error, "无法启动安装", ex.Message); }
        finally { if (!_closed) GitHubBusy(false); }
    }
    private bool _updatingSources;
    private void RefreshGitHubSources()
    {
        _updatingSources = true;
        try {
            var settings = AppSettings.Instance;
            settings.GitHubCustomDownloadSources ??= new();
            GitHubSourcePicker.Items.Clear();
            GitHubSourcePicker.Items.Add(new ComboBoxItem { Content = "GitHub 官方直连", Tag = "direct" });
            GitHubSourcePicker.Items.Add(new ComboBoxItem { Content = "GH-Proxy", Tag = "ghproxy" });
            GitHubSourcePicker.Items.Add(new ComboBoxItem { Content = "GHFast", Tag = "ghfast" });
            GitHubSourcePicker.Items.Add(new ComboBoxItem { Content = "ghproxy.net", Tag = "ghproxynet" });
            foreach (var address in settings.GitHubCustomDownloadSources)
                GitHubSourcePicker.Items.Add(new ComboBoxItem { Content = address, Tag = "custom" });
            GitHubSourcePicker.SelectedIndex = settings.GitHubDownloadSourceId switch { "ghproxy" => 1, "ghfast" => 2, "ghproxynet" => 3, _ => 0 };
            if (settings.GitHubDownloadSourceId == "custom")
                foreach (ComboBoxItem item in GitHubSourcePicker.Items)
                    if (item.Content?.ToString() == settings.GitHubCustomDownloadSource) GitHubSourcePicker.SelectedItem = item;
            GitHubSourceAddress.Text = settings.GitHubCustomDownloadSource;
        } finally { _updatingSources = false; }
    }
    private void OnGitHubSourceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingSources || _suppress || _githubBusy || GitHubSourcePicker.SelectedItem is not ComboBoxItem item) return;
        var settings = AppSettings.Instance;
        settings.GitHubDownloadSourceId = item.Tag.ToString()!;
        if (settings.GitHubDownloadSourceId == "custom")
            GitHubSourceAddress.Text = settings.GitHubCustomDownloadSource = item.Content.ToString()!;
        settings.Save();
    }
    private void OnSaveGitHubSource(object sender, RoutedEventArgs args)
    {
        if (_githubBusy) return;
        try {
            string address = GitHubDownloadSource.NormalizePrefix(GitHubSourceAddress.Text);
            var settings = AppSettings.Instance;
            if (!settings.GitHubCustomDownloadSources.Contains(address)) settings.GitHubCustomDownloadSources.Add(address);
            if (settings.GitHubDownloadSourceId == "custom") settings.GitHubCustomDownloadSources.Remove(settings.GitHubCustomDownloadSource);
            if (!settings.GitHubCustomDownloadSources.Contains(address)) settings.GitHubCustomDownloadSources.Add(address);
            settings.GitHubCustomDownloadSource = address; settings.GitHubDownloadSourceId = "custom";
            settings.Save(); RefreshGitHubSources();
        } catch (ArgumentException ex) { GitHubResult(InfoBarSeverity.Warning, "地址无效", ex.Message); }
    }
    private void OnDeleteGitHubSource(object sender, RoutedEventArgs args)
    {
        if (_githubBusy || AppSettings.Instance.GitHubDownloadSourceId != "custom") return;
        var settings = AppSettings.Instance;
        settings.GitHubCustomDownloadSources.Remove(settings.GitHubCustomDownloadSource);
        settings.GitHubCustomDownloadSource = ""; settings.GitHubDownloadSourceId = "direct";
        settings.Save(); RefreshGitHubSources();
    }
    private void OnUpdateHistoryExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        try { UpdateHistoryText.Text = UpdateHistory.Read(); }
        catch (Exception ex) { UpdateHistoryText.Text = "无法读取更新历史：" + ex.Message; }
    }
    public static void OpenGitHubUpdates()
    {
        Open();
        if (_instance is not { } window) return;
        window.Nav.SelectedItem = window.Nav.FooterMenuItems.OfType<NavigationViewItem>().FirstOrDefault(x => x.Tag?.ToString() == "updates");
        window.OnCheckGitHubUpdates(window, new RoutedEventArgs());
    }
}
