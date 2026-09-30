using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RimePPT.Core;
using RimePPT.Services;
using Windows.Graphics;
using Windows.UI;

namespace RimePPT.Windows
{
    /// <summary>
    /// 工具菜单卡（工具条"工具"按钮弹出）：黑屏模式 / 计时器 / 设置。
    /// 独立无边框窗口而非 Flyout——Flyout 会被裁剪在工具条窗口内。
    /// 与 PenPickerWindow 同模式：可激活、Measure(∞) 撑窗、DPI 换算。
    /// </summary>
    public sealed class ToolsMenuWindow : Window
    {
        private readonly Grid _root = new();
        private readonly Border _card = new()
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
        };
        private readonly StackPanel _items = new() { Spacing = 2 };

        private int _edgeX;
        private int _centerY;
        private bool _pointRight = true;

        private static readonly Color LightBar = Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF);
        private static readonly Color DarkBar = Color.FromArgb(0xF0, 0x28, 0x28, 0x28);
        private static readonly Color LightStroke = Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        private static readonly Color DarkStroke = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        public ToolsMenuWindow(IReadOnlyList<(string Glyph, string Text, Action OnClick)> items)
        {
            Title = "RimePPT";
            SystemBackdrop = new TransparentBackdrop();

            // 菜单项可点击：可激活（与 PenPickerWindow 同规则）
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
            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

            foreach (var (glyph, text, onClick) in items)
            {
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    MinHeight = 44, // 触摸最小命中目标
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
                row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 });
                button.Content = row;
                button.Click += (_, _) =>
                {
                    onClick();
                    Dismiss();
                };
                _items.Children.Add(button);
            }

            _card.Child = _items;
            _root.Children.Add(_card);
            Content = _root;

            // 内容真实布局完成后重新量测撑窗（仅 ShowAt 一次会在字体/图标
            // 未就绪时量小，导致菜单被裁剪）
            _root.SizeChanged += (_, _) => UpdatePlacement();

            ApplyTheme();
        }

        /// <summary>在工具按钮外缘打开，失败返回 false（调用方丢弃实例）。</summary>
        public bool ShowAt(int edgeX, int centerY, bool pointRight)
        {
            _edgeX = edgeX;
            _centerY = centerY;
            _pointRight = pointRight;

            ApplyTheme();

            // 先定位后激活（Activate 进行中触发布局回调会打断激活）
            UpdatePlacement();
            try
            {
                Activate();
            }
            catch (Exception ex)
            {
                CrashReporter.Log($"tools menu activate failed: {ex.Message}");
                Close();
                return false;
            }
            return true;
        }

        public void Dismiss()
        {
            Close();
        }

        private void UpdatePlacement()
        {
            _card.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            int width = (int)Math.Ceiling(_card.DesiredSize.Width * scale);
            int height = (int)Math.Ceiling(_card.DesiredSize.Height * scale);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            AppWindow.Resize(new SizeInt32(width, height));
            int x = _pointRight ? _edgeX + 4 : _edgeX - width - 4;
            int y = Math.Max(0, _centerY - height / 2);
            AppWindow.Move(new PointInt32(x, y));
        }

        private void ApplyTheme()
        {
            bool isDark = ThemeHelper.IsDarkTheme();

            _card.Background = new AcrylicBrush
            {
                TintColor = isDark ? DarkBar : LightBar,
                TintOpacity = 1,
                FallbackColor = isDark ? DarkBar : LightBar,
            };
            _card.BorderBrush = new SolidColorBrush(isDark ? DarkStroke : LightStroke);
            _card.BorderThickness = new Thickness(1);

            // 卡片配色可与窗口主题相反，菜单文字前景必须跟卡片走
            var label = new SolidColorBrush(isDark
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B));
            foreach (var child in _items.Children)
            {
                if (child is Button { Content: StackPanel row })
                {
                    foreach (var element in row.Children)
                    {
                        if (element is TextBlock tb)
                        {
                            tb.Foreground = label;
                        }
                        else if (element is FontIcon icon)
                        {
                            icon.Foreground = label;
                        }
                    }
                }
            }
        }
    }
}
