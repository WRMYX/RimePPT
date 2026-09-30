using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RimePPT.Controls;
using RimePPT.Core;
using RimePPT.Services;
using Windows.Graphics;
using Windows.UI;

namespace RimePPT.Windows
{
    /// <summary>
    /// 放映浮动的原生 Fluent 工具条窗口：置顶、无边框、不进 Alt-Tab、不抢放映焦点。
    /// 每个窗口只承载一张卡片，窗口与卡片同尺寸（内部无透明区域，避免玻璃底片外露）。
    /// 主题通过 ApplyTheme() 直接换刷子实现（不得改 RequestedTheme，见 XAML 注释）。
    /// </summary>
    public sealed partial class ToolbarWindow : Window
    {

        private static readonly Dictionary<ToolbarCommand, (string Glyph, string Label)> ButtonInfo = new()
        {
            [ToolbarCommand.Prev] = ("\uE76B", "上一页"),
            [ToolbarCommand.Next] = ("\uE76C", "下一页"),
            [ToolbarCommand.Annotate] = ("\uE70F", "批注"),
            [ToolbarCommand.Eraser] = ("\uE74D", "橡皮"),
            [ToolbarCommand.Tools] = ("\uE90F", "工具"),
            [ToolbarCommand.ExitShow] = ("\uE711", "退出"),
        };

