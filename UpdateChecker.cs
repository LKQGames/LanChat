using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanChat;

/// <summary>检查 GitHub Releases 上是否有比当前版本更新的构建。</summary>
internal static class UpdateChecker
{
    private const string DefaultReleasesApi = "https://api.github.com/repos/LKQGames/LanChat/releases?per_page=30";
    private const string ReleasesPage = "https://github.com/LKQGames/LanChat/releases/latest";

    /// <summary>
    /// 仓库是私有时，可用环境变量 LANCHAT_UPDATE_TOKEN 提供只读令牌；
    /// LANCHAT_UPDATE_API 可指向镜像或其他发布源（便于内网分发）。
    /// </summary>
    private static string ReleasesApi =>
        Environment.GetEnvironmentVariable("LANCHAT_UPDATE_API") is { Length: > 0 } custom ? custom : DefaultReleasesApi;

    private static string? UpdateToken => Environment.GetEnvironmentVariable("LANCHAT_UPDATE_TOKEN");

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub API 要求带 User-Agent，否则返回 403。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LanChat-UpdateChecker");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Version CurrentVersion =>
        Normalize(Assembly.GetExecutingAssembly().GetName().Version) ?? new Version(1, 0, 0);

    /// <summary>统一显示成三段式版本号（1.1.10），不显示第四段修订号。</summary>
    public static string Format(Version? version) =>
        version is null ? "未知" : $"{version.Major}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}";

    /// <summary>查询结果：失败时 Error 非空；成功时 LatestVersion/DownloadUrl 有效。</summary>
    internal sealed record Result(Version? LatestVersion, string? Tag, string? DownloadUrl, string? Error)
    {
        public bool Succeeded => Error is null && LatestVersion is not null;
        public bool HasUpdate => Succeeded && LatestVersion > CurrentVersion;
    }

    public static async Task<Result> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
            if (UpdateToken is { Length: > 0 } token) request.Headers.Authorization = new("Bearer", token);
            using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // API 限流（60 次/小时/IP）或临时故障时，退回不消耗配额的发布页重定向。
                AppLog.Write($"发布列表 API 返回 HTTP {(int)response.StatusCode}，尝试发布页兜底。");
                var fallbackVersion = await TryGetLatestTagFromReleasesPageAsync(cancellationToken).ConfigureAwait(false);
                if (fallbackVersion is not null)
                    return new Result(fallbackVersion, $"v{Format(fallbackVersion)}", ReleasesPage, null);
                return new Result(null, null, null, response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden
                    ? $"无法读取发布列表（HTTP {(int)response.StatusCode}），仓库可能未公开或已改名。"
                    : $"GitHub 返回 HTTP {(int)response.StatusCode}。");
            }
            var releases = await response.Content.ReadFromJsonAsync<List<ReleaseInfo>>(cancellationToken).ConfigureAwait(false);
            if (releases is null || releases.Count == 0)
                return new Result(null, null, null, "未获取到任何发布版本。");

            ReleaseInfo? best = null;
            Version? bestVersion = null;
            foreach (var release in releases)
            {
                if (release.Draft || release.Prerelease) continue;
                var version = ParseTag(release.TagName);
                if (version is null) continue;
                if (bestVersion is null || version > bestVersion)
                {
                    bestVersion = version;
                    best = release;
                }
            }
            if (best is null || bestVersion is null)
                return new Result(null, null, null, "发布列表中没有可识别的版本号。");

            var downloadUrl = best.Assets?.FirstOrDefault(asset =>
                    asset.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true &&
                    !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))?.BrowserDownloadUrl
                ?? best.HtmlUrl
                ?? ReleasesPage;
            return new Result(bestVersion, best.TagName, downloadUrl, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException or InvalidOperationException)
        {
            AppLog.Write($"检查更新失败：{ex.GetType().Name}: {ex.Message}");
            try
            {
                var fallbackVersion = await TryGetLatestTagFromReleasesPageAsync(cancellationToken).ConfigureAwait(false);
                if (fallbackVersion is not null)
                    return new Result(fallbackVersion, $"v{Format(fallbackVersion)}", ReleasesPage, null);
            }
            catch (Exception fallbackError) when (fallbackError is HttpRequestException or TaskCanceledException)
            {
                AppLog.Write($"发布页兜底同样失败：{fallbackError.GetType().Name}: {fallbackError.Message}");
            }
            return new Result(null, null, null, ex is TaskCanceledException ? "连接 GitHub 超时。" : "无法连接 GitHub。");
        }
    }

    /// <summary>
    /// 不消耗 API 配额的兜底：读取 /releases/latest 的跳转目标，从中取出标签版本号。
    /// 只用于判断版本；下载时使用发布页地址。
    /// </summary>
    private static async Task<Version?> TryGetLatestTagFromReleasesPageAsync(CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LanChat-UpdateChecker");
        using var response = await client.GetAsync(ReleasesPage, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is not (301 or 302 or 303 or 307 or 308)) return null;
        var location = response.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(location)) return null;
        var index = location.LastIndexOf("/tag/", StringComparison.Ordinal);
        if (index < 0) return null;
        var tag = location[(index + 5)..];
        var version = ParseTag(tag);
        if (version is not null) AppLog.Write($"已通过发布页兜底获取最新版本：{tag}。");
        return version;
    }

    /// <summary>把 v1.2.3 之类的标签转成可比较的版本号。</summary>
    internal static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var separator = text.IndexOfAny(['-', '+', ' ']);
        if (separator >= 0) text = text[..separator];
        return Normalize(System.Version.TryParse(text, out var version) ? version : null);
    }

    private static Version? Normalize(Version? version)
    {
        if (version is null) return null;
        var major = Math.Max(0, version.Major);
        var minor = Math.Max(0, version.Minor);
        var build = version.Build < 0 ? 0 : version.Build;
        var revision = version.Revision < 0 ? 0 : version.Revision;
        return new Version(major, minor, build, revision);
    }

    /// <summary>用系统默认浏览器打开下载页。</summary>
    public static bool OpenDownloadPage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            AppLog.Write($"已在浏览器中打开更新下载页：{url}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            AppLog.Write($"打开更新下载页失败：{ex.Message}");
            return false;
        }
    }

    private sealed class ReleaseInfo
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<ReleaseAsset>? Assets { get; set; }
    }

    private sealed class ReleaseAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
