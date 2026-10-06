using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using RimePPT.Core;
using RimePPT.Core.Ink;
using RimePPT.Services;
using Windows.Graphics;
using Windows.UI;
using Windows.Services.Store;
using System.Threading;

namespace RimePPT.Windows
{
    /// <summary>
    /// 设置窗口：WinUI Gallery 式 NavigationView（Header + 汉堡折叠 + 页脚菜单项），
    /// 窗格顶部显示应用 Logo 与名称，关于页含开发者 Logo（按主题换色）。
    /// 设置项以 Win11 设置风格的卡片分组（全部系统 ThemeResource 刷子）。
    /// </summary>
    public sealed partial class SettingsWindow : Window
    {
        private static SettingsWindow? _instance;

        private bool _suppress = true;
        private readonly DispatcherTimer _connectionTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        private bool _refreshingConnection;
        private bool _closed;
        private StoreUpdateService? _storeUpdates;
        private IReadOnlyList<StorePackageUpdate> _availableUpdates = Array.Empty<StorePackageUpdate>();
        private CancellationTokenSource? _storeCancellation;
        private bool _storeBusy;
        private bool _supportsStoreUpdates;

        // 标记实时预览由本窗口启动（窗口关闭时据此自动结束）
        private bool _previewing;

        public static bool IsOpen => _instance is not null;

        public static void Open()
        {
            if (_instance is not null)
            {
                _instance.Activate();
                return;
            }
            _instance = new SettingsWindow();
            _instance.Activate();
        }

        public SettingsWindow()
        {
            InitializeComponent();
            InitializeFeatureSettings();
            InitializeDeveloperSettings();
            // XAML 中的命名设置控件保留实例与事件，只更换原生导航容器。
            foreach (var page in new[] { AppearancePage, InkPage, ToolbarPage, ClassWidgetsPage, StoreUpdatePage, DebugPage, AboutPage })
            {
                PageHost.Children.Remove(page);
                page.Visibility = Visibility.Visible;
            }
            NavigateSection(AppearancePage, false);

            Title = "RimePPT 设置";
            // WinUI 一体化标题栏：内容贯通顶条，Mica 直达窗口上缘
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            WindowRoot.SizeChanged += (_, _) => AdaptLayout();
            Nav.Loaded += (_, _) => AdaptLayout();
            Nav.PaneOpened += (_, _) => AdaptLayout(); Nav.PaneClosed += (_, _) => AdaptLayout();
            AppSettings.SettingsChanged += SettingsUpdated;
            Nav.ActualThemeChanged += (_, _) =>
            {
                ApplyTitleBarButtonsTheme();
                UpdateDevLogo();
            };
            ApplyTitleBarButtonsTheme();

            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));
            var workArea = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
            AppWindow.Resize(new SizeInt32(Math.Min(1000, workArea.Width - 48), Math.Min(720, workArea.Height - 48)));
            CenterOnPrimary();

            LoadValues();
            LoadImages();
            VersionText.Text = "版本 " + GitHubUpdateService.InstalledVersion;
            InitializeStoreUpdates();
            InitializeGitHubUpdates();
            ClassWidgetsDataPath.Text = ClassWidgetsCourseReader.DataPath;
            _connectionTimer.Tick += OnConnectionTick;
            _connectionTimer.Start();

