namespace RimePPT.Services;
// The pure test executable has no package identity or Windows Store runtime.
internal static class StoreUpdateService
{
    internal sealed class PackageStub { public IdStub Id { get; } = new(); }
    internal sealed class IdStub { public VersionStub Version { get; } = new(); }
    internal sealed class VersionStub { public int Major => 1; public int Minor => 0; public int Build => 0; public int Revision => 0; }
    public static PackageStub? GetInstalledPackage() => null;
    public static bool SupportsUpdates(PackageStub package) => false;
}
