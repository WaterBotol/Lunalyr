// "Cutout · Highlight": the tile's timing (LyricHighlightTiming), its look (CutoutTile), where tiles fall on a label
// (LyricHighlightMark) and a run's sung words (ScatteredTypography.Segment and segment(_:), ScatteredRunFrame.highlight).
// Ported from the Mac's LyricHighlightTile.swift, LyricTypography.swift and ScatteredLyricText.swift: Lunalyr's own
// code (git blame puts none of these lines in the squashed Verso import).

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>
/// As each word is sung, a cutout tile presses in behind it (a quick ease-out from the word's leading edge, no pop),
/// stays while the rest of the line is sung, and fades at the line's end. Under Reduce Motion the tile only fades in and
/// out. A pure function of playback time, so it is the same at any frame rate and after a seek.
/// </summary>
public static class LyricHighlightTiming
{
    /// <summary>The <c>LyricStyle.Reveal</c> id of the entrance that presses a tile behind each sung word.</summary>
    public const string RevealId = "cutoutHighlight";

    /// <summary>The press, seconds.</summary>
    public const double Press = 0.12;

    /// <summary>The exit at the end of the line, seconds (the Mac's 436adda: 0.42, was a 0.30 fade).</summary>
    public const double Fade = LineFlow.HighlightFade;

    /// <summary>The exit's length, seconds.</summary>
    public const double ExitDuration = LineFlow.HighlightFade;

    /// <summary>How much smaller the tile ends, scaled about its centre.</summary>
    public const double ExitScale = 0.06;

    /// <summary>How soft the tile ends, in ems of the word's size.</summary>
    public const double ExitBlur = 0.06;

    /// <summary>How gradually the word's colour eases back from the tile's while it leaves (a share of its opacity).</summary>
    public const double ExitTextBand = 0.5;

    /// <summary>
    /// How far a tile has left, <paramref name="age"/> s after its release: smootherstep over <see cref="ExitDuration"/>
    /// (no jump on the first frame, none at the last).
    /// </summary>
    public static double ExitProgress(double age)
    {
        if (!(age > 0)) return 0;
        if (age >= ExitDuration) return 1;
        var x = Math.Clamp(age / ExitDuration, 0, 1);
        return x * x * x * (x * (6 * x - 15) + 10);
    }

    /// <summary>How quickly the word's colour changes over to the tile's text colour (a share of the tile's opacity).</summary>
    public const double SwitchWidth = 0.04;

    /// <summary>
    /// Whether a reveal id is "Cutout · Highlight" itself (any spelling <see cref="WordArrivals.Parse"/> accepts). A mix
    /// that holds it decides word by word: see <see cref="EntranceChoice.For"/>.
    /// </summary>
    public static bool HighlightsSungWords(string? revealId) => WordArrivals.Parse(revealId).HighlightsSungWords();

    /// <summary>Where a line's tiles release: its end, or its last word's end if later (previews may give only words).</summary>
    public static double LineEnd(double? end, IReadOnlyList<LyricWord> words) => LineFlow.HighlightLineEnd(end, words);

    /// <summary>
    /// How much of the colour set on the tile the word shows while its tile is at <paramref name="opacity"/>: all of
    /// it above <paramref name="switchAt"/> (<c>LyricHighlight.textSwitch</c>), none below, changing over about a
    /// frame of the fade.
    /// </summary>
    public static double TextOpacity(double opacity, double switchAt) => TextOpacity(opacity, switchAt, SwitchWidth);

    /// <summary>
    /// The word's share of the tile's text colour over a band of <paramref name="band"/>, centred on the switch point,
    /// which is held in from the ends so a whole tile always carries its colour and a gone tile none (the Mac's 436adda).
    /// </summary>
    public static double TextOpacity(double opacity, double switchAt, double band)
    {
        band = Math.Clamp(band, 1e-6, 1);
        var centre = Math.Clamp(switchAt, band / 2, 1 - band / 2);
        return Easing.Smoothstep((opacity - centre) / band + 0.5);
    }

    /// <summary>The word's share of the tile's text colour at <paramref name="pose"/>: the press band, or the wider exit band while it leaves.</summary>
    public static double TextOpacity(HighlightPose pose, double switchAt) =>
        TextOpacity(pose.Opacity, switchAt, pose.Leaving > 0 ? ExitTextBand : SwitchWidth);

    /// <summary>The tile of a word sung at <paramref name="onset"/> whose line ends at <paramref name="release"/>.</summary>
    public static HighlightPose Pose(double time, double onset, double release, bool reduceMotion)
    {
        var age = time - onset;
        if (!double.IsFinite(age) || age < 0 || !(time < release + ExitDuration)) return HighlightPose.Hidden;
        var pressed = Math.Min(1, age / Press);
        // Cubic ease-out: quick, and it never passes the word's width.
        var eased = 1 - Math.Pow(1 - pressed, 3);
        var incoming = reduceMotion ? Easing.Smoothstep(pressed) : Math.Min(1, eased / 0.6);
        // The exit (the Mac's 436adda): smootherstep over 0.42 s, the tile shrinking 6% about its centre and softening;
        // under Reduce Motion it only fades. The width stays as pressed.
        var leaving = ExitProgress(time - release);
        var opacity = incoming * (1 - leaving);
        if (!(opacity > 0.001)) return HighlightPose.Hidden;
        return new HighlightPose(opacity, reduceMotion ? 1 : 0.1 + 0.9 * eased)
        {
            Leaving = leaving,
            Scale = reduceMotion ? 1 : 1 - ExitScale * leaving,
            Blur = reduceMotion ? 0 : ExitBlur * leaving,
        };
    }
}

