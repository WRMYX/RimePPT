using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using RimePPT.Services;
using Windows.Graphics;

namespace RimePPT.Windows
{
    /// <summary>
    /// 黑屏遮罩：放映时全屏纯黑（含任务栏区域），单击任意位置退出。
    /// 激活后支持键盘退出，置顶于批注层与工具条之上；
    /// 文本提示用白色（黑底上恒定可读，不随主题）。
    /// </summary>
    public sealed class BlackoutWindow : Window
    {
        private readonly Grid _root = new() { Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0, 0, 0)) };

        /// <summary>用户单击黑屏要求退出（App 侧关闭窗口）。</summary>
        public event EventHandler? ExitRequested;

        public BlackoutWindow()
        {
            Title = "RimePPT 黑屏";

            var hint = new TextBlock
            {
                Text = "点击屏幕或按 Esc 退出黑屏",
                FontSize = 18,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 48),
            };
            _root.Children.Add(hint);
            Content = _root;

            WindowPlumbing.RemoveWindowBorder(this);
            WindowPlumbing.RemoveResizableFrame(this);
            // 纯黑不透明：无需 DWM 透明管道

            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }
            AppWindow.IsShownInSwitchers = false;

            uint? pointer = null;
            _root.PointerPressed += (_, e) => { if (pointer is null && _root.CapturePointer(e.Pointer)) pointer = e.Pointer.PointerId; };
            _root.PointerReleased += (_, e) => { if (pointer != e.Pointer.PointerId) return; pointer = null; _root.ReleasePointerCapture(e.Pointer); ExitRequested?.Invoke(this, EventArgs.Empty); };
            _root.PointerCanceled += (_, e) => { pointer = null; _root.ReleasePointerCapture(e.Pointer); };
            _root.PointerCaptureLost += (_, _) => pointer = null;
            var escape = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
            escape.Invoked += (_, e) => { e.Handled = true; ExitRequested?.Invoke(this, EventArgs.Empty); };
            _root.KeyboardAccelerators.Add(escape);
        }

        /// <summary>覆盖指定显示器全屏并置顶。</summary>
        public void ShowOn(DisplayArea area)
        {
            var bounds = area.OuterBounds;
            AppWindow.Resize(new SizeInt32(bounds.Width, bounds.Height));
            AppWindow.Move(new PointInt32(bounds.X, bounds.Y));
            Activate();
            // 罩住工具条/批注层（黑屏后由本窗自身单击退出）
            WindowPlumbing.RaiseToTopmost(this);
        }
    }
}
