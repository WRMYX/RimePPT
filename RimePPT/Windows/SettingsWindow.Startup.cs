using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RimePPT.Core;

namespace RimePPT.Windows;

public sealed partial class SettingsWindow
{
    private bool _startupBusy;
    private bool _loadingStartup;
    private async System.Threading.Tasks.Task RefreshStartupAsync()
    {
        if (_closed || _startupBusy) return;
        _startupBusy = true; StartupToggle.IsEnabled = false;
        try { ApplyStartupStatus(await Startup.GetStatusAsync()); }
        finally { _startupBusy = false; if (!_closed) StartupToggle.IsEnabled = true; }
    }
    private void ApplyStartupStatus(StartupResult result)
    {
        if (_closed) return;
        _loadingStartup = true;
        try { StartupToggle.IsOn = result.Enabled; }
        finally { _loadingStartup = false; }
        StartupDescription.Text = result.Message;
        string warning = result.Status == StartupStatus.Error || result.Blocked || result.Status == StartupStatus.OtherLocation
            ? result.Message : result.Enabled ? "" : AppSettings.Instance.StartupLastError;
        StartupInfo.Message = warning;
        StartupInfo.Severity = result.Status == StartupStatus.Error ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        StartupInfo.IsOpen = !string.IsNullOrEmpty(warning);
    }
    private async void OnStartupToggled(object sender, RoutedEventArgs args)
    {
        if (_suppress || _loadingStartup || _startupBusy) return;
        bool requested = StartupToggle.IsOn;
        _startupBusy = true; StartupToggle.IsEnabled = false;
        try
        {
            var result = await Startup.SetEnabledAsync(requested);
            var settings = AppSettings.Instance;
            settings.StartupLastError = result.Enabled != requested || result.Status == StartupStatus.Error ? result.Message : "";
            if (result.Status != StartupStatus.Error) settings.RunAtStartup = result.Enabled;
            settings.Save();
            ApplyStartupStatus(result);
        }
        finally { _startupBusy = false; if (!_closed) StartupToggle.IsEnabled = true; }
    }
    private async void OnOpenStartupSettings(object sender, RoutedEventArgs args)
    {
        try
        {
            if (!await global::Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps")))
                throw new InvalidOperationException("Windows 无法打开启动应用设置，请手动打开 设置 → 应用 → 启动。");
        }
        catch (Exception ex) { if (!_closed) { StartupInfo.Message = ex.Message; StartupInfo.IsOpen = true; } }
    }
}
