using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace RimePPT.Windows;

/// <summary>原生 Frame 的页面容器；设置控件由设置窗口持有，导航时保留状态。</summary>
public sealed class SettingsSectionPage : Page
{
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Content = (ScrollViewer)e.Parameter;
    }
}
