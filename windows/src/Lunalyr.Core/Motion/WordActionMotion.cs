// Word presence (Mac queue item 5) and a word symbol's fade. The action words' motion itself is LivelyAction (the Mac's
// 16 Nov 2026 redo); the 15 Nov motion it replaced is gone, as on the Mac.

using Lunalyr.Core.Compositions;

namespace Lunalyr.Core.Motion;

/// <summary>
/// How far a stronger word presence takes the in, hold and out of a word (Mac queue item 5). <c>p</c> = 1 is today's
/// look, exactly; each part scales its departure from rest by its own share of the extra.
/// </summary>
public readonly record struct WordPresence(double In, double Hold, double Glow, double Out)
{
    public static WordPresence Today => new(1, 1, 1, 1);

    /// <summary>The strength the user chose from the before/after sample (15 Nov 2026).</summary>
    public const double Chosen = 2;

    /// <summary>
    /// The stage layouts' own presence, laid on each word (their in, hold and out are their own): a rise of 0.32 em and
    /// a grow from 88% per unit of extra as the word arrives, a swell of 6% while it is sung, and a drift up of 0.26 em
    /// with a shrink of 4% as the line leaves. Offsets in ems (up negative); identity at strength 1.
    /// </summary>
    public static (double Dy, double Scale, double Blur) Stage(double strength, double arrival, double sung, double leave)
    {
        var extra = double.IsFinite(strength) ? Math.Clamp(strength, 1, 2) - 1 : 0;
        if (extra <= 0) return (0, 1, 0);
        var coming = 1 - Math.Clamp(arrival, 0, 1);
        leave = Math.Clamp(leave, 0, 1);
        var dy = 0.32 * extra * coming - 0.26 * extra * leave;
        var scale = (1 - 0.12 * extra * coming) * (1 + 0.06 * extra * Math.Clamp(sung, 0, 1)) * (1 - 0.04 * extra * leave);
        // Blur in points: a soft focus on arrival and as it leaves.
        var blur = 2.0 * extra * coming + 2.4 * extra * leave;
        return (dy, scale, blur);
    }

    /// <summary>At strength <paramref name="p"/>: the entrance travels 0.8 more per unit of extra, the hold swells
    /// 0.7 more, the glow 0.35 more and the exit drifts 0.8 more.</summary>
    public static WordPresence At(double p)
    {
        var extra = double.IsFinite(p) ? Math.Clamp(p, 1, 2) - 1 : 0;
        return new(1 + 0.8 * extra, 1 + 0.7 * extra, 1 + 0.35 * extra, 1 + 0.8 * extra);
    }

    public bool IsToday => In == 1 && Hold == 1 && Glow == 1 && Out == 1;
}

public static class WordActionMotion
{
    /// <summary>A word symbol's opacity <paramref name="sinceOnset"/> seconds after its word's onset: smoothstep over 0.3 s.</summary>
    public static double SymbolOpacity(double sinceOnset) => Easing.Smoothstep(sinceOnset / 0.3);
}
