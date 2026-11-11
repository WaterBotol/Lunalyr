// Ported from the Mac app's BackingVocals.swift (BackingVocalLayer's timing and placement) and LayerExit.swift
// (LayerExit.backing): Lunalyr's own code. The text split itself is Lunalyr.Core.Lyrics.Parsing.BackingVocals.

using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>A credit's band on the stage while it is up (credits are not drawn on Windows yet; kept for the placement rule).</summary>
public readonly record struct CreditBand(double Start, double End, bool Top);

/// <summary>
/// Backing vocals as their own layer: small, dim and behind the lead, easing in, drifting a little, and staying a
/// beat after they were sung, in a band near the top or the foot of the stage.
/// </summary>
public static class BackingVocalLayout
{
    /// <summary>How long a group is held at least, however brief the singing was.</summary>
    public const double MinimumHold = 1.1;
    public const double FadeIn = 0.45;
    public const double FadeOut = 0.9;

    /// <summary>The box a group is set in, as a share of the stage.</summary>
    public static SizeD Box(SizeD stage) => new(stage.Width * 0.6, stage.Height * 0.26);

    /// <summary>When the group stops being held.</summary>
    public static double Hold(BackingGroup group) => Math.Max(group.End, group.Start + MinimumHold);

    /// <summary>0…1: eases in over the first moments, holds, then fades out slowly.</summary>
    public static double Level(BackingGroup group, double time)
    {
        var start = group.Start - 0.12;
        var hold = Hold(group);
        if (time < start) return 0;
        if (time < start + FadeIn) return Easing.Smoothstep((time - start) / FadeIn);
        if (time <= hold) return 1;
        return 1 - Easing.Smoothstep((time - hold) / FadeOut);
    }

    /// <summary>0 while the group is up, then 0…1 as it leaves.</summary>
    public static double ExitProgress(BackingGroup group, double time)
    {
        var hold = Hold(group);
        return time <= hold ? 0 : Easing.Smoothstep((time - hold) / FadeOut);
    }

    /// <summary>Backing vocals sink as they leave: a small settle downward and shrink, no blur.</summary>
    public static (double Offset, double Scale) Exit(double progress, double size)
    {
        var p = Math.Min(1, Math.Max(0, progress));
        return (p * size * 0.3, 1 - 0.06 * p);
    }

    /// <summary>
    /// The band a group takes: its own, unless a credit holds that band while the group is up and the other band is
    /// free then. Decided for the group's whole life, so it never jumps between bands.
    /// </summary>
    public static bool OnTop(BackingGroup group, bool preferred, ReadOnlySpan<CreditBand> credits)
    {
        double from = group.Start - 0.12, to = Hold(group) + FadeOut;
        bool Busy(bool top, ReadOnlySpan<CreditBand> bands)
        {
            foreach (var band in bands)
                if (band.Top == top && band.Start < to && band.End > from) return true;
            return false;
        }
        return Busy(preferred, credits) && !Busy(!preferred, credits) ? !preferred : preferred;
    }

    /// <summary>A steady 0…1 per group and salt, so a group keeps its place.</summary>
    public static double Unit(int seed, int index, int salt)
    {
        unchecked
        {
            var value = (ulong)((long)Math.Abs((long)seed) * 2654435761L + index * 40503L + salt * 9973L);
            value ^= value >> 13;
            value *= 0x2545F4914F6CDD1DUL;
            value ^= value >> 29;
            return (double)(value % 10_000) / 10_000;
        }
    }

    /// <summary>
    /// Where a group's box is centred on the stage: alternately up near the top or down near the foot, away from the
    /// centre where the lead sits, drifting slowly.
    /// </summary>
    public static PointD Position(BackingGroup group, int seed, SizeD stage, double baseSize, double time, bool reduceMotion,
                                  ReadOnlySpan<CreditBand> credits = default)
    {
        var top = OnTop(group, (group.Id + Math.Abs((long)seed)) % 2 == 0, credits);
        var x = 0.30 + 0.40 * Unit(seed, group.Id, 1);
        var y = top ? 0.14 + 0.08 * Unit(seed, group.Id, 2) : 0.80 + 0.07 * Unit(seed, group.Id, 3);
        var drift = reduceMotion ? 0 : Math.Sin(time * 0.45 + group.Id) * baseSize * 0.07;
        return new PointD(stage.Width * x, stage.Height * y + drift);
    }
}
