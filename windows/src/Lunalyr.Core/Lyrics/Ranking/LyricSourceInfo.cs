namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>What ranking needs to know about each lyric source.</summary>
public static class LyricSourceInfo
{
    /// <summary>Every source, in declaration order.</summary>
    public static IReadOnlyList<LyricSourceId> All { get; } = Enum.GetValues<LyricSourceId>();

    /// <summary>
    /// The Mac's identifier for the source ("qqMusic", "lyricsOVH"…). It feeds the cache fingerprint and saved
    /// entries, so it is an explicit table rather than the enum's name.
    /// </summary>
    public static string RawValue(this LyricSourceId source) => source switch
    {
        LyricSourceId.AppleMusicCache => "appleMusicCache",
        LyricSourceId.LyricsPlus => "lyricsPlus",
        LyricSourceId.NetEase => "netEase",
        LyricSourceId.QQMusic => "qqMusic",
        LyricSourceId.Kugou => "kugou",
        LyricSourceId.Lrclib => "lrclib",
        LyricSourceId.Lyrimuse => "lyrimuse",
        LyricSourceId.Kuwo => "kuwo",
        LyricSourceId.LyricsOvh => "lyricsOVH",
        LyricSourceId.Amll => "amll",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    /// <summary>The source with the Mac identifier <paramref name="raw"/> (exact, case-sensitive), if any.</summary>
    public static bool TryParseRaw(string? raw, out LyricSourceId source)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.RawValue(), raw, StringComparison.Ordinal))
            {
                source = candidate;
                return true;
            }
        }
        source = default;
        return false;
    }

    /// <summary>The name shown for the source, and the source in each of its results' provenance.</summary>
    public static string Title(this LyricSourceId source) => source switch
    {
        LyricSourceId.AppleMusicCache => "Apple Music",
        LyricSourceId.LyricsPlus => "Lyrics+ (KPoe)",
        LyricSourceId.NetEase => "NetEase Cloud Music",
        LyricSourceId.QQMusic => "QQ Music",
        LyricSourceId.Kugou => "Kugou Music",
        LyricSourceId.Lrclib => "LRCLIB",
        LyricSourceId.Kuwo => "Kuwo Music",
        LyricSourceId.LyricsOvh => "lyrics.ovh",
        LyricSourceId.Amll => "AMLL TTML DB",
        LyricSourceId.Lyrimuse => "Lyrimuse local files",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    /// <summary>The timing the source can deliver, for the source list.</summary>
    public static string TimingDescription(this LyricSourceId source) => source switch
    {
        LyricSourceId.AppleMusicCache => "Word or line timing from Apple Music",
        LyricSourceId.LyricsPlus => "Word and syllable timing",
        LyricSourceId.NetEase => "Word or line timing",
        LyricSourceId.QQMusic or LyricSourceId.Kuwo => "Line timing",
        LyricSourceId.Kugou => "Word or line timing",
        LyricSourceId.Lrclib => "Word or line timing",
        LyricSourceId.LyricsOvh => "Plain text fallback",
        LyricSourceId.Amll => "Word / syllable timing",
        LyricSourceId.Lyrimuse => "LRC / TTML / YRC + readings, translations",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    /// <summary>
    /// Read from this PC rather than a lyric service. Local sources are evaluated on every lookup and never enter the
    /// automatic cache.
    /// </summary>
    public static bool IsLocal(this LyricSourceId source) => source is LyricSourceId.AppleMusicCache or LyricSourceId.Lyrimuse;

    /// <summary>
    /// Best timing tier the source can deliver (<see cref="LyricTimingRules.TimingTier"/>), so a lookup can settle
    /// without waiting for sources that cannot win.
    /// </summary>
    public static int TimingCeiling(this LyricSourceId source) => source switch
    {
        LyricSourceId.LyricsOvh => 0,
        LyricSourceId.Kuwo => 1, // Kuwo's line list is converted to line-level LRC.
        _ => 3,
    };
}
