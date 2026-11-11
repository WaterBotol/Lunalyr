// Ported from the Mac app's LyricCredits.swift (Lunalyr's own code).

using System.Text.RegularExpressions;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>
/// Production credits that lyric services publish as timed lines, e.g. "作词 : 方文山", "人声录音室 : 雅旺录音室",
/// "词版权管理方：…" or "Producer: …". A line counts as a credit only when it opens with a label made entirely of credit
/// terms, followed by a colon. A term inside an ordinary lyric ("我 鼓起勇气 说爱你", "弹着 吉他 唱歌", "高鳴る鼓動が")
/// never matches. A label that is one everyday word a lyric could also open with ("音乐", "鼓", "Music", "Drums")
/// needs names after the colon, and a place among other credits, so "鼓：咚咚" and "Music: it's all I need" stay lyrics.
/// </summary>
public static partial class LyricCredits
{
    /// <summary>
    /// <paramref name="inCredits"/>: no lyric has come before this line yet, or the line just before it was a credit.
    /// </summary>
    public static bool IsCredit(string text, bool inCredits = true)
    {
        var line = text.Trim();
        // A line wholly in brackets is a sung aside: "(和声：啦啦啦)".
        if (line.Length > 0 && line[0] is '(' or '（' && line[^1] is ')' or '）') return false;
        var colon = ColonInFirstCharacters(line, 48);
        if (colon < 0) return inCredits && IsColonlessCredit(line);
        var label = TrimLabel(line[..colon]);
        if (label.Length == 0 || TextRules.CharacterCount(label) > 32) return false;
        var kind = EnglishRole(label) ?? ChineseRole(TextRules.Characters(label));
        if (kind is null) return false;
        return kind == Kind.Specific || (inCredits && LooksLikeNames(line[(colon + 1)..]));
    }

    /// <summary>The UTF-16 index of the first ':' or '：' among the first <paramref name="limit"/> characters, or -1.</summary>
    private static int ColonInFirstCharacters(string line, int limit)
    {
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(line);
        var count = 0;
        while (enumerator.MoveNext() && count < limit)
        {
            var element = (string)enumerator.Current;
            if (element is ":" or "：") return enumerator.ElementIndex;
            count++;
        }
        return -1;
    }

    private const string Brackets = "[]()（）【】「」〔〕<>《》";

    private static string TrimLabel(string label)
    {
        int start = 0, end = label.Length;
        while (start < end && (TextRules.IsSpace(label[start]) || Brackets.Contains(label[start]))) start++;
        while (end > start && (TextRules.IsSpace(label[end - 1]) || Brackets.Contains(label[end - 1]))) end--;
        return label[start..end];
    }

    /// <summary>
    /// Every source's lines, whichever parser made them: credits the parsers did not already set apart (Lyrics+,
    /// LRCLIB's lyrics files, a cached result) are flagged here, in song order. Safe to run again.
    /// </summary>
    public static IReadOnlyList<LyricLine> Flagging(IReadOnlyList<LyricLine> lines, string title = "", string artist = "")
    {
        var names = new TrackNames(title, artist);
        var result = lines.ToList();
        void Convert(int index) => result[index] = AsCredit(result[index]);
        int? NextSung(int index)
        {
            for (var next = index + 1; next < result.Count; next++)
            {
                if (!result[next].IsInstrumental || result[next].Credit == true) return next;
            }
            return null;
        }
        var inCredits = true;
        for (var index = 0; index < result.Count; index++)
        {
            if (result[index].Credit == true) { inCredits = true; continue; }
            if (result[index].IsInstrumental) continue;
            var text = result[index].Text;
            // "[Verse 1]", "[ Chorus ]", "Verse 3]": section labels, not sung.
            if (IsSectionLabel(text))
            {
                result[index] = result[index] with
                {
                    IsInstrumental = true, Text = "", Words = [], WordTimed = false, Romanization = null, Translation = null,
                };
                continue;
            }
            var credit = IsCredit(text, inCredits);
            if (!credit && inCredits && names.IsHeading(text)) credit = true;
            // A bare title or artist line heading the credits.
            if (!credit && inCredits && names.IsLoneName(text) && NextSung(index) is { } next
                && (result[next].Credit == true || IsCredit(result[next].Text, true) || names.IsHeading(result[next].Text)))
            {
                credit = true;
            }
            if (credit) { inCredits = true; Convert(index); }
            else inCredits = false;
        }
        return MarkingTitleHeader(result);
    }

    private static LyricLine AsCredit(LyricLine line) => line with
    {
        IsInstrumental = true, Words = [], WordTimed = false, Romanization = null, Translation = null, Credit = true,
        End = line.Start + 0.01,
    };

    private const string SectionWords =
        "verse|chorus|pre-?chorus|post-?chorus|bridge|intro|outro|hook|refrain|interlude|instrumental|solo|breakdown|drop|ad-?libs?";

