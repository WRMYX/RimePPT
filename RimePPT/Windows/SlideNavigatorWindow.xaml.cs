using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using RimePPT.Core;

namespace RimePPT.Windows;

public sealed partial class SlideNavigatorWindow : UserControl
{
    public sealed class PageItem : INotifyPropertyChanged
    {
        public int Index { get; init; }
        private string _label = "";
        public string Label { get => _label; set { if (_label == value) return; _label = value; PropertyChanged?.Invoke(this, new(nameof(Label))); } }
        private BitmapImage? _image; public BitmapImage? Image { get => _image; set { _image = value; PropertyChanged?.Invoke(this, new(nameof(Image))); } }
        private string _error = ""; public string Error { get => _error; set { _error = value; PropertyChanged?.Invoke(this, new(nameof(Error))); } }
        public bool Loading { get; set; }
        public int Attempts { get; set; }
        public long LastAttempt { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly Flyout _flyout = new() { ShouldConstrainToRootBounds = false, AreOpenCloseAnimationsEnabled = true,
        Placement = FlyoutPlacementMode.Top, SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop() };
    private bool _disposed;
    public bool IsShowing { get; private set; }
    public FrameworkElement? Anchor { get; private set; }
    public event EventHandler? Closed;
    private readonly IPresentationController _controller;
    private readonly SlideThumbnailCache _cache;
    private readonly CancellationTokenSource _cancel = new();
    private readonly ObservableCollection<PageItem> _items = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly long _openedAt = Stopwatch.GetTimestamp();
    private string? _actionError;
    public SlideNavigatorWindow(IPresentationController controller)
    {
        InitializeComponent(); _controller = controller; _cache = new(controller);
        RequestedTheme = App.ToolbarTheme;
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, Application.Current.Resources["OverlayCornerRadius"]));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8)));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, double.PositiveInfinity));
        style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, double.PositiveInfinity));
        _flyout.FlyoutPresenterStyle = style;
        _flyout.Content = this;
        Pages.ItemsSource = _items;
        RefreshPresentation();
        _controller.SlideChanged += OnPresentationChanged;
        _controller.ShowEnded += OnPresentationEnded;
        _refreshTimer.Tick += OnRefreshTick;
        _flyout.Opening += (_, _) => { IsShowing = true; _refreshTimer.Start(); RefreshPresentation(); };
        _flyout.Closed += (_, _) => { IsShowing = false; DisposeContent(); Closed?.Invoke(this, EventArgs.Empty); };
    }
    public void ShowAt(FrameworkElement anchor, Microsoft.UI.Windowing.DisplayArea? area, int toolbarWidthPixels)
    {
        Anchor = anchor;
        double scale = anchor.XamlRoot.RasterizationScale;
        PreviewRoot.Width = Math.Max(64, toolbarWidthPixels / scale - 16);
        PreviewRoot.Height = Math.Min(340, Math.Max(160, (area?.OuterBounds.Height ?? 800) / scale - 160));
        _flyout.ShowAt(anchor);
    }
    public void Close() { _flyout.Hide(); DisposeContent(); }
    private void OnRefreshTick(object? sender, object args) => RefreshPresentation();
    private void OnPresentationChanged(object? sender, int slide) => DispatcherQueue.TryEnqueue(RefreshPresentation);
    private void OnPresentationEnded(object? sender, SlideEndedReason reason) => DispatcherQueue.TryEnqueue(Close);
    public void RefreshPresentation()
    {
        if (_disposed) return;
        int count = Math.Max(0, _controller.SlideCount);
        while (_items.Count > count) _items.RemoveAt(_items.Count - 1);
        while (_items.Count < count) _items.Add(new() { Index = _items.Count + 1 });
        foreach (var item in _items)
        {
            item.Label = $"第 {item.Index} 页" + (item.Index == _controller.CurrentSlide ? " · 当前页" : "");
            if (item.Image is null && !item.Loading && !string.IsNullOrEmpty(item.Error)
                && item.Attempts < 3 && (item.LastAttempt == 0 || Stopwatch.GetElapsedTime(item.LastAttempt) > TimeSpan.FromSeconds(3))
                && _controller.Capabilities.Navigation && Pages.ContainerFromItem(item) is ListViewItem container)
                _ = LoadThumbnailAsync(item, container);
        }
        if (_controller.CurrentSlide > 0 && _controller.CurrentSlide <= count)
            Pages.SelectedItem = _items[_controller.CurrentSlide - 1];
        Status.Text = _actionError ?? (count == 0
            ? (Stopwatch.GetElapsedTime(_openedAt) < TimeSpan.FromSeconds(8) ? "正在获取页面…" : "尚未获取页面，正在重试连接 PowerPoint。")
            : !_controller.Capabilities.Navigation ? "正在连接 PowerPoint，页面预览将自动更新。"
            : _controller is DebugPresentationController ? "模拟预览" : "点击跳转");
    }
    private void DisposeContent()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop(); _refreshTimer.Tick -= OnRefreshTick;
        _controller.SlideChanged -= OnPresentationChanged;
        _controller.ShowEnded -= OnPresentationEnded;
        _cancel.Cancel(); _cache.Dispose();
        foreach (var item in _items) item.Image = null;
    }
    private async void OnContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not PageItem item) return;
        if (args.InRecycleQueue) { item.Image = null; return; }
        if (item.Image is not null || item.Loading || _controller is DebugPresentationController) return;
        await LoadThumbnailAsync(item, args.ItemContainer);
    }
    private async Task LoadThumbnailAsync(PageItem item, SelectorItem container)
    {
        if (_disposed || item.Loading || _controller is DebugPresentationController) return;
        if (!_controller.Capabilities.Navigation) { item.Error = "等待 PowerPoint 连接…"; return; }
        item.Loading = true;
        item.Attempts++; item.LastAttempt = Stopwatch.GetTimestamp();
        try
        {
            var path = await _cache.GetAsync(item.Index, _cancel.Token);
            if (!_cancel.IsCancellationRequested && container.Content == item) { item.Image = new BitmapImage(new Uri(path)); item.Error = ""; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_cancel.IsCancellationRequested) item.Error = "缩略图加载失败；点击仍可跳页。"; CrashReporter.Log(ex.Message); }
        finally { item.Loading = false; }
    }
    private async void OnItemClick(object sender, ItemClickEventArgs e)
    {
        try { _actionError = null; Pages.IsEnabled = false; await _controller.GoToSlideAsync(((PageItem)e.ClickedItem).Index); Close(); }
        catch (Exception ex) { _actionError = ex.Message; if (!_disposed) { Status.Text = ex.Message; Pages.IsEnabled = true; } }
    }
}
