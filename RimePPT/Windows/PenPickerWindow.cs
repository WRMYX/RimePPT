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
    /// 笔设置卡片窗（替代 Flyout——Flyout 会被裁剪在 30×66 的箭头小窗内显示不全）：
    /// 可激活的无边框亚克力卡，承载 6 个笔色圆点与粗细滑杆，实时写回设置。
    /// 全局同一时刻只保留一个实例（打开新的会关掉旧的）。
    /// </summary>
    public sealed class PenPickerWindow : Window
    {
        private static PenPickerWindow? _active;

        private readonly Grid _root = new();
        private readonly Border _card = new()
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
        };
        private readonly StackPanel _swatches = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        private readonly TextBlock _colorLabel = new() { Text = "笔颜色", FontSize = 12, Opacity = 0.7 };
        private readonly TextBlock _thicknessLabel = new()
        {
            Text = "笔粗细",
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        private readonly Slider _thickness = new()
        {
            Minimum = 2,
            Maximum = 10,
            StepFrequency = 1,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
        };
        private readonly TextBlock _thicknessValue = new()
        {
            FontSize = 12,
            MinWidth = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };
        private readonly List<Button> _swatchButtons = new();
        private bool _suppress = true;

        // 锚点：箭头窗外缘中点（ShowAt 时记录，布局回调重算位置用）
        private int _edgeX;
        private int _centerY;
        private bool _pointRight = true;

        private static readonly Color LightBar = Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF);
        private static readonly Color DarkBar = Color.FromArgb(0xF0, 0x28, 0x28, 0x28);
        private static readonly Color LightStroke = Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        private static readonly Color DarkStroke = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);

        public PenPickerWindow()
        {
            Title = "RimePPT";
            SystemBackdrop = new TransparentBackdrop();

            // 拾取窗是交互卡：可激活（与 PromptWindow 同规则）；
            // DWM alpha 路径必须开，否则透明/亚克力会透出黑底
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

            BuildContent();
            _root.Children.Add(_card);
            Content = _root;

            // 内容真实布局完成后再次量测撑窗（与 ToolbarWindow/PromptWindow 同模式）
            _root.SizeChanged += (_, _) => UpdatePlacement();

            Closed += (_, _) =>
            {
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }
            };

            ApplyTheme();
        }

        /// <summary>当前是否已有打开的拾取窗。</summary>
        public static bool IsOpen => _active is not null;

        private void BuildContent()
        {
            var panel = new StackPanel { Spacing = 10 };

            panel.Children.Add(_colorLabel);
            panel.Children.Add(_swatches);

            var thicknessRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            thicknessRow.Children.Add(_thicknessLabel);
            _thickness.ValueChanged += OnThicknessChanged;
            thicknessRow.Children.Add(_thickness);
            thicknessRow.Children.Add(_thicknessValue);
            panel.Children.Add(thicknessRow);

            _card.Child = panel;

            foreach (var (name, argb) in PenPalette.Presets)
            {
                var color = Color.FromArgb(argb[0], argb[1], argb[2], argb[3]);
                var swatch = new Button
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(22),
                    Background = new SolidColorBrush(color),
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0),
                    Tag = name,
                };
                ToolTipService.SetToolTip(swatch, name);
                swatch.Click += (_, _) =>
                {
                    AppSettings.Instance.PenColor = name;
                    AppSettings.Instance.Save();
                    HighlightSwatch(name);
                };
                _swatchButtons.Add(swatch);
                _swatches.Children.Add(swatch);
            }
        }

        /// <summary>在箭头窗外缘打开（全局唯一，重复打开会先关掉旧的）。
        /// 失败返回 false（窗口对象已失效，调用方应丢弃并重建）。</summary>
        public bool ShowAt(int edgeX, int centerY, bool pointRight)
        {
            if (_active is not null && !ReferenceEquals(_active, this))
            {
                _active.Close();
            }

            _edgeX = edgeX;
            _centerY = centerY;
            _pointRight = pointRight;

            ApplyTheme();
            RefreshFromSettings();

            // 先定位后激活：Activate 进行中触发布局回调再 Resize 会把
            // 激活状态打断（实测报 "Desktop Window object has already been closed"）
            UpdatePlacement();
            try
            {
                Activate();
            }
            catch (Exception ex)
            {
                CrashReporter.Log($"picker activate failed: {ex.Message}");
                Close();
                return false;
            }

            _active = this;
            return true;
        }

        public void Dismiss()
        {
            Close();
        }

        /// <summary>书写置顶时随工具条同步重提。</summary>
        public void Raise()
        {
            WindowPlumbing.RaiseToTopmost(this);
        }

        private void UpdatePlacement()
        {
            // 窗口与卡片同尺寸：Measure(∞) 后按窗口 DPI 换算成物理像素
            // （XamlRoot 在 NOACTIVATE/未激活窗口上不可靠，统一用 GetDpiForWindow）
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

        private void RefreshFromSettings()
        {
            _suppress = true;
            _thickness.Value = AppSettings.Instance.PenThickness;
            _thicknessValue.Text = ((int)AppSettings.Instance.PenThickness).ToString();
            HighlightSwatch(AppSettings.Instance.PenColor);
            _suppress = false;
        }

        private void HighlightSwatch(string? selectedName)
        {
            var accent = (Color)Application.Current.Resources["SystemAccentColor"];
            foreach (var swatch in _swatchButtons)
            {
                bool selected = string.Equals((string)swatch.Tag, selectedName, StringComparison.OrdinalIgnoreCase);
                swatch.BorderThickness = new Thickness(selected ? 3 : 0);
                swatch.BorderBrush = new SolidColorBrush(selected ? accent : Color.FromArgb(0, 0, 0, 0));
            }
        }

        private void OnThicknessChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppress)
            {
                return;
            }
            _thicknessValue.Text = ((int)_thickness.Value).ToString();
            AppSettings.Instance.PenThickness = (int)_thickness.Value;
            AppSettings.Instance.Save();
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private void ApplyTheme()
        {
            // 与工具条同规则：设置强制浅/深色优先，否则跟随系统（注册表判定）
            bool isDark = ThemeHelper.IsDarkTheme();

            _card.Background = new AcrylicBrush
            {
                TintColor = isDark ? DarkBar : LightBar,
                TintOpacity = 1,
                FallbackColor = isDark ? DarkBar : LightBar,
            };
            _card.BorderBrush = new SolidColorBrush(isDark ? DarkStroke : LightStroke);
            _card.BorderThickness = new Thickness(1);

            // 卡片配色可与窗口主题相反（设置强制浅色 + 系统深色），
            // 标签前景必须跟卡片走，否则默认前景会淡到看不清
            var label = new SolidColorBrush(isDark
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B));
            _colorLabel.Foreground = label;
            _thicknessLabel.Foreground = label;
            _thicknessValue.Foreground = label;
        }
    }
}