    [GeneratedRegex(@"^[\[【(（]?\s*(" + SectionWords + @")\b[^\]】)）]*[\]】)）]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SectionLabelPattern();

    /// <summary>A bracketed section label ("[Verse 1]", "[Chorus: Name]", "Verse 3]"). A bare "Chorus" is left alone: it could be sung.</summary>
    public static bool IsSectionLabel(string text)
    {
        var line = text.Trim();
        if (line.Length == 0 || !("[【(（".Contains(line[0]) || "]】)）".Contains(line[^1]))) return false;
        return SectionLabelPattern().IsMatch(line);
    }

    /// <summary>
    /// The playing song's names, and the ways a lyric file repeats them in its heading: "Title - Artist",
    /// "Artist - Title", "Title (Artist)", "Title by Artist", "Artist: Title", "Title / Artist".
    /// </summary>
    public sealed class TrackNames
    {
        private readonly string _title;
        private readonly string _artist;
        private readonly string _primaryArtist;

        public TrackNames(string title, string artist)
        {
            _title = TextRules.Normalize(title);
            _artist = TextRules.Normalize(artist);
            // "Yot Club & Someone", "A, B", "A feat. B": the first name.
            var first = artist.Split([',', '&', ';', '/', '、'])[0];
            var lead = first.Split(" feat", StringSplitOptions.None)[0].Split(" ft", StringSplitOptions.None)[0];
            _primaryArtist = TextRules.Normalize(lead);
        }

        private bool Usable => _title.Length > 0;

        /// <summary>A line carrying both the title and the artist and little else.</summary>
        public bool IsHeading(string text)
        {
            if (!Usable || _primaryArtist.Length == 0) return false;
            var line = TextRules.Normalize(text);
            return TextRules.CharacterCount(line) <= TextRules.CharacterCount(_title) + TextRules.CharacterCount(_artist) + 14
                && Contains(line, _title) && Contains(line, _primaryArtist);
        }

        /// <summary>A line that is only the title, or only the artist.</summary>
        public bool IsLoneName(string text)
        {
            if (!Usable) return false;
            var line = TextRules.Normalize(text);
            return line.Length > 0 && (line == _title || line == _artist || line == _primaryArtist);
        }

        public bool IsEither(string text) => IsHeading(text) || IsLoneName(text);

        /// <summary>Whole-word containment on normalised text.</summary>
        private static bool Contains(string line, string phrase) =>
            phrase.Length > 0 && $" {line} ".Contains($" {phrase} ", StringComparison.Ordinal);
    }

    /// <summary>Unsynced lyrics have no time to show a credit at, so the ones opening the text (and the heading above them) are dropped.</summary>
    public static IReadOnlyList<string> DroppingLeadingCredits(IReadOnlyList<string> lines, string title = "", string artist = "")
    {
        var names = new TrackNames(title, artist);
        static bool Blank(string text) => TextRules.TrimSpaces(text).Length == 0;
        var index = 0;
        while (index < lines.Count && Blank(lines[index])) index++;
        // "Title - Artist" heading right above the credits.
        if (index < lines.Count
            && (lines[index].Contains(" - ", StringComparison.Ordinal) || lines[index].Contains(" – ", StringComparison.Ordinal) || names.IsHeading(lines[index])))
        {
            var next = lines.Skip(index + 1).FirstOrDefault(line => !Blank(line));
            if (next is not null && IsCredit(next, true)) index++;
        }
        var heading = index;
        while (index < lines.Count && (Blank(lines[index]) || IsCredit(lines[index], true))) index++;
        return index == heading ? lines : lines.Skip(index).ToList();
    }

    /// <summary>
    /// Services open the credits with the song's own heading ("Human Nature - Yot Club"). That line is not sung
    /// either, so when it sits right ahead of the credits it becomes one.
    /// </summary>
    public static List<LyricLine> MarkingTitleHeader(List<LyricLine> lines)
    {
        var index = lines.FindIndex(line => !line.IsInstrumental);
        if (index < 0 || index + 1 >= lines.Count || lines[index + 1].Credit != true || !(lines[index].Start < 20)) return lines;
        var text = lines[index].Text;
        if (!text.Contains(" - ", StringComparison.Ordinal) && !text.Contains(" – ", StringComparison.Ordinal)) return lines;
        var result = new List<LyricLine>(lines);
        result[index] = AsCredit(result[index]);
        return result;
    }

    /// <summary>
    /// "Produced by Max Martin", "Written by Tove Lo &amp; Ali Payami": a role word, "by", then names, with no colon.
    /// Only before the first lyric or straight after another credit, and only past-tense roles, so a lyric like
    /// "Written by the stars" (no names) or later in the song never matches.
    /// </summary>
    private static bool IsColonlessCredit(string line)
    {
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3 || words.Length > 12) return false;
        var by = Array.FindIndex(words, 0, Math.Min(4, words.Length), word => word.ToLowerInvariant() == "by");
        if (by < 1) return false;
        var before = words[..by];
        var label = string.Join(' ', words[..(by + 1)]);
        if (EnglishRole(label) != Kind.Specific) return false;
        if (!before.All(word => EnglishRoles.Contains(word.ToLowerInvariant()) || EnglishQualifiers.Contains(word.ToLowerInvariant()))) return false;
        if (!before.Any(word => word.ToLowerInvariant().EndsWith("ed", StringComparison.Ordinal) || word.ToLowerInvariant() is "written" or "prod.")) return false;
        return LooksLikeNames(string.Join(' ', words[(by + 1)..]));
    }

    private enum Kind { Specific, Everyday }

    /// <summary>Terms that can begin a credit label or follow another one: "人声" + "录音" + "室", "词" + "版权" + "管理方".</summary>
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

    /// <summary>Terms that only qualify the role after them: "电" + "吉他", "架子" + "鼓", "总" + "监制", "特别" + "鸣谢".</summary>
    private static readonly string[] Modifiers =
        ["电", "電", "木", "原声", "原聲", "架子", "总", "總", "主", "副", "低音", "第一", "第二", "首席", "特别", "特別"];

    /// <summary>Terms that only complete a preceding one: "吉他" + "手", "管理" + "方", "母带" + "处理", "录音" + "棚".</summary>
    private static readonly string[] Suffixes =
    [
        "工程师", "工程師", "工作室", "公司", "团队", "團隊", "处理", "處理", "师", "師", "手", "方", "室", "棚",
        "团", "團", "组", "組", "队", "隊", "部", "人", "者",
    ];

    /// <summary>Separators between two terms: "作词/作曲", "录音作品及MV版权".</summary>
    private static readonly string[] Joins = ["和", "及", "与", "與", "/", "／", "&", "＆", "、", ",", "，", "·", "・", "+", "＋", " ", "　"];

    /// <summary>Whole labels a lyric could also begin with: instruments, "music", and the vocal parts duets mark in the lyric itself.</summary>
    private static readonly HashSet<string> Everyday =
    [
        "音乐", "音樂", "鼓", "和声", "和聲", "合声", "合聲", "和音", "人声", "人聲", "合唱", "伴唱", "主唱", "演唱",
        "童声", "童聲", "吉他", "贝斯", "貝斯", "钢琴", "鋼琴", "键盘", "鍵盤", "弦乐", "弦樂", "提琴", "笛子", "口琴",
        "二胡", "古筝", "古箏", "琵琶",
    ];

    private static readonly string[][] RoleTerms = Roles.Select(TextRules.Characters).ToArray();
    private static readonly string[][] ModifierTerms = Modifiers.Select(TextRules.Characters).ToArray();
    private static readonly string[][] SuffixTerms = Suffixes.Select(TextRules.Characters).ToArray();
    private static readonly string[][] JoinTerms = Joins.Select(TextRules.Characters).ToArray();

    /// <summary>Segments the label into credit terms; an English role may follow ("作词 Lyricist", "鼓 Drums").</summary>
    private static Kind? ChineseRole(string[] label)
    {
        var count = label.Length;
        // complete[i]: label[..<i] is a run of terms ending in a role or suffix.
        // open[i]: it is empty or ends in a separator or modifier, so a role must follow.
        var complete = new bool[count + 1];
        var open = new bool[count + 1];
        open[0] = true;
        void Mark(string[][] terms, int index, bool[] table)
        {
            foreach (var term in terms)
            {
                if (index + term.Length <= count && label.AsSpan(index, term.Length).SequenceEqual(term)) table[index + term.Length] = true;
            }
        }
        for (var index = 0; index < count; index++)
        {
            if (!complete[index] && !open[index]) continue;
            if (complete[index])
            {
                var rest = TextRules.TrimSpaces(string.Concat(label[index..]));
                if (rest.Length > 0 && char.IsAscii(rest[0]) && EnglishRole(rest) is not null) return Kind.Specific;
                Mark(SuffixTerms, index, complete);
                Mark(JoinTerms, index, open);
            }
            Mark(RoleTerms, index, complete);
            Mark(ModifierTerms, index, open);
        }
        if (!complete[count]) return null;
        return Everyday.Contains(string.Concat(label)) ? Kind.Everyday : Kind.Specific;
    }

    /// <summary>Role words: one of them, with any qualifiers and joins, makes a label.</summary>
    private static readonly HashSet<string> EnglishRoles =
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
    ];

