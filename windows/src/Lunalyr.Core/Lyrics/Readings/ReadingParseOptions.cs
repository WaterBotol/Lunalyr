using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>
/// What the readings services give the parsers: readers for <see cref="LyricScript.IsReading"/> (is a Latin line
/// beside a lyric its reading or a translation?), and the system word breaker for estimated word timing.
/// </summary>
public static class ReadingParseOptions
{
    /// <summary>
    /// Readers for <see cref="LyricScript.IsReading"/>: the Japanese tokenizer for kana and kanji (kanji alone may be
    /// Japanese or Chinese, so both are tried), the system's transliteration, then the built-in reader. A reader returns
    /// null where it reads nothing, so the parser's syllable check still covers Han without a reading. Each reader is
    /// safe to call from several parses at once.
    /// </summary>
    public static IReadOnlyList<LatinReader> LatinReaders(this ReadingServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var readers = new List<LatinReader>();
        if (services.JapaneseTokenizer is not null)
        {
            var gate = new Lock();
            readers.Add(text =>
            {
                if (!text.EnumerateRunes().Any(rune => rune.Value is >= 0x3040 and <= 0x30FF or >= 0x4E00 and <= 0x9FFF))
                {
                    return null;
                }
                lock (gate)
                {
                    var parts = services.Tokenizer.Tokenize(text, 0, text.Length).Take(400)
                        .Select(token => token.Latin).OfType<string>().ToList();
                    return parts.Count == 0 ? null : string.Join(' ', parts);
                }
            });
        }
        if (services.Transliterator is not null)
        {
            var gate = new Lock();
            readers.Add(text =>
            {
                lock (gate)
                {
                    return services.ToLatin(text) is { Length: > 0 } latin ? latin : null;
                }
            });
        }
        readers.Add(BasicLatinReading.Read);
        return readers;
    }

    /// <summary>
    /// Parse options using these services: their readers, and their word breaker for estimated word timing when it is
    /// the system's (the simple fallback breaker is no better than the parsers' own splitting).
    /// </summary>
    public static LyricParseOptions ParseOptions(this ReadingServices services, IReadOnlyList<string>? preferredLanguages = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var distribute = services.WordBreaker is SimpleWordBreaker ? null : EstimatedWords.Distributor(services.WordBreaker);
        return new LyricParseOptions(PreferredLanguages: preferredLanguages, Readers: services.LatinReaders(), Distribute: distribute);
    }
}
