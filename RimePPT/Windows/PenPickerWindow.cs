using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Media.Imaging;
using RimePPT.Core;
using RimePPT.Core.Ink;
using Windows.UI;

namespace RimePPT.Windows;

/// <summary>笔/橡皮设置原生弹层，保留旧类型名以兼容调用方。</summary>
public partial class PenPickerWindow : Flyout
{
    private readonly bool _eraser;
    private readonly bool _forceSelf;
    private readonly StackPanel _panel = new() { Spacing = 12, Width = 320 };
    private readonly ScrollViewer _scroll = new() { MaxHeight = 600, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly ComboBox _backend = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Slider _width;
    private readonly Image _eraserPreview = new() { HorizontalAlignment = HorizontalAlignment.Center, Stretch = Stretch.Fill };
    private readonly Rectangle _preview = new() { RadiusX = 4, RadiusY = 4, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _size = new();
    private Ellipse? _currentColor;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Flyout? _colorFlyout;
    private bool _suppress, _dirty, _subscribed;
    private FrameworkElement? _anchor;
    public bool IsShowing { get; private set; }
    public bool Owns(FrameworkElement anchor) => ReferenceEquals(_anchor, anchor);
    public PenPickerWindow() : this(false) { }
    internal static PenPickerWindow ForWhiteboard(bool eraser) => new(eraser, true);
    protected PenPickerWindow(bool eraser) : this(eraser, false) { }
    private PenPickerWindow(bool eraser, bool forceSelf)
    {
        _eraser = eraser; _forceSelf = forceSelf;
        ShouldConstrainToRootBounds = false;
        AreOpenCloseAnimationsEnabled = true;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        FlyoutPresenterStyle = PresenterStyle();
        _panel.Children.Add(new TextBlock { Text = eraser ? "橡皮设置" : "笔设置", FontSize = 20 });
        _panel.Children.Add(_backend); _panel.Children.Add(_status);
        _backend.Items.Add("PowerPoint 原生（COM）"); _backend.Items.Add("RimePPT 自研");
        _backend.IsEnabled = !forceSelf;
        _backend.SelectionChanged += (_, _) =>
        {
            if (_suppress || _backend.SelectedIndex < 0) return;
            AppSettings.Instance.InkBackend = (InkBackend)_backend.SelectedIndex; ChangedLive();
        };
        if (!eraser) InitializePenSelector();
        _panel.Children.Add(new TextBlock { Text = eraser ? "橡皮整体大小（DIP）" : "笔粗细（DIP）" });
        _width = new Slider { Minimum = eraser ? 24 : 2, Maximum = eraser ? 160 : 20, StepFrequency = 1 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_width, eraser ? "橡皮整体大小" : "笔粗细");
        _panel.Children.Add(_width);
        _width.ValueChanged += (_, _) => SizeChangedLive();
        _panel.Children.Add(_size); _panel.Children.Add(eraser ? _eraserPreview : _preview);
        if (eraser)
        {
            var clear = new Button { Content = "清除当前页墨迹", HorizontalAlignment = HorizontalAlignment.Stretch };
            clear.Click += async (_, _) => { if (_forceSelf) WhiteboardWindow.ClearCurrentPage(); else await App.ClearInkAsync(); };
            _panel.Children.Add(clear);
        }
        _scroll.Content = _panel; Content = _scroll;
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
            IsShowing = false; HideColorPicker();
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
            _backend.SelectedIndex = _forceSelf ? (int)InkBackend.Rime : (int)s.InkBackend;
            bool custom = _forceSelf || App.EffectiveInkBackend == InkBackend.Rime;
            _width.IsEnabled = custom;
            _width.Value = _eraser ? s.EraserSizeDip : s.PenThickness;
            _status.Text = _forceSelf ? "独立画板使用自研笔迹，可调整颜色、线型、形状及尺寸。" :
                App.InkBackendStatus + (_eraser ? "\n仅擦除当前模式产生的墨迹。" : "\n原生墨迹由 PowerPoint 管理保存。") + (!custom ? "\n尺寸由 PowerPoint 管理。" : "");
            var c = s.GetPenArgb(); var color = Color.FromArgb(c[0], c[1], c[2], c[3]);
            if (_currentColor is not null) _currentColor.Fill = new SolidColorBrush(color);
            _preview.Width = 200;
            _preview.Height = _width.Value;
            _preview.Fill = _eraser ? new SolidColorBrush(Microsoft.UI.Colors.Gray) : new SolidColorBrush(color);
            if (_eraser)
            {
                bool dark = s.Theme == "dark" || (s.Theme == "auto" && ThemeHelper.IsDarkTheme());
                _eraserPreview.Source = new SvgImageSource(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", dark ? "Eraser-Dark.svg" : "Eraser-Light.svg")));
                _eraserPreview.Width = s.EraserWidthDip; _eraserPreview.Height = s.EraserSizeDip;
            }
            RefreshPenSelection();
            _size.Text = $"{_width.Value:0} DIP";
        }
        finally { _suppress = false; }
    }
    private void SizeChangedLive()
    {
        if (_suppress) return;
        if (_eraser) AppSettings.Instance.EraserSizeDip = _width.Value;
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
    private void ShowColorPicker(FrameworkElement anchor)
    {
        HideColorPicker(); Flush();
        var argb = AppSettings.Instance.GetPenArgb();
        var initial = Color.FromArgb(argb[0], argb[1], argb[2], argb[3]);
        var root = new Grid { Width = 320, MaxHeight = 500, RowSpacing = 12, RequestedTheme = _panel.RequestedTheme };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "自定义笔颜色", FontSize = 20 });
        var picker = new ColorPicker { Color = initial, IsAlphaEnabled = false, IsHexInputVisible = true,
            IsColorChannelTextInputVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, "选择笔颜色");
        var scroll = new ScrollViewer { Content = picker, MaxHeight = 380,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right };
        var preview = new Border { Width = 32, Height = 24, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(initial), VerticalAlignment = VerticalAlignment.Center };
        picker.ColorChanged += (_, e) => preview.Background = new SolidColorBrush(e.NewColor);
        var cancel = new Button { Content = "取消", MinHeight = 40 };
        var accept = new Button { Content = "确认", MinHeight = 40,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        cancel.Click += (_, _) => HideColorPicker();
        accept.Click += (_, _) =>
        {
            var color = picker.Color;
            AppSettings.Instance.CustomPenArgb = $"FF{color.R:X2}{color.G:X2}{color.B:X2}";
            AppSettings.Instance.PenColor = "custom"; AppSettings.Instance.Save();
            HideColorPicker();
        };
        actions.Children.Add(preview); actions.Children.Add(cancel); actions.Children.Add(accept);
        Grid.SetRow(actions, 2); root.Children.Add(actions);
        var colorFlyout = new Flyout { Content = root, ShouldConstrainToRootBounds = false,
            AreOpenCloseAnimationsEnabled = true, FlyoutPresenterStyle = PresenterStyle(),
            SystemBackdrop = new DesktopAcrylicBackdrop(),
            Placement = Placement == FlyoutPlacementMode.Left ? FlyoutPlacementMode.Left : FlyoutPlacementMode.Right };
        colorFlyout.Closed += (_, _) => { if (ReferenceEquals(_colorFlyout, colorFlyout)) _colorFlyout = null; };
        _colorFlyout = colorFlyout;
        colorFlyout.ShowAt(anchor);
    }
    private void HideColorPicker() { var flyout = _colorFlyout; _colorFlyout = null; flyout?.Hide(); }
    public bool ShowAt(FrameworkElement anchor, ToolbarLayout layout)
    {
        _anchor = anchor;
        Placement = layout switch { ToolbarLayout.LeftRail => FlyoutPlacementMode.Right, ToolbarLayout.RightRail => FlyoutPlacementMode.Left, _ => FlyoutPlacementMode.Top };
        base.ShowAt(anchor);
        return true;
    }
    public void Dismiss() { HideColorPicker(); Hide(); Flush(); }
}