    /// <summary>Instruments and other everyday words: alone, they need names after the colon ("Drums: Steve Gadd", not "Drums: boom boom").</summary>
    private static readonly HashSet<string> EnglishEveryday =
    [
        "music", "words", "vocals", "vocal", "voice", "guitar", "guitars", "bass", "drums", "drum", "percussion",
        "piano", "keys", "keyboard", "keyboards", "synth", "synths", "synthesizer", "strings", "string", "violin",
        "violins", "viola", "cello", "horns", "horn", "brass", "saxophone", "sax", "trumpet", "trombone", "flute",
        "harp", "organ", "choir", "orchestra", "beat", "beats",
    ];

    /// <summary>Words that only qualify the rest: "Executive", "Electric", "by".</summary>
    private static readonly HashSet<string> EnglishQualifiers =
    [
        "executive", "co", "additional", "assistant", "associate", "lead", "background", "backing", "original",
        "sub", "electric", "acoustic", "by", "chief", "head", "main", "rhythm", "steel", "pedal", "upright",
        "double", "grand", "first", "second", "solo", "art", "artistic", "creative", "visual", "cover", "graphic",
        "project",
    ];

    /// <summary>"Producer", "Lyrics by", "Executive Producers", "Mixed &amp; Mastered by", "A&amp;R"; a lone "Keyboard" is an everyday label.</summary>
    private static Kind? EnglishRole(string label)
    {
        var words = new List<string>();
        foreach (var token in EnglishTokens(label.ToLowerInvariant()))
        {
            if (token == "a&r") words.Add(token);
            else words.AddRange(token.Split('&', StringSplitOptions.RemoveEmptyEntries));
        }
        words.RemoveAll(word => word == "and");
        if (words.Count == 0
            || !words.All(word => EnglishRoles.Contains(word) || EnglishEveryday.Contains(word) || EnglishQualifiers.Contains(word))
            || !words.Any(word => !EnglishQualifiers.Contains(word))) return null;
        return words.Count == 1 && EnglishEveryday.Contains(words[0]) ? Kind.Everyday : Kind.Specific;
    }

