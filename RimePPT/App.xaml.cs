using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RimePPT.Services;
using System;
using WinRT.Interop;

namespace RimePPT
{
    public partial class App : Application
    {
        // ── Long-lived objects ────────────────────────────────────────
        private MainWindow?    _settingsWindow;
        private OverlayWindow? _overlayWindow;
        private PptMonitorService?  _monitor;
        private PptControlService   _control = new();
        private TrayService?   _tray;

        private DispatcherQueue? _uiQueue;

        public App() { InitializeComponent(); }

        // ── Launch ────────────────────────────────────────────────────
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _uiQueue = DispatcherQueue.GetForCurrentThread();

            // Create and immediately hide the settings window (keeps app alive)
            _settingsWindow = new MainWindow();
            HideWindow(_settingsWindow);

            // Create the transparent overlay (hidden until slideshow detected)
            _overlayWindow = new OverlayWindow(_control);
            HideWindow(_overlayWindow);

            // Start PPT monitor
            _monitor = new PptMonitorService();
            _monitor.SlideshowStarted += OnSlideshowStarted;
            _monitor.SlideshowEnded   += OnSlideshowEnded;
            _monitor.Start();

            // Apply tray mode from settings
            string bgMode = Windows.Storage.ApplicationData.Current
                                   .LocalSettings.Values["BgMode"] as string ?? "tray";
            ApplyTrayMode(bgMode == "tray");
        }

        // ── Slideshow events (may arrive on threadpool) ───────────────
        private void OnSlideshowStarted(IntPtr pptHwnd)
        {
            _uiQueue?.TryEnqueue(() =>
            {
                _settingsWindow?.SetStatus("🎯 正在放映中", true);
                _overlayWindow?.ShowOverlay(pptHwnd);
            });
        }

        private void OnSlideshowEnded()
        {
            _uiQueue?.TryEnqueue(() =>
            {
                _settingsWindow?.SetStatus("正在监听 PowerPoint 放映…", false);
                _overlayWindow?.HideOverlay();
            });
        }

        // ── Tray management ───────────────────────────────────────────
        public void ApplyTrayMode(bool showTray)
        {
            if (showTray && _tray == null && _settingsWindow != null)
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(_settingsWindow);
                _tray = new TrayService(hwnd);
                _tray.OpenSettingsRequested += ShowSettings;
                _tray.ExitRequested += Shutdown;
                _tray.Show();
            }
            else if (!showTray && _tray != null)
            {
                _tray.Dispose();
                _tray = null;
            }
        }

        // ── Settings window ───────────────────────────────────────────
        public void ShowSettings()
        {
            _uiQueue?.TryEnqueue(() =>
            {
                if (_settingsWindow == null) return;
                IntPtr hwnd = WindowNative.GetWindowHandle(_settingsWindow);
                var wid = Win32Interop.GetWindowIdFromWindow(hwnd);
                AppWindow.GetFromWindowId(wid).Show();
                _settingsWindow.Activate();
            });
        }

        // ── Shutdown ──────────────────────────────────────────────────
        public void Shutdown()
        {
            _monitor?.Stop();
            _monitor?.Dispose();
            _tray?.Dispose();
            Exit();
        }

        // ── Helpers ───────────────────────────────────────────────────
        private static void HideWindow(Window w)
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(w);
            var wid = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow.GetFromWindowId(wid).Hide();
        }
    }
}
