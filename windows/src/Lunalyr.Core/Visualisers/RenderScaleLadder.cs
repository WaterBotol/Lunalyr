// Adapted from Synesthia by Jon Jaques (MIT), via Lunalyr for Mac's RenderScaleLadder.swift and
// AudioVisualizerView.Coordinator.drawableSize (Lunalyr's own lines).

namespace Lunalyr.Core.Visualisers;

/// <summary>
/// The adaptive-resolution decision: when the measured GPU time can't fit the frame budget, the scene renders at a
/// smaller size instead of dropping frames. Discrete rungs with hysteresis: over 85% of the budget steps one rung
/// down, a predicted cost (area ratio × average) under 60% steps back up.
/// </summary>
public struct RenderScaleLadder
{
    /// <summary>The scene renders at this fraction of its budgeted pixels per axis; the bottom rung costs a quarter.</summary>
    public static ReadOnlySpan<double> Scales => [0.5, 0.625, 0.75, 0.875, 1.0];

    public RenderScaleLadder()
    {
    }

    public int Index { get; private set; } = 4;

    public readonly double Scale => Scales[Index];

    /// <summary>A freshly built scene (or another GPU) starts at full resolution and is measured afresh.</summary>
    public void Reset() => Index = Scales.Length - 1;

    /// <summary>One adjustment window's evidence. <paramref name="average"/> and <paramref name="budget"/> are seconds per frame.</summary>
    public void Adjust(double average, double budget)
    {
        if (average > budget * 0.85 && Index > 0)
        {
            Index -= 1;
        }
        else if (Index < Scales.Length - 1)
        {
            var growth = Scales[Index + 1] / Scales[Index];
            if (average * growth * growth < budget * 0.6) Index += 1;
        }
    }

    /// <summary>
    /// The scene's render size for a surface of <paramref name="nativeWidth"/> × <paramref name="nativeHeight"/> pixels.
    /// Soft fields don't need native-resolution offscreen surfaces (text keeps native resolution in its own layer), so
    /// each scene has a pixel budget, which the ladder trims while the GPU falls behind.
    /// </summary>
    public static (int Width, int Height) DrawableSize(double nativeWidth, double nativeHeight, VisualizerStyle style, bool preview,
        double ladderScale = 1)
    {
        var cap = preview ? 220_000.0 : style.IsWavefield() || style == VisualizerStyle.Tunnel ? 950_000.0 : 1_800_000.0;
        var budgetScale = Math.Min(1, Math.Sqrt(cap / Math.Max(1, nativeWidth * nativeHeight)));
        var scale = budgetScale * ladderScale;
        return ((int)Math.Max(1, Math.Floor(nativeWidth * scale)), (int)Math.Max(1, Math.Floor(nativeHeight * scale)));
    }
}

/// <summary>
/// Accumulates measured GPU frame times. A failed or empty frame reports zero and is ignored, so it can't drag the
/// average toward "everything is fine". Thread-safe.
/// </summary>
public sealed class GpuFrameTimer
{
    private readonly Lock _lock = new();
    private double _total;
    private int _count;

    public void Record(double seconds)
    {
        if (!(seconds > 0)) return;
        lock (_lock)
        {
            _total += seconds;
            _count += 1;
        }
    }

    /// <summary>
    /// Average seconds of GPU time per frame since the last drain, then a fresh window; null until
    /// <paramref name="minimumSamples"/> frames have reported, so one window can't act on noise.
    /// </summary>
    public double? Drain(int minimumSamples)
    {
        lock (_lock)
        {
            if (_count < minimumSamples) return null;
            var average = _total / _count;
            _total = 0;
            _count = 0;
            return average;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _total = 0;
            _count = 0;
        }
    }
}
