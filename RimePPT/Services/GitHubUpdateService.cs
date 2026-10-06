using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimePPT.Core;

namespace RimePPT.Services;

internal sealed class GitHubUpdateService
{
    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client;
    private readonly string _downloadRoot;
    internal GitHubUpdateService(HttpClient? client = null, string? downloadRoot = null)
    {
        _client = client ?? SharedClient;
        _downloadRoot = downloadRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimePPT", "Updates");
    }
    public static GitHubUpdateChannel Channel => StoreUpdateService.GetInstalledPackage() is { } package
        ? StoreUpdateService.SupportsUpdates(package) ? GitHubUpdateChannel.Store : GitHubUpdateChannel.Msix
        : GitHubUpdateChannel.Portable;
    public static Version InstalledVersion
    {
        get
        {
            if (StoreUpdateService.GetInstalledPackage() is { } package)
            {
                var v = package.Id.Version;
                return new(v.Major, v.Minor, v.Build, v.Revision);
            }
            return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        }
    }
    public async Task<GitHubRelease> CheckAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{GitHubRelease.Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("RimePPT/" + InstalledVersion);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _client.SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("暂未找到公开的正式版本，请确认仓库已公开且 Release 已发布。");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("GitHub 暂时限制了请求，请稍后重试。");
        response.EnsureSuccessStatusCode();
        return GitHubRelease.Parse(await response.Content.ReadAsStringAsync(timeout.Token), Channel,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
    }

    public async Task<string> DownloadAsync(GitHubAsset asset, IProgress<double> progress, CancellationToken token, string sourceId = "direct", string? customPrefix = null)
    {
        string downloadUrl = GitHubDownloadSource.Resolve(asset.Url.AbsoluteUri, sourceId, customPrefix);
        string folder = Path.Combine(_downloadRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "download.zip");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            request.Headers.UserAgent.ParseAdd("RimePPT/" + InstalledVersion);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[128 * 1024]; long total = 0; int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += read;
                    if (total > asset.Size) throw new InvalidDataException("下载文件大小与发布信息不一致。");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    progress.Report(total * 100.0 / asset.Size);
                }
                if (total != asset.Size) throw new InvalidDataException("更新文件下载不完整。");
            }
            using var input = File.OpenRead(file);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
            if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新文件 SHA-256 校验失败，已停止更新。");
            return file;
        }
        catch { File.Delete(file); throw; }
    }

    public static string Extract(string archivePath, GitHubUpdateChannel channel, CancellationToken token = default)
    {
        string root = Path.Combine(Path.GetDirectoryName(archivePath)!, "payload");
        Directory.CreateDirectory(root);
        using var archive = ZipFile.OpenRead(archivePath);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            total += entry.Length;
            if (total > 3L * 1024 * 1024 * 1024 || archive.Entries.Count > 10000)
                throw new InvalidDataException("更新压缩包解压大小不支持。");
            string target = GitHubRelease.SafeEntryPath(root, entry.FullName);
            if ((entry.ExternalAttributes & 0x400) != 0 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("更新压缩包不能包含链接。");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
        if (channel == GitHubUpdateChannel.Portable)
        {
            root = Path.Combine(root, "RimePPT");
            foreach (string file in new[] { "RimePPT.exe", "RimePPT.dll", "coreclr.dll", "Microsoft.UI.Xaml.dll", "RimePPT.pri", "Assets/icon.ico" })
                if (!File.Exists(Path.Combine(root, file))) throw new InvalidDataException("更新包缺少必要程序文件。");
        }
        return root;
    }
}
