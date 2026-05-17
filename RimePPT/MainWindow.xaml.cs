using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using RimePPT.Models;
using RimePPT.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace RimePPT
{
    public sealed partial class MainWindow : Window
    {
        // ── Services & State ──────────────────────────────────────────
        private readonly PptExportService _pptService = new();
        private List<string> _slidePaths = new();
        private int _currentIndex = 0;

        private bool _isLocked = false;
        private bool _isAnnotating = false;
        private bool _isErasing = false;

        private Color _currentColor = Colors.Red;
        private double _currentThickness = 3.0;

        // Per-slide annotation storage: page index → list of strokes
        private readonly Dictionary<int, List<StrokeData>> _annotations = new();
        // Current page live elements (Polyline ↔ StrokeData pair)
        private readonly List<(Polyline Poly, StrokeData Stroke)> _pageElements = new();

        // Active stroke being drawn
        private Polyline? _activePoly = null;
        private StrokeData? _activeStroke = null;
        private bool _isDrawing = false;

        // ── Constructor ───────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            InitializeWindow();
        }

        private void InitializeWindow()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            Title = "RimePPT";
        }

        // ── File Loading ──────────────────────────────────────────────
        private async void BtnOpenFile_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".pptx");
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            await LoadPptAsync(file.Path);
        }

        private async Task LoadPptAsync(string path)
        {
            WelcomeOverlay.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingText.Text = "正在导出幻灯片，请稍候...";

            try
            {
                _annotations.Clear();
                _pageElements.Clear();
                InkCanvas.Children.Clear();
                _currentIndex = 0;

                _slidePaths = await _pptService.ExportSlidesAsync(path);

                if (_slidePaths.Count == 0)
                {
                    await ShowErrorAsync("未能从文件中读取幻灯片。");
                    WelcomeOverlay.Visibility = Visibility.Visible;
                    return;
                }

                ShowSlide(0);
            }
            catch (InvalidOperationException ex)
            {
                await ShowErrorAsync(ex.Message);
                WelcomeOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                await ShowErrorAsync($"加载失败：{ex.Message}");
                WelcomeOverlay.Visibility = Visibility.Visible;
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        // ── Slide Navigation ──────────────────────────────────────────
        private void ShowSlide(int index)
        {
            if (_slidePaths.Count == 0) return;
            index = Math.Max(0, Math.Min(index, _slidePaths.Count - 1));
            _currentIndex = index;

            SlideImage.Source = new BitmapImage(new Uri(_slidePaths[index]));
            RestoreAnnotations(index);
            UpdatePageInfo();
        }

        private void NavigateSlide(int delta)
        {
            if (_isLocked || _slidePaths.Count == 0) return;
            int next = _currentIndex + delta;
            if (next < 0 || next >= _slidePaths.Count) return;
            SaveCurrentAnnotations();
            ShowSlide(next);
        }

        private void BtnPrev_Click(object sender, RoutedEventArgs e) => NavigateSlide(-1);
        private void BtnNext_Click(object sender, RoutedEventArgs e) => NavigateSlide(1);

        // ── Annotation Storage ────────────────────────────────────────
        private void SaveCurrentAnnotations()
        {
            _annotations[_currentIndex] = _pageElements.Select(e => e.Stroke).ToList();
        }

        private void RestoreAnnotations(int index)
        {
            InkCanvas.Children.Clear();
            _pageElements.Clear();

            if (_annotations.TryGetValue(index, out var strokes))
            {
                foreach (var stroke in strokes)
                {
                    var poly = BuildPolyline(stroke);
                    _pageElements.Add((poly, stroke));
                }
            }
        }

        private Polyline BuildPolyline(StrokeData stroke)
        {
            var poly = new Polyline
            {
                Stroke = new SolidColorBrush(stroke.StrokeColor),
                StrokeThickness = stroke.Thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Points = new PointCollection()
            };
            foreach (var pt in stroke.Points)
                poly.Points.Add(pt);
            InkCanvas.Children.Add(poly);
            return poly;
        }

        // ── Ink Drawing Events ────────────────────────────────────────
        private void InkCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_isAnnotating || _isErasing) return;

            var pt = e.GetCurrentPoint(InkCanvas).Position;
            _activeStroke = new StrokeData
            {
                StrokeColor = _currentColor,
                Thickness = _currentThickness,
                Points = new List<Point> { pt }
            };
            _activePoly = BuildPolyline(_activeStroke);
            _pageElements.Add((_activePoly, _activeStroke));
            _isDrawing = true;
            InkCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void InkCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isAnnotating && !_isErasing && _isDrawing && _activePoly != null && _activeStroke != null)
            {
                var pt = e.GetCurrentPoint(InkCanvas).Position;
                _activeStroke.Points.Add(pt);
                _activePoly.Points.Add(pt);
                e.Handled = true;
            }
            else if (_isAnnotating && _isErasing)
            {
                EraseAtPoint(e.GetCurrentPoint(InkCanvas).Position);
            }
        }

        private void InkCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isDrawing)
            {
                _isDrawing = false;
                _activePoly = null;
                _activeStroke = null;
                try { InkCanvas.ReleasePointerCapture(e.Pointer); } catch { }
                e.Handled = true;
            }
        }

        private void EraseAtPoint(Point pt)
        {
            const double radius = 20.0;
            var toRemove = new List<(Polyline, StrokeData)>();

            foreach (var (poly, stroke) in _pageElements)
            {
                foreach (var sp in stroke.Points)
                {
                    double dx = sp.X - pt.X, dy = sp.Y - pt.Y;
                    if (dx * dx + dy * dy <= radius * radius)
                    {
                        toRemove.Add((poly, stroke));
                        break;
                    }
                }
            }

            foreach (var item in toRemove)
            {
                InkCanvas.Children.Remove(item.Item1);
                _pageElements.Remove(item);
            }
        }

        // ── Toolbar Buttons ───────────────────────────────────────────
        private void BtnAnnotate_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnnotating && !_isErasing)
            {
                SetAnnotateMode(false, false);
                ColorPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                SetAnnotateMode(true, false);
                ColorPanel.Visibility = Visibility.Visible;
            }
        }

        private void BtnEraser_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnnotating && _isErasing)
            {
                SetAnnotateMode(false, false);
            }
            else
            {
                SetAnnotateMode(true, true);
                ColorPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void SetAnnotateMode(bool annotating, bool erasing)
        {
            _isAnnotating = annotating;
            _isErasing = erasing;
            InkCanvas.IsHitTestVisible = annotating;

            var penHighlight = new SolidColorBrush(Color.FromArgb(0x44, 0x21, 0x96, 0xF3));
            var eraserHighlight = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0x98, 0x00));
            var none = new SolidColorBrush(Colors.Transparent);

            BtnLeftAnnotate.Background = (annotating && !erasing) ? penHighlight : none;
            BtnRightAnnotate.Background = BtnLeftAnnotate.Background;
            BtnBottomAnnotate.Background = (annotating && !erasing)
                ? new SolidColorBrush(Color.FromArgb(0x55, 0x21, 0x96, 0xF3))
                : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

            BtnLeftEraser.Background = (annotating && erasing) ? eraserHighlight : none;
            BtnRightEraser.Background = BtnLeftEraser.Background;
            BtnBottomEraser.Background = (annotating && erasing)
                ? new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0x98, 0x00))
                : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }

        private void BtnClearInk_Click(object sender, RoutedEventArgs e)
        {
            InkCanvas.Children.Clear();
            _pageElements.Clear();
            _annotations.Remove(_currentIndex);
        }

        private void BtnLock_Click(object sender, RoutedEventArgs e)
        {
            _isLocked = !_isLocked;
            string glyph = _isLocked ? "\uE72F" : "\uE72E";
            IconLeftLock.Glyph = glyph;
            IconRightLock.Glyph = glyph;
            IconBottomLock.Glyph = glyph;

            BtnLeftPrev.IsEnabled = !_isLocked;
            BtnLeftNext.IsEnabled = !_isLocked;
            BtnRightPrev.IsEnabled = !_isLocked;
            BtnRightNext.IsEnabled = !_isLocked;
        }

        private async void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "退出 RimePPT",
                Content = "确定要退出吗？未保存的批注将丢失。",
                PrimaryButtonText = "退出",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _pptService.Cleanup();
                Application.Current.Exit();
            }
        }

        // ── Color & Thickness ─────────────────────────────────────────
        private void BtnColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                _currentColor = tag switch
                {
                    "Red"    => Colors.Red,
                    "Yellow" => Colors.Yellow,
                    "Green"  => Color.FromArgb(255, 76, 175, 80),
                    "Blue"   => Color.FromArgb(255, 33, 150, 243),
                    "White"  => Colors.White,
                    "Black"  => Colors.Black,
                    _        => Colors.Red
                };
            }
        }

        private void BtnThin_Click(object sender, RoutedEventArgs e)   => _currentThickness = 2.0;
        private void BtnMedium_Click(object sender, RoutedEventArgs e) => _currentThickness = 4.0;
        private void BtnThick_Click(object sender, RoutedEventArgs e)  => _currentThickness = 8.0;

        // ── Helpers ───────────────────────────────────────────────────
        private void UpdatePageInfo()
        {
            TbPageInfo.Text = _slidePaths.Count == 0
                ? "— / —"
                : $"{_currentIndex + 1} / {_slidePaths.Count}";
        }

        private async Task ShowErrorAsync(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "错误",
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}
