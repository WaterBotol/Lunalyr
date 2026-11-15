using static Lunalyr.Core.Motion.MotionMath;

namespace Lunalyr.Core.Motion;

/// <summary>The three rolling (word-at-a-time) arrangements.</summary>
public enum RollingStyle
{
    /// <summary>The sung word large, older words smaller and spread out sideways behind it.</summary>
    Fan = 0,
    /// <summary>A steady flush column (the stage sets each word's indent): the sung word steps forward a size.</summary>
    Column = 1,
    /// <summary>A loose collage of labels, each with its own offset and tilt, the sung word a size up.</summary>
    Collage = 2,
}

/// <summary>
/// The rolling layouts' curves: where a word sits by rank, how it breathes while sung, and the older eases.
/// </summary>
/// <remarks>
/// Re-implemented from a description of the Mac's behaviour (its lines trace back to the squashed import commit, so
/// they are not translated). Behaviour, as specified:
/// <list type="bullet">
/// <item><b>Rank</b> is a word's distance from the word being sung (0), growing as later words arrive; it is held to
/// [-2, 9]. Its resting height falls by 0.66 per rank plus extra clearance right around rank 0 that saturates
/// (0.70 × tanh(1.2 rank), 0.86 for the collage); the column sits 0.35 higher and the collage 0.30.</item>
/// <item><b>Opacity</b> by rank is full up to 2.4 and gone at 4.1 (smoothstep), so words retire before a stage edge;
/// it also fades over one second from 0.45 s after the word ends.</item>
/// <item><b>Focus</b> (e^(−2.4 rank²)) is what makes rank 0 the hero: larger in the fan (0.70 → 1.04). Older fan words
/// drift sideways and tilt by a per-word phase from the seed.</item>
/// <item>The column and the collage (Lunalyr's own lines since Mac Test Build 88, ported): the column is flush (x 0;
/// the stage sets each word's indent), its sung word steps forward and the words behind settle back a size
/// (0.80 → 1.10), tilting by sin(phase) × 0.8 × (1 − focus); nothing varies with rank, so it does not snake as it
/// rolls. The collage's labels keep their own offset, sin(1.3 phase) × 0.42 + sin(0.72 rank) × 0.12, and tilt,
/// sin(phase + 0.18 rank) × (1.4 + 1.6 (1 − focus)), and the sung one is a size up (0.80 → 1.06).</item>
/// <item><b>Breathing</b> while sung: seeded noise (never periodic, zero at the onset) lifts and swells the hero by
/// how long the word is held and how loud the music is; older words blur from rank 2.4 on. Reduce Motion: no
/// breathing, no tilt, no blur.</item>
/// </list>
/// All inputs are sanitised (not-finite values take the low end of their range), so any input gives a finite pose.
/// </remarks>
public static class RollingMotion
{
    private const double TwoPi = 6.2831853;

    /// <summary>
    /// A damped arrival over progress 0…1 with a small overshoot: 0 at 0, exactly 1 at 1, never above 1.12. It is
    /// the step response of a spring (ζω = 7, damped frequency 10) normalised to land on 1.
    /// </summary>
    public static double MotionEase(double progress)
    {
        var t = Clamp(progress, 0, 1);
        if (t == 0 || t == 1) return t;
        return Clamp(LyricMotion.SpringStep(t, EaseSpring) / EaseEnd, 0, 1.12);
    }

    private static readonly Spring EaseSpring = new(Math.Sqrt(149), 7 / Math.Sqrt(149));
    private static readonly double EaseEnd = LyricMotion.SpringStep(1, EaseSpring);

