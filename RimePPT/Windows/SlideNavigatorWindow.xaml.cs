using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
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
        public string Label { get; init; } = "";
        private BitmapImage? _image; public BitmapImage? Image { get => _image; set { _image = value; PropertyChanged?.Invoke(this, new(nameof(Image))); } }
        private string _error = ""; public string Error { get => _error; set { _error = value; PropertyChanged?.Invoke(this, new(nameof(Error))); } }
        public bool Loading { get; set; }
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
        Status.Text = controller is DebugPresentationController ? "模拟预览" : "点击跳转";
        for (int i = 1; i <= controller.SlideCount; i++) _items.Add(new() { Index = i, Label = $"第 {i} 页" + (i == controller.CurrentSlide ? " · 当前页" : "") });
        Pages.ItemsSource = _items;
        if (controller.CurrentSlide > 0 && controller.CurrentSlide <= _items.Count) Pages.SelectedItem = _items[controller.CurrentSlide - 1];
        _flyout.Opening += (_, _) => IsShowing = true;
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
    private void DisposeContent()
    {
        if (_disposed) return;
        _disposed = true; _cancel.Cancel(); _cache.Dispose();
        foreach (var item in _items) item.Image = null;
    }
    private async void OnContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not PageItem item) return;
        if (args.InRecycleQueue) { item.Image = null; return; }
        if (item.Image is not null || item.Loading || _controller is DebugPresentationController) return;
        item.Loading = true;
        try
        {
            var path = await _cache.GetAsync(item.Index, _cancel.Token);
            if (!_cancel.IsCancellationRequested && args.ItemContainer.Content == item) { item.Image = new BitmapImage(new Uri(path)); item.Error = ""; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_cancel.IsCancellationRequested) item.Error = "缩略图加载失败；点击仍可跳页。"; CrashReporter.Log(ex.Message); }
        finally { item.Loading = false; }
    }
    private async void OnItemClick(object sender, ItemClickEventArgs e)
    {
        try { Pages.IsEnabled = false; await _controller.GoToSlideAsync(((PageItem)e.ClickedItem).Index); Close(); }
        catch (Exception ex) { Status.Text = ex.Message; Pages.IsEnabled = true; }
    }
}
