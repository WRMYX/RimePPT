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
            [ToolbarCommand.Eraser] = ("\uE75C", "橡皮"),
            [ToolbarCommand.Tools] = ("\uE90F", "工具"),
            [ToolbarCommand.Pages] = ("\uE8A5", "页面导航"),
            [ToolbarCommand.Undo] = ("\uE7A7", "撤销"),
            [ToolbarCommand.Redo] = ("\uE7A6", "重做"),
            [ToolbarCommand.ExitShow] = ("\uE711", "退出"),
        };


        // 点击反馈：亮色 #005FB8（内容白），暗色 #60CDFF（内容黑）——在 ToolbarButton 内实现

        private readonly ToolbarLayout _layout;
        private readonly List<ToolbarButton> _buttons = new();
        private ElementTheme _theme = ElementTheme.Default;
        private DisplayArea? _targetArea;
        private bool _firstPlacement = true;
        private PointInt32 _entranceTarget;
        public void BeginEntrance()
        {
            if (!_firstPlacement || _closing || _targetArea is null) return;
            _firstPlacement = false;
            ToolbarEntranceAnimator.Start(this, _layout, _targetArea.OuterBounds, _entranceTarget);
        }
        private bool _closing;
        private ToolArrowWindow? _toolArrow;
        private ToolbarCommand? _arrowCommand;

        public event EventHandler<ToolbarCommand>? ToolbarClicked;
        public event EventHandler<ToolbarCommand>? ToolSettingsRequested;

        public ToolbarWindow(ToolbarLayout layout, IReadOnlyList<ToolbarCommand> commands)
        {
            _layout = layout;
            InitializeComponent();
            SystemBackdrop = new DesktopAcrylicBackdrop();
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

            ThemeResources.Attach(this, RootGrid, HostCard());
            ApplyTheme();
            Closed += (_, _) => { _toolArrow?.Dismiss(); _toolArrow = null; };
        }

        /// <summary>工具条布局（供 App 定位批注箭头窗）。</summary>
        public ToolbarLayout Layout => _layout;
        internal void SetGuidePreview(bool enabled)
        {
            foreach (var button in _buttons) button.SetGuidePreview(enabled);
        }
        internal void PreviewGuideCommand(ToolbarCommand command)
        {
            foreach (var button in _buttons)
                if (button.Command is ToolbarCommand.Annotate or ToolbarCommand.Eraser or ToolbarCommand.Tools)
                    button.SetGuideSelected(button.Command == command);
        }
        internal TeachingTip CreateGuideTip()
        {
            var tip = new TeachingTip
            {
                ShouldConstrainToRootBounds = false,
                IsLightDismissEnabled = false,
                CloseButtonContent = "跳过",
                ActionButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            RootGrid.Children.Add(tip);
            return tip;
        }
        internal void RemoveGuideTip(TeachingTip tip) => RootGrid.Children.Remove(tip);

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

        internal int GetButtonCenterX(ToolbarCommand command)
        {
            var button = _buttons.First(b => b.Command == command);
            var point = button.TransformToVisual(RootGrid).TransformPoint(new global::Windows.Foundation.Point());
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
            return AppWindow.Position.X + (int)Math.Round((point.X + button.ActualWidth / 2) * scale);
        }

        public FrameworkElement? GetCommandTarget(ToolbarCommand command) => _buttons.FirstOrDefault(b => b.Command == command);
        public void UpdatePresentation(int current, int total, bool canUndo, bool canRedo)
        {
            foreach (var button in _buttons)
            {
                if (button.Command == ToolbarCommand.Pages) button.SetPageNumber(current, total);
                if (button.Command == ToolbarCommand.Undo) button.SetEnabled(canUndo);
                if (button.Command == ToolbarCommand.Redo) button.SetEnabled(canRedo);
            }
            UpdatePlacement();
        }
        public async void Dismiss()
        {
            if (_closing) return;
            _closing = true; RootGrid.IsHitTestVisible = false;
            _toolArrow?.Dismiss(); _toolArrow = null;
            try { if (_targetArea is not null) await ToolbarEntranceAnimator.ExitAsync(this, _layout, _targetArea.OuterBounds); }
            catch (Exception ex) { CrashReporter.Log($"toolbar exit: {ex.Message}"); }
            finally { Close(); }
        }
        public void RaiseArrow() => _toolArrow?.Raise();
        public FrameworkElement? GetToolSettingsTarget(ToolbarCommand command)
            => _arrowCommand == command ? _toolArrow?.SettingsTarget : null;
        public (global::Windows.Foundation.Point Anchor, bool PointRight) GetSettingsAnchor(ToolbarCommand command)
            => _arrowCommand == command && _toolArrow is not null ? _toolArrow.SettingsAnchor() : GetButtonAnchor(command);

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
                button.UpdatePenColor();
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
            WindowPlumbing.ApplyPointerNoActivate(this);
            WindowPlumbing.EnableTransparency(this);
            WindowPlumbing.RemoveWindowBorder(this);
            WindowPlumbing.RemoveResizableFrame(this);
            UpdatePlacement();
            Activate();
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
            if (!_buttons.Any(b => b.Command == command)) return;
            if (isChecked && _arrowCommand != command)
            {
                _toolArrow?.Dismiss(); _arrowCommand = command;
                _toolArrow = new ToolArrowWindow(this, command, () => ToolSettingsRequested?.Invoke(this, command));
            }
            else if (!isChecked && _arrowCommand == command)
            {
                _toolArrow?.Dismiss(); _toolArrow = null; _arrowCommand = null;
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
                    FillList(BottomList, new[] { ToolbarCommand.Prev, ToolbarCommand.Pages, ToolbarCommand.Next });
                    break;
                case ToolbarLayout.BottomCenter:
                    BottomCard.Visibility = Visibility.Visible;
                    FillList(BottomList, new[]
                    {
                        ToolbarCommand.Annotate,
                        ToolbarCommand.Eraser,
                        ToolbarCommand.Undo,
                        ToolbarCommand.Redo,
                        ToolbarCommand.Tools,
                        ToolbarCommand.ExitShow,
                    });
                    break;
                default: // BottomRight
                    BottomCard.Visibility = Visibility.Visible;
                    FillList(BottomList, new[] { ToolbarCommand.Prev, ToolbarCommand.Pages, ToolbarCommand.Next });
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
            RootGrid.RequestedTheme = _theme;
            HostCard().BorderThickness = new Thickness(1);
        }

        private void UpdatePlacement()
        {
            if (_targetArea is null || _closing)
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

            var target = new PointInt32(x,y);
            if (_firstPlacement)
            {
                _entranceTarget = target;
                AppWindow.Move(_layout switch
                {
                    ToolbarLayout.LeftRail => new PointInt32(bounds.X - width, y),
                    ToolbarLayout.RightRail => new PointInt32(bounds.X + bounds.Width, y),
                    _ => new PointInt32(x, bounds.Y + bounds.Height)
                });
            }
            else if (!ToolbarEntranceAnimator.Retarget(this, target)) AppWindow.Move(target);
            WindowPlumbing.RemoveWindowBorder(this);
        }
    }
}
