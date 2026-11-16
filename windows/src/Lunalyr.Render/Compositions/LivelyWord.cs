// The whole-word part of an action word as every layout draws it (the Mac's LivelyActionEffect, 16 Nov 2026): the pose
// at a frame (the strength, the hold and a chorus repeat scaling it; Reduce Motion's glow-only cue), its transform in
// SwiftUI's order (scale about its anchor, the spin's turn in perspective, the tilt, the travel), and its glow.

using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Motion;
using Lunalyr.Render.Compositions.Stage;

namespace Lunalyr.Render.Compositions;

internal static class LivelyWord
{
    /// <summary>The hold the Mac assumes when a word's end is not known.</summary>
    public const double UnknownHold = 0.6;

    /// <summary>
    /// The pose of <paramref name="accent"/>'s action <paramref name="elapsed"/> seconds after the word's onset, for a word
    /// held <paramref name="hold"/> seconds, at "Action word strength" <paramref name="strength"/> (0.2, 0.5 or 1).
    /// Identity before the onset and once the motion is over.
    /// </summary>
    public static LivelyPose Pose(in RunAccent accent, double elapsed, double hold, double strength, bool reduceMotion)
    {
        if (accent.Action is not { } action) return LivelyPose.Identity;
        if (!double.IsFinite(hold) || hold <= 0) hold = UnknownHold;
        var duration = LivelyAction.Duration(action, hold);
        if (!(elapsed > 0) || !(elapsed < duration)) return LivelyPose.Identity;
        if (reduceMotion) return LivelyAction.ReduceMotionCue(action, elapsed, duration, accent.Gentle);
        var amount = (double.IsFinite(strength) ? strength : LivelyAction.DefaultAmount) * (accent.Gentle ? LivelyAction.GentleShare : 1);
        return LivelyPose.Make(action, elapsed, duration, LivelyAction.Intensity(amount, hold), hold);
    }

    /// <summary>How long <paramref name="accent"/>'s motion runs from the onset (0 for none).</summary>
    public static double Duration(in RunAccent accent, double hold) =>
        accent.Action is { } action ? LivelyAction.Duration(action, double.IsFinite(hold) && hold > 0 ? hold : UnknownHold) : 0;

    /// <summary>
    /// The pose as a transform of the word's own space (layout pixels), about the word's box: <paramref name="centre"/>
    /// and <paramref name="box"/> its size. Travel is in ems of <paramref name="size"/>. In SwiftUI's order: the scale
    /// about (centre, anchor), the spin's turn about the upright axis in perspective (0.5 of the width, the Mac's
    /// <c>rotation3DEffect</c>; its back never shows), the tilt about the centre, then the offset.
    /// </summary>
    /// <param name="flat">The turn drawn flat (the width scaled by its cosine), for what is drawn only through flat
    /// transforms, such as Portal's pill.</param>
    public static Matrix4x4 Transform(in LivelyPose pose, double size, Vector2 centre, SizeD box, bool flat = false)
    {
        if (pose.IsIdentity) return Matrix4x4.Identity;
        var anchor = new Vector2(centre.X, centre.Y + (float)((pose.AnchorY - 0.5) * box.Height));
        var m = Matrix4x4.CreateTranslation(-anchor.X, -anchor.Y, 0)
            * Matrix4x4.CreateScale((float)pose.Sx, (float)pose.Sy, 1)
            * Matrix4x4.CreateTranslation(anchor.X, anchor.Y, 0);
        if (pose.Turn != 0 && flat)
        {
            m *= Matrix4x4.CreateTranslation(-centre.X, -centre.Y, 0)
                * Matrix4x4.CreateScale((float)Math.Cos(pose.ShownTurn * Math.PI / 180), 1, 1)
                * Matrix4x4.CreateTranslation(centre.X, centre.Y, 0);
        }
        else if (pose.Turn != 0)
        {
            // The Mac's perspective is 0.5 of the word's width (measured 16 Nov 2026); TurnY takes the larger side,
            // which for a word is its width.
            m *= Homography.TurnY(pose.ShownTurn, centre, new SizeD(Math.Max(1, box.Width), Math.Min(box.Height, Math.Max(1, box.Width))), 0.5);
        }
        if (pose.Tilt != 0)
            m *= Matrix4x4.CreateTranslation(-centre.X, -centre.Y, 0)
                * Matrix4x4.CreateRotationZ((float)(pose.Tilt * Math.PI / 180))
                * Matrix4x4.CreateTranslation(centre.X, centre.Y, 0);
        if (pose.Dx != 0 || pose.Dy != 0) m *= Matrix4x4.CreateTranslation((float)(pose.Dx * size), (float)(pose.Dy * size), 0);
        return m;
    }

