using System;
using System.Threading.Tasks;

namespace RimePPT.Core
{
    /// <summary>
    /// 调试用模拟控制器：无 Office 时驱动工具条/批注流程。
    /// 所有事件在调用者线程触发。
    /// </summary>
    public sealed class DebugPresentationController : IPresentationController
    {
        /// <summary>本次模拟放映要显示的工具条布局（由调试面板设置）。</summary>
        public ToolbarLayout[] DebugLayouts { get; set; } = { ToolbarLayout.LeftRail, ToolbarLayout.RightRail };

        public int SlideCount { get; private set; } = 5;

        public int CurrentSlide { get; private set; } = 1;

        public IntPtr ShowWindowHandle => IntPtr.Zero;

        public string? ShowFilePath => null;

        public bool IsPresenting { get; private set; }

        public event EventHandler? ShowStarted;

        public event EventHandler<SlideEndedReason>? ShowEnded;

        public event EventHandler<int>? SlideChanged;

        public void Start()
        {
        }

        public void RaiseShowStarted()
        {
            CurrentSlide = 1;
            IsPresenting = true;
            ShowStarted?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseShowEnded(SlideEndedReason reason = SlideEndedReason.UserExit)
        {
            IsPresenting = false;
            ShowEnded?.Invoke(this, reason);
        }

        public PresentationCapabilities Capabilities => new(false, true, false);
        public Task SetNativePointerAsync(NativePointerTool tool, byte[]? argb) => Task.FromException(new NotSupportedException("模拟放映没有 PowerPoint 原生笔。"));
        public Task ClearNativeInkAsync() => Task.FromException(new NotSupportedException("模拟放映没有原生墨迹。"));
        public Task<string> ExportSlideThumbnailAsync(int slideIndex, string destinationPath, int pixelWidth, System.Threading.CancellationToken cancellationToken)
            => Task.FromException<string>(new NotSupportedException("模拟页面由导航窗口生成示意卡片。"));
        public Task GoToSlideAsync(int slideIndex)
        {
            if (!IsPresenting || slideIndex < 1 || slideIndex > SlideCount) throw new ArgumentOutOfRangeException(nameof(slideIndex));
            CurrentSlide = slideIndex; SlideChanged?.Invoke(this, slideIndex); return Task.CompletedTask;
        }

        public Task NextAsync()
        {
            Step(1);
            return Task.CompletedTask;
        }

        public Task PreviousAsync()
        {
            Step(-1);
            return Task.CompletedTask;
        }

        public Task ExitShowAsync()
        {
            RaiseShowEnded();
            return Task.CompletedTask;
        }

        private void Step(int delta)
        {
            if (!IsPresenting)
            {
                return;
            }

            int next = CurrentSlide + delta;
            if (next < 1 || next > SlideCount)
            {
                return;
            }

            CurrentSlide = next;
            SlideChanged?.Invoke(this, CurrentSlide);
        }
    }
}
