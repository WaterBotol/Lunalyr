namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>
/// What the other sources in the same lookup say about a match: how many give the same words and, for LRCLIB's word
/// timing, whether its timing is the odd one out (<see cref="LyricAgreementRules.Weigh"/>).
/// </summary>
/// <param name="Peers">Other sources whose words match these (capped at 2 when ranking).</param>
/// <param name="TimedCompared">Other timed sources with the same words that this one's timing was compared with.</param>
/// <param name="TimingOutlier">
/// Its timing disagrees with every other timed source with the same words, while two of those agree with each other.
/// </param>
public readonly record struct LyricAgreement(int Peers = 0, int TimedCompared = 0, bool TimingOutlier = false);

/// <summary>One source's best match for a lookup, with the recording the service said it matched.</summary>
/// <param name="Source">The source that answered.</param>
/// <param name="Result">The lyrics.</param>
/// <param name="Title">The matched recording's title, or the search's when the service named none that can be checked.</param>
/// <param name="Artist">The matched recording's artist, likewise.</param>
/// <param name="Duration">The matched recording's length in seconds, when the service gave one.</param>
/// <param name="Score">The provider's own score, used only to pick among one service's hits.</param>
public sealed record LyricCandidate(
    LyricSourceId Source,
    LyricResult Result,
    string Title,
    string Artist,
    double? Duration,
    double Score)
{
    /// <summary>
    /// False when the service did not name the recording it matched in a form that can be checked (not at all, or in
    /// another script); <see cref="Title"/> and <see cref="Artist"/> then repeat the search.
    /// </summary>
    public bool MetadataReported { get; init; } = true;

    /// <summary>What the other sources in the same lookup say about it.</summary>
    public LyricAgreement Agreement { get; init; }

    /// <summary>
    /// Its lyrics are in another language than the song's (<see cref="LyricLanguage"/>), set when candidates are
    /// screened: it ranks as another version.
    /// </summary>
    public bool LanguageMismatch { get; init; }

    /// <summary>
    /// Found by searching under the song's original title (<see cref="OriginalTitles"/>), which shows the song is sung
    /// in that language.
    /// </summary>
    public bool ViaOriginalTitle { get; init; }

    /// <summary>
    /// The source this match ranks as (ranking decision of 12 Nov 2026): Apple Music's own lyrics relayed by Lyrics+
    /// (provenance "Apple via Lyrics+") rank as Apple Music, just after its own (lead 1); every other match ranks as its
    /// <see cref="Source"/>. Only ranking and the source-list note use it: the match is still labelled, enabled and
    /// cached as its own source.
    /// </summary>
    public LyricSourceId RankingSource => LyricSourceRanking.RankingSource(Source, Result.Provenance.Source);

    /// <summary>The Mac identifier of the source.</summary>
    public string Id => Source.RawValue();

    /// <summary>True when any line carries a reading supplied with the lyrics.</summary>
    public bool HasReadings =>
        Result is LyricResult.Synced synced && synced.Lines.Any(line => !string.IsNullOrEmpty(line.Romanization));

    /// <summary>The provenance, with the precision recomputed from the lines for synced lyrics.</summary>
    public LyricProvenance Provenance => Result switch
    {
        LyricResult.Synced synced => new LyricProvenance(LyricTimingRules.Precision(synced.Lines), synced.Provenance.Source),
        _ => Result.Provenance,
    };

    public int LineCount => Result switch
    {
        LyricResult.Synced synced => synced.Lines.Count,
        LyricResult.Plain plain => plain.Lines.Count,
        _ => 0,
    };
}

/// <summary>A candidate that failed the recording check, kept so the user can see why and still choose it.</summary>
public sealed record LyricRejection(LyricCandidate Candidate, string Reason)
{
    public string Id => Candidate.Id;
}

/// <summary>What a lookup found, and how far it got. Progress updates and the final answer share this shape.</summary>
public sealed record LyricLookupReport
{
    /// <summary>What to show. During a progressive lookup this is the current choice.</summary>
    public LyricResult? Result { get; init; }

    /// <summary>Accepted matches in ranking order.</summary>
    public IReadOnlyList<LyricCandidate> Candidates { get; init; } = [];

    /// <summary>A line of status per source, for the source list.</summary>
    public IReadOnlyDictionary<LyricSourceId, string> States { get; init; } = new Dictionary<LyricSourceId, string>();

    /// <summary>A saved explicit choice is showing.</summary>
    public bool Pinned { get; init; }

    /// <summary>The online choice came from the automatic cache.</summary>
    public bool Cached { get; init; }

    /// <summary>Matches that failed the recording check, with the reason.</summary>
    public IReadOnlyList<LyricRejection> Rejected { get; init; } = [];

    /// <summary>Increments each time <see cref="Result"/> changes; 0 means nothing to show yet.</summary>
    public int Revision { get; init; }

    /// <summary>False for progress updates while sources are still answering.</summary>
    public bool IsComplete { get; init; } = true;

    /// <summary>
    /// The network looked unreachable: nothing is cached for the miss and the lookup is worth repeating when the
    /// connection returns.
    /// </summary>
    public bool Offline { get; init; }

    /// <summary>The item reports no song length (a radio station or stream), so no automatic lookup ran.</summary>
    public bool SkippedStream { get; init; }

    /// <summary>Local lyric files that matched the track when the lookup ran.</summary>
    public string? LocalPrint { get; init; }
}
