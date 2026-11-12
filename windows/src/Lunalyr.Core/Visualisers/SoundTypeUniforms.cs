namespace Lunalyr.Core.Visualisers;

/// <summary>
/// The twelve sound-type values a native scene receives at one displayed time. Independent of any GPU layout: the
/// renderer's uniform struct copies these fields (on the Mac they are the last twelve floats of VizUniforms, in this
/// order).
/// </summary>
public readonly record struct SoundTypeUniforms
{
    /// <summary>Counters wrap here, so a float holds every value exactly.</summary>
    public const uint CounterWrap = 1 << 24;
    /// <summary>An age longer than any scene cares about; also "no onset yet".</summary>
    public const float MaximumAge = 60;

    public float Kick { get; init; }
    public float Snare { get; init; }
    public float Hat { get; init; }
    public float Bass { get; init; }
    public float Vocal { get; init; }
    public float Pad { get; init; }
    public float KickOnsets { get; init; }
    public float SnareOnsets { get; init; }
    public float HatOnsets { get; init; }
    public float KickAge { get; init; } = MaximumAge;
    public float SnareAge { get; init; } = MaximumAge;
    public float HatAge { get; init; } = MaximumAge;

    /// <summary>Nothing playing: no flares, no counts, every age at <see cref="MaximumAge"/>.</summary>
    public SoundTypeUniforms()
    {
    }

    /// <summary>
    /// The transients are re-read from the onset rings at <paramref name="time"/>, the way <paramref name="response"/>
    /// asks. Punch scales them, clamped to 1. Calm (Reduce Motion or dim flashing lights) reads slow shapes instead, at
    /// half strength at most: 0.5 × Punch up to Punch 1, so calm never flares harder than Punch alone would, and Punch
    /// Off stays off. The sustained classes are never scaled. Ages are real seconds. Never allocates.
    /// </summary>
    public SoundTypeUniforms(in SoundTypeFeatures sound, double time, VisualizerResponse response)
    {
        var shapes = response.Calm ? SoundTypeShapes.Calm : SoundTypeShapes.Normal;
        var gain = TransientGain(response);
        Kick = Flare(shapes.Kick, sound.KickRing, time, gain);
        Snare = Flare(shapes.Snare, sound.SnareRing, time, gain);
        Hat = Flare(shapes.Hat, sound.HatRing, time, gain);
        Bass = Unit(sound.Bass);
        Vocal = Unit(sound.Vocal);
        Pad = Unit(sound.Pad);
        KickOnsets = sound.KickOnsets % CounterWrap;
        SnareOnsets = sound.SnareOnsets % CounterWrap;
        HatOnsets = sound.HatOnsets % CounterWrap;
        KickAge = Age(sound.KickTime, time);
        SnareAge = Age(sound.SnareTime, time);
        HatAge = Age(sound.HatTime, time);
    }

    /// <summary>What the transient envelopes are multiplied by (see the constructor).</summary>
    public static float TransientGain(VisualizerResponse response)
    {
        if (!response.Calm) return response.TransientScale;
        return 0.5f * (float)Math.Min(1, VisualizerPreferences.ClampedPunch(response.Punch));
    }

    /// <summary>Real seconds from <paramref name="onset"/> to <paramref name="time"/>, 0...<see cref="MaximumAge"/> (the maximum when there was no onset).</summary>
    public static float Age(double onset, double time)
    {
        if (!double.IsFinite(onset) || !double.IsFinite(time)) return MaximumAge;
        return (float)SwiftMath.Min(MaximumAge, SwiftMath.Max(0, time - onset));
    }

    private static float Flare(SoundOnsetShape shape, in SoundOnsetRing ring, double time, float gain) =>
        Unit(shape.Envelope(ring, time) * gain);

    private static float Unit(float value) => SwiftMath.Min(1, SwiftMath.Max(0, value));
}
