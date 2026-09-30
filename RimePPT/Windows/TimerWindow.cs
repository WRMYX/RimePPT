using System;
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
    /// 浮动计时器卡（正计时）：开始/暂停、清零、关闭；标题行可拖动。
    /// 与 PenPickerWindow 同模式：可激活、Measure(∞) 撑窗、DPI 换算。
    /// </summary>
    public sealed class TimerWindow : Window
    {
        private readonly Grid _root = new();
        private readonly Border _card = new()
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
        };
        private readonly TextBlock _display = new()
        {
            Text = "00:00",
            FontSize = 34,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = new FontFamily("Consolas"),
        };
        private readonly Button _toggleButton = new()
        {
            Content = "开始",
            MinWidth = 84,
            MinHeight = 44, // 触摸最小命中目标
            CornerRadius = new CornerRadius(4),
        };
        private readonly Button _resetButton = new()
        {
            Content = "清零",
            MinWidth = 68,
            MinHeight = 44,
            CornerRadius = new CornerRadius(4),
        };
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private TimeSpan _accumulated;
        private DateTimeOffset? _startedAt;

        private int _edgeX;
        private int _centerY;
        private bool _pointRight = true;

        private static readonly Color LightBar = Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF);
        private static readonly Color DarkBar = Color.FromArgb(0xF0, 0x28, 0x28, 0x28);
        private static readonly Color LightStroke = Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        private static readonly Color DarkStroke = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        public TimerWindow()
        {
            Title = "RimePPT 计时器";
            SystemBackdrop = new TransparentBackdrop();

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

            var titleRow = new Grid { Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
            titleRow.PointerPressed += (_, _) => WindowPlumbing.StartMoveDrag(this);
            titleRow.Children.Add(new TextBlock
            {
                Text = "计时器",
                FontSize = 12,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var closeButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE8BB", FontSize = 14 },
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Width = 40,
                Height = 40,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(4),
            };
            closeButton.Click += (_, _) => Close();
            titleRow.Children.Add(closeButton);

            _toggleButton.Click += OnToggleClicked;
            _resetButton.Click += (_, _) =>
            {
                _accumulated = TimeSpan.Zero;
                _startedAt = null;
                UpdateDisplay();
                UpdateToggleButton();
            };

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0),
            };
            buttonRow.Children.Add(_toggleButton);
            buttonRow.Children.Add(_resetButton);

            var panel = new StackPanel { Spacing = 8, MinWidth = 200 };
            panel.Children.Add(titleRow);
            panel.Children.Add(_display);
            panel.Children.Add(buttonRow);

            _card.Child = panel;
            _root.Children.Add(_card);
            Content = _root;

            _root.SizeChanged += (_, _) => UpdatePlacement();
            _timer.Tick += (_, _) => UpdateDisplay();

            ApplyTheme();
        }

        /// <summary>在工具按钮外缘打开，失败返回 false（调用方丢弃实例）。</summary>
        public bool ShowAt(int edgeX, int centerY, bool pointRight)
        {
            _edgeX = edgeX;
            _centerY = centerY;
            _pointRight = pointRight;

            ApplyTheme();
            UpdatePlacement();
            try
            {
                Activate();
            }
            catch (Exception ex)
            {
                CrashReporter.Log($"timer activate failed: {ex.Message}");
                Close();
                return false;
            }
            return true;
        }

        private void OnToggleClicked(object sender, RoutedEventArgs e)
        {
            if (_startedAt is null)
            {
                _startedAt = DateTimeOffset.Now;
                _timer.Start();
            }
            else
            {
                _accumulated += DateTimeOffset.Now - _startedAt.Value;
                _startedAt = null;
                _timer.Stop();
            }
            UpdateDisplay();
            UpdateToggleButton();
        }

        private void UpdateToggleButton()
        {
            _toggleButton.Content = _startedAt is null ? "开始" : "暂停";
        }

        private void UpdateDisplay()
        {
            var total = _accumulated;
            if (_startedAt is not null)
            {
                total += DateTimeOffset.Now - _startedAt.Value;
            }
            _display.Text = $"{(int)total.TotalMinutes:00}:{total.Seconds:00}";
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

            var label = new SolidColorBrush(isDark
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B));
            _display.Foreground = label;
            foreach (var child in ((StackPanel)_card.Child).Children)
            {
                if (child is Grid { } grid)
                {
                    foreach (var g in grid.Children)
                    {
                        if (g is TextBlock tb)
                        {
                            tb.Foreground = label;
                        }
                    }
                }
            }
        }
    }
}
