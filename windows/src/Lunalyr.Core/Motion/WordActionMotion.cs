// An action word's motion (the Mac's cba108f, ported exactly): a pure function of the time since the word's onset,
// exactly identity before it and once it is over. Render-only, layered on the layout's own transforms.

using Lunalyr.Core.Compositions;

namespace Lunalyr.Core.Motion;

/// <summary>
/// An action word's pose: offsets in ems (up is negative), a horizontal turn scale (cos of the spin), a scale, an
/// opacity, and the colour cues (warm, cool, glow), which never lighten the word.
/// </summary>
public readonly record struct WordActionPose(double Dx, double Dy, double TurnScale, double Scale, double Opacity,
                                             double Warm, double Cool, double Glow)
{
    public static WordActionPose Identity => new(0, 0, 1, 1, 1, 0, 0, 0);

    public bool IsIdentity => this == Identity;

    /// <summary>What the word's rendered sRGB colour is multiplied by (never above 1).</summary>
    public (double R, double G, double B) Multiply => (1 - 0.35 * Cool, 1 - 0.25 * Warm - 0.12 * Cool, 1 - 0.55 * Warm);

    public bool Tints => Warm > 0 || Cool > 0;

    /// <summary>The glow shadow's opacity (in the word's own glow colour).</summary>
    public double GlowOpacity => 0.75 * Glow;

    /// <summary>The glow shadow's radius for a word of <paramref name="size"/> (at least 1).</summary>
    public double GlowRadius(double size) => Math.Max(1, 0.22 * size * Glow);
}

public static class WordActionMotion
{
    /// <summary>How long each action runs, in seconds.</summary>
    public static double Duration(WordAction action) => action switch
    {
        WordAction.Spin => 0.8,
        WordAction.Float => 0.9,
        WordAction.Dip => 0.7,
        WordAction.Shake => 0.6,
        WordAction.Hop => 0.7,
        WordAction.Grow or WordAction.Shrink => 0.8,
        _ => 0.9, // dissolve, flicker, frost
    };

    public static double Smoothstep(double x)
    {
        if (double.IsNaN(x)) return 0;
        var c = Math.Clamp(x, 0, 1);
        return c * c * (3 - 2 * c);
    }

    public static double Bump(double p, double rise, double fall) =>
        Smoothstep(p / rise) * (1 - Smoothstep((p - (1 - fall)) / fall));

    /// <summary>
    /// The pose <paramref name="elapsed"/> seconds after the word's onset. Under Reduce Motion nothing moves, scales,
    /// turns or fades: only a colour cue shows.
    /// </summary>
    public static WordActionPose Pose(WordAction action, double elapsed, bool reduceMotion)
    {
        var duration = Duration(action);
        if (!(elapsed > 0) || elapsed >= duration) return WordActionPose.Identity;
        var p = elapsed / duration;
        if (reduceMotion)
        {
            var cue = Bump(p, 0.2, 0.5);
            return action switch
            {
                WordAction.Flicker => WordActionPose.Identity with { Warm = 0.5 * cue, Glow = 0.5 * cue },
                WordAction.Frost => WordActionPose.Identity with { Cool = 0.5 * cue },
                _ => WordActionPose.Identity with { Glow = 0.6 * cue },
            };
        }
        switch (action)
        {
            case WordAction.Spin:
            {
                var eased = p * p * p * (p * (6 * p - 15) + 10);
                var turn = 2 * Math.PI * eased;
                return WordActionPose.Identity with { TurnScale = Math.Cos(turn) };
            }
            case WordAction.Float:
            {
                var lift = Bump(p, 0.35, 0.3);
                return WordActionPose.Identity with { Dy = -0.22 * lift - 0.02 * Math.Sin(4 * Math.PI * p) * lift };
            }
            case WordAction.Dip:
                return WordActionPose.Identity with { Dy = 0.16 * Bump(p, 0.3, 0.55) };
            case WordAction.Shake:
                return WordActionPose.Identity with { Dx = 0.045 * Math.Sin(10 * Math.PI * p) * (1 - p) * Smoothstep(p / 0.08) };
            case WordAction.Hop:
                return WordActionPose.Identity with
                {
                    Dy = p < 0.6 ? -0.24 * Math.Sin(Math.PI * p / 0.6) : -0.06 * Math.Sin(Math.PI * (p - 0.6) / 0.4),
                };
            case WordAction.Grow:
                return WordActionPose.Identity with { Scale = 1 + 0.16 * Bump(p, 0.35, 0.4) };
            case WordAction.Shrink:
                return WordActionPose.Identity with { Scale = 1 - 0.16 * Bump(p, 0.35, 0.4) };
            case WordAction.Dissolve:
                return WordActionPose.Identity with { Opacity = 1 - 0.7 * Bump(p, 0.3, 0.45) };
            case WordAction.Flicker:
            {
                var heat = Bump(p, 0.15, 0.35);
                return WordActionPose.Identity with
                {
                    Warm = 0.6 * heat,
                    Glow = heat * (0.6 + 0.4 * Math.Abs(Math.Sin(12 * Math.PI * p))),
                    Opacity = 1 - 0.12 * heat * Math.Abs(Math.Sin(14.6 * Math.PI * p)),
                };
            }
            default: // frost
                return WordActionPose.Identity with { Cool = 0.7 * Bump(p, 0.25, 0.4) };
        }
    }

    /// <summary>A word symbol's opacity <paramref name="sinceOnset"/> seconds after its word's onset: smoothstep over 0.3 s.</summary>
    public static double SymbolOpacity(double sinceOnset) => Smoothstep(sinceOnset / 0.3);
}
