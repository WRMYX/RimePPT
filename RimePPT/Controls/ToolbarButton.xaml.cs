using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RimePPT.Core;
using Windows.UI;

namespace RimePPT.Controls
{
    /// <summary>
    /// 工具条按钮：图标+文字的原生 Button 封装。
    /// 按压反馈用指针事件直接换色（亮 #005FB8/白字，暗 #60CDFF/黑字），
    /// 不走模板按压态——Fluent 的 Pressed 状态会叠烟雾层导致颜色偏差。
    /// </summary>
    public sealed partial class ToolbarButton : UserControl
    {
        public event EventHandler<ToolbarCommand>? Clicked;

        private static readonly Color LightPressed = Color.FromArgb(0xFF, 0x00, 0x5F, 0xB8);
        private static readonly Color DarkPressed = Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF);
        private static readonly Color LightPressedForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        private static readonly Color DarkPressedForeground = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        private static readonly Color NormalBackground = Color.FromArgb(0x00, 0x00, 0x00, 0x00);

        private bool _isDark;
        private bool _captured;
        private bool _isChecked;
        private Brush? _normalForeground;
        private readonly DispatcherTimer _restoreTimer;

        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(string), typeof(ToolbarButton), new PropertyMetadata(string.Empty, OnIconChanged));

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(ToolbarButton), new PropertyMetadata(string.Empty, OnLabelChanged));

        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.Register(nameof(Command), typeof(ToolbarCommand), typeof(ToolbarButton), new PropertyMetadata(ToolbarCommand.Tools));

        public string Icon
        {
            get => (string)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public ToolbarCommand Command
        {
            get => (ToolbarCommand)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public ToolbarButton()
        {
            InitializeComponent();

            // 按压反馈由 UserControl 层接管（RootButton 退出命中测试）：
            // Button 的 Pressed 视觉状态会强制回写背景色，无法精确控制颜色
            Background = new SolidColorBrush(NormalBackground);
            RootButton.IsHitTestVisible = false;

            // 反馈色保持 150ms 再恢复：足以感知点击，又不拖沓
            _restoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _restoreTimer.Tick += OnRestoreTimerTick;

            PointerPressed += OnPointerPressed;
            PointerReleased += OnPointerReleased;
            PointerCanceled += OnPointerReleased;
            PointerCaptureLost += OnPointerReleased;
        }

        /// <summary>常规态前景色（由宿主窗口按主题下发）。</summary>
        public void ApplyForeground(SolidColorBrush brush)
        {
            _normalForeground = brush;
            IconGlyph.Foreground = brush;
            LabelText.Foreground = brush;
        }

        /// <summary>主题标记（由宿主窗口按主题下发，决定按压强调色）。</summary>
        public void SetThemeColors(bool isDark)
        {
            _isDark = isDark;
            if (_isChecked)
            {
                ApplyCheckedVisual();
            }
        }

        /// <summary>是否显示按钮文字（显示文字关闭时只留图标）。</summary>
        public void SetShowText(bool show)
        {
            LabelText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>开关型按钮的选中态（批注/橡皮）：选中即以强调色常驻。</summary>
        public void SetChecked(bool isChecked)
        {
            _isChecked = isChecked;
            if (isChecked)
            {
                _restoreTimer.Stop();
                ApplyCheckedVisual();
            }
            else
            {
                RestoreNormalVisual();
            }
        }

        private void ApplyCheckedVisual()
        {
            RootButton.Background = new SolidColorBrush(_isDark ? DarkPressed : LightPressed);
            var foreground = new SolidColorBrush(_isDark ? DarkPressedForeground : LightPressedForeground);
            IconGlyph.Foreground = foreground;
            LabelText.Foreground = foreground;
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _restoreTimer.Stop();
            RootButton.Background = new SolidColorBrush(_isDark ? DarkPressed : LightPressed);
            var foreground = new SolidColorBrush(_isDark ? DarkPressedForeground : LightPressedForeground);
            IconGlyph.Foreground = foreground;
            LabelText.Foreground = foreground;
            CapturePointer(e.Pointer);
            _captured = true;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_captured)
            {
                _captured = false;
                ReleasePointerCapture(e.Pointer);
                Clicked?.Invoke(this, Command);
            }

            // 反馈色保持 150ms 再恢复，避免松手瞬间颜色立即消失
            _restoreTimer.Stop();
            _restoreTimer.Start();
        }

        private void OnRestoreTimerTick(object? sender, object e)
        {
            _restoreTimer.Stop();
            if (_isChecked)
            {
                ApplyCheckedVisual();
                return;
            }
            RestoreNormalVisual();
        }

        private void RestoreNormalVisual()
        {
            RootButton.Background = new SolidColorBrush(NormalBackground);
            if (_normalForeground is not null)
            {
                IconGlyph.Foreground = _normalForeground;
                LabelText.Foreground = _normalForeground;
            }
        }

        private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ToolbarButton)d).IconGlyph.Glyph = (string)e.NewValue;
        }

        private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ToolbarButton)d).LabelText.Text = (string)e.NewValue;
        }
    }
}
