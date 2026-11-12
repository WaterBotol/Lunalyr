using Lunalyr.Core.Lyrics;
using static Lunalyr.Shell.Demo.LyricBuilder;

namespace Lunalyr.Shell.Demo;

/// <summary>One invented song for <c>--demo</c>: what a player would report, its cover and its word-timed lyrics.</summary>
public sealed record DemoSong(string Title, string Artist, string Album, double Duration, LyricResult Lyrics, byte[] Cover);

/// <summary>
/// The songs <c>--demo</c> plays in a loop. Both are invented (not from any real song). Each opens with an intro long
/// enough for the full opening title (its title, artist, album and cover; the Japanese title with its reading). The
/// English one has production credits (shown by the credits layer once the opening title has gone), a held note,
/// backing vocals, a duet answer held under the next line, an instrumental break and a line long enough to wrap; the
/// Japanese one is full of kanji, partly unspaced and partly spaced by phrase, so the readings (worked out by the app
/// as for any song, word by word) show under each phrase.
/// </summary>
public static class DemoSongs
{
    public const string Artist = "The Lunalyr Demo Band";
    public const string Album = "Invented Songs";

    /// <summary>The Japanese song's title (its reading, "Yuugure no Mado", shows under it on the opening title).</summary>
    public const string JapaneseTitle = "夕暮れの窓";

    /// <summary>The intro before each song's first line: the English song's first line is at 9.5 s, the Japanese at 6.5 s.</summary>
    public const double EnglishIntro = 8, JapaneseIntro = 5;

    /// <summary>Song times worth a look in the English song (for screenshots).</summary>
    public static class EnglishMoments
    {
        public const double OpeningTitle = 4, Backing = 14.2, Duet = 19.2, HeldNote = 24.5, Break = 29, Wrapping = 37.2, Last = 41.4;
    }

    /// <summary>Song times worth a look in the Japanese song (for screenshots).</summary>
    public static class JapaneseMoments
    {
        public const double OpeningTitle = 3.5, First = 9.2, Second = 13.0, Third = 17.5, HeldNote = 25.8;
    }

    /// <summary>The lines moved later by <paramref name="intro"/> seconds.</summary>
    private static Sung[][] After(double intro, params Sung[][] lines) =>
        [.. lines.Select(line => line.Select(word => word with { Start = word.Start + intro, End = word.End + intro }).ToArray())];

    private static readonly Lazy<IReadOnlyList<DemoSong>> All = new(Make);

    /// <summary>The songs, in the order they play.</summary>
    public static IReadOnlyList<DemoSong> Songs => All.Value;

    private static IReadOnlyList<DemoSong> Make() => [English(), Japanese()];

    private static DemoSong English()
    {
        Sung[] first = [.. Even(1.5, 0.42, 0.38, "We", "keep", "dreaming", "under"), new("neon", 3.2, 3.55), new("rain", 3.6, 4.7)];
        Sung[] second =
        [
            new("Hold", 5.1, 5.5), new("me", 5.5, 5.8),
            // In parentheses: the compositions lift them out as backing vocals, as they do for real lyrics.
            new("(oh", 5.8, 6.2), new("oh)", 6.2, 6.6),
            new("through", 6.6, 7.0), new("the", 7.0, 7.2), new("night", 7.2, 8.1),
        ];
        Sung[] duet = [.. Even(8.5, 0.8, 0.8, "I", "will", "wait", "for"), new("you", 11.7, 12.9)];
        Sung[] answer = [new("Yeah", 10.5, 11.1), new("yeah", 11.1, 11.6)];
        Sung[] held = [new("Stay", 13.5, 14.0), new("with", 14.0, 14.3), new("me", 14.3, 17.9)];
        Sung[] wrapping = Even(24.5, 0.36, 0.34, "Every", "little", "window", "on", "the", "boulevard", "is", "singing",
                               "back", "the", "colours", "of", "the", "morning", "light");
        Sung[] last = Even(31.0, 0.5, 0.45, "And", "the", "river", "carries", "us", "home");
        const double duration = 37 + EnglishIntro;
        var parsed = (LyricResult.Synced)Parse(Core.Lyrics.LyricFormat.Ttml,
            Ttml("en", After(EnglishIntro, first, second, duet, answer, held, wrapping, last)), duration);
        // Production credits, as lyric services publish them ahead of the lyric: shown by the credits layer once the
        // opening title has gone, never as lead lines.
        LyricLine[] credits =
        [
            new() { Start = 0, End = 0.01, Text = "Produced by: Mira Okafor", IsInstrumental = true, Credit = true },
            new() { Start = 0, End = 0.01, Text = "Written by: Jun Ishida & Lea Brandt", IsInstrumental = true, Credit = true },
        ];
        var lyrics = new LyricResult.Synced([.. credits, .. parsed.Lines], parsed.Provenance);
        var cover = DemoCover.Paint(new(232, 58, 160), new(74, 40, 176), new(22, 160, 172), new(250, 140, 52), new(255, 120, 200));
        return new DemoSong("Neon Harbour", Artist, Album, duration, lyrics, cover);
    }

    private static DemoSong Japanese()
    {
        static Sung W(string text, double start, double end) => new(text, start, end, Space: false);
        Sung[] first = [W("夕焼け", 1.5, 2.1), W("の", 2.1, 2.3), W("帰り道", 2.4, 3.0), W("を", 3.0, 3.2), W("歩いた", 3.3, 4.5)];
        Sung[] second =
        [
            W("窓辺", 4.9, 5.4), W("に", 5.4, 5.6), W("座って", 5.7, 6.4), W("海", 6.5, 6.9), W("を", 6.9, 7.1),
            W("見ていた", 7.2, 8.5),
        ];
        // Spaced by phrase, as many lyric files write Japanese: each phrase carries its own reading.
        static Sung P(string text, double start, double end) => new(text, start, end, Space: true);
        Sung[] third = [W("星空", 9.2, 9.9), P("の", 9.9, 10.1), W("下", 10.2, 10.7), P("で", 10.7, 10.9), W("君", 11.0, 11.5), P("を", 11.5, 11.7), W("待つ", 11.8, 12.9)];
        Sung[] fourth = [W("明日", 13.5, 14.1), P("も", 14.1, 14.3), P("また", 14.4, 14.9), W("会える", 15.0, 15.7), W("よう", 15.7, 16.4), W("に", 16.4, 17.0)];
        Sung[] fifth = [W("夏", 18.0, 18.5), W("の", 18.5, 18.7), W("終わり", 18.8, 19.5), W("に", 19.5, 19.7), W("歌おう", 19.8, 21.6)];
        Sung[] sixth = [W("遠い", 23.0, 23.6), W("街", 23.7, 24.3), W("の", 24.3, 24.5), W("灯り", 24.6, 25.4), W("が", 25.4, 25.6), W("揺れる", 25.7, 27.0)];
        const double duration = 30 + JapaneseIntro;
        var lyrics = Parse(Core.Lyrics.LyricFormat.Ttml, Ttml("ja", After(JapaneseIntro, first, second, third, fourth, fifth, sixth)), duration);
        var cover = DemoCover.Paint(new(250, 152, 60), new(240, 108, 140), new(30, 42, 112), new(112, 60, 152), new(255, 214, 96));
        return new DemoSong(JapaneseTitle, Artist, Album, duration, lyrics, cover);
    }
}
