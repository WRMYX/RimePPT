using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace RimePPT.Updater;
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) return;
        var app = new Application();
        app.Run(new ProgressWindow(Path.GetFullPath(args[0])));
    }
}
internal sealed class ProgressWindow : Window
{
    private readonly TextBlock _stage = new() { FontSize = 23, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ProgressBar _bar = new() { Height = 8, IsIndeterminate = true, Maximum = 100 };
    private readonly TextBlock _percent = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly string _plan;
    private string _status = "", _log = "";
    private Process? _worker;
    private bool _finished;
    public ProgressWindow(string plan)
    {
        _plan = plan; Title = "RimePPT · 安装更新"; Width = 580; Height = 420; MinWidth = 480; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "RimePPT 更新", FontSize = 16, Margin = new Thickness(0,0,0,18) });
        foreach (var control in new FrameworkElement[] { _stage, _bar, _percent, _detail }) { control.Margin = new Thickness(0,0,0,16); panel.Children.Add(control); }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        AddButton(buttons, "复制详情", () => Clipboard.SetText(_detail.Text));
        AddButton(buttons, "打开日志", () => { if (File.Exists(_log)) Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { _log }, UseShellExecute = false }); });
        AddButton(buttons, "关闭", Close); panel.Children.Add(buttons); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Closing += (_, e) => { if (!_finished) { e.Cancel = true; _detail.Text = "正在安装，请等待完成。关闭窗口不会中止文件替换。"; } };
        Loaded += (_, _) => Start();
        _timer.Tick += (_, _) => Poll();
    }
    private static void AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12,6,12,6), Margin = new Thickness(0,0,8,0) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
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
            _stage.Text = "等待 RimePPT 退出";
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
                _stage.Text = stage switch { "waiting" => "等待 RimePPT 退出", "backup" => "备份旧版本", "install" => "安装程序文件", "verify" => "校验安装文件", "restart" => "重新启动 RimePPT", "rollback" => "恢复旧版本", "success" => "更新已完成", "error" => "更新未完成", _ => stage };
                double value = root.GetProperty("Percent").GetDouble(); _bar.IsIndeterminate = value < 0;
                _bar.Value = Math.Clamp(value,0,100); _percent.Text = value < 0 ? "正在处理…" : $"{value:0}%";
                _detail.Text = root.GetProperty("Message").GetString() ?? "";
                if (stage is "success" or "error") { _finished = true; _timer.Stop(); }
            }
            if (!_finished && _worker?.HasExited == true) {
                string result = File.Exists(_log) ? File.ReadAllText(_log) : "安装进程未返回结果，请查看日志后重试。";
                Finish(_worker.ExitCode == 0 && result.TrimStart('\uFEFF').StartsWith("Update succeeded.", StringComparison.Ordinal) ? "更新已完成" : "更新未完成", result);
            }
        } catch (IOException) { } catch (JsonException) { } catch (Exception ex) { Finish("无法读取安装状态", ex.Message); }
    }
    private void Finish(string title, string detail) { _finished = true; _timer.Stop(); _stage.Text = title; _detail.Text = detail; _bar.IsIndeterminate = false; _percent.Text = ""; }
}
