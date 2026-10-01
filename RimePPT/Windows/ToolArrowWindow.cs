using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RimePPT.Core;
using RimePPT.Services;

namespace RimePPT.Windows;

/// <summary>外侧细长箭头，使用原生 Button 和系统实心三角图标。</summary>
internal sealed class ToolArrowWindow : Window
{
    private readonly ToolbarWindow _owner;
    private readonly ToolbarCommand _command;
    private readonly double _widthDip;
    private readonly double _heightDip;
    private bool _closed;
    public FrameworkElement SettingsTarget { get; }

    public ToolArrowWindow(ToolbarWindow owner, ToolbarCommand command, Action openSettings)
    {
        _owner = owner; _command = command;
        bool side = owner.Layout is ToolbarLayout.LeftRail or ToolbarLayout.RightRail;
        _widthDip = side ? 28 : 56; _heightDip = side ? 56 : 28;
        double angle = owner.Layout == ToolbarLayout.LeftRail ? 0 : owner.Layout == ToolbarLayout.RightRail ? 180 : 270;
        string direction = angle == 0 ? "向右" : angle == 180 ? "向左" : "向上";
        Title = command == ToolbarCommand.Eraser ? "RimePPT 橡皮设置箭头" : "RimePPT 笔设置箭头";
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
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(arrow, command == ToolbarCommand.Eraser ? "打开橡皮设置" : "打开笔设置");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(arrow, direction);
        SettingsTarget = arrow;
        arrow.Click += (_, _) => openSettings();
        var card = new Border
        {
            Width = arrow.Width, Height = arrow.Height, CornerRadius = new CornerRadius(6),
            HorizontalAlignment = side ? (owner.Layout == ToolbarLayout.LeftRail ? HorizontalAlignment.Left : HorizontalAlignment.Right) : HorizontalAlignment.Center,
            VerticalAlignment = side ? VerticalAlignment.Center : VerticalAlignment.Bottom,
            Child = arrow,
        };
        var root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.Children.Add(card); Content = root;
        // 独立窗口紧贴可见按钮，避免 DWM 为额外留白绘制半透明外框。
        SystemBackdrop = new TransparentBackdrop();
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
        Closed += (_, _) => { _closed = true; _owner.AppWindow.Changed -= OnOwnerChanged; };
        Place(); AppWindow.Show(false); Place();
    }
    private void OnOwnerChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange) Place();
    }
    private void Place()
    {
        if (_closed) return;
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
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(x, y, width, height));
    }
    public void Dismiss() { if (!_closed) Close(); }
    public void Raise() { if (!_closed) WindowPlumbing.RaiseToTopmost(this); }
    public (global::Windows.Foundation.Point Anchor, bool Right) SettingsAnchor()
    {
        bool right = _owner.Layout == ToolbarLayout.LeftRail;
        return (new global::Windows.Foundation.Point(right ? AppWindow.Position.X + AppWindow.Size.Width : AppWindow.Position.X,
            AppWindow.Position.Y + AppWindow.Size.Height / 2), right);
    }
}
