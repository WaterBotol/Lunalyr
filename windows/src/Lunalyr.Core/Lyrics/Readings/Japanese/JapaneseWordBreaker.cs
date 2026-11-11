using System.Text;

namespace Lunalyr.Core.Lyrics.Readings.Japanese;

/// <summary>
/// Word breaks for Japanese from the bundled morphological analyser, and <paramref name="other"/>'s (ICU on
/// Windows) for everything else. A text with kana in it is Japanese: its runs of Japanese characters (kana, kanji,
/// ー, 々) are split into morphemes, with an auxiliary verb kept on the verb or adjective it inflects (会お|う is one
/// word, 会おう, as Apple's word breaker has it; 見|て|いた stay apart), and the rest of it (Latin words, digits,
/// punctuation) goes to <paramref name="other"/>. Text without kana (Chinese, Thai, Latin) goes to
/// <paramref name="other"/> whole. Thread-safe when <paramref name="other"/> is.
/// </summary>
public sealed class JapaneseWordBreaker(DictionaryJapaneseTokenizer tokenizer, IWordBreaker other) : IWordBreaker
{
    public IWordBreaker Other => other;

    public IReadOnlyList<(int Start, int Length)> Words(string text)
    {
        if (!text.EnumerateRunes().Any(rune => ReadingText.IsHiraganaScript(rune) || ReadingText.IsKatakanaScript(rune)))
        {
            return other.Words(text);
        }
        var words = new List<(int Start, int Length)>();
        foreach (var (start, length, japanese) in Segments(text))
        {
            if (!japanese)
            {
                foreach (var (wordStart, wordLength) in other.Words(text.Substring(start, length))) words.Add((start + wordStart, wordLength));
                continue;
            }
            var dictionary = tokenizer.Analyzer.Dictionary;
            string? previousPart = null;
            foreach (var morpheme in tokenizer.Analyze(text.AsSpan(start, length)))
            {
                var part = dictionary.PartOfSpeech(morpheme.ContextId);
                if (part.StartsWith("記号", StringComparison.Ordinal)) { previousPart = null; continue; }
                var from = start + morpheme.Start;
                if (previousPart is not null && words.Count > 0 && words[^1].Start + words[^1].Length == from
                    && part.StartsWith("助動詞", StringComparison.Ordinal) && Inflects(previousPart))
                {
                    words[^1] = (words[^1].Start, words[^1].Length + morpheme.Length);
                }
                else
                {
                    words.Add((from, morpheme.Length));
                }
                previousPart = part;
            }
        }
        return words;
    }

    private static bool Inflects(string part) =>
        part.StartsWith("動詞", StringComparison.Ordinal) || part.StartsWith("形容詞", StringComparison.Ordinal)
        || part.StartsWith("助動詞", StringComparison.Ordinal);

    /// <summary>The text in runs of Japanese characters and runs of anything else. Digits next to Japanese go with it
    /// (５時, ２人), as the analyser reads a counter from the number before it.</summary>
    private static List<(int Start, int Length, bool Japanese)> Segments(string text)
    {
        var runes = text.EnumerateRunes().ToList();
        var japanese = runes.Select(JapaneseReading.IsJapanese).ToList();
        for (var index = 0; index < runes.Count; index++)
        {
            if (!Rune.IsDigit(runes[index])) continue;
            var end = index;
            while (end < runes.Count && Rune.IsDigit(runes[end])) end++;
            var touches = (index > 0 && japanese[index - 1]) || (end < runes.Count && japanese[end]);
            for (var digit = index; digit < end; digit++) japanese[digit] = touches;
            index = end - 1;
        }
        var segments = new List<(int Start, int Length, bool Japanese)>();
        var offset = 0;
        for (var index = 0; index < runes.Count; index++)
        {
            var rune = runes[index];
            if (segments.Count > 0 && segments[^1].Japanese == japanese[index])
            {
                segments[^1] = (segments[^1].Start, segments[^1].Length + rune.Utf16SequenceLength, japanese[index]);
            }
            else
            {
                segments.Add((offset, rune.Utf16SequenceLength, japanese[index]));
            }
            offset += rune.Utf16SequenceLength;
        }
        return segments;
    }
}
