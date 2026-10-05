using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RimePPT.Core;
using RimePPT.Services;

namespace RimePPT.Windows;
public sealed partial class SettingsWindow
{
    private readonly StackPanel _launcherRows = new() { Spacing = 8 };
    private void InitializeFeatureSettings()
    {
        var host = (StackPanel)ToolbarPage.Content;
        var features = new StackPanel { Spacing = 12, Padding = new Thickness(20) };
        features.Children.Add(new TextBlock { Text = "工具栏功能", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        InitializeToolbarEditor(features);
        host.Children.Add(new Border { Style = (Style)PageHost.Resources["CardBorder"], Child = features });
        var menuSettings = new StackPanel { Spacing = 12, Padding = new Thickness(20) };
        menuSettings.Children.Add(new TextBlock { Text = "工具菜单中的功能", FontSize = 20 });
        menuSettings.Children.Add(new TextBlock { Text = "控制展开工具菜单后显示的项目，不影响各位置的独立按钮。", TextWrapping = TextWrapping.Wrap });
        foreach (var item in new[] { ("spotlight","聚光与放大"), ("blackout","黑屏模式"), ("timer","计时器"), ("export","导出课堂板书"), ("whiteboard","独立画板"), ("launcher","快捷启动"), ("guide","使用指南") })
        {
            var box = new ToggleSwitch { Header = item.Item2, IsOn = AppSettings.Instance.IsToolVisible(item.Item1), MinHeight = 48 };
            box.Toggled += (_, _) =>
            {
                var settings = AppSettings.Instance;
                settings.VisibleToolItems ??= new HashSet<string> { "spotlight", "blackout", "timer", "export", "whiteboard", "launcher", "guide" };
                if (box.IsOn) settings.VisibleToolItems.Add(item.Item1); else settings.VisibleToolItems.Remove(item.Item1);
                settings.Save();
            };
            menuSettings.Children.Add(box);
        }
        host.Children.Add(new Border { Style = (Style)PageHost.Resources["CardBorder"], Child = menuSettings });
        var launchers = new StackPanel { Spacing = 12, Padding = new Thickness(20) };
        launchers.Children.Add(new TextBlock { Text = "应用与文件快捷启动", FontSize = 20 });
        launchers.Children.Add(new TextBlock { Text = "添加常用应用或文件，可修改显示名称并固定到指定工具栏；未固定的项目仍可从快捷启动菜单打开。", TextWrapping = TextWrapping.Wrap });
        var add = new Button { Content = "添加应用或文件", MinHeight = 40 };
        add.Click += OnAddLauncher;
        launchers.Children.Add(add); launchers.Children.Add(_launcherRows);
        host.Children.Add(new Border { Style = (Style)PageHost.Resources["CardBorder"], Child = launchers });
        RefreshLauncherRows();
        var whiteboard = new Button { Content = "打开独立画板", MinHeight = 40 };
        whiteboard.Click += (_, _) => WhiteboardWindow.Open();
        var board = new StackPanel { Spacing = 8, Padding = new Thickness(20) };
        board.Children.Add(new TextBlock { Text = "独立画板", FontSize = 20 });
        board.Children.Add(new TextBlock { Text = "打开全屏画板，使用浮动工具栏书写、擦除和导出。", TextWrapping = TextWrapping.Wrap });
        board.Children.Add(whiteboard);
        host.Children.Add(new Border { Style = (Style)PageHost.Resources["CardBorder"], Child = board });
        var wps = new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
            Title = "WPS Windows 适配", Message = "已加入 WPS COM 放映适配与自研批注。页码、预览等能力取决于 WPS 版本和 COM 接口可用性；目前尚未完成 WPS 实机验证。" };
        ((StackPanel)AppearancePage.Content).Children.Add(wps);
    }
    private async void OnAddLauncher(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new global::Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync(); if (file is null || _closed) return;
            if (!AppSettings.Instance.QuickLaunchEntries.Any(x => string.Equals(x.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                AppSettings.Instance.QuickLaunchEntries.Add(new() { Name = file.DisplayName, Path = file.Path });
            AppSettings.Instance.Save(); RefreshLauncherRows();
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex, "launcher-add");
            if (!_closed) _launcherRows.Children.Add(new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Error,
                Title = "无法添加快捷启动", Message = ex.Message });
        }
    }
    private void RefreshLauncherRows()
    {
        _launcherRows.Children.Clear();
        var entries = AppSettings.Instance.QuickLaunchEntries;
        foreach (var entry in entries.ToArray())
        {
            var row = new Grid { ColumnSpacing = 12, RowSpacing = 12, Padding = new Thickness(16) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var identity = new Grid { ColumnSpacing = 12 };
            identity.ColumnDefinitions.Add(new() { Width = new GridLength(40) });
            identity.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
            var fallback = new FontIcon { Glyph = "\uE8A7", FontSize = 24 };
            var image = new Image { Width = 32, Height = 32 };
            icon.Children.Add(fallback); icon.Children.Add(image); identity.Children.Add(icon);
            var name = new TextBox { Text = entry.Name, PlaceholderText = "显示名称", Header = "显示名称", HorizontalAlignment = HorizontalAlignment.Stretch };
            name.LostFocus += (_, _) => { entry.Name = string.IsNullOrWhiteSpace(name.Text) ? System.IO.Path.GetFileName(entry.Path) : name.Text.Trim(); AppSettings.Instance.Save(); RefreshToolbarEditor(); };
            Grid.SetColumn(name, 1); identity.Children.Add(name); row.Children.Add(identity);
            var path = new TextBlock { Text = entry.Path, TextTrimming = TextTrimming.CharacterEllipsis,
                Style = (Style)PageHost.Resources["SettingDesc"] };
            ToolTipService.SetToolTip(path, entry.Path); Grid.SetRow(path, 1); row.Children.Add(path);
            var up = new Button { Content = new FontIcon { Glyph = "\uE74A", FontSize = 14 }, MinWidth = 40, MinHeight = 40, IsEnabled = entries.IndexOf(entry) > 0 };
            ToolTipService.SetToolTip(up, "上移"); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(up, entry.Name + " 上移");
            up.Click += (_, _) => { int index = entries.IndexOf(entry); if (index < 1) return; entries.RemoveAt(index); entries.Insert(index - 1, entry); AppSettings.Instance.Save(); RefreshLauncherRows(); };
            var down = new Button { Content = new FontIcon { Glyph = "\uE74B", FontSize = 14 }, MinWidth = 40, MinHeight = 40, IsEnabled = entries.IndexOf(entry) < entries.Count - 1 };
            ToolTipService.SetToolTip(down, "下移"); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(down, entry.Name + " 下移");
            down.Click += (_, _) => { int index = entries.IndexOf(entry); if (index < 0 || index >= entries.Count - 1) return; entries.RemoveAt(index); entries.Insert(index + 1, entry); AppSettings.Instance.Save(); RefreshLauncherRows(); };
            var remove = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 }, MinWidth = 40, MinHeight = 40 };
            ToolTipService.SetToolTip(remove, "删除"); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, entry.Name + " 删除");
            remove.Click += (_, _) => { entries.Remove(entry); AppSettings.Instance.Save(); RefreshLauncherRows(); RefreshToolbarEditor(); };
            var footer = new StackPanel { Spacing = 12 };
            var pin = new ComboBox { Header = "固定到工具栏", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (string label in new[] { "不固定", "左侧", "右侧", "底部左侧", "底部中间", "底部右侧" }) pin.Items.Add(label);
            pin.SelectedIndex = entry.PinnedLayout is { } layout ? (int)layout + 1 : 0;
            pin.SelectionChanged += (_, _) => { entry.PinnedLayout = pin.SelectedIndex > 0 ? (ToolbarLayout)(pin.SelectedIndex - 1) : null; AppSettings.Instance.Save(); RefreshToolbarEditor(); };
            footer.Children.Add(pin);
            footer.Children.Add(new TextBlock { Text = "固定后，可在上方“已显示功能”中与其他按钮一起排序。",
                TextWrapping = TextWrapping.Wrap, Style = (Style)PageHost.Resources["SettingDesc"] });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(up); actions.Children.Add(down); actions.Children.Add(remove); footer.Children.Add(actions);
            Grid.SetRow(footer, 2); row.Children.Add(footer);
            _launcherRows.Children.Add(new Border { Style = (Style)PageHost.Resources["CardBorder"], Child = row });
            image.Loaded += async (_, _) =>
            {
                try
                {
                    var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(entry.Path);
                    using var thumbnail = await file.GetThumbnailAsync(global::Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 48);
                    if (thumbnail is null || _closed) return;
                    var source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(); await source.SetSourceAsync(thumbnail);
                    if (!_closed) { image.Source = source; fallback.Visibility = Visibility.Collapsed; }
                }
                catch { /* 缺失文件或图标不可用时显示备用图标。 */ }
            };
        }
    }
}
