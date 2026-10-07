using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RimePPT.Core;
using RimePPT.Core.Ink;
using RimePPT.Services;
using RimePPT.Windows;

namespace RimePPT
{
    /// <summary>
    /// 应用组合根：持有演示控制器与托盘，编排工具条/批注层/墨迹持久化
    /// 随放映事件的生命周期。控制器事件可能来自 COM 线程，统一 marshal 到 UI 线程。
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private System.Threading.Mutex? _singleInstance;
        private DispatcherQueue? _dispatcher;
        private IPresentationController? _presenting;
        private AnnotationWindow? _annotation;
        private PromptWindow? _promptWindow;
        private ToolsMenuWindow? _toolsMenu;
        private BlackoutWindow? _blackout;
        private TimerWindow? _timer;
        private Microsoft.UI.Windowing.DisplayArea? _showArea;
        private bool _annotating;
        private bool _erasing;
        private bool _inkDirty;      // 本次放映墨迹有变化（含擦除，加载恢复的不算）
        private bool _saveSettled;   // 保存询问已处理过，避免重复弹窗
        private InkBackendCoordinator? _backend;
        private PenPickerWindow? _picker;
        private bool _settingsPending, _settingsApplying;
        private string? _toolbarSettingsSnapshot, _nativeColorSnapshot;
        private SlideNavigatorWindow? _navigator;
        private SpotlightWindow? _spotlight;
        private int _toolTransitionVersion;
        private static App? _instance;
        private readonly ToolbarOnboardingSession _onboarding = new();
        public static bool ResetToolbarOnboarding()
        {
            if (!OnboardingState.TryReset()) return false;
            if (_instance is { } app)
            {
                app._onboarding.Stop();
                if (ActiveToolbars.Count > 0) _ = app._onboarding.StartAsync(ActiveToolbars.ToArray());
            }
            return true;
        }
        public static InkBackend EffectiveInkBackend => _instance?._backend?.EffectiveBackend ?? AppSettings.Instance.InkBackend;
        public static string InkBackendStatus => _instance?._backend?.Status ?? (AppSettings.Instance.InkBackend == InkBackend.Native ? "PowerPoint 原生（需正在放映）" : "RimePPT 自研");
        public static async Task ClearInkAsync()
        {
            if (_instance?._backend is not { } backend) return;
            try { await backend.ClearCurrentSlideAsync(); }
            catch (Exception ex) { await _instance.ShowPromptAsync(_instance._showArea, "无法清屏", ex.Message, "确定", "关闭"); }
        }
        private H.NotifyIcon.TaskbarIcon? _trayIcon;

        /// <summary>当前存活的工具条浮窗集合。</summary>
        public static List<ToolbarWindow> ActiveToolbars { get; } = new();

        /// <summary>本次放映会话的墨迹（页码 → 笔画）。</summary>
        public static Dictionary<int, List<StrokeData>> SessionInk { get; } = new();
        private bool _exporting;
        public static async Task ExportBoardAsync(Window owner)
        {
            var app = _instance;
            if (app is null || app._exporting) return;
            var controller = app._presenting;
            app._annotation?.FinishInput();
            if (controller is null || !SessionInk.Values.Any(s => s.Count > 0))
            {
                await app.ShowPromptAsync(app._showArea, "没有可导出的板书", "请先在放映中使用 RimePPT 自研笔迹书写。PowerPoint 原生笔迹由 Office 管理。", "知道了", "关闭"); return;
            }
            app._exporting = true;
            try
            {
                var snapshot = SessionInk.ToDictionary(p => p.Key, p => p.Value.Select(s => new StrokeData { Id = s.Id, SlideIndex = s.SlideIndex, Argb = (byte[])s.Argb.Clone(), ThicknessDips = s.ThicknessDips, LineStyle = s.LineStyle, Dots = s.Dots.Select(d => new StrokeData.Dot { X = d.X, Y = d.Y }).ToList() }).ToList());
                var viewport = app._annotation?.CurrentViewport ?? new InkViewport(1920, 1080);
                var picker = new global::Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
                var destination = await picker.PickSingleFolderAsync(); if (destination is null) return;
                string folder = await BoardExportService.ExportAsync(controller, snapshot, viewport, destination.Path);
                await global::Windows.System.Launcher.LaunchFolderAsync(await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(folder));
            }
            catch (Exception ex) { CrashReporter.Report(ex, "board-export"); await app.ShowPromptAsync(app._showArea, "导出未完成", ex.Message, "知道了", "关闭"); }
            finally { app._exporting = false; }
        }
        public static async Task ShowToolbarGuideAsync(bool complete = true)
        {
            if (_instance is not { } app) return;
            if (ActiveToolbars.Count == 0)
            {
                await app.ShowPromptAsync(app._showArea, "放映时查看指南", "请先开始放映，或在调试页面启动模拟放映。指南会围绕实际工具栏按钮展开。", "知道了", "关闭");
                return;
            }
            await app._onboarding.StartAsync(ActiveToolbars.ToArray(), force: true, complete: complete);
        }