    /// <summary>The label cut at whitespace and at "/", ",", "+" and "-", empty pieces dropped.</summary>
    private static List<string> EnglishTokens(string label)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var index = 0; index <= label.Length; index++)
        {
            var separator = index == label.Length || char.IsWhiteSpace(label[index]) || label[index] is '/' or ',' or '+' or '-';
            if (!separator) { if (start < 0) start = index; continue; }
            if (start >= 0) tokens.Add(label[start..index]);
            start = -1;
        }
        return tokens;
    }

    private static readonly HashSet<string> Particles = ["van", "von", "de", "da", "di", "del", "der", "den", "du", "la", "le", "y"];

    /// <summary>Pronouns and particles of sung Chinese that names do not contain.</summary>
    private static readonly HashSet<string> SungCharacters =
        [.. TextRules.Characters("我你妳他她它们們的了吗嗎呢吧啊哦喔啦呀么麼着著过過")];

    /// <summary>Endings of the studios, orchestras and companies credited by name.</summary>
    private static readonly string[] Organisations =
        ["团", "團", "队", "隊", "组合", "組合", "公司", "工作室", "录音棚", "錄音棚", "录音室", "錄音室", "传媒", "傳媒", "文化", "唱片", "音乐", "音樂"];

    private static readonly char[] NameSeparators = ['/', '／', '、', ',', '，', '&', '＆', ';', '；', '|'];

    /// <summary>
    /// Names as credits list them: "高飞", "霍锦 卢泓宇", "Max Martin", "Sony Music", "西田修大/玉田豊夢". Sung words rarely
    /// look like that: "我们一起", "咚咚", "叮咚叮咚", "it's all I need", "boom boom".
    /// </summary>
    private static bool LooksLikeNames(string value)
    {
        var items = value.Split(NameSeparators).Select(TextRules.TrimSpaces).Where(item => item.Length > 0).ToList();
        if (items.Count == 0 || items.Count > 12) return false;
        return items.All(item =>
        {
            var words = item.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 5 || !words.Any(word => !Particles.Contains(word.ToLowerInvariant()))) return false;
            return words.All(word =>
            {
                if (!LyricScript.KeepsSyllables(word))
                {
                    return char.IsUpper(word, 0) || Particles.Contains(word.ToLowerInvariant());
                }
                // Han, kana or Hangul names have no pronoun or particle in them, and people's names run two to four
                // characters without being one sound repeated.
                var characters = TextRules.Characters(word);
                if (characters.Any(SungCharacters.Contains)) return false;
                if (characters.Length <= 24 && Organisations.Any(ending =>
                        characters.Length > TextRules.CharacterCount(ending) && word.EndsWith(ending, StringComparison.Ordinal))) return true;
                return characters.Length is >= 2 and <= 4 && characters.Distinct().Count() * 2 > characters.Length;
            });
        });
    }
}
