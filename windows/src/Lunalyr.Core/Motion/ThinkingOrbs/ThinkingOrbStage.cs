// Lunalyr's layer over the thinking-orbs engine (the Mac's ThinkingOrb.swift, 22128a2 and 13b8ed6, without the countdown
// ring): during an instrumental break the chosen state (listening by default) runs on the break's own clock, follows the
// music when audio is captured, and in the last moments tightens, brightens and cross-fades to breathing before it
// hands over to the next line. Colour comes from the lyric ink (ThinkingOrb.Colours, v1's palette).

using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Motion.ThinkingOrbs;

/// <summary>One layer to draw: a state, its weight, its engine time and resolved preset.</summary>
public readonly record struct OrbLayer(OrbState State, double Weight, double EngineT, OrbResolved Resolved);

/// <summary>
/// The orb on one frame: its design size (64 or 20), the scale from the design frame to device pixels, the focus, and
/// the music's mapping (swell for listening only, dot scale, brighten), plus up to two layers.
/// </summary>
public readonly record struct OrbStagePose(int Native, double K, double Focus, double Swell, double DotScale, double Brighten,
                                           double HaloAlpha, OrbLayer First, OrbLayer? Second)
{
    public int Count => Second is null ? 1 : 2;
}

/// <summary>The music as the orb hears it, smoothed: a one-pole follower per value, and the extra spin phase.</summary>
public sealed class OrbAudioFollower
{
    public const double Rise = 0.06, Fall = 0.25, MaxStep = 0.1;
    private double _wall = double.NaN;

    public double Level { get; private set; }
    public double Onset { get; private set; }
    public double Phase { get; private set; }

    /// <summary>Steps toward the analyser's levels; only when the wall clock advances (and only while an orb shows).</summary>
    /// <param name="rms">The level of the sound, 0…1 (the analyser's overall level).</param>
    /// <param name="bass">The bass level, 0…1.</param>
    /// <param name="pulse">The beat transient, 0…1, decaying after each hit.</param>
    public void Step(double wall, double rms, double bass, double pulse)
    {
        if (!double.IsFinite(wall)) return;
        if (double.IsNaN(_wall) || wall < _wall) { _wall = wall; return; }
        var dt = Math.Min(MaxStep, wall - _wall);
        _wall = wall;
        if (!(dt > 0)) return;
        var level = Math.Clamp(Finite(rms) * 2.6 + Finite(bass) * 0.8, 0, 1);
        var onset = Math.Clamp(Finite(pulse), 0, 1);
        Level = Follow(Level, level, dt);
        Onset = Follow(Onset, onset, dt);
        Phase += dt * ThinkingOrbStage.Speed * 0.35 * Level;
    }

    /// <summary>No music: back to the orb's own clock.</summary>
    public void Quiet()
    {
        Level = 0;
        Onset = 0;
    }

    /// <summary>A new break starts its phase afresh.</summary>
    public void Reset()
    {
        Level = Onset = Phase = 0;
        _wall = double.NaN;
    }

    private static double Follow(double y, double x, double dt) => y + (x - y) * (1 - Math.Exp(-dt / (x > y ? Rise : Fall)));

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}

public static class ThinkingOrbStage
{
    /// <summary>Upstream's <c>speed</c> prop: its tunings are spinner-paced, the orb runs at 0.55 of them.</summary>
    public const double Speed = 0.55;

    /// <summary>The hand-off: the orb shrinks and fades over this before the cue's end (the existing leave).</summary>
    public const double HandOff = 0.4;

    /// <summary>Upstream's static frame, used under Reduce Motion.</summary>
    public const double StillT = 0.6;

    /// <summary>The design the orb is drawn from: 20 below 40 pt, 64 otherwise.</summary>
    public static int Native(double diameterPoints) => diameterPoints < 40 ? 20 : 64;

    /// <summary>The orb's own clock, in seconds, for a break first seen <paramref name="elapsed"/> s ago.</summary>
    public static double Seconds(ulong seed, double elapsed, double audioPhase) =>
        (seed & 0xFFFF) / 65536.0 * 40 + elapsed * Speed + audioPhase;

    /// <summary>
    /// 0…1: the final window before the hand-off, min(2 s, half the span), eased in. 0 when the span is unknown.
    /// </summary>
    public static double Focus(double elapsed, double? span)
    {
        if (span is not { } s || !(s > 0)) return 0;
        var window = Math.Min(2.0, 0.5 * s);
        if (!(window > 0)) return 0;
        return Easing.Smoothstep((elapsed - (s - HandOff - window)) / window);
    }

