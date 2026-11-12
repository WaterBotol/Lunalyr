// The arrival-only compositions (Spoken, Cascade, Cutout) and Cutout's labels. Which compositions roll and which
// curves each takes are described in docs/compositions/shared.md and spoken.md (re-implemented from behaviour); the
// label's width rule and its tile (RollingUnit.tileWidth, CutoutTile in the Mac app) are Lunalyr's own and ported.

using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

public static class RollingCompositions
{
    /// <summary>Word-timed lines roll one word at a time instead of being set as a line: Spoken, Cascade and Cutout.</summary>
    public static bool ArrivalOnly(this CompositionKind composition) =>
        composition is CompositionKind.Spoken or CompositionKind.Cascade or CompositionKind.Cutout;

    /// <summary>The rolling curves a composition takes: Spoken the fan, Cascade the column, Cutout the collage.</summary>
    public static RollingStyle RollingStyle(this CompositionKind composition) => composition switch
    {
        CompositionKind.Cascade => Motion.RollingStyle.Column,
        CompositionKind.Cutout => Motion.RollingStyle.Collage,
        _ => Motion.RollingStyle.Fan,
    };
}

/// <summary>Cutout's album-colour label behind a word.</summary>
public static class RollingLabel
{
    /// <summary>The room a label's own tilt takes on the stack, degrees.</summary>
    public const double Tilt = 1.2;

    /// <summary>The tile's tilt as drawn, degrees (it leans the other way from the room kept for it): the cutout
    /// tile's, which the Mac shares between labels and highlight tiles.</summary>
    public const double DrawnTilt = CutoutTile.Tilt;

    /// <summary>The tile's corner radius, points (the cutout tile's).</summary>
    public const double CornerRadius = CutoutTile.CornerRadius;

    /// <summary>Padding round a labelled word (and its reading), points.</summary>
    public const double PaddingX = 11, PaddingY = 3;

    /// <summary>Padding at each side of an unlabelled rolling word, points.</summary>
    public const double PlainPaddingX = 2;

    /// <summary>
    /// How far a "Cutout · Highlight" tile reaches past an unlabelled rolling unit on each side, points: a label's
    /// padding past the plain word's (the Mac's <c>RollingUnit.labelReach</c>), so the tile is a label's size.
    /// </summary>
    public const double ReachX = 9, ReachY = 3;

    /// <summary>
    /// The highlight tile behind an unlabelled rolling unit (its word and reading, <paramref name="width"/> ×
    /// <paramref name="height"/> pixels with their own padding), centred on the unit: the whole unit and
    /// <see cref="ReachX"/> × <see cref="ReachY"/> points more at each side. <paramref name="scale"/> is pixels per point.
    /// </summary>
    public static RectD HighlightRect(double width, double height, double scale)
    {
        double x = ReachX * scale, y = ReachY * scale;
        return new RectD(-width / 2 - x, -height / 2 - y, width + 2 * x, height + 2 * y);
    }

    /// <summary>
    /// The part of a tile's <paramref name="rect"/> the press has reached (<paramref name="share"/>, 0…1), from the
    /// word's leading edge: where the word and its reading take the colour set on the tile.
    /// </summary>
    public static RectD Pressed(RectD rect, double share, bool rightToLeft)
    {
        var width = rect.Width * Math.Min(1, Math.Max(0, share));
        return rightToLeft ? rect with { X = rect.X + rect.Width - width, Width = width } : rect with { Width = width };
    }

    /// <summary>
    /// How far a label has pressed in, as a share of its word: one rule before and after the onset. A sliver under
    /// the faint preview, then pressed on with the word's spring (<paramref name="arrival"/>), so it lands with a
    /// little give. With Reduce Motion the label is simply there and fades in with its word.
    /// </summary>
    public static double TileWidth(double arrival, bool reduceMotion) =>
        reduceMotion ? 1 : Math.Min(1.06, Math.Max(0.05, arrival));
}
