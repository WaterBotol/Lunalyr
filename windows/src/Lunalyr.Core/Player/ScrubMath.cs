// Ported from the Mac's Sources/Player/ScrubMath.swift (Lunalyr's own code, no Verso lines) and the text key of
// CoverLookup.normal in Sources/Player/DesktopPlayerView.swift.

using System.Globalization;
using System.Text;
using Lunalyr.Core.Lyrics;

namespace Lunalyr.Core.Player;

/// <summary>
/// A lyric line as the seek bar sees it: in the song's own time (the lyric offsets already taken off), so a line start
/// is where a seek must land.
/// </summary>
public readonly record struct ScrubLine(double Start, double End, string Text, string? Romanized, bool IsInstrumental)
{
    /// <summary>
    /// The lines of a synced lyric, in song time. Credits are left out: they are not sung and never a seek target.
    /// <paramref name="offset"/> is what the lyric layer adds to the song's time (global plus per-song offset).
    /// </summary>
    public static ScrubLine[] Lines(IReadOnlyList<LyricLine> lyric, double offset, IReadOnlyDictionary<int, string>? romanized = null)
    {
        ArgumentNullException.ThrowIfNull(lyric);
        var shift = double.IsFinite(offset) ? offset : 0;
        var lines = new List<ScrubLine>(lyric.Count);
        for (var index = 0; index < lyric.Count; index++)
        {
            var line = lyric[index];
            if (line.Credit == true || !double.IsFinite(line.Start) || !double.IsFinite(line.End)) continue;
            var roman = romanized is not null && romanized.TryGetValue(index, out var r) ? r : line.Romanization;
            lines.Add(new ScrubLine(line.Start - shift, line.End - shift, line.Text,
                string.IsNullOrEmpty(roman) ? null : roman, line.IsInstrumental));
        }

        return [.. lines];
    }
}

/// <summary>What the lyric capsule over the seek bar says for a moment of the song.</summary>
public readonly record struct ScrubLyric(ScrubLyricKind Kind, int Index = -1)
{
    public static ScrubLyric None => new(ScrubLyricKind.None);

    public static ScrubLyric Instrumental => new(ScrubLyricKind.Instrumental);

    public static ScrubLyric Line(int index) => new(ScrubLyricKind.Line, index);
}

public enum ScrubLyricKind { None, Line, Instrumental }

/// <summary>
/// The seek bar's arithmetic, kept apart from the view so every rule is tested without a window: fine-scrub gain by
/// distance from the bar, snapping to lyric lines, the rubber band past the ends, where a seek lands, and what the bar
/// draws for the lyric structure. Units are device-independent pixels (the Mac's points).
/// </summary>
public static class ScrubMath
{
    /// <summary>The seek bar spans the player card's right-hand column.</summary>
    public const double BarWidth = 364;

    /// <summary>Snap radius at full speed; scaled by the gain, so fine modes barely snap.</summary>
    public const double SnapRadius = 7;

    /// <summary>A sweep faster than this (DIP/s) is never sticky.</summary>
    public const double SnapSpeedLimit = 250;

    /// <summary>At most this share of the distance to the neighbouring snap target.</summary>
    public const double SnapNeighbourShare = 0.4;

    /// <summary>
    /// A seek to a line start lands this much early, so its first word is not clipped by the seek's latency and the
    /// player's buffering.
    /// </summary>
    public const double PreRoll = 0.20;

    /// <summary>Seeking to the very end makes some players skip to the next song.</summary>
    public const double EndGuard = 0.5;

    /// <summary>How far past an end the fill may stretch, and the iOS constant.</summary>
    public const double RubberBandDimension = 40;

    public const double RubberBandCoefficient = 0.55;

    /// <summary>A silence this long between two lines breaks the bar into sections.</summary>
    public const double SectionGap = 6;

    /// <summary>The fewest DIPs between two drawn line ticks.</summary>
    public const double MinimumTickSpacing = 4;

    // ---- Fine scrub ----

    /// <summary>
    /// Horizontal gain by vertical distance from the bar's centre line: 1, ½, ¼, then 0.1. The fine-scrub modifier
    /// (Alt on Windows, ⌥ on the Mac) is always fine.
    /// </summary>
    public static double Gain(double verticalDistance, bool fine = false)
    {
        if (fine) return 0.1;
        var distance = double.IsFinite(verticalDistance) ? Math.Abs(verticalDistance) : 0;
        return distance switch
        {
            < 40 => 1,
            < 80 => 0.5,
            < 120 => 0.25,
            _ => 0.1,
        };
    }

