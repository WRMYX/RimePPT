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
            // XAML 中的命名设置控件保留实例与事件，只更换原生导航容器。
            foreach (var page in new[] { AppearancePage, InkPage, ToolbarPage, DebugPage, AboutPage })
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
            VersionText.Text = "版本 0.2（开发中）";

            Closed += (_, _) =>
            {
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
            PageTitle.Text = tag switch { "ink" => "笔迹", "toolbar" => "工具栏", "debug" => "调试", "about" => "关于", _ => "外观" };
            PageDescription.Text = tag switch
            {
                "ink" => "设置翻页时笔迹的显示方式与动画节奏。",
                "toolbar" => "选择浮动工具栏的位置、布局与常用操作。",
                "debug" => "模拟放映、验证工具栏，或重置使用引导。",
                "about" => "了解 RimePPT、开发者与软件技术。",
                _ => "选择书写模式，调整主题与启动行为。"
            };
            NavigateSection(tag switch { "ink" => InkPage, "toolbar" => ToolbarPage, "debug" => DebugPage, "about" => AboutPage, _ => AppearancePage }, true);
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
