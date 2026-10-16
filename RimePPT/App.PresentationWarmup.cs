using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using RimePPT.Core;
using RimePPT.Windows;

namespace RimePPT;

public partial class App
{
    private readonly Dictionary<ToolbarLayout, ToolbarWindow> _preparedToolbars = new();
    private AnnotationWindow? _preparedAnnotation;
    private string? _preparedSignature;
    private int _preparationVersion;

    private ToolbarWindow CreatePresentationToolbar(ToolbarLayout layout)
    {
        var toolbar = new ToolbarWindow(layout, AppSettings.Instance.GetToolbarCommands(layout));
        toolbar.ToolbarClicked += OnToolbarClicked;
        toolbar.ToolSettingsRequested += OpenToolSettings;
        toolbar.ApplySettings();
        return toolbar;
    }

    private void PreparePresentationToolbars()
    {
        if (_dispatcher is null || ActiveToolbars.Count > 0 || WhiteboardWindow.IsOpen) return;
        string signature = ToolbarSettingsSignature();
        if (_preparedSignature == signature && _preparedToolbars.Count > 0) return;
        DiscardPreparedToolbars();
        _preparedSignature = signature;
        int version = _preparationVersion;
        var layouts = new Queue<ToolbarLayout>(AppSettings.Instance.GetEnabledToolbarLayouts()
            .Where(layout => AppSettings.Instance.GetToolbarCommands(layout).Count > 0 ||
                AppSettings.Instance.QuickLaunchEntries.Any(entry => entry.PinnedLayout == layout)));
        async void PrepareNext()
        {
            if (version != _preparationVersion || ActiveToolbars.Count > 0 || WhiteboardWindow.IsOpen) return;
            if (ToolbarSettingsSignature() != signature) { PreparePresentationToolbars(); return; }
            if (!layouts.TryDequeue(out var layout))
            {
                try
                {
                    _preparedAnnotation = new AnnotationWindow(SessionInk);
                    CrashReporter.Log("toolbar warmup: ready, count=" + _preparedToolbars.Count + ", annotation prepared");
                    var arrows = new Queue<ToolbarWindow>(_preparedToolbars.Values);
                    async void WarmNextArrow()
                    {
                        if (version != _preparationVersion || ActiveToolbars.Count > 0 || !arrows.TryDequeue(out var toolbar)) return;
                        try { await toolbar.PrewarmToolArrowAsync(); }
                        catch (Exception ex) { CrashReporter.Report(ex, "tool-arrow-warmup"); }
                        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, WarmNextArrow);
                    }
                    _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, WarmNextArrow);
                }
                catch (Exception ex) { CrashReporter.Report(ex, "annotation-warmup"); }
                return;
            }
            try
            {
                var toolbar = CreatePresentationToolbar(layout);
                _preparedToolbars[layout] = toolbar;
                try { await toolbar.PrewarmAsync(); }
                catch (Exception ex) { CrashReporter.Report(ex, "toolbar-native-warmup"); }
                if (version != _preparationVersion || ActiveToolbars.Count > 0) return;
                CrashReporter.Log($"toolbar warmup: composed layout={layout}");
                _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, PrepareNext);
            }
            catch (Exception ex) { DiscardPreparedToolbars(); CrashReporter.Report(ex, "toolbar-warmup"); }
        }
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, PrepareNext);
    }

    private ToolbarWindow TakePreparedToolbar(ToolbarLayout layout)
    {
        if (_preparedSignature != ToolbarSettingsSignature()) DiscardPreparedToolbars();
        if (_preparedToolbars.Remove(layout, out var toolbar))
        {
            if (!toolbar.IsWarming) return toolbar;
            toolbar.Dismiss();
        }
        return CreatePresentationToolbar(layout);
    }

    private void DiscardPreparedToolbars()
    {
        _preparationVersion++;
        foreach (var toolbar in _preparedToolbars.Values) toolbar.Dismiss();
        _preparedToolbars.Clear();
        _preparedAnnotation?.Close();
        _preparedAnnotation = null;
        _preparedSignature = null;
    }
}
