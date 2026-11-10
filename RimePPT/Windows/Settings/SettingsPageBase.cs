using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RimePPT.Core;

namespace RimePPT.Windows.Settings;

public abstract class SettingsPageBase : Page
{
    protected readonly StackPanel Rows = new() { Spacing = 12, Padding = new Thickness(0, 0, 0, 24) };
    protected AppSettings Settings => AppSettings.Instance;
    protected SettingsPageBase()
    {
        Content = new ScrollViewer { Content = Rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    protected void Section(string title, string description)
    {
        Rows.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] }, new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] } } });
    }
}
