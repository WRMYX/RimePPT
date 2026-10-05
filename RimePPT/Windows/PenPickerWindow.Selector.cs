using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using RimePPT.Core;
using RimePPT.Core.Ink;
using Windows.Foundation;
using Windows.UI;

namespace RimePPT.Windows;

public partial class PenPickerWindow
{
    private readonly SelectorBar _penSelector = new();
    private readonly Frame _penOptions = new() { IsNavigationStackEnabled = false };
    private GridView? _optionGrid;
    private int _optionIndex = -1;
    private bool _nativeNoticeOpen;

    private void InitializePenSelector()
    {
        foreach (string title in new[] { "颜色", "线型", "形状" })
            _penSelector.Items.Add(new SelectorBarItem { Text = title });
        AutomationProperties.SetName(_penSelector, "笔设置分页");
        _penSelector.SelectionChanged += (_, _) =>
        {
            int index = _penSelector.Items.IndexOf(_penSelector.SelectedItem);
            if (index < 0 || index == _optionIndex) return;
            if (index > 0 && !_forceSelf && AppSettings.Instance.InkBackend == InkBackend.Native)
            {
                _panel.DispatcherQueue.TryEnqueue(() =>
                {
                    _penSelector.SelectedItem = _penSelector.Items[Math.Max(0, _optionIndex)];
                    ShowNativeOptionsNotice();
                });
                return;
            }
            HideColorPicker();
            int previous = _optionIndex;
            _optionIndex = index;
            _optionGrid = null; _currentColor = null;
            var transition = previous < 0 || !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled
                ? (NavigationTransitionInfo)new SuppressNavigationTransitionInfo()
                : new SlideNavigationTransitionInfo { Effect = index > previous
                    ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft };
            if (_penOptions.XamlRoot is not null) _penOptions.Navigate(typeof(PenOptionsPage), this, transition);
            RefreshPenSelection();
        };
        _panel.Children.Add(_penSelector);
        _panel.Children.Add(_penOptions);
        _penOptions.Loaded += (_, _) =>
        {
            if (_penOptions.Content is null)
            {
                _penOptions.Navigate(typeof(PenOptionsPage), this, new SuppressNavigationTransitionInfo());
                RefreshPenSelection();
            }
        };
        _penSelector.SelectedItem = _penSelector.Items[0];
    }

