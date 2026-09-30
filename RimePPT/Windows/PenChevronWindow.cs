using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using RimePPT.Core;
using RimePPT.Services;
using Windows.Graphics;
using Windows.UI;

namespace RimePPT.Windows
{
    /// <summary>
    /// 批注启用时的独立箭头按钮窗：FlipView/PipsPager 导航按钮观感
    /// （圆角矩形 + chevron 字形，浅色=白底深箭头 / 深色=黑底白箭头，与工具条同色），
    /// 尺寸按 DIP 设计（28×56），触摸目标充足；垂直对齐笔按钮、位于工具条外侧、
    /// 指向幻灯片方向，点击开/关笔设置拾取窗（PenPickerWindow）。不与工具条一体。
    /// </summary>
    public sealed class PenChevronWindow : Window
    {
        // DIP 设计尺寸（物理尺寸在 Show 时按窗口 DPI 换算）
        private const double ButtonWidthDip = 28;
        private const double ButtonHeightDip = 56;

        private readonly Grid _root = new() { Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
        private readonly Button _chevron = new()
        {
            Width = ButtonWidthDip,
            Height = ButtonHeightDip,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        private readonly FontIcon _chevronIcon = new() { FontSize = 14 };
        private PenPickerWindow? _picker;
        private bool _pickerOpen;
        private bool _pressed;

        // 与工具条卡片同色系
        private static readonly Color LightBar = Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF);
        private static readonly Color DarkBar = Color.FromArgb(0xF0, 0x28, 0x28, 0x28);
        private static readonly Color LightForeground = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B);
        private static readonly Color DarkForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        // 按压反馈：与工具条按钮同款强调色
        private static readonly Color LightPressed = Color.FromArgb(0xFF, 0x00, 0x5F, 0xB8);
        private static readonly Color DarkPressed = Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF);
        private static readonly Color LightPressedForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        private static readonly Color DarkPressedForeground = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);

        private bool _pointRight = true;
        // 笔按钮外侧中点锚点（PositionAt 记录，Show 时换算成窗口左上角）
        private int _anchorX;
        private int _anchorY;

        public PenChevronWindow(bool pointRight)
        {
            _pointRight = pointRight;

            SystemBackdrop = new TransparentBackdrop();
            Title = "RimePPT";

            // 不抢焦点，但本体可点击
            WindowPlumbing.ApplyNoActivate(this);
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

            // Button 退出命中测试：杜绝模板视觉状态（Normal/Hover/Pressed）
            // 异步重应用时覆盖手动颜色（实测同批两窗可能一深一浅的根源）；
            // 点击由外层 Grid 指针事件接管（与 ToolbarButton 同模式）
            _chevron.IsHitTestVisible = false;
            _chevron.Content = _chevronIcon;
            _root.Children.Add(_chevron);
            Content = _root;

            _root.PointerPressed += OnPointerPressed;
            _root.PointerReleased += OnPointerReleased;
            _root.PointerCanceled += OnPointerReleased;
            _root.PointerCaptureLost += OnPointerReleased;

            // 箭头窗关闭（放映结束/取消批注）时联动收掉拾取窗
            Closed += (_, _) => _picker?.Dismiss();

            ApplyTheme();
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _pressed = true;
            _root.CapturePointer(e.Pointer);
            _chevron.Background = new SolidColorBrush(_isDark ? DarkPressed : LightPressed);
            _chevron.Foreground = new SolidColorBrush(_isDark ? DarkPressedForeground : LightPressedForeground);
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_pressed)
            {
                return;
            }
            _pressed = false;
            _root.ReleasePointerCapture(e.Pointer);
            ApplyTheme();
            TogglePicker();
        }

        /// <summary>重提本窗到置顶带顶端（书写置顶时随批注层同步）。</summary>
        public void Raise()
        {
            WindowPlumbing.RaiseToTopmost(this);
            _picker?.Raise();
        }

        /// <summary>屏幕物理坐标（窗口左上角）。</summary>
        public global::Windows.Foundation.Point Position { get; private set; }

        /// <summary>设置位置与朝向（调用 Show 生效）。</summary>
        public void PositionAt(int anchorX, int anchorY, bool pointRight)
        {
            _pointRight = pointRight;
            _anchorX = anchorX;
            _anchorY = anchorY;
            _chevronIcon.Glyph = pointRight ? "\uE76C" : "\uE76B";
        }

        /// <summary>显示箭头窗（垂直与笔按钮对齐）。</summary>
        public void Show()
        {
            Activate();
            ApplyTheme();

            // 物理尺寸 = DIP × 窗口 DPI 缩放（XamlRoot 在 NOACTIVATE 窗口上
            // 可能拿不到/返回 1.0，必须用 Win32 的 GetDpiForWindow）
            double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            int widthPx = (int)Math.Ceiling(ButtonWidthDip * scale);
            int heightPx = (int)Math.Ceiling(ButtonHeightDip * scale);
            AppWindow.Resize(new SizeInt32(widthPx, heightPx));

            // 锚点 = 笔按钮外侧中点：左栏在锚点右侧，右栏镜像在左侧
            int x = _pointRight ? _anchorX + 4 : _anchorX - widthPx - 4;
            int y = _anchorY - heightPx / 2;
            Position = new global::Windows.Foundation.Point(x, y);
            AppWindow.Move(new PointInt32(x, y));
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private void TogglePicker()
        {
            if (_pickerOpen)
            {
                _picker?.Dismiss();
                _pickerOpen = false;
                return;
            }

            _picker ??= new PenPickerWindow();
            // 拾取窗贴箭头窗外缘：左栏在箭头右侧，右栏镜像在左侧
            int edgeX = _pointRight ? (int)Position.X + AppWindow.Size.Width : (int)Position.X;
            int centerY = (int)Position.Y + AppWindow.Size.Height / 2;
            if (!_picker.ShowAt(edgeX, centerY, _pointRight))
            {
                // 窗口对象已失效：丢弃，下次点击用全新窗口重试
                _picker = null;
                _pickerOpen = false;
                return;
            }
            _pickerOpen = true;
        }

        private bool _isDark;

        private void ApplyTheme()
        {
            // 与工具条同规则：设置强制浅/深色优先，否则跟随系统。
            // 不用 _root.ActualTheme：实测同批创建的两个窗口可能读到不一致的
            // ActualTheme（一个 Dark 一个 Light），改读系统注册表保证确定性
            _isDark = ThemeHelper.IsDarkTheme();

            // 与工具条卡片同色：浅色=白底深箭头，深色=黑底白箭头；
            // 按压中保持强调色反馈不被冲掉
            if (!_pressed)
            {
                _chevron.Background = new SolidColorBrush(_isDark ? DarkBar : LightBar);
                _chevron.Foreground = new SolidColorBrush(_isDark ? DarkForeground : LightForeground);
            }
        }
    }
}
