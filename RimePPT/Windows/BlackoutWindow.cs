using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using RimePPT.Services;
using Windows.Graphics;

namespace RimePPT.Windows
{
    /// <summary>
    /// 黑屏遮罩：放映时全屏纯黑（含任务栏区域），单击任意位置退出。
    /// 不抢焦点（NOACTIVATE），置顶于批注层与工具条之上；
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
                Text = "单击任意位置退出黑屏",
                FontSize = 18,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 48),
            };
            _root.Children.Add(hint);
            Content = _root;

            WindowPlumbing.ApplyNoActivate(this);
            WindowPlumbing.RemoveWindowBorder(this);
            WindowPlumbing.RemoveResizableFrame(this);
            // 纯黑不透明：无需 DWM 透明管道

            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }
            AppWindow.IsShownInSwitchers = false;

            _root.PointerReleased += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
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
