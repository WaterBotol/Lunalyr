using System.Runtime.CompilerServices;
using System.Text;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>
/// Timed tokens grouped into the words a reader actually sees. TTML, YRC and word-tagged LRC time every span: a
/// syllable ("beau", "ti", "ful") or a single CJK character is a token of its own. A run is a stretch of tokens with
/// nothing but non-space characters between them in the line's own text; it is set as one shaped word, while every
/// token keeps its own timing for the karaoke fill. Ported from the Mac's LyricRuns.swift (the pure parts: grouping,
/// parting and readings). A <see cref="LyricWord"/> is its own key (the Mac's <c>presentationID</c>): words are equal
/// when their text and times are.
/// </summary>
public static class LyricRuns
{
    /// <summary>For each token, whether it continues the previous token's run. Tokens that cannot be found in
    /// <paramref name="text"/> stand alone.</summary>
    public static List<bool> Glue(IReadOnlyList<LyricWord> words, string text)
    {
        var result = Enumerable.Repeat(false, words.Count).ToList();
        var cursor = 0;
        var previousOpen = false;
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            var token = word.Text.Trim();
            var leading = word.Text.Length > 0 && char.IsWhiteSpace(word.Text[0]);
            var trailing = word.Text.Length > 0 && char.IsWhiteSpace(word.Text[^1]);
            if (token.Length == 0) { previousOpen = false; continue; }
            var scan = cursor;
            var spaced = false;
            while (scan < text.Length && char.IsWhiteSpace(text[scan]))
            {
                spaced = true;
                scan++;
            }
            int found;
            if (text.AsSpan(scan).StartsWith(token, StringComparison.Ordinal))
            {
                found = scan;
            }
            else
            {
                found = text.IndexOf(token, scan, StringComparison.Ordinal);
                // Something the tokens do not cover sits in between; a space there still separates the words.
                if (found >= 0 && text[scan..found].Any(char.IsWhiteSpace)) spaced = true;
            }
            if (found < 0) { previousOpen = false; continue; }
            result[index] = index > 0 && previousOpen && !spaced && !leading;
            cursor = found + token.Length;
            previousOpen = !trailing;
        }
        return result;
    }

    /// <summary>Glue for the words shown for <paramref name="line"/>. Readings that replace the words one for one
    /// take the original words' grouping -- the syllables of one word still read as that word -- except that a Latin
    /// reading of a Chinese, Japanese or Thai token is a word of its own: those scripts are written without spaces,
    /// their romanisation is not (君の名は is "kimi no na wa", never "kiminonawa").</summary>
    public static List<bool> Glue(LyricLine line, IReadOnlyList<LyricWord> words)
    {
        if (words.Count == line.Words.Count && words.Zip(line.Words).Any(pair => pair.First.Text != pair.Second.Text))
        {
            var result = Glue(line.Words, line.Text);
            for (var index = 0; index < words.Count; index++)
            {
                if (words[index].Text != line.Words[index].Text && WrittenWithoutSpaces(line.Words[index].Text)
                    && !WrittenWithoutSpaces(words[index].Text))
                {
                    result[index] = false;
                    if (index + 1 < result.Count) result[index + 1] = false;
                }
            }
            return result;
        }
        return Glue(words, line.Text);
    }

    /// <summary>Han characters and kana, and Thai, Lao, Myanmar and Khmer: scripts that do not separate words with
    /// spaces, so a run of them can hold several words. (Korean does separate them.)</summary>
    public static bool WrittenWithoutSpaces(string text) =>
        text.EnumerateRunes().Any(scalar => JapaneseReading.IsJapanese(scalar)
            || scalar.Value is >= 0x0E00 and <= 0x0EFF or >= 0x1000 and <= 0x109F or >= 0x1780 and <= 0x17FF);

    /// <summary>Glue for a rolling stage, which sets a line one word at a time: <see cref="Glue(LyricLine,IReadOnlyList{LyricWord})"/>
    /// with each unspaced run parted into its words. Kept whole, a Japanese, Chinese or Thai line rolled as one
    /// word.</summary>
    public static List<bool> RollingGlue(LyricLine line, IReadOnlyList<LyricWord> words, IWordBreaker breaker) =>
        SplittingWords(words, Glue(line, words), breaker, Parting.Words);

    /// <summary>How finely <see cref="SplittingWords"/> parts an unspaced run.</summary>
    public enum Parting
    {
        /// <summary>Phrases, for a constellation's fragments: a light word -- one of kana alone, or a single
        /// character -- stays with the word before it (沈むように 溶けてゆくように, 君の名は 希望).</summary>
        Phrases,
        /// <summary>Words as a reader takes them in, for a rolling stage: in Japanese only a word that cannot begin
        /// one (<see cref="JapaneseReading.ContinuesPhrase"/>) stays with the word before it (窓辺に 座って 海を
        /// 見ていた), and elsewhere a single character does (我们 一起 走过 春天的 小路).</summary>
        Words,
    }

    /// <summary><paramref name="glue"/> with each run of a script written without spaces parted into phrases or
    /// words, each one's characters still together, and only where a timed token begins.</summary>
    public static List<bool> SplittingWords(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue, IWordBreaker breaker,
        Parting parting = Parting.Phrases)
    {
        var result = glue.ToList();
        foreach (var (start, end) in Ranges(glue, words.Count))
        {
            if (end - start <= 1) continue;
            if (!Enumerable.Range(start, end - start).Any(index => WrittenWithoutSpaces(words[index].Text))) continue;
            var offsets = new List<int>();
            var text = new StringBuilder();
            for (var index = start; index < end; index++)
            {
                offsets.Add(text.Length);
                text.Append(words[index].Text);
            }
            var starts = PhraseStarts(text.ToString(), parting, breaker);
            for (var offset = 1; offset < end - start; offset++)
            {
                if (starts.Contains(offsets[offset])) result[start + offset] = false;
            }
        }
        return result;
    }

    private static readonly Lock StartsLock = new();
    private static readonly Dictionary<string, HashSet<int>> KnownStarts = [];

    private static bool IsLight(string word) =>
        TextElements.Count(word) <= 1 || word.EnumerateRunes().All(rune => rune.Value is >= 0x3041 and <= 0x309F or 0x30FC);

    /// <summary>Where phrases or words begin in a string, by UTF-16 offset. The system's word segmentation parts out
    /// every particle and ending (沈む よう に, and Chinese nearly character by character), so some words stay with the
    /// word before them (see <see cref="Parting"/>). Worked out once per text.</summary>
    private static HashSet<int> PhraseStarts(string text, Parting parting, IWordBreaker breaker)
    {
        var key = RuntimeHelpers.GetHashCode(breaker) + (parting == Parting.Phrases ? "\u0002" : "\u0001") + text;
        lock (StartsLock)
        {
            if (KnownStarts.TryGetValue(key, out var found)) return found;
        }
        var japanese = text.EnumerateRunes().Any(rune => rune.Value is >= 0x3041 and <= 0x30FF);
        var starts = new HashSet<int>();
        string? previous = null;
        foreach (var (start, length) in breaker.Words(text))
        {
            var word = text.Substring(start, length);
            var stays = parting switch
            {
                Parting.Phrases => IsLight(word),
                _ when japanese => previous is not null && JapaneseReading.ContinuesPhrase(word, previous),
                _ => TextElements.Count(word) <= 1,
            };
            if (!stays) starts.Add(start);
            previous = word;
        }
        lock (StartsLock)
        {
            if (KnownStarts.Count > 256) KnownStarts.Clear();
            KnownStarts[key] = starts;
        }
        return starts;
    }

    /// <summary>The line as one string, runs apart and the tokens of each run together: what Echo repeats and a
    /// lyric card sets.</summary>
    public static string Text(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue) => string.Join(" ", Labels(words, glue));

    /// <summary>Each run's text, in order, punctuation included.</summary>
    public static List<string> Labels(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue) =>
        Ranges(glue, words.Count)
            .Select(range => string.Concat(Enumerable.Range(range.Start, range.End - range.Start).Select(index => words[index].Text)).Trim())
            .Where(label => label.Length > 0)
            .ToList();

    /// <summary>A glued run's reading: its tokens' readings written together. The parts of one phrase share the
    /// phrase's time (the rule <see cref="LyricRomanizer.LineReading"/> uses), so where the time changes a new phrase
    /// begins, and in a script written without spaces it is spaced from the last (今日も reads "kyou mo", 佢哋係
    /// "keoi5 dei6 hai6"). The syllables of an alphabetic word stay together ("lyu" "blyu" is "lyublyu"), and a
    /// reading's own spaces are kept. Null when nothing in the run reads differently from how it is written.</summary>
    public static string? Reading(IEnumerable<LyricWord> tokens, IReadOnlyDictionary<LyricWord, LyricWord> readings)
    {
        var text = new StringBuilder();
        var read = false;
        (double Start, double End)? phrase = null;
        LyricWord? previous = null;
        foreach (var token in tokens)
        {
            try
            {
                if (!readings.TryGetValue(token, out var reading)) { phrase = null; continue; }
                var piece = reading.Text.Trim();
                var own = (reading.Start, reading.End);
                var continues = phrase is not { } last || (last.Start == own.Start && last.End == own.End);
                phrase = own;
                if (piece.Length == 0) continue;
                if (LyricReadingPairs.IsDistinct(piece, token.Text)) read = true;
                if (text.Length > 0 && !continues
                    && (WrittenWithoutSpaces(token.Text) || (previous is not null && WrittenWithoutSpaces(previous.Text))))
                {
                    text.Append(' ');
                }
                text.Append(piece);
            }
            finally
            {
                previous = token;
            }
        }
        return read && text.Length > 0 ? text.ToString() : null;
    }

    /// <summary>Token index ranges (start inclusive, end exclusive), one per run.</summary>
    public static List<(int Start, int End)> Ranges(IReadOnlyList<bool> glue, int count)
    {
        var result = new List<(int Start, int End)>();
        var start = 0;
        for (var index = 1; index < count; index++)
        {
            if (index < glue.Count && glue[index]) continue;
            result.Add((start, index));
            start = index;
        }
        if (count > 0) result.Add((start, count));
        return result;
    }

    /// <summary>Music notes mark breaks rather than words.</summary>
    private const string Decorations = "♩♪♫♬♭♮♯";

    /// <summary>Whether a token is drawn: letters, numbers and symbols such as emoji, but not punctuation on its own
    /// or music-note decorations.</summary>
    public static bool IsVisible(string text) =>
        ReadingText.Graphemes(text).Any(grapheme => grapheme.IsLetterOrNumber
            || (IsSymbol(grapheme.First) && !Decorations.Contains(grapheme.Text, StringComparison.Ordinal)));

    private static bool IsSymbol(System.Text.Rune rune) => System.Text.Rune.GetUnicodeCategory(rune) is
        System.Globalization.UnicodeCategory.MathSymbol or System.Globalization.UnicodeCategory.CurrencySymbol
        or System.Globalization.UnicodeCategory.ModifierSymbol or System.Globalization.UnicodeCategory.OtherSymbol;

    /// <summary>A source token or glued run can contain a whole phrase. Only preview a label that can be read as one
    /// word.</summary>
    public static bool IsPreviewWord(string text, IWordBreaker breaker) => breaker.Words(text).Count == 1;

    /// <summary>The visible tokens and their glue. A token stays glued across a dropped one only if nothing but glue
    /// separated them.</summary>
    public static (List<LyricWord> Words, List<bool> Glue) Visible(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue)
    {
        var kept = new List<LyricWord>();
        var flags = new List<bool>();
        var chained = true;
        for (var index = 0; index < words.Count; index++)
        {
            chained = chained && index < glue.Count && glue[index];
            if (!IsVisible(words[index].Text)) continue;
            flags.Add(kept.Count > 0 && chained);
            kept.Add(words[index]);
            chained = true;
        }
        return (kept, flags);
    }
}

