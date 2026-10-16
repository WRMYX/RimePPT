namespace RimePPT.Core;

// Route shared controller errors to the standalone probe's diagnostic stream.
internal static class CrashReporter
{
    internal static void Log(string message) => System.Console.Error.WriteLine(message);
    internal static void Report(System.Exception error, string context) =>
        Log($"{context}: {error}");
}
