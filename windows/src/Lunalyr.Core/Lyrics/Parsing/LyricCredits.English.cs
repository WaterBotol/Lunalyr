// Ported from the Mac app's LyricCredits.swift (a0b60c3; Lunalyr's own code).

using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>Chinese credit labels in songs sung in another language.</summary>
public static partial class LyricCredits
{
    /// <summary>
    /// Whether the song is sung in something other than Chinese: its sung lines are under 30% Han. No sung lines:
    /// unknown, so false (credits are kept as they are).
    /// </summary>
    public static bool SungInAnotherLanguage(IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        int han = 0, letters = 0;
        foreach (var line in lines)
        {
            if (line.Credit == true) continue;
            foreach (var rune in line.Text.EnumerateRunes())
            {
                if (!IsLetterOrMark(rune)) continue;
                letters++;
                if (IsHan(rune)) han++;
            }
        }
        return letters > 0 && (double)han / letters < 0.3;
    }

    /// <summary>
    /// Chinese catalogues (QQ Music, Kuwo, Kugou) head an English song's lyrics with credits labelled in Chinese
    /// ("作词 : Dylan Minnette") and notices ("未经许可，不得翻唱"). The label in English, the value kept; null drops the
    /// line: a notice, or a label with no common English name.
    /// </summary>
    public static string? EnglishCredit(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (Notices.Any(notice => trimmed.Contains(notice, StringComparison.Ordinal))) return null;
        var colon = trimmed.IndexOfAny([':', '：']);
        if (colon < 0) return ContainsHan(trimmed) ? null : trimmed;
        var label = trimmed[..colon].Trim();
        var value = trimmed[(colon + 1)..].Trim();
        if (!ContainsHan(label)) return trimmed;
        if (value.Length == 0 || !EnglishLabels.TryGetValue(label, out var english)) return null;
        return $"{english}: {value}";
    }

    private static bool IsHan(Rune rune) => rune.Value is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF;

    private static bool ContainsHan(string text) => text.EnumerateRunes().Any(IsHan);

    /// <summary>Letters and marks, as Foundation's letters set counts them.</summary>
    private static bool IsLetterOrMark(Rune rune) => Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>Copyright and licensing notices: never credits worth showing.</summary>
    private static readonly string[] Notices = ["未经", "未經", "许可", "許可", "著作权", "著作權", "版权所有", "版權所有", "享有", "不得", "禁止", "授权", "授權"];

    /// <summary>Common labels, Simplified and Traditional, in the English credits' wording.</summary>
    public static IReadOnlyDictionary<string, string> EnglishLabels { get; } = BuildEnglishLabels();

    private static Dictionary<string, string> BuildEnglishLabels()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string english, params string[] labels)
        {
            foreach (var label in labels) table[label] = english;
        }
        Add("Lyrics by", "作词", "作詞", "词", "詞", "填词", "填詞");
        Add("Composed by", "作曲", "曲", "谱曲", "譜曲");
        Add("Written by", "作词作曲", "作詞作曲", "词曲", "詞曲", "词/曲", "詞/曲", "作词/作曲", "作詞/作曲");
        Add("Arranged by", "编曲", "編曲", "编配", "編配");
        Add("Produced by", "制作人", "製作人", "制作", "製作", "音乐制作", "音樂製作", "制作人/编曲");
        Add("Executive producer", "监制", "監製", "总监制", "總監製", "音乐总监", "音樂總監");
        Add("Mixed by", "混音", "混缩", "混音师", "混音師", "混音工程师", "混音工程師");
        Add("Mastered by", "母带", "母帶", "母带处理", "母帶處理", "母带工程师", "母帶工程師");
        Add("Mixed & mastered by", "混音/母带", "混音/母帶", "混音&母带", "混音及母带");
        Add("Recorded by", "录音", "錄音", "录制", "錄製", "录音师", "錄音師", "录音工程师", "錄音工程師");
        Add("Vocals recorded by", "人声录音", "人聲錄音", "人声录音师", "人聲錄音師");
        Add("Vocal producer", "配唱制作人", "配唱製作人", "人声制作", "人聲製作", "配唱");
        Add("Backing vocals", "和声", "和聲", "合声", "合聲", "和音", "伴唱", "和声编写", "和聲編寫");
        Add("Guitar", "吉他", "吉他手");
        Add("Electric guitar", "电吉他", "電吉他");
        Add("Acoustic guitar", "木吉他", "原声吉他", "原聲吉他");
        Add("Bass", "贝斯", "貝斯");
        Add("Drums", "鼓", "架子鼓", "鼓手");
        Add("Keyboards", "键盘", "鍵盤");
        Add("Piano", "钢琴", "鋼琴");
        Add("Strings", "弦乐", "弦樂");
        Add("Programming", "编程", "編程");
        Add("Presented by", "出品", "出品人", "出品方");
        Add("Released by", "发行", "發行", "发行方", "發行方");
        Add("Recording studio", "录音室", "錄音室", "录音棚", "錄音棚");
        Add("Mixing studio", "混音室");
        return table;
    }
}
