using System;
using System.Threading.Tasks;

namespace RimePPT.Core
{
    /// <summary>放映结束的原因。</summary>
    public enum SlideEndedReason
    {
        /// <summary>用户主动退出放映。</summary>
        UserExit,

        /// <summary>放映自然播完或演示软件关闭。</summary>
        PresentationClosed,
    }

    /// <summary>
    /// 演示控制抽象：统一 PowerPoint COM 实现与调试模拟实现，
    /// 事件可能从非 UI 线程触发，订阅方需自行 marshal。
    /// </summary>
    public interface IPresentationController
    {
        event EventHandler? ShowStarted;

        event EventHandler<SlideEndedReason>? ShowEnded;

        event EventHandler<int>? SlideChanged;

        /// <summary>当前页码（从 1 开始）。</summary>
        int CurrentSlide { get; }

        /// <summary>总页数；未知时为 0。</summary>
        int SlideCount { get; }

        /// <summary>放映窗口 HWND；未放映时为 IntPtr.Zero（用于定位所在显示器）。</summary>
        IntPtr ShowWindowHandle { get; }

        /// <summary>正在放映的课件完整路径；未知/调试模式为 null。</summary>
        string? ShowFilePath { get; }

        bool IsPresenting { get; }

        /// <summary>启动监控（幂等）。</summary>
        void Start();

        Task NextAsync();

        Task PreviousAsync();

        Task ExitShowAsync();
    }
}
