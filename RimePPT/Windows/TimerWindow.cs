using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RimePPT.Services;
using Windows.Graphics;

namespace RimePPT.Windows;

public sealed class TimerWindow : Window
{
    private readonly Grid _root = new();
    private readonly Border _card = new() { Padding = new(16), CornerRadius = new(8), BorderThickness = new(1) };
    private readonly TextBlock _display = new() { Text = "00:00", FontSize = 48, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status = new() { Text = "就绪", HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ComboBox _mode = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44 };
    private readonly NumberBox _minutes = new() { Header = "分钟", Value = 5, Minimum = 0, Maximum = 999, Width = 120, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _seconds = new() { Header = "秒", Value = 0, Minimum = 0, Maximum = 59, Width = 120, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly StackPanel _duration = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly Button _toggle = new() { Content = "开始", MinHeight = 44, MinWidth = 120 };
    private readonly Button _reset = new() { Content = "重置", MinHeight = 44, MinWidth = 120 };
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DisplayArea? _area;
    private bool _expired, _closed, _dragged;
    private uint? _dragPointer;
    private global::Windows.Foundation.Point _dragStartScreen;
    private PointInt32 _dragStartWindow;
    private TimeSpan _limit;
    private bool Countdown => _mode.SelectedIndex == 1;
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct ScreenPosition { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPosition point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    public TimerWindow()
    {
        Title = "RimePPT 计时器";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
        }
        WindowPlumbing.EnableTransparency(this);
        WindowPlumbing.RemoveWindowBorder(this);
        WindowPlumbing.RemoveResizableFrame(this);
        AppWindow.IsShownInSwitchers = false;
        var panel = new StackPanel { Width = 272, Spacing = 12 };
        var heading = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), ManipulationMode = ManipulationModes.None };
        heading.Children.Add(new TextBlock { Text = "计时器", FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
        heading.PointerPressed += (_, e) =>
        {
            for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
                if (element is Button) return;
            var point = e.GetCurrentPoint(_root);
            if (_dragPointer.HasValue || (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse && !point.IsInContact) ||
                (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) ||
                !heading.CapturePointer(e.Pointer)) return;
            _dragPointer = e.Pointer.PointerId;
            _dragStartWindow = AppWindow.Position;
            _dragStartScreen = ScreenPoint(e);
            _dragged = true;
            RimePPT.Core.Ink.InkDiagnostics.Write(new { kind = "timer-drag", phase = "pressed", device = e.Pointer.PointerDeviceType.ToString() });
            e.Handled = true;
        };
        heading.PointerMoved += (_, e) =>
        {
            if (_dragPointer != e.Pointer.PointerId) return;
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse &&
                (GetAsyncKeyState(1) & 0x8000) == 0)
            {
                _dragPointer = null; heading.ReleasePointerCapture(e.Pointer); return;
            }
            var point = ScreenPoint(e);
            AppWindow.Move(new PointInt32(_dragStartWindow.X + (int)Math.Round(point.X - _dragStartScreen.X),
                _dragStartWindow.Y + (int)Math.Round(point.Y - _dragStartScreen.Y)));
            e.Handled = true;
        };
        void EndDrag(PointerRoutedEventArgs e, bool released)
        {
            if (_dragPointer != e.Pointer.PointerId) return;
            if (released)
            {
                var point = ScreenPoint(e);
                AppWindow.Move(new PointInt32(_dragStartWindow.X + (int)Math.Round(point.X - _dragStartScreen.X),
                    _dragStartWindow.Y + (int)Math.Round(point.Y - _dragStartScreen.Y)));
            }
            _dragPointer = null; heading.ReleasePointerCapture(e.Pointer); e.Handled = true;
            RimePPT.Core.Ink.InkDiagnostics.Write(new { kind = "timer-drag", phase = "released", device = e.Pointer.PointerDeviceType.ToString() });
        }
        heading.PointerReleased += (_, e) => EndDrag(e, true);
        heading.PointerCanceled += (_, e) => EndDrag(e, false);
        heading.PointerCaptureLost += (_, e) => { if (_dragPointer == e.Pointer.PointerId) _dragPointer = null; };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                _dragPointer = null; heading.ReleasePointerCaptures();
            }
        };
        var close = new Button { Content = new FontIcon { Glyph = "\uE8BB", FontSize = 16 }, Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        heading.Children.Add(close);
        _mode.Items.Add("正计时"); _mode.Items.Add("倒计时"); _mode.SelectedIndex = 0;
        _mode.SelectionChanged += (_, _) => { _duration.Visibility = Countdown ? Visibility.Visible : Visibility.Collapsed; Reset(); Place(); };
        _duration.Children.Add(_minutes); _duration.Children.Add(_seconds);
        _minutes.ValueChanged += (_, _) => { if (!_clock.IsRunning) Reset(); };
        _seconds.ValueChanged += (_, _) => { if (!_clock.IsRunning) Reset(); };
        _toggle.Click += (_, _) =>
        {
            if (_clock.IsRunning) { _clock.Stop(); _timer.Stop(); _status.Text = "已暂停"; }
            else
            {
                if (_expired) Reset();
                if (Countdown && _limit <= TimeSpan.Zero) { _status.Text = "请设置倒计时时长"; return; }
                _clock.Start(); _timer.Start(); _status.Text = "运行中";
            }
            Update();
        };
        _reset.Click += (_, _) => Reset();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(_toggle); buttons.Children.Add(_reset);
        panel.Children.Add(heading); panel.Children.Add(_display); panel.Children.Add(_status);
        panel.Children.Add(_mode); panel.Children.Add(_duration); panel.Children.Add(buttons);
        _card.Child = panel; _root.Children.Add(_card); Content = _root;
        ThemeResources.Attach(this, _root, _card);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_display, "TimerDisplay");
        var escape = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { e.Handled = true; Close(); };
        _root.KeyboardAccelerators.Add(escape);
        _root.SizeChanged += (_, _) => Place();
        _timer.Tick += (_, _) => Update();
        Closed += (_, _) => { _closed = true; _dragPointer = null; _timer.Stop(); _clock.Stop(); };
        Reset();
    }
    public void ShowCentered(DisplayArea area) { _area = area; Place(); Activate(); }
    private global::Windows.Foundation.Point ScreenPoint(PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && GetCursorPos(out var cursor))
            return new(cursor.X, cursor.Y);
        var point = e.GetCurrentPoint(_root).Position;
        double scale = _root.XamlRoot?.RasterizationScale ?? GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var position = AppWindow.Position;
        return new(position.X + point.X * scale, position.Y + point.Y * scale);
    }
    private void Reset()
    {
        _timer.Stop(); _clock.Reset(); _expired = false;
        double minutes = double.IsFinite(_minutes.Value) ? Math.Clamp(_minutes.Value, 0, 999) : 0;
        double seconds = double.IsFinite(_seconds.Value) ? Math.Clamp(_seconds.Value, 0, 59) : 0;
        _limit = TimeSpan.FromSeconds(Math.Floor(minutes) * 60 + Math.Floor(seconds));
        _status.Text = "就绪"; Update();
    }
    private void Update()
    {
        var value = Countdown ? _limit - _clock.Elapsed : _clock.Elapsed;
        if (Countdown && _clock.IsRunning && value <= TimeSpan.Zero)
        {
            _clock.Stop(); _timer.Stop(); _expired = true; _status.Text = "时间到";
        }
        value = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        int totalSeconds = (int)(Countdown ? Math.Ceiling(value.TotalSeconds) : Math.Floor(value.TotalSeconds));
        _display.Text = totalSeconds >= 3600 ? $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}" : $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
        _toggle.Content = _clock.IsRunning ? "暂停" : _expired ? "重新开始" : _clock.Elapsed > TimeSpan.Zero ? "继续" : "开始";
        _minutes.IsEnabled = _seconds.IsEnabled = !_clock.IsRunning;
    }
    private void Place()
    {
        if (_area is null || _closed) return;
        _card.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        int width = (int)Math.Ceiling(_card.DesiredSize.Width * scale), height = (int)Math.Ceiling(_card.DesiredSize.Height * scale);
        if (width <= 0 || height <= 0) return;
        AppWindow.Resize(new SizeInt32(width, height));
        if (!_dragged) { var bounds = _area.OuterBounds; AppWindow.Move(new PointInt32(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2)); }
    }
}
