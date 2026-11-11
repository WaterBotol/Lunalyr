using System.Text;
using System.Text.RegularExpressions;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>
/// Japanese romaji from the system tokenizer. It reads each phrase of a line at once, because kanji need their
/// context (今日 kyou, 大人 otona), and its Latin transcription is tidied into Hepburn with wapuro-style long vowels:
/// the particles は and へ read wa and e, っ doubles the next consonant across token boundaries, ー repeats its vowel,
/// small kana join their syllable, and inflections are written with their stem (思った omotta, 会いたい aitai).
/// Ported from the Mac's JapaneseReading.swift; the tokenizer is behind <see cref="IJapaneseTokenizer"/>.
/// </summary>
public sealed partial class JapaneseReading(IJapaneseTokenizer tokenizer, IScriptTransliterator? transliterator)
{
    /// <summary>One romanised stretch of the source, with the UTF-16 range it reads.</summary>
    public sealed record Piece
    {
        public required string Text { get; set; }
        public required int Location { get; set; }
        public required int Length { get; set; }
        /// <summary>Written straight after the previous piece, with no space.</summary>
        public bool JoinsPrevious { get; set; }
        /// <summary>A space or the start of the line comes before this piece.</summary>
        public bool BreakBefore { get; set; }
        public bool IsJapanese { get; set; } = true;
    }

    public JapaneseReading(ReadingServices services) : this(services.Tokenizer, services.Transliterator)
    {
    }

    public string Romanize(string text) => Joined(Pieces(text));

    public static string Joined(IEnumerable<Piece> pieces)
    {
        var result = new StringBuilder();
        foreach (var piece in pieces)
        {
            if (piece.Text.Length == 0) continue;
            if (result.Length > 0 && !piece.JoinsPrevious) result.Append(' ');
            result.Append(piece.Text);
        }
        return result.ToString();
    }

