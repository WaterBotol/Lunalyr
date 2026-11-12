namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>
/// Which source a match ranks as. Ranking decision of 12 Nov 2026 (docs/LYRIC-MATCHING-NOTES.md): when Lyrics+ relays
/// Apple Music's own lyrics (its node names the upstream "Apple"), the match ranks exactly as Apple Music's would, at
/// every timing tier, in agreement and in the configured order. It is still listed, shown and cached as Lyrics+.
/// </summary>
public static class LyricSourceRanking
{
    /// <summary>What a Lyrics+ provenance label ends with, after the upstream's name ("Apple via Lyrics+").</summary>
    public const string RelaySuffix = " via Lyrics+";

    /// <summary>The upstream name a Lyrics+ node gives for Apple Music's lyrics.</summary>
    public const string AppleUpstream = "Apple";

    /// <summary>
    /// Whether a Lyrics+ node's upstream name means Apple Music's lyrics: "Apple" or "Apple Music" in any case, as on
    /// the Mac (<c>isAppleRelay</c>, spec §2.3), and "qApple", which one node gives for the very same lyrics (same
    /// lines, same song parts; checked live 13 Nov 2026; a Windows addition the Mac lacks). "Applesauce", "Musixmatch",
    /// "Spotify", "QQ" and "Lyrics+" are other upstreams.
    /// </summary>
    public static bool IsAppleUpstream(string? upstream) =>
        string.Equals(upstream, AppleUpstream, StringComparison.OrdinalIgnoreCase)
        || string.Equals(upstream, "Apple Music", StringComparison.OrdinalIgnoreCase)
        || string.Equals(upstream, "qApple", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The upstream name a relay is labelled with: "Apple" for a node's "qApple" (or "apple"), so the same lyrics carry
    /// one label (and one per-song offset) whichever node answered. "Apple Music" stays as the node wrote it, as the
    /// Mac labels it; every other name is kept as given.
    /// </summary>
    public static string RelayLabel(string upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        return IsAppleUpstream(upstream) && !string.Equals(upstream, "Apple Music", StringComparison.OrdinalIgnoreCase)
            ? AppleUpstream
            : upstream;
    }

    /// <summary>
    /// The upstream a Lyrics+ match was relayed from, from its provenance label ("Apple via Lyrics+" → "Apple"), or
    /// null when the label names none. The label is kept with saved lyrics, so a cached match ranks the same.
    /// </summary>
    public static string? RelayUpstream(LyricSourceId source, string provenanceSource)
    {
        ArgumentNullException.ThrowIfNull(provenanceSource);
        return source == LyricSourceId.LyricsPlus && provenanceSource.EndsWith(RelaySuffix, StringComparison.Ordinal)
            ? provenanceSource[..^RelaySuffix.Length].Trim()
            : null;
    }

    /// <summary>
    /// The source a match from <paramref name="source"/> labelled <paramref name="provenanceSource"/> ranks as:
    /// Apple Music for Apple's lyrics relayed by Lyrics+, otherwise the source itself.
    /// </summary>
    public static LyricSourceId RankingSource(LyricSourceId source, string provenanceSource) =>
        IsAppleUpstream(RelayUpstream(source, provenanceSource))
            ? LyricSourceId.AppleMusicCache
            : source;

    /// <summary>
    /// The highest a source that has not answered yet could rank as: Lyrics+ might relay Apple Music's lyrics, so it
    /// could rank as Apple Music.
    /// </summary>
    public static LyricSourceId BestRankingSource(LyricSourceId source) =>
        source == LyricSourceId.LyricsPlus ? LyricSourceId.AppleMusicCache : source;
}
