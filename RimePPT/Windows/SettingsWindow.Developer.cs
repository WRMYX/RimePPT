using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RimePPT.Core;
using RimePPT.Services;
namespace RimePPT.Windows;
public sealed partial class SettingsWindow
{
    private int _developerClicks;
    private DateTime _developerLastClick;
    private bool _developerNoticeShown;
    private void InitializeDeveloperSettings()
    {
        DeveloperNav.Visibility = AppSettings.Instance.DeveloperModeEnabled ? Visibility.Visible : Visibility.Collapsed;
        DevLogo.Tapped += (_, _) =>
        {
            if (DateTime.UtcNow - _developerLastClick > TimeSpan.FromSeconds(3)) _developerClicks = 0;
            _developerLastClick = DateTime.UtcNow;
            if (++_developerClicks < 5) return;
            _developerClicks = 0;
            AppSettings.Instance.DeveloperModeEnabled = true; AppSettings.Instance.Save();
            DeveloperNav.Visibility = Visibility.Visible;
        };
        var tests = new StackPanel { Spacing = 12, Padding = new Thickness(16) };
        tests.Children.Add(new TextBlock { Text = "实验功能", FontSize = 20 });
        var separator = new ToggleSwitch { IsOn = AppSettings.Instance.ExitSeparatorEnabled, OnContent = "", OffContent = "", MinWidth = 0 };
        var separate = new ToggleSwitch { IsOn = AppSettings.Instance.SeparateExitToolbarEnabled, OnContent = "", OffContent = "", MinWidth = 0 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(separator, "退出按钮旁显示分隔线");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(separate, "退出按钮使用独立工具栏");
        bool changing = false;
        void Save()
        {
            var s = AppSettings.Instance; s.ExitSeparatorEnabled = separator.IsOn;
            s.SeparateExitToolbarEnabled = separate.IsOn; s.Save();
        }
        separator.Toggled += (_, _) => { if (changing) return; changing = true; if (separator.IsOn) separate.IsOn = false; Save(); changing = false; };
        separate.Toggled += (_, _) => { if (changing) return; changing = true; if (separate.IsOn) separator.IsOn = false; Save(); changing = false; };
        tests.Children.Add(new RimePPT.Controls.SettingRow { Title = "退出按钮旁显示分隔线", Editor = separator, EditorWidth = 60, RowPadding = new Thickness(0, 8, 0, 8) });
        tests.Children.Add(new RimePPT.Controls.SettingRow { Title = "退出按钮使用独立工具栏", Editor = separate, EditorWidth = 60, RowPadding = new Thickness(0, 8, 0, 8) });
        var exit = new Button { Content = "退出开发者调试", MinHeight = 40 };
        exit.Click += (_, _) =>
        {
            changing = true; separator.IsOn = false; separate.IsOn = false; changing = false;
            var s = AppSettings.Instance; s.DeveloperModeEnabled = false;
            s.ExitSeparatorEnabled = false; s.SeparateExitToolbarEnabled = false; s.Save();
            DeveloperNav.Visibility = Visibility.Collapsed; Nav.SelectedItem = Nav.MenuItems[0]; _developerNoticeShown = false;
        };
        tests.Children.Add(exit);
        var diagnostics = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, MaxHeight = 260, Text = "点击刷新查看首次放映耗时和透明批注层状态。" };
        var refresh = new Button { Content = "刷新放映与透明批注诊断" };
        refresh.Click += (_, _) => diagnostics.Text = PresentationDiagnostics.Snapshot() + "\nPowerPoint: " + App.PowerPoint.ConnectionState;
        var export = new Button { Content = "导出白屏与启动诊断日志" };
        export.Click += (_, _) =>
        {
            try { string path = PresentationDiagnostics.Export(); diagnostics.Text = "已导出：" + path + "\n\n" + PresentationDiagnostics.Snapshot(); }
            catch (Exception ex) { diagnostics.Text = "导出失败：" + ex.Message; CrashReporter.Report(ex, "presentation-diagnostic-export"); }
        };
        tests.Children.Add(new TextBlock { Text = "放映速度与透明批注层诊断", FontSize = 20 });
        tests.Children.Add(refresh); tests.Children.Add(export); tests.Children.Add(diagnostics); ((StackPanel)DebugPage.Content).Children.Add(tests);
    }
    private async void ShowDeveloperNotice()
    {
        if (_developerNoticeShown || !AppSettings.Instance.DeveloperModeEnabled || WindowRoot.XamlRoot is null) return;
        _developerNoticeShown = true;
        try { await new ContentDialog { XamlRoot = WindowRoot.XamlRoot, Title = "恭喜你找到了一个小彩蛋",
            Content = "开发者的调试页面。实验功能默认关闭，可自行开启或退出开发者调试。", CloseButtonText = "知道了" }.ShowAsync(); }
        catch (Exception ex) { CrashReporter.Report(ex, "developer-notice"); }
    }
    private async void OnPreviewUpdateProgress(object sender, RoutedEventArgs e)
    {
        if (!AppSettings.Instance.DeveloperModeEnabled) return;
        try
        {
            string updater = System.IO.Path.Combine(AppContext.BaseDirectory, "Updater", "RimePPT.Updater.exe");
            if (!System.IO.File.Exists(updater)) throw new System.IO.FileNotFoundException("找不到更新进度程序，请使用完整发布测试版。", updater);
            var start = new System.Diagnostics.ProcessStartInfo(updater) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(updater)! };
            start.ArgumentList.Add((sender as Button)?.Tag as string == "error" ? "--preview-error" : "--preview-success");
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("无法打开更新进度测试窗口。");
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex, "update-progress-preview");
            await new ContentDialog { XamlRoot = WindowRoot.XamlRoot, Title = "无法打开测试窗口", Content = ex.Message, CloseButtonText = "知道了" }.ShowAsync();
        }
    }
}
