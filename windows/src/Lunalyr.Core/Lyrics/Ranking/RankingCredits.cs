using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>
/// Production-credit detection ("作词 : 方文山", "Producer: …", "Produced by Max Martin") as the timing-fault rules
/// need it: credit lines are often stamped a moment apart before the song starts and must not count as sung.
/// </summary>
/// <remarks>
/// Ported from the Mac's <c>LyricCredits.isCredit</c> (Lunalyr's own code). The parsers flag credits themselves; this
/// copy exists so ranking does not depend on them. Candidate for de-duplication once the parser workstream lands its
/// <c>LyricCredits</c>.
/// </remarks>
internal static class RankingCredits
{
    /// <summary>
    /// Whether <paramref name="text"/> is a credit. <paramref name="inCredits"/>: no lyric has come before this line
    /// yet, or the line just before it was a credit.
    /// </summary>
    public static bool IsCredit(string text, bool inCredits = true)
    {
        var line = text.Trim();
        // A line wholly in brackets is a sung aside: "(和声：啦啦啦)".
        if (line.Length > 0 && line[0] is '(' or '（' && line[^1] is ')' or '）')
        {
            return false;
        }
        var colon = ColonIndex(line);
        if (colon < 0)
        {
            return inCredits && IsColonlessCredit(line);
        }
        var label = TrimLabel(line[..colon]);
        if (label.Length == 0 || LyricTextFolding.GraphemeCount(label) > 32)
        {
            return false;
        }
        var kind = EnglishRole(label) ?? ChineseRole([.. LyricTextFolding.Graphemes(label)]);
        if (kind is null)
        {
            return false;
        }
        return kind == Kind.Specific || (inCredits && LooksLikeNames(line[(colon + 1)..]));
    }

