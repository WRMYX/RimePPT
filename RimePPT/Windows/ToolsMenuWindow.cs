using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RimePPT.Core;

namespace RimePPT.Windows;

/// <summary>跟随工具按钮的原生菜单，允许展开到工具栏窗口外。</summary>
public sealed class ToolsMenuWindow : Flyout
{
    public bool IsShowing { get; private set; }
    public ToolsMenuWindow(IReadOnlyList<(string Glyph, string Text, Action OnClick)> items)
    {
        ShouldConstrainToRootBounds = false;
        AreOpenCloseAnimationsEnabled = true;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, Application.Current.Resources["OverlayCornerRadius"]));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6)));
        FlyoutPresenterStyle = style;
        var panel = new StackPanel { Spacing = 2, MinWidth = 200, RequestedTheme = App.ToolbarTheme };
        foreach (var (glyph, text, onClick) in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
            row.Children.Add(new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = row, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10,8,10,8) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
            button.Click += (_, _) => { Hide(); onClick(); };
            panel.Children.Add(button);
        }
        Content = panel;
        Opening += (_, _) => IsShowing = true;
        Closed += (_, _) => IsShowing = false;
    }
    public void ShowAt(FrameworkElement anchor, ToolbarLayout layout)
    {
        Placement = layout switch { ToolbarLayout.LeftRail => Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Right,
            ToolbarLayout.RightRail => Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Left,
            _ => Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
        base.ShowAt(anchor);
    }
    public void Dismiss() => Hide();
}