/// <summary>
/// Where "Below lyrics" draws a line's readings: under each word they read (<see cref="Match(IReadOnlyList{LyricWord},IReadOnlyList{LyricWord})"/>),
/// or, for a reading that cannot be split by word, once under the whole line (<see cref="Line"/>). Ported from the
/// Mac's WordPresentation.swift (LyricReadingPairs).
/// </summary>
public static class LyricReadingPairs
{
    public static bool IsDistinct(string reading, string lyric)
    {
        static string Key(string value) =>
            ReadingText.LettersAndNumbers(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant());
        var readingKey = Key(reading);
        return readingKey.Length > 0 && readingKey != Key(lyric);
    }

    public static Dictionary<LyricWord, string> Match(IReadOnlyList<LyricWord>? readings, IReadOnlyList<LyricWord> words)
    {
        var result = new Dictionary<LyricWord, string>();
        if (readings is null || readings.Count != words.Count) return result;
        for (var index = 0; index < words.Count; index++)
        {
            if (IsDistinct(readings[index].Text, words[index].Text)) result[words[index]] = readings[index].Text;
        }
        return result;
    }

    /// <summary>The same for a line set in runs (<paramref name="glue"/>, see <see cref="LyricRuns"/>): a glued run is
    /// one word on screen, so its reading is written once, on the run's first token, spaced by phrase
    /// (<see cref="LyricRuns.Reading"/>).</summary>
    public static Dictionary<LyricWord, string> Match(IReadOnlyList<LyricWord>? readings, IReadOnlyList<LyricWord> words,
        IReadOnlyList<bool> glue)
    {
        if (!glue.Contains(true)) return Match(readings, words);
        var result = new Dictionary<LyricWord, string>();
        if (readings is null || readings.Count != words.Count) return result;
        var timed = new Dictionary<LyricWord, LyricWord>();
        for (var index = 0; index < words.Count; index++) timed.TryAdd(words[index], readings[index]);
        var (visibleWords, visibleGlue) = LyricRuns.Visible(words, glue);
        foreach (var (start, end) in LyricRuns.Ranges(visibleGlue, visibleWords.Count))
        {
            var first = visibleWords[start];
            if (end - start == 1)
            {
                if (timed.TryGetValue(first, out var reading) && IsDistinct(reading.Text, first.Text)) result[first] = reading.Text;
            }
            else if (LyricRuns.Reading(visibleWords.GetRange(start, end - start), timed) is { } joined)
            {
                result[first] = joined;
            }
        }
        return result;
    }

