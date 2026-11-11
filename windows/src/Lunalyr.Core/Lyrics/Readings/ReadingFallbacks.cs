using System.Text;

namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>
/// Word breaks without a dictionary: runs of letters and digits, a new word wherever the script class changes
/// (Han, hiragana, katakana, Hangul, Thai and its neighbours, other letters), and an apostrophe between letters kept
/// inside the word ("It's"). Spaced scripts come out as ICU would split them; unspaced Chinese and Japanese only by
/// script, since telling their words apart needs a dictionary (<c>IcuWordBreaker</c> on Windows has one).
/// </summary>
public sealed class SimpleWordBreaker : IWordBreaker
{
    public static SimpleWordBreaker Instance { get; } = new();

    private enum Class { None, Han, Hiragana, Katakana, Hangul, Spaceless, Other }

    public IReadOnlyList<(int Start, int Length)> Words(string text)
    {
        var words = new List<(int Start, int Length)>();
        var graphemes = ReadingText.Graphemes(text);
        int start = -1, end = 0;
        var current = Class.None;
        for (var index = 0; index < graphemes.Count; index++)
        {
            var grapheme = graphemes[index];
            var kind = Classify(grapheme);
            if (kind == Class.None && current != Class.None && IsApostrophe(grapheme.Text)
                && index + 1 < graphemes.Count && Classify(graphemes[index + 1]) == current)
            {
                end = grapheme.Offset + grapheme.Length;
                continue;
            }
            if (kind != current && start >= 0)
            {
                words.Add((start, end - start));
                start = -1;
            }
            if (kind != Class.None)
            {
                if (start < 0) start = grapheme.Offset;
                end = grapheme.Offset + grapheme.Length;
            }
            current = kind;
        }
        if (start >= 0) words.Add((start, end - start));
        return words;
    }

    private static bool IsApostrophe(string text) => text is "'" or "’";

    private static Class Classify(ReadingText.Grapheme grapheme)
    {
        if (!grapheme.IsLetterOrNumber) return grapheme.Text == "ー" ? Class.Katakana : Class.None;
        var rune = grapheme.First;
        if (ReadingText.IsHanScript(rune)) return Class.Han;
        if (ReadingText.IsHiraganaScript(rune)) return Class.Hiragana;
        if (ReadingText.IsKatakanaScript(rune)) return Class.Katakana;
        if (KoreanRomanizer.ContainsHangul(grapheme.Text)) return Class.Hangul;
        if (rune.Value is >= 0x0E00 and <= 0x0EFF or >= 0x1000 and <= 0x109F or >= 0x1780 and <= 0x17FF) return Class.Spaceless;
        return Class.Other;
    }
}

/// <summary>
/// A Japanese tokenizer from word breaks alone, for when the system has no Japanese reader: each word is split
/// again where kanji meet kana, and no word has a reading of its own. Kana and Latin are then read by
/// transliteration and kanji get no reading, rather than a Mandarin one.
/// </summary>
public sealed class SimpleJapaneseTokenizer(IWordBreaker breaker) : IJapaneseTokenizer
{
    public static SimpleJapaneseTokenizer Create(IWordBreaker breaker) => new(breaker);

    public bool ReadsKanji => false;

    public IReadOnlyList<JapaneseToken> Tokenize(string text, int start, int length)
    {
        var slice = text.Substring(start, length);
        var tokens = new List<JapaneseToken>();
        foreach (var (wordStart, wordLength) in breaker.Words(slice))
        {
            var word = slice.Substring(wordStart, wordLength);
            var pieceStart = 0;
            bool? kanji = null;
            foreach (var grapheme in ReadingText.Graphemes(word))
            {
                var isKanji = ReadingText.IsIdeographic(grapheme.First) || grapheme.Text is "々" or "〆";
                if (kanji is { } previous && previous != isKanji)
                {
                    tokens.Add(new JapaneseToken(start + wordStart + pieceStart, grapheme.Offset - pieceStart, null));
                    pieceStart = grapheme.Offset;
                }
                kanji = isKanji;
            }
            tokens.Add(new JapaneseToken(start + wordStart + pieceStart, wordLength - pieceStart, null));
        }
        return tokens;
    }
}

/// <summary>
/// A language guess from the script alone, for Han text in particular: kana make it Japanese; otherwise characters
/// only Chinese writes (你, 们, 吗, 這) or only Japanese writes (々, 気, 駅, 渋) decide, and Simplified against
/// Traditional forms decide the script. Unsure (no marker either way) is Chinese, as the Mac treats Han without kana.
/// </summary>
public sealed class ScriptLanguageGuesser : ILanguageGuesser
{
    public static ScriptLanguageGuesser Instance { get; } = new();

    private const string ChineseOnly = "你妳她们們这這吗嗎呢吧啊么麼说說没沒个個着咱喔哦啦嘛给給让讓吃喝跟很";
    private const string JapaneseOnly = "々〆気歩戦駅広図売読渋円帰楽薬恋変様桜浜県実黒発単数対関闘辺込働畑峠枠沢涙込払伝鉄径応弁拝";
    private const string SimplifiedOnly = "们这吗么说没个爱见时间问门风飞东车长开关书语话认让还过进远选运钟里鸟马龙梦泪恋声听觉灯";
    private const string TraditionalOnly = "們這嗎麼說沒個愛見時間問門風飛東車長開關書語話認讓還過進遠選運鐘裡鳥馬龍夢淚戀聲聽覺燈";

    public string? DominantLanguage(string text)
    {
        int kana = 0, hangul = 0, han = 0, latin = 0, cyrillic = 0, chinese = 0, japanese = 0, simplified = 0, traditional = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (ReadingText.IsHiraganaScript(rune) || ReadingText.IsKatakanaScript(rune)) kana++;
            else if (rune.Value is >= 0xAC00 and <= 0xD7A3 or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F) hangul++;
            else if (ReadingText.IsHanScript(rune)) han++;
            else if (ReadingText.IsLatinScript(rune)) latin++;
            else if (rune.Value is >= 0x0400 and <= 0x052F) cyrillic++;
            if (rune.Value >= 0x10000) continue;
            var character = (char)rune.Value;
            if (ChineseOnly.Contains(character)) chinese++;
            if (JapaneseOnly.Contains(character)) japanese++;
            if (SimplifiedOnly.Contains(character)) simplified++;
            if (TraditionalOnly.Contains(character)) traditional++;
        }
        if (kana > 0 && kana * 10 >= han) return "ja";
        if (hangul > 0 && hangul >= han) return "ko";
        if (han > 0)
        {
            if (japanese > chinese) return "ja";
            return traditional > simplified ? "zh-Hant" : "zh-Hans";
        }
        // Spaced scripts need a real detector to name a language; Latin and Cyrillic stay unknown here.
        return null;
    }
}

/// <summary>Helpers shared by the fallbacks and the platform implementations.</summary>
internal static class ReadingStrings
{
    public static string Join(IEnumerable<string> parts, string separator)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (builder.Length > 0) builder.Append(separator);
            builder.Append(part);
        }
        return builder.ToString();
    }
}