        /// <summary>真实 PowerPoint 控制器（应用启动即开始轮询）。</summary>
        public static PowerPointController PowerPoint { get; } = new();
        public static WpsPresentationController Wps { get; } = new();

        /// <summary>调试模拟控制器（无 Office 时验收用）。</summary>
        public static DebugPresentationController Debug { get; } = new();

        /// <summary>主窗口（托盘“显示主面板”用）。</summary>
        public static Window? Main { get; private set; }

        /// <summary>工具条主题选择：由设置窗口控制（兼容旧入口）。</summary>
        public static ElementTheme ToolbarTheme
        {
            get => AppSettings.Instance.Theme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            set
            {
                AppSettings.Instance.Theme = value switch
                {
                    ElementTheme.Light => "light",
                    ElementTheme.Dark => "dark",
                    _ => "auto",
                };
                AppSettings.Instance.Save();
            }
        }

        public App()
        {
            InitializeComponent(); _instance = this;
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 全局异常兜底最先就位：任何线程的未处理异常都要落日志并可见，
            // 而不是无提示消失（"后台都没了"）
            CrashReporter.Init();

            // 单实例保护：主窗口是隐藏锚点，托盘图标是唯一可见入口，
            // 用户极易误以为未启动而再次拉起——第二个实例直接退出
            _singleInstance = new System.Threading.Mutex(true, @"Local\RimePPT.SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                Environment.Exit(0);
                return;
            }

            _dispatcher = DispatcherQueue.GetForCurrentThread();
            // 首次使用先持久化待展示状态，之后即使设置文件生成仍保留引导资格。
            if (OnboardingState.ShouldShow()) OnboardingState.TryArmFreshUser();
            AppSettings.Load();
            if (AppSettings.IsFreshInstallation)
            {
                AppSettings.Instance.Persist();
            }
            _ = Startup.InitializeAsync(AppSettings.Instance, AppSettings.IsFreshInstallation);
            _toolbarSettingsSnapshot = ToolbarSettingsSignature();
            AppSettings.SettingsChanged += (_, _) => QueueSettingsRefresh();

            PowerPoint.ShowStarted += OnShowStarted;
            PowerPoint.ShowEnded += OnShowEnded;
            PowerPoint.SlideChanged += OnSlideChanged;
            Debug.ShowStarted += OnShowStarted;
            Debug.ShowEnded += OnShowEnded;
            Debug.SlideChanged += OnSlideChanged;
            PowerPoint.Start();
            Wps.ShowStarted += OnShowStarted; Wps.ShowEnded += OnShowEnded; Wps.SlideChanged += OnSlideChanged;
            Wps.Start();

            // 诊断入口：RimePPT.exe --crash-test 模拟后台线程致命异常，
            // 验证报错弹窗与退出路径
            foreach (string a in Environment.GetCommandLineArgs())
            {
                if (string.Equals(a, "--crash-test", StringComparison.OrdinalIgnoreCase))
                {
                    var timer = new System.Threading.Timer(_ =>
                        throw new InvalidOperationException("crash-test: 模拟后台线程致命异常"),
                        null, 3000, System.Threading.Timeout.Infinite);
                    GC.KeepAlive(timer);
                    break;
                }
            }

            // MainWindow 仅为隐藏的应用锚点（托盘与设置窗承载全部交互）
            _window = new MainWindow();

            // 自动化/排障入口：RimePPT.exe --settings 直接打开设置窗口
            string[] cliArgs = Environment.GetCommandLineArgs();
            foreach (string a in cliArgs)
            {
                if (string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase))
                {
                    SettingsWindow.Open();
                    break;
                }
            }

