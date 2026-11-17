// The carrying layouts' hand-over from one line to the next inside a phrase run, ported from the Mac's
// LyricPhraseRuns.swift (StageCarry.heldUntil, handoverStart and the StageMotion hand-overs; ddb1a3c5, e6959bca; Lunalyr's
// own code). Torque's column turns on from one line's last face into the next line's first, Hinge's last page turns
// over onto the next line's page, and Portal's trail climbs away as the next line's first pill takes its place. A line
// that hands on is held on the stage until its hand-over has played; that hold is its exit.

using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

public static class StageCarryMotion
{
    /// <summary>Layouts whose stage hands one line straight on to the next inside a run: Torque, Hinge and Portal.</summary>
    public static bool CarriesStage(this CompositionKind kind) =>
        kind is CompositionKind.Torque or CompositionKind.Hinge or CompositionKind.Portal;

    /// <summary>
    /// When a carrying layout's hand-over to the next line starts, or null (no hand-over, or a layout that does not
    /// carry). Text that is not part of the stage (a translation, a whole-line reading) fades from here, so it never sits
    /// over the next line's (the Mac's e6959bca).
    /// </summary>
    public static double? HandoverStart(StageCarry? carry, CompositionKind kind)
    {
        if (!kind.CarriesStage() || carry?.Outgoing is not { } hand) return null;
        return kind switch
        {
            CompositionKind.Torque => TorqueHandover(hand),
            CompositionKind.Hinge => HingeHandover(hand),
            _ => PortalHandover(hand),
        };
    }

    /// <summary>When a carrying layout's line that hands on to the next starts its own exit: once the hand-over has played.</summary>
    public static double? HeldUntil(StageCarry? carry, CompositionKind kind)
    {
        if (!kind.CarriesStage() || carry?.Outgoing is not { } hand) return null;
        return kind switch
        {
            CompositionKind.Torque => TorqueHandover(hand) + TorqueHandoverSpan,
            CompositionKind.Hinge => HingeHandover(hand) + HingeGeometry.Turn,
            _ => PortalHandover(hand),
        };
    }

    // ---- Torque ----

    /// <summary>
    /// When the column starts its turn from one line's last face to the next line's first, as between two faces of one
    /// line: facing the new line as its first word is sung, never before the last word has landed, and never before the
    /// new line is on the stage.
    /// </summary>
    public static double TorqueHandover(StageCarry.Hand hand)
    {
        var start = hand.Boundary.NextOnset;
        var early = start - StageMotion.Lead - TorqueGeometry.TurnLead;
        var turn = Math.Min(start - StageMotion.Lead, Math.Max(early, hand.Boundary.LastOnset + TorqueGeometry.Unit));
        return Math.Max(turn, hand.LaterStart);
    }

    /// <summary>The hand-over turn's length, with its overshoot settled.</summary>
    public const double TorqueHandoverSpan = 3 * TorqueGeometry.Unit * (1 + TorqueLaunch);

    /// <summary>The share of the turn's out-ease over which the hand-over turn runs up to speed (the Mac's <c>torqueLaunch</c>).</summary>
    public const double TorqueLaunch = 0.15;

    /// <summary>
    /// The column's turn, <paramref name="x"/> (0…1 of three units) since it started: Torque's out-ease, its overshoot and
    /// its landing, but run up to speed over the first <see cref="TorqueLaunch"/> instead of starting at full speed (the
    /// Mac's <c>torqueTurn</c>), so the face being sung never snaps away from a standstill as the next line begins.
    /// </summary>
    public static double TorqueTurn(double x)
    {
        if (!(x > 0)) return 0;
        var eased = x < TorqueLaunch ? x * x / (2 * TorqueLaunch) : x - TorqueLaunch / 2;
        return TorqueGeometry.Out(eased);
    }

    /// <summary>
    /// The column's turn from one line to the next, in faces (0…1, a little past during the overshoot): the turn between
    /// two faces of a line. Both lines work it out from the same hand-over, so they turn as one column.
    /// </summary>
    public static double TorqueHandoverTurn(StageCarry.Hand hand, double time)
    {
        return TorqueTurn((time - TorqueHandover(hand)) / (3 * TorqueGeometry.Unit));
    }

    // ---- Hinge ----

    /// <summary>
    /// When the last page of a line starts to turn over onto the next line's first page, as a page turns within a line.
    /// The next line need not be on the stage yet: its page lies beneath, uncovered as the turning page lifts off it.
    /// </summary>
    public static double HingeHandover(StageCarry.Hand hand) =>
        hand.Boundary.NextOnset - StageMotion.Lead - HingeGeometry.TurnLead;

    /// <summary>How far the hand-over page has turned, 0…1, at an ordinary page's pace, from the hand-over both lines share.</summary>
    public static double HingeHandoverTurn(StageCarry.Hand hand, double time) =>
        HingeGeometry.EaseInOut((time - HingeHandover(hand)) / HingeGeometry.Turn);

    /// <summary>
    /// How much of the next line's page the turning page has uncovered, 0…1: none while the page still lies flat over it,
    /// all of it once the page stands edge-on, as the turning page fades on its way there.
    /// </summary>
    public static double HingeUncovered(StageCarry.Hand hand, double time)
    {
        var turn = HingeHandoverTurn(hand, time);
        if (turn >= 0.5) return 1;
        var edgeOn = Math.Abs(Math.Cos(turn * Math.PI));
        return 1 - Easing.Smoothstep((edgeOn - 0.1) / 0.35);
    }

    /// <summary>A turned-over last page fading once the hand-over has landed, seconds.</summary>
    public const double HingeCarryFade = 0.4;

    // ---- Portal ----

    /// <summary>When the trail starts to climb away, a moment ahead of the next line's first pill, so the live pill is lifting clear as that pill arrives.</summary>
    public static double PortalHandover(StageCarry.Hand hand) => hand.Boundary.NextOnset - StageMotion.Lead - PortalHandoverLead;

    /// <summary>How far ahead of the next line's first pill the trail starts to climb.</summary>
    public const double PortalHandoverLead = 0.12;

    /// <summary>A steady 0…1 for a run's seed and a salt, as a line's own chance is for a line (the Mac's <c>runChance</c>).</summary>
    public static double RunChance(int seed, int salt)
    {
        unchecked
        {
            var magnitude = seed < 0 ? 0UL - (ulong)(long)seed : (ulong)seed;
            var value = magnitude * 2654435761UL + 977UL * 40503UL + (ulong)salt * 9973UL;
            value ^= value >> 13;
            value *= 0x2545F4914F6CDD1DUL;
            value ^= value >> 29;
            return value % 10_000 / 10_000.0;
        }
    }
}
