// Ported from Lunalyr for Mac's VisualizerSafety.swift (ExposureLimiter) and VisualizerGPU.swift (ExposureMemory),
// Lunalyr's own lines. These protect people with photosensitivity: keep every rule.

namespace Lunalyr.Core.Visualisers;

/// <summary>
/// Caps how fast the mean luminance of a visualiser may rise. WCAG 2.3.1 counts a flash as a pair of opposing changes
/// of at least 10% of the maximum relative luminance. Limiting every rise to <see cref="MaxRisePerSecond"/> bounds the
/// number of such pairs to 2.5 a second for the whole frame, while falls stay immediate so the limiter never adds a
/// dark flash of its own. The GPU pass (<c>Visualisers/Shaders/ExposureMeasure.hlsl</c>) implements this same model;
/// this copy documents and tests it.
/// </summary>
public struct ExposureLimiter
{
    /// <summary>Relative luminance per second, in 0...1 linear units.</summary>
    public const float MaxRisePerSecond = 0.25f;
    /// <summary>Longest step one frame may claim, so a stall cannot release a flash.</summary>
    public const float MaxStep = 0.1f;

    public ExposureLimiter()
    {
    }

    /// <summary>The displayed mean. A new surface starts from black and fades in; null adopts the next measurement.</summary>
    public float? Shown { get; private set; } = 0;

    /// <summary>
    /// The gain to multiply sRGB-encoded output by so the displayed mean follows the rate-limited target.
    /// <paramref name="mean"/> is linear luminance.
    /// </summary>
    public float Gain(float mean, float dt)
    {
        mean = float.IsFinite(mean) ? Math.Max(0, mean) : 0;
        var step = MaxRisePerSecond * Math.Min(MaxStep, Math.Max(0, float.IsFinite(dt) ? dt : 0));
        var target = Shown is { } shown ? Math.Min(mean, shown + step) : mean;
        Shown = target;
        if (!(mean > 0.0001f)) return 1;
        return MathF.Pow(Math.Min(1, target / mean), 1 / 2.2f);
    }

    /// <summary>The limiter just turned on: take the current brightness as it is rather than dimming what is on screen.</summary>
    public void Adopt() => Shown = null;

    /// <summary>Linear relative luminance of an sRGB-encoded colour.</summary>
    public static float Luminance(float red, float green, float blue)
    {
        static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        return 0.2126f * Linear(red) + 0.7152f * Linear(green) + 0.0722f * Linear(blue);
    }
}

/// <summary>
/// The brightness each scene was last shown at through the limiter, per surface. A new surface for a scene that is on
/// screen (a crossfade's copy, a layer rebuilt after a device change) starts at the level the scene is shown at; a
/// scene coming on screen still fades in from black. Not thread-safe: the render thread owns it.
/// </summary>
public sealed class ExposureMemory
{
    /// <summary>A level older than this belongs to a scene that has left the screen.</summary>
    public const double Freshness = 0.5;

    /// <summary>Which scene on which surface: <paramref name="Display"/> names the monitor (null before it has one).</summary>
    public readonly record struct Key(VisualizerStyle Style, string? Display, bool Preview);

    private readonly Dictionary<Key, (float Shown, double At)> _levels = [];

    public void Record(float shown, Key key, double time)
    {
        if (!float.IsFinite(shown) || !double.IsFinite(time)) return;
        _levels[key] = (Math.Max(0, shown), time);
    }

    /// <summary>The level a new surface for <paramref name="key"/> starts at, while the scene is on screen.</summary>
    public float? Shown(Key key, double time)
    {
        if (!_levels.TryGetValue(key, out var level)) return null;
        var age = time - level.At;
        return age >= 0 && age < Freshness ? level.Shown : null;
    }
}