            InitializeTray();
            _ = CheckGitHubUpdatesAtStartupAsync();
            // 独立预览诊断入口，不读写课件或墨迹，用于验证原生询问框。
            var promptPreview = cliArgs.FirstOrDefault(a => a.StartsWith("--prompt-preview=", StringComparison.OrdinalIgnoreCase));
            if (promptPreview is not null)
                _dispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        bool load = promptPreview.EndsWith("=load", StringComparison.OrdinalIgnoreCase);
                        var choice = await ShowPromptAsync(DisplayArea.Primary, load ? "打开已保存的墨迹" : "保存墨迹",
                            load ? "发现这个课件之前保存的批注墨迹。是否打开并恢复到对应页面？" : "本次放映的批注墨迹是否保存到本地，以便下次打开课件时恢复？",
                            load ? "打开" : "保存", load ? "不打开" : "不保存", "删除墨迹");
                        CrashReporter.Log($"prompt preview result: {choice}");
                    }
                    catch (Exception ex) { CrashReporter.Report(ex, "prompt-preview"); }
                });
        }

        // ———— 放映事件 ————

        private void OnShowStarted(object? sender, EventArgs e)
        {
            // TryEnqueue 回调是 async void 语义：体内异常会直接杀死进程，
            // 必须整体兜住（"莫名退出"的最大嫌疑入口）
            _dispatcher?.TryEnqueue(async () =>
            {
                try
                {
                    await OnShowStartedCore(sender!);
                }
                catch (Exception ex)
                {
                    CrashReporter.Report(ex, "show-started");
                }
            });
        }

        private async Task OnShowStartedCore(object sender)
        {
            if (_presenting is { IsPresenting: true } && !ReferenceEquals(sender, _presenting)) return;
            CloseAllToolbars();
            ClosePrompt();
            CloseAnnotation();
            CloseToolsMenu();
            CloseBlackout();
            CloseTimer();
            SessionInk.Clear();
            _annotating = false;
            _erasing = false;
            _inkDirty = false;
            _saveSettled = false;
            _presenting = (IPresentationController)sender;

                ToolbarLayout[] layouts = sender is DebugPresentationController debug
                    ? debug.DebugLayouts
                    : AppSettings.Instance.GetEnabledToolbarLayouts();

                DisplayArea area = sender is PowerPointController ppt
                    ? WindowPlumbing.GetDisplayAreaFromHwnd(ppt.ShowWindowHandle) ?? DisplayArea.Primary
                    : DisplayArea.Primary;
                _showArea = area;

                foreach (var layout in layouts.Where(x => AppSettings.Instance.GetToolbarCommands(x).Count > 0 || AppSettings.Instance.QuickLaunchEntries.Any(e => e.PinnedLayout == x)))
                {
                    var toolbar = new ToolbarWindow(layout, AppSettings.Instance.GetToolbarCommands(layout));
                    toolbar.ToolbarClicked += OnToolbarClicked;
                    toolbar.ToolSettingsRequested += OpenToolSettings;
                    toolbar.ApplySettings();
                    toolbar.ShowOn(area);
                    ActiveToolbars.Add(toolbar);
                }

                // 批注层最后创建（创建顺序影响置顶带内的输入路由稳定性）；
                // 书写时批注层会盖住工具条，笔画结束（松开）后 App 会把工具条重提上来
                _annotation = new AnnotationWindow(SessionInk);
                _annotation.InkChanged += (_, _) => { _inkDirty = true; SyncPresentationControls(); };
                _annotation.CompanionsRaise = RaiseCompanions;
                _annotation.ShowOn(area);
                _annotation.SetActiveSlide(_presenting.CurrentSlide);
                SyncPresentationControls();
                _backend = new InkBackendCoordinator(_presenting, _annotation);
                await _backend.SwitchBackendAsync(AppSettings.Instance.InkBackend);
                foreach (var toolbar in ActiveToolbars) toolbar.BeginEntrance();

                // 墨迹持久化：放映开始时询问是否加载之前保存的墨迹
                var path = _presenting.ShowFilePath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    await HandleInkLoadAsync(path, _presenting.CurrentSlide, area);
                }
                if (WhiteboardWindow.IsOpen) SetWhiteboardMode(true);
                else _ = _onboarding.StartAsync(ActiveToolbars.ToArray());
        }

        private void OnSlideChanged(object? sender, int slide)
        {
            Log($"slide changed -> {slide}");
            _dispatcher?.TryEnqueue(() =>
            {
                try
                {
                    if (sender != _presenting) return;
                    _spotlight?.Close(); _spotlight = null;
                    _annotation?.SetActiveSlide(slide);
                    if (!WhiteboardWindow.IsOpen) SyncPresentationControls();
                }
                catch (Exception ex)
                {
                    CrashReporter.Report(ex, "slide-changed");
                }
            });
        }

        private void OnShowEnded(object? sender, SlideEndedReason reason)
        {
            _dispatcher?.TryEnqueue(async () =>
            {
                if (!ReferenceEquals(sender, _presenting)) return;
                try
                {
                    await OnShowEndedCore();
                }
                catch (Exception ex)
                {
                    CrashReporter.Report(ex, "show-ended");
                }
            });
        }

        private async Task OnShowEndedCore()
        {
            var path = _presenting?.ShowFilePath;
            _toolTransitionVersion++;
            _backend = null;
            _navigator?.Close(); _navigator = null; _spotlight?.Close(); _spotlight = null;

            CloseAllToolbars();
            CloseAnnotation();
            ClosePenChevron();
            CloseToolsMenu();
            CloseBlackout();
            CloseTimer();
            ClosePrompt(); // 退出放映时关闭仍在等待的加载询问窗
            AppSettings.Instance.ResetPenDefaults();
            AppSettings.Instance.Save();

            // 兜底：不是通过工具条“退出”结束的放映（如按 Esc），
            // 若有新书写的墨迹，结束后仍询问一次
            if (!_saveSettled && _inkDirty && !string.IsNullOrEmpty(path))
            {
                _saveSettled = true;
                var snapshot = new Dictionary<int, List<StrokeData>>(SessionInk);
                var result = await ShowSavePromptAsync(snapshot);
                if (result == PromptResult.Primary)
                {
                    await SaveInkAsync(path, snapshot);
                }
                else if (result == PromptResult.Delete)
                {
                    await InkStore.DeleteAsync(await InkStore.HashFileAsync(path));
                }
            }

            SessionInk.Clear();
            _annotating = false;
            _erasing = false;
            _inkDirty = false;
            _presenting = null;
            if (WhiteboardWindow.IsOpen) SetWhiteboardMode(true);
        }

        // ———— 工具条命令 ————

        private async void OnToolbarClicked(object? sender, ToolbarCommand command)
        {
            if (WhiteboardWindow.IsOpen && sender is ToolbarWindow boardToolbar)
            {
                try { await WhiteboardWindow.HandleCommandAsync(command, boardToolbar); }
                catch (Exception ex) { CrashReporter.Report(ex, "whiteboard-command"); }
                return;
            }
            if (_onboarding.TryPreviewCommand(command)) return;
            var controller = _presenting;
            if (controller is null)
            {
                return;
            }

            try
            {
                Log($"toolbar click: {command}");
                switch (command)
                {
                    case ToolbarCommand.Spotlight: await OpenSpotlightAsync(); break;
                    case ToolbarCommand.Blackout: await ToggleBlackoutAsync(); break;
                    case ToolbarCommand.Timer: ToggleTimer((ToolbarWindow)sender!); break;
                    case ToolbarCommand.Export: await ExportBoardAsync((ToolbarWindow)sender!); break;
                    case ToolbarCommand.Whiteboard: WhiteboardWindow.Open(); break;
                    case ToolbarCommand.QuickLaunch: OpenQuickLaunch((ToolbarWindow)sender!); break;
                    case ToolbarCommand.Prev:
                        await controller.PreviousAsync();
                        break;
                    case ToolbarCommand.Next:
                        await controller.NextAsync();
                        break;
                    case ToolbarCommand.ExitShow:
                    {
                        _annotation?.FinishInput();
                        // 先询问保存（有新书写的墨迹时），选择后再真正退出放映。
                        // 弹窗前先拍快照：等待期间会话可能被清空，保存只写快照
                        if (_inkDirty && !_saveSettled &&
                            controller is PowerPointController ppt &&
                            !string.IsNullOrEmpty(ppt.ShowFilePath))
                        {
                            _saveSettled = true;
                            var snapshot = new Dictionary<int, List<StrokeData>>(SessionInk);
                            var result = await ShowSavePromptAsync(snapshot);
                            if (result == PromptResult.Primary)
                            {
                                await SaveInkAsync(ppt.ShowFilePath, snapshot);
                            }
                            else if (result == PromptResult.Delete)
                            {
                                // 删除已有档案（本次墨迹不保存）
                                await InkStore.DeleteAsync(await InkStore.HashFileAsync(ppt.ShowFilePath));
                            }
                            SessionInk.Clear();
                        }

                        await controller.ExitShowAsync();
                        break;
                    }
                    case ToolbarCommand.Annotate:
                        await ToggleToolAsync(AnnotationTool.Pen);
                        break;
                    case ToolbarCommand.Eraser:
                        await ToggleToolAsync(AnnotationTool.Eraser);
                        break;
                    case ToolbarCommand.Tools:
                        ToggleToolsMenu((ToolbarWindow)sender!);
                        break;
                    case ToolbarCommand.Pages:
                        await OpenNavigatorAsync((ToolbarWindow)sender!);
                        break;
                    case ToolbarCommand.Undo:
                        if (EffectiveInkBackend == InkBackend.Rime) _annotation?.Undo();
                        SyncPresentationControls();
                        break;
                    case ToolbarCommand.Redo:
                        if (EffectiveInkBackend == InkBackend.Rime) _annotation?.Redo();
                        SyncPresentationControls();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"toolbar command {command} failed: {ex.Message}");
            }
        }

        // ———— 批注开关 ————

        private static string ToolbarSettingsSignature()
        {
            var s = AppSettings.Instance;
            return $"{s.Theme}|{s.ShowToolbarText}|{s.EdgeMargin}|{string.Join(',', s.GetEnabledToolbarLayouts())}|{string.Join(';', Enum.GetValues<ToolbarLayout>().Select(x => $"{x}:{string.Join(',', s.GetToolbarCommands(x))}:{ToolbarWindow.ExtrasSignature(x)}"))}";
        }
        private void QueueSettingsRefresh()
        {
            _settingsPending = true;
            if (_settingsApplying || _dispatcher is null) return;
            _settingsApplying = true;
            if (!_dispatcher.TryEnqueue(ProcessSettingsRefresh)) _settingsApplying = false;
        }
        private async void ProcessSettingsRefresh()
        {
            try
            {
                while (_settingsPending)
                {
                    _settingsPending = false;
                    foreach (var toolbar in ActiveToolbars)
                        if (toolbar.GetCommandTarget(ToolbarCommand.Annotate) is RimePPT.Controls.ToolbarButton penButton)
                            penButton.UpdatePenColor();
                    var signature = ToolbarSettingsSignature();
                    if (_toolbarSettingsSnapshot != signature)
                    {
                        _toolbarSettingsSnapshot = signature;
                        foreach (var toolbar in ActiveToolbars) toolbar.ApplySettings();
                        RebuildToolbars();
                    }
                    if (_backend is not null && _presenting is not null && !WhiteboardWindow.IsOpen)
                    {
                        var desired = _presenting is DebugPresentationController or WpsPresentationController ? InkBackend.Rime : AppSettings.Instance.InkBackend;
                        bool switched = _backend.EffectiveBackend != desired;
                        if (switched) await _backend.SwitchBackendAsync(desired);
                        string color = Convert.ToHexString(AppSettings.Instance.GetPenArgb());
                        if (switched || (_backend.EffectiveBackend == InkBackend.Native && _annotating && !_erasing && _nativeColorSnapshot != color))
                        {
                            await _backend.SetToolAsync(_annotating ? (_erasing ? AnnotationTool.Eraser : AnnotationTool.Pen) : null);
                            _nativeColorSnapshot = color;
                            SyncAnnotationChecks();
                        }
                    }
                    _picker?.RefreshBackendState();
                }
            }
            catch (Exception ex) { CrashReporter.Report(ex, "settings-changed"); }
            finally { _settingsApplying = false; if (_settingsPending) QueueSettingsRefresh(); }
        }

        private async Task ToggleToolAsync(AnnotationTool tool)
        {
            if (_backend is null) return;
            bool selected = _annotating && _erasing == (tool == AnnotationTool.Eraser);
            _picker?.Dismiss(); _picker = null;
            try
            {
                await _backend.SetToolAsync(selected ? null : tool);
                _annotating = !selected; _erasing = !selected && tool == AnnotationTool.Eraser;
            }
            catch (Exception ex)
            {
                _annotating = false; _erasing = false;
                await ShowPromptAsync(_showArea, "工具切换失败", ex.Message, "确定", "关闭");
            }
            SyncAnnotationChecks(); RaiseCompanions();
        }
        private void OpenToolSettings(object? sender, ToolbarCommand command)
        {
            if (_onboarding.IsRunning) return;
            if (sender is not ToolbarWindow toolbar) return;
            if (toolbar.GetToolSettingsTarget(command) is not { } anchor) return;
            if (_picker is { IsShowing: true } && _picker.Owns(anchor)) { ClosePenChevron(); return; }
            _picker?.Dismiss();
            var picker = WhiteboardWindow.IsOpen ? PenPickerWindow.ForWhiteboard(command == ToolbarCommand.Eraser) :
                command == ToolbarCommand.Eraser ? new EraserPickerWindow() : new PenPickerWindow();
            _picker = picker;
            picker.Closed += (_, _) => { if (ReferenceEquals(_picker, picker)) _picker = null; };
            picker.ShowAt(anchor, toolbar.Layout);
        }
        private void ShowPenChevron() { }
        private void ClosePenChevron() { _picker?.Dismiss(); _picker = null; }
        private void RaiseCompanions()
        {
            if (_blackout is not null) { WindowPlumbing.RaiseToTopmost(_blackout); return; }
            if (_spotlight is not null) { WindowPlumbing.RaiseToTopmost(_spotlight); return; }
            foreach (var toolbar in ActiveToolbars) { WindowPlumbing.RaiseToTopmost(toolbar); toolbar.RaiseArrow(); }
            if (_timer is not null) WindowPlumbing.RaiseToTopmost(_timer);
        }

        private void SyncPresentationControls()
        {
            if (_presenting is null) return;
            bool custom = EffectiveInkBackend == InkBackend.Rime;
            foreach (var toolbar in ActiveToolbars)
                toolbar.UpdatePresentation(_presenting.CurrentSlide, _presenting.SlideCount,
                    custom && (_annotation?.CanUndo ?? false), custom && (_annotation?.CanRedo ?? false));
        }
        private void SyncAnnotationChecks()
        {
            SyncPresentationControls();
            foreach (var toolbar in ActiveToolbars)
            {
                toolbar.SetCommandChecked(ToolbarCommand.Annotate, _annotating && !_erasing);
                toolbar.SetCommandChecked(ToolbarCommand.Eraser, _annotating && _erasing);
            }
        }

        private void CloseAnnotation()
        {
            _annotation?.Close();
            _annotation = null;
        }

        // ———— 工具（黑屏 / 计时器 / 设置入口） ————

        private void ToggleToolsMenu(ToolbarWindow anchor)
        {
            if (_toolsMenu is not null)
            {
                CloseToolsMenu();
                return;
            }

            if (anchor.GetCommandTarget(ToolbarCommand.Tools) is not { } target) return;
            ClosePenChevron(); _navigator?.Close(); _navigator = null;
            var settings = AppSettings.Instance;
            var actions = new List<(string Key, string Glyph, string Name, Action Action)>
            {
                ("spotlight","\uE7B3", "聚光与放大", () => RunFeatureAsync(OpenSpotlightAsync)),
                ("blackout","\uE708", "黑屏模式", () => RunFeatureAsync(ToggleBlackoutAsync)),
                ("timer","\uE916", "计时器", () => ToggleTimer(anchor)),
                ("export","\uE896", "导出课堂板书", () => RunFeatureAsync(() => ExportBoardAsync(anchor))),
                ("whiteboard","\uE70F", "独立画板", WhiteboardWindow.Open),
                ("launcher","\uE8A7", "快捷启动", () => OpenQuickLaunch(anchor)),
                ("guide","\uE897", "使用指南", () => RunFeatureAsync(() => ShowToolbarGuideAsync())),
            };
            var items = actions.Where(x => settings.IsToolVisible(x.Key)).Select(x => (x.Glyph, x.Name, x.Action)).ToList();
            items.Add(("\uE713", "设置", SettingsWindow.Open));
            var menu = new ToolsMenuWindow(items);
            _toolsMenu = menu;
            menu.Closed += (_, _) => { if (ReferenceEquals(_toolsMenu, menu)) _toolsMenu = null; };
            menu.ShowAt(target, anchor.Layout);
        }

        private async void RunFeatureAsync(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) { await ShowPromptAsync(_showArea, "无法打开工具", ex.Message, "确定", "关闭"); }
        }
        private void OpenQuickLaunch(ToolbarWindow anchor)
        {
            CloseToolsMenu();
            var target = anchor.GetCommandTarget(ToolbarCommand.QuickLaunch) ?? anchor.GetCommandTarget(ToolbarCommand.Tools);
            if (target is null) return;
            var items = AppSettings.Instance.QuickLaunchEntries.Select(entry => ("\uE8A7", entry.Name, (Action)(() => RunFeatureAsync(() => QuickLaunchService.LaunchAsync(entry))))).ToList();
            if (items.Count == 0) items.Add(("\uE713", "在设置中添加应用或文件", SettingsWindow.Open));
            var menu = new ToolsMenuWindow(items); _toolsMenu = menu;
            menu.Closed += (_, _) => { if (ReferenceEquals(_toolsMenu, menu)) _toolsMenu = null; };
            menu.ShowAt(target, anchor.Layout);
        }
        private Task OpenNavigatorAsync(ToolbarWindow toolbar)
        {
            if (_presenting is null || toolbar.GetCommandTarget(ToolbarCommand.Pages) is not { } anchor) return Task.CompletedTask;
            if (_navigator is { IsShowing: true } && ReferenceEquals(_navigator.Anchor, anchor))
            { _navigator.Close(); _navigator = null; return Task.CompletedTask; }
            CloseToolsMenu(); ClosePenChevron(); _navigator?.Close(); _annotation?.FinishInput();
            var navigator = new SlideNavigatorWindow(_presenting);
            _navigator = navigator;
            navigator.Closed += (_, _) => { if (ReferenceEquals(_navigator, navigator)) _navigator = null; };
            navigator.ShowAt(anchor, _showArea, toolbar.AppWindow.Size.Width);
            return Task.CompletedTask;
        }
        private async Task OpenSpotlightAsync()
        {
            if (_showArea is null || _backend is null) return;
            int version = ++_toolTransitionVersion;
            var backend = _backend;
            var area = _showArea;
            var oldBlackout = _blackout; _blackout = null; oldBlackout?.Close();
            var old = _spotlight; _spotlight = null; old?.Close();
            _annotation?.FinishInput(); await backend.SetToolAsync(null);
            if (version != _toolTransitionVersion || !ReferenceEquals(_backend, backend)) return;
            var spotlight = new SpotlightWindow(area);
            _spotlight = spotlight;
            spotlight.Closed += async (_, _) =>
            {
                if (!ReferenceEquals(_spotlight, spotlight)) return;
                _spotlight = null;
                try { if (version == _toolTransitionVersion && ReferenceEquals(_backend, backend) && _blackout is null) await backend.SetToolAsync(_annotating ? (_erasing ? AnnotationTool.Eraser : AnnotationTool.Pen) : null); RaiseCompanions(); }
                catch (Exception ex) { Log(ex.Message); }
            };
            try { spotlight.Show(); } catch { spotlight.Close(); throw; }
        }

        private void CloseToolsMenu()
        {
            _toolsMenu?.Dismiss();
            _toolsMenu = null;
        }

        private async Task ToggleBlackoutAsync()
        {
            if (_blackout is not null)
            {
                CloseBlackout();
                return;
            }

            var oldSpotlight = _spotlight; _spotlight = null; oldSpotlight?.Close();
            int version = ++_toolTransitionVersion;
            var backend = _backend;
            _annotation?.FinishInput();
            if (backend is not null) await backend.SetToolAsync(null);
            if (version != _toolTransitionVersion || !ReferenceEquals(_backend, backend) || _presenting is null) return;
            var blackout = new BlackoutWindow();
            _blackout = blackout;
            blackout.ExitRequested += (_, _) => CloseBlackout();
            blackout.Closed += async (_, _) =>
            {
                if (!ReferenceEquals(_blackout, blackout)) return;
                _blackout = null;
                try { if (version == _toolTransitionVersion && backend is not null && ReferenceEquals(_backend, backend) && _spotlight is null) await backend.SetToolAsync(_annotating ? (_erasing ? AnnotationTool.Eraser : AnnotationTool.Pen) : null); RaiseCompanions(); }
                catch (Exception ex) { Log(ex.Message); }
            };
            try { blackout.ShowOn(_showArea ?? DisplayArea.Primary); } catch { blackout.Close(); throw; }
        }

        private void CloseBlackout()
        {
            _blackout?.Close();
            _blackout = null;
        }

        private void ToggleTimer(ToolbarWindow anchor)
        {
            if (_timer is not null)
            {
                CloseTimer();
                return;
            }

            var timer = new TimerWindow();
            _timer = timer;
            timer.Closed += (_, _) => { if (ReferenceEquals(_timer, timer)) _timer = null; };
            timer.ShowCentered(_showArea ?? DisplayArea.Primary);
        }

        private void CloseTimer()
        {
            _timer?.Close();
            _timer = null;
        }

        // ———— 墨迹持久化 ————

        private async Task HandleInkLoadAsync(string pptxPath, int currentSlide, DisplayArea area)
        {
            var doc = await InkStore.TryLoadAsync(pptxPath);
            if (doc is null || !doc.Slides.Values.Any(strokes => strokes.Count > 0))
            {
                return;
            }

            Log($"ink archive found: {doc.Slides.Count} slide(s)");

            var result = await ShowPromptAsync(
                area,
                "发现之前保存的墨迹",
                $"是否加载该课件上次保存的批注墨迹？\n（{doc.Slides.Count} 页有墨迹，保存于 {doc.SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}）",
                "加载", "不加载", "删除墨迹");

            switch (result)
            {
                case PromptResult.Primary:
                    foreach (var pair in doc.Slides)
                    {
                        SessionInk[pair.Key] = pair.Value;
                    }
                    _annotation?.SetActiveSlide(currentSlide);
                    Log($"ink loaded: {SessionInk.Count} slide(s)");
                    break;

                case PromptResult.Delete:
                    // 删除档案：下次放映不再询问
                    await InkStore.DeleteAsync(doc.ContentHash);
                    Log("ink archive deleted");
                    break;

                // Secondary：不加载，什么都不做
            }
        }

        private async Task<PromptResult> ShowSavePromptAsync(Dictionary<int, List<StrokeData>> snapshot)
        {
            int pageCount = snapshot.Values.Count(strokes => strokes.Count > 0);
            int strokeCount = snapshot.Values.Sum(strokes => strokes.Count);
            return await ShowPromptAsync(
                null,
                "保存批注墨迹",
                strokeCount == 0
                    ? "本次已擦除全部批注。是否保存空白结果，覆盖该课件之前的墨迹？"
                    : $"本次放映有 {pageCount} 页共 {strokeCount} 笔批注墨迹。\n是否保存到本地，下次放映该课件时恢复？",
                "保存", "不保存", "删除墨迹");
        }

        private async Task<PromptResult> ShowPromptAsync(DisplayArea? area, string title, string message, string primaryText, string secondaryText, string? deleteText = null)
        {
            ClosePrompt();
            _promptWindow = new PromptWindow(title, message, primaryText, secondaryText, deleteText);
            return await _promptWindow.ShowCenteredAsync(area ?? Microsoft.UI.Windowing.DisplayArea.Primary);
        }

        private void ClosePrompt()
        {
            _promptWindow?.Close();
            _promptWindow = null;
        }

        private async Task SaveInkAsync(string pptxPath, Dictionary<int, List<StrokeData>> snapshot)
        {
            await InkStore.SaveAsync(new InkDocument
            {
                ContentHash = await InkStore.HashFileAsync(pptxPath),
                PathHint = pptxPath,
                SavedAtUtc = DateTime.UtcNow,
                Slides = snapshot,
            });
        }

        // ———— 托盘 ————

        private void InitializeTray()
        {
            var settingsCommand = new Microsoft.UI.Xaml.Input.XamlUICommand { Description = "打开设置" };
            settingsCommand.ExecuteRequested += (_, _) =>
            {
                try
                {
                    SettingsWindow.Open();
                }
                catch (Exception ex)
                {
                    CrashReporter.Report(ex, "tray-settings");
                }
            };

            var exitCommand = new Microsoft.UI.Xaml.Input.XamlUICommand { Description = "退出 RimePPT" };
            exitCommand.ExecuteRequested += (_, _) => ExitApplication();

            var menu = new MenuFlyout();
            menu.Items.Add(new MenuFlyoutItem { Command = settingsCommand, Text = "设置" });
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Command = exitCommand, Text = "退出" });

            // 图标缺失只降级为系统图标，绝不能让托盘初始化杀死整个应用
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            _trayIcon = new H.NotifyIcon.TaskbarIcon
            {
                ToolTipText = "RimePPT 放映助手",
                ContextFlyout = menu,
                ContextMenuMode = H.NotifyIcon.ContextMenuMode.SecondWindow,
                Icon = System.IO.File.Exists(iconPath)
                    ? new System.Drawing.Icon(iconPath)
                    : System.Drawing.SystemIcons.Application,
            };
            // 系统长按和键盘菜单键发送 WM_CONTEXTMENU；库只抛出事件，未自动显示菜单。
            // WinUI 版本的菜单入口接收屏幕坐标，复用现有原生菜单窗口。
            var tray = _trayIcon;
            tray.TrayIcon.MessageWindow.KeyboardEventReceived += (_, e) =>
            {
                if (e.KeyboardEvent != H.NotifyIcon.Core.KeyboardEvent.ContextMenu) return;
                _dispatcher?.TryEnqueue(() =>
                {
                    if (!tray.IsDisposed)
                        tray.ShowContextMenu(e.Point);
                });
            };
            _trayIcon.ForceCreate();
        }

        private void ExitApplication()
        {
            _trayIcon?.Dispose();
            Environment.Exit(0);
        }

        public static void ExitForGitHubUpdate() => _instance?.ExitApplication();

        private async Task CheckGitHubUpdatesAtStartupAsync()
        {
            if (!AppSettings.Instance.CheckGitHubUpdatesOnStartup || GitHubUpdateService.Channel == GitHubUpdateChannel.Store) return;
            try
            {
                await Task.Delay(5000);
                using var cancellation = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var release = await new GitHubUpdateService().CheckAsync(cancellation.Token);
                if (release.Version <= GitHubUpdateService.InstalledVersion || release.Asset is null) return;
                _dispatcher?.TryEnqueue(async () =>
                {
                    try
                    {
                        if (PowerPoint.IsPresenting || Wps.IsPresenting || Debug.IsPresenting || WhiteboardWindow.IsOpen) return;
                        var answer = await ShowPromptAsync(DisplayArea.Primary, "发现 GitHub 更新 " + release.Tag,
                            "可以在设置中查看并下载更新。不会自动下载或安装。", "查看更新", "稍后");
                        if (answer == PromptResult.Primary) SettingsWindow.OpenGitHubUpdates();
                    }
                    catch (Exception ex) { CrashReporter.Report(ex, "github-update-notification"); }
                });
            }
            catch (Exception ex) { CrashReporter.Log("GitHub startup check: " + ex.Message); }
        }

        public static void CloseAllToolbars()
        {
            _instance?._onboarding.Stop();
            foreach (var toolbar in ActiveToolbars)
            {
                toolbar.Dismiss();
            }
            ActiveToolbars.Clear();
        }

        /// <summary>
        /// 按当前设置重建工具条（放映进行中改选显示区域时调用）。
        /// 布局集合未变化时直接返回，避免主题/笔色等无关设置变更引起浮窗闪烁。
        /// 只重建工具条，不动批注层；批注态在新按钮上恢复选中与置顶。
        /// </summary>
        private void RebuildToolbars()
        {
            if (WhiteboardWindow.IsOpen) return;
            if (_presenting is null || _showArea is null)
            {
                return;
            }

            var settings = AppSettings.Instance;
            var target = settings.GetEnabledToolbarLayouts().Where(x => settings.GetToolbarCommands(x).Count > 0 || settings.QuickLaunchEntries.Any(e => e.PinnedLayout == x)).ToArray();
            var replaced = ActiveToolbars.Where(t => !target.Contains(t.Layout) ||
                !t.OrderedCommands.SequenceEqual(settings.GetToolbarCommands(t.Layout)) ||
                t.ExtraConfiguration != ToolbarWindow.ExtrasSignature(t.Layout)).ToArray();
            var added = target.Where(x => !ActiveToolbars.Any(t => t.Layout == x && !replaced.Contains(t))).ToArray();
            if (replaced.Length == 0 && added.Length == 0) return;

            CloseToolsMenu(); ClosePenChevron(); _navigator?.Close(); _navigator = null;
            bool resumeGuide = _onboarding.IsRunning;
            _onboarding.Stop();
            foreach (var toolbar in replaced)
            {
                ActiveToolbars.Remove(toolbar);
                toolbar.Dismiss();
            }
            foreach (var layout in added)
            {
                var toolbar = new ToolbarWindow(layout, settings.GetToolbarCommands(layout));
                toolbar.ToolbarClicked += OnToolbarClicked;
                toolbar.ToolSettingsRequested += OpenToolSettings;
                toolbar.ApplySettings();
                toolbar.ShowOn(_showArea);
                ActiveToolbars.Add(toolbar);
                toolbar.BeginEntrance();
            }
            if (_annotating)
            {
                ShowPenChevron();
            }
            SyncAnnotationChecks();
            RaiseCompanions();
            _ = _onboarding.StartAsync(ActiveToolbars.ToArray(), resumeGuide);
        }

        private static readonly object LogLock = new();

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "rimeppt_app.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [pid{Environment.ProcessId}] {message}\r\n");
            }
            catch
            {
                // 日志失败不影响功能
            }
        }
    }
}
