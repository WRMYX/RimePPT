using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RimePPT.Core;
using RimePPT.Core.Ink;
using RimePPT.Services;

namespace RimePPT.Windows;
public sealed class WhiteboardWindow : Window
{
    private static WhiteboardWindow? _instance;
    private Flyout? _pageFlyout;
    private readonly Grid _root = new();
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(12) };
    private readonly CanvasControl _canvas = new() { ClearColor = global::Windows.UI.Color.FromArgb(255, 16, 38, 30) };
    private readonly Dictionary<int, List<StrokeData>> _pages = new();
    private readonly InkHistory _history;
    private readonly InkContactManager _contacts = new();
    private readonly StrokeEraser _eraser = new();
    private readonly DispatcherTimer _shapeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<uint, long> _lastMotion = new();
    private readonly Dictionary<uint, Vector2> _holdPositions = new();
    private readonly Dictionary<uint, StrokeData> _shapePreviews = new();
    private readonly HashSet<uint> _shapeChecked = new();
    private readonly Dictionary<uint, InkDrawingOptions> _options = new();
    private readonly Dictionary<uint, Pointer> _pointers = new();
    private readonly TextBlock _pageText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4, 12, 4) };
    private StrokeData[]? _eraseBefore;
    private InkRenderer? _renderer;
    private int _page = 1, _pageCount = 1;
    private bool _erase, _closed, _closeAllowed, _prompting, _exporting, _dirty;
    public static bool IsOpen => _instance is not null;
    public static void Open()
    {
        _instance ??= new WhiteboardWindow(); _instance.Activate(); WindowPlumbing.RemoveWindowBorder(_instance); WindowPlumbing.RemoveResizableFrame(_instance);
        _instance.AppWindow.MoveAndResize(App.PresentationDisplayArea.OuterBounds); WindowPlumbing.RaiseToTopmost(_instance);
        App.SetWhiteboardMode(true);
    }
    private InkViewport Viewport => new((float)_canvas.ActualWidth, (float)_canvas.ActualHeight);
    internal static async Task HandleCommandAsync(ToolbarCommand command, ToolbarWindow toolbar)
    {
        var board = _instance; if (board is null || board._exporting) return;
        board.FinishAll();
        switch (command)
        {
            case ToolbarCommand.Annotate: board._erase = false; break;
            case ToolbarCommand.Eraser: board._erase = true; break;
            case ToolbarCommand.Prev: if (board._page > 1) board.SetPage(board._page - 1); break;
            case ToolbarCommand.Next: if (board._page == board._pageCount) board._pageCount++; board.SetPage(board._page + 1); break;
            case ToolbarCommand.Undo: if (board._history.Undo(board._page)) board.Changed(); break;
            case ToolbarCommand.Redo: if (board._history.Redo(board._page)) board.Changed(); break;
            case ToolbarCommand.Pages:
                if (toolbar.GetCommandTarget(command) is { } pageTarget) board.ShowPageNavigator(pageTarget);
                break;
            case ToolbarCommand.NewBoardPage: board.SetPage(++board._pageCount); break;
            case ToolbarCommand.Export: await board.ExportAsync(); break;
            case ToolbarCommand.ExitShow: board.Close(); break;
            case ToolbarCommand.Tools:
                var menu = new MenuFlyout();
                void Add(string title, Action action) { var item = new MenuFlyoutItem { Text = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
                Add("导出 PNG / PDF", async () => await board.ExportAsync());
                Add("返回 PPT", board.Close);
                if (toolbar.GetCommandTarget(ToolbarCommand.Tools) is { } target) menu.ShowAt(target);
                break;
        }
        if (!board._closed) board._canvas.Invalidate();
        RefreshToolbarState();
        App.RaiseBoardToolbars();
    }
    private void ShowPageNavigator(FrameworkElement anchor)
    {
        _pageFlyout?.Hide();
        var cards = new StackPanel { Spacing = 8 };
        var list = new ScrollViewer { Width = 256, MaxHeight = 360, Content = cards,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        var previews = new List<CanvasControl>();
        var viewport = Viewport;
        float width = 224;
        float height = viewport.IsValid ? width * viewport.HeightDip / viewport.WidthDip : 126;

        var flyout = new Flyout { Content = list, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top,
            ShouldConstrainToRootBounds = false, SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop() };
        list.RequestedTheme = App.ToolbarTheme;
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12)));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        flyout.FlyoutPresenterStyle = presenterStyle;
        for (int i = 1; i <= _pageCount; i++)
        {
            int page = i;
            var strokes = _pages.TryGetValue(page, out var source) ? source.ToArray() : Array.Empty<StrokeData>();
            var thumbnailStrokes = strokes.Select(x => new StrokeData { Id = x.Id, SlideIndex = x.SlideIndex,
                Argb = x.Argb, Dots = x.Dots, LineStyle = x.LineStyle,
                ThicknessDips = viewport.IsValid ? x.ThicknessDips * width / viewport.WidthDip : x.ThicknessDips }).ToArray();
            var canvas = new CanvasControl { Width = width, Height = height, ClearColor = _canvas.ClearColor,
                IsHitTestVisible = false };
            canvas.Draw += (sender, args) =>
            {
                try
                {
                    using var renderer = new InkRenderer(sender.Device, () => thumbnailStrokes, () => Array.Empty<StrokeData>());
                    renderer.Draw(args.DrawingSession, new(width, height));
                }
                catch (Exception ex) { CrashReporter.Report(ex, "board-page-preview"); }
            };
            previews.Add(canvas);
            var content = new StackPanel { Spacing = 8 };
            var previewFrame = new Border { Child = canvas, CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] };
            content.Children.Add(previewFrame);
            var caption = new Grid();
            caption.Children.Add(new TextBlock { Text = $"第 {page} 页", FontSize = 12 });
            if (page == _page) caption.Children.Add(new TextBlock { Text = "当前页", FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
            content.Children.Add(caption);
            var button = new Button { Content = content, Padding = new Thickness(8), CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(page == _page ? 2 : 1), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            if (page == _page) button.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            button.Click += (_, _) => { SetPage(page); RefreshToolbarState(); flyout.Hide(); App.RaiseBoardToolbars(); };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"画板第 {page} 页");
            cards.Children.Add(button);
        }
        flyout.Closed += (_, _) =>
        {
            foreach (var canvas in previews) canvas.RemoveFromVisualTree();
            cards.Children.Clear();
            if (ReferenceEquals(_pageFlyout, flyout)) _pageFlyout = null;
        };
        _pageFlyout = flyout; flyout.ShowAt(anchor);
    }
    internal static void ClearCurrentPage()
    {
        if (_instance is not { } board || board._exporting) return;
        board.FinishAll();
        if (board._history.Clear(board._page)) board.Changed();
        board._canvas.Invalidate(); RefreshToolbarState();
    }
    internal static void RefreshToolbarState()
    {
        if (_instance is not { } board) return;
        foreach (var item in App.ActiveToolbars)
        {
            item.SetCommandChecked(ToolbarCommand.Annotate, !board._erase);
            item.SetCommandChecked(ToolbarCommand.Eraser, board._erase);
            item.UpdatePresentation(board._page, board._pageCount, board._history.CanUndo(board._page), board._history.CanRedo(board._page));
        }
    }
    public WhiteboardWindow()
    {
        Title = "RimePPT 独立画板"; _shapeTimer.Tick += OnShapeFrame; _history = new(_pages);
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false;
            presenter.IsMinimizable = false; presenter.IsMaximizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        var bounds = App.PresentationDisplayArea.OuterBounds;
        AppWindow.MoveAndResize(bounds);
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = _toolbar;
        _canvas.ManipulationMode = ManipulationModes.None;
        Button Add(string text, Action action)
        {
            var button = new Button { Content = text, MinHeight = 40 };
            button.Click += (_, _) => { if (_exporting) return; FinishAll(); action(); _canvas.Invalidate(); };
            toolbar.Children.Add(button); return button;
        }
        Add("笔", () => _erase = false);
        var pen = Add("笔与形状", () => _erase = false);
        pen.Click += (_, _) => PenPickerWindow.ForWhiteboard(false).ShowAt(pen);
        Add("橡皮", () => _erase = true);
        var eraser = Add("橡皮大小", () => _erase = true);
        eraser.Click += (_, _) => PenPickerWindow.ForWhiteboard(true).ShowAt(eraser);
        Add("撤销", () => { if (_history.Undo(_page)) Changed(); });
        Add("重做", () => { if (_history.Redo(_page)) Changed(); });
        Add("清屏", () => { if (_history.Clear(_page)) Changed(); });
        Add("上一页", () => { if (_page > 1) SetPage(_page - 1); });
        toolbar.Children.Add(_pageText);
        Add("下一页", () => { if (_page < _pageCount) SetPage(_page + 1); });
        Add("新增页", () => SetPage(++_pageCount));
        var export = Add("导出 PNG / PDF", () => { });
        export.Click += async (_, _) => await ExportAsync();
        var bar = new ScrollViewer { Content = toolbar, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        bar.Visibility = Visibility.Collapsed;
        _root.RowDefinitions.Clear();
        _root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(_canvas.ClearColor);
        Grid.SetRow(_canvas, 0); _root.Children.Add(_canvas);
        _message.VerticalAlignment = VerticalAlignment.Bottom;
        _message.Margin = new Thickness(16, 0, 16, 100);
        _message.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        _message.IsHitTestVisible = false;
        _root.Children.Add(_message); Content = _root;
        _canvas.CreateResources += (sender, _) => { _renderer?.Dispose(); _renderer = new InkRenderer(sender.Device, CurrentStrokes, Previews); };
        _canvas.Draw += (sender, args) => { if (!_closed) {
            _renderer ??= new InkRenderer(sender.Device, CurrentStrokes, Previews);
            _renderer.EraserPositions = _contacts.Contacts.Select(x => x.Value).Where(x => x.Tool.Tool == InkTool.Eraser && x.LastPosition.HasValue).Select(x => x.LastPosition!.Value).ToArray();
            _renderer.EraserSize = new((float)AppSettings.Instance.EraserWidthDip, (float)AppSettings.Instance.EraserHeightDip);
            _renderer.Draw(args.DrawingSession, Viewport); } };
        _canvas.PointerPressed += Press; _canvas.PointerMoved += Move;
        _canvas.PointerReleased += (_, e) => { Move(_canvas, e); Finish(e.Pointer.PointerId, true); };
        _canvas.PointerCanceled += (_, e) => Finish(e.Pointer.PointerId, false);
        _canvas.PointerCaptureLost += (_, e) => Finish(e.Pointer.PointerId, false);
        _canvas.SizeChanged += (_, _) => { FinishAll(); _renderer?.InvalidateSlide(); };
        AppWindow.Closing += (_, args) =>
        {
            if (_exporting) { args.Cancel = true; return; }
            FinishAll();
            if (_closeAllowed || !_dirty) return;
            args.Cancel = true;
            if (!_prompting) DispatcherQueue.TryEnqueue(async () => await ConfirmCloseAsync());
        };
        Closed += (_, _) => { _pageFlyout?.Hide(); _shapeTimer.Stop(); _shapeTimer.Tick -= OnShapeFrame; _closed = true; _renderer?.Dispose(); _canvas.RemoveFromVisualTree(); if (ReferenceEquals(_instance, this)) _instance = null; App.SetWhiteboardMode(false); };
        SetPage(1);
    }
    private IReadOnlyList<StrokeData> CurrentStrokes() => _pages.TryGetValue(_page, out var strokes) ? strokes : Array.Empty<StrokeData>();
    private IReadOnlyList<StrokeData> Previews() => _contacts.Contacts.Where(x => x.Value.Preview is not null)
        .Select(x => InkDrawing.Apply(_shapePreviews.TryGetValue(x.Key, out var shape) ? shape : x.Value.Preview!, x.Value.Viewport, _options[x.Key])).ToArray();
    private InkSample Sample(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas);
        return new(new Vector2((float)p.Position.X, (float)p.Position.Y), p.Timestamp,
            e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch ? InkDevice.Touch : InkDevice.Mouse);
    }
    private void Press(object sender, PointerRoutedEventArgs e)
    {
        if (_closed || _exporting || !Viewport.IsValid || !e.GetCurrentPoint(_canvas).IsInContact) return;
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        if (!_canvas.CapturePointer(e.Pointer)) return;
        var settings = AppSettings.Instance;
        if (!_contacts.Begin(e.Pointer.PointerId, Sample(e), new(_erase ? InkTool.Eraser : InkTool.Pen, _page, settings.GetPenArgb(), (float)settings.PenThickness), Viewport)) { _canvas.ReleasePointerCapture(e.Pointer); return; }
        _options[e.Pointer.PointerId] = new(settings.PenLineStyle, settings.PenShape); _pointers[e.Pointer.PointerId] = e.Pointer;
        _lastMotion[e.Pointer.PointerId] = Stopwatch.GetTimestamp();
        _holdPositions[e.Pointer.PointerId] = Sample(e).Position;
        if (!_erase && settings.SmartShapesEnabled && settings.PenShape == InkShape.Freehand && settings.PenLineStyle == InkLineStyle.Solid) _shapeTimer.Start();
        App.RaiseBoardToolbars();
        if (_erase) { _eraseBefore ??= _history.Snapshot(_page); Erase(Sample(e).Position, Sample(e).Position); }
        e.Handled = true; _canvas.Invalidate();
    }
    private void Move(object sender, PointerRoutedEventArgs e)
    {
        var input = _contacts.Find(e.Pointer.PointerId); if (input is null || _closed) return;
        var sample = Sample(e);
        if (_holdPositions.TryGetValue(e.Pointer.PointerId, out var hold) && Vector2.Distance(sample.Position, hold) > SmartShapeRecognizer.HoldTolerance(sample.Device))
        {
            _lastMotion[e.Pointer.PointerId] = Stopwatch.GetTimestamp(); _holdPositions[e.Pointer.PointerId] = sample.Position;
            _shapePreviews.Remove(e.Pointer.PointerId); _shapeChecked.Remove(e.Pointer.PointerId);
        }
        if (input.Tool.Tool == InkTool.Eraser && input.LastPosition is { } before) Erase(before, sample.Position);
        input.Move(e.Pointer.PointerId, new[] { sample }); e.Handled = true; _canvas.Invalidate();
    }
    private void OnShapeFrame(object? sender, object args)
    {
        if (!AppSettings.Instance.SmartShapesEnabled) { _shapePreviews.Clear(); _shapeTimer.Stop(); _canvas.Invalidate(); return; }
        foreach (var contact in _contacts.Contacts)
            if (contact.Value.Tool.Tool == InkTool.Pen && _options.TryGetValue(contact.Key, out var options)
                && options == new InkDrawingOptions(InkLineStyle.Solid, InkShape.Freehand)
                && !_shapeChecked.Contains(contact.Key) && contact.Value.Preview is { } preview
                && _lastMotion.TryGetValue(contact.Key, out var moved) && Stopwatch.GetElapsedTime(moved).TotalMilliseconds >= 600)
            {
                _shapeChecked.Add(contact.Key);
                if (SmartShapeRecognizer.Recognize(preview, contact.Value.Viewport) is { } shape)
                { _shapePreviews[contact.Key] = shape; _canvas.Invalidate(); }
            }
    }
    private void Erase(Vector2 from, Vector2 to)
    {
        if (!_pages.TryGetValue(_page, out var strokes)) return;
        var settings = AppSettings.Instance;
        if (_eraser.EraseSweep(strokes, new(from,to,(float)settings.EraserWidthDip,(float)settings.EraserHeightDip), Viewport).Changed) Changed();
    }
    private void Finish(uint id, bool commit)
    {
        var input = _contacts.Find(id); if (input is null) return;
        var before = _history.Snapshot(input.Tool.SlideIndex); var viewport = input.Viewport; var options = _options[id];
        var stroke = _contacts.End(id, null);
        if (commit && stroke is not null)
        {
            if (AppSettings.Instance.SmartShapesEnabled && _shapePreviews.TryGetValue(id, out var shape)) stroke = shape;
            stroke = InkDrawing.Apply(stroke, viewport, options);
            if (!_pages.TryGetValue(stroke.SlideIndex, out var list)) _pages[stroke.SlideIndex] = list = new();
            list.Add(stroke); _history.Record(stroke.SlideIndex,before); Changed();
        }
        if (_contacts.Count == 0 && _eraseBefore is not null) { _history.Record(_page, _eraseBefore); _eraseBefore = null; }
        _options.Remove(id); _lastMotion.Remove(id); _holdPositions.Remove(id); _shapePreviews.Remove(id); _shapeChecked.Remove(id);
        if (_contacts.Count == 0) _shapeTimer.Stop();
        if (_pointers.Remove(id,out var pointer)) _canvas.ReleasePointerCapture(pointer);
        if (!_closed) _canvas.Invalidate();
    }
    private void FinishAll() { foreach (uint id in _contacts.Ids()) Finish(id,true); }
    private void Changed()
    {
        _dirty = true; _renderer?.InvalidateSlide(); _eraser.Reset();
        foreach (var toolbar in App.ActiveToolbars) toolbar.UpdatePresentation(_page, _pageCount, _history.CanUndo(_page), _history.CanRedo(_page));
    }
    private void SetPage(int page)
    {
        FinishAll(); _page = page; _pageText.Text = $"{_page} / {_pageCount}";
        _renderer?.InvalidateSlide(); _eraser.Reset(); _canvas.Invalidate();
        foreach (var toolbar in App.ActiveToolbars) toolbar.UpdatePresentation(_page, _pageCount, _history.CanUndo(_page), _history.CanRedo(_page));
    }
    private async Task<bool> ExportAsync()
    {
        if (_exporting || _closed) return false;
        FinishAll(); _exporting = true; SetToolbarEnabled(false);
        try
        {
            var picker = new global::Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync(); if (folder is null || _closed) return false;
            var snapshot = _pages.ToDictionary(x => x.Key,x => x.Value.ToList());
            _canvas.IsHitTestVisible = false;
            string exported = await BoardExportService.ExportAsync(new DebugPresentationController(), snapshot, Viewport, folder.Path, _canvas.ClearColor);
            _dirty = false; _message.Text = "已导出：" + exported; return true;
        }
        catch (Exception ex) { CrashReporter.Report(ex,"whiteboard-export"); if (!_closed) _message.Text = "导出未完成：" + ex.Message; return false; }
        finally { _exporting = false; if (!_closed) { _canvas.IsHitTestVisible = true; SetToolbarEnabled(true); } }
    }
    private void SetToolbarEnabled(bool enabled)
    {
        foreach (var control in _toolbar.Children.OfType<Control>()) control.IsEnabled = enabled;
    }
    private async Task ConfirmCloseAsync()
    {
        if (_prompting || _closed) return; _prompting = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = _root.XamlRoot, Title = "保存独立画板？", Content = "画板有尚未导出的内容。关闭后，本次画板内容将被清除。",
                PrimaryButtonText = "导出后关闭", SecondaryButtonText = "不保存", CloseButtonText = "继续书写", DefaultButton = ContentDialogButton.Close };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary || (result == ContentDialogResult.Primary && await ExportAsync())) { _closeAllowed = true; Close(); }
        }
        catch (Exception ex) { CrashReporter.Report(ex,"whiteboard-close"); }
        finally { _prompting = false; }
    }
}