    /// <summary>
    /// A word symbol's entrance (the Mac's SymbolEntranceEffect, 16 Nov 2026): <paramref name="elapsed"/> seconds after
    /// its word's onset, it pops in (a springy scale with overshoot, a wobble, a rise), echoing the word's
    /// <paramref name="action"/>. A transform of the symbol's own space (layout pixels from its centre; <paramref name="box"/>
    /// its size), travel in ems of the symbol (half the word's <paramref name="size"/>); identity under Reduce Motion
    /// (it only fades) and once settled. <paramref name="cool"/>: how cold it lands (frost), 0…1.
    /// </summary>
    public static Matrix3x2 SymbolEntrance(WordAction? action, double elapsed, bool reduceMotion, double size, Vector2 box,
                                           out double cool)
    {
        cool = 0;
        if (SymbolEntrancePose.Entrance(elapsed, reduceMotion) is not { } e) return Matrix3x2.Identity;
        var p = SymbolEntrancePose.Make(action, e);
        cool = 0.9 * p.Cool;
        var em = (float)(size * 0.5);
        var anchor = new Vector2(0, (float)((p.AnchorY - 0.5) * box.Y));
        return Matrix3x2.CreateScale((float)p.Sx, (float)p.Sy, anchor)
            * Matrix3x2.CreateRotation((float)(p.Rotation * Math.PI / 180))
            * Matrix3x2.CreateTranslation((float)p.Dx * em, (float)p.Dy * em);
    }

    /// <summary>
    /// The frame round the word that its whirlwind and vignette are drawn in (the Mac draws them outside the word's scale
    /// and turn): the pose's tilt about <paramref name="centre"/>, then its travel in ems of <paramref name="size"/>.
    /// </summary>
    public static Matrix3x2 Frame(in LivelyPose pose, double size, Vector2 centre) =>
        Matrix3x2.CreateRotation((float)(pose.Tilt * Math.PI / 180), centre)
        * Matrix3x2.CreateTranslation((float)(pose.Dx * size), (float)(pose.Dy * size));

    /// <summary>The pose's glow in the word's own <paramref name="ink"/>: its opacity, and its radius for a word of <paramref name="size"/>.</summary>
    public static GlowSpec Glow(in LivelyPose pose, Vector4 ink, double size) =>
        pose.Glow > 0.002
            ? new GlowSpec(new Vector4(ink.X, ink.Y, ink.Z, (float)Math.Min(0.9, 0.75 * pose.Glow)),
                           (float)Math.Max(1, size * 0.22 * Math.Min(2, pose.Glow)))
            : GlowSpec.None;

    /// <summary>The ink colour the pose's tint lays over the word (<paramref name="colour"/> multiplied; never lighter).</summary>
    public static Vector4 Tinted(in LivelyPose pose, Vector4 colour, double opacity)
    {
        var (r, g, b) = pose.Multiply;
        return new Vector4(colour.X * (float)r, colour.Y * (float)g, colour.Z * (float)b, (float)(colour.W * opacity));
    }
}
