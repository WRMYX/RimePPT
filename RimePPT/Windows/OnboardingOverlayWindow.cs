using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RimePPT.Services;
using Windows.Graphics;

namespace RimePPT.Windows;

internal sealed class OnboardingOverlayWindow : Window
{
    internal OnboardingOverlayWindow(DisplayArea area)
    {
        Title = "RimePPT 引导遮罩";
        Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        WindowPlumbing.RemoveWindowBorder(this);
        WindowPlumbing.RemoveResizableFrame(this);
        WindowPlumbing.ApplyPointerNoActivate(this);
        WindowPlumbing.SetOpacity(this, 128);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        var bounds = area.OuterBounds;
        AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        AppWindow.Show(false);
        WindowPlumbing.RaiseToTopmost(this);
    }
}
