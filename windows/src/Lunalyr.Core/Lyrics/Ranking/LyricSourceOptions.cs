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
    /// artist or title header as credits, and leaves credits and headers out of agreement. v20 (tester feedback on 0.9.0,
    /// 14 Nov 2026) drops "纯音乐，请欣赏"-style filler lines, and asks Lyrics+ again without the album when the album made
    /// it answer "not found" (Heathens found NetEase's timing instead of Apple's). v21 (the Mac's v17, 16 Nov 2026): word
    /// stamps spread evenly rank as line timing, a timeline shifted to another recording ranks lower, the leading text
    /// timed by the line takes another match's word timing, QQ Music and Kuwo give word timing, and seven sources were
    /// added; choices cached under the earlier ranking are looked up again. v22 (the Mac's v18, 17 Nov 2026): Mixed timing
    /// with under 80% of its sung lines word-timed ranks below word-by-word timing
    /// (<see cref="LyricTimingRules.FullWordTimingShare"/>).
    /// </summary>
    public const string CachePolicy = "v22";

    /// <summary>The order a fresh install uses.</summary>
    public static IReadOnlyList<LyricSourceId> RecommendedOrder { get; } =
    [
        LyricSourceId.AppleMusicCache, LyricSourceId.Lyrimuse, LyricSourceId.Musixmatch, LyricSourceId.LyricsPlus, LyricSourceId.Amll,
        LyricSourceId.NetEase, LyricSourceId.Lrclib, LyricSourceId.QQMusic, LyricSourceId.Kugou, LyricSourceId.Kuwo,
        LyricSourceId.SodaMusic, LyricSourceId.PetitLyrics, LyricSourceId.Deezer, LyricSourceId.Migu, LyricSourceId.Vibe,
        LyricSourceId.Genie, LyricSourceId.LyricFind, LyricSourceId.LyricsOvh,
    ];

    /// <summary>
    /// Sources added after people saved an order, and the source each joins that order after (the Mac's
    /// <c>placedAfter</c>). Others missing from a saved order go at the end.
    /// </summary>
    public static IReadOnlyDictionary<LyricSourceId, LyricSourceId> PlacedAfter { get; } = new Dictionary<LyricSourceId, LyricSourceId>
    {
        [LyricSourceId.Musixmatch] = LyricSourceId.Lyrimuse,
        [LyricSourceId.SodaMusic] = LyricSourceId.Kuwo,
        [LyricSourceId.PetitLyrics] = LyricSourceId.SodaMusic,
        [LyricSourceId.Deezer] = LyricSourceId.PetitLyrics,
        [LyricSourceId.Migu] = LyricSourceId.Deezer,
        [LyricSourceId.Vibe] = LyricSourceId.Migu,
        [LyricSourceId.Genie] = LyricSourceId.Vibe,
        [LyricSourceId.LyricFind] = LyricSourceId.Genie,
    };

    /// <summary>
    /// Listed but off until switched on (the Mac's <c>offByDefault</c>): Soda Music's search answered nothing to new
    /// queries when it was added (16 Nov 2026).
    /// </summary>
    public static IReadOnlySet<LyricSourceId> OffByDefault { get; } = new HashSet<LyricSourceId> { LyricSourceId.SodaMusic };

    /// <summary>
    /// Sources added after people saved their choices: each starts switched on once, for everyone, and is then
    /// remembered as seen, so switching it off again sticks (the Mac's <c>switchedOnOnce</c> and <c>addedLater</c>).
    /// Soda Music is not among them: it stays off until switched on.
    /// </summary>
    public static IReadOnlyList<LyricSourceId> AddedLater { get; } =
    [
        LyricSourceId.Musixmatch, LyricSourceId.PetitLyrics, LyricSourceId.Deezer, LyricSourceId.Migu, LyricSourceId.Vibe,
        LyricSourceId.Genie, LyricSourceId.LyricFind,
    ];

    /// <summary>
    /// The order to show for a saved one (by raw value; null: never saved): every source once, a newer source after the
    /// one it is <see cref="PlacedAfter"/>, any other missing one at the end (the Mac's <c>order(saved:)</c>).
    /// </summary>
    public static List<LyricSourceId> OrderFor(IEnumerable<string>? saved)
    {
        if (saved is null) return [.. RecommendedOrder];
        var order = new List<LyricSourceId>();
        foreach (var raw in saved)
            if (LyricSourceInfo.TryParseRaw(raw, out var id) && !order.Contains(id)) order.Add(id);
        foreach (var id in RecommendedOrder)
        {
            if (order.Contains(id)) continue;
            if (PlacedAfter.TryGetValue(id, out var anchor) && order.IndexOf(anchor) is var at and >= 0) order.Insert(at + 1, id);
            else order.Add(id);
        }
        return order;
    }

    /// <summary>
    /// The switched-on sources for saved ones, each <see cref="AddedLater"/> source not yet in <paramref name="seen"/>
    /// switched on (unless it is <see cref="OffByDefault"/>), and the sources now seen.
    /// </summary>
    public static (List<string> Enabled, List<string> Seen) WithAddedSources(IEnumerable<string> enabled, IEnumerable<string>? seen)
    {
        var on = new SortedSet<string>(enabled, StringComparer.Ordinal);
        var known = new SortedSet<string>(seen ?? [], StringComparer.Ordinal);
        foreach (var id in AddedLater)
        {
            if (!known.Add(id.RawValue())) continue;
            if (!OffByDefault.Contains(id)) on.Add(id.RawValue());
        }
        return ([.. on], [.. known]);
    }

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
            var musixmatch = LyricSourceId.Musixmatch.Title();
            return $"Word-by-word and Mixed timing lyrics come first: {music}, then Apple Music’s lyrics from Lyrics+, then {musixmatch}, then {lrclib}, then your other sources. "
                + $"Line-synced lyrics come next, again with {music} first, then Apple Music’s from Lyrics+, then {musixmatch}."
                + " Timed lyrics for another version of the song (Live, Remix, Acoustic, English Version…), or in another language than the song’s, come after timed lyrics for the version playing.";
        }
    }

    public bool Equals(LyricSourceSnapshot? other) =>
        other is not null && Mode == other.Mode && LocalFolder == other.LocalFolder && LocalVersion == other.LocalVersion
        && Enabled.SequenceEqual(other.Enabled);

    public override int GetHashCode() => HashCode.Combine(Mode, LocalFolder, LocalVersion, Enabled.Count);
}
