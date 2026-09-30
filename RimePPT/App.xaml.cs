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
        private readonly List<PenChevronWindow> _penChevrons = new();
        private H.NotifyIcon.TaskbarIcon? _trayIcon;

        /// <summary>当前存活的工具条浮窗集合。</summary>
        public static List<ToolbarWindow> ActiveToolbars { get; } = new();

        /// <summary>本次放映会话的墨迹（页码 → 笔画）。</summary>
        public static Dictionary<int, List<StrokeData>> SessionInk { get; } = new();

        /// <summary>真实 PowerPoint 控制器（应用启动即开始轮询）。</summary>
        public static PowerPointController PowerPoint { get; } = new();

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

        private static readonly ToolbarCommand[] DefaultCommands =
        {
            ToolbarCommand.Prev,
            ToolbarCommand.Next,
            ToolbarCommand.Annotate,
            ToolbarCommand.Eraser,
            ToolbarCommand.Tools,
            ToolbarCommand.ExitShow,
        };

        public App()
        {
            InitializeComponent();
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
            AppSettings.Load();
            AppSettings.SettingsChanged += (_, _) => _dispatcher?.TryEnqueue(() =>
            {
                try
                {
                    foreach (var toolbar in ActiveToolbars)
                    {
                        toolbar.ApplySettings();
                    }
                    foreach (var chevron in _penChevrons)
                    {
                        chevron.Show(); // 重刷主题配色（Show 内部会 ApplyTheme）
                    }
                    RebuildToolbars(); // 布局集合变化时重建，未变化时内部空转
                }
                catch (Exception ex)
                {
                    CrashReporter.Report(ex, "settings-changed");
                }
            });

            PowerPoint.ShowStarted += OnShowStarted;
            PowerPoint.ShowEnded += OnShowEnded;
            PowerPoint.SlideChanged += OnSlideChanged;
            Debug.ShowStarted += OnShowStarted;
            Debug.ShowEnded += OnShowEnded;
            Debug.SlideChanged += OnSlideChanged;
            PowerPoint.Start();

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

                foreach (var layout in layouts)
                {
                    var toolbar = new ToolbarWindow(layout, DefaultCommands);
                    toolbar.ToolbarClicked += OnToolbarClicked;
                    toolbar.ApplySettings();
                    toolbar.ShowOn(area);
                    ActiveToolbars.Add(toolbar);
                }

                // 批注层最后创建（创建顺序影响置顶带内的输入路由稳定性）；
                // 书写时批注层会盖住工具条，笔画结束（松开）后 App 会把工具条重提上来
                _annotation = new AnnotationWindow(SessionInk);
                _annotation.InkChanged += (_, _) => _inkDirty = true;
                _annotation.CompanionsRaise = RaiseCompanions;
                _annotation.ShowOn(area);

                // 墨迹持久化：放映开始时询问是否加载之前保存的墨迹
                var path = _presenting.ShowFilePath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    await HandleInkLoadAsync(path, _presenting.CurrentSlide, area);
                }
        }

        private void OnSlideChanged(object? sender, int slide)
        {
            Log($"slide changed -> {slide}");
            _dispatcher?.TryEnqueue(() =>
            {
                try
                {
                    _annotation?.SetActiveSlide(slide);
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

            CloseAllToolbars();
            CloseAnnotation();
            ClosePenChevron();
            CloseToolsMenu();
            CloseBlackout();
            CloseTimer();
            ClosePrompt(); // 退出放映时关闭仍在等待的加载询问窗

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
        }

        // ———— 工具条命令 ————

        private async void OnToolbarClicked(object? sender, ToolbarCommand command)
        {
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
                        ToggleAnnotate();
                        break;
                    case ToolbarCommand.Eraser:
                        ToggleEraser();
                        break;
                    case ToolbarCommand.Tools:
                        ToggleToolsMenu((ToolbarWindow)sender!);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"toolbar command {command} failed: {ex.Message}");
            }
        }

        // ———— 批注开关 ————

        private void ToggleAnnotate()
        {
            if (_annotating && !_erasing)
            {
                _annotating = false;
                if (_annotation is not null)
                {
                    _annotation.AnnotationEnabled = false;
                }
                ClosePenChevron(); // 取消批注：箭头窗必须一起撤掉
                SyncAnnotationChecks();
                return;
            }

            _annotating = true;
            _erasing = false;
            EnableAnnotation(AnnotationTool.Pen);
            ShowPenChevron();
            SyncAnnotationChecks();
        }

        private void ToggleEraser()
        {
            if (_annotating && _erasing)
            {
                _annotating = false;
                _erasing = false;
                if (_annotation is not null)
                {
                    _annotation.AnnotationEnabled = false;
                }
                ClosePenChevron();
                SyncAnnotationChecks();
                return;
            }

            _annotating = true;
            _erasing = true;
            EnableAnnotation(AnnotationTool.Eraser);
            ShowPenChevron();
            SyncAnnotationChecks();
        }

        private void ShowPenChevron()
        {
            // 先撤旧的再建：批注/橡皮互切或反复开关时不叠窗
            ClosePenChevron();

            // 每个侧栏各一个箭头窗（左右镜像，指向幻灯片方向）
            foreach (var rail in ActiveToolbars)
            {
                if (rail.Layout != ToolbarLayout.LeftRail && rail.Layout != ToolbarLayout.RightRail)
                {
                    continue;
                }

                var (anchor, pointRight) = rail.GetPenAnchor();
                var chevron = new PenChevronWindow(pointRight);
                chevron.PositionAt((int)anchor.X, (int)anchor.Y, pointRight);
                _penChevrons.Add(chevron);
            }

            foreach (var chevron in _penChevrons)
            {
                chevron.Show();
            }
        }

        private void ClosePenChevron()
        {
            foreach (var chevron in _penChevrons)
            {
                chevron.Close();
            }
            _penChevrons.Clear();
        }

        private void RaiseCompanions()
        {
            foreach (var toolbar in ActiveToolbars)
            {
                WindowPlumbing.RaiseToTopmost(toolbar);
            }
            foreach (var chevron in _penChevrons)
            {
                chevron.Raise();
            }
        }

        private void EnableAnnotation(AnnotationTool tool)
        {
            if (_annotation is null)
            {
                return;
            }

            _annotation.Tool = tool;
            // 注意：SetClickThrough 修改扩展样式会把批注层重新插到置顶带里，
            // 且实测插入位置不稳定（可能压住某一侧工具条）——
            // 启用后必须显式把全部工具条重提到批注层之上，否则批注状态下
            // 点击上一页/下一页会被全屏批注层吃掉（表现为"点了没反应"）
            _annotation.AnnotationEnabled = true;
            foreach (var toolbar in ActiveToolbars)
            {
                WindowPlumbing.RaiseToTopmost(toolbar);
            }
        }

        private void SyncAnnotationChecks()
        {
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

            var (toolAnchor, pointRight) = anchor.GetButtonAnchor(ToolbarCommand.Tools);
            _toolsMenu = new ToolsMenuWindow(new List<(string, string, Action)>
            {
                ("\uE708", "黑屏模式", () => { CloseToolsMenu(); ToggleBlackout(); }),
                ("\uE916", "计时器", () => { CloseToolsMenu(); ToggleTimer(anchor); }),
                ("\uE713", "设置", () => { CloseToolsMenu(); SettingsWindow.Open(); }),
            });
            if (!_toolsMenu.ShowAt((int)toolAnchor.X, (int)toolAnchor.Y, pointRight))
            {
                _toolsMenu = null;
            }
        }

        private void CloseToolsMenu()
        {
            _toolsMenu?.Dismiss();
            _toolsMenu = null;
        }

        private void ToggleBlackout()
        {
            if (_blackout is not null)
            {
                CloseBlackout();
                return;
            }

            _blackout = new BlackoutWindow();
            _blackout.ExitRequested += (_, _) => CloseBlackout();
            _blackout.ShowOn(_showArea ?? Microsoft.UI.Windowing.DisplayArea.Primary);
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

            var (toolAnchor, pointRight) = anchor.GetButtonAnchor(ToolbarCommand.Tools);
            _timer = new TimerWindow();
            if (!_timer.ShowAt((int)toolAnchor.X, (int)toolAnchor.Y, pointRight))
            {
                _timer = null;
            }
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
            _trayIcon.ForceCreate();
        }

        private void ExitApplication()
        {
            _trayIcon?.Dispose();
            Environment.Exit(0);
        }

        public static void CloseAllToolbars()
        {
            foreach (var toolbar in ActiveToolbars)
            {
                toolbar.Close();
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
            if (_presenting is null || _showArea is null)
            {
                return;
            }

            var target = AppSettings.Instance.GetEnabledToolbarLayouts();
            var current = new HashSet<ToolbarLayout>(ActiveToolbars.Select(t => t.Layout));
            if (current.SetEquals(target))
            {
                return;
            }

            CloseAllToolbars();
            foreach (var layout in target)
            {
                var toolbar = new ToolbarWindow(layout, DefaultCommands);
                toolbar.ToolbarClicked += OnToolbarClicked;
                toolbar.ApplySettings();
                toolbar.ShowOn(_showArea);
                ActiveToolbars.Add(toolbar);
            }

            if (_annotating)
            {
                ShowPenChevron();
            }
            SyncAnnotationChecks();
            RaiseCompanions();
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