    /// <summary>
    /// How far the rolling cluster has followed an onset, 0…1: a smootherstep (6t⁵ − 15t⁴ + 10t³) lasting 82% of the
    /// cadence, held between 0.2 and 0.58 s. 0 before the onset.
    /// </summary>
    public static double MotionHandoff(double age, double cadence)
    {
        if (!double.IsFinite(age) || age <= 0) return 0;
        var span = Clamp(cadence * .82, .20, .58);
        var t = Clamp(age / span, 0, 1);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    /// <summary>Opacity by rank: 1 up to rank 2.4, 0 from 4.1.</summary>
    public static double RankOpacity(double rank) => 1 - Smooth((Clamp(rank, 0, 9) - 2.4) / 1.7);

    /// <summary>
    /// The vertical place <see cref="Pose"/> gives a rank before breathing: the pose's Y is this plus the breath.
    /// Stages that space words by their measured size use the difference as a small per-word lift.
    /// </summary>
    public static double Baseline(RollingStyle style, double rank) => Lift(style) - Separation(style, Clamp(rank, -2, 9));

    private static double Separation(RollingStyle style, double rank) =>
        .66 * rank + (style == RollingStyle.Collage ? .86 : .70) * Math.Tanh(rank * 1.2);

    private static double Lift(RollingStyle style) => style switch
    {
        RollingStyle.Column => .35,
        RollingStyle.Collage => .30,
        _ => 0,
    };

    /// <summary>A word's pose in a rolling layout at <paramref name="rank"/>, <paramref name="age"/> seconds after its onset.</summary>
    public static WordPose Pose(RollingStyle style, double rank, double age, double duration, ulong seed, double energy,
                                bool reduceMotion)
    {
        rank = Clamp(rank, -2, 9);
        age = Clamp(age, -60, 3600);
        duration = Clamp(duration, .06, 30);
        energy = Clamp(energy, 0, 1);
        var history = Max(0, rank);
        var phase = (double)(seed % 1021) / 1021 * TwoPi;
        var focus = Math.Exp(-rank * rank * 2.4);
        var afterWord = 1 - Smooth(age - duration - .45);
        var opacity = RankOpacity(history) * afterWord;
        var y = Baseline(style, rank);

        double x, scale, angle;
        switch (style)
        {
            case RollingStyle.Fan:
                var reach = Min(history, 1);
                x = Math.Sin(history * .92 + phase * .22) * reach * .70;
                scale = .70 + .34 * focus;
                angle = Math.Sin(phase + history * .6) * reach * 2.4;
                break;
            case RollingStyle.Column:
                // A flush column (the stage sets each word's indent): differences in type do the emphasis, and the
                // word being sung steps forward while the words behind it settle back a size. Nothing here varies with
                // rank, so the column does not snake as it rolls.
                x = 0;
                scale = .80 + .30 * focus;
                angle = Math.Sin(phase) * .8 * (1 - focus);
                break;
            default:
                // Labels travel into a loose collage: each keeps its own offset and tilt, with only a gentle drift as
                // the stack rolls, and the word being sung is a size up.
                x = Math.Sin(phase * 1.3) * .42 + Math.Sin(rank * .72) * .12;
                scale = .80 + .26 * focus;
                angle = Math.Sin(phase + rank * .18) * (1.4 + 1.6 * (1 - focus));
                break;
        }

        double blur = 0;
        if (reduceMotion)
        {
            angle = 0;
        }
        else if (age >= 0)
        {
            var sustain = Smooth((duration - .35) / 1.4);
            var arc = Math.Sin(Clamp(age / duration, 0, 1) * Pi);
            var attack = Smooth(age / .12);
            var breath = LyricMotion.Noise2(seed, age * (.32 + energy * .3)) * sustain * attack;
            y += breath * .055 * focus;
            scale += focus * (sustain * arc * .12 + energy * .07 * attack);
            angle += LyricMotion.Noise(seed ^ 0x5151UL, age * .42) * energy * .75 * focus;
            blur = Smooth((history - 2.4) / 1.7) * 1.6;
        }
        return new WordPose(x, y, scale, angle, opacity, blur);
    }

    /// <summary>
    /// The older sung-word gesture: while a word is sung (and for 0.65 s after, fading) it lifts on a sine arc over
    /// its length, swells and sways by how long it is held, arriving over 0.18 s; louder music lifts and swells it a
    /// little more. Exact rest before the onset, after the fade, and under Reduce Motion.
    /// </summary>
    public static WordPose VocalGesture(double age, double duration, ulong seed, double energy, bool reduceMotion)
    {
        if (reduceMotion || !double.IsFinite(age) || age < 0) return WordPose.Rest;
        duration = Clamp(duration, .06, 30);
        energy = Clamp(energy, 0, 1);
        var heat = 1 - Smooth((age - duration) / .65);
        if (heat <= 0) return WordPose.Rest;
        var phase = (double)(seed % 997) / 997 * TwoPi;
        var held = Smooth((duration - .25) / 1.1);
        var strength = heat * Smooth(age / .18);
        var arc = Math.Sin(Clamp(age / duration, 0, 1) * Pi);
        var sway = Math.Sin(age * (2.2 + energy) + phase) * held;
        return new WordPose(
            X: Math.Sin(age * 1.65 + phase) * .025 * held * strength,
            Y: (-.13 * held * arc + sway * .055 - energy * .035) * strength,
            Scale: 1 + (held * arc * .12 + energy * .045) * strength,
            Angle: sway * 1.2 * strength,
            Opacity: 1,
            Blur: 0);
    }
}
