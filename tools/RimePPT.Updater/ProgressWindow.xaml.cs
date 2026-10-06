using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
namespace RimePPT.Updater;
public sealed partial class ProgressWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly string _plan;
    private string _status = "", _log = "";
    private Process? _worker;
    private bool _finished;
    private bool _closed;
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly string? _preview;
    private int _previewTick;
    public ProgressWindow(string plan, string? preview = null)
    {
        InitializeComponent(); _plan = plan; _preview = preview; Title = preview is null ? "RimePPT · 安装更新" : "RimePPT · 更新进度测试";
        ExtendsContentIntoTitleBar = true; SetTitleBar(TitleBar); SystemBackdrop = new MicaBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false; presenter.IsResizable = false; presenter.IsMinimizable = true;
            presenter.SetBorderAndTitleBar(true, false);
        }
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); if (!_closed) Close(); };
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(600, 480));
        var area = DisplayArea.Primary.WorkArea;
        AppWindow.Move(new global::Windows.Graphics.PointInt32(area.X + (area.Width - 600)/2, area.Y + (area.Height - 480)/2));
        Root.Loaded += (_, _) => Start(); _timer.Tick += (_, _) => Poll();
        AppWindow.Closing += (_, args) => { if (!_finished && _preview is null) { args.Cancel = true; DetailText.Text = "正在安装，请等待完成。"; } };
        Closed += (_, _) => { _closed = true; _timer.Stop(); _closeTimer.Stop(); _worker?.Dispose(); };
    }
    private void OnMinimize(object sender, RoutedEventArgs args) { (AppWindow.Presenter as OverlappedPresenter)?.Minimize(); }
    private void OnCopy(object sender, RoutedEventArgs args)
    {
        try { var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage(); package.SetText(DetailText.Text); global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package); }
        catch (Exception ex) { DetailText.Text = "无法复制详情：" + ex.Message; }
    }
    private void OnLog(object sender, RoutedEventArgs args)
    {
        try { if (File.Exists(_log)) Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { _log }, UseShellExecute = false }); }
        catch (Exception ex) { DetailText.Text = "无法打开日志：" + ex.Message; }
    }
    private void OnClose(object sender, RoutedEventArgs args) { if (_finished || _preview is not null) Close(); }
    private void Start()
    {
        if (_preview is not null)
        {
            StageText.Text = "测试预览 · 等待安装";
            DetailText.Text = "模拟更新流程，不下载、不安装，也不会退出 RimePPT。";
            CloseButton.IsEnabled = true;
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimePPT", "Updates", "Preview");
                Directory.CreateDirectory(folder);
                _log = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".log");
                File.WriteAllText(_log, DetailText.Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text += "\n测试日志不可用：" + ex.Message; }
            _timer.Start();
            return;
        }
        try {
            using var data = JsonDocument.Parse(File.ReadAllText(_plan));
            _status = data.RootElement.GetProperty("StatusPath").GetString()!;
            _log = data.RootElement.GetProperty("ResultPath").GetString()!;
            Directory.CreateDirectory(Path.GetDirectoryName(_log)!);
            File.WriteAllText(_log, "Preparing installation.");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Path.GetDirectoryName(_plan)!, "Update-Portable.ps1"), "-Plan", _plan }) start.ArgumentList.Add(arg);
            _worker = Process.Start(start) ?? throw new IOException("无法启动安装进程。");
            StageText.Text = "等待 RimePPT 退出";
            File.WriteAllText(data.RootElement.GetProperty("ReadyPath").GetString()!, "ready");
            _timer.Start();
        } catch (Exception ex) {
            try { if (!string.IsNullOrEmpty(_log)) File.WriteAllText(_log, ex.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Finish("无法开始安装", ex.Message);
        }
    }
    private void Poll()
    {
        if (_preview is not null) { PollPreview(); return; }
        try {
            if (File.Exists(_status)) {
                using var doc = JsonDocument.Parse(File.ReadAllText(_status));
                var root = doc.RootElement; string stage = root.GetProperty("Stage").GetString()!;
                StageText.Text = stage switch { "waiting" => "等待 RimePPT 退出", "backup" => "备份旧版本", "install" => "安装程序文件", "verify" => "校验安装文件", "restart" => "重新启动 RimePPT", "rollback" => "恢复旧版本", "success" => "更新已完成", "error" => "更新未完成", _ => stage };
                double value = root.GetProperty("Percent").GetDouble(); InstallProgress.IsIndeterminate = value < 0;
                InstallProgress.Value = Math.Clamp(value,0,100); PercentText.Text = value < 0 ? "正在处理…" : $"{value:0}%";
                DetailText.Text = root.GetProperty("Message").GetString() ?? "";
                if (stage is "success" or "error") Finish(StageText.Text, DetailText.Text, stage == "success");
            }
            if (!_finished && _worker?.HasExited == true) {
                string result = File.Exists(_log) ? File.ReadAllText(_log) : "安装进程未返回结果，请查看日志后重试。";
                bool succeeded = _worker.ExitCode == 0 && result.TrimStart('\uFEFF').StartsWith("Update succeeded.", StringComparison.Ordinal);
                Finish(succeeded ? "更新已完成" : "更新未完成", result, succeeded);
            }
        } catch (IOException) { } catch (JsonException) { } catch (Exception ex) { Finish("无法读取安装状态", ex.Message); }
    }
    private void PollPreview()
    {
        int tick = ++_previewTick;
        if (tick < 5) return;
        double percent = Math.Min(100, (tick - 5) * 4);
        InstallProgress.IsIndeterminate = false;
        InstallProgress.Value = percent;
        PercentText.Text = $"{percent:0}%";
        string stage = percent < 25 ? "备份旧版本" : percent < 70 ? "安装程序文件" : percent < 90 ? "校验安装文件" : "重新启动 RimePPT";
        if (_preview == "error" && percent >= 70) stage = "恢复旧版本";
        StageText.Text = "测试预览 · " + stage;
        DetailText.Text = "这是模拟进度，未修改任何安装文件。\n" + (_preview == "error" && percent >= 70 ? "模拟校验失败，正在演示恢复旧版本。" : "正在演示：" + stage);
        if (percent < 100) return;
        Finish(_preview == "error" ? "测试预览 · 更新未完成" : "测试预览 · 更新已完成",
            _preview == "error" ? "模拟错误：安装文件校验失败。\n已模拟恢复旧版本，请重新下载后重试。\n本次测试没有执行真实安装。" : "模拟更新成功。\n本次测试没有下载、安装或重新启动 RimePPT。", _preview == "success");
        try { if (!string.IsNullOrEmpty(_log)) File.AppendAllText(_log, "\n" + DetailText.Text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text += "\n测试日志写入失败：" + ex.Message; }
    }
    private void Finish(string title, string detail, bool succeeded = false)
    {
        if (_closed || _finished) return;
        _finished = true; CloseButton.IsEnabled = true; _timer.Stop(); _closeTimer.Stop();
        StageText.Text = title; DetailText.Text = detail; InstallProgress.IsIndeterminate = false; PercentText.Text = "";
        if (succeeded)
        {
            InstallProgress.Value = 100; PercentText.Text = "100% · 窗口将在 3 秒后自动关闭";
            _closeTimer.Start();
        }
    }
}
