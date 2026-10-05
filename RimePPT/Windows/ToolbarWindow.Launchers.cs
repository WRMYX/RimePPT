using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using RimePPT.Core;
using RimePPT.Services;
namespace RimePPT.Windows;
public sealed partial class ToolbarWindow
{
    internal string ExtraConfiguration { get; private set; } = "";
    internal static string ExtrasSignature(ToolbarLayout layout) =>
        $"{AppSettings.Instance.DeveloperModeEnabled}:{AppSettings.Instance.ExitSeparatorEnabled}:{AppSettings.Instance.SeparateExitToolbarEnabled}:" +
        string.Join(",", AppSettings.Instance.GetToolbarItemOrder(layout)) + ":" +
        string.Join("|", AppSettings.Instance.QuickLaunchEntries.Where(x => x.PinnedLayout == layout).Select(x => $"{x.Id}:{x.Name}:{x.Path}"));
    private void AddPinnedLaunchers(ItemsControl list)
    {
        ExtraConfiguration = ExtrasSignature(_layout);
        if (_exitOnly) return;
        foreach (var entry in AppSettings.Instance.QuickLaunchEntries.Where(x => x.PinnedLayout == _layout))
        {
            var image = new Image { Width = 24, Height = 24 };
            var panel = new StackPanel { Spacing = 4 };
            var fallback = new FontIcon { Glyph = "\uE8A7", FontSize = 24 };
            var visual = new Grid(); visual.Children.Add(fallback); visual.Children.Add(image);
            panel.Children.Add(visual);
            panel.Children.Add(new TextBlock { Text = entry.Name, FontSize = 11, MaxWidth = 88,
                TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
            var button = new Button { Tag = "launcher:" + entry.Id, Content = panel, MinWidth = 44, MinHeight = 44, Padding = new Thickness(8) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, entry.Name);
            ToolTipService.SetToolTip(button, entry.Name);
            button.Click += async (_, _) =>
            {
                try { await QuickLaunchService.LaunchAsync(entry); }
                catch (Exception ex)
                {
                    CrashReporter.Report(ex, "pinned-launch");
                    if (RootGrid.XamlRoot is not null) await new ContentDialog { XamlRoot = RootGrid.XamlRoot,
                        Title = "无法打开快捷启动", Content = ex.Message, CloseButtonText = "知道了" }.ShowAsync();
                }
            };
            button.Loaded += async (_, _) =>
            {
                try
                {
                    var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(entry.Path);
                    using var thumbnail = await file.GetThumbnailAsync(global::Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 48);
                    if (thumbnail is null || _closing) return;
                    var source = new BitmapImage(); await source.SetSourceAsync(thumbnail);
                    if (!_closing) { image.Source = source; fallback.Visibility = Visibility.Collapsed; }
                }
                catch { /* 文件图标不可用时保留启动图标。 */ }
            };
            list.Items.Add(button);
        }
        var original = list.Items.Cast<FrameworkElement>().ToList();
        var sorted = AppSettings.Instance.GetToolbarItemOrder(_layout).Select(key => original.FirstOrDefault(x =>
            x is RimePPT.Controls.ToolbarButton tool ? AppSettings.CommandKey(tool.Command) == key : Equals(x.Tag, key)))
            .Where(x => x is not null).Cast<FrameworkElement>().ToList();
        foreach (var separator in original.Where(x => x is Border))
        {
            int exit = sorted.FindIndex(x => x is RimePPT.Controls.ToolbarButton { Command: ToolbarCommand.ExitShow });
            if (exit > 0) sorted.Insert(exit, separator);
        }
        list.Items.Clear();
        foreach (var item in sorted) list.Items.Add(item);
    }
}
