using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace LanChat;

/// <summary>
/// 富文本渲染：把消息里的 @用户名 渲染成蓝色，把 emoji 渲染成内置的 Google emoji 贴图，
/// 其余保持普通文本。
/// </summary>
public sealed class MentionTextBlock : TextBlock
{
    private static readonly Regex MentionPattern = new(@"@[\p{L}\p{N}_\-\.]+", RegexOptions.Compiled);
    private static readonly Brush DefaultMentionBrush = CreateBrush();
    private static readonly HashSet<string> KnownNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object KnownLock = new();

    /// <summary>只有房间内存在的用户名才高亮。</summary>
    public static void SetKnownNames(IEnumerable<string> names)
    {
        lock (KnownLock)
        {
            KnownNames.Clear();
            foreach (var name in names)
                if (!string.IsNullOrWhiteSpace(name)) KnownNames.Add(name);
        }
    }

    /// <summary>@全体成员 始终高亮，即使没有同名成员。</summary>
    private const string AllMention = "全体成员";

    private static bool IsKnownName(string name)
    {
        if (string.Equals(name, AllMention, StringComparison.OrdinalIgnoreCase)) return true;
        lock (KnownLock) return KnownNames.Contains(name);
    }

    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText), typeof(string), typeof(MentionTextBlock),
        new PropertyMetadata(string.Empty, OnSourceTextChanged));

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public Brush MentionBrush { get; set; } = DefaultMentionBrush;

    private static Brush CreateBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        brush.Freeze();
        return brush;
    }

    private static void OnSourceTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MentionTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();
        var text = SourceText;
        if (string.IsNullOrEmpty(text)) return;

        var builder = new StringBuilder();
        var index = 0;
        while (index < text.Length)
        {
            // 1) 艾特：@用户名
            if (text[index] == '@')
            {
                var mention = MentionPattern.Match(text, index);
                if (mention.Success && mention.Index == index)
                {
                    Flush();
                    var name = mention.Value[1..];
                    Inlines.Add(IsKnownName(name)
                        ? new Run(mention.Value) { Foreground = MentionBrush }
                        : new Run(mention.Value));
                    index += mention.Length;
                    continue;
                }
            }

            // 2) emoji：命中内置贴图则替换为图片
            if (EmojiCatalog.TryMatch(text, index, out var length, out var image))
            {
                Flush();
                var size = Math.Max(14, FontSize * 1.22);
                var container = new InlineUIContainer(new Image
                {
                    Source = image,
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 1, 0)
                })
                {
                    BaselineAlignment = BaselineAlignment.Center
                };
                Inlines.Add(container);
                index += length;
                continue;
            }

            builder.Append(text[index]);
            index++;
        }
        Flush();
        return;

        void Flush()
        {
            if (builder.Length == 0) return;
            Inlines.Add(new Run(builder.ToString()));
            builder.Clear();
        }
    }
}
