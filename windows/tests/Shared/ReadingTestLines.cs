using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;

namespace Lunalyr.Tests.Shared;

/// <summary>Lines shaped as the Mac's reading tests build them (RomanizationTests, LyricRunReadingTests).</summary>
internal sealed class ReadingTestLines(ReadingServices services)
{
    public ReadingServices Services { get; } = services;
    public LyricRomanizer Romanizer { get; } = new(services);

    public static RomanizationOptions Options(RomanizationDisplay display = RomanizationDisplay.Below,
        RomanizationLanguage language = RomanizationLanguage.Automatic, bool preferSaved = true) =>
        new(display, language, ToneMarks: true, PreferSaved: preferSaved);

    /// <summary>A word-timed line with one word per character, as syllable-synced Japanese, Chinese and Korean lyrics
    /// often arrive.</summary>
    public static LyricLine CharacterTimed(string text, double start = 10, string? reading = null)
    {
        var time = start;
        var words = new List<LyricWord>();
        foreach (var grapheme in ReadingText.Graphemes(text))
        {
            if (grapheme.IsWhitespace) continue;
            words.Add(new LyricWord(grapheme.Text, time, time + 0.4));
            time += 0.4;
        }
        return new LyricLine { Start = start, End = time, Text = text, Words = words, WordTimed = true, Romanization = reading };
    }

    /// <summary>A line with the given words, timed word by word or only as a line.</summary>
    public static LyricLine Line(string[] parts, double start = 0, bool spaced = false, bool wordTimed = true, string? reading = null)
    {
        var time = start;
        var words = new List<LyricWord>();
        foreach (var part in parts)
        {
            words.Add(new LyricWord(part, time, time + 0.5));
            time += 0.5;
        }
        return new LyricLine
        {
            Start = start, End = time, Text = string.Join(spaced ? " " : "", parts), Words = words, WordTimed = wordTimed,
            Romanization = reading,
        };
    }

    public LyricLine LineTimed(string text, double start = 0, string? reading = null) =>
        EstimatedWords.LineTimed(text, start, start + 4, Services.WordBreaker) with { Romanization = reading };

    public List<LyricWord>? Readings(LyricLine line, RomanizationOptions? options = null, IReadOnlyList<LyricLine>? song = null)
    {
        var lines = song ?? [line];
        var index = Math.Max(0, lines.ToList().FindIndex(other => other.Text == line.Text && other.Start == line.Start));
        return Song(lines, options).GetValueOrDefault(index);
    }

    /// <summary>Readings of a whole song by line index.</summary>
    public Dictionary<int, List<LyricWord>> Song(IReadOnlyList<LyricLine> lines, RomanizationOptions? options = null) =>
        Romanizer.Readings(lines, options ?? Options(), TestContext.Current.CancellationToken);

    /// <summary>The readings' texts; empty when the line has none (see <see cref="TextsOrNull"/>).</summary>
    public string[] Texts(LyricLine line, RomanizationOptions? options = null, IReadOnlyList<LyricLine>? song = null) =>
        TextsOrNull(line, options, song) ?? [];

    public string[]? TextsOrNull(LyricLine line, RomanizationOptions? options = null, IReadOnlyList<LyricLine>? song = null) =>
        Readings(line, options, song)?.Select(word => word.Text).ToArray();

    public static string Joined(IEnumerable<string>? texts) => string.Join(" ", (texts ?? []).Where(text => text.Length > 0));

    public static string Letters(string text) => new(text.ToLowerInvariant().Where(char.IsLetter).ToArray());
}
