using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;

namespace LanChat;

/// <summary>
/// 检查 GitHub 上是否有比当前版本更新的构建。
/// 只读取发布页 /releases/latest 的跳转地址来获取最新标签，
/// 不调用 GitHub API（避免未认证配额限制与令牌依赖）。
/// </summary>
internal static class UpdateChecker
{
    private const string ReleasesPage = "https://github.com/LKQGames/LanChat/releases/latest";
    private const string DownloadTemplate = "https://github.com/LKQGames/LanChat/releases/download/{0}/LanChat.exe";

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        // 关闭自动跳转，直接读取 302 的 Location 得到最新标签。
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LanChat-UpdateChecker");
        return client;
    }

    public static Version CurrentVersion =>
        Normalize(Assembly.GetExecutingAssembly().GetName().Version) ?? new Version(1, 0, 0);

    /// <summary>统一显示成三段式版本号（1.2.0），不显示第四段修订号。</summary>
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
            using var response = await Client.GetAsync(ReleasesPage, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308))
            {
                AppLog.Write($"检查更新失败：发布页返回 HTTP {status}。");
                return new Result(null, null, null, status == 404
                    ? "发布页不存在（HTTP 404），仓库可能未公开或已改名。"
                    : $"发布页返回 HTTP {status}。");
            }

            var location = response.Headers.Location?.ToString();
            var index = location?.LastIndexOf("/tag/", StringComparison.Ordinal) ?? -1;
            if (location is null || index < 0)
            {
                AppLog.Write($"检查更新失败：发布页跳转地址异常（{location ?? "空"}）。");
                return new Result(null, null, null, "未能从发布页解析出最新版本。");
            }

            var tag = location[(index + 5)..].Trim('/');
            var version = ParseTag(tag);
            if (version is null)
            {
                AppLog.Write($"检查更新失败：最新标签“{tag}”无法解析为版本号。");
                return new Result(null, null, null, $"最新标签“{tag}”无法解析为版本号。");
            }

            AppLog.Write($"已从发布页获取最新版本：{tag}。");
            return new Result(version, tag, string.Format(DownloadTemplate, tag), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException or InvalidOperationException)
        {
            AppLog.Write($"检查更新失败：{ex.GetType().Name}: {ex.Message}");
            return new Result(null, null, null, ex is TaskCanceledException ? "连接 GitHub 超时。" : "无法连接 GitHub。");
        }
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

    /// <summary>用系统默认浏览器打开下载地址。</summary>
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
}
