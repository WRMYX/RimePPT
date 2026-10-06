using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RimePPT.Core;

internal enum GitHubUpdateChannel { Portable, Msix, Store }
internal sealed record GitHubAsset(string Name, Uri Url, long Size, string Sha256);
internal sealed record GitHubRelease(Version Version, string Tag, string Notes, DateTimeOffset Published, GitHubAsset? Asset)
{
    public const string Repository = "WRMYX/RimePPT";
    public static Version ParseVersion(string tag)
    {
        if (!Regex.IsMatch(tag, @"^v[1-9][0-9]*\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"))
            throw new InvalidDataException("GitHub 版本标签格式不支持，请使用 v1.2.3 这样的版本。");
        var version = Version.Parse(tag[1..] + ".0");
        if (version.Major > 65535 || version.Minor > 65535 || version.Build > 65535)
            throw new InvalidDataException("GitHub 版本号超出安装包支持范围。");
        return version;
    }

    public static GitHubRelease Parse(string json, GitHubUpdateChannel channel, string architecture)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("此版本尚未正式发布。");
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        string suffix = channel == GitHubUpdateChannel.Portable ? "Portable" : "MSIX-with-certificate";
        string expected = $"RimePPT-{version}-{suffix}-{architecture}.zip";
        var matches = root.GetProperty("assets").EnumerateArray()
            .Where(x => x.GetProperty("name").GetString() == expected).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("发布文件重复，请联系开发者。");
        GitHubAsset? asset = null;
        if (matches.Length == 1 && channel != GitHubUpdateChannel.Store)
        {
            var item = matches[0];
            var uri = new Uri(item.GetProperty("browser_download_url").GetString()!);
            if (uri.Scheme != "https" || uri.Host != "github.com" ||
                !uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/{tag}/", StringComparison.Ordinal))
                throw new InvalidDataException("更新下载地址不属于 RimePPT 发布仓库。");
            string digest = item.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
            if (!Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("该发布文件缺少 SHA-256 校验信息，请在 GitHub 中手动下载。");
            long size = item.GetProperty("size").GetInt64();
            if (size <= 0 || size > 1024L * 1024 * 1024) throw new InvalidDataException("更新文件大小不支持。");
            asset = new(expected, uri, size, digest[7..]);
        }
        return new(version, tag, root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            root.GetProperty("published_at").GetDateTimeOffset(), asset);
    }

    public static string SafeEntryPath(string root, string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || entry.Contains(':') || entry.Contains('\\') ||
            entry.Split('/').Any(x => x is ".." or ".") || Path.IsPathRooted(entry))
            throw new InvalidDataException("更新压缩包包含不安全的路径。");
        string destination = Path.GetFullPath(Path.Combine(root, entry));
        if (!destination.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新文件超出目标目录。");
        return destination;
    }
}
