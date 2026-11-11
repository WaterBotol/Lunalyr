namespace Lunalyr.Core.Lyrics.Timing;

/// <summary>Synced lines ready to show, with whether long pauses in them are inferred.</summary>
public sealed record PreparedTiming(IReadOnlyList<LyricLine> Lines, bool InfersPauses)
{
    /// <summary>When singing is heard, for the instrumental-break cue (work it out once per result, not per frame).</summary>
    public VocalWindow[] VocalWindows() => LyricGapTiming.Windows(Lines, InfersPauses);
}

/// <summary>
/// "Treat long pauses as instrumental": the one setting that covers both estimates of when singing stops.
/// </summary>
/// <remarks>
/// QQ Music's lyrics are split by <see cref="QQGapRepair"/>, which judges every line end itself, and other line-only
/// lyrics (LRCLIB, NetEase's LRC, Kugou, Kuwo, local files, imports) have the breaks after their lines inferred by
/// <see cref="LyricGapTiming"/>. Estimated word timing is worked out afresh for the song as a whole, so only an
/// abnormally long window (the last line, or a line before a long break, judged against the song's own pace) is
/// shortened (Mac Build 64, <c>next/lyric-rush</c>). Word timing and supplied line ends are never shortened.
/// <para>
/// This is the timing part of the Mac's <c>LyricsViewModel.presentation</c>. The lyrics model composes it with the
/// steps owned by other workstreams: credit flagging before it, then word-timing validation and
/// <see cref="LyricLineIndex.RemovingMixedTimingCopies"/> after it.
/// </para>
/// </remarks>
public static class LyricPauseTiming
{
    /// <summary>Whether a source's lyrics are QQ Music's (by its provenance name).</summary>
    public static bool IsQQ(string source) => source.Contains("qq", StringComparison.OrdinalIgnoreCase);

    /// <param name="lines">The synced lines (credits already flagged).</param>
    /// <param name="source">The provenance source name, e.g. "QQ Music", "LRCLIB".</param>
    /// <param name="treatLongPausesAsInstrumental">The setting.</param>
    /// <param name="distribute">The word distributor (defaults to <see cref="EstimatedWords.Distribute"/>).</param>
    public static PreparedTiming Prepare(IReadOnlyList<LyricLine> lines, string source, bool treatLongPausesAsInstrumental,
                                         WordDistributor? distribute = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(source);
        var qq = IsQQ(source);
        IReadOnlyList<LyricLine> split = treatLongPausesAsInstrumental && qq ? QQGapRepair.Repair(lines, distribute) : lines;
        var estimated = LyricGapTiming.EstimatingWords(split, treatLongPausesAsInstrumental, distribute);
        return new PreparedTiming(estimated, treatLongPausesAsInstrumental && !qq);
    }
}