    /// <summary>A saved or source reading that could not be split by word, which <c>Match</c> places under no word:
    /// it is drawn once under the whole line. Null when the readings are drawn word by word, and when not "Below
    /// lyrics" ("Replace lyrics" already sets such a reading in the line's place).</summary>
    public static string? Line(IReadOnlyList<LyricWord>? readings, LyricLine line, RomanizationDisplay display)
    {
        if (display != RomanizationDisplay.Below || readings is null || readings.Count == line.Words.Count) return null;
        return LyricRomanizer.LineReading(readings, line) is { } reading && IsDistinct(reading, line.Text) ? reading : null;
    }

    /// <summary>For a rolling stage, which sets words one at a time with no line of their own: the readings by word
    /// (by run, given the line's glue), and a reading of the whole line under the line's first word.</summary>
    public static Dictionary<LyricWord, string> Rolling(IReadOnlyList<LyricWord>? readings, LyricLine line, IReadOnlyList<bool>? glue = null)
    {
        if (Line(readings, line, RomanizationDisplay.Below) is not { } whole) return Match(readings, line.Words, glue ?? []);
        var first = line.Words.FirstOrDefault(word => LyricRuns.IsVisible(word.Text));
        return first is null ? [] : new Dictionary<LyricWord, string> { [first] = whole };
    }
}
