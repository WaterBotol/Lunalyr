using System.Globalization;

namespace Lunalyr.Core.Lyrics.Timing;

/// <summary>
/// Tap to Sync Lyrics: the listener presses a key each time a lyric line starts in the song, and the per-song offset
/// (<see cref="SongOffsets"/>) is worked out from those taps. Pure logic; <see cref="LyricTapSession"/> runs a
/// session and <see cref="LyricTapSyncController"/> wires it to the song and the offsets.
/// </summary>
public static class LyricTapSync
{
    /// <summary>
    /// How late a tap lands after the line it marks is heard. Shorter than a single visual reaction on purpose: the
    /// listener taps along to the song again and again, and a rhythm is anticipated.
    /// </summary>
    public const double ReactionTime = 0.15;

    /// <summary>Taps before the offset is first worked out and applied.</summary>
    public const int RequiredTaps = 4;

    /// <summary>Taps after which a session that still doesn't line up gives up.</summary>
    public const int MaximumTaps = 10;

    /// <summary>A tap within this many seconds of a line's start (at the offset being tried) counts as marking that line.</summary>
    public const double Tolerance = 0.35;

    /// <summary>
    /// Lines further than this from where the offset in force puts a tap are not considered for it: Tap to sync
    /// refines timing, it doesn't search the whole song.
    /// </summary>
    public const double SearchWindow = 8.0;

    /// <summary>No first tap in this long ends the session.</summary>
    public const double FirstTapLimit = 30;

    /// <summary>No further tap in this long ends the session.</summary>
    public const double NextTapLimit = 15;

    /// <summary><see cref="SongOffsets"/> keeps offsets within ±30 s.</summary>
    public const double OffsetLimit = SongOffsets.Limit;

    /// <summary>What the taps agreed on.</summary>
    /// <param name="Offset">The song offset (positive shows lyrics earlier), to a hundredth.</param>
    /// <param name="Used">Taps that agreed on it.</param>
    /// <param name="Total">All the taps.</param>
    public readonly record struct Estimate(double Offset, int Used, int Total);

    /// <summary>
    /// The song offset the taps point to, or null when they don't agree.
    /// </summary>
    /// <param name="taps">Each tap's lyric time with no song offset: the playback time of the tap, less the reaction
    /// time, plus the global offset (<see cref="TapTime"/>).</param>
    /// <param name="lineStarts">The start of every sung line.</param>
    /// <param name="prior">The song offset in force while tapping.</param>
    /// <remarks>
    /// Robust to stray taps: every pairing of a tap with a nearby line gives a candidate offset; the one the most taps
    /// agree with (each line counted once, so a double tap is one vote) wins, ties going to the candidate nearest
    /// <paramref name="prior"/>. The result is the median of the agreeing taps' own offsets, so a stray tap moves
    /// nothing. At least <see cref="RequiredTaps"/> taps, and at least three in five of them, must agree.
    /// </remarks>
    public static Estimate? EstimateOffset(IReadOnlyList<double> taps, IReadOnlyList<double> lineStarts, double prior)
    {
        var starts = lineStarts.Where(double.IsFinite).Order().ToArray();
        var finiteTaps = taps.Where(double.IsFinite).ToArray();
        if (finiteTaps.Length < RequiredTaps || starts.Length == 0) return null;
        (int Votes, double Distance, double Residual, List<double> Matches)? best = null;
        foreach (var tap in finiteTaps)
        {
            foreach (var start in starts)
            {
                var candidate = start - tap;
                if (Math.Abs(candidate - prior) > SearchWindow || Math.Abs(candidate) > OffsetLimit) continue;
                var matches = Agreeing(finiteTaps, starts, candidate);
                var votes = matches.Count;
                var distance = Math.Abs(candidate - prior);
                double residual = 0;
                foreach (var match in matches) residual += Math.Abs(match - candidate);
                if (best is { } current)
                {
                    if (votes < current.Votes) continue;
                    if (votes == current.Votes)
                    {
                        // Clearly nearer the offset in force wins; otherwise the tighter fit.
                        if (distance > current.Distance + Tolerance) continue;
                        if (Math.Abs(distance - current.Distance) <= Tolerance && residual >= current.Residual) continue;
                    }
                }
                best = (votes, distance, residual, matches);
            }
        }
        if (best is not { } winner || winner.Votes < RequiredTaps || winner.Votes * 5 < finiteTaps.Length * 3
            || Median(winner.Matches) is not { } middle)
            return null;
        var offset = Math.Round(Math.Clamp(middle, -OffsetLimit, OffsetLimit) * 100, MidpointRounding.AwayFromZero) / 100;
        return new Estimate(offset, winner.Votes, finiteTaps.Length);
    }

    /// <summary>
    /// For each line some tap marks at <paramref name="offset"/> (within <see cref="Tolerance"/>), that tap's own
    /// offset: the closest tap when several mark the same line. In line order.
    /// </summary>
    public static List<double> Agreeing(IReadOnlyList<double> taps, IReadOnlyList<double> starts, double offset)
    {
        var byLine = new SortedDictionary<int, double>();
        foreach (var tap in taps)
        {
            if (Nearest(starts, tap + offset) is not { } index) continue;
            var own = starts[index] - tap;
            if (Math.Abs(own - offset) > Tolerance) continue;
            if (byLine.TryGetValue(index, out var kept) && Math.Abs(kept - offset) <= Math.Abs(own - offset)) continue;
            byLine[index] = own;
        }
        return [.. byLine.Values];
    }

    /// <summary>The index of the value in sorted <paramref name="values"/> nearest <paramref name="target"/>.</summary>
    public static int? Nearest(IReadOnlyList<double> values, double target)
    {
        if (values.Count == 0) return null;
        int low = 0, high = values.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (values[mid] < target) low = mid + 1;
            else high = mid;
        }
        if (low == 0) return 0;
        if (low == values.Count) return values.Count - 1;
        return target - values[low - 1] <= values[low] - target ? low - 1 : low;
    }

    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    /// <summary>A tap's lyric time with no song offset.</summary>
    public static double TapTime(double playbackTime, double globalOffset, double reaction = ReactionTime) =>
        playbackTime - reaction + globalOffset;

    /// <summary>The starts of the sung lines (no instrumental breaks or empty lines).</summary>
    public static double[] LineStarts(IReadOnlyList<LyricLine> lines) =>
        [.. lines.Where(l => !l.IsInstrumental && !string.IsNullOrWhiteSpace(l.Text)).Select(l => l.Start)];
}

/// <summary>An offset as the timing controls show it: "+0.25 s", "−0.40 s" (a true minus sign), "0.00 s".</summary>
public static class TimingNudge
{
    public static string Format(double seconds)
    {
        if (seconds == 0) return "0.00 s";
        var magnitude = Math.Abs(seconds).ToString("0.00", CultureInfo.InvariantCulture);
        // A value that rounds to zero keeps its sign, as printf's %+.2f does.
        return (double.IsNegative(seconds) ? "−" : "+") + magnitude + " s";
    }

    /// <summary>"Lyrics 0.25 seconds earlier", "No offset".</summary>
    public static string Spoken(double seconds)
    {
        if (seconds == 0) return "No offset";
        return string.Create(CultureInfo.InvariantCulture,
            $"Lyrics {Math.Abs(seconds):0.00} seconds {(seconds > 0 ? "earlier" : "later")}");
    }
}
