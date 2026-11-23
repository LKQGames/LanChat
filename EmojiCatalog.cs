using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace LanChat;

/// <summary>
/// 内置表情目录：使用 Google（Noto）emoji 贴图，打包在 Assets/Emoji 中。
/// 贴图为一张精灵图，按索引裁剪出每个表情。
/// </summary>
internal static class EmojiCatalog
{
    public sealed record EmojiItem(string Emoji, string Name, string Category, BitmapSource Image);

    private sealed class IndexFile
    {
        public int Tile { get; set; }
        public int Columns { get; set; }
        public List<Entry> Items { get; set; } = new();
    }

    private sealed class Entry
    {
        public string Category { get; set; } = string.Empty;
        public string Emoji { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    private static readonly Lazy<IReadOnlyList<EmojiItem>> Cached = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<EmojiItem> Items => Cached.Value;

    private static readonly Lazy<Dictionary<char, List<EmojiItem>>> ByFirstChar = new(() =>
    {
        var map = new Dictionary<char, List<EmojiItem>>();
        foreach (var item in Items)
        {
            if (item.Emoji.Length == 0) continue;
            if (!map.TryGetValue(item.Emoji[0], out var list)) map[item.Emoji[0]] = list = new List<EmojiItem>();
            list.Add(item);
        }
        foreach (var list in map.Values) list.Sort((a, b) => b.Emoji.Length.CompareTo(a.Emoji.Length));
        return map;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>从 <paramref name="index"/> 开始按最长匹配查找内置 emoji。</summary>
    public static bool TryMatch(string text, int index, out int length, out BitmapSource? image)
    {
        length = 0;
        image = null;
        if (index < 0 || index >= text.Length) return false;
        if (!ByFirstChar.Value.TryGetValue(text[index], out var candidates)) return false;
        foreach (var item in candidates)
        {
            if (item.Emoji.Length > text.Length - index) continue;
            if (string.CompareOrdinal(text, index, item.Emoji, 0, item.Emoji.Length) != 0) continue;
            length = item.Emoji.Length;
            image = item.Image;
            return true;
        }
        return false;
    }

    /// <summary>分类顺序（与贴图生成时一致）。</summary>
    public static IReadOnlyList<string> Categories { get; } = new[] { "表情", "人物", "动物", "食物", "出行", "活动", "物品", "符号" };

    private static Uri ResourceUri(string path) =>
        new($"pack://application:,,,/{typeof(EmojiCatalog).Assembly.GetName().Name};component/{path}", UriKind.Absolute);

    private static IReadOnlyList<EmojiItem> Load()
    {
        var result = new List<EmojiItem>();
        try
        {
            var indexStream = Application.GetResourceStream(ResourceUri("Assets/Emoji/index.json"));
            if (indexStream is null) return result;
            IndexFile? index;
            // 资源里的 JSON 使用 camelCase，需忽略大小写。
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            using (var reader = new StreamReader(indexStream.Stream))
                index = JsonSerializer.Deserialize<IndexFile>(reader.ReadToEnd(), options);
            if (index is null || index.Items.Count == 0) return result;

            var sheet = new BitmapImage();
            sheet.BeginInit();
            sheet.CacheOption = BitmapCacheOption.OnLoad;
            var sheetStream = Application.GetResourceStream(ResourceUri("Assets/Emoji/sprites.png"));
            if (sheetStream is null) return result;
            sheet.StreamSource = sheetStream.Stream;
            sheet.EndInit();
            sheet.Freeze();

            var tile = index.Tile <= 0 ? 26 : index.Tile;
            var columns = Math.Max(1, index.Columns);
            for (var i = 0; i < index.Items.Count; i++)
            {
                var entry = index.Items[i];
                var x = (i % columns) * tile;
                var y = (i / columns) * tile;
                if (x + tile > sheet.PixelWidth || y + tile > sheet.PixelHeight) break;
                var crop = new CroppedBitmap(sheet, new Int32Rect(x, y, tile, tile));
                crop.Freeze();
                result.Add(new EmojiItem(entry.Emoji, entry.Name, entry.Category, crop));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException or ArgumentException)
        {
            AppLog.Write($"加载内置表情失败：{ex.Message}");
        }
        return result;
    }
}
