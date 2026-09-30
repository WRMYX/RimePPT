using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RimePPT.Services;
using Windows.Graphics;
using Windows.UI;

namespace RimePPT.Windows
{
    /// <summary>提示窗按钮结果。</summary>
    public enum PromptResult
    {
        /// <summary>主按钮（保存/加载）。</summary>
        Primary,

        /// <summary>次按钮（不保存/不加载）。</summary>
        Secondary,

        /// <summary>删除墨迹按钮。</summary>
        Delete,
    }

    /// <summary>
    /// 与工具条同风格的置顶提示窗：无边框亚克力圆角卡片 + 原生按钮，
    /// 可选的"删除墨迹"按钮固定在左下角。ShowCenteredAsync 返回用户选择。
    /// </summary>
    public sealed class PromptWindow : Window
    {
        private const double CardWidthDips = 380;

        private static readonly Color LightForeground = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B);
        private static readonly Color DarkForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        private static readonly Color LightStroke = Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        private static readonly Color DarkStroke = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
        private static readonly Color LightBar = Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF);
        private static readonly Color DarkBar = Color.FromArgb(0xF0, 0x28, 0x28, 0x28);
        private static readonly Color LightAccent = Color.FromArgb(0xFF, 0x00, 0x5F, 0xB8);
        private static readonly Color DarkAccent = Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF);
        private static readonly Color LightAccentForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        private static readonly Color DarkAccentForeground = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        private static readonly Color LightDanger = Color.FromArgb(0xFF, 0xC4, 0x2C, 0x1E);
        private static readonly Color DarkDanger = Color.FromArgb(0xFF, 0xFF, 0x7A, 0x6B);

        private readonly Grid _root = new();
        private readonly Border _card = new()
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Width = CardWidthDips,
        };
        private readonly TextBlock _titleBlock = new()
        {
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        private readonly TextBlock _messageBlock = new()
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        };
        private readonly Button _primaryButton = new()
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 6, 14, 6),
            MinWidth = 92,
            MinHeight = 44, // 触摸最小命中目标
        };
        private readonly Button _secondaryButton = new()
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 6, 14, 6),
            MinWidth = 92,
            MinHeight = 44,
        };
        private readonly Button? _deleteButton;
        private readonly TaskCompletionSource<PromptResult> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DisplayArea? _area;

        /// <summary>用户做出选择时触发（SetResult 内 raising，供非 await 调用方使用）。</summary>
        public event EventHandler<PromptResult>? ChoiceMade;

        public PromptWindow(string title, string message, string primaryText, string secondaryText, string? deleteText = null)
        {
            SystemBackdrop = new TransparentBackdrop();
            Title = "RimePPT";

            // 提示窗是模态对话框性质：可激活、可聚焦，按钮才能可靠接收点击
            WindowPlumbing.EnableTransparency(this);
            WindowPlumbing.RemoveWindowBorder(this);
            WindowPlumbing.RemoveResizableFrame(this);

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }
            AppWindow.IsShownInSwitchers = false;

            _titleBlock.Text = title;
            _messageBlock.Text = message;
            _primaryButton.Content = primaryText;
            _primaryButton.Click += (_, _) => SetResult(PromptResult.Primary);
            _secondaryButton.Content = secondaryText;
            _secondaryButton.Click += (_, _) => SetResult(PromptResult.Secondary);

            // 删除按钮固定在左下角
            var buttonGrid = new Grid();
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttonGrid.Margin = new Thickness(0, 12, 0, 0);

            if (!string.IsNullOrEmpty(deleteText))
            {
                _deleteButton = new Button
                {
                    Content = deleteText,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(14, 6, 14, 6),
                    MinHeight = 44,
                };
                _deleteButton.Click += (_, _) => SetResult(PromptResult.Delete);
                Grid.SetColumn(_deleteButton, 0);
                buttonGrid.Children.Add(_deleteButton);
            }

            var rightStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(rightStack, 1);
            rightStack.Children.Add(_secondaryButton);
            rightStack.Children.Add(_primaryButton);
            buttonGrid.Children.Add(rightStack);

            var stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(_titleBlock);
            stack.Children.Add(_messageBlock);
            stack.Children.Add(buttonGrid);
            _card.Child = stack;
            _root.Children.Add(_card);
            Content = _root;

            _root.SizeChanged += (_, _) => UpdatePlacement();
            Closed += (_, _) => _tcs.TrySetResult(PromptResult.Secondary);

            ApplyTheme();
        }

        /// <summary>在指定显示器上正居中显示并等待用户选择。</summary>
        public async Task<PromptResult> ShowCenteredAsync(DisplayArea area)
        {
            _area = area;
            Activate();
            UpdatePlacement();
            ApplyTheme();
            return await _tcs.Task;
        }

        private void SetResult(PromptResult result)
        {
            _tcs.TrySetResult(result);
            ChoiceMade?.Invoke(this, result);
            Close();
        }

        private void ApplyTheme()
        {
            bool isDark = _root.ActualTheme == ElementTheme.Dark;

            _card.Background = new AcrylicBrush
            {
                TintColor = isDark ? DarkBar : LightBar,
                TintOpacity = 1,
                FallbackColor = isDark ? DarkBar : LightBar,
            };
            _card.BorderBrush = new SolidColorBrush(isDark ? DarkStroke : LightStroke);
            _card.BorderThickness = new Thickness(1);

            var foreground = new SolidColorBrush(isDark ? DarkForeground : LightForeground);
            _titleBlock.Foreground = foreground;
            _messageBlock.Foreground = foreground;
            _secondaryButton.Foreground = foreground;
            if (_deleteButton is not null)
            {
                // 破坏性操作用暗红提示
                _deleteButton.Foreground = new SolidColorBrush(isDark ? DarkDanger : LightDanger);
            }

            _primaryButton.Background = new SolidColorBrush(isDark ? DarkAccent : LightAccent);
            _primaryButton.Foreground = new SolidColorBrush(isDark ? DarkAccentForeground : LightAccentForeground);
        }

        private void UpdatePlacement()
        {
            var area = _area ?? Microsoft.UI.Windowing.DisplayArea.Primary;
            _card.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
            int width = (int)Math.Ceiling(_card.DesiredSize.Width * scale);
            int height = (int)Math.Ceiling(_card.DesiredSize.Height * scale);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            AppWindow.Resize(new SizeInt32(width, height));
            var bounds = area.OuterBounds;
            AppWindow.Move(new PointInt32(
                bounds.X + Math.Max(0, (bounds.Width - width) / 2),
                bounds.Y + Math.Max(0, (bounds.Height - height) / 2)));
        }
    }
}
