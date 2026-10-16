using System;
using System.Threading.Tasks;
using System.Threading;
using WinRT;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RimePPT.Windows;

/// <summary>Transparent composition with bounded UI-thread retries, never a blocking sleep.</summary>
public sealed class TransparentBackdrop : SystemBackdrop
{
    private static global::Windows.UI.Composition.Compositor? _compositor;
    private TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _version;
    private Window? _window;
    private FrameworkElement? _root;
    private global::Windows.UI.Composition.CompositionBrush? _brush;
    private bool _closed;

    internal void Attach(Window window)
    {
        _window = window;
        _root = window.Content as FrameworkElement;
        Services.WindowPlumbing.UseTransparentNativeBackground(window);
        window.SystemBackdrop = this;
        if (_root is not null)
        {
            _root.Loaded += OnRootLoaded;
            _root.ActualThemeChanged += OnThemeChanged;
        }
        window.Closed += (_, _) =>
        {
            _closed = true;
            if (_root is not null) { _root.Loaded -= OnRootLoaded; _root.ActualThemeChanged -= OnThemeChanged; }
            _root = null; _window = null; _ready.TrySetResult(false);
        };
    }

    private void OnRootLoaded(object sender, RoutedEventArgs args) => ReapplyWindowBackground();
    private void OnThemeChanged(FrameworkElement sender, object args) => ReapplyWindowBackground();
    private void ReapplyWindowBackground()
    {
        if (_closed || !IsReady || _brush is null || _window is null) return;
        try
        {
            // Apply through the Window target as well as the island target. This explicitly
            // removes the theme-coloured HWND background after loading or a theme change.
            _window.As<ICompositionSupportsSystemBackdrop>().SystemBackdrop = _brush;
        }
        catch (Exception ex)
        {
            IsReady = false;
            Core.CrashReporter.Report(ex, "transparent-window-background");
            Disconnected?.Invoke();
        }
    }

    internal static async Task WaitForFrameAsync(FrameworkElement root, CancellationToken cancellationToken = default)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int frames = 0;
        EventHandler<object> frame = (_, _) =>
        {
            if (root.IsLoaded && root.ActualWidth > 0 && ++frames >= 2) complete.TrySetResult();
        };
        CompositionTarget.Rendering += frame;
        try { await complete.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); }
        finally { CompositionTarget.Rendering -= frame; }
    }

    public bool IsReady { get; private set; }
    internal event Action? Disconnected;
    internal event Action? Connected;
    public Task<bool> Ready => _ready.Task;

    protected override async void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        int version = ++_version;
        IsReady = false;
        if (_ready.Task.IsCompleted) _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        // Keep compositor creation and target updates on the owning XAML thread.
        foreach (int delay in new[] { 0, 100, 200, 400, 800, 1000, 1000 })
        {
            if (delay > 0) await Task.Delay(delay);
            if (_closed || version != _version) return;
            try
            {
                _compositor ??= new global::Windows.UI.Composition.Compositor();
                _brush = _compositor.CreateColorBrush(default(global::Windows.UI.Color));
                connectedTarget.SystemBackdrop = _brush;
                IsReady = true;
                ReapplyWindowBackground();
                if (!IsReady) throw new InvalidOperationException("透明窗口背景未连接。");
                Core.PresentationDiagnostics.Record("透明背景", "已连接");
                _ready.TrySetResult(true);
                Connected?.Invoke();
                return;
            }
            catch (Exception ex)
            {
                failure = ex;
                Core.CrashReporter.Log($"transparent backdrop retry: 0x{ex.HResult:X8} {ex.Message}");
            }
        }
        Core.PresentationDiagnostics.Record("透明背景", "连接失败，保持隐藏");
        _ready.TrySetResult(false);
        if (failure is not null) Core.CrashReporter.Report(failure, "transparent-backdrop");
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop connectedTarget)
    {
        ++_version;
        IsReady = false;
        _ready.TrySetResult(false);
        // Avoid the SDK's historical base-disconnect access violation.
        connectedTarget.SystemBackdrop = null;
        _brush = null;
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Disconnected?.Invoke();
    }
}
