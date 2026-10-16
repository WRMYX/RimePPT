using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using RimePPT.Core;
using RimePPT.Windows;

namespace RimePPT;

public partial class App
{
    private CancellationTokenSource? _toolbarEntranceCancellation;

    private void CancelToolbarEntrances()
    {
        _toolbarEntranceCancellation?.Cancel();
        _toolbarEntranceCancellation?.Dispose();
        _toolbarEntranceCancellation = null;
    }

    private Task<bool> ShowToolbarsInOrderAsync(IEnumerable<ToolbarWindow> added, DisplayArea area)
    {
        var supplied = added.ToHashSet();
        // A settings rebuild can replace a sequence before its second group appears.
        // Carry still-hidden retained toolbars into the new sequence as well.
        var targets = ActiveToolbars.Where(t => !t.IsClosing && (supplied.Contains(t) || !t.AppWindow.IsVisible)).ToArray();
        CancelToolbarEntrances();
        var lifetime = _presenting is { IsPresenting: true } && _showCancellation is not null
            ? _showCancellation.Token : CancellationToken.None;
        var cancellation = _toolbarEntranceCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var token = cancellation.Token;
        bool Side(ToolbarWindow toolbar) => toolbar.Layout is ToolbarLayout.LeftRail or ToolbarLayout.RightRail;
        var groups = AppSettings.Instance.EntranceOrder switch
        {
            ToolbarEntranceOrder.Together => new[] { targets },
            ToolbarEntranceOrder.BottomFirst => new[] { targets.Where(t => !Side(t)).ToArray(), targets.Where(Side).ToArray() },
            _ => new[] { targets.Where(Side).ToArray(), targets.Where(t => !Side(t)).ToArray() }
        };
        return RunAsync();

        async Task<bool> RunAsync()
        {
            try
            {
                foreach (var group in groups)
                {
                    token.ThrowIfCancellationRequested();
                    var shown = new List<ToolbarWindow>();
                    foreach (var toolbar in group)
                    {
                        if (toolbar.IsClosing || !ActiveToolbars.Contains(toolbar)) continue;
                        toolbar.ShowOn(area);
                        toolbar.BeginEntrance();
                        shown.Add(toolbar);
                    }
                    await Task.WhenAll(shown.Select(t => t.WaitForEntranceAsync())).WaitAsync(token);
                }
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) { CrashReporter.Report(ex, "toolbar-entrance-sequence"); return false; }
            finally
            {
                if (ReferenceEquals(_toolbarEntranceCancellation, cancellation)) _toolbarEntranceCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async Task GuideAfterEntranceAsync(Task<bool> entrance, bool resumeGuide)
    {
        if (!await entrance || ActiveToolbars.Count == 0 || WhiteboardWindow.IsOpen) return;
        try { await _onboarding.StartAsync(ActiveToolbars.ToArray(), resumeGuide); }
        catch (Exception ex) { CrashReporter.Report(ex, "onboarding-after-entrance"); }
    }
}
