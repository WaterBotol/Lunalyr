namespace Lunalyr.Core.Visualisers;

/// <summary>
/// How strongly one frame of a visualiser may react to transients.
/// </summary>
/// <remarks>
/// Two system settings ask for calmer visuals: Reduce Motion and the "dim flashing lights" preference. Either one
/// removes beat flashes, slows pattern changes and caps how quickly the mean brightness may rise. Only Reduce Motion
/// also slows the scene's overall motion, which is what it asks for.
/// </remarks>
public readonly record struct VisualizerResponse
{
    private readonly double _punch;

    /// <summary>The shipped look: no calm setting, Punch 1. (<c>default</c> would be Punch 0.)</summary>
    public VisualizerResponse() : this(false, false, 1)
    {
    }

    public VisualizerResponse(bool reduceMotion = false, bool dimFlashingLights = false, double punch = 1)
    {
        ReduceMotion = reduceMotion;
        DimFlashingLights = dimFlashingLights;
        _punch = double.IsFinite(punch) ? punch : 1;
    }

    public bool ReduceMotion { get; init; }

    public bool DimFlashingLights { get; init; }

    /// <summary>The Punch control: transient strength, where 1 is the shipped look. Non-finite values read as 1.</summary>
    public double Punch
    {
        get => _punch;
        init => _punch = double.IsFinite(value) ? value : 1;
    }

    /// <summary>Beat flashes are removed and brightness changes are rate-limited.</summary>
    public bool Calm => ReduceMotion || DimFlashingLights;

    /// <summary>Multiplies the scene clock. Reduce Motion keeps phrasing, not travel.</summary>
    public double MotionScale => ReduceMotion ? 0.15 : 1;

    /// <summary>Multiplies beat, treble-beat and flux before each scene's own scaling.</summary>
    public float TransientScale => Calm ? 0 : (float)VisualizerPreferences.ClampedPunch(Punch);

    /// <summary>Pattern morphs and modal changes run at this fraction of real time.</summary>
    public float PatternRate => Calm ? 0.25f : 1;
}

/// <summary>The visualiser engine's preference rules that the audio core needs.</summary>
/// <remarks>Partial so the stored preferences can be added beside these in another file.</remarks>
public static partial class VisualizerPreferences
{
    /// <summary>The Punch slider's range.</summary>
    public const double PunchMinimum = 0, PunchMaximum = 2;

    /// <summary>Punch clamped to its slider range; a non-finite value reads as 1, the shipped look.</summary>
    public static double ClampedPunch(double value) =>
        double.IsFinite(value) ? Math.Min(PunchMaximum, Math.Max(PunchMinimum, value)) : 1;
}
