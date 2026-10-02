namespace RimePPT.Core;
internal static class CrashReporter
{
    internal static void Report(System.Exception error, string context) => System.Console.Error.WriteLine(context + ": " + error.Message);
}