    /// <summary>The words under the time while scrubbing slower than the hand.</summary>
    public static string? RateLabel(double gain) => gain switch
    {
        >= 0.75 => null,
        >= 0.4 => "Half speed",
        >= 0.2 => "Quarter speed",
        _ => "Fine",
    };

    // ---- Position ----

    public static double Time(double x, double duration, double width = BarWidth)
    {
        if (!(width > 0) || !(duration > 0) || !double.IsFinite(x)) return 0;
        return Math.Clamp(x / width, 0, 1) * duration;
    }

    public static double X(double time, double duration, double width = BarWidth)
    {
        if (!(width > 0) || !(duration > 0) || !double.IsFinite(time)) return 0;
        return Math.Clamp(time / duration, 0, 1) * width;
    }

    /// <summary>iOS's rubber band: <c>(1 - 1/(x·c/d + 1))·d</c>, signed. Grows ever slower and never reaches <c>dimension</c>.</summary>
    public static double RubberBand(double overshoot, double dimension = RubberBandDimension, double coefficient = RubberBandCoefficient)
    {
        if (!double.IsFinite(overshoot) || !(dimension > 0) || overshoot == 0) return 0;
        var magnitude = (1 - 1 / (Math.Abs(overshoot) * coefficient / dimension + 1)) * dimension;
        return overshoot < 0 ? -magnitude : magnitude;
    }

    // ---- Snapping ----

    /// <summary>Line starts (instrumental lines left out) plus the very start, sorted and without near-duplicates, inside the song.</summary>
    public static double[] SnapTargets(IReadOnlyList<ScrubLine> lines, double duration)
    {
        if (!(duration > 0)) return [];
        var targets = new List<double>(lines.Count + 1) { 0.0 };
        foreach (var line in lines)
            if (!line.IsInstrumental && double.IsFinite(line.Start) && line.Start > 0 && line.Start < duration)
                targets.Add(line.Start);
        targets.Sort();
        var unique = new List<double>(targets.Count);
        foreach (var target in targets)
            if (unique.Count == 0 || target - unique[^1] >= 0.05) unique.Add(target);
        return [.. unique];
    }

    /// <summary>
    /// The snap target a time is pulled to, as an index into <paramref name="targets"/>, or null.
    /// <paramref name="pointsPerSecond"/> is the bar's scale; <paramref name="speed"/> the pointer's horizontal speed in
    /// DIP/s; <paramref name="disabled"/> is the no-snap modifier held (Ctrl on Windows, ⌘ on the Mac).
    /// </summary>
    public static int? Snap(double time, IReadOnlyList<double> targets, double pointsPerSecond, double gain = 1,
        double speed = 0, bool disabled = false)
    {
        if (disabled || targets.Count == 0 || !(pointsPerSecond > 0) || !double.IsFinite(time)
            || !double.IsFinite(speed) || !(Math.Abs(speed) < SnapSpeedLimit)) return null;
        var nearest = NearestIndex(time, targets);
        var target = targets[nearest];
        var radius = SnapRadius * Math.Max(0, Math.Min(gain, 1));
        var neighbourGap = double.PositiveInfinity;
        if (nearest > 0) neighbourGap = Math.Min(neighbourGap, target - targets[nearest - 1]);
        if (nearest + 1 < targets.Count) neighbourGap = Math.Min(neighbourGap, targets[nearest + 1] - target);
        if (double.IsFinite(neighbourGap)) radius = Math.Min(radius, neighbourGap * pointsPerSecond * SnapNeighbourShare);
        return Math.Abs(time - target) * pointsPerSecond <= radius ? nearest : null;
    }

    public static int NearestIndex(double time, IReadOnlyList<double> sorted)
    {
        int low = 0, high = sorted.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (sorted[middle] < time) low = middle + 1;
            else high = middle;
        }