/// <summary>A tile on one frame (the Mac's <c>LyricHighlightTiming.Pose</c>).</summary>
/// <param name="Opacity">0…1.</param>
/// <param name="Width">How far it has pressed in from the word's leading edge, 0…1.</param>
public readonly record struct HighlightPose(double Opacity, double Width)
{
    public static HighlightPose Hidden => default;
    public bool IsVisible => Opacity > 0.001;

    /// <summary>How far the tile has left, 0…1 (0 until its line's end).</summary>
    public double Leaving { get; init; }

    /// <summary>The tile's scale about its centre (1 until it leaves).</summary>
    public double Scale { get; init; } = 1;

    /// <summary>The tile's softness, in ems of the word's size.</summary>
    public double Blur { get; init; }
}

/// <summary>
/// A cutout tile's look: flat paper with slightly rounded corners and the small tilt Cutout's labels have, pressed in
/// from the word's leading edge.
/// </summary>
public static class CutoutTile
{
    /// <summary>The tilt, degrees (none under Reduce Motion).</summary>
    public const double Tilt = -1.2;

    /// <summary>The corner radius, points.</summary>
    public const double CornerRadius = 3;
}

/// <summary>
/// Where a tile falls on a word or a run of glued words, on one frame (the Mac's <c>LyricHighlightMark</c>): each sung
/// word of the run is a stretch of its label, by character.
/// </summary>
/// <param name="From">Along the label, 0…1 by character.</param>
/// <param name="To">Along the label, 0…1 by character.</param>
public readonly record struct HighlightMark(double From, double To, HighlightPose Pose)
{
    /// <summary>Tiles reach this far past the ink of their word, in ems (width, height): within the clearance the
    /// layout keeps around every word, so a tile never covers a neighbour.</summary>
    public const double ReachX = 0.1, ReachY = 0.05;

    /// <summary>Where two words of one run meet, their tiles just overlap by this much, in ems.</summary>
    public const double Seam = 0.04;

    /// <summary>The tile's rectangle within a label of <paramref name="width"/> × <paramref name="height"/> (top-left
    /// origin), set at <paramref name="pointSize"/>.</summary>
    public RectD Rect(double width, double height, double pointSize, bool rightToLeft)
    {
        double reach = ReachX * pointSize, seam = Seam * pointSize;
        var start = From * width - (From <= 0.001 ? reach : seam);
        var end = To * width + (To >= 0.999 ? reach : seam);
        if (rightToLeft) (start, end) = (width - end, width - start);
        var rise = ReachY * pointSize;
        return new RectD(start, -rise, Math.Max(0, end - start), height + rise * 2);
    }

    /// <summary>The part of <see cref="Rect"/> the press has reached, from the leading edge.</summary>
    public RectD Pressed(double width, double height, double pointSize, bool rightToLeft)
    {
        var rect = Rect(width, height, pointSize, rightToLeft);
        var pressed = rect.Width * Pose.Width;
        return rightToLeft ? rect with { X = rect.X + rect.Width - pressed, Width = pressed } : rect with { Width = pressed };
    }
}

/// <summary>
/// One sung word of a run (the Mac's <c>ScatteredTypography.Segment</c>): where it falls along the run's label (0…1 by
/// character), when it is sung, when it ends, and the end of its line, where its tile releases.
/// </summary>
public readonly record struct HighlightSegment(double From, double To, double Onset, double End, double Release)
{
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

    /// <summary>
    /// The tiles of one run's <paramref name="segments"/> showing at <paramref name="time"/> (the Mac's
    /// <c>ScatteredRunFrame.highlight</c>), written into <paramref name="marks"/>; their count.
    /// </summary>
    /// <param name="carried">The word carries its tiles out (a line that leaves): they hold whole and leave with it.</param>
    public static int Marks(ReadOnlySpan<HighlightSegment> segments, double time, bool reduceMotion, Span<HighlightMark> marks,
                            bool carried = false)
    {
        var count = 0;
        foreach (var segment in segments)
        {
            if (count >= marks.Length) break;
            var pose = LyricHighlightTiming.Pose(time, segment.Onset, carried ? double.PositiveInfinity : segment.Release, reduceMotion);
            if (pose.IsVisible) marks[count++] = new HighlightMark(segment.From, segment.To, pose);
        }
        return count;
    }
}

/// <summary>A rectangle (top-left origin).</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
}
