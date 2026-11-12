// Word entrances. The entrance list, its titles and "Match the style" are re-implemented from a description of the
// Mac's behaviour (LyricComposition.swift traces back to the squashed Verso import; see docs/compositions/shared.md).
// The transform table (WordEntrance.transform in ScatteredLyricText.swift) and the travel bounds are Lunalyr's own
// and ported as they are.

using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>The lyric compositions built on Windows so far.</summary>
public enum CompositionKind
{
    Editorial,
    Spoken,
    /// <summary>Cascade: a moving column of words, arriving with the voice.</summary>
    Cascade,
    /// <summary>Cutout: uneven type and album-colour labels, built word by word.</summary>
    Cutout,
    Hinge, Broadside, Portal, Mosaic, Torque, Fable,
}

/// <summary>How a word makes its entrance. Lunalyr's cutout, highlight, swipe and mix entrances are not built yet.</summary>
public enum WordArrival
{
    /// <summary>"Match the style": the composition's own entrance.</summary>
    Automatic,
    /// <summary>"Soft focus": fades in out of a blur.</summary>
    Fade,
    /// <summary>"Float up".</summary>
    Lift,
    Pop,
    Orbit,
    Glide,
    Unfold,
}

public static class WordArrivals
{
    /// <summary>
    /// A <c>LyricStyle.Reveal</c> id: the Mac's raw values (<c>fade</c>, <c>lift</c>, <c>pop</c>, <c>orbit</c>,
    /// <c>glide</c>, <c>unfold</c>, <c>automatic</c>) or the spelled-out titles (<c>softFocus</c>, <c>floatUp</c>,
    /// <c>matchStyle</c>). Unknown ids (and the entrances not built yet) are Soft focus.
    /// </summary>
    public static WordArrival Parse(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "automatic" or "matchstyle" or "match" or "" or null => WordArrival.Automatic,
        "fade" or "softfocus" or "soft focus" => WordArrival.Fade,
        "lift" or "floatup" or "float up" => WordArrival.Lift,
        "pop" => WordArrival.Pop,
        "orbit" => WordArrival.Orbit,
        "glide" => WordArrival.Glide,
        "unfold" => WordArrival.Unfold,
        _ => WordArrival.Fade,
    };

    /// <summary>"Match the style" resolved for a composition: Editorial fades in, Spoken glides, Cascade floats up, Cutout folds in.</summary>
    public static WordArrival Resolved(this WordArrival arrival, CompositionKind composition) =>
        arrival != WordArrival.Automatic ? arrival : composition switch
        {
            CompositionKind.Spoken => WordArrival.Glide,
            CompositionKind.Cascade => WordArrival.Lift,
            // The Mac's Cutout · Fold is not built yet: Unfold, the nearest built entrance, stands in for it.
            CompositionKind.Cutout => WordArrival.Unfold,
            _ => WordArrival.Fade,
        };

    /// <summary>The entrance's blur at <paramref name="progress"/>, in points: 3.5 for Soft focus, 2.5 otherwise.</summary>
    public static double Blur(this WordArrival arrival, double progress, bool reduceMotion = false) =>
        reduceMotion ? 0 : Math.Max(0, 1 - progress) * (arrival == WordArrival.Fade ? 3.5 : 2.5);

    /// <summary>
    /// The largest travel an entrance makes, for room that must be kept around a word: width and height in the same
    /// units as <paramref name="pointSize"/>.
    /// </summary>
    public static SizeD TravelBounds(this WordArrival arrival, double pointSize, bool active = true)
    {
        if (!active) return SizeD.Zero;
        var horizontal = arrival == WordArrival.Orbit ? 0.32 : arrival == WordArrival.Glide ? 0.72 : 0;
        var vertical = arrival == WordArrival.Lift ? 0.48 : arrival is WordArrival.Glide or WordArrival.Orbit ? 0.20 : 0;
        return new SizeD(horizontal * pointSize * 2, vertical * pointSize * 2);
    }
}

/// <summary>Where an entrance has a word on this frame.</summary>
/// <param name="OffsetX">Travel still to cover, in the units of the word's size.</param>
/// <param name="OffsetY">Travel still to cover (positive is down).</param>
/// <param name="Turn">Turn still to unwind, degrees (signed).</param>
/// <param name="Scale">Entrance scale.</param>
/// <param name="Fold">Fold about the bottom edge, degrees (negative leans back).</param>
/// <param name="Eased">The 0…1 value the transforms follow.</param>
public readonly record struct EntranceTransform(double OffsetX, double OffsetY, double Turn, double Scale, double Fold, double Eased)
{
    public static EntranceTransform Rest => new(0, 0, 0, 1, 0, 1);

    /// <summary>
    /// The entrance a word is drawn with. Travel, turn and scale follow the word's spring (<paramref name="arrival"/>,
    /// which may overshoot), or an eased <paramref name="progress"/> without one. Shared with the layouts that keep
    /// room for it, so the room and the motion never disagree.
    /// </summary>
    public static EntranceTransform Make(double progress, double? arrival, WordArrival style, double size, double direction,
                                         bool reduceMotion, double maxTurn = 12)
    {
        if (reduceMotion) return Rest;
        var eased = arrival ?? RollingMotion.MotionEase(progress);
        var remaining = 1 - eased;
        var travelsSideways = style is WordArrival.Orbit or WordArrival.Glide;
        var travelsDown = style is WordArrival.Lift or WordArrival.Glide or WordArrival.Orbit;
        return new EntranceTransform(
            OffsetX: travelsSideways ? remaining * direction * size * (style == WordArrival.Orbit ? 0.32 : 0.72) : 0,
            OffsetY: travelsDown ? remaining * size * (style == WordArrival.Lift ? 0.48 : 0.20) : 0,
            Turn: style == WordArrival.Orbit ? remaining * direction * Math.Min(12, Math.Max(0, maxTurn)) : 0,
            Scale: style == WordArrival.Pop ? 0.76 + 0.24 * eased : 1,
            Fold: style == WordArrival.Unfold ? -48 * remaining : 0,
            Eased: eased);
    }
}