    /// <summary>The UTF-16 index of the first ':' or '：' within the first 48 characters, or -1.</summary>
    private static int ColonIndex(string line)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(line);
        var seen = 0;
        while (seen < 48 && enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (element is ":" or "：")
            {
                return enumerator.ElementIndex;
            }
            seen++;
        }
        return -1;
    }

    private static bool IsHorizontalSpace(char c) => c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    private const string Brackets = "[]()（）【】「」〔〕<>《》";

    private static string TrimLabel(string label)
    {
        int start = 0, end = label.Length;
        while (start < end && (IsHorizontalSpace(label[start]) || Brackets.Contains(label[start], StringComparison.Ordinal)))
        {
            start++;
        }
        while (end > start && (IsHorizontalSpace(label[end - 1]) || Brackets.Contains(label[end - 1], StringComparison.Ordinal)))
        {
            end--;
        }
        return label[start..end];
    }

    private static string TrimHorizontal(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && IsHorizontalSpace(text[start]))
        {
            start++;
        }
        while (end > start && IsHorizontalSpace(text[end - 1]))
        {
            end--;
        }
        return text[start..end];
    }

    /// <summary>
    /// "Produced by Max Martin", "Written by Tove Lo &amp; Ali Payami": a role word, "by", then names, with no colon.
    /// Only past-tense roles, so a lyric like "Written by the stars" (no names) never matches.
    /// </summary>
    private static bool IsColonlessCredit(string line)
    {
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 3 or > 12)
        {
            return false;
        }
        var by = -1;
        for (var i = 0; i < Math.Min(4, words.Length); i++)
        {
            if (Lower(words[i]) == "by")
            {
                by = i;
                break;
            }
        }
        if (by < 1)
        {
            return false;
        }
        var label = string.Join(' ', words[..(by + 1)]);
        if (EnglishRole(label) != Kind.Specific)
        {
            return false;
        }
        var before = words[..by];
        if (!before.All(w => EnglishRoles.Contains(Lower(w)) || EnglishQualifiers.Contains(Lower(w))))
        {
            return false;
        }
        if (!before.Any(w => Lower(w).EndsWith("ed", StringComparison.Ordinal) || Lower(w) is "written" or "prod."))
        {
            return false;
        }
        return LooksLikeNames(string.Join(' ', words[(by + 1)..]));
    }

    private static string Lower(string text) => text.ToLowerInvariant();

    private enum Kind
    {
        Specific,
        Everyday,
    }

    // Terms that can begin a credit label or follow another one: "人声" + "录音" + "室", "词" + "版权" + "管理方".
    private static readonly string[] Roles =
    [
        "作词作曲", "作词", "作詞", "作曲", "编曲", "編曲", "词曲", "詞曲", "词", "詞", "曲", "作者",
        "制作人", "製作人", "制作", "製作", "监制", "監製", "出品人", "出品", "联合", "聯合", "发行", "發行",
        "混音", "混缩", "母带", "母帶", "录音", "錄音", "录制", "錄製", "人声", "人聲", "和声", "和聲",
        "和音", "合声", "合聲", "伴唱", "伴奏", "配唱", "主唱", "演唱", "原唱", "翻唱", "合唱", "童声", "童聲",
        "编写", "編寫", "编辑", "編輯", "编配", "編配", "配器", "编程", "編程", "采样", "採樣", "音效",
        "音频", "音頻", "吉他", "贝斯", "貝斯", "鼓", "键盘", "鍵盤", "钢琴", "鋼琴", "弦乐", "弦樂",
        "管乐", "管樂", "打击乐", "打擊樂", "提琴", "小提琴", "大提琴", "中提琴", "长笛", "長笛", "笛子",
        "二胡", "古筝", "古箏", "琵琶", "口琴", "合成器", "乐队", "樂隊", "乐团", "樂團", "乐手", "樂手", "器乐", "器樂",
        "乐器", "樂器", "电子琴", "電子琴", "萨克斯", "薩克斯", "小号", "小號", "长号", "長號", "圆号", "圓號",
        "手风琴", "手風琴", "竖琴", "豎琴", "扬琴", "揚琴", "唢呐", "嗩吶", "尤克里里",
        "演奏", "工程师", "工程師", "版权", "版權", "管理方", "管理", "作品", "统筹", "統籌", "策划",
        "策劃", "企划", "企劃", "宣传", "宣傳", "推广", "推廣", "营销", "營銷", "封面", "设计", "設計",
        "摄影", "攝影", "视觉", "視覺", "后期", "後期", "美术", "美術", "文案", "音乐", "音樂", "总监",
        "總監", "艺人", "藝人", "经纪", "經紀", "项目", "項目", "执行", "執行", "助理", "指导", "指導",
        "声乐", "聲樂", "鸣谢", "鳴謝", "艺术", "藝術", "작사", "작곡", "편곡", "프로듀서", "MV", "OP", "SP", "ISRC",
    ];

    // Terms that only qualify the role after them: "电" + "吉他", "架子" + "鼓".
    private static readonly string[] Modifiers =
        ["电", "電", "木", "原声", "原聲", "架子", "总", "總", "主", "副", "低音", "第一", "第二", "首席", "特别", "特別"];

    // Terms that only complete a preceding one: "吉他" + "手", "管理" + "方".
    private static readonly string[] Suffixes =
    [
        "工程师", "工程師", "工作室", "公司", "团队", "團隊", "处理", "處理", "师", "師", "手", "方", "室", "棚",
        "团", "團", "组", "組", "队", "隊", "部", "人", "者",
    ];

    // Separators between two terms: "作词/作曲", "录音作品及MV版权".
    private static readonly string[] Joins = ["和", "及", "与", "與", "/", "／", "&", "＆", "、", ",", "，", "·", "・", "+", "＋", " ", "　"];

    // Whole labels a lyric could also begin with.
    private static readonly HashSet<string> Everyday = new(
    [
        "音乐", "音樂", "鼓", "和声", "和聲", "合声", "合聲", "和音", "人声", "人聲", "合唱", "伴唱", "主唱", "演唱",
        "童声", "童聲", "吉他", "贝斯", "貝斯", "钢琴", "鋼琴", "键盘", "鍵盤", "弦乐", "弦樂", "提琴", "笛子", "口琴",
        "二胡", "古筝", "古箏", "琵琶",
    ], StringComparer.Ordinal);

    private static readonly string[][] RoleTerms = Terms(Roles);
    private static readonly string[][] ModifierTerms = Terms(Modifiers);
    private static readonly string[][] SuffixTerms = Terms(Suffixes);
    private static readonly string[][] JoinTerms = Terms(Joins);

    private static string[][] Terms(string[] values) => [.. values.Select(v => LyricTextFolding.Graphemes(v).ToArray())];

    /// <summary>Segments the label into credit terms; an English role may follow ("作词 Lyricist").</summary>
    private static Kind? ChineseRole(string[] label)
    {
        var count = label.Length;
        // complete[i]: label[..i] is a run of terms ending in a role or suffix.
        // open[i]: it is empty or ends in a separator or modifier, so a role must follow.
        var complete = new bool[count + 1];
        var open = new bool[count + 1];
        open[0] = true;
        for (var index = 0; index < count; index++)
        {
            if (!complete[index] && !open[index])
            {
                continue;
            }
            if (complete[index])
            {
                var rest = TrimHorizontal(string.Concat(label[index..]));
                if (rest.Length > 0 && rest[0] < 0x80 && EnglishRole(rest) is not null)
                {
                    return Kind.Specific;
                }
                Mark(SuffixTerms, index, complete);
                Mark(JoinTerms, index, open);
            }
            Mark(RoleTerms, index, complete);
            Mark(ModifierTerms, index, open);
        }
        if (!complete[count])
        {
            return null;
        }
        return Everyday.Contains(string.Concat(label)) ? Kind.Everyday : Kind.Specific;

        void Mark(string[][] terms, int at, bool[] table)
        {
            foreach (var term in terms)
            {
                if (at + term.Length <= count && label.AsSpan(at, term.Length).SequenceEqual(term))
                {
                    table[at + term.Length] = true;
                }
            }
        }
    }

    private static readonly HashSet<string> EnglishRoles = new(
    [
        "producer", "producers", "production", "produced", "prod", "prod.", "coproducer", "composer", "composers", "composed",
        "composition", "lyricist", "lyricists", "lyrics", "lyric", "writer", "writers", "written", "songwriter",
        "songwriters", "songwriting", "arranger", "arrangers", "arranged", "arrangement", "arrangements", "engineer",
        "engineers", "engineered", "engineering", "mixer", "mixing", "mixed", "mix", "mastering", "mastered",
        "recording", "recorded", "programming", "programmed", "programmer", "publisher", "publishers", "publishing",
        "published", "copyright", "isrc", "a&r", "op", "sp", "studio", "studios", "editor", "editing", "edited",
        "director", "conductor", "orchestrator", "orchestration", "orchestrated", "coordinator", "management",
        "manager", "label", "remix", "remixer", "remixed", "performer", "performers", "performed", "supervisor",
        "supervision", "supervised", "direction", "coordination", "presented", "presenter", "release", "released",
        "distribution", "distributed", "distributor", "marketing", "promotion", "design", "designer", "designed",
        "photography", "photographer", "artwork", "planning", "planner", "mv",
    ], StringComparer.Ordinal);

    private static readonly HashSet<string> EnglishEveryday = new(
    [
        "music", "words", "vocals", "vocal", "voice", "guitar", "guitars", "bass", "drums", "drum", "percussion",
        "piano", "keys", "keyboard", "keyboards", "synth", "synths", "synthesizer", "strings", "string", "violin",
        "violins", "viola", "cello", "horns", "horn", "brass", "saxophone", "sax", "trumpet", "trombone", "flute",
        "harp", "organ", "choir", "orchestra", "beat", "beats",
    ], StringComparer.Ordinal);

    private static readonly HashSet<string> EnglishQualifiers = new(
    [
        "executive", "co", "additional", "assistant", "associate", "lead", "background", "backing", "original",
        "sub", "electric", "acoustic", "by", "chief", "head", "main", "rhythm", "steel", "pedal", "upright",
        "double", "grand", "first", "second", "solo", "art", "artistic", "creative", "visual", "cover", "graphic",
        "project",
    ], StringComparer.Ordinal);

    /// <summary>"Producer", "Lyrics by", "Mixed &amp; Mastered by", "Electric Guitar"; a lone "Keyboard" is everyday.</summary>
    private static Kind? EnglishRole(string label)
    {
        var words = new List<string>();
        var token = new StringBuilder();
        foreach (var c in label.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c) || c is '/' or ',' or '+' or '-')
            {
                Flush();
            }
            else
            {
                token.Append(c);
            }
        }
        Flush();
        words.RemoveAll(w => w == "and");
        if (words.Count == 0
            || !words.All(w => EnglishRoles.Contains(w) || EnglishEveryday.Contains(w) || EnglishQualifiers.Contains(w))
            || !words.Any(w => !EnglishQualifiers.Contains(w)))
        {
            return null;
        }
        return words.Count == 1 && EnglishEveryday.Contains(words[0]) ? Kind.Everyday : Kind.Specific;

        void Flush()
        {
            if (token.Length == 0)
            {
                return;
            }
            var value = token.ToString();
            token.Clear();
            if (value == "a&r")
            {
                words.Add(value);
            }
            else
            {
                words.AddRange(value.Split('&', StringSplitOptions.RemoveEmptyEntries));
            }
        }
    }

    private static readonly HashSet<string> Particles =
        new(["van", "von", "de", "da", "di", "del", "der", "den", "du", "la", "le", "y"], StringComparer.Ordinal);

    // Pronouns and particles of sung Chinese that names do not contain.
    private static readonly HashSet<string> SungCharacters =
        new(LyricTextFolding.Graphemes("我你妳他她它们們的了吗嗎呢吧啊哦喔啦呀么麼着著过過"), StringComparer.Ordinal);

    // Endings of the studios, orchestras and companies credited by name.
    private static readonly string[] Organisations =
        ["团", "團", "队", "隊", "组合", "組合", "公司", "工作室", "录音棚", "錄音棚", "录音室", "錄音室", "传媒", "傳媒", "文化", "唱片", "音乐", "音樂"];

    private const string NameSeparators = "/／、,，&＆;；|";

    /// <summary>Names as credits list them: "高飞", "Max Martin", "西田修大/玉田豊夢". Sung words rarely look like that.</summary>
    private static bool LooksLikeNames(string value)
    {
        var items = value.Split(NameSeparators.ToCharArray())
            .Select(TrimHorizontal).Where(item => item.Length > 0).ToList();
        if (items.Count is 0 or > 12)
        {
            return false;
        }
        return items.All(item =>
        {
            var words = item.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 5 || !words.Any(w => !Particles.Contains(Lower(w))))
            {
                return false;
            }
            return words.All(word =>
            {
                if (!RankingScript.KeepsSyllables(word))
                {
                    return (Rune.DecodeFromUtf16(word, out var first, out _) == System.Buffers.OperationStatus.Done && Rune.IsUpper(first))
                        || Particles.Contains(Lower(word));
                }
                // Han, kana or Hangul names have no pronoun or particle in them, and people's names run two to four
                // characters without being one sound repeated.
                var characters = LyricTextFolding.Graphemes(word).ToList();
                if (characters.Any(SungCharacters.Contains))
                {
                    return false;
                }
                var length = characters.Count;
                if (length <= 24 && Organisations.Any(o => length > LyricTextFolding.GraphemeCount(o) && word.EndsWith(o, StringComparison.Ordinal)))
                {
                    return true;
                }
                return length is >= 2 and <= 4 && characters.Distinct(StringComparer.Ordinal).Count() * 2 > length;
            });
        });
    }
}

/// <summary>Script facts ranking needs.</summary>
internal static class RankingScript
{
    /// <summary>
    /// Text in a script written without spaces whose characters are syllables (Han, kana, Hangul, Thai, Lao, Myanmar,
    /// Khmer). Ported from the Mac's <c>LyricScript.keepsSyllables</c>; a de-duplication candidate.
    /// </summary>
    public static bool KeepsSyllables(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is
                (>= 0x1100 and <= 0x11FF) or (>= 0x3130 and <= 0x318F) or (>= 0xA960 and <= 0xA97F) or (>= 0xAC00 and <= 0xD7FF) // Hangul
                or (>= 0x3040 and <= 0x30FF) or (>= 0x31F0 and <= 0x31FF) or (>= 0xFF66 and <= 0xFF9F) // Kana
                or 0x3005 or 0x3007 or 0x303B or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF)
                or (>= 0x20000 and <= 0x323AF) // Han
                or (>= 0x0E00 and <= 0x0EFF) or (>= 0x1000 and <= 0x109F) or (>= 0x1780 and <= 0x17FF)) // Thai, Lao, Myanmar, Khmer
            {
                return true;
            }
        }
        return false;
    }
}
