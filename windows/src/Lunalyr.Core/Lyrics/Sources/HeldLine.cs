// Ported from the Mac app's AdditionalLyricsProviders.swift (HeldLine and wordTimed, 7ddb08a; Lunalyr's own lines).

using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Keeps the first line-timed answer while a source's later hits are tried for word timing. Word timing from a hit
/// that is not the playing recording does not displace the right recording's line timing.
/// </summary>
internal sealed class HeldLine
{
    public RawLyrics? Lyrics { get; private set; }

    private bool _namesRecording;

    public void Hold(RawLyrics? found, bool namesRecording)
    {
        if (Lyrics is not null || found is null) return;
        Lyrics = found;
        _namesRecording = namesRecording;
    }

    /// <summary>Whether something from a hit that does (or doesn't) name the recording may replace what is held.</summary>
    public bool YieldsTo(bool namesRecording) => Lyrics is null || namesRecording || !_namesRecording;

    /// <summary>Whether these lyrics parse with word timing on at least one line.</summary>
    public static bool WordTimed(RawLyrics? lyrics) =>
        lyrics is not null && LyricParser.Parse(lyrics) is LyricResult.Synced synced && synced.Lines.Any(line => line.WordTimed);

    /// <summary>Whether a hit names the playing recording (<see cref="LyricRanking.NamesRecording"/>).</summary>
    public static bool Names(LyricSourceId source, string title, string artist, double? duration, LyricQuery query) =>
        LyricRanking.NamesRecording(source, title, artist, duration, query);
}
