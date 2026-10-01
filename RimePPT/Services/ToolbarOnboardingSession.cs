using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RimePPT.Core;
using RimePPT.Windows;

namespace RimePPT.Services;

/// <summary>一次只打开一个原生 TeachingTip，目标始终来自实际工具栏按钮。</summary>
internal sealed class ToolbarOnboardingSession
{
    private sealed record Step(ToolbarWindow Window, FrameworkElement Target, string Title, string Description);
    private readonly List<Step> _steps = new();
    private TeachingTip? _tip;
    private ToolbarWindow? _host;
    private int _version;
    private int _index;
    private bool _advance;
    private bool _waiting;
    private bool _marked;
    private TextBlock? _tipContent;
    private Task _closing = Task.CompletedTask;
    internal bool IsRunning => _waiting || _tip is not null;

    private static readonly (ToolbarCommand Command, string Title, string Description)[] Lessons =
    {
        (ToolbarCommand.Prev, "上一页", "点击这里返回上一张幻灯片。工具栏会在开始放映后出现，结束放映后收起。"),
        (ToolbarCommand.Next, "下一页", "点击这里继续到下一张幻灯片，按自己的讲解节奏控制演示。"),
        (ToolbarCommand.Pages, "页面导航", "点击页码打开幻灯片导航，再选择要跳转的页面。"),
        (ToolbarCommand.Annotate, "批注画笔", "点击进入书写模式，在幻灯片上标记重点。选中后，旁边的展开箭头可调整颜色和粗细。"),
        (ToolbarCommand.Eraser, "橡皮", "点击切换到擦除。自研书写模式下，可通过展开箭头调整橡皮尺寸。"),
        (ToolbarCommand.Undo, "撤销", "自研书写模式支持逐页撤销最近的批注操作。有可撤销内容时，这个按钮才会启用。"),
        (ToolbarCommand.Redo, "重做", "撤销后可以恢复操作。重做按钮会根据当前页面的批注历史启用。"),
        (ToolbarCommand.Tools, "辅助工具", "点击打开工具菜单，使用聚光与放大、黑屏模式和计时器。"),
        (ToolbarCommand.ExitShow, "退出放映", "点击结束演示。自研模式产生了新墨迹时，会按实际状态询问是否保存。"),
    };

    internal async Task StartAsync(IReadOnlyList<ToolbarWindow> windows, bool force = false)
    {
        Stop();
        if (!force && !OnboardingState.ShouldShow()) return;
        int version = _version;
        _waiting = true;
        try
        {
            await Task.WhenAll(windows.Select(ToolbarEntranceAnimator.WaitForEntranceAsync).Append(_closing));
            if (version != _version) return;
            _waiting = false;
            foreach (var lesson in Lessons)
            {
                foreach (var window in windows)
                {
                    var target = window.GetCommandTarget(lesson.Command);
                    // TeachingTip 绑定/解绑 Loaded 时会暂时使目标 IsLoaded=false。
                    // 入场已完成，使用实际布局尺寸和可见性判断，避免重置时漏掉按钮。
                    if (target is null || target.Visibility != Visibility.Visible || target.ActualWidth <= 0 || target.ActualHeight <= 0) continue;
                    _steps.Add(new Step(window, target, lesson.Title, lesson.Description));
                    break; // 多个窗口重复的命令只讲解一次。
                }
            }
            if (_steps.Count > 0) ShowStep(0);
        }
        catch (Exception ex)
        {
            if (version == _version) Stop();
            CrashReporter.Report(ex, "toolbar-onboarding");
        }
    }

    internal void Stop()
    {
        _version++;
        _waiting = false;
        _advance = false;
        DetachTip();
        _steps.Clear();
        _marked = false;
    }

