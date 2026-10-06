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
    public ProgressWindow(string plan)
    {
        InitializeComponent(); _plan = plan; Title = "RimePPT · 安装更新";
        ExtendsContentIntoTitleBar = true; SetTitleBar(TitleBar); SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(600, 480));
        var area = DisplayArea.Primary.WorkArea;
        AppWindow.Move(new global::Windows.Graphics.PointInt32(area.X + (area.Width - 600)/2, area.Y + (area.Height - 480)/2));
        Root.Loaded += (_, _) => Start(); _timer.Tick += (_, _) => Poll();
        AppWindow.Closing += (_, args) => { if (!_finished) { args.Cancel = true; DetailText.Text = "正在安装，请等待完成。"; } };
        Closed += (_, _) => { _timer.Stop(); _worker?.Dispose(); };
    }
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
    private void OnClose(object sender, RoutedEventArgs args) { if (_finished) Close(); }
    private void Start()
    {
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
        try {
            if (File.Exists(_status)) {
                using var doc = JsonDocument.Parse(File.ReadAllText(_status));
                var root = doc.RootElement; string stage = root.GetProperty("Stage").GetString()!;
                StageText.Text = stage switch { "waiting" => "等待 RimePPT 退出", "backup" => "备份旧版本", "install" => "安装程序文件", "verify" => "校验安装文件", "restart" => "重新启动 RimePPT", "rollback" => "恢复旧版本", "success" => "更新已完成", "error" => "更新未完成", _ => stage };
                double value = root.GetProperty("Percent").GetDouble(); InstallProgress.IsIndeterminate = value < 0;
                InstallProgress.Value = Math.Clamp(value,0,100); PercentText.Text = value < 0 ? "正在处理…" : $"{value:0}%";
                DetailText.Text = root.GetProperty("Message").GetString() ?? "";
                if (stage is "success" or "error") { _finished = true; CloseButton.IsEnabled = true; _timer.Stop(); }
            }
            if (!_finished && _worker?.HasExited == true) {
                string result = File.Exists(_log) ? File.ReadAllText(_log) : "安装进程未返回结果，请查看日志后重试。";
                Finish(_worker.ExitCode == 0 && result.TrimStart('\uFEFF').StartsWith("Update succeeded.", StringComparison.Ordinal) ? "更新已完成" : "更新未完成", result);
            }
        } catch (IOException) { } catch (JsonException) { } catch (Exception ex) { Finish("无法读取安装状态", ex.Message); }
    }
    private void Finish(string title, string detail) { _finished = true; CloseButton.IsEnabled = true; _timer.Stop(); StageText.Text = title; DetailText.Text = detail; InstallProgress.IsIndeterminate = false; PercentText.Text = ""; }
}