        private static readonly Color LightForeground = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B);
        private static readonly Color DarkForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        private static readonly Color LightStroke = Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        private static readonly Color DarkStroke = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
        private static readonly Color LightBar = Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF);
        private static readonly Color DarkBar = Color.FromArgb(0xF0, 0x28, 0x28, 0x28);

        // 点击反馈：亮色 #005FB8（内容白），暗色 #60CDFF（内容黑）——在 ToolbarButton 内实现
        private static readonly Color LightPressedBackground = Color.FromArgb(0xFF, 0x00, 0x5F, 0xB8);
        private static readonly Color DarkPressedBackground = Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF);
        private static readonly Color LightPressedForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        private static readonly Color DarkPressedForeground = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);

        private readonly ToolbarLayout _layout;
        private readonly List<ToolbarButton> _buttons = new();
        private ElementTheme _theme = ElementTheme.Default;
        private DisplayArea? _targetArea;

        public event EventHandler<ToolbarCommand>? ToolbarClicked;

        public ToolbarWindow(ToolbarLayout layout, IReadOnlyList<ToolbarCommand> commands)
        {
            _layout = layout;
            InitializeComponent();
            SystemBackdrop = new TransparentBackdrop();
            BuildContent(commands);

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }
            AppWindow.IsShownInSwitchers = false;
            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

            // 先缩到极小，内容完成布局后由 UpdatePlacement 撑到真实尺寸，避免白板闪现
            AppWindow.Resize(new SizeInt32(1, 1));

            HostCard().SizeChanged += (_, _) => UpdatePlacement();

            ApplyTheme();
        }

        /// <summary>工具条布局（供 App 定位批注箭头窗）。</summary>
        public ToolbarLayout Layout => _layout;

        /// <summary>
        /// 指定命令按钮外侧中点的屏幕物理坐标（弹窗锚点）：
        /// 左侧栏在按钮右侧，右侧栏镜像在左侧。
        /// 点击时布局早已完成，TransformToVisual 量测可靠。
        /// </summary>
        public (global::Windows.Foundation.Point Anchor, bool PointRight) GetButtonAnchor(ToolbarCommand command)
        {
            ToolbarButton? target = null;
            foreach (var b in _buttons)
            {
                if (b.Command == command) { target = b; break; }
            }
            if (target is null)
            {
                return (default(global::Windows.Foundation.Point), _layout == ToolbarLayout.LeftRail);
            }

            bool pointRight = _layout == ToolbarLayout.LeftRail;
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;

            double centerY;
            try
            {
                var transform = target.TransformToVisual(RootGrid);
                var pt = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));
                centerY = pt.Y + target.ActualHeight / 2;
            }
            catch
            {
                // 量测异常时退回工具条垂直中心
                centerY = AppWindow.Size.Height / scale / 2;
            }

            int anchorX = pointRight ? AppWindow.Position.X + AppWindow.Size.Width + 4 : AppWindow.Position.X - 4;
            int anchorY = AppWindow.Position.Y + (int)Math.Round(centerY * scale);
            return (new global::Windows.Foundation.Point(anchorX, anchorY), pointRight);
        }

        /// <summary>笔按钮锚点（箭头窗用）。</summary>
        public (global::Windows.Foundation.Point Anchor, bool PointRight) GetPenAnchor()
        {
            return GetButtonAnchor(ToolbarCommand.Annotate);
        }

        /// <summary>应用用户设置（主题/按钮文字/边距），并重新定位。</summary>
        public void ApplySettings()
        {
            var settings = AppSettings.Instance;
            _theme = settings.Theme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            ApplyTheme();
            foreach (var button in _buttons)
            {
                button.SetShowText(settings.ShowToolbarText);
            }
            UpdatePlacement();
        }

        /// <summary>覆盖工具条主题：Default 跟随系统，Light/Dark 强制对应模式。</summary>
        public void SetTheme(ElementTheme theme)
        {
            _theme = theme;
            ApplyTheme();
        }

        /// <summary>显示在指定显示器上：侧栏垂直贴左右缘内缩 12px 居中，底栏贴底缘内缩 12px。</summary>
        public void ShowOn(DisplayArea area)
        {
            _targetArea = area;
            WindowPlumbing.ApplyNoActivate(this);
            WindowPlumbing.EnableTransparency(this);
            WindowPlumbing.RemoveWindowBorder(this);
            WindowPlumbing.RemoveResizableFrame(this);
            Activate();
            UpdatePlacement();
            ApplyTheme();
        }

        /// <summary>同步某命令按钮的选中态（批注/橡皮开关样态）。</summary>
        public void SetCommandChecked(ToolbarCommand command, bool isChecked)
        {
            foreach (var button in _buttons)
            {
                if (button.Command == command)
                {
                    button.SetChecked(isChecked);
                }
            }
        }

        private void BuildContent(IReadOnlyList<ToolbarCommand> commands)
        {
            switch (_layout)
            {
                case ToolbarLayout.LeftRail:
                case ToolbarLayout.RightRail:
                    SideCard.Visibility = Visibility.Visible;
                    FillList(SideList, commands);
                    break;
                case ToolbarLayout.BottomLeft:
                    BottomCard.Visibility = Visibility.Visible;
                    FillList(BottomList, new[] { ToolbarCommand.Prev, ToolbarCommand.Next });
                    break;
                case ToolbarLayout.BottomCenter:
                    BottomCard.Visibility = Visibility.Visible;
                    FillList(BottomList, new[]
                    {
                        ToolbarCommand.Annotate,
                        ToolbarCommand.Eraser,
                        ToolbarCommand.Tools,
                        ToolbarCommand.ExitShow,
                    });
                    break;
                default: // BottomRight
                    BottomCard.Visibility = Visibility.Visible;
                    FillList(BottomList, new[] { ToolbarCommand.Prev, ToolbarCommand.Next });
                    break;
            }
        }

        private void FillList(Microsoft.UI.Xaml.Controls.ItemsControl list, IReadOnlyList<ToolbarCommand> commands)
        {
            foreach (var command in commands)
            {
                var (glyph, label) = ButtonInfo[command];
                var button = new ToolbarButton { Icon = glyph, Label = label, Command = command };
                button.Clicked += OnButtonClicked;
                _buttons.Add(button);
                list.Items.Add(button);
            }
        }

        private Microsoft.UI.Xaml.Controls.Border HostCard()
        {
            return _layout == ToolbarLayout.LeftRail || _layout == ToolbarLayout.RightRail ? SideCard : BottomCard;
        }

        private void OnButtonClicked(object? sender, ToolbarCommand command)
        {
            ToolbarClicked?.Invoke(this, command);
        }

        private void ApplyTheme()
        {
            bool isDark = _theme == ElementTheme.Default
                ? RootGrid.ActualTheme == ElementTheme.Dark
                : _theme == ElementTheme.Dark;

            var bar = new AcrylicBrush
            {
                TintColor = isDark ? DarkBar : LightBar,
                TintOpacity = 1,
                FallbackColor = isDark ? DarkBar : LightBar,
            };
            var stroke = new SolidColorBrush(isDark ? DarkStroke : LightStroke);
            var foreground = new SolidColorBrush(isDark ? DarkForeground : LightForeground);

            var card = HostCard();
            card.Background = bar;
            card.BorderBrush = stroke;
            card.BorderThickness = new Thickness(1);

            foreach (var button in _buttons)
            {
                button.SetThemeColors(isDark);
                button.ApplyForeground(foreground);
            }
        }

        private void UpdatePlacement()
        {
            if (_targetArea is null)
            {
                return;
            }

            // 用期望尺寸（而非受窗口约束的实际尺寸）决定窗口大小，
            // 否则初始 1x1 窗口会把内容压扁并互相锁死
            var card = HostCard();
            card.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            int width = (int)Math.Ceiling(card.DesiredSize.Width * scale);
            int height = (int)Math.Ceiling(card.DesiredSize.Height * scale);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            AppWindow.Resize(new SizeInt32(width, height));

            // 以整块屏幕（OuterBounds）为基准，而不是扣除任务栏的工作区，
            // 边距来自用户设置
            double margin = AppSettings.Instance.EdgeMargin * scale;
            var bounds = _targetArea.OuterBounds;
            int x;
            int y;
            switch (_layout)
            {
                case ToolbarLayout.LeftRail:
                    x = bounds.X + (int)margin;
                    y = bounds.Y + (bounds.Height - height) / 2;
                    break;
                case ToolbarLayout.RightRail:
                    x = bounds.X + bounds.Width - width - (int)margin;
                    y = bounds.Y + (bounds.Height - height) / 2;
                    break;
                case ToolbarLayout.BottomLeft:
                    x = bounds.X + (int)margin;
                    y = bounds.Y + bounds.Height - height - (int)margin;
                    break;
                case ToolbarLayout.BottomCenter:
                    x = bounds.X + (bounds.Width - width) / 2;
                    y = bounds.Y + bounds.Height - height - (int)margin;
                    break;
                default: // BottomRight
                    x = bounds.X + bounds.Width - width - (int)margin;
                    y = bounds.Y + bounds.Height - height - (int)margin;
                    break;
            }

            AppWindow.Move(new PointInt32(x, y));
            WindowPlumbing.RemoveWindowBorder(this);
        }
    }
}
