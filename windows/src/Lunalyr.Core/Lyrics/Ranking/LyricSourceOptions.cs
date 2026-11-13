namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>How matches from the sources without a fixed lead are ordered.</summary>
public enum LyricMatchMode
{
    /// <summary>"Accuracy first": the closest title and artist match, then the configured order. Mac raw value "smart".</summary>
    AccuracyFirst,

    /// <summary>"Source order": the configured order alone. Mac raw value "priority".</summary>
    SourceOrder,
}

/// <summary>Labels and identifiers for <see cref="LyricMatchMode"/>.</summary>
public static class LyricMatchModeInfo
{
    /// <summary>Both modes, in the Mac's order.</summary>
    public static IReadOnlyList<LyricMatchMode> All { get; } = [LyricMatchMode.AccuracyFirst, LyricMatchMode.SourceOrder];

    /// <summary>The Mac's identifier ("smart", "priority"), used in the cache fingerprint and settings.</summary>
    public static string RawValue(this LyricMatchMode mode) => mode == LyricMatchMode.AccuracyFirst ? "smart" : "priority";

    public static bool TryParseRaw(string? raw, out LyricMatchMode mode)
    {
        switch (raw)
        {
            case "smart":
                mode = LyricMatchMode.AccuracyFirst;
                return true;
            case "priority":
                mode = LyricMatchMode.SourceOrder;
                return true;
            default:
                mode = LyricMatchMode.AccuracyFirst;
                return false;
        }
    }

    public static string Title(this LyricMatchMode mode) => mode == LyricMatchMode.AccuracyFirst ? "Accuracy first" : "Source order";

    /// <summary>What the mode decides beyond <see cref="LyricSourceSnapshot.RankingText"/>.</summary>
    public static string Detail(this LyricMatchMode mode) => mode == LyricMatchMode.AccuracyFirst
        ? $"Among your other sources, {mode.Title()} prefers the closest title and artist match, then the order of the list below."
        : $"Among your other sources, {mode.Title()} follows the order of the list below.";
}

