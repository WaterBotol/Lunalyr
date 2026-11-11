namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>
/// Whether this PC has a usable network path. A lookup that found nothing while offline is not remembered as a miss,
/// and is worth repeating once a path returns. The platform layer implements it (Windows network list events).
/// </summary>
public interface ILyricConnectivity
{
    bool IsOnline { get; }

    /// <summary>Raised when a usable path returns after being lost.</summary>
    event EventHandler? Restored;
}

/// <summary>
/// Asks one source for its best match, as a ranked candidate. The lookup calls it once per source (twice for Apple
/// Music's second look) and cancels <paramref name="cancellationToken"/> when the source is stopped or times out.
/// <see cref="Pipeline.LyricSourceSet"/> builds one from the sources (<see cref="Sources.ILyricSource"/>): fetch,
/// parse, flag credits, label.
/// </summary>
/// <remarks>
/// The lookup calls this on its own flow, so it must return promptly (do any blocking work after the first await, or
/// on the thread pool); otherwise it holds up the other sources, the grace period and its own time limit.
/// </remarks>
/// <returns>The source's best match (it may still fail the recording check), or null when it has nothing.</returns>
/// <exception cref="Exception">
/// Any failure means the source could not answer, so the miss is not remembered. Throw
/// <see cref="Sources.LyricSourceException"/> with <see cref="Sources.LyricSourceFailure.Offline"/> when the network
/// is down (or let a socket error through).
/// </exception>
public delegate ValueTask<LyricCandidate?> LyricSourceFetch(LyricSourceId source, LyricQuery query, string localFolder,
    CancellationToken cancellationToken);

/// <summary>Builds candidates from what a provider fetched, so providers label and check names consistently.</summary>
public static class LyricCandidates
{
    /// <summary>
    /// A candidate for parsed lyrics, ranked and labelled by the recording the service reported. The provenance's
    /// precision is recomputed from the lines.
    /// </summary>
    /// <param name="raw">What the provider fetched, with the names the service reported.</param>
    /// <param name="parsed">The parsed lyrics (<see cref="Parsing.LyricParser"/> turns <see cref="RawLyrics"/> into these).</param>
    /// <param name="query">The lookup.</param>
    /// <param name="score">The provider's own score, used only to pick among its hits.</param>
    /// <param name="sourceLabel">The provenance's source label; the parsed result's (the Mac providers' labels) when null.</param>
    /// <param name="namesMayBeTransliterated">
    /// The Mac's rule for Lyrics+ only: its search used the library's names, so a reported name that cannot be checked
    /// (<see cref="LyricQuery.Checkable"/>, e.g. in another script) is replaced by the search's and only the length is
    /// checked. Off (every other source), reported names are used as given and a missing one falls back to the
    /// search's; <see cref="LyricCandidate.MetadataReported"/> is then false only when the service named nothing.
    /// </param>
    public static LyricCandidate FromRaw(RawLyrics raw, LyricResult parsed, LyricQuery query, double score, string? sourceLabel = null,
        bool namesMayBeTransliterated = false)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(query);
        string? title, artist;
        bool reported;
        if (namesMayBeTransliterated)
        {
            title = LyricQuery.Checkable(raw.ReportedTitle, query.Title);
            artist = LyricQuery.Checkable(raw.ReportedArtist, query.Artist);
            reported = title is not null && artist is not null;
        }
        else
        {
            title = raw.ReportedTitle;
            artist = raw.ReportedArtist;
            reported = title is not null || artist is not null;
        }
        var label = sourceLabel ?? parsed.Provenance.Source;
        LyricResult result = parsed switch
        {
            LyricResult.Synced synced => new LyricResult.Synced(synced.Lines, new LyricProvenance(LyricTimingRules.Precision(synced.Lines), label)),
            LyricResult.Plain plain => new LyricResult.Plain(plain.Lines, new LyricProvenance(plain.Provenance.Precision, label)),
            _ => parsed,
        };
        return new LyricCandidate(raw.Source, result, title ?? query.Title, artist ?? query.Artist, raw.ReportedDuration, score)
        {
            MetadataReported = reported,
        };
    }
}
