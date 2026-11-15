// Adapted from Synesthia by Jon Jaques (MIT), via Lunalyr for Mac's VisualizerUniforms.swift and
// AudioVisualizerView.Coordinator.uniforms (Lunalyr's own lines).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lunalyr.Core.Visualisers;

/// <summary>
/// What every scene's pixel shader receives each frame: the Mac's 54 floats in the Mac's order, then two floats of
/// padding so the constant buffer is a whole number of 16-byte registers (224 bytes). The HLSL side
/// (<c>Visualisers/Shaders/Common.hlsli</c>) declares the same fields; no field straddles a register, so HLSL's
/// constant-buffer packing equals this sequential layout. The 16 option slots are a <c>float4[4]</c> there.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct VizUniforms
{
    public const int Floats = 56;
    public const int ByteSize = Floats * 4;

    /// <summary>Drawable size in pixels; shaders use it to correct for aspect ratio.</summary>
    public float ResolutionX, ResolutionY;
    /// <summary>The scene clock (see <see cref="VisualizerTimeline"/>).</summary>
    public float Time;
    public float Dt;
    public float Bass, Mid, Treble, Level, Beat;
    public float Sensitivity, Speed;
    /// <summary>Palette index as a float (0 Prism, 1 Ember, 2 Ocean, 3 Violet, 4 Mono).</summary>
    public float Palette;
    /// <summary>The scene's own option slots, p0...p15 (p15: colour richness).</summary>
    public OptionSlots P;
    public float SubBass, LowMid, HighMid, Presence, Air, TrebleBeat, Flux, Centroid;
    public float BeatHit, TrebleHit, BeatCount, Frame, ReduceMotion, Intro;
    public float SoundKick, SoundSnare, SoundHat, SoundBass, SoundVocal, SoundPad;
    public float KickOnsets, SnareOnsets, HatOnsets, KickAge, SnareAge, HatAge;
    private float _padding0, _padding1;

    /// <summary>The Mac's defaults: unit sensitivity and speed, intro 1, every onset age 60 (none yet).</summary>
    public VizUniforms()
    {
        Sensitivity = 1; Speed = 1; Intro = 1;
        KickAge = SnareAge = HatAge = SoundTypeUniforms.MaximumAge;
    }

    /// <summary>The twelve sound-type values, as the last twelve floats.</summary>
    public void Apply(in SoundTypeUniforms sound)
    {
        SoundKick = sound.Kick; SoundSnare = sound.Snare; SoundHat = sound.Hat;
        SoundBass = sound.Bass; SoundVocal = sound.Vocal; SoundPad = sound.Pad;
        KickOnsets = sound.KickOnsets; SnareOnsets = sound.SnareOnsets; HatOnsets = sound.HatOnsets;
        KickAge = sound.KickAge; SnareAge = sound.SnareAge; HatAge = sound.HatAge;
    }

    /// <summary>The sixteen option slots.</summary>
    [InlineArray(16)]
    public struct OptionSlots
    {
        private float _slot;
    }
}

/// <summary>Builds a scene's uniforms (the Mac's <c>Coordinator.uniforms</c>).</summary>
public static class VisualizerUniforms
{
    /// <summary>
    /// <paramref name="time"/> is the scene clock (Reduce Motion's slower rate is already applied by its timeline).
    /// <paramref name="reduced"/> means calm: Reduce Motion or dim flashing lights, which remove beat-driven
    /// transients. <paramref name="presentationTime"/> is the displayed time on the capture clock the sound types are
    /// evaluated at; null uses the time <paramref name="frame"/> was read at. Never allocates.
    /// </summary>
    public static VizUniforms Build(VisualizerStyle style, double width, double height, double time, in AudioSnapshot frame,
        double sensitivity, double speed, bool reduced, double vividness = 1, double punch = 1, double? presentationTime = null)
    {
        var u = new VizUniforms
        {
            ResolutionX = (float)width, ResolutionY = (float)height,
            Time = (float)time, Dt = 1f / 30,
            Speed = (float)(speed * (style == VisualizerStyle.Tunnel ? 0.22 : 0.34)),
            Sensitivity = (float)Math.Min(3, Math.Max(0.3, sensitivity)),
            Palette = style == VisualizerStyle.Silk ? 3 : 0,
            Bass = frame.Bass, Mid = frame.Mid, Treble = frame.Treble, Level = frame.Level,
            SubBass = frame.Components[0], LowMid = frame.Components[2],
            HighMid = frame.Components[4], Presence = frame.Components[5], Air = frame.Components[7],
            Centroid = frame.Centroid,
        };
        u.P[15] = (float)Math.Min(1.8, Math.Max(0.4, double.IsFinite(vividness) ? vividness : 1));
        // Keep musical phrasing while removing camera jolts, flashing stars and beat-wide exposure jumps from desktop
        // defaults. Punch scales what remains (1 is the shipped look); calm removes it.
        var response = new VisualizerResponse(reduceMotion: reduced, punch: punch);
        var transient = response.TransientScale;
        u.Beat = frame.Beat * 0.20f * transient;
        u.TrebleBeat = frame.TrebleBeat * 0.12f * transient;
        u.Flux = frame.Flux * 0.14f * transient;
        u.ReduceMotion = reduced ? 1 : 0;
        // Sound types, re-read from the onsets at the displayed time so calm gets its own slow flares. Kicks are the
        // beat count.
        u.Apply(new SoundTypeUniforms(frame.Sound, presentationTime ?? frame.Sound.PresentationTime, response));
        u.BeatCount = u.KickOnsets;
        if (style.IsWavefield())
        {
            u.P[0] = style == VisualizerStyle.WavefieldSphere ? 1 : 0;
        }
        else if (style == VisualizerStyle.Tunnel)
        {
            u.P[0] = 0.38f; u.P[1] = 0.78f; u.P[2] = 0.50f;
            u.P[3] = 0; u.P[4] = 0.42f; u.P[5] = 0.72f;
        }
        else
        {
            u.P[0] = style == VisualizerStyle.Silk ? 4 : 6;
            u.P[1] = style == VisualizerStyle.Silk ? 0.16f : 0.20f;
            u.P[2] = 0; // A calm background for lyrics, without flashing stars.
        }
        if (style == VisualizerStyle.FosforaAurora)
        {
            // Each of Fosfora's seven ribbons is its own register. The broad bass and mid registers contain their
            // neighbours' ranges, so those ribbons moved together; these two read 60-150 Hz and 400-1200 Hz instead.
            u.P[3] = frame.Components[1];
            u.P[4] = frame.Components[3];
        }
        return u;
    }
}
