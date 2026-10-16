using System;
using System.Threading.Tasks;
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
            if (version != _version) return;
            try
            {
                _compositor ??= new global::Windows.UI.Composition.Compositor();
                connectedTarget.SystemBackdrop = _compositor.CreateColorBrush(default(global::Windows.UI.Color));
                IsReady = true;
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
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Disconnected?.Invoke();
    }
}
