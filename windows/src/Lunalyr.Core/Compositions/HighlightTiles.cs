// Ported from the Mac app's LyricHighlightTile.swift (LyricHighlightTiming, LyricHighlightMark): Lunalyr's own code
// (git blame puts none of it in the squashed Verso import). The segments of a run follow the Mac's
// ScatteredTypography segments (each word a reader takes in has its own tile), written here from that behaviour.

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>
/// "Cutout · Highlight": as each word is sung, a cutout tile presses in behind it -- a quick ease-out from the word's
/// leading edge, with no pop -- stays while the rest of the line is sung, and fades at the line end. With Reduce Motion
/// the tile only fades in and out. A pure function of playback time, so it is the same at any frame rate and after a seek.
/// </summary>
public static class HighlightTiming
{
    /// <summary>The <c>LyricStyle.Reveal</c> id of the entrance that presses a tile behind each sung word.</summary>
    public const string RevealId = "cutoutHighlight";

    /// <summary>The press.</summary>
    public const double Press = 0.12;

    /// <summary>The fade at the end of the line.</summary>
    public const double Fade = 0.30;

    /// <summary>How long the word's ink takes to change between the tile's text colour and its own.</summary>
    public const double SwitchWidth = 0.04;

    /// <summary>The tile's tilt, degrees, as Cutout's labels have.</summary>
    public const double Tilt = -1.2;

    /// <summary>The tile's corner radius, points.</summary>
    public const double CornerRadius = 3;

    /// <summary>Whether a reveal id presses tiles behind sung words. The words still enter as the layout has them enter.</summary>
    public static bool HighlightsSungWords(string? revealId) =>
        string.Equals(revealId?.Trim(), RevealId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The tile of a word sung at <paramref name="onset"/> whose line ends at <paramref name="release"/>.</summary>
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

    /// <summary>
    /// How much of the colour set on the tile the word shows while its tile is at <paramref name="opacity"/>: all of
    /// it above <paramref name="switchAt"/> (<c>LyricHighlight.TextSwitch</c>), none below, changing within about a
    /// frame of the fade.
    /// </summary>
    public static double TextOpacity(double opacity, double switchAt) =>
        Easing.Smoothstep((opacity - switchAt) / SwitchWidth + 0.5);
}

/// <summary>A tile's opacity (0…1) and how far it has pressed in from the word's leading edge (0…1).</summary>
public readonly record struct HighlightPose(double Opacity, double Width)
{
    public static HighlightPose Hidden => new(0, 0);
    public bool IsVisible => Opacity > 0.001;
}

/// <summary>A stretch of a label one tile covers: along the label by character (0…1), and when it is sung.</summary>
public readonly record struct HighlightSegment(double From, double To, double Onset);

/// <summary>
/// Where a tile falls on a word or a run of glued words: each sung word of the run is a stretch of its label, by
/// character.
/// </summary>
public readonly record struct HighlightMark(double From, double To, HighlightPose Pose)
{
    /// <summary>Tiles reach this far past the ink of their word, in ems (width, height): within the clearance the
    /// layout keeps around every word, so a tile never covers a neighbour. Where two words of one run meet, their
    /// tiles just overlap by <see cref="Seam"/>.</summary>
    public const double ReachX = 0.1, ReachY = 0.05, Seam = 0.04;

    /// <summary>The tile's rectangle (x, y, width, height) in a label <paramref name="width"/> × <paramref name="height"/>
    /// (top-left origin), set at <paramref name="pointSize"/>.</summary>
    public RectD Rect(double width, double height, double pointSize, bool rightToLeft)
    {
        double reach = ReachX * pointSize, seam = Seam * pointSize;
        var start = From * width - (From <= 0.001 ? reach : seam);
        var end = To * width + (To >= 0.999 ? reach : seam);
        if (rightToLeft) (start, end) = (width - end, width - start);
        var lift = ReachY * pointSize;
        return new RectD(start, -lift, Math.Max(0, end - start), height + lift * 2);
    }

    /// <summary>The part of <see cref="Rect"/> the press has reached, from the leading edge.</summary>
    public RectD Pressed(double width, double height, double pointSize, bool rightToLeft)
    {
        var rect = Rect(width, height, pointSize, rightToLeft);
        var reached = rect.Width * Pose.Width;
        return new RectD(rightToLeft ? rect.X + rect.Width - reached : rect.X, rect.Y, reached, rect.Height);
    }
}

/// <summary>A rectangle in layout units, top-left origin.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height);

public static class HighlightSegments
{
    /// <summary>
    /// The tiles of one run of <paramref name="tokens"/> (its drawn tokens in order, with their character
    /// <paramref name="boundaries"/> along the label): one per word a reader takes in, so a run of Japanese is lit word
    /// by word along its own letters, while the syllables of one Latin word stay one tile.
    /// </summary>
    public static HighlightSegment[] For(IReadOnlyList<LyricWord> tokens, IReadOnlyList<double> boundaries, IWordBreaker breaker)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(boundaries);
        ArgumentNullException.ThrowIfNull(breaker);
        if (tokens.Count == 0) return [];
        if (tokens.Count == 1 || boundaries.Count != tokens.Count + 1) return [new HighlightSegment(0, 1, tokens[0].Start)];
        var glue = new bool[tokens.Count];
        for (var i = 1; i < glue.Length; i++) glue[i] = true;
        var split = LyricRuns.SplittingWords(tokens, glue, breaker, LyricRuns.Parting.Words);
        var segments = new List<HighlightSegment>();
        var start = 0;
        for (var i = 1; i <= tokens.Count; i++)
        {
            if (i < tokens.Count && split[i]) continue;
            segments.Add(new HighlightSegment(start == 0 ? 0 : boundaries[start], i == tokens.Count ? 1 : boundaries[i], tokens[start].Start));
            start = i;
        }
        return [.. segments];
    }

    /// <summary>The marks lit at <paramref name="time"/>, written into <paramref name="marks"/>; their count.</summary>
    public static int Marks(ReadOnlySpan<HighlightSegment> segments, double time, double release, bool reduceMotion,
                            Span<HighlightMark> marks)
    {
        var count = 0;
        foreach (var segment in segments)
        {
            if (count >= marks.Length) break;
            var pose = HighlightTiming.Pose(time, segment.Onset, release, reduceMotion);
            if (pose.IsVisible) marks[count++] = new HighlightMark(segment.From, segment.To, pose);
        }
        return count;
    }
}
