using Lunalyr.Platform.Desktop;
using Lunalyr.Render.Pacing;

namespace Lunalyr.Render;

/// <summary>Average and percentiles of a series, in milliseconds.</summary>
public readonly record struct Percentiles(double Avg, double P50, double P95, double P99)
{
    internal static Percentiles Of(ReadOnlySpan<double> values)
    {
        if (values.IsEmpty) return default;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        double sum = 0;
        foreach (double v in sorted) sum += v;
        double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
        return new Percentiles(Math.Round(sum / sorted.Length, 3), Math.Round(P(0.5), 3), Math.Round(P(0.95), 3), Math.Round(P(0.99), 3));
    }
}

/// <summary>One surface as the engine sees it now.</summary>
public sealed record SurfaceStatus(
    int Index,
    string Monitor,
    int Width,
    int Height,
    float Scale,
    int RefreshHz,
    AttachMode Mode,
    string Adapter,
    bool AdapterDrivesMonitor,
    bool Hdr,
    SurfaceState State,
    PauseReason Reason,
    FrameRateCap FrameRate,
    uint SyncInterval,
    double GatePeriod,
    bool Idle);

/// <summary>Frame statistics since the last <see cref="RenderEngine.ResetStats"/>.</summary>
/// <param name="Frames">Render-loop iterations that drew at least one surface.</param>
/// <param name="FrameCpuMs">Render-thread time per iteration (every surface drawn and presented).</param>
/// <param name="FrameIntervalMs">Time between consecutive iterations while drawing continuously.</param>
/// <param name="SlowFrames">Intervals longer than 1.5 × the expected one.</param>
public sealed record RenderStats(
    double Seconds,
    int Frames,
    double Fps,
    Percentiles FrameCpuMs,
    Percentiles FrameIntervalMs,
    int SlowFrames,
    IReadOnlyList<SurfaceStatus> Surfaces,
    PowerConstraint? Power,
    int Rebuilds,
    int DeviceRecoveries);

/// <summary>Raised when a surface starts or stops drawing, or the power constraint changes.</summary>
public sealed class RenderStatusChangedEventArgs(IReadOnlyList<SurfaceStatus> surfaces, PowerConstraint? power) : EventArgs
{
    public IReadOnlyList<SurfaceStatus> Surfaces { get; } = surfaces;
    public PowerConstraint? Power { get; } = power;
}

/// <summary>Fixed-size rings of frame timings: no allocation per frame, percentiles over the latest frames.</summary>
internal sealed class FrameRecorder
{
    private const int Capacity = 1 << 16;
    private readonly Lock _gate = new();
    private readonly double[] _cpu = new double[Capacity];
    private readonly double[] _interval = new double[Capacity];
    private int _frames, _intervals, _slow;
    private long _startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

    public void Add(double cpuMs, double intervalMs, double expectedIntervalMs)
    {
        lock (_gate)
        {
            _cpu[_frames++ & (Capacity - 1)] = cpuMs;
            if (!double.IsNaN(intervalMs))
            {
                _interval[_intervals++ & (Capacity - 1)] = intervalMs;
                if (expectedIntervalMs > 0 && intervalMs > expectedIntervalMs * 1.5) _slow++;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _frames = _intervals = _slow = 0;
            _startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    public (double Seconds, int Frames, Percentiles Cpu, Percentiles Interval, int Slow) Read()
    {
        lock (_gate)
        {
            double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
            return (seconds, _frames, Percentiles.Of(_cpu.AsSpan(0, Math.Min(_frames, Capacity))),
                Percentiles.Of(_interval.AsSpan(0, Math.Min(_intervals, Capacity))), _slow);
        }
    }
}
