using System;
using System.IO;

namespace RimePPT.Core;

public enum StartupStatus { Disabled, Enabled, DisabledByUser, DisabledByPolicy, EnabledByPolicy, OtherLocation, Error }

public sealed record StartupResult(StartupStatus Status, string Message)
{
    public bool Enabled => Status is StartupStatus.Enabled or StartupStatus.EnabledByPolicy;
    public bool Blocked => Status is StartupStatus.DisabledByUser or StartupStatus.DisabledByPolicy or StartupStatus.EnabledByPolicy;
}

public static class StartupPolicy
{
    public static bool MatchesExecutable(string? command, string? executable)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(executable)) return false;
        string value = command.Trim();
        // Only claim our own registration: one quoted executable, no arguments.
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' || value[1..^1].Contains('"')) return false;
        try { return Path.IsPathFullyQualified(value[1..^1]) && Path.IsPathFullyQualified(executable)
            && string.Equals(Path.GetFullPath(value[1..^1]), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    public static bool ShouldInitialize(bool fresh, bool packaged, bool desired, bool migrated, StartupResult actual)
        => !migrated && !actual.Blocked && actual.Status != StartupStatus.Error
            && (fresh || packaged && desired && actual.Status == StartupStatus.Disabled);
}
