using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Timing;

/// <summary>What the app does with a lookup report (<see cref="LyricSwap.Reception"/>).</summary>
public enum LyricReception
{
    /// <summary>Nothing new to show.</summary>
    Ignore,

    /// <summary>Show its result now.</summary>
    Show,

    /// <summary>Swap its result in once the line being sung ends.</summary>
    SwapAtBoundary,
}

/// <summary>
/// When a better match found during a lookup replaces the lyrics on screen: the first result shows at once, and each
/// later one (an upgrade, or LRCLIB demoted by a later agreement) swaps in between lines, so a line is never re-timed
/// while it is sung. Ported from the Mac's <c>LyricsViewModel.reception</c> and <c>lineBeingSung</c> (Lunalyr's own code).
/// </summary>
public static class LyricSwap
{
    /// <summary>How often the app looks whether the line being sung has ended.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(150);

    /// <summary>The longest a swap waits for a line to end; it is made then anyway.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(12);

    /// <summary>
    /// What to do with <paramref name="update"/>: a lookup's first result shows at once and each later one swaps at a
    /// boundary. A rerun for lyrics already on screen shows nothing until it completes (its early choice could be worse
    /// than what is showing), then swaps at a boundary.
    /// </summary>
    /// <param name="applied">The revision already applied (0 for none).</param>
    /// <param name="rerun">The lookup runs again for lyrics already on screen.</param>
    public static LyricReception Reception(LyricLookupReport update, int applied, bool rerun)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (rerun && !update.IsComplete)
        {
            return LyricReception.Ignore;
        }
        if (!(update.Revision > applied || (update.IsComplete && applied == 0)))
        {
            return LyricReception.Ignore;
        }
        return applied > 0 || (rerun && update.Result is not null) ? LyricReception.SwapAtBoundary : LyricReception.Show;
    }

    /// <summary>
    /// The line being sung at <paramref name="time"/>, or null in a gap, an instrumental break or before the first
    /// line. When this changes (into a gap or on to the next line) a boundary has passed and the lyrics can be swapped.
    /// </summary>
    public static int? LineBeingSung(IReadOnlyList<LyricLine> lines, LyricLineIndex index, double time)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(index);
        if (lines.Count == 0 || !double.IsFinite(time) || index.LineAt(time) is not { } found || found >= lines.Count)
        {
            return null;
        }
        var line = lines[found];
        return time < line.End && !line.IsInstrumental ? found : null;
    }
}
