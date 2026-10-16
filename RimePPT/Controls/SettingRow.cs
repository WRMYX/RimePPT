using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace RimePPT.Controls;

/// <summary>One native setting row: description on the left and a compact editor on the right.</summary>
public sealed class SettingRow : UserControl
{
    private readonly Grid _layout = new() { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 12, MinHeight = 60 };
    private readonly StackPanel _labels = new() { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _description = (TextBlock)XamlReader.Load("<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' FontSize='12' TextWrapping='Wrap' Foreground='{ThemeResource TextFillColorSecondaryBrush}'/>");
    private readonly ContentControl _editor = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(nameof(Editor), typeof(UIElement), typeof(SettingRow), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty EditorWidthProperty = DependencyProperty.Register(nameof(EditorWidth), typeof(double), typeof(SettingRow), new PropertyMetadata(180d, Changed));
    public static readonly DependencyProperty RowPaddingProperty = DependencyProperty.Register(nameof(RowPadding), typeof(Thickness), typeof(SettingRow), new PropertyMetadata(new Thickness(16, 12, 16, 12), Changed));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public UIElement? Editor { get => (UIElement?)GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    public double EditorWidth { get => (double)GetValue(EditorWidthProperty); set => SetValue(EditorWidthProperty, value); }
    public Thickness RowPadding { get => (Thickness)GetValue(RowPaddingProperty); set => SetValue(RowPaddingProperty, value); }

    public SettingRow()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _labels.Children.Add(_title); _labels.Children.Add(_description);
        _layout.Children.Add(_labels); _layout.Children.Add(_editor); Content = _layout;
        SizeChanged += (_, _) => UpdateRow();
        UpdateRow();
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((SettingRow)sender).UpdateRow();
    private void UpdateRow()
    {
        _title.Text = Title; _description.Text = Description;
        _layout.Padding = RowPadding;
        _description.Visibility = string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;
        if (!ReferenceEquals(_editor.Content, Editor))
        {
            _editor.Content = Editor;
            if (Editor is FrameworkElement element)
            {
                element.HorizontalAlignment = HorizontalAlignment.Stretch;
                if (string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(element)))
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, Title);
            }
        }
        bool narrow = ActualWidth > 0 && ActualWidth < 520;
        Grid.SetColumnSpan(_labels, narrow ? 2 : 1);
        Grid.SetColumn(_editor, narrow ? 0 : 1); Grid.SetRow(_editor, narrow ? 1 : 0);
        Grid.SetColumnSpan(_editor, narrow ? 2 : 1);
        _editor.Width = narrow ? double.NaN : EditorWidth;
        _editor.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }
}
