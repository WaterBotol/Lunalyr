namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>
/// How well one of a service's own hits fits the playing recording, so a source can pick among its hits before the
/// lookup ranks sources against each other. The score never compares different sources.
/// </summary>
/// <remarks>
/// <para>
/// Written fresh for this port. The Mac's scorer (<c>vc_match_score</c>) is Verso code, so it was not read or
/// translated; this follows only the behaviour its callers and tests pin down:
/// </para>
/// <list type="bullet">
/// <item><description>Not a number in, or a wrong artist (0.2), or a known length 80 s off: negative ("not this
/// recording"). Callers drop hits that do not score above zero.</description></item>
/// <item><description>Word timing beats line timing when the names and length agree.</description></item>
/// <item><description>The exact title a few seconds too long (3.5–4 s) still outscores an edition whose length fits but
/// whose title or artist is only close ("Our Song - Remastered" by "Our Artist &amp; Friend", "Our Song (Acoustic)"):
/// names weigh more than a small length difference. The recording check, not the score, then keeps the wrong
/// length out.</description></item>
/// <item><description>A matching album adds a little; LRCLIB takes 6 off an upload whose timing is unsound and 12 off
/// plain text, so those land below sound timing and timing respectively; local files add 32.</description></item>
/// </list>
/// <para>
/// The weights below are this port's own: 50 per unit of title similarity, 25 of artist similarity, a length term
/// of 10 − 1.5 s⁻¹ × difference (floor −25) when both lengths are known, 20 for word timing or 8 for line timing,
/// 5 for the album. A title under 0.6, an artist under 0.5 (0.5 is what a query without an artist gives) or a known
/// length more than 30 s off is rejected.
/// </para>
/// </remarks>
public static class LyricMatchScore
{
    /// <summary>The score of a hit that is not the playing recording.</summary>
    public const double Rejected = -1;

    /// <summary>What a local file adds: it was exported for this library.</summary>
    public const double LocalFileBonus = 32;

    /// <summary>What LRCLIB takes off an upload whose timing has a fault.</summary>
    public const double UnsoundTimingPenalty = 6;

    /// <summary>What LRCLIB takes off an upload with only plain text.</summary>
    public const double PlainTextPenalty = 12;

    public const double MinimumTitle = 0.6;
    public const double MinimumArtist = 0.5;
    public const double MaximumLengthDifference = 30;

    /// <param name="title">Title similarity, 0…1.</param>
    /// <param name="artist">Artist similarity, 0…1 (0.5 when the query names no artist).</param>
    /// <param name="durationDelta">How far the hit's length is from the playing one, in seconds.</param>
    /// <param name="durationKnown">Both lengths are known.</param>
    /// <param name="wordTimed">The hit has word timing.</param>
    /// <param name="lineTimed">The hit has line timing.</param>
    /// <param name="albumMatches">The hit names the playing album.</param>
    public static double Score(double title, double artist, double durationDelta, bool durationKnown, bool wordTimed,
        bool lineTimed, bool albumMatches)
    {
        if (!double.IsFinite(title) || !double.IsFinite(artist) || (durationKnown && !double.IsFinite(durationDelta)))
        {
            return Rejected;
        }
        var delta = Math.Abs(durationKnown ? durationDelta : 0);
        if (title < MinimumTitle || artist < MinimumArtist || delta > MaximumLengthDifference)
        {
            return Rejected;
        }
        var length = durationKnown ? Math.Max(-25, 10 - (1.5 * delta)) : 0;
        var timing = wordTimed ? 20 : lineTimed ? 8 : 0;
        return (50 * title) + (25 * artist) + length + timing + (albumMatches ? 5 : 0);
    }

    /// <summary>The score of a line-timed catalogue hit (no album compared), as QQ, Kugou and Kuwo weigh theirs.</summary>
    public static double HitScore(LyricQuery query, string title, string artist, double? duration)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Score(LyricQuery.Similarity(query.Title, title), query.ArtistScore(artist),
            duration is double d ? Math.Abs(d - query.Duration) : 0, duration is not null && query.Duration > 0,
            wordTimed: false, lineTimed: true, albumMatches: false);
    }
}
