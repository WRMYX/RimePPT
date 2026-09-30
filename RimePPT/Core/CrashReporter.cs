using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace RimePPT.Core
{
    /// <summary>
    /// 全局异常兜底：三级钩子（AppDomain / TaskScheduler / XAML Application），
    /// 一律先写 %TEMP%\rimeppt_app.log（含 PID 与堆栈）再决定呈现方式——
    /// 致命异常用 Win32 MessageBox（不依赖 XAML，进程垂死时仍可显示），
    /// XAML 线程异常尝试 PromptWindow 错误卡（可继续运行），失败降级 MessageBox。
    /// </summary>
    public static class CrashReporter
    {
        private static readonly object LogLock = new();

        public static void Init()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Exception ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown");
                Log($"FATAL(AppDomain) {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}");
                ShowFatalMessageBox(ex);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                // 未观察的任务异常：只记录并吞掉，不弹窗（多为可忽略的竞态尾巴）
                Log($"UNOBSERVED(Task) {e.Exception}");
                e.SetObserved();
            };

            Microsoft.UI.Xaml.Application.Current.UnhandledException += (sender, e) =>
            {
                Exception ex = e.Exception;
                Log($"XAML {ex?.GetType().Name ?? "?"}: {ex?.Message}\r\n{ex?.StackTrace}");
                e.Handled = true;
                ShowErrorCard(ex);
            };
        }

        /// <summary>记录非致命异常（各事件入口的 catch 边界调用）。</summary>
        public static void Report(Exception ex, string context)
        {
            Log($"CAUGHT({context}) {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}");
        }

        /// <summary>写一行日志（与 App/AnnotationWindow 同文件，加 flush 保证垂死前落盘）。</summary>
        public static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    using var writer = new StreamWriter(
                        Path.Combine(Path.GetTempPath(), "rimeppt_app.log"),
                        append: true,
                        Encoding.UTF8);
                    writer.Write($"{DateTime.Now:HH:mm:ss.fff} [pid{Environment.ProcessId}] {message}\r\n");
                    writer.Flush();
                }
            }
            catch
            {
                // 日志失败不影响兜底流程
            }
        }

        private static string LogPath => Path.Combine(Path.GetTempPath(), "rimeppt_app.log");

        // ———— 致命：Win32 原生弹窗（无 XAML 依赖） ————

        private const uint MB_OK = 0x0;
        private const uint MB_ICONERROR = 0x10;
        private const uint MB_TOPMOST = 0x40000;
        private const uint MB_SETFOREGROUND = 0x10000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        private static void ShowFatalMessageBox(Exception ex)
        {
            try
            {
                MessageBoxW(IntPtr.Zero,
                    $"RimePPT 遇到未处理的错误，即将退出。\n\n" +
                    $"{ex.GetType().Name}: {ex.Message}\n\n" +
                    $"详细信息已写入日志：\n{LogPath}",
                    "RimePPT",
                    MB_OK | MB_ICONERROR | MB_TOPMOST | MB_SETFOREGROUND);
            }
            catch
            {
                // 弹窗也失败则无能为力，日志已写
            }
        }

        // ———— XAML 线程：尝试错误卡（可继续），失败降级原生弹窗 ————

        private static void ShowErrorCard(Exception? ex)
        {
            try
            {
                string summary = ex is null
                    ? "未知错误"
                    : $"{ex.GetType().Name}: {Truncate(ex.Message, 160)}";
                var prompt = new RimePPT.Windows.PromptWindow(
                    "RimePPT 遇到错误",
                    $"{summary}\n\n可以选择继续运行；若反复出现请查看日志：\n{LogPath}",
                    "继续",
                    "退出");
                _ = prompt.AfterChoice(result =>
                {
                    if (result == RimePPT.Windows.PromptResult.Secondary)
                    {
                        Environment.Exit(0);
                    }
                });
                // 有意不等待：错误卡自己活在自己的消息循环里，选完由 AfterChoice 处理
                _ = prompt.ShowCenteredAsync(Microsoft.UI.Windowing.DisplayArea.Primary);
            }
            catch
            {
                ShowFatalMessageBox(ex ?? new Exception("unknown"));
            }
        }

        private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? s
            : s.Length <= max ? s
            : s[..max] + "…";
    }

    /// <summary>PromptWindow 选择回调扩展（供 CrashReporter 等非 await 调用方使用）。</summary>
    public static class PromptWindowExtensions
    {
        /// <summary>注册选择回调并返回原窗口（ShowCenteredAsync 之后触发）。</summary>
        public static RimePPT.Windows.PromptWindow AfterChoice(
            this RimePPT.Windows.PromptWindow prompt,
            Action<RimePPT.Windows.PromptResult> onChoice)
        {
            prompt.ChoiceMade += (_, result) => onChoice(result);
            return prompt;
        }
    }
}
