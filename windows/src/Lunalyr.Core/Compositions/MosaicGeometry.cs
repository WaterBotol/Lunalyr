// Mosaic's geometry: the original Mosaic, as the Mac shipped it up to 2.0.2 (StageLyricStyles.swift; Lunalyr's own
// code, no line from the Verso import, ported as it is). The owner preferred it to the Build 88/90 rework (14 Nov 2026),
// so this is Windows' Mosaic from before that rework, unchanged.

using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>Mosaic: each word set a different way as it arrives, shrinking into a small stack above.</summary>
public static class MosaicGeometry
{
    /// <summary>The word is set huge, cut off by the stage's edge, with a star beside it.</summary>
    public const int Huge = 0;
    /// <summary>The word is set on a colour bar across a pale panel.</summary>
    public const int Bar = 1;
    /// <summary>The word is set letter by letter on jumping baselines.</summary>
    public const int Letters = 2;

    /// <summary>
    /// How run <paramref name="run"/> of a line seeded <paramref name="seed"/> is set: the Mac's chance. A word whose
    /// glyphs can't be drawn one at a time (Arabic and other right-to-left or joined scripts, combining marks) is never
    /// set letter by letter, since splitting it would reverse its letters and break their joins: it is set huge instead.
    /// </summary>
    public static int Kind(long seed, int run, string text)
    {
        var kind = (int)(StageShared.Chance(seed, run, 20) * 3) % 3;
        return kind == Letters && (ScriptDirection.IsRightToLeft(text) || !ScriptDirection.AllowsGlyphMotion(text)) ? Huge : kind;
    }

    /// <summary>The size the small stack sets its words at.</summary>
    public static double SmallSize(double unit) => unit * 0.6;

    /// <summary>How far apart the stack sets its rows: a word, and its reading when the line has readings.</summary>
    public static double StackStep(double unit, bool readings, double point = 1) =>
        unit * 0.85 + (readings ? StageShared.ReadingSlot(SmallSize(unit), point: point) : 0);

    /// <summary>One continuous path for word <paramref name="index"/>: where it landed, gliding into the stack by its rank.</summary>
    public static PointD Place(int index, double rank, long seed, double step, StageRect bounds)
    {
        var settle = StageShared.Glide(Math.Min(rank, 1.2) / 1.2);
        var sway = (StageShared.Chance(seed, index, 21) - 0.5) * 0.24 * bounds.Width;
        var home = new PointD(bounds.MidX + sway, bounds.MidY + (StageShared.Chance(seed, index, 22) - 0.5) * 0.16 * bounds.Height);
        var slot = Math.Min(3, Math.Max(rank, 1));
        var stack = new PointD(bounds.MidX - bounds.Width * 0.2 + index % 2 * bounds.Width * 0.04,
                               bounds.MinY + bounds.Height * 0.12 + (slot - 1) * step);
        return new PointD(home.X + (stack.X - home.X) * settle, home.Y + (stack.Y - home.Y) * settle);
    }
}
