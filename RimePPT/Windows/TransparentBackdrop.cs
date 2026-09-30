using System;
using System.Threading;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RimePPT.Windows
{
    /// <summary>
    /// 全透明 SystemBackdrop：通过 ICompositionSupportsSystemBackdrop 挂一块
    /// Windows.UI.Composition 透明画刷，配合 WindowPlumbing.EnableTransparency 的
    /// DWM 管道，让窗口的 Transparent XAML 区域真正透视到桌面/放映画面。
    /// Compositor 全局共享一个：放映全屏切换瞬间新建 Compositor 可能被 DWM
    /// 拒绝（UnauthorizedAccessException，历史"莫名退出"元凶之一），
    /// 共享实例 + 短重试规避；首次成功后所有窗口复用。
    /// </summary>
    public sealed class TransparentBackdrop : SystemBackdrop
    {
        private static global::Windows.UI.Composition.Compositor? _compositor;
        private static readonly object CompositorLock = new();

        private static global::Windows.UI.Composition.Compositor GetCompositor()
        {
            if (_compositor is not null)
            {
                return _compositor;
            }

            lock (CompositorLock)
            {
                if (_compositor is not null)
                {
                    return _compositor;
                }

                // 瞬态失败窗口实测可超过 10 秒（强杀进程后 DWM 清理未完成），
                // 指数退避最多约 39s：放映启动瞬间 UI 线程无关键任务，可安全阻塞
                int[] backoffMs = { 0, 250, 500, 1000, 2000, 4000, 8000, 8000, 8000, 8000 };
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        _compositor = new global::Windows.UI.Composition.Compositor();
                        break;
                    }
                    catch (Exception ex) when (attempt < backoffMs.Length - 1)
                    {
                        Core.CrashReporter.Log($"compositor create retry {attempt + 1}: {ex.Message}");
                        Thread.Sleep(backoffMs[attempt + 1]);
                    }
                }

                return _compositor!;
            }
        }

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            // default(Color) = ARGB(0,0,0,0)，即全透明
            connectedTarget.SystemBackdrop = GetCompositor().CreateColorBrush(default(global::Windows.UI.Color));
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop connectedTarget)
        {
            // base 实现在 WinUI 2.3.x 的窗口关闭路径上有 AccessViolation 缺陷，
            // 只清引用、不调 base 以绕开（关窗后整个窗口随即销毁，无泄漏影响）。
            connectedTarget.SystemBackdrop = null;
        }
    }
}