            Closed += (_, _) =>
            {
                _closed = true;
                _storeCancellation?.Cancel();
                _githubCancellation?.Cancel();
                _connectionTimer.Stop();
                _connectionTimer.Tick -= OnConnectionTick;
                // 由本窗口启动的预览随窗口关闭自动结束，避免浮窗残留
                if (_previewing)
                {
                    _previewing = false;
                    App.Debug.RaiseShowEnded();
                }
                AppSettings.SettingsChanged -= SettingsUpdated;
                _instance = null;
            };
            _suppress = false;
        }

        private void ApplyTitleBarButtonsTheme()
        {
            bool dark = Nav.ActualTheme == ElementTheme.Dark;
            var bar = AppWindow.TitleBar;
            bar.ButtonForegroundColor = dark ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0xFF, 0x88, 0x88, 0x88) : Color.FromArgb(0xFF, 0x99, 0x99, 0x99);
            bar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            bar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        }

        private void LoadImages()
        {
            string baseDir = AppContext.BaseDirectory;
            PaneLogo.Source = new BitmapImage(new Uri(System.IO.Path.Combine(baseDir, "Assets", "icon44.png")));
            AboutLogo.Source = new SvgImageSource(new Uri(System.IO.Path.Combine(baseDir, "Assets", "RimePPT-Logo.svg")));
            UpdateDevLogo();
        }

        /// <summary>开发者 Logo：暗色主题用白色版，浅色主题用黑色版。</summary>
        private void UpdateDevLogo()
        {
            string file = ThemeHelper.IsDarkTheme() ? "MYXMJY-white.png" : "MYXMJY-black.png";
            DevLogo.Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", file)));
        }

        // ———— 标题/窗格拖动 ————

        private void OnPaneHeaderPressed(object sender, PointerRoutedEventArgs e)
        {
            WindowPlumbing.StartMoveDrag(this);
        }

        private void OnHeaderPressed(object sender, PointerRoutedEventArgs e)
        {
            WindowPlumbing.StartMoveDrag(this);
        }

        private void CenterOnPrimary()
        {
            var bounds = Microsoft.UI.Windowing.DisplayArea.Primary.OuterBounds;
            var size = AppWindow.Size;
            AppWindow.Move(new PointInt32(
                bounds.X + Math.Max(0, (bounds.Width - size.Width) / 2),
                bounds.Y + Math.Max(0, (bounds.Height - size.Height) / 2)));
        }

        private void SettingsUpdated(object? sender, EventArgs e)
        {
            _suppress = true; BackendCombo.SelectedIndex = (int)AppSettings.Instance.InkBackend;
            LoadInkAnimationValues();
            WindowRoot.RequestedTheme = App.ToolbarTheme; BackendDescription.Text = App.InkBackendStatus + "。原生墨迹由 PowerPoint 管理；自研模式支持多指、尺寸调节和逐页撤销。";
            _suppress = false;
        }
        private void OnBackendChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || BackendCombo.SelectedIndex < 0) return;
            AppSettings.Instance.InkBackend = (InkBackend)BackendCombo.SelectedIndex; AppSettings.Instance.Save();
        }
        private void AdaptLayout()
        {
            double scale = WindowRoot.XamlRoot?.RasterizationScale ?? 1;
            double available = Math.Max(240, Nav.ActualWidth - (Nav.IsPaneOpen ? Nav.OpenPaneLength : 48));
            double contentWidth = Math.Min(1248, Math.Max(200, available - (available > 900 ? 96 : 48)));
            SectionHeader.Width = contentWidth;
            SectionHeader.HorizontalAlignment = HorizontalAlignment.Center;
            SectionHeader.Margin = new Thickness(0, 28, 0, 24);
            foreach (var section in new[] { AppearancePage, InkPage, ToolbarPage, ClassWidgetsPage, StoreUpdatePage, DebugPage, AboutPage })
            {
                section.Padding = new Thickness(0, 0, 0, 32);
                if (section.Content is FrameworkElement content)
                { content.MaxWidth = 1248; content.Width = contentWidth; content.HorizontalAlignment = HorizontalAlignment.Center; }
            }
            AppTitleBar.Padding = new Thickness(16, 0, Math.Max(144, AppWindow.TitleBar.RightInset / scale), 0);
            void Walk(DependencyObject node)
            {
                if (node is Grid grid && grid.ColumnDefinitions.Count == 2 && grid.Children.Count > 1 && grid.Children[0] is StackPanel)
                {
                    bool narrow = Nav.ActualWidth - (Nav.IsPaneOpen ? Nav.OpenPaneLength : 48) < 500;
                    if (grid.RowDefinitions.Count == 0) { grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); }
                    foreach (var child in grid.Children)
                        if (child is FrameworkElement element && element is not StackPanel)
                        { Grid.SetRow(element, narrow ? 1 : 0); Grid.SetColumn(element, narrow ? 0 : 1); element.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right; element.Margin = narrow ? new Thickness(0,8,0,0) : new Thickness(12,0,0,0); }
                }
                // Walk our logical setting rows only, never NavigationView/control templates.
                if (node is Panel panel) foreach (var child in panel.Children) Walk(child);
                else if (node is Border border && border.Child is not null) Walk(border.Child);
                else if (node is ScrollViewer scroll && scroll.Content is DependencyObject content) Walk(content);
            }
            Walk(AppearancePage); Walk(InkPage); Walk(ToolbarPage); Walk(DebugPage); Walk(AboutPage);
        }

        // ———— 初始化 ————

        private void LoadValues()
        {
            var settings = AppSettings.Instance;
            BackendCombo.SelectedIndex = (int)settings.InkBackend;
            LoadInkAnimationValues();
            WindowRoot.RequestedTheme = App.ToolbarTheme;
            ThemeCombo.SelectedIndex = settings.Theme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            };
            ShowTextToggle.IsOn = settings.ShowToolbarText;
            MarginBox.Value = settings.EdgeMargin;
            AutoShowToggle.IsOn = settings.AutoShowOverlay;
            StartupToggle.IsOn = Startup.GetRunAtStartup();
            LoadToolbarToggles();
        }

        private void LoadToolbarToggles()
        {
            var settings = AppSettings.Instance;
            ZoneLeftRail.IsChecked = settings.ShowLeftRail;
            ZoneRightRail.IsChecked = settings.ShowRightRail;
            ZoneBottomLeft.IsChecked = settings.ShowBottomLeft;
            ZoneBottomCenter.IsChecked = settings.ShowBottomCenter;
            ZoneBottomRight.IsChecked = settings.ShowBottomRight;
            UpdateLayoutWarning();
        }

        // ———— 导航 ————

        private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is null)
            {
                return;
            }

            string tag = (string)args.SelectedItemContainer.Tag;
            PageTitle.Text = tag switch { "ink" => "笔迹", "toolbar" => "工具栏", "classwidgets" => "ClassWidgets 2", "updates" => "商店更新", "debug" => "调试", "about" => "关于", _ => "外观" };
            PageDescription.Text = tag switch
            {
                "ink" => "调整书写、智能图形、板书导出与翻页动画。",
                "classwidgets" => "查看课表插件的连接状态与当前课程。",
                "updates" => "查看 Microsoft Store 更新与发布状态。",
                "toolbar" => "选择浮动工具栏的位置、布局与常用操作。",
                "debug" => "模拟放映、验证工具栏，或重置使用引导。",
                "about" => "了解 RimePPT、开发者与软件技术。",
                _ => "选择书写模式，调整主题与启动行为。"
            };
            NavigateSection(tag switch { "ink" => InkPage, "toolbar" => ToolbarPage, "classwidgets" => ClassWidgetsPage, "updates" => StoreUpdatePage, "debug" => DebugPage, "about" => AboutPage, _ => AppearancePage }, true);
            if (tag == "classwidgets") OnRefreshClassWidgets(this, new RoutedEventArgs());
            if (tag == "debug") ShowDeveloperNotice();
            AdaptLayout();
        }

        private void NavigateSection(ScrollViewer section, bool animate)
        {
            if (ContentFrame.Content is Page previous)
            {
                if (ReferenceEquals(previous.Content, section)) return;
                previous.Content = null;
            }
            bool enabled = animate && new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            ContentFrame.Navigate(typeof(SettingsSectionPage), section, enabled ? new EntranceNavigationTransitionInfo() : new SuppressNavigationTransitionInfo());
            ContentFrame.BackStack.Clear();
        }

        // ———— 外观 ————

        private void LoadInkAnimationValues()
        {
            var settings = AppSettings.Instance;
            SmartShapesToggle.IsOn = settings.SmartShapesEnabled;
            InkAnimationCombo.SelectedIndex = (int)settings.InkPageAnimation;
            InkFadeDurationBox.Value = settings.InkFadeDurationMs;
            InkReplayDurationBox.Value = settings.InkReplayDurationMs;
            InkFadeDurationBox.IsEnabled = settings.InkPageAnimation != InkPageAnimationMode.None;
            InkReplayDurationBox.IsEnabled = settings.InkPageAnimation == InkPageAnimationMode.Replay;
        }

        private void OnInkAnimationChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || InkAnimationCombo.SelectedIndex < 0) return;
            AppSettings.Instance.InkPageAnimation = (InkPageAnimationMode)InkAnimationCombo.SelectedIndex;
            AppSettings.Instance.Save();
        }

        private void OnSmartShapesToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            AppSettings.Instance.SmartShapesEnabled = SmartShapesToggle.IsOn;
            AppSettings.Instance.Save();
        }

        private async void OnExportBoard(object sender, RoutedEventArgs e) => await App.ExportBoardAsync(this);

        private void OnConnectionTick(object? sender, object e)
        {
            if (ContentFrame.Content is Page page && ReferenceEquals(page.Content, ClassWidgetsPage))
                OnRefreshClassWidgets(this, new RoutedEventArgs());
        }

        private async void OnRefreshClassWidgets(object sender, RoutedEventArgs e)
        {
            if (_closed || _refreshingConnection) return;
            _refreshingConnection = true;
            try
            {
                var status = await System.Threading.Tasks.Task.Run(() => ClassWidgetsCourseReader.ReadStatus());
                if (_closed) return;
                ClassWidgetsHostStatus.Text = status.Host;
                ClassWidgetsPluginStatus.Text = status.Plugin;
                ClassWidgetsSubject.Text = status.Course;
                ClassWidgetsHeartbeat.Text = status.Updated?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
                ClassWidgetsInfo.Severity = status.Connected ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
                ClassWidgetsInfo.Title = status.Connected ? "课表联动可正常使用" : "尚未建立有效连接";
                ClassWidgetsInfo.Message = status.Detail;
                ClassWidgetsInfo.IsOpen = true;
            }
            catch (Exception ex)
            {
                CrashReporter.Report(ex, "classwidgets-status");
                if (!_closed) { ClassWidgetsInfo.Severity = InfoBarSeverity.Error; ClassWidgetsInfo.Title = "无法读取连接状态"; ClassWidgetsInfo.Message = "请检查共享数据文件的读取权限。"; }
            }
            finally { _refreshingConnection = false; }
        }

        private void InitializeStoreUpdates()
        {
            var package = StoreUpdateService.GetInstalledPackage();
            _supportsStoreUpdates = StoreUpdateService.SupportsUpdates(package);
            if (package is not null)
            {
                var version = package.Id.Version;
                StoreInstalledVersion.Text = $"已安装版本：{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            else StoreInstalledVersion.Text = "当前运行：便携版";
            StoreCheckButton.IsEnabled = _supportsStoreUpdates;
            StoreUpdateSummary.Text = _supportsStoreUpdates
                ? "检查由 Microsoft Store 提供的 RimePPT 更新。"
                : "请从 Microsoft Store 安装 RimePPT，以使用商店更新。";
            StoreChannelText.Text = _supportsStoreUpdates
                ? "更新由 Microsoft Store 下载和安装。安装可能关闭应用，请先保存批注并结束放映。"
                : package is null ? "便携版使用 ZIP 文件更新；也可以打开商店安装商店版。"
                : "当前为非商店安装包，无法直接使用商店更新。请打开商店安装商店版。";
        }

        private void SetStoreBusy(bool busy)
        {
            _storeBusy = busy;
            StoreCheckButton.IsEnabled = !busy && _supportsStoreUpdates;
            StoreInstallButton.IsEnabled = !busy;
        }

        private void ShowStoreResult(InfoBarSeverity severity, string title, string message)
        {
            StoreUpdateInfo.Severity = severity;
            StoreUpdateInfo.Title = title;
            StoreUpdateInfo.Message = message;
            StoreUpdateInfo.IsOpen = true;
            StoreUpdateHeading.Text = title;
        }

        private async void OnCheckStoreUpdates(object sender, RoutedEventArgs e)
        {
            if (_closed || _storeBusy || !_supportsStoreUpdates) return;
            SetStoreBusy(true);
            _availableUpdates = Array.Empty<StorePackageUpdate>();
            StoreInstallButton.Visibility = Visibility.Collapsed;
            StoreUpdateInfo.IsOpen = false;
            StoreUpdateHeading.Text = "正在检查更新…";
            StoreProgress.Visibility = Visibility.Visible;
            StoreProgress.IsIndeterminate = true;
            StoreProgressText.Visibility = Visibility.Collapsed;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _storeCancellation = cancellation;
            try
            {
                _storeUpdates ??= new StoreUpdateService(WinRT.Interop.WindowNative.GetWindowHandle(this));
                var updates = await _storeUpdates.CheckAsync(cancellation.Token);
                if (_closed) return;
                _availableUpdates = updates;
                StoreLastChecked.Text = $"上次成功检查：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                if (updates.Count == 0)
                    ShowStoreResult(InfoBarSeverity.Success, "当前没有可用更新", "Microsoft Store 暂未提供适用于此安装版本的更新。");
                else
                {
                    ShowStoreResult(InfoBarSeverity.Informational, "发现可用更新", "请先保存批注并结束放映，再下载和安装更新。");
                    StoreInstallButton.Visibility = Visibility.Visible;
                }
            }
            catch (OperationCanceledException)
            {
                if (!_closed) ShowStoreResult(InfoBarSeverity.Warning, "检查更新超时", "请检查网络连接后重试，或在 Microsoft Store 中检查更新。");
            }
            catch (Exception ex)
            {
                CrashReporter.Report(ex, "store-update-check");
                if (!_closed) ShowStoreResult(InfoBarSeverity.Error, "无法检查更新", $"请检查网络连接和 Microsoft Store 是否可用后重试。错误代码：0x{ex.HResult:X8}");
            }
            finally
            {
                _storeCancellation = null;
                if (!_closed) { StoreProgress.Visibility = Visibility.Collapsed; SetStoreBusy(false); }
            }
        }

        private async void OnInstallStoreUpdates(object sender, RoutedEventArgs e)
        {
            if (_closed || _storeBusy || _availableUpdates.Count == 0 || _storeUpdates is null) return;
            if (App.ActiveToolbars.Count > 0)
            {
                ShowStoreResult(InfoBarSeverity.Warning, "请先结束放映", "为避免中断课堂和丢失未保存批注，请结束放映后再安装更新。");
                return;
            }
            SetStoreBusy(true);
            using var cancellation = new CancellationTokenSource();
            _storeCancellation = cancellation;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = WindowRoot.XamlRoot,
                    Title = "下载并安装更新？",
                    Content = "更新可能关闭 RimePPT，请先保存批注。下载和安装将由 Microsoft Store 完成。",
                    PrimaryButtonText = "下载并安装", CloseButtonText = "暂不更新",
                    DefaultButton = ContentDialogButton.Close,
                    RequestedTheme = WindowRoot.ActualTheme
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closed) return;
                if (App.ActiveToolbars.Count > 0)
                {
                    ShowStoreResult(InfoBarSeverity.Warning, "请先结束放映", "放映正在进行，请结束后再安装更新。");
                    return;
                }
                StoreUpdateHeading.Text = "正在下载和安装…";
                StoreUpdateInfo.IsOpen = false;
                StoreProgress.Visibility = Visibility.Visible;
                StoreProgress.IsIndeterminate = true;
                StoreProgressText.Visibility = Visibility.Visible;
                StoreProgressText.Text = "正在等待 Microsoft Store…";
                var result = await _storeUpdates.InstallAsync(_availableUpdates, status =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_closed) return;
                        StoreProgress.IsIndeterminate = false;
                        StoreProgress.Value = Math.Clamp(status.TotalDownloadProgress * 100, 0, 100);
                        StoreProgressText.Text = $"下载和安装进度：{StoreProgress.Value:0}%";
                    });
                }, cancellation.Token);
                if (_closed) return;
                switch (result.OverallState)
                {
                    case StorePackageUpdateState.Completed:
                        _availableUpdates = Array.Empty<StorePackageUpdate>();
                        StoreInstallButton.Visibility = Visibility.Collapsed;
                        ShowStoreResult(InfoBarSeverity.Success, "更新已完成", "如果应用尚未重启，请关闭后重新打开 RimePPT。");
                        break;
                    case StorePackageUpdateState.Canceled:
                        ShowStoreResult(InfoBarSeverity.Informational, "已取消更新", "你可以稍后重新下载和安装。");
                        break;
                    default:
                        ShowStoreResult(InfoBarSeverity.Warning, "更新未完成", $"请检查网络、电量和商店状态，稍后重试。状态：{result.OverallState}");
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                if (!_closed) ShowStoreResult(InfoBarSeverity.Informational, "已取消更新", "你可以稍后重新检查更新。");
            }
            catch (Exception ex)
            {
                CrashReporter.Report(ex, "store-update-install");
                if (!_closed) ShowStoreResult(InfoBarSeverity.Error, "无法安装更新", $"请在 Microsoft Store 中重试。错误代码：0x{ex.HResult:X8}");
            }
            finally
            {
                _storeCancellation = null;
                if (!_closed)
                {
                    StoreProgress.Visibility = Visibility.Collapsed;
                    StoreProgressText.Visibility = Visibility.Collapsed;
                    SetStoreBusy(false);
                }
            }
        }

        private async void OnOpenMicrosoftStore(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!await global::Windows.System.Launcher.LaunchUriAsync(new Uri($"ms-windows-store://pdp/?productid={StoreUpdateService.StoreId}")) && !_closed)
                    ShowStoreResult(InfoBarSeverity.Warning, "无法打开 Microsoft Store", $"可在浏览器中访问 https://apps.microsoft.com/detail/{StoreUpdateService.StoreId}");
            }
            catch (Exception ex)
            {
                CrashReporter.Report(ex, "store-open");
                if (!_closed) ShowStoreResult(InfoBarSeverity.Error, "无法打开 Microsoft Store", $"可在浏览器中访问 https://apps.microsoft.com/detail/{StoreUpdateService.StoreId}");
            }
        }

        private void OnInkDurationChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_suppress || !double.IsFinite(args.NewValue)) return;
            if (sender == InkFadeDurationBox) AppSettings.Instance.InkFadeDurationMs = (int)Math.Round(args.NewValue);
            else AppSettings.Instance.InkReplayDurationMs = (int)Math.Round(args.NewValue);
            AppSettings.Instance.Save();
        }

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress)
            {
                return;
            }
            AppSettings.Instance.Theme = ThemeCombo.SelectedIndex switch
            {
                1 => "light",
                2 => "dark",
                _ => "auto",
            };
            AppSettings.Instance.Save();
        }

        private void OnShowTextToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress)
            {
                return;
            }
            AppSettings.Instance.ShowToolbarText = ShowTextToggle.IsOn;
            AppSettings.Instance.Save();
        }

        private void OnMarginChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_suppress)
            {
                return;
            }
            AppSettings.Instance.EdgeMargin = (int)Math.Clamp(MarginBox.Value, 0, 60);
            AppSettings.Instance.Save();
        }

        private void OnAutoShowToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress)
            {
                return;
            }
            AppSettings.Instance.AutoShowOverlay = AutoShowToggle.IsOn;
            AppSettings.Instance.Save();
        }

        private void OnStartupToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress)
            {
                return;
            }
            AppSettings.Instance.RunAtStartup = StartupToggle.IsOn;
            Startup.SetRunAtStartup(StartupToggle.IsOn);
            AppSettings.Instance.Save();
        }

        // ———— 工具栏 ————

        private void OnZoneToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress)
            {
                return;
            }
            if (sender is not ToggleButton toggle || toggle.Tag is not string tag)
            {
                return;
            }

            var settings = AppSettings.Instance;
            bool isOn = toggle.IsChecked == true;
            switch (tag)
            {
                case "LeftRail": settings.ShowLeftRail = isOn; break;
                case "RightRail": settings.ShowRightRail = isOn; break;
                case "BottomLeft": settings.ShowBottomLeft = isOn; break;
                case "BottomCenter": settings.ShowBottomCenter = isOn; break;
                case "BottomRight": settings.ShowBottomRight = isOn; break;
            }
            settings.Save();
            UpdateLayoutWarning();

            // 预览进行中：带新选择重建浮窗（OnShowStartedCore 自带关闭重建）
            if (_previewing)
            {
                App.Debug.DebugLayouts = settings.GetEnabledToolbarLayouts();
                App.Debug.RaiseShowStarted();
            }
        }

        private void UpdateLayoutWarning()
        {
            var settings = AppSettings.Instance;
            bool none = !settings.ShowLeftRail && !settings.ShowRightRail &&
                        !settings.ShowBottomLeft && !settings.ShowBottomCenter && !settings.ShowBottomRight;
            LayoutWarning.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnPreviewToggled(object sender, RoutedEventArgs e)
        {
            if (_previewing)
            {
                _previewing = false;
                App.Debug.RaiseShowEnded();
                PreviewButton.Content = "启动预览";
                return;
            }

            App.Debug.DebugLayouts = AppSettings.Instance.GetEnabledToolbarLayouts();
            App.Debug.RaiseShowStarted();
            _previewing = true;
            PreviewButton.Content = "结束预览";
        }

        // ———— 调试 ————

        private async void OnOpenGuide(object sender, RoutedEventArgs e)
        {
            try { await App.ShowToolbarGuideAsync((sender as FrameworkElement)?.Tag as string == "complete"); }
            catch (Exception ex) { CrashReporter.Report(ex, "settings-guide"); }
        }

        private void OnResetOnboarding(object sender, RoutedEventArgs e)
        {
            bool reset = App.ResetToolbarOnboarding();
            OnboardingResetInfo.Severity = reset ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            OnboardingResetInfo.Title = reset ? "全部科目引导记录已重置" : "无法重置引导会话";
            OnboardingResetInfo.Message = reset
                ? (App.ActiveToolbars.Count > 0 ? "正在从第一个工具栏按钮重新开始引导。" : "下次工具栏出现时开始引导，也可以在下方启动模拟放映。")
                : "无法保存引导记录，请检查本地数据目录的写入权限后重试。";
            OnboardingResetInfo.IsOpen = true;
        }

        private void OnDebugSide(object sender, RoutedEventArgs e)
        {
            App.Debug.DebugLayouts = new[] { ToolbarLayout.LeftRail, ToolbarLayout.RightRail };
            App.Debug.RaiseShowStarted();
        }

        private void OnDebugBottom(object sender, RoutedEventArgs e)
        {
            App.Debug.DebugLayouts = new[] { ToolbarLayout.BottomLeft, ToolbarLayout.BottomCenter, ToolbarLayout.BottomRight };
            App.Debug.RaiseShowStarted();
        }

        private void OnDebugStop(object sender, RoutedEventArgs e)
        {
            App.Debug.RaiseShowEnded();
        }
    }
}