/// <summary>
/// What a lookup asks and ranks by. Timing, and the lead of Apple Music's own lyrics and LRCLIB's, are fixed
/// (<see cref="LyricRanking.Outranks"/>); the order of <see cref="Enabled"/>, and <see cref="Mode"/>, only rank the
/// other sources.
/// </summary>
/// <param name="Enabled">The switched-on sources in the configured order.</param>
/// <param name="Mode">How the other sources are ordered.</param>
/// <param name="LocalFolder">The local lyric folder (Lyrimuse), or empty.</param>
/// <param name="LocalVersion">
/// Identity of the configured local folder. Local results are read afresh on every lookup, so this is not part of the
/// cache key.
/// </param>
public sealed record LyricSourceSnapshot(
    IReadOnlyList<LyricSourceId> Enabled,
    LyricMatchMode Mode,
    string LocalFolder = "",
    string LocalVersion = "")
{
    /// <summary>
    /// Bump when matching, ranking or parsing changes, so automatic choices made under the old rules are looked up
    /// again. Saved explicit choices are keyed separately and are unaffected. Kept in step with the Mac:
    /// v11 ranks lyrics for another version (Live, Remix…) below the playing one's; v12 ranks timing faults below
    /// sound timing; v14 (the Mac's number for the same change) ranks Apple Music's lyrics relayed by Lyrics+ as
    /// Apple Music's own, lets a later agreement demote LRCLIB and applies the phrase-token rule (12 Nov 2026); v15
    /// (the Mac's v13, whose numbers Windows had already passed) ranks a language version (English Version…), or lyrics
    /// in another language than the song's, below the playing one's, and searches songs listed under an English title
    /// under their original title too; v16 ranks Apple Music's lyrics as Apple's when a Lyrics+ node names them
    /// "qApple" (13 Nov 2026); v17 also ranks them as Apple's when a node names them "Apple Music", as the Mac does
    /// (parity check of 13 Nov 2026 against the Mac's lookup spec, §2.3): choices cached while such a relay ranked as
    /// plain Lyrics+ are looked up again; v18 compares traditional and simplified Chinese names as the same name and
    /// searches the Chinese catalogues again in simplified script (13 Nov 2026, a Windows addition): a song named in
    /// traditional script, such as "晴天 — 周杰倫", had its choice cached while the catalogues could not match it, and
    /// would keep it for 14 days. v18 also keeps synced lyrics that are only instrumental breaks or credits, ranked with
    /// untimed text, as the Mac does (§4.5; until then such a song found nothing or a sung edition). v19 (the Mac's v16,
    /// next/integration 1cdde21, 13 Nov 2026) keeps Lyrics+ "type":"Line" replies as line timing (until then dropped, so
    /// Apple's line-timed lyrics relayed by Lyrics+ were never found), searches an East Asian artist's song under its
    /// original title when the listed one found only Latin-script lyrics (genre, native-script artist or album, or
    /// MusicBrainz country) and looks that title up in more places, treats closing "role：names" lines and a lone
    /// artist or title header as credits, and leaves credits and headers out of agreement.
    /// </summary>
    public const string CachePolicy = "v19";

    /// <summary>The order a fresh install uses.</summary>
    public static IReadOnlyList<LyricSourceId> RecommendedOrder { get; } =
    [
        LyricSourceId.AppleMusicCache, LyricSourceId.Lyrimuse, LyricSourceId.LyricsPlus, LyricSourceId.Amll,
        LyricSourceId.NetEase, LyricSourceId.Lrclib, LyricSourceId.QQMusic, LyricSourceId.Kugou, LyricSourceId.Kuwo,
        LyricSourceId.LyricsOvh,
    ];

    /// <summary>The snapshot for a configured order and the set of switched-on sources.</summary>
    public static LyricSourceSnapshot From(IEnumerable<LyricSourceId> order, IReadOnlySet<LyricSourceId> enabled,
        LyricMatchMode mode, string localFolder = "", string localVersion = "") =>
        new([.. order.Where(enabled.Contains).Distinct()], mode, localFolder, localVersion);

    /// <summary>
    /// Keys the cached online choice. Only settings that change which online result wins take part: local sources
    /// are always evaluated afresh.
    /// </summary>
    public string Fingerprint =>
        $"{CachePolicy}|{Mode.RawValue()}|{string.Join(',', Enabled.Where(s => !s.IsLocal()).Select(s => s.RawValue()))}";

    /// <summary>Position in the configured order; sources not enabled come last.</summary>
    public int OrderOf(LyricSourceId source)
    {
        for (var i = 0; i < Enabled.Count; i++)
        {
            if (Enabled[i] == source)
            {
                return i;
            }
        }
        return 999;
    }

    public bool IsEnabled(LyricSourceId source) => OrderOf(source) != 999;

    /// <summary>How every match is ranked, in the labels the source settings show.</summary>
    public static string RankingText
    {
        get
        {
            var music = LyricSourceId.AppleMusicCache.Title();
            var lrclib = LyricSourceId.Lrclib.Title();
            return $"Word-by-word and Mixed timing lyrics come first: {music}, then {lrclib}, then your other sources. "
                + $"Line-synced lyrics come next, again with {music} first."
                + " Timed lyrics for another version of the song (Live, Remix, Acoustic, English Version…), or in another language than the song’s, come after timed lyrics for the version playing.";
        }
    }

    public bool Equals(LyricSourceSnapshot? other) =>
        other is not null && Mode == other.Mode && LocalFolder == other.LocalFolder && LocalVersion == other.LocalVersion
        && Enabled.SequenceEqual(other.Enabled);

    public override int GetHashCode() => HashCode.Combine(Mode, LocalFolder, LocalVersion, Enabled.Count);
}
