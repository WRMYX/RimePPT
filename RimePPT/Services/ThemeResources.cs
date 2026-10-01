using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using RimePPT.Core;

namespace RimePPT.Services;

public static class ThemeResources
{
    public static void Attach(Window window, FrameworkElement root, Border card)
    {
        // Keep ThemeResource expressions on the actual visual, rather than copying resolved colours.
        var themed = (Border)XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource AcrylicInAppFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='1' CornerRadius='8'/>");
        themed.Visibility = Visibility.Collapsed;
        if (root is Grid grid) grid.Children.Insert(0, themed);
        card.SetBinding(Border.BackgroundProperty, new Binding { Source = themed, Path = new PropertyPath("Background") });
        card.SetBinding(Border.BorderBrushProperty, new Binding { Source = themed, Path = new PropertyPath("BorderBrush") });
        card.BorderThickness = new(1);
        void Apply(object? sender, EventArgs e)
        {
            root.RequestedTheme = AppSettings.Instance.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        }
        Apply(null, EventArgs.Empty); AppSettings.SettingsChanged += Apply;
        window.Closed += (_, _) => AppSettings.SettingsChanged -= Apply;
    }
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
