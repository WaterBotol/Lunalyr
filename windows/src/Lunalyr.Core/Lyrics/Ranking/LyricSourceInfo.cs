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
        LyricSourceId.Musixmatch => "musixmatch",
        LyricSourceId.SodaMusic => "sodaMusic",
        LyricSourceId.PetitLyrics => "petitLyrics",
        LyricSourceId.Deezer => "deezer",
        LyricSourceId.Migu => "migu",
        LyricSourceId.Vibe => "vibe",
        LyricSourceId.Genie => "genie",
        LyricSourceId.LyricFind => "lyricFind",
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
        LyricSourceId.Musixmatch => "Musixmatch",
        LyricSourceId.SodaMusic => "Soda Music",
        LyricSourceId.PetitLyrics => "PetitLyrics",
        LyricSourceId.Deezer => "Deezer",
        LyricSourceId.Migu => "Migu Music",
        LyricSourceId.Vibe => "VIBE",
        LyricSourceId.Genie => "Genie Music",
        LyricSourceId.LyricFind => "LyricFind (YouTube Music)",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    /// <summary>The timing the source can deliver, for the source list.</summary>
    public static string TimingDescription(this LyricSourceId source) => source switch
    {
        LyricSourceId.AppleMusicCache => "Word or line timing from Apple Music",
        LyricSourceId.LyricsPlus => "Word and syllable timing",
        LyricSourceId.NetEase => "Word or line timing",
        LyricSourceId.QQMusic or LyricSourceId.Kuwo or LyricSourceId.Kugou => "Word or line timing",
        LyricSourceId.Lrclib or LyricSourceId.Musixmatch => "Word or line timing",
        LyricSourceId.SodaMusic or LyricSourceId.Deezer or LyricSourceId.Migu => "Word or line timing",
        LyricSourceId.PetitLyrics => "Word timing",
        LyricSourceId.Vibe or LyricSourceId.Genie or LyricSourceId.LyricFind => "Line timing",
        LyricSourceId.LyricsOvh => "Plain text fallback",
        LyricSourceId.Amll => "Word and syllable timing",
        LyricSourceId.Lyrimuse => "LRC, TTML or YRC files, with readings and translations",
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
        // These services time lines only (the Mac's dc56f8a).
        LyricSourceId.Vibe or LyricSourceId.Genie or LyricSourceId.LyricFind => 1,
        // Kuwo's lrcx lyrics are word-timed; its line list is only the fallback (the Mac's 7ddb08a).
        _ => 3,
    };
}
