using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RimePPT.Core;

namespace RimePPT;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        StartupDiagnostics.Mark("managed-entry");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        StartupDiagnostics.Mark("com-wrappers-ready");
        Application.Start(parameters =>
        {
            StartupDiagnostics.Mark("xaml-start-callback");
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
            StartupDiagnostics.Mark("app-constructed");
        });
    }
}
