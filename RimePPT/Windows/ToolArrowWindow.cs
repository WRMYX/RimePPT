using System;
using Microsoft.UI.Windowing;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RimePPT.Core;
using RimePPT.Services;

namespace RimePPT.Windows;

/// <summary>外侧细长箭头，使用原生 Button 和系统实心三角图标。</summary>
internal sealed class ToolArrowWindow : Window
{
    private readonly TransparentBackdrop _backdrop = new();
    private readonly ToolbarWindow _owner;
    private ToolbarCommand _command;
    private readonly double _widthDip;
    private readonly double _heightDip;
    private bool _closed;
    private int _revealVersion;
    private bool _placing;
    private FrameworkElement? _target;
    private readonly Grid _root;
    private bool _visibleRequested, _warming, _prewarmed;
    public FrameworkElement SettingsTarget { get; }

    public ToolArrowWindow(ToolbarWindow owner, Action<ToolbarCommand> openSettings)
    {
        _owner = owner;
        bool side = owner.Layout is ToolbarLayout.LeftRail or ToolbarLayout.RightRail;
        _widthDip = side ? 28 : 56; _heightDip = side ? 56 : 28;
        double angle = owner.Layout == ToolbarLayout.LeftRail ? 0 : owner.Layout == ToolbarLayout.RightRail ? 180 : 270;
        string direction = angle == 0 ? "向右" : angle == 180 ? "向左" : "向上";
        Title = "RimePPT 工具设置箭头";
        var icon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"), Glyph = "\uF5B0", FontSize = 12,
            RenderTransformOrigin = new global::Windows.Foundation.Point(.5, .5),
            RenderTransform = new RotateTransform { Angle = angle },
        };
        var arrow = new Button
        {
            Width = side ? 28 : 56, Height = side ? 56 : 28,
            MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new Viewbox { Width = 12, Height = 12, Child = icon },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(arrow, direction);
        SettingsTarget = arrow;
        arrow.Click += (_, _) => openSettings(_command);
        var card = new Border
        {
            Width = arrow.Width, Height = arrow.Height, CornerRadius = new CornerRadius(6),
            HorizontalAlignment = side ? (owner.Layout == ToolbarLayout.LeftRail ? HorizontalAlignment.Left : HorizontalAlignment.Right) : HorizontalAlignment.Center,
            VerticalAlignment = side ? VerticalAlignment.Center : VerticalAlignment.Bottom,
            Child = arrow,
        };
        var root = _root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.Children.Add(card); Content = root;
        // 独立窗口紧贴可见按钮，避免 DWM 为额外留白绘制半透明外框。
        _backdrop.Attach(this);
        ThemeResources.Attach(this, root, card);
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false); p.IsAlwaysOnTop = true;
            p.IsResizable = false; p.IsMinimizable = false; p.IsMaximizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        WindowPlumbing.ApplyPointerNoActivate(this);
        WindowPlumbing.EnableTransparency(this, enableBlur: false);
        WindowPlumbing.RemoveWindowBorder(this);
        WindowPlumbing.RemoveResizableFrame(this);
        _owner.AppWindow.Changed += OnOwnerChanged;
        ((FrameworkElement)_owner.Content).LayoutUpdated += OnOwnerLayout;
        Closed += (_, _) =>
        {
            _closed = true; ++_revealVersion;
            _owner.AppWindow.Changed -= OnOwnerChanged;
            ((FrameworkElement)_owner.Content).LayoutUpdated -= OnOwnerLayout;
        };
    }
    public void ShowFor(ToolbarCommand command)
    {
        if (_closed) return;
        _command = command; _target = _owner.GetCommandTarget(command);
        _visibleRequested = true;
        _root.RequestedTheme = (_owner.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
        Title = command == ToolbarCommand.Eraser ? "RimePPT 橡皮设置箭头" : "RimePPT 笔设置箭头";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SettingsTarget, command == ToolbarCommand.Eraser ? "打开橡皮设置" : "打开笔设置");
        if (_warming)
        {
            _warming = false;
            WindowPlumbing.SetClickThrough(this, false, preserveLayered: true);
        }
        Place();
    }
    public void Hide()
    {
        _visibleRequested = false; ++_revealVersion;
        if (!_closed) AppWindow.Hide();
    }
    public async Task PrewarmAsync()
    {
        if (_closed || _visibleRequested || _prewarmed) return;
        _warming = true;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? frame = null;
        frame = (_, _) => { if (_root.IsLoaded && _root.ActualWidth > 0) complete.TrySetResult(); };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += frame;
        try
        {
            WindowPlumbing.SetClickThrough(this, true);
            WindowPlumbing.SetOpacity(this, 0);
            AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(-32000, -32000, (int)_widthDip, (int)_heightDip));
            AppWindow.Show(false);
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(2));
            _prewarmed = true;
        }
        finally
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= frame;
            _warming = false;
            if (!_closed)
            {
                AppWindow.Hide();
                WindowPlumbing.SetOpacity(this, 0);
                WindowPlumbing.SetClickThrough(this, false, preserveLayered: true);
                if (_visibleRequested) Place();
            }
        }
    }
    private void OnOwnerChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange) Place();
    }
    private void Place()
    {
        if (_closed || _placing || _warming || !_visibleRequested) return;
        if (_owner.IsClosing || !_owner.AppWindow.IsVisible) { AppWindow.Hide(); return; }
        if (_target is null || _target.ActualWidth <= 0 || _target.ActualHeight <= 0) return;
        _placing = true;
        try
        {
        double scale = (_owner.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1;
        int width = (int)Math.Ceiling(_widthDip * scale), height = (int)Math.Ceiling(_heightDip * scale);
        var (anchor, right) = _owner.GetButtonAnchor(_command);
        int x, y;
        if (_owner.Layout is ToolbarLayout.LeftRail or ToolbarLayout.RightRail)
        {
            int gap = (int)Math.Ceiling(4 * scale);
            x = right ? _owner.AppWindow.Position.X + _owner.AppWindow.Size.Width + gap : _owner.AppWindow.Position.X - width - gap;
            y = (int)anchor.Y - height / 2;
        }
        else
        {
            x = _owner.GetButtonCenterX(_command) - width / 2;
            y = _owner.AppWindow.Position.Y - height - (int)Math.Ceiling(4 * scale);
        }
        if (AppWindow.Position.X != x || AppWindow.Position.Y != y || AppWindow.Size.Width != width || AppWindow.Size.Height != height)
            AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(x, y, width, height));
        if (!AppWindow.IsVisible)
        {
            WindowPlumbing.SetOpacity(this, 0);
            AppWindow.Show(false);
            _ = RevealAsync(++_revealVersion);
        }
        }
        finally { _placing = false; }
    }
    private async Task RevealAsync(int version)
    {
        try
        {
            bool ready = await _backdrop.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            if (ready) await TransparentBackdrop.WaitForFrameAsync(_root);
            if (_closed || version != _revealVersion || !_visibleRequested) return;
            if (!ready || !_backdrop.IsReady) { AppWindow.Hide(); return; }
            WindowPlumbing.SetOpacity(this, 255);
        }
        catch (Exception ex)
        {
            if (!_closed && version == _revealVersion) AppWindow.Hide();
            CrashReporter.Report(ex, "tool-arrow-first-frame");
        }
    }
    private void OnOwnerLayout(object? sender, object args) => Place();
    public void Dismiss() { if (!_closed) Close(); }
    public void Raise() { if (!_closed && _visibleRequested && AppWindow.IsVisible) WindowPlumbing.RaiseToTopmost(this); }
    public (global::Windows.Foundation.Point Anchor, bool Right) SettingsAnchor()
    {
        bool right = _owner.Layout == ToolbarLayout.LeftRail;
        return (new global::Windows.Foundation.Point(right ? AppWindow.Position.X + AppWindow.Size.Width : AppWindow.Position.X,
            AppWindow.Position.Y + AppWindow.Size.Height / 2), right);
    }
}
