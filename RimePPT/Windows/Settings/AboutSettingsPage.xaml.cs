using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Linq;

namespace RimePPT.Windows;

public sealed partial class AboutSettingsPage : Page
{
    private int _clicks;
    private DateTime _lastClick;
    private Action? _enableDeveloper;
    public AboutSettingsPage()
    {
        InitializeComponent();
        VersionText.Text = "版本 " + Services.GitHubUpdateService.InstalledVersion;
    }
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _enableDeveloper = e.Parameter as Action;
    }
    private async void Open(string url) => await global::Windows.System.Launcher.LaunchUriAsync(new global::System.Uri(url));
    private void OnGitHub(object sender, RoutedEventArgs e) => Open("https://github.com/WRMYX/RimePPT");
    private void OnWebsite(object sender, RoutedEventArgs e) => Open("https://rimeppt.myxmjy.top");
    private void OnFeedback(object sender, RoutedEventArgs e) => Open("https://github.com/WRMYX/RimePPT/issues");
    private void DeveloperLogo_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (DateTime.UtcNow - _lastClick > TimeSpan.FromSeconds(3)) _clicks = 0;
        _lastClick = DateTime.UtcNow;
        if (++_clicks < 5) return;
        _clicks = 0;
        Core.AppSettings.Instance.DeveloperModeEnabled = true;
        Core.AppSettings.Instance.Save();
        _enableDeveloper?.Invoke();
    }
    private async void Support_Click(object sender, RoutedEventArgs e)
    {
        var theme = ActualTheme == ElementTheme.Light ? "Light" : "Dark";
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(SupportCard("微信支付", $"ms-appx:///Assets/Support/WeChat-{theme}.png"));
        grid.Children.Add(SupportCard("支付宝", $"ms-appx:///Assets/Support/Alipay-{theme}.png"));
        Grid.SetColumn((FrameworkElement)grid.Children[1], 1);
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = "感谢你使用 RimePPT。项目由个人维护，支持将用于持续改进软件。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(grid);
        panel.Children.Add(new TextBlock { Text = "你可以选择微信或支付宝支持项目，金额由你决定。", TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { Title = "支持 RimePPT", Content = panel, CloseButtonText = "关闭", XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }
    private static Border SupportCard(string title, string source)
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(source)), Width = 180, Height = 180, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform });
        panel.Children.Add(new TextBlock { Text = title, HorizontalAlignment = HorizontalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        return new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Child = panel };
    }
}

