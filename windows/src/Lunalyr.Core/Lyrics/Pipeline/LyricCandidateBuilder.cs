using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Pipeline;

/// <summary>
/// Turns what a source fetched into a ranked candidate: parse (the playing track's length ends the last line, not the
/// length the service reported), flag production credits, then label and name it as the Mac's providers do.
/// </summary>
public static class LyricCandidateBuilder
{
    /// <summary>The candidate for <paramref name="raw"/>, or null when it holds no lyrics.</summary>
    /// <param name="raw">What a source returned.</param>
    /// <param name="query">The lookup; its duration, when known, is the track length the parsers use.</param>
    /// <param name="options">Languages and reading detection; its track duration is replaced by the query's.</param>
    public static LyricCandidate? Build(RawLyrics raw, LyricQuery query, LyricParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(query);
        var parse = (options ?? new LyricParseOptions()) with { TrackDuration = query.Duration > 0 ? query.Duration : null };
        var parsed = LyricParser.Parse(raw, parse);
        switch (parsed)
        {
            case LyricResult.Synced synced:
                var lines = LyricCredits.Flagging(synced.Lines, query.Title, query.Artist);
                // Credits alone are not lyrics.
                if (!lines.Any(line => !line.IsInstrumental))
                {
                    return null;
                }
                parsed = synced with { Lines = lines };
                break;
            case LyricResult.Plain plain when plain.Lines.All(string.IsNullOrWhiteSpace):
                return null;
            case null:
                return null;
        }
        return LyricCandidates.FromRaw(raw, parsed, query, Score(raw, parsed, query),
            namesMayBeTransliterated: raw.Source == LyricSourceId.LyricsPlus);
    }

    /// <summary>
    /// How well the reported recording fits (<see cref="LyricMatchScore"/>). Each source already picked its own best hit,
    /// so this only orders a source's offers and is shown for diagnosis; it never ranks sources against each other.
    /// </summary>
    private static double Score(RawLyrics raw, LyricResult parsed, LyricQuery query)
    {
        var length = raw.ReportedDuration;
        var timing = LyricTimingRules.TimingTier(parsed);
        return LyricMatchScore.Score(query.TitleSimilarity(raw.ReportedTitle ?? query.Title), query.ArtistScore(raw.ReportedArtist ?? query.Artist),
            length is double l ? Math.Abs(l - query.Duration) : 0, length is > 0 && query.Duration > 0,
            wordTimed: timing >= 2, lineTimed: timing >= 1, albumMatches: false);
    }
}
