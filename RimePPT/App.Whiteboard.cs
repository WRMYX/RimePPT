using System;
using System.Linq;
using Microsoft.UI.Windowing;
using RimePPT.Core;
using RimePPT.Services;
using RimePPT.Windows;
namespace RimePPT;
public partial class App
{
    public static DisplayArea PresentationDisplayArea => _instance?._showArea ?? DisplayArea.Primary;
    internal static async void SetWhiteboardMode(bool enabled)
    {
        var app = _instance; if (app is null) return;
        try
        {
            app.ClosePenChevron(); app.CloseToolsMenu(); app._onboarding.Stop();
            if (enabled)
            {
                app._navigator?.Close(); app._navigator = null;
                app._spotlight?.Close(); app._spotlight = null; app.CloseBlackout(); app.CloseTimer();
                app._annotation?.FinishInput();
                if (app._backend is not null) await app._backend.SetToolAsync(null);
                app._annotation?.AppWindow.Hide();
                if (ActiveToolbars.Count == 0)
                {
                    var entering = new System.Collections.Generic.List<ToolbarWindow>();
                    var layouts = AppSettings.Instance.GetEnabledToolbarLayouts();
                    if (layouts.Length == 0) layouts = new[] { ToolbarLayout.BottomCenter };
                    foreach (var layout in layouts)
                    {
                        var toolbar = new ToolbarWindow(layout, AppSettings.Instance.GetToolbarCommands(layout));
                        toolbar.ToolbarClicked += app.OnToolbarClicked; toolbar.ToolSettingsRequested += app.OpenToolSettings;
                        toolbar.SetWhiteboardContent(true); toolbar.ApplySettings();
                        ActiveToolbars.Add(toolbar); entering.Add(toolbar);
                    }
                    _ = app.ShowToolbarsInOrderAsync(entering, PresentationDisplayArea);
                }
                foreach (var toolbar in ActiveToolbars) toolbar.SetWhiteboardContent(true);
                WhiteboardWindow.RefreshToolbarState();
            }
            else if (app._presenting is null) CloseAllToolbars();
            else
            {
                foreach (var toolbar in ActiveToolbars) toolbar.SetWhiteboardContent(false);
                app.RebuildToolbars();
                // 即使进入画板前没有选笔，也先恢复透明批注层；之后点击笔才有可用的输入窗口。
                if (app._annotation is { } annotation)
                {
                    bool ready = await annotation.ShowOnAsync(PresentationDisplayArea,
                        app._showCancellation?.Token ?? System.Threading.CancellationToken.None);
                    if (WhiteboardWindow.IsOpen || app._annotation != annotation) return;
                    if (!ready)
                    {
                        app._annotating = app._erasing = false;
                        app.SyncAnnotationChecks();
                        return;
                    }
                }
                if (app._backend is not null)
                {
                    await app._backend.SetToolAsync(app._annotating
                        ? (app._erasing ? AnnotationTool.Eraser : AnnotationTool.Pen) : null);
                }
                app.SyncAnnotationChecks();
            }
            app.RaiseCompanions();
        }
        catch (Exception ex) { CrashReporter.Report(ex, "whiteboard-mode"); }
    }
    internal static void RaiseBoardToolbars() => _instance?.RaiseCompanions();
}
