using System.Runtime.CompilerServices;

namespace Lunalyr.Core.Motion;

/// <summary>
/// Seek-safe word arrival and the next-word preview, as pure functions of playback time.
/// </summary>
/// <remarks>
/// Re-implemented from a description of the Mac's behaviour (most of its lines trace back to the squashed import
/// commit; the preview ramp is Lunalyr's own and ported as is):
/// <list type="bullet">
/// <item><see cref="Reveal"/> is 0 before the word's start and a smoothstep up to 1 over 65% of the word's length,
/// held to 0.16–0.46 s. Not-finite input gives 0.</item>
/// <item><see cref="Visibility"/> adds the preview: with the preview on, only the next word ramps (smoothstep) from 0
/// at <c>lead</c> seconds before its onset up to a quarter at the onset; from the onset a previewed word goes on from
/// a quarter (0.25 + 0.75 × reveal), so it never blinks at the hand-over.</item>
/// <item><see cref="Settle"/> maps reveal progress to a settle with a slight overshoot: a cubic through (0, 0) and
/// (1, 1), flat at 1, peaking just under 1.05.</item>
/// </list>
/// </remarks>
public static class WordReveal
{
    /// <summary>A word's arrival, 0 before <paramref name="start"/> and 1 once its entrance is over.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Reveal(double time, double start, double end)
    {
        if (!double.IsFinite(time) || !double.IsFinite(start) || !double.IsFinite(end) || time < start) return 0;
        var span = Math.Clamp((end - start) * .65, .16, .46);
        return Easing.Smoothstep((time - start) / span);
    }

    /// <summary>
    /// How visible a word is, the next-word preview included. <paramref name="isNextWord"/> marks the one word that
    /// may be previewed; <paramref name="lead"/> is how many seconds ahead its preview begins.
    /// </summary>
    public static double Visibility(double time, double start, double end, bool previewEnabled, bool isNextWord,
                                    double lead)
    {
        if (!double.IsFinite(time) || !double.IsFinite(start) || !double.IsFinite(end)) return 0;
        if (time < start)
        {
            if (!previewEnabled || !isNextWord || !double.IsFinite(lead) || lead <= 0) return 0;
            var t = Math.Min(1, Math.Max(0, 1 - (start - time) / lead));
            return .25 * t * t * (3 - 2 * t);
        }
        var progress = Reveal(time, start, end);
        return previewEnabled ? .25 + .75 * progress : progress;
    }

    /// <summary>Reveal progress to a settle with a slight overshoot (≤ 1.05); 0 at or below 0, exactly 1 from 1.</summary>
    public static double Settle(double progress)
    {
        if (!double.IsFinite(progress) || progress <= 0) return 0;
        if (progress >= 1) return 1;
        var u = 1 - progress;
        // 1 − 1.85u³ + 0.85u² (u = 1 − progress): leaves 0 steeply, overshoots slightly, lands flat on 1.
        return 1 - u * u * (1.85 * u - .85);
    }

    /// <summary>
    /// How much of a line's text has been revealed, weighting each word by its length in characters (the Echo
    /// layout's backdrop). Continuous across onsets; 1 for no words.
    /// </summary>
    public static double LineRevealed(IReadOnlyList<Lunalyr.Core.Lyrics.LyricWord> words, double time)
    {
        double total = 0, shown = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            double weight = Math.Max(1, Lunalyr.Core.Lyrics.Timing.TextElements.Count(word.Text));
            total += weight;
            shown += weight * LyricMotion.Settle(time - word.Start, Math.Min(.46, Math.Max(.16, (word.End - word.Start) * .65)));
        }
        return total > 0 ? shown / total : 1;
    }
}

/// <summary>An axis-aligned box: origin and size.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height);

/// <summary>A link between two words' boxes, from word <see cref="From"/> to word <see cref="To"/>.</summary>
public readonly record struct Segment(double Ax, double Ay, double Bx, double By, int From, int To);