    private void ShowStep(int index)
    {
        _index = index;
        var step = _steps[index];
        _host = step.Window;
        _tip = step.Window.GuideTip;
        _tip.Target = step.Target;
        _tip.Title = $"{step.Title} · {index + 1} / {_steps.Count}";
        _tipContent = new TextBlock { Text = step.Description, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
        _tipContent.Loaded += OnContentLoaded;
        _tip.Content = _tipContent;
        _tip.ActionButtonContent = index == _steps.Count - 1 ? "完成" : "下一步";
        _tip.PreferredPlacement = step.Window.Layout switch
        {
            ToolbarLayout.LeftRail => TeachingTipPlacementMode.Right,
            ToolbarLayout.RightRail => TeachingTipPlacementMode.Left,
            ToolbarLayout.BottomLeft => TeachingTipPlacementMode.TopRight,
            ToolbarLayout.BottomRight => TeachingTipPlacementMode.TopLeft,
            _ => TeachingTipPlacementMode.Top,
        };
        _tip.ActionButtonClick += OnNext;
        _tip.CloseButtonClick += OnSkip;
        _tip.Closed += OnClosed;
        _host.Closed += OnHostClosed;
        _tip.IsOpen = true;
    }

    private void OnContentLoaded(object sender, RoutedEventArgs args)
    {
        // TeachingTip 没有 Opened 事件；正文进入 popup 的可视树后才消耗首次引导。
        if (_marked || !ReferenceEquals(sender, _tipContent) || _tip?.IsOpen != true) return;
        _marked = true;
        OnboardingState.TryMarkShown();
    }

    private void OnNext(TeachingTip sender, object args)
    {
        _advance = _index + 1 < _steps.Count;
        RequestClose(sender);
    }

    private void OnSkip(TeachingTip sender, object args)
    {
        _advance = false;
        RequestClose(sender);
    }

    private void RequestClose(TeachingTip tip)
    {
        int version = _version, index = _index;
        var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        retry.Tick += (_, _) =>
        {
            if (version != _version || index != _index || !ReferenceEquals(_tip, tip))
            {
                retry.Stop();
                return;
            }
            if (tip.IsOpen) tip.IsOpen = false;
        };
        retry.Start();
        tip.IsOpen = false;
    }

    private void OnClosed(TeachingTip sender, TeachingTipClosedEventArgs args)
    {
        if (!ReferenceEquals(sender, _tip)) return;
        bool advance = _advance;
        _advance = false;
        DetachTip();
        if (!advance) { Stop(); return; }
        int version = _version;
        // Closed 后下一轮 UI 消息再打开，避免原生 popup 的关闭/开启生命周期重叠。
        if (!sender.DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _version) return;
            try { ShowStep(_index + 1); }
            catch (Exception ex) { Stop(); CrashReporter.Report(ex, "toolbar-onboarding-next"); }
        })) Stop();
    }

    private void OnHostClosed(object sender, WindowEventArgs args) => Stop();

    private void DetachTip()
    {
        var host = _host;
        if (host is not null) host.Closed -= OnHostClosed;
        _host = null;
        if (_tip is null) return;
        var tip = _tip;
        _tip = null;
        tip.ActionButtonClick -= OnNext;
        tip.CloseButtonClick -= OnSkip;
        tip.Closed -= OnClosed;
        var content = _tipContent;
        if (_tipContent is not null) _tipContent.Loaded -= OnContentLoaded;
        _tipContent = null;
        if (tip.IsOpen)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            int ticks = 0;
            bool completed = false;
            void Complete()
            {
                if (completed) return;
                completed = true;
                retry.Stop();
                tip.Closed -= Closed;
                if (host is not null) host.Closed -= HostClosed;
                try { tip.Target = null; tip.Content = null; }
                catch (Exception ex) { CrashReporter.Report(ex, "onboarding-close"); }
                completion.TrySetResult();
            }
            void Closed(TeachingTip sender, TeachingTipClosedEventArgs args) => Complete();
            void HostClosed(object sender, WindowEventArgs args) => Complete();
            tip.Closed += Closed;
            if (host is not null) host.Closed += HostClosed;
            _closing = completion.Task;
            // 原生控件会在展开动画期间反转关闭请求，动画结束后重试。
            retry.Tick += (_, _) =>
            {
                ticks++;
                if (tip.IsOpen) tip.IsOpen = false;
                else if (ticks >= 2 && content?.IsLoaded != true) Complete();
            };
            retry.Start();
            tip.IsOpen = false;
            return;
        }
        tip.IsOpen = false;
        tip.Target = null;
        tip.Content = null;
    }
}
