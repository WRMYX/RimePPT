using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace RimePPT.Services;

internal sealed class StoreUpdateService
{
    public const string StoreId = "9MV9Q91FTTFZ";
    private readonly StoreContext _context;

    public static Package? GetInstalledPackage()
    {
        try { return Package.Current; }
        catch (InvalidOperationException) { return null; }
    }

    public static bool SupportsUpdates(Package? package) => package is not null
        && package.Id.Name == "MYXMJY.RimePPT"
        && package.Id.Publisher == "CN=99E0AD8D-459D-4C6D-8850-C7F11B790A9F"
        && package.SignatureKind == PackageSignatureKind.Store;

    public StoreUpdateService(IntPtr windowHandle)
    {
        if (!SupportsUpdates(GetInstalledPackage()))
            throw new InvalidOperationException("请使用 Microsoft Store 安装的 RimePPT 检查更新。");
        _context = StoreContext.GetDefault();
        WinRT.Interop.InitializeWithWindow.Initialize(_context, windowHandle);
    }

    public async Task<IReadOnlyList<StorePackageUpdate>> CheckAsync(CancellationToken cancellationToken)
    {
        // Cancel the underlying WinRT operation as well as the awaiting task.
        return await _context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask(cancellationToken);
    }

    public async Task<StorePackageUpdateResult> InstallAsync(IReadOnlyList<StorePackageUpdate> updates,
        Action<StorePackageUpdateStatus> progress, CancellationToken cancellationToken)
    {
        var operation = _context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
        operation.Progress = (_, status) => progress(status);
        return await operation.AsTask(cancellationToken);
    }
}
