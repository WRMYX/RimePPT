using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RimePPT.Core;

namespace RimePPT.Controls;
public sealed partial class ToolbarButton : UserControl
{
    public event EventHandler<ToolbarCommand>? Clicked;
    private readonly Grid _host = new();
    private readonly FontIcon _icon = new() { FontSize = 20, FontFamily = new FontFamily("Segoe Fluent Icons") };
    private readonly TextBlock _label = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _penColor = new() { Width = 24, Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 0, 0), IsHitTestVisible = false };
    private ButtonBase? _button;
    private bool _guidePreview;
    private bool _normalChecked;
    private bool _normalEnabled = true;
    internal void SetGuidePreview(bool enabled)
    {
        _guidePreview = enabled;
        if (_button is null) return;
        _button.IsEnabled = enabled || _normalEnabled;
        SetGuideSelected(false);
        if (!enabled && _button is ToggleButton toggle) toggle.IsChecked = _normalChecked;
    }
    internal void SetGuideSelected(bool selected)
    {
        if (_button is null) return;
        bool active = _guidePreview && selected;
        // 普通 Button 的 PointerOver 会覆盖 Background；使用原生强调样式，
        // 保证工具按钮在悬停和点击后仍保持蓝色反馈。
        if (_button is Button)
        {
            if (active) _button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            else _button.ClearValue(FrameworkElement.StyleProperty);
        }
        _button.Background = active ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (active) _button.Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
        else _button.ClearValue(Control.ForegroundProperty);
        if (_guidePreview && _button is ToggleButton toggle) toggle.IsChecked = active;
    }
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(ToolbarButton), new PropertyMetadata("", (d,e) => ((ToolbarButton)d)._icon.Glyph = (string)e.NewValue));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(ToolbarButton), new PropertyMetadata("", (d,e) => ((ToolbarButton)d)._label.Text = (string)e.NewValue));
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty,value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty,value); }
    private ToolbarCommand _command;
    public ToolbarCommand Command { get => _command; set { _command = value; Build(); } }
    public ToolbarButton() { Content = _host; Loaded += (_,_) => Build(); }
    private void Build()
    {
        if (_button is not null) return;
        var content = new StackPanel { Spacing = 2 };
        if (Command == ToolbarCommand.Annotate)
        {
            // 色条与笔尖共用图标区域，不增加工具按钮的高度。
            var pen = new Grid { Width = 28, Height = 20 };
            pen.Children.Add(_penColor); pen.Children.Add(_icon);
            content.Children.Add(pen); UpdatePenColor();
        }
        else content.Children.Add(_icon);
        content.Children.Add(_label);
        _button = Command is ToolbarCommand.Annotate or ToolbarCommand.Eraser ? new ToggleButton() : new Button();
        _button.Content = content;
        _button.MinWidth = 44; _button.MinHeight = 44;
        _button.Padding = new Thickness(10, 8, 10, 8);
        _button.BorderThickness = new Thickness(0);
        _button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _button.HorizontalAlignment = HorizontalAlignment.Stretch;
        _button.HorizontalContentAlignment = HorizontalAlignment.Center;
        _button.VerticalContentAlignment = VerticalAlignment.Center;
        _button.Click += (_,_) => Clicked?.Invoke(this, Command);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_button, Label);
        if (Command == ToolbarCommand.Pages)
        {
            // ThemeResource 保留动态主题绑定，页码区域与翻页按钮明确分开。
            var frame = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource SubtleFillColorSecondaryBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='1' CornerRadius='4' Margin='2,4'/>" );
            frame.Child = _button;
            _host.Children.Add(frame);
        }
        else _host.Children.Add(_button);
    }
    public void SetShowText(bool show) => _label.Visibility = show || Command == ToolbarCommand.Pages ? Visibility.Visible : Visibility.Collapsed;
    public void UpdatePenColor()
    {
        if (Command != ToolbarCommand.Annotate) return;
        var color = AppSettings.Instance.GetPenArgb();
        _penColor.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(color[0], color[1], color[2], color[3]));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_penColor, $"当前笔颜色 #{Convert.ToHexString(color)}");
    }
    public void SetEnabled(bool enabled) { _normalEnabled = enabled; if (_button is not null) _button.IsEnabled = _guidePreview || enabled; }
    public void SetPageNumber(int current, int total)
    {
        _icon.Visibility = Visibility.Collapsed;
        _label.FontSize = 14;
        Label = total > 0 ? $"{current} / {total}" : $"{current} / —";
        _label.Visibility = Visibility.Visible;
    }
    public void SetChecked(bool value) { _normalChecked = value; if (!_guidePreview && _button is ToggleButton toggle) toggle.IsChecked = value; }
}
