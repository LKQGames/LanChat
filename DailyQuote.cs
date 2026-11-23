using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace LanChat;

/// <summary>每日一言：从 uapis.cn 获取一句随机句子，失败时返回 null（界面保持占位文案）。</summary>
internal static class DailyQuote
{
    private const string Endpoint = "https://uapis.cn/api/v1/saying";
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LanChat");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private sealed class Payload
    {
        public string? Text { get; set; }
    }

    public static async Task<string?> FetchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Client.GetAsync(Endpoint, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Write($"获取每日一言失败：HTTP {(int)response.StatusCode}。");
                return null;
            }
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var payload = await response.Content.ReadFromJsonAsync<Payload>(options, cancellationToken).ConfigureAwait(false);
            var text = payload?.Text?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException or InvalidOperationException)
        {
            AppLog.Write($"获取每日一言失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
