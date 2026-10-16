using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RimePPT.Services;
using Windows.Graphics;

namespace RimePPT.Windows;

internal sealed class OnboardingOverlayWindow : Window
{
    internal OnboardingOverlayWindow(DisplayArea area, System.Action restoreGuide)
    {
        Title = "RimePPT 引导遮罩";
        var background = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        background.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            restoreGuide();
            DispatcherQueue.TryEnqueue(() => restoreGuide());
        };
        Content = background;
        WindowPlumbing.UseTransparentNativeBackground(this);
        WindowPlumbing.RemoveWindowBorder(this);
        WindowPlumbing.RemoveResizableFrame(this);
        WindowPlumbing.ApplyNoActivate(this);
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
