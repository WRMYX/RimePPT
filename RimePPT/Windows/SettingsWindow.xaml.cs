using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using RimePPT.Core;
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

            Title = "RimePPT 设置";
            // WinUI 一体化标题栏：内容贯通顶条，Mica 直达窗口上缘
            ExtendsContentIntoTitleBar = true;
            Nav.ActualThemeChanged += (_, _) =>
            {
                ApplyTitleBarButtonsTheme();
                UpdateDevLogo();
            };
            ApplyTitleBarButtonsTheme();

            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));
            AppWindow.Resize(new SizeInt32(820, 620));
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

        // ———— 初始化 ————

        private void LoadValues()
        {
            var settings = AppSettings.Instance;
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
            AppearancePage.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
            ToolbarPage.Visibility = tag == "toolbar" ? Visibility.Visible : Visibility.Collapsed;
            DebugPage.Visibility = tag == "debug" ? Visibility.Visible : Visibility.Collapsed;
            AboutPage.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        }

        // ———— 外观 ————

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
