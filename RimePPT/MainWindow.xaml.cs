using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using WinRT.Interop;

namespace RimePPT
{
    public sealed partial class MainWindow : Window
    {
        private const string RegRunKey  = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RegAppName = "RimePPT";
        private const string SettingBgMode   = "BgMode";   // "tray" | "background"
        private const string SettingAutoStart = "AutoStart"; // "1" | "0"

        private bool _suppressEvents = true;

        public MainWindow()
        {
            InitializeComponent();
            SetFixedSize(420, 360);
            LoadSettings();
            _suppressEvents = false;
        }

        // ── Window sizing ─────────────────────────────────────────────
        private void SetFixedSize(int w, int h)
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            var wid = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var aw  = AppWindow.GetFromWindowId(wid);
            aw.Resize(new Windows.Graphics.SizeInt32(w, h));
            if (aw.Presenter is OverlappedPresenter op)
            {
                op.IsResizable  = false;
                op.IsMaximizable = false;
            }
        }

        // ── Settings persistence ──────────────────────────────────────
        private void LoadSettings()
        {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;

            string bgMode = settings.Values[SettingBgMode] as string ?? "tray";
            RbTray.IsChecked       = bgMode == "tray";
            RbBackground.IsChecked = bgMode == "background";

            string autoStart = settings.Values[SettingAutoStart] as string ?? "0";
            TsAutoStart.IsOn = autoStart == "1";
        }

        private void SaveBgMode(string mode)
        {
            Windows.Storage.ApplicationData.Current.LocalSettings.Values[SettingBgMode] = mode;
        }

        // ── Event handlers ────────────────────────────────────────────
        private void OnBgModeChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            string mode = RbTray.IsChecked == true ? "tray" : "background";
            SaveBgMode(mode);
            // Notify App to apply tray mode change at runtime
            (Application.Current as App)?.ApplyTrayMode(mode == "tray");
        }

        private void TsAutoStart_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = TsAutoStart.IsOn;
            Windows.Storage.ApplicationData.Current.LocalSettings.Values[SettingAutoStart] = on ? "1" : "0";
            SetAutoStart(on);
        }

        private void BtnQuit_Click(object sender, RoutedEventArgs e)
        {
            (Application.Current as App)?.Shutdown();
        }

        // ── Auto-start registry ───────────────────────────────────────
        private static void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegRunKey, writable: true);
                if (key == null) return;
                if (enable)
                {
                    string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                    key.SetValue(RegAppName, $"\"{exe}\"");
                }
                else
                {
                    key.DeleteValue(RegAppName, throwOnMissingValue: false);
                }
            }
            catch { /* registry not available in sandboxed mode */ }
        }

        // ── Status update (called by App) ─────────────────────────────
        public void SetStatus(string text, bool active)
        {
            TbStatus.Text = text;
            StatusDot.Fill = new SolidColorBrush(
                active
                    ? Windows.UI.Color.FromArgb(255, 76, 175, 80)   // green
                    : Windows.UI.Color.FromArgb(255, 255, 152, 0));  // amber
        }
    }
}
