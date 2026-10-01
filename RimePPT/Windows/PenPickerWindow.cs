using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RimePPT.Core;
using Windows.UI;

namespace RimePPT.Windows;

/// <summary>笔/橡皮设置原生弹层，保留旧类型名以兼容调用方。</summary>
public class PenPickerWindow : Flyout
{
    private readonly bool _eraser;
    private readonly StackPanel _panel = new() { Spacing = 12, Width = 320 };
    private readonly ComboBox _backend = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Slider _width;
    private readonly Slider? _height;
    private readonly Rectangle _preview = new() { RadiusX = 4, RadiusY = 4, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _size = new();
    private readonly Ellipse? _currentColor;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Flyout? _colorFlyout;
    private ColorPicker? _colorPicker;
    private bool _suppress, _dirty, _subscribed;
    private FrameworkElement? _anchor;
    public bool IsShowing { get; private set; }
    public bool Owns(FrameworkElement anchor) => ReferenceEquals(_anchor, anchor);
    public PenPickerWindow() : this(false) { }
    protected PenPickerWindow(bool eraser)
    {
        _eraser = eraser;
        ShouldConstrainToRootBounds = false;
        AreOpenCloseAnimationsEnabled = true;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        FlyoutPresenterStyle = PresenterStyle();
        _panel.Children.Add(new TextBlock { Text = eraser ? "橡皮设置" : "笔设置", FontSize = 20 });
        _panel.Children.Add(_backend); _panel.Children.Add(_status);
        _backend.Items.Add("PowerPoint 原生（COM）"); _backend.Items.Add("RimePPT 自研");
        _backend.SelectionChanged += (_, _) =>
        {
            if (_suppress || _backend.SelectedIndex < 0) return;
            AppSettings.Instance.InkBackend = (InkBackend)_backend.SelectedIndex; ChangedLive();
        };
        if (!eraser)
        {
            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var (name, argb) in PenPalette.Presets)
            {
                var b = new Button
                {
                    Width = 44, Height = 44, Padding = new Thickness(0), CornerRadius = new CornerRadius(22),
                    Background = new SolidColorBrush(Color.FromArgb(argb[0], argb[1], argb[2], argb[3])),
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name);
                b.Click += (_, _) => { AppSettings.Instance.PenColor = name; ChangedLive(); };
                swatches.Children.Add(b);
            }
            _panel.Children.Add(swatches);
            var rainbow = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
            Color[] colors = { Microsoft.UI.Colors.Red, Microsoft.UI.Colors.Orange, Microsoft.UI.Colors.Yellow, Microsoft.UI.Colors.LimeGreen, Microsoft.UI.Colors.DeepSkyBlue, Microsoft.UI.Colors.BlueViolet, Microsoft.UI.Colors.DeepPink };
            for (int i = 0; i < colors.Length; i++) rainbow.GradientStops.Add(new GradientStop { Color = colors[i], Offset = i / (double)(colors.Length - 1) });
            var circle = new Grid { Width = 44, Height = 44 };
            circle.Children.Add(new Ellipse { Fill = rainbow });
            _currentColor = new Ellipse { Width = 26, Height = 26 };
            circle.Children.Add(_currentColor);
            var custom = new Button { Width = 44, Height = 44, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(22), Content = circle };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(custom, "自定义颜色");
            custom.Click += (_, _) => ShowColorPicker(custom);
            var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            colorRow.Children.Add(custom);
            colorRow.Children.Add(new TextBlock { Text = "自定义颜色", VerticalAlignment = VerticalAlignment.Center });
            _panel.Children.Add(colorRow);
        }
        _panel.Children.Add(new TextBlock { Text = eraser ? "橡皮宽度 / 高度（DIP）" : "笔粗细（DIP）" });
        _width = new Slider { Minimum = eraser ? 16 : 2, Maximum = eraser ? 160 : 20, StepFrequency = 1 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_width, eraser ? "橡皮宽度" : "笔粗细");
        _panel.Children.Add(_width);
        if (eraser)
        {
            _height = new Slider { Minimum = 16, Maximum = 160, StepFrequency = 1 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_height, "橡皮高度");
            _panel.Children.Add(_height);
        }
        _width.ValueChanged += (_, _) => SizeChangedLive();
        if (_height is not null) _height.ValueChanged += (_, _) => SizeChangedLive();
        _panel.Children.Add(_size); _panel.Children.Add(_preview);
        if (eraser)
        {
            var clear = new Button { Content = "清除当前页墨迹", HorizontalAlignment = HorizontalAlignment.Stretch };
            clear.Click += async (_, _) => await App.ClearInkAsync();
            _panel.Children.Add(clear);
        }
        Content = new ScrollViewer { Content = _panel, MaxHeight = 600, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_panel, eraser ? "橡皮设置" : "笔设置");
        _saveTimer.Tick += (_, _) => Flush();
        Opening += (_, _) =>
        {
            IsShowing = true;
            if (!_subscribed) { AppSettings.SettingsChanged += SettingsChanged; _subscribed = true; }
            RefreshBackendState();
        };
        Closed += (_, _) =>
        {
            IsShowing = false; _colorFlyout?.Hide();
            if (_subscribed) { AppSettings.SettingsChanged -= SettingsChanged; _subscribed = false; }
            Flush();
        };
        RefreshBackendState();
    }
    private static Style PresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(12)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16)));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 380d));
        return style;
    }
    private void SettingsChanged(object? sender, EventArgs e) => RefreshBackendState();
    public void RefreshBackendState()
    {
        _suppress = true;
        try
        {
            var s = AppSettings.Instance;
            _panel.RequestedTheme = s.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
            _backend.SelectedIndex = (int)s.InkBackend;
            bool custom = App.EffectiveInkBackend == InkBackend.Rime;
            _width.IsEnabled = custom; if (_height is not null) _height.IsEnabled = custom;
            _width.Value = _eraser ? s.EraserWidthDip : s.PenThickness;
            if (_height is not null) _height.Value = s.EraserHeightDip;
            _status.Text = App.InkBackendStatus + (_eraser ? "\n仅擦除当前模式产生的墨迹。" : "\n原生墨迹由 PowerPoint 管理保存。") + (!custom ? "\n尺寸由 PowerPoint 管理。" : "");
            var c = s.GetPenArgb(); var color = Color.FromArgb(c[0], c[1], c[2], c[3]);
            if (_currentColor is not null) _currentColor.Fill = new SolidColorBrush(color);
            if (_colorPicker is not null) _colorPicker.Color = color;
            _preview.Width = _eraser ? _width.Value : 200;
            _preview.Height = _eraser ? _height?.Value ?? 72 : _width.Value;
            _preview.Fill = _eraser ? new SolidColorBrush(Microsoft.UI.Colors.Gray) : new SolidColorBrush(color);
            _size.Text = _eraser ? $"{_width.Value:0} × {_height?.Value:0} DIP" : $"{_width.Value:0} DIP";
        }
        finally { _suppress = false; }
    }
    private void SizeChangedLive()
    {
        if (_suppress) return;
        if (_eraser) { AppSettings.Instance.EraserWidthDip = _width.Value; AppSettings.Instance.EraserHeightDip = _height!.Value; }
        else AppSettings.Instance.PenThickness = _width.Value;
        ChangedLive();
    }
    private void ChangedLive()
    {
        _dirty = true;
        AppSettings.Instance.NotifyChanged();
        _saveTimer.Stop(); _saveTimer.Start();
        RefreshBackendState();
    }
    private void Flush()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        _dirty = false; AppSettings.Instance.Persist();
    }
    private void ShowColorPicker(Button anchor)
    {
        if (_colorPicker is null)
        {
            _colorPicker = new ColorPicker { IsAlphaEnabled = false, IsHexInputVisible = true, IsColorChannelTextInputVisible = false };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_colorPicker, "选择笔颜色");
            _colorFlyout = new Flyout
            {
                ShouldConstrainToRootBounds = false, AreOpenCloseAnimationsEnabled = true,
                SystemBackdrop = new DesktopAcrylicBackdrop(), FlyoutPresenterStyle = PresenterStyle(),
                Content = new ScrollViewer { Content = _colorPicker, MaxHeight = 580 },
            };
            _colorPicker.ColorChanged += (_, e) =>
            {
                if (_suppress) return;
                var c = e.NewColor; AppSettings.Instance.CustomPenArgb = $"FF{c.R:X2}{c.G:X2}{c.B:X2}";
                AppSettings.Instance.PenColor = "custom"; ChangedLive();
            };
        }
        RefreshBackendState();
        _colorFlyout!.ShowAt(anchor);
    }
    public bool ShowAt(FrameworkElement anchor, ToolbarLayout layout)
    {
        _anchor = anchor;
        Placement = layout switch { ToolbarLayout.LeftRail => FlyoutPlacementMode.Right, ToolbarLayout.RightRail => FlyoutPlacementMode.Left, _ => FlyoutPlacementMode.Top };
        base.ShowAt(anchor);
        return true;
    }
    public void Dismiss() { _colorFlyout?.Hide(); Hide(); Flush(); }
}
