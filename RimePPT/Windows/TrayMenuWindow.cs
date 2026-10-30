using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RimePPT.Services;
using Windows.Foundation;
using Windows.Graphics;

namespace RimePPT.Windows;

public sealed class TrayMenuWindow : Window
{
    private readonly Grid _anchor = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly MenuFlyout _menu = new();
    private readonly TransparentBackdrop _backdrop = new();
    private bool _opening, _closed;
    public TrayMenuWindow(Action settings, Action exit)
    {
        Title = "RimePPT 托盘菜单";
        Content = _anchor;
        _backdrop.Attach(this);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Resize(new SizeInt32(1, 1));
        if (AppWindow.Presenter is OverlappedPresenter p)
        { p.SetBorderAndTitleBar(false, false); p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true; }
        WindowPlumbing.EnableTransparency(this);
        WindowPlumbing.RemoveWindowBorder(this);
        WindowPlumbing.RemoveResizableFrame(this);
        var item = new MenuFlyoutItem { Text = "打开设置", Icon = new SymbolIcon(Symbol.Setting) };
        item.Click += (_, _) => settings();
        _menu.ShouldConstrainToRootBounds = false;
        _menu.Items.Add(item);
        _menu.Items.Add(new MenuFlyoutSeparator());
        var exitItem = new MenuFlyoutItem { Text = "退出 RimePPT", Icon = new SymbolIcon(Symbol.Cancel) };
        exitItem.Click += (_, _) => { _menu.Hide(); exit(); }; _menu.Items.Add(exitItem);
        _menu.Closed += (_, _) => { if (!_closed) AppWindow.Hide(); };
        _backdrop.Disconnected += () => { if (!_closed) { _menu.Hide(); AppWindow.Hide(); } };
        Closed += (_, _) => _closed = true;
    }
    public async void ShowAt(PointInt32 location, ElementTheme theme)
    {
        if (_opening || _closed) return;
        _opening = true;
        try
        {
            _menu.Hide();
            foreach (var entry in _menu.Items) if (entry is MenuFlyoutItem row) { if(RimePPT.Core.AppSettings.Instance.TouchFriendlyTrayMenu) row.MinHeight=44; else row.ClearValue(FrameworkElement.MinHeightProperty); }
            _anchor.RequestedTheme = theme;
            var area = DisplayArea.GetFromPoint(location, DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Move(new PointInt32(Math.Clamp(location.X, area.X, area.X + area.Width - 2), Math.Clamp(location.Y, area.Y, area.Y + area.Height - 2)));
            WindowPlumbing.SetOpacity(this, 0);
            WindowPlumbing.ResetTrayHost(this);
            Activate();
            WindowPlumbing.BringToForeground(this);
            // A slow backdrop must not cancel the menu: the empty host region hides the host either way.
            try { await _backdrop.Ready.WaitAsync(TimeSpan.FromSeconds(2)); await TransparentBackdrop.WaitForFrameAsync(_anchor).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
            if (_closed) return;
            WindowPlumbing.ConfigureTrayHost(this);
            WindowPlumbing.SetOpacity(this, 255);
            _menu.ShowAt(_anchor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = new Point(0, 0), Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight });
        }
        catch (Exception ex) { if (!_closed) AppWindow.Hide(); RimePPT.Core.CrashReporter.Report(ex, "tray-menu"); }
        finally { _opening = false; }
    }
}