    public List<Piece> Pieces(string text)
    {
        var tokens = Tokenize(text);
        var pieces = new List<Piece>();
        var lastSource = "";
        var atBreak = true;
        var pendingSokuon = false;
        // は or へ read ha or he begins a word (はなれないで hanarenaide), and nan is written with what follows
        // (何でも nandemo, 何度 nando).
        var startsWord = false;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            switch (token.Kind)
            {
                case Kind.Space:
                    atBreak = true; pendingSokuon = false; startsWord = false;
                    break;
                case Kind.Mark:
                {
                    pendingSokuon = false; startsWord = false;
                    var mark = Punctuation(token.Source);
                    if (mark.Length == 0) continue;
                    pieces.Add(new Piece
                    {
                        Text = mark, Location = token.Location, Length = token.Length,
                        JoinsPrevious = !atBreak && pieces.Count > 0, BreakBefore = atBreak, IsJapanese = false,
                    });
                    atBreak = false;
                    break;
                }
                case Kind.Latin:
                    pieces.Add(new Piece
                    {
                        Text = token.Source, Location = token.Location, Length = token.Length, BreakBefore = atBreak,
                        IsJapanese = false,
                    });
                    atBreak = false; pendingSokuon = false; startsWord = false;
                    break;
                case Kind.Japanese:
                {
                    var reading = Romaji(token, index, tokens);
                    // Nothing joins onto a kanji the tokenizer could not read.
                    var continuesPrevious = !atBreak && pieces.Count > 0 && pieces[^1].IsJapanese && pieces[^1].Text.Length > 0;
                    if (continuesPrevious && (reading == "~tsu" || reading.StartsWith('~') || IsLongVowelMark(reading)))
                    {
                        // Small kana, a lone っ or ー belong to the previous syllable.
                        var previous = pieces[^1];
                        previous.Text = IsLongVowelMark(reading) ? Lengthened(previous.Text, reading) : previous.Text + reading;
                        previous.Length = token.Location + token.Length - previous.Location;
                        // はぁ and はっ are sighs and gasps, not the start of a word.
                        startsWord = false;
                        continue;
                    }
                    if (reading == "~tsu") { pendingSokuon = true; atBreak = false; continue; }
                    if (IsLongVowelMark(reading)) continue;
                    if (pendingSokuon) { reading = "~tsu" + reading; pendingSokuon = false; }
                    pieces.Add(new Piece
                    {
                        Text = reading, Location = token.Location, Length = token.Length,
                        JoinsPrevious = continuesPrevious && (startsWord || Joins(token.Source, lastSource)),
                        BreakBefore = atBreak,
                    });
                    lastSource = reading.Length == 0 ? "" : token.Source;
                    startsWord = (token.Source == "は" && reading == "ha") || (token.Source == "へ" && reading == "he")
                        || (token.Source == "何" && reading == "nan" && index + 1 < tokens.Count
                            && (Counters.Contains(tokens[index + 1].Source)
                                || "でてだ".Contains(FirstGrapheme(tokens[index + 1].Source), StringComparison.Ordinal)));
                    atBreak = false;
                    break;
                }
            }
        }
        foreach (var piece in pieces)
        {
            if (piece.IsJapanese) piece.Text = SmallKana(piece.Text);
        }
        ResolveSokuon(pieces);
        return pieces.Where(piece => piece.Text.Length > 0).ToList();
    }

    // MARK: - Tokens

    private enum Kind { Space, Mark, Latin, Japanese }

    private sealed record Token(string Source, string? Latin, int Location, int Length, Kind Kind);

    /// <summary>Each phrase is read on its own: across a space or a Latin word the tokenizer loses the thread (行 after
    /// "Yeah yeah" reads kou, 君 after "ねえ " the suffix kun). The tokens keep their places in the line.</summary>
    private List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        if (text.Length == 0) return tokens;
        // The tokenizer reads full-width digits apart from what they count (２|人 nin), so it is given them as ASCII
        // digits (2人 futari). Each is one UTF-16 unit either way, so the ranges are the same, and the tokens keep the
        // digits as written.
        var ascii = AsciiDigits(text);
        void Add(int location, int count, string? latin)
        {
            var source = text.Substring(location, count);
            tokens.Add(new Token(source, latin, location, count, KindOf(source)));
        }
        foreach (var run in Runs(text))
        {
            if (run.IsSpace) { Add(run.Location, run.Length, null); continue; }
            var end = run.Location + run.Length;
            var cursor = run.Location;
            foreach (var word in tokenizer.Tokenize(ascii, run.Location, run.Length))
            {
                if (word.Length <= 0 || word.Location < cursor || word.Location + word.Length > end) continue;
                if (word.Location > cursor) Add(cursor, word.Location - cursor, null);
                Add(word.Location, word.Length, word.Latin);
                cursor = word.Location + word.Length;
            }
            if (cursor < end) Add(cursor, end - cursor, null);
        }
        return tokens;
    }

    private readonly record struct Run(int Location, int Length, bool IsSpace);

    /// <summary>Spaces, and runs of text that change between Latin letters and Japanese. Digits and punctuation go
    /// with the text around them (2人).</summary>
    private static List<Run> Runs(string text)
    {
        var runs = new List<Run>();
        Run? current = null;
        bool? script = null;
        var offset = 0;
        foreach (var scalar in text.EnumerateRunes())
        {
            var width = scalar.Utf16SequenceLength;
            var isSpace = Rune.IsWhiteSpace(scalar) || IsSeparator(scalar);
            bool? japanese = IsJapanese(scalar) ? true : ReadingText.IsAlphabetic(scalar) ? false : null;
            if (current is { } run && run.IsSpace == isSpace
                && (isSpace || japanese is null || script is null || japanese == script))
            {
                current = run with { Length = run.Length + width };
                if (!isSpace && japanese is not null) script = japanese;
            }
            else
            {
                if (current is { } finished) runs.Add(finished);
                current = new Run(offset, width, isSpace);
                script = isSpace ? null : japanese;
            }
            offset += width;
        }
        if (current is { } last) runs.Add(last);
        return runs;
    }

    private static Kind KindOf(string source)
    {
        if (source.EnumerateRunes().All(rune => Rune.IsWhiteSpace(rune) || IsSeparator(rune))) return Kind.Space;
        if (source.EnumerateRunes().Any(IsJapanese)) return Kind.Japanese;
        if (ReadingText.HasLetterOrNumber(source)) return Kind.Latin;
        return Kind.Mark;
    }

    /// <summary>The middle dot separates words in names (ジョン・レノン).</summary>
    private static bool IsSeparator(Rune rune) => rune.Value is 0x30FB or 0xFF65;

    public static bool IsJapanese(Rune scalar) => scalar.Value switch
    {
        0x30FB => false,
        >= 0x3041 and <= 0x309F or >= 0x30A1 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F => true, // kana, ー
        >= 0x3005 and <= 0x3007 or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x3134F => true,
        _ => false,
    };

    public static bool IsHiragana(Rune scalar) => scalar.Value is >= 0x3041 and <= 0x309F;

    /// <summary>A full-width digit (０–９) as its ASCII digit; anything else as it is.</summary>
    private static string AsciiDigits(string text)
    {
        var builder = new StringBuilder(text);
        for (var index = 0; index < builder.Length; index++)
        {
            if (builder[index] is >= '０' and <= '９') builder[index] = (char)(builder[index] - 0xFF10 + '0');
        }
        return builder.ToString();
    }

    private static string FirstGrapheme(string text)
    {
        if (text.Length == 0) return "";
        return text[..System.Globalization.StringInfo.GetNextTextElementLength(text, 0)];
    }

    // MARK: - Romaji

    /// <summary>Words that keep a historical は (pronounced wa) inside a single token.</summary>
    private static readonly Dictionary<string, string> Exceptions = new()
    {
        ["こんにちは"] = "konnichiwa", ["こんばんは"] = "konbanwa", ["もしくは"] = "moshikuwa", ["あるいは"] = "aruiwa",
    };

    /// <summary>A particle written after a quotation or bracket is still a particle (「愛」は).</summary>
    private static readonly HashSet<string> ClosingMarks = ["」", "』", "）", ")", "】", "〉", "》", "”", "’", "\""];

    /// <summary>Counters after which 何 reads nan (何度 nando, 何人 nannin).</summary>
    private static readonly HashSet<string> Counters =
    [
        "度", "回", "人", "年", "時", "日", "月", "歳", "才", "本", "枚", "個", "番", "階", "分", "秒", "週",
        "件", "杯", "匹", "冊", "台", "色", "語", "曲", "倍", "十", "百", "千", "万",
    ];

    /// <summary>Accents the tokenizer only produces when it falls back to Mandarin pinyin (2人 2rén); Japanese long
    /// vowels use macrons and circumflexes.</summary>
    private const string PinyinTones = "áàǎéèěíìǐóòǒúùǔǘǚǜÁÀǍÉÈĚÍÌǏÓÒǑÚÙǓǗǙǛ";

    private static bool HasPinyinTone(string text)
    {
        foreach (var character in text.Normalize(NormalizationForm.FormC))
        {
            if (PinyinTones.Contains(character)) return true;
        }
        return false;
    }

    private string Romaji(Token token, int index, List<Token> tokens)
    {
        if (Exceptions.TryGetValue(token.Source, out var fixedReading)) return fixedReading;
        var before = index > 0 ? tokens[index - 1] : null;
        var after = index + 1 < tokens.Count ? tokens[index + 1] : null;
        switch (token.Source)
        {
            case "は" or "へ":
            {
                // A particle is written straight after its word (君の名は, 未来へ), and starts a line that goes on
                // from the one before (は笑った, へ行こう). At the start of a phrase は and へ can also begin a word
                // (はなれないで) or be an interjection (は？).
                var attached = before is not null
                    && (before.Kind is Kind.Japanese or Kind.Latin || ClosingMarks.Contains(before.Source));
                var phrase = string.Concat(tokens.Skip(index).TakeWhile(t => t.Kind == Kind.Japanese).Select(t => t.Source));
                var particle = attached
                    || (!WordStarts.Any(start => phrase.StartsWith(start, StringComparison.Ordinal))
                        && LeadsAsParticle(index, tokens));
                if (token.Source == "は") return particle ? "wa" : "ha";
                return particle ? "e" : "he";
            }
            case "月" or "日" or "日間" when before is not null && IsNumberToken(before):
                return Date(token.Source, before.Source);
            case "君":
                // Alone 君 is kimi in lyrics; the tokenizer reads it as the suffix kun after a word it takes for a
                // name (ねえ君 nee kun).
                return "kimi";
            case "私":
                // Sung 私 is watashi; the tokenizer gives the formal watakushi.
                return "watashi";
            case "何":
                // The tokenizer always says nan. It is nan before d, n and t sounds and counters (何でも, 何の, 何度)
                // and nani elsewhere.
                if (after is { Kind: Kind.Japanese }
                    && (Counters.Contains(after.Source) || (after.Source.Length > 0 && "だでどなのてと".Contains(FirstGrapheme(after.Source), StringComparison.Ordinal))))
                {
                    return "nan";
                }
                return "nani";
        }
        var hasHan = token.Source.EnumerateRunes().Any(ReadingText.IsIdeographic);
        string raw;
        if (token.Latin is { Length: > 0 } latin && !(hasHan && HasPinyinTone(latin)))
        {
            raw = latin;
        }
        else if (!hasHan)
        {
            raw = transliterator?.ToLatin(token.Source) ?? token.Source;
        }
        else
        {
            // A kanji the tokenizer cannot read (simplified Chinese 爱) gets no reading rather than a Mandarin one
            // or the kanji itself; counted people (2人) are read the Japanese way.
            return Counted(token.Source) ?? "";
        }
        return Hepburn(raw);
    }

    /// <summary>Whether the は or へ at <paramref name="index"/>, which begins a phrase, is the particle, from the
    /// tokens after it. Before a word in kanji, katakana, digits or Latin letters it is (は笑った wa waratta, へ行こう
    /// e ikou). Before hiragana it is only where the tokenizer keeps the two apart even on their own (は|どこ): it
    /// splits a word such as はなれ off for what follows (は|なれ|ないで). A は followed by a particle of one kana or
    /// an ending, and then nothing in hiragana, begins a word the tokenizer took apart (はね hane, はよ hayo); a
    /// longer particle it splits in two still follows the particle (は|で|も wa demo). Before a pause, punctuation or
    /// nothing it is an interjection (は？).</summary>
    private bool LeadsAsParticle(int index, List<Token> tokens)
    {
        if (index + 1 >= tokens.Count) return false;
        var token = tokens[index];
        var next = tokens[index + 1];
        if (next.Kind is not (Kind.Japanese or Kind.Latin) || ReadingText.FirstScalar(next.Source) is not { } first
            || Continuations.Contains(first.Value))
        {
            return false;
        }
        if (!IsHiragana(first)) return true;
        // へ does come before another particle (へと, への).
        if (token.Source == "は"
            && ((TextElements.Count(next.Source) == 1 && Particles.Contains(next.Source)) || Endings.Contains(next.Source)))
        {
            var more = index + 2 < tokens.Count && tokens[index + 2].Kind == Kind.Japanese
                && ReadingText.FirstScalar(tokens[index + 2].Source) is { } third && IsHiragana(third);
            if (!more) return false;
        }
        return TokenCount(token.Source + next.Source) > 1;
    }

    /// <summary>How words beginning with は or へ start where the tokenizer reads them as the particle and a word
    /// (は|しれ for はしれ, 走れ), so that every form of them reads whole (はぐして, はぐしよう, へりくつを).</summary>
    private static readonly string[] WordStarts = ["はしれ", "はくしゅ", "はぐし", "へりくつ", "へ理屈"];

    /// <summary>月 and 日 after a number, whose readings the tokenizer guesses: the month is gatsu (it says now gatsu,
    /// now tsuki: 3月 gatsu, 4月 tsuki), and a day ka from the 2nd to the 10th and on the 14th, 20th and 24th
    /// (futsuka, tooka, hatsuka) and nichi otherwise (365日), where it says ka throughout.</summary>
    private static string Date(string counter, string number)
    {
        if (counter == "月") return "gatsu";
        var value = int.TryParse(AsciiDigits(number), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var day = value is >= 2 and <= 10 or 14 or 20 or 24 ? "ka" : "nichi";
        return counter == "日間" ? day + "kan" : day;
    }

    /// <summary>A token of ASCII or full-width digits.</summary>
    private static bool IsNumberToken(Token token) =>
        token.Source.Length > 0 && token.Source.All(character => character is >= '0' and <= '9' or >= '０' and <= '９');

    /// <summary>Small kana, っ, ー and repeat marks carry on the syllable before them (はぁ, はっ, はー), so they never
    /// follow a particle.</summary>
    private static readonly HashSet<int> Continuations =
        [.. "ぁぃぅぇぉっゃゅょゎゕゖゝゞァィゥェォッャュョヮヵヶヽヾーｰ".EnumerateRunes().Select(rune => rune.Value)];

    /// <summary>How many tokens the tokenizer makes of a short text on its own.</summary>
    private int TokenCount(string text) => tokenizer.Tokenize(AsciiDigits(text), 0, text.Length).Count;

    /// <summary>Digits with kanji, which the tokenizer reads as Mandarin: 1人 hitori, 2人 futari, otherwise the
    /// digits and the kanji's own reading.</summary>
    private string? Counted(string source)
    {
        var result = new StringBuilder();
        var graphemes = ReadingText.Graphemes(source);
        var position = 0;
        while (position < graphemes.Count)
        {
            var digits = new StringBuilder();
            while (position < graphemes.Count && graphemes[position].IsNumber) digits.Append(graphemes[position++].Text);
            var kanji = new StringBuilder();
            while (position < graphemes.Count && !graphemes[position].IsNumber) kanji.Append(graphemes[position++].Text);
            var number = AsciiDigits(digits.ToString());
            var kanjiText = kanji.ToString();
            if (kanjiText.StartsWith('人') && number is "1" or "2")
            {
                result.Append(number == "1" ? "hitori" : "futari");
                kanjiText = kanjiText[1..];
            }
            else
            {
                result.Append(number);
            }
            if (kanjiText.Length == 0) continue;
            if (Transcription(kanjiText) is not { } reading) return null;
            result.Append(reading);
        }
        var text = result.ToString();
        return text.Length == 0 || ReadingText.Graphemes(text).All(grapheme => grapheme.IsNumber) ? null : text;
    }

    /// <summary>The tokenizer's reading of a short text on its own, if every token has a Japanese one.</summary>
    private string? Transcription(string text)
    {
        var reading = new StringBuilder();
        foreach (var token in tokenizer.Tokenize(text, 0, text.Length))
        {
            if (token.Latin is not { Length: > 0 } latin || HasPinyinTone(latin)) return null;
            reading.Append(Hepburn(latin));
        }
        return reading.Length == 0 ? null : reading.ToString();
    }

    private static readonly (string Macron, string Plain)[] LongVowels =
    [
        ("ā", "aa"), ("ī", "ii"), ("ū", "uu"), ("ē", "ee"), ("ō", "oo"),
        ("â", "aa"), ("î", "ii"), ("û", "uu"), ("ê", "ee"), ("ô", "oo"),
    ];

    internal static string Hepburn(string latin)
    {
        if (IsLongVowelMark(latin)) return latin;
        var text = latin.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        // Long vowels are written out, as the rest of the reading is (kyou).
        foreach (var (macron, plain) in LongVowels) text = text.Replace(macron, plain, StringComparison.Ordinal);
        text = text.Replace("dzu", "zu", StringComparison.Ordinal).Replace("dji", "ji", StringComparison.Ordinal);
        text = DjBeforeVowel().Replace(text, "j");
        // The apostrophe only separates n from a following vowel or y.
        text = ApostropheAfterN().Replace(text, "n");
        return Lengthened("", text);
    }

    [GeneratedRegex("dj(?=[auo])")]
    private static partial Regex DjBeforeVowel();

    [GeneratedRegex("n'(?![aiueoy])")]
    private static partial Regex ApostropheAfterN();

    private static bool IsLongVowelMark(string text) => text.Length > 0 && text.All(character => character is 'ー' or 'ｰ');

    /// <summary>Appends <paramref name="marks"/>, writing each ー as a repeat of the vowel before it.</summary>
    private static string Lengthened(string text, string marks)
    {
        var result = new StringBuilder(text);
        foreach (var grapheme in ReadingText.Graphemes(marks))
        {
            if (grapheme.Text is "ー" or "ｰ")
            {
                if (result.Length > 0 && "aiueo".Contains(result[^1])) result.Append(result[^1]);
            }
            else
            {
                result.Append(grapheme.Text);
            }
        }
        return result.ToString();
    }

    private static readonly (Regex Pattern, string Template)[] SmallKanaRules =
    [
        (new Regex("vu~y([auo])"), "vy$1"), (new Regex("vu~([aieo])"), "v$1"),
        (new Regex("([kg])u~a"), "$1wa"), (new Regex("([kg])u~([ieo])"), "$1w$2"),
        (new Regex("fu~y([auo])"), "fy$1"), (new Regex("fu~([aieo])"), "f$1"), (new Regex("tsu~([aieo])"), "ts$1"),
        (new Regex("([sz])u~i"), "$1i"), (new Regex("([td])o~u"), "$1u"), (new Regex("([td])e~y([auo])"), "$1y$2"),
        (new Regex("([td])e~i"), "$1i"),
        (new Regex("(sh|ch|j)i~e"), "$1e"), (new Regex("(sh|ch|j)i~y([auo])"), "$1$2"),
        (new Regex("([kgnhbpmr])i~e"), "$1ye"), (new Regex("([kgnhbpmr])i~y([auo])"), "$1y$2"),
        (new Regex("(^|[^a-z])u~([aieo])"), "$1w$2"), (new Regex("(^|[^a-z])i~e"), "$1ye"),
        (new Regex("~(?=[aiueo]|y[auo]|wa|k[ae])"), ""), (new Regex("~(?!tsu)"), ""),
    ];

    /// <summary>Small kana: ヴァ va, ウィ wi, ティ ti, ねぇ nee. っ is left for sokuon.</summary>
    private static string SmallKana(string text)
    {
        if (!text.Contains('~')) return text;
        var result = text;
        foreach (var (pattern, template) in SmallKanaRules) result = pattern.Replace(result, template);
        return result;
    }

    /// <summary>っ doubles the consonant that follows it, even in the next token (思っ + た = omotta). Before a vowel
    /// or a pause it is not written.</summary>
    private static void ResolveSokuon(List<Piece> pieces)
    {
        for (var index = 0; index < pieces.Count; index++)
        {
            while (pieces[index].Text.IndexOf("~tsu", StringComparison.Ordinal) is var marker and >= 0)
            {
                var text = pieces[index].Text;
                var rest = text[(marker + 4)..];
                if (rest.Length > 0)
                {
                    pieces[index].Text = text[..marker] + (Geminate(rest) ?? "") + rest;
                    continue;
                }
                if (index + 1 < pieces.Count && pieces[index + 1].IsJapanese && !pieces[index + 1].BreakBefore
                    && Geminate(pieces[index + 1].Text) is { } consonant)
                {
                    pieces[index].Text = text[..marker] + consonant;
                    pieces[index + 1].JoinsPrevious = true;
                }
                else
                {
                    pieces[index].Text = text[..marker];
                }
            }
        }
    }

    private static string? Geminate(string following)
    {
        if (following.StartsWith("ch", StringComparison.Ordinal)) return "t";
        if (following.Length == 0 || !"bcdfghjklmpqrstvwxyz".Contains(following[0])) return null;
        return following[0].ToString();
    }

    public static string Punctuation(string source)
    {
        var result = new StringBuilder();
        foreach (var grapheme in ReadingText.Graphemes(source))
        {
            switch (grapheme.Text)
            {
                case "。" or "｡" or "．": result.Append('.'); break;
                case "、" or "､" or "，": result.Append(','); break;
                case "！": result.Append('!'); break;
                case "？": result.Append('?'); break;
                case "〜" or "～": result.Append('~'); break;
                case "「" or "」" or "『" or "』" or "｢" or "｣" or "【" or "】" or "（" or "）" or "(" or ")" or "〈" or "〉"
                    or "《" or "》" or "\"" or "“" or "”" or "‘" or "’" or "[" or "]":
                    break;
                default: result.Append(grapheme.Text); break;
            }
        }
        return result.ToString();
    }

    // MARK: - Spacing

    private static readonly HashSet<string> Particles =
    [
        "は", "が", "を", "に", "へ", "と", "で", "も", "の", "や", "か", "ね", "よ", "な", "わ", "ぞ",
        "じゃ", "って", "から", "まで", "より", "けど", "けれど", "だけ", "ほど", "など", "こそ",
        "でも", "とか", "ので", "のに", "しか", "ばかり",
    ];

    /// <summary>Endings that only ever follow a verb or adjective stem.</summary>
    private static readonly HashSet<string> Endings =
    [
        "た", "て", "てる", "てた", "ちゃ", "ちゃう", "ちゃっ", "じゃう", "じゃっ", "たい", "たく", "たかっ",
        "たけれ", "たら", "たり", "ます", "まし", "ませ", "ましょう", "ません", "れ", "られ", "せ", "させ",
        "ば", "ず", "ぬ",
    ];

    private static readonly HashSet<string> Negatives = ["ない", "なかっ", "なく", "なきゃ", "なけれ", "ないで"];
    private static readonly HashSet<string> VoicedEndings = ["だ", "で", "だっ", "だら", "だり"];

    /// <summary>Whether a word only ever follows another, so a space before it never begins a phrase: a particle or
    /// an ending (は, から, た, たい, として). A negative is left out, as ない also stands on its own (もう ない,
    /// へんじ ない).</summary>
    public static bool FollowsAWord(string word) =>
        Particles.Contains(word) || Endings.Contains(word) || VoicedEndings.Contains(word) || word is "として" or "という";

    /// <summary>Small kana, っ, ー and repeat marks, which carry on the syllable before them.</summary>
    public static bool CarriesOn(Rune scalar) => Continuations.Contains(scalar.Value);

    /// <summary>Whether a word of the system's word segmentation goes on the phrase of the word before it, as a
    /// reader takes a line in, rather than beginning one (窓辺に 座って 海を 見ていた): a particle or an ending, a
    /// lone kana, small kana or ー, a negative after anything but a particle (見ない, 行かない, but 時間が ない), a
    /// kanji going on a compound the segmentation parted (何 度), a verb going on a te-form (歩いて いく, 待って いる),
    /// and よう, みたい and らしい.</summary>
    public static bool ContinuesPhrase(string word, string previous)
    {
        if (ReadingText.FirstScalar(word) is not { } first || ReadingText.LastScalar(previous) is not { } last) return false;
        if (Continuations.Contains(first.Value) || FollowsAWord(word) || PhraseEndings.Contains(word)) return true;
        var single = TextElements.Count(word) == 1;
        if (single && (IsHiragana(first) || first.Value is >= 0x30A1 and <= 0x30FA)) return true;
        if (Negatives.Contains(word)) return !Particles.Contains(previous);
        if (single && ReadingText.IsIdeographic(first)) return ReadingText.IsIdeographic(last);
        return (previous.EndsWith('て') || previous.EndsWith('で')) && TeAuxiliaries.Contains(word);
    }

    private static readonly HashSet<string> PhraseEndings = ["てく", "てっ", "よう", "みたい", "らしい"];

    /// <summary>Verbs that go on a te-form (〜ている, 〜ていく, 〜てしまう, 〜てくれる).</summary>
    private static readonly HashSet<string> TeAuxiliaries =
    [
        "いる", "いた", "いて", "いく", "いっ", "いき", "いけ", "いれ", "ゆく", "ゆき", "ゆけ", "くる", "きた", "きて",
        "こない", "しまう", "しまっ", "しまい", "しまえ", "みる", "みた", "みて", "みれ", "みよ", "おく", "おい", "おこ",
        "あげる", "あげ", "くれる", "くれ", "もらう", "もらっ", "もらい", "ほしい", "ほしく", "ほしかっ",
    ];

    /// <summary>Whether <paramref name="source"/> is written onto the previous token (aishi + teru).</summary>
    private static bool Joins(string source, string previous)
    {
        if (previous.Length == 0) return false;
        if (source == "ん") return true;
        if ((previous == "だ" && source == "って") || (previous == "で" && source == "も")) return true;
        if (Particles.Contains(previous)) return false;
        if (Endings.Contains(source)) return true;
        if (Negatives.Contains(source)) return ReadingText.LastScalar(previous) is { } last && IsHiragana(last);
        if (VoicedEndings.Contains(source)) return previous.EndsWith('ん') || previous.EndsWith("ない", StringComparison.Ordinal);
        return false;
    }
}