        if (low == 0) return 0;
        if (low == sorted.Count) return sorted.Count - 1;
        return time - sorted[low - 1] <= sorted[low] - time ? low - 1 : low;
    }

    /// <summary>
    /// Where a seek actually goes: a snapped line start lands <see cref="PreRoll"/> early; never past
    /// <c>duration - EndGuard</c>, never before 0.
    /// </summary>
    public static double CommitTarget(double time, double duration, double? snappedTo = null)
    {
        var target = snappedTo is { } lineStart ? lineStart - PreRoll : time;
        if (!double.IsFinite(target)) target = 0;
        var last = duration > 0 ? Math.Max(0, duration - EndGuard) : Math.Max(0, target);
        return Math.Min(Math.Max(target, 0), last);
    }

    // ---- Drawing the lyric structure ----

    /// <summary>x positions of the line-start ticks, at most one per <paramref name="spacing"/> DIPs. The first target (0:00) is not a line and gets no tick.</summary>
    public static double[] Ticks(IReadOnlyList<double> targets, double duration, double width = BarWidth, double spacing = MinimumTickSpacing)
    {
        if (!(duration > 0) || !(width > 0)) return [];
        var result = new List<double>(targets.Count);
        foreach (var target in targets)
        {
            if (!(target > 0)) continue;
            var x = X(target, duration, width);
            if (result.Count > 0 && x - result[^1] < spacing) continue;
            result.Add(x);
        }

        return [.. result];
    }

    /// <summary>
    /// Song times where a new section starts: the first sung line after a silence of <paramref name="gap"/> seconds or
    /// more, or after an instrumental line. Never within a second of either end, and never two within a second.
    /// </summary>
    public static double[] SectionBreaks(IReadOnlyList<ScrubLine> lines, double duration, double gap = SectionGap)
    {
        if (!(duration > 2) || lines.Count <= 1) return [];
        var breaks = new List<double>();
        double? previousEnd = null;
        var afterInstrumental = false;
        foreach (var line in lines.OrderBy(l => l.Start))
        {
            if (line.IsInstrumental)
            {
                afterInstrumental = previousEnd is not null;
                continue;
            }

            if (previousEnd is { } end && (afterInstrumental || line.Start - end >= gap)
                && line.Start > 1 && line.Start < duration - 1
                && (breaks.Count == 0 || line.Start - breaks[^1] >= 1))
            {
                breaks.Add(line.Start);
            }

            previousEnd = Math.Max(previousEnd ?? line.End, line.End);
            afterInstrumental = false;
        }

        return [.. breaks];
    }

    /// <summary>
    /// Stretches where lines that come back in the song are sung, two or more such lines in a row: most often the
    /// chorus. A guess from the words alone, so the bar only lightens them and never names them.
    /// </summary>
    public static (double Start, double End)[] RepeatedSpans(IReadOnlyList<ScrubLine> lines, double duration, double gap = SectionGap)
    {
        var sung = lines.Where(l => !l.IsInstrumental).OrderBy(l => l.Start).ToArray();
        var keys = sung.Select(l => NormalKey(l.Text)).ToArray();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in keys)
            if (key.Length > 0) counts[key] = counts.GetValueOrDefault(key) + 1;
        var spans = new List<(double, double)>();
        var run = new List<ScrubLine>();

        void Close()
        {
            if (run.Count >= 2)
            {
                var first = run[0];
                var last = run[^1];
                var end = Math.Min(Math.Max(last.End, first.Start), duration > 0 ? duration : last.End);
                if (end > first.Start) spans.Add((first.Start, end));
            }

            run.Clear();
        }

        for (var i = 0; i < sung.Length; i++)
        {
            var line = sung[i];
            var key = keys[i];
            var repeated = key.Length > 0 && counts.GetValueOrDefault(key) >= 2;
            if (repeated && run.Count > 0 && line.Start - run[^1].End < gap)
            {
                run.Add(line);
            }
            else
            {
                Close();
                if (repeated) run.Add(line);
            }
        }

        Close();
        return [.. spans];
    }

    /// <summary>
    /// The line being sung at <paramref name="time"/>, an instrumental stretch, or nothing. A gap of 3 s or more after
    /// a line's end reads as instrumental, and so does an intro of 3 s or more before the first line.
    /// </summary>
    public static ScrubLyric Lyric(double time, IReadOnlyList<ScrubLine> lines)
    {
        if (lines.Count == 0 || !double.IsFinite(time)) return ScrubLyric.None;
        int low = 0, high = lines.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (lines[middle].Start <= time) low = middle + 1;
            else high = middle;
        }

        if (low == 0)
        {
            var firstSung = lines[0].Start;
            foreach (var line in lines)
            {
                if (line.IsInstrumental) continue;
                firstSung = line.Start;
                break;
            }

            return firstSung >= 3 ? ScrubLyric.Instrumental : ScrubLyric.None;
        }

        var index = low - 1;
        // Windows addition (not on the Mac): an answer line that has already ended (a duet's "yeah yeah" sung under a
        // longer line) gives way to the line still being sung around it, so the bubble matches the wallpaper.
        if (lines[index].End < time)
        {
            for (var i = index - 1; i >= Math.Max(0, index - 3); i--)
            {
                if (lines[i].IsInstrumental || lines[i].Start > time || lines[i].End < time) continue;
                index = i;
                break;
            }
        }

        var found = lines[index];
        if (found.IsInstrumental) return ScrubLyric.Instrumental;
        var nextStart = index + 1 < lines.Count ? lines[index + 1].Start : double.PositiveInfinity;
        if (time > found.End + 1 && nextStart - found.End >= 3) return ScrubLyric.Instrumental;
        return ScrubLyric.Line(index);
    }

    // ---- Stepping (keyboard and screen readers) ----

    /// <summary>
    /// The next or previous line start from <paramref name="time"/>. A time just before a start (a pre-rolled seek)
    /// counts as at it, so repeated presses walk line by line. Going back more than 2 s into a line first returns to
    /// its own start, as a previous-track button does.
    /// </summary>
    public static double? AdjacentLine(double time, IReadOnlyList<double> targets, bool forward)
    {
        if (targets.Count == 0 || !double.IsFinite(time)) return null;
        const double slack = PreRoll + 0.05;
        int? current = null;
        for (var i = targets.Count - 1; i >= 0; i--)
        {
            if (targets[i] > time + slack) continue;
            current = i;
            break;
        }

        if (forward)
        {
            var next = current is { } c ? c + 1 : 0;
            return next < targets.Count ? targets[next] : null;
        }

        if (current is not { } at) return null;
        if (time - targets[at] > 2) return targets[at];
        return at > 0 ? targets[at - 1] : targets[0];
    }

    /// <summary>A seek by a fixed step, inside the song.</summary>
    public static double Stepped(double time, double step, double duration) => CommitTarget(time + step, duration);

    /// <summary>The 0–9 keys: 0 % to 90 % of the song.</summary>
    public static double? PercentJump(int digit, double duration)
    {
        if (digit is < 0 or > 9 || !(duration > 0)) return null;
        return CommitTarget(duration * digit / 10, duration);
    }

    // ---- After a seek ----

    /// <summary>
    /// What the bar shows after a commit: the target, for at most <paramref name="holdFor"/> seconds or until the
    /// player's clock is within <paramref name="tolerance"/> of it, so a single stale redraw never pulls the knob back.
    /// </summary>
    public static double DisplayedTime(double playback, double? held, double heldSince, double now, double holdFor = 0.6, double tolerance = 0.5)
    {
        if (held is not { } h || !(now - heldSince < holdFor) || !(Math.Abs(playback - h) > tolerance)) return playback;
        return h;
    }

    // ---- Words ----

    /// <summary>1:05, 12:40, 1:02:03.</summary>
    public static string Timestamp(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "0:00";
        var total = (long)Math.Floor(seconds);
        long hours = total / 3600, minutes = total % 3600 / 60, rest = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }

    /// <summary>The right-hand label: time left (with a minus) or the song's length.</summary>
    public static string TrailingLabel(double elapsed, double duration, bool showsRemaining)
    {
        if (!(duration > 0)) return "";
        if (!showsRemaining) return Timestamp(duration);
        var left = Math.Max(0, duration - Math.Min(Math.Max(elapsed, 0), duration));
        return "-" + Timestamp(Math.Ceiling(left));
    }

    /// <summary>The screen reader's value for the bar (Narrator here, VoiceOver on the Mac).</summary>
    public static string AccessibilityValue(double elapsed, double duration, string? line)
    {
        var value = $"{Timestamp(elapsed)} of {Timestamp(duration)}";
        if (!string.IsNullOrEmpty(line)) value += $", line: {line}";
        return value;
    }

    /// <summary>
    /// A line's text with case, accents, spaces and punctuation ignored (the Mac's <c>CoverLookup.normal</c>): how the
    /// bar recognises a line that comes back.
    /// </summary>
    public static string NormalKey(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
