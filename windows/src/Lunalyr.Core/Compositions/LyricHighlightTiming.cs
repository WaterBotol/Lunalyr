// "Cutout · Highlight": the tile's timing (LyricHighlightTiming), where tiles fall on a label (LyricHighlightMark) and
// a run's sung words (ScatteredTypography.segment). Ported from the Mac's LyricHighlightTile.swift and
// LyricTypography.swift, Lunalyr's own code.

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>
/// As each word is sung, a cutout tile presses in behind it (a quick ease-out from the word's leading edge, no pop),
/// stays while the rest of the line is sung, and fades at the line's end. Under Reduce Motion the tile only fades in and
/// out. A pure function of playback time.
/// </summary>
public static class LyricHighlightTiming
{
    /// <summary>The press, seconds.</summary>
    public const double Press = 0.12;

    /// <summary>The fade at the end of the line, seconds.</summary>
    public const double Fade = LineFlow.HighlightFade;

    /// <summary>How quickly the word's colour changes over to the tile's text colour (a share of the tile's opacity).</summary>
    public const double SwitchWidth = 0.04;

    /// <summary>Where a line's tiles release: its end, or its last word's end if later.</summary>
    public static double LineEnd(double? end, IReadOnlyList<LyricWord> words) => LineFlow.HighlightLineEnd(end, words);

    /// <summary>
    /// How much of the colour set on the tile the word shows while its tile is at <paramref name="opacity"/>: all of
    /// it above <paramref name="switchAt"/>, none below, changing over about a frame of the fade.
    /// </summary>
    public static double TextOpacity(double opacity, double switchAt) => Easing.Smoothstep((opacity - switchAt) / SwitchWidth + 0.5);

    public static HighlightPose Pose(double time, double onset, double release, bool reduceMotion)
    {
        var age = time - onset;
        if (!double.IsFinite(age) || age < 0 || !(time < release + Fade)) return HighlightPose.Hidden;
        var pressed = Math.Min(1, age / Press);
        // Cubic ease-out: quick, and it never passes the word's width.
        var eased = 1 - Math.Pow(1 - pressed, 3);
        var incoming = reduceMotion ? Easing.Smoothstep(pressed) : Math.Min(1, eased / 0.6);
        var gone = Math.Min(1, Math.Max(0, (time - release) / Fade));
        var outgoing = (1 - gone) * (1 - gone);
        var opacity = incoming * outgoing;
        if (!(opacity > 0.001)) return HighlightPose.Hidden;
        return new HighlightPose(opacity, reduceMotion ? 1 : 0.1 + 0.9 * eased);
    }
}

/// <summary>A tile on one frame.</summary>
/// <param name="Opacity">0…1.</param>
/// <param name="Width">How far it has pressed in from the word's leading edge, 0…1.</param>
public readonly record struct HighlightPose(double Opacity, double Width)
{
    public static HighlightPose Hidden => default;
    public bool IsVisible => Opacity > 0.001;
}

/// <summary>A cutout tile's look: its tilt and corner radius (points).</summary>
public static class CutoutTile
{
    public const double Tilt = -1.2;
    public const double CornerRadius = 3;
}

/// <summary>One sung word of a run: where it falls along the run's label (0…1 by character), when it is sung, and the end of its line.</summary>
public readonly record struct HighlightSegment(double From, double To, double Onset, double End, double Release)
{
    /// <summary>Tiles reach this far past the ink of their word, in ems (within the clearance kept around every word).</summary>
    public const double ReachWidth = 0.1, ReachHeight = 0.05;

    /// <summary>Where two words of one run meet, their tiles just overlap by this much, in ems.</summary>
    public const double Seam = 0.04;

    /// <summary>The tile's rectangle within a label of <paramref name="width"/> × <paramref name="height"/> (top-left origin), set at <paramref name="pointSize"/>.</summary>
    public RectD Rect(double width, double height, double pointSize, bool rightToLeft)
    {
        double reach = ReachWidth * pointSize, seam = Seam * pointSize;
        var start = From * width - (From <= 0.001 ? reach : seam);
        var end = To * width + (To >= 0.999 ? reach : seam);
        if (rightToLeft) (start, end) = (width - end, width - start);
        var rise = ReachHeight * pointSize;
        return new RectD(start, -rise, Math.Max(0, end - start), height + rise * 2);
    }

    /// <summary>The part of <see cref="Rect"/> the press has reached at <paramref name="pose"/>.</summary>
    public RectD Pressed(HighlightPose pose, double width, double height, double pointSize, bool rightToLeft)
    {
        var rect = Rect(width, height, pointSize, rightToLeft);
        var pressed = rect.Width * pose.Width;
        return rightToLeft ? rect with { X = rect.X + rect.Width - pressed, Width = pressed } : rect with { Width = pressed };
    }

    /// <summary>
    /// Each run's sung words. A run of a script written without spaces is parted where a reader would start a word
    /// (<see cref="LyricRuns.Parting.Words"/>), so a Japanese line set as one run is tiled word by word; the syllables
    /// of one Latin word stay one word. Every segment releases at <paramref name="lineEnd"/>.
    /// </summary>
    /// <param name="line">The line's runs (<see cref="EditorialSetting.Layout"/>).</param>
    /// <param name="words">The line's words, as passed to the layout.</param>
    /// <param name="glue">Their glue, as passed to the layout.</param>
    public static HighlightSegment[][] Of(EditorialLine line, IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue,
                                          IWordBreaker breaker, double lineEnd)
    {
        ArgumentNullException.ThrowIfNull(line);
        var (visible, visibleGlue) = LyricRuns.Visible(words, glue);
        var parted = LyricRuns.SplittingWords(visible, visibleGlue, breaker, LyricRuns.Parting.Words);
        var result = new HighlightSegment[line.Runs.Count][];
        var found = new List<HighlightSegment>();
        for (var r = 0; r < line.Runs.Count; r++)
        {
            var run = line.Runs[r];
            var bounds = run.Boundaries;
            int lower = run.TokenStart, upper = run.TokenEnd;
            found.Clear();
            var start = lower;
            for (var index = lower; index <= upper; index++)
            {
                var begins = index == upper || (index > lower && !(index < parted.Count && parted[index]));
                if (!begins || index <= start) continue;
                var from = start - lower < bounds.Length ? bounds[start - lower] : 0;
                var to = index - lower < bounds.Length ? bounds[index - lower] : 1;
                var end = visible[start].End;
                for (var k = start + 1; k < index; k++) end = Math.Max(end, visible[k].End);
                found.Add(new HighlightSegment(from, to, visible[start].Start, end, lineEnd));
                start = index;
            }
            result[r] = [.. found];
        }
        return result;
    }
}

/// <summary>A rectangle (top-left origin).</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
}
