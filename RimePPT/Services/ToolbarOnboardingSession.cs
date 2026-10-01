using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RimePPT.Core;
using RimePPT.Windows;

namespace RimePPT.Services;

/// <summary>一次只打开一个原生 TeachingTip，目标始终来自实际工具栏按钮。</summary>
internal sealed class ToolbarOnboardingSession
{
    private sealed record Step(ToolbarWindow Window, FrameworkElement Target, string Title, string Description, ToolbarCommand Command);
    private readonly List<Step> _steps = new();
    private readonly List<ToolbarWindow> _windows = new();
    private readonly List<OnboardingOverlayWindow> _overlays = new();
    private string? _subject;
    internal bool TryPreviewCommand(ToolbarCommand command)
    {
        if (!IsRunning) return false;
        foreach (var window in _windows) window.PreviewGuideCommand(command);
        return true;
    }
    private TeachingTip? _tip;
    private ToolbarWindow? _host;
    private int _version;
    private int _index;
    private bool _advance;
    private bool _waiting;
    private bool _marked;
    private TextBlock? _tipContent;
    internal bool IsRunning => _waiting || _tip is not null || _windows.Count > 0;

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
        _subject = ClassWidgetsCourseReader.ReadCurrentSubject();
        if (!force && !OnboardingState.ShouldShow(_subject)) return;
        int version = _version;
        _waiting = true;
        try
        {
            await Task.WhenAll(windows.Select(ToolbarEntranceAnimator.WaitForEntranceAsync));
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
                    _steps.Add(new Step(window, target, lesson.Title, lesson.Description, lesson.Command));
                    break; // 多个窗口重复的命令只讲解一次。
                }
            }
            if (_steps.Count > 0)
            {
                _windows.AddRange(windows);
                var areas = windows.Select(w => Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(w.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest));
                foreach (var area in areas.DistinctBy(a => a.DisplayId.Value)) _overlays.Add(new OnboardingOverlayWindow(area));
                foreach (var window in windows) { window.SetGuidePreview(true); WindowPlumbing.RaiseToTopmost(window); }
                ShowStep(0);
            }
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
        foreach (var window in _windows) window.SetGuidePreview(false);
        _windows.Clear();
        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear();
        _subject = null;
        _steps.Clear();
        _marked = false;
    }

    private void ShowStep(int index)
    {
        _index = index;
        var step = _steps[index];
        foreach (var window in _windows) window.PreviewGuideCommand(step.Command);
        _host = step.Window;
        _tip = step.Window.CreateGuideTip();
        _tip.Target = step.Target;
        _tip.HeroContent = OnboardingIllustrations.Create(step.Command);
        _tip.Title = $"{step.Title} · {index + 1} / {_steps.Count}";
        _tipContent = new TextBlock { Text = step.Description, Style = (Style)Application.Current.Resources["BodyTextBlockStyle"], TextWrapping = TextWrapping.Wrap, MaxWidth = 280, Margin = new Thickness(0, 8, 0, 0) };
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
        OnboardingState.TryMarkShown(_subject);
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
        CloseNativePopup(tip);
    }

    private static void CloseNativePopup(TeachingTip tip)
    {
        var content = tip.Content as DependencyObject;
        var root = tip.XamlRoot;
        tip.IsOpen = false;
        if (content is null || root is null) return;
        // IsOpen 的动画保护可能撤销关闭请求。只关闭包含本提示正文的原生 popup，
        // 不影响画笔面板、工具菜单或其他窗口的弹出层。
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            DependencyObject? ancestor = content;
            while (ancestor is not null && !ReferenceEquals(ancestor, popup.Child))
                ancestor = VisualTreeHelper.GetParent(ancestor);
            if (ancestor is null) continue;
            popup.IsOpen = false;
            break;
        }
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
        if (_tipContent is not null) _tipContent.Loaded -= OnContentLoaded;
        _tipContent = null;
        try
        {
            CloseNativePopup(tip);
            host?.RemoveGuideTip(tip);
            tip.Target = null;
            tip.Content = null;
            tip.HeroContent = null;
        }
        catch (Exception ex) { CrashReporter.Report(ex, "onboarding-close"); }
    }
}
