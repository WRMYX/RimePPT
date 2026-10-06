using System;
namespace RimePPT.Core;
public static class GitHubDownloadSource
{
    public static string NormalizePrefix(string prefix)
    {
        if (!Uri.TryCreate(prefix.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("加速地址必须是无账号、查询参数和片段的 HTTPS 地址。");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }
    public static string Resolve(string original, string source, string? custom = null)
    {
        if (!Uri.TryCreate(original, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/" + GitHubRelease.Repository + "/releases/download/", StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("只能加速本项目的 GitHub 发布附件。");
        return source switch {
            "ghproxy" => "https://gh-proxy.org/" + original,
            "custom" => NormalizePrefix(custom ?? "") + original,
            _ => original
        };
    }
}
