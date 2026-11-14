using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Lyrics.Timing;

/// <summary>A stretch of the song where singing is heard, in seconds.</summary>
public readonly record struct VocalWindow(double Start, double End);

/// <summary>
/// A break in the singing. <see cref="End"/> is the next vocal's start, the track's end when the break runs to it, or
/// -1 when the track length is unknown; <see cref="NextVocal"/> is the index of the vocal window that ends the break,
/// or -1 when none does.
/// </summary>
public readonly record struct VocalGap(double Start, double End, int NextVocal)
{
    /// <summary>
    /// How present the instrumental cue is at <paramref name="time"/>: it eases in over 0.7 s from the break's start
    /// and, when singing follows, out over the 0.65 s before it returns.
    /// </summary>
    public double Presence(double time)
    {
        var enter = Easing.Smoothstep((time - Start) / 0.7);
        var leave = NextVocal >= 0 ? 1 - Easing.Smoothstep((time - End + 0.65) / 0.65) : 1;
        return Math.Min(enter, leave);
    }

    /// <summary>
    /// The interlude dots for this break at <paramref name="time"/>, or null while they are invisible. They fade in
    /// after a short delay and out before the singing returns (ahead of it by up to the next-word preview lead).
    /// </summary>
    public InstrumentalCue? InterludeCue(double time, double previewLead, double energy = 0)
    {
        var elapsed = time - Start;
        var fadeIn = Easing.Smoothstep((elapsed - WallpaperMotion.InterludeInDelay) / WallpaperMotion.InterludeIn);
        var knownEnd = End > Start;
        var cueEnd = End - (NextVocal >= 0 ? Math.Min(previewLead, Math.Max(0, End - Start - 0.85)) : 0);
        // Leaves almost as gently as it came, not in a quarter second.
        var fadeOut = knownEnd ? Easing.Smoothstep((cueEnd - time) / WallpaperMotion.InterludeOut) : 1;
        var opacity = Math.Min(fadeIn, fadeOut);
        double? progress = knownEnd ? Math.Min(1, Math.Max(0, elapsed / Math.Max(0.01, cueEnd - Start))) : null;
        var seed = LyricPhraseTiming.TimeSeed(Start) ^ 0x1D07_5EEDUL;
        return opacity > 0 ? new InstrumentalCue(elapsed, opacity, progress, seed, energy, fadeOut) : null;
    }
}

/// <summary>The three interlude dots of an instrumental break.</summary>
/// <param name="Elapsed">Seconds since the break began.</param>
/// <param name="Opacity">0…1.</param>
/// <param name="Progress">How far through the break, 0…1; null when its end is unknown.</param>
/// <param name="Seed">Seeds the dots' wander, so each break moves in its own way.</param>
/// <param name="Energy">Live or timing-derived intensity, 0…1: livelier music, livelier dots.</param>
/// <param name="FadeOut">1 until the cue starts leaving, falling to 0 as it goes (the orb shrinks as it leaves).</param>
public readonly record struct InstrumentalCue(double Elapsed, double Opacity, double? Progress, ulong Seed = 0x1D07_5EED,
                                             double Energy = 0, double FadeOut = 1)
{
    /// <summary>
    /// Vertical wander of one dot, -1…1. Each dot follows its own seeded noise over a shared slow swell, so the three
    /// drift like things floating rather than tracing a sine wave in lockstep.
    /// </summary>
    public double Bob(int index)
    {
        var pace = 0.38 + 0.22 * Math.Min(1, Math.Max(0, Energy));
        var own = LyricMotion.Noise2(unchecked(Seed + (ulong)index * 0x9E37_79B9UL), Elapsed * pace);
        var shared = LyricMotion.Noise(Seed ^ 0xA5A5UL, Elapsed * 0.21);
        return Math.Min(1, Math.Max(-1, own * 0.8 + shared * 0.45));
    }

    /// <summary>A gentle swell in size, -1…1, out of phase with the bob.</summary>
    public double Breath(int index) => LyricMotion.Noise(Seed ^ unchecked(0xB0UL + (ulong)index), Elapsed * 0.27);

    public double Highlight(int index) => Progress is { } p ? Easing.Smoothstep(p * 3 - index) : 0;
}

/// <summary>Finds the instrumental break, if any, at a moment of the song.</summary>
/// <remarks>
/// Re-implemented from a description of the Mac's behaviour (its lines trace back to the squashed import commit):
/// the windows must be finite, non-negative, not reversed and sorted by start, or nothing is found. Overlapping or
/// touching windows count as one stretch of singing. A break is the time before the first window (from 0), between
/// stretches, or after the last (to the track's end, or open-ended when the length is unknown); it counts only when it
/// lasts at least the minimum (held to 0.5–10 s, 2.4 when not finite). Nothing is found during singing, in a break
/// shorter than the minimum, for a negative or not-finite time, or at or after the track's end.
/// </remarks>
public static class VocalGaps
{
    public const double DefaultMinimum = 2.4;

    /// <summary>The break at <paramref name="time"/>, or null. Allocation-free: call it every frame.</summary>
    public static VocalGap? Find(ReadOnlySpan<VocalWindow> vocals, double time, double trackDuration,
                                 double minimum = DefaultMinimum)
    {
        if (vocals.Length > 20000 || !double.IsFinite(time) || time < 0) return null;
        minimum = double.IsFinite(minimum) ? Math.Clamp(minimum, 0.5, 10) : DefaultMinimum;
        var durationKnown = double.IsFinite(trackDuration) && trackDuration > 0;
        if (durationKnown && time >= trackDuration) return null;
        if (!AreValid(vocals)) return null;

        double silentSince = 0;
        var index = 0;
        while (index < vocals.Length)
        {
            var first = index;
            var start = vocals[index].Start;
            var end = vocals[index].End;
            if (time >= silentSince && time < start && start - silentSince >= minimum)
                return new VocalGap(silentSince, start, first);
            // Fold every window that starts before this stretch of singing ends into it.
            while (index + 1 < vocals.Length && vocals[index + 1].Start <= end)
            {
                index++;
                end = Math.Max(end, vocals[index].End);
            }
            if (time >= start && time < end) return null;
            silentSince = Math.Max(silentSince, end);
            index++;
        }
        if (time >= silentSince && (!durationKnown || trackDuration - silentSince >= minimum))
            return new VocalGap(silentSince, durationKnown ? trackDuration : -1, -1);
        return null;
    }

    private static bool AreValid(ReadOnlySpan<VocalWindow> vocals)
    {
        for (var i = 0; i < vocals.Length; i++)
        {
            var v = vocals[i];
            if (!double.IsFinite(v.Start) || !double.IsFinite(v.End) || v.Start < 0 || v.End < v.Start) return false;
            if (i > 0 && v.Start < vocals[i - 1].Start) return false;
        }
        return true;
    }
}
