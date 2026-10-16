namespace RimePPT.Core;

// The standalone probe writes controller diagnostics to its console, without a WinUI host.
internal static class PresentationDiagnostics
{
    internal static void Record(string stage, string detail) =>
        System.Console.Error.WriteLine($"{System.DateTimeOffset.Now:HH:mm:ss.fff} {stage}: {detail}");
}
