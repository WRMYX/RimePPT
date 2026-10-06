using Microsoft.UI.Xaml;
namespace RimePPT.Updater;
public partial class App : Application
{
    private ProgressWindow? _window;
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length != 2) { Exit(); return; }
        _window = arguments[1] switch
        {
            "--preview-success" => new ProgressWindow("", "success"),
            "--preview-error" => new ProgressWindow("", "error"),
            _ => new ProgressWindow(System.IO.Path.GetFullPath(arguments[1]))
        };
        _window.Activate();
    }
}
