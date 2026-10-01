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
    private readonly Grid _root = new();
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
        SystemBackdrop = new TransparentBackdrop();
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
        Closed += (_, _) => { _closed = true; if (_shown) _dialog.Hide(); _ready.TrySetResult(); };
    }
    public async Task<PromptResult> ShowCenteredAsync(DisplayArea area)
    {
        await DialogGate.WaitAsync();
        try
        {
            if (_closed) return PromptResult.Secondary;
            AppWindow.MoveAndResize(area.OuterBounds);
            Activate(); await _ready.Task;
            if (_closed) return PromptResult.Secondary;
            _dialog.XamlRoot = _root.XamlRoot;
            _shown = true;
            var result = await _dialog.ShowAsync();
            _shown = false;
            var choice = result switch { ContentDialogResult.Primary => PromptResult.Primary,
                ContentDialogResult.Secondary when _hasDelete => PromptResult.Delete, _ => PromptResult.Secondary };
            ChoiceMade?.Invoke(this, choice);
            return choice;
        }
        finally { _shown = false; if (!_closed) Close(); DialogGate.Release(); }
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