    internal FrameworkElement BuildPenOptions()
    {
        var grid = new GridView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 160 };
        grid.ItemsPanel = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsWrapGrid Orientation='Horizontal' MaximumRowsOrColumns='3'/></ItemsPanelTemplate>");
        _optionGrid = grid;
        AutomationProperties.SetName(grid, _optionIndex == 0 ? "笔颜色" : _optionIndex == 1 ? "笔线型" : "绘制形状");
        if (_optionIndex == 0)
        {
            string[] labels = { "红色", "蓝色", "绿色", "橙色", "紫色", "黑色" };
            for (int i = 0; i < PenPalette.Presets.Length; i++)
            {
                var (name, argb) = PenPalette.Presets[i];
                AddOption(grid, name, labels[i], new Ellipse { Width = 24, Height = 24,
                    Fill = new SolidColorBrush(Color.FromArgb(argb[0], argb[1], argb[2], argb[3])) });
            }
            var rainbow = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
            var colors = new[] { Microsoft.UI.Colors.Red, Microsoft.UI.Colors.Orange, Microsoft.UI.Colors.LimeGreen,
                Microsoft.UI.Colors.DeepSkyBlue, Microsoft.UI.Colors.BlueViolet };
            for (int i = 0; i < colors.Length; i++) rainbow.GradientStops.Add(new() { Color = colors[i], Offset = i / (double)(colors.Length - 1) });
            var circle = new Grid { Width = 28, Height = 28 };
            circle.Children.Add(new Ellipse { Fill = rainbow });
            _currentColor = new Ellipse { Width = 18, Height = 18 };
            circle.Children.Add(_currentColor);
            var customItem = AddOption(grid, "custom", "自定义", circle);
            var customContent = customItem.Content; customItem.Content = null;
            var customButton = new Button { Content = customContent, Width = 84, Height = 68,
                Padding = new Thickness(4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            customItem.Content = customButton;
            AutomationProperties.SetName(customButton, "打开自定义颜色");
            customButton.Click += (_, _) => ShowColorPicker(customButton);
        }
        else
        {
            string[] labels = _optionIndex == 1
                ? new[] { "实线", "虚线", "点线", "点划线", "波浪线", "荧光笔" }
                : new[] { "自由书写", "直线", "箭头", "矩形", "椭圆", "三角形" };
            for (int i = 0; i < labels.Length; i++) AddOption(grid, i, labels[i], OptionDrawing(_optionIndex, i));
        }
        grid.SelectionChanged += (_, _) =>
        {
            if (_suppress || grid.SelectedItem is not GridViewItem item || (_optionIndex == 0 && (string)item.Tag == "custom")) return;
            ApplyPenOption(item);
        };
        grid.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is not GridViewItem item) return;
            if (_optionIndex > 0 && !_forceSelf && AppSettings.Instance.InkBackend != InkBackend.Rime) ApplyPenOption(item);
        };
        return grid;
    }

    private void ApplyPenOption(GridViewItem item)
    {
        var settings = AppSettings.Instance;
        if (_optionIndex > 0 && !_forceSelf && settings.InkBackend == InkBackend.Native)
        {
            RefreshPenSelection(); ShowNativeOptionsNotice(); return;
        }
        if (_optionIndex == 0) settings.PenColor = (string)item.Tag;
        else
        {
            if (_optionIndex == 1) settings.PenLineStyle = (InkLineStyle)(int)item.Tag;
            else settings.PenShape = (InkShape)(int)item.Tag;
        }
        ChangedLive();
    }

    private async void ShowNativeOptionsNotice()
    {
        if (_nativeNoticeOpen) return;
        _nativeNoticeOpen = true;
        try
        {
            Hide();
            var prompt = new PromptWindow("原生笔不支持此功能",
                "PowerPoint 原生 COM 笔不支持线型和形状。请自行切换为 RimePPT 自研模式后使用。", "知道了", "");
            await prompt.ShowCenteredAsync(App.PresentationDisplayArea);
        }
        catch (Exception ex) { CrashReporter.Report(ex, "native-pen-notice"); }
        finally { _nativeNoticeOpen = false; }
    }

    private static GridViewItem AddOption(GridView grid, object value, string label, FrameworkElement drawing)
    {
        var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        var image = new Grid { Width = 44, Height = 28 };
        image.Children.Add(drawing); panel.Children.Add(image);
        var caption = new TextBlock { Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(caption);
        if (drawing is Polyline line) line.SetBinding(Shape.StrokeProperty,
            new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath("Foreground"), Source = caption });
        var item = new GridViewItem { Tag = value, Content = panel, Width = 92, Height = 76,
            Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(item, label);
        grid.Items.Add(item);
        return item;
    }

    private static FrameworkElement OptionDrawing(int category, int value)
    {
        var line = new Polyline { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        IEnumerable<Point> points;
        if (category == 1)
        {
            points = value == (int)InkLineStyle.Wave
                ? Enumerable.Range(0, 41).Select(i => new Point(2 + i, 14 + 4 * Math.Sin(i * Math.PI / 12)))
                : new[] { new Point(2, 14), new Point(42, 14) };
            if (value == 1) line.StrokeDashArray = new DoubleCollection { 4, 3 };
            if (value == 2) line.StrokeDashArray = new DoubleCollection { 0.1, 3 };
            if (value == 3) line.StrokeDashArray = new DoubleCollection { 4, 2, 0.1, 2 };
            if (value == 5) { line.StrokeThickness = 7; line.Opacity = 0.45; }
        }
        else points = value switch
        {
            0 => Enumerable.Range(0, 41).Select(i => new Point(2 + i, 15 + 6 * Math.Sin(i * Math.PI / 28))),
            1 => new[] { new Point(3, 23), new Point(41, 5) },
            2 => new[] { new Point(3, 23), new Point(40, 5), new Point(30, 6), new Point(40, 5), new Point(34, 14) },
            3 => new[] { new Point(5, 4), new Point(39, 4), new Point(39, 24), new Point(5, 24), new Point(5, 4) },
            4 => Enumerable.Range(0, 49).Select(i => new Point(22 + 18 * Math.Cos(i * Math.PI / 24), 14 + 10 * Math.Sin(i * Math.PI / 24))),
            _ => new[] { new Point(22, 3), new Point(41, 24), new Point(3, 24), new Point(22, 3) }
        };
        foreach (var point in points) line.Points.Add(point);
        return line;
    }

    private void RefreshPenSelection()
    {
        if (_optionGrid is null) return;
        var s = AppSettings.Instance;
        object selected = _optionIndex == 0 ? s.PenColor : _optionIndex == 1 ? (int)s.PenLineStyle : (int)s.PenShape;
        bool previous = _suppress;
        _suppress = true;
        try { _optionGrid.SelectedItem = _optionGrid.Items.Cast<GridViewItem>().FirstOrDefault(x => Equals(x.Tag, selected)); }
        finally { _suppress = previous; }
    }
}

public sealed partial class PenOptionsPage : Page
{
    public PenOptionsPage() { InitializeComponent(); }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is PenPickerWindow picker) Content = picker.BuildPenOptions();
    }
}
