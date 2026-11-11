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
    /// sound timing.
    /// </summary>
    public const string CachePolicy = "v12";

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
                + " Timed lyrics for another version of the song (Live, Remix, Acoustic…) come after timed lyrics for the version playing.";
        }
    }

    public bool Equals(LyricSourceSnapshot? other) =>
        other is not null && Mode == other.Mode && LocalFolder == other.LocalFolder && LocalVersion == other.LocalVersion
        && Enabled.SequenceEqual(other.Enabled);

    public override int GetHashCode() => HashCode.Combine(Mode, LocalFolder, LocalVersion, Enabled.Count);
}
