using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RimePPT.Services;

namespace RimePPT.Windows;

public enum PromptResult { Primary, Secondary, Delete }

/// <summary>透明置顶宿主承载原生 ContentDialog，保存/加载选择沿用原有结果。</summary>
public sealed class PromptWindow : Window
{
    private static readonly SemaphoreSlim DialogGate = new(1, 1);
    private readonly TransparentBackdrop _backdrop = new();
    private readonly Grid _root = new() { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly ContentDialog _dialog;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _hasDelete;
    private bool _closed, _shown;
    public event EventHandler<PromptResult>? ChoiceMade;
    public PromptWindow(string title, string message, string primaryText, string secondaryText, string? deleteText = null)
    {
        Title = "RimePPT 墨迹询问";
        Content = _root;
        _root.RequestedTheme = App.ToolbarTheme;
        _root.Loaded += (_, _) => _ready.TrySetResult();
        _backdrop.Attach(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false); presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false; presenter.IsMinimizable = false; presenter.IsMaximizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        WindowPlumbing.EnableTransparency(this);
        WindowPlumbing.RemoveWindowBorder(this);
        WindowPlumbing.RemoveResizableFrame(this);
        _hasDelete = !string.IsNullOrEmpty(deleteText);
        _dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            SecondaryButtonText = deleteText ?? "",
            CloseButtonText = secondaryText,
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = App.ToolbarTheme,
            CornerRadius = (CornerRadius)Application.Current.Resources["OverlayCornerRadius"],
        };
        _root.Resources["ContentDialogSmokeFill"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(72, 0, 0, 0));
        _dialog.Resources["ContentDialogSmokeFill"] = _root.Resources["ContentDialogSmokeFill"];
        _backdrop.Disconnected += () =>
        {
            if (_closed) return;
            WindowPlumbing.SetOpacity(this, 0);
            // Release the modal input gate if an already-visible host loses composition.
            if (_shown) _dialog.Hide();
        };
        Closed += (_, _) => { _closed = true; if (_shown) _dialog.Hide(); _ready.TrySetResult(); };
    }
    public async Task<PromptResult> ShowCenteredAsync(DisplayArea area)
    {
        await DialogGate.WaitAsync();
        try
        {
            if (_closed) return PromptResult.Secondary;
            AppWindow.MoveAndResize(area.OuterBounds);
            WindowPlumbing.SetOpacity(this, 0);
            Activate(); await _ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (_closed) return PromptResult.Secondary;
            bool transparent = false;
            try { transparent = await _backdrop.Ready.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            if (_closed) return PromptResult.Secondary;
            if (!transparent || !_backdrop.IsReady) UseCompactFallback(area);
            _dialog.XamlRoot = _root.XamlRoot;
            _shown = true;
            var operation = _dialog.ShowAsync();
            try { await TransparentBackdrop.WaitForFrameAsync(_root); }
            catch (TimeoutException) { if (!_closed) UseCompactFallback(area); }
            if (_closed) return PromptResult.Secondary;
            WindowPlumbing.SetOpacity(this, 255);
            var result = await operation;
            _shown = false;
            var choice = result switch { ContentDialogResult.Primary => PromptResult.Primary,
                ContentDialogResult.Secondary when _hasDelete => PromptResult.Delete, _ => PromptResult.Secondary };
            ChoiceMade?.Invoke(this, choice);
            return choice;
        }
        finally { _shown = false; if (!_closed) Close(); DialogGate.Release(); }
    }
    private void UseCompactFallback(DisplayArea area)
    {
        // A failed composition must never leave an opaque fullscreen host over PowerPoint.
        var bounds = area.WorkArea;
        int width = Math.Min(640, bounds.Width), height = Math.Min(420, bounds.Height);
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(
            bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height));
        _root.Resources["ContentDialogSmokeFill"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _dialog.Resources["ContentDialogSmokeFill"] = _root.Resources["ContentDialogSmokeFill"];
        RimePPT.Core.CrashReporter.Log("prompt: transparency unavailable; using compact host");
    }
    public new void Close()
    {
        if (_closed) return;
        // 等原生对话框关闭动画完成后再销毁宿主，避免根视图提前释放。
        if (_shown) { _dialog.Hide(); return; }
        _closed = true;
        _ready.TrySetResult(); base.Close();
    }
}