    /// <summary>
    /// The orb at a cue: its layers, size and the music's mapping. <paramref name="diameterPixels"/> is the drawn
    /// diameter (clamp(0.8 × type size, 30, 104) pt, in pixels); <paramref name="diameterPoints"/> picks the design.
    /// </summary>
    public static OrbStagePose Pose(InstrumentalCue cue, OrbState state, double diameterPoints, double diameterPixels,
                                    bool reduceMotion, OrbAudioFollower? audio)
    {
        var native = Native(diameterPoints);
        var focus = Focus(cue.Elapsed, cue.Span);
        double level = 0, onset = 0, phase = 0;
        if (!reduceMotion && audio is not null)
        {
            level = audio.Level;
            onset = audio.Onset;
            phase = audio.Phase;
        }
        var entering = Easing.Smoothstep((cue.Elapsed - WallpaperMotion.InterludeInDelay) / WallpaperMotion.InterludeIn);
        var leaving = 1 - Math.Clamp(cue.FadeOut, 0, 1);
        var scale = reduceMotion ? 1 : (0.55 + 0.45 * entering) * (1 - 0.7 * leaving) * (1 - 0.14 * focus);
        var k = diameterPixels * 1.3 / native * scale;
        var seconds = Seconds(cue.Seed, cue.Elapsed, phase);
        OrbLayer Layer(OrbState s, double weight)
        {
            var resolved = ThinkingOrbEngine.Resolve(s, native);
            return new OrbLayer(s, weight, reduceMotion ? StillT : seconds * resolved.Speed, resolved);
        }
        OrbLayer first;
        OrbLayer? second = null;
        if (state == OrbState.Breathing) first = Layer(OrbState.Breathing, 1);
        else if (focus <= 0) first = Layer(state, 1);
        else if (focus >= 1) first = Layer(OrbState.Breathing, 1);
        else
        {
            first = Layer(state, 1 - focus);
            second = Layer(OrbState.Breathing, focus);
        }
        return new OrbStagePose(native, k, focus, Swell: 1 + 0.6 * level, DotScale: 1 + 0.12 * level + 0.25 * onset,
                                Brighten: 0.25 * onset + 0.2 * focus, HaloAlpha: 0.10 + 0.14 * focus, first, second);
    }

    /// <summary>
    /// "Mix": one of the nine states for a break, from a hash of its seed, never the previous break's.
    /// </summary>
    public static OrbState Mix(ulong seed, OrbState? previous)
    {
        var count = previous is null ? 9 : 8;
        var hash = (seed ^ (seed >> 29)) * 0x9E37_79B9_7F4A_7C15UL;
        var pick = (int)((hash >> 33) % (ulong)count);
        foreach (var state in ThinkingOrbEngine.States)
        {
            if (state == previous) continue;
            if (pick-- == 0) return state;
        }
        return OrbState.Listening;
    }

    /// <summary>
    /// A stored orb style: one of the nine states, or Mix (null). Unknown values read as listening.
    /// </summary>
    public static (OrbState State, bool Mix) Style(string? stored)
    {
        if (string.Equals(stored?.Trim(), "mix", StringComparison.OrdinalIgnoreCase)) return (OrbState.Listening, true);
        return (ThinkingOrbEngine.Parse(stored) ?? OrbState.Listening, false);
    }

    /// <summary>The stored values, in the picker's order.</summary>
    public static readonly IReadOnlyList<string> StyleValues =
        ["listening", "searching", "solving", "working", "weaving", "connecting", "composing", "shaping", "breathing", "mix"];

    // ---- Ink ----------------------------------------------------------------------------------------------------------

    /// <summary>The colour bands down the orb (top to bottom): neighbour 1, the primary, neighbour 2, mixed in 5 steps.</summary>
    public const int Bands = 5;

    /// <summary>Ink strength steps per band.</summary>
    public const int Strengths = 10;

    /// <summary>The band a dot at <paramref name="y"/> (in the design frame of size <paramref name="native"/>) takes.</summary>
    public static int Band(double y, int native) => Math.Clamp((int)(y / native * Bands), 0, Bands - 1);

    /// <summary>A band's lobe colour: neighbour 1 at the top, the primary in the middle, neighbour 2 at the bottom.</summary>
    public static OkLab Lobe(int band, in OrbColours colours)
    {
        var f = (band + 0.5) / Bands;
        return f < 0.5 ? Perceptual.Mix(colours.Neighbour1, colours.Primary, f / 0.5)
                       : Perceptual.Mix(colours.Primary, colours.Neighbour2, (f - 0.5) / 0.5);
    }

    /// <summary>The strength step for upstream's <paramref name="white"/> (0 = the strongest mark) and the brighten.</summary>
    public static int Strength(double white, double brighten) =>
        Math.Clamp((int)Math.Round(Math.Clamp(1 - white + brighten, 0, 1) * (Strengths - 1)), 0, Strengths - 1);

    /// <summary>
    /// A dot's colour: strong dots near the lobe (lightened toward white on light lyrics), weak ones toward the paper
    /// (black for light lyrics, white for dark ones). <paramref name="dark"/> = 1 is dark lyrics on a light wallpaper.
    /// </summary>
    public static OkLab Ink(OkLab lobe, int strength, double dark)
    {
        var s = (double)strength / (Strengths - 1);
        dark = Math.Clamp(dark, 0, 1);
        var near = Perceptual.Mix(Perceptual.Mix(lobe, OkLab.White, 0.3), lobe, dark);
        var paper = Perceptual.Mix(new OkLab(0, 0, 0), OkLab.White, dark);
        var far = Perceptual.Mix(lobe, paper, 0.55);
        return Perceptual.Mix(far, near, s);
    }

    /// <summary>The halo's centre alpha (×0.4 at 55% of its radius, 0 at its edge).</summary>
    public static double Halo(double haloAlpha, double dark) => Math.Min(1, haloAlpha + 0.45 * Math.Clamp(dark, 0, 1));
}
