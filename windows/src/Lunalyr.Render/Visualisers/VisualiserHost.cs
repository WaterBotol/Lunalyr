// What every visualiser surface shares: the settings, the sound, the scene clocks and the exposure memory. One host per
// factory (the desktop's, and the settings preview's own), so the desktop's monitors stay in phase.

using Lunalyr.Core.Visualisers;

namespace Lunalyr.Render.Visualisers;

/// <summary>The visualiser's own settings, as the app reads them now (the Mac's Visualiser and Music response sections).</summary>
public sealed record VisualiserSettings
{
    public static VisualiserSettings Default { get; } = new();

    /// <summary>The scene (the Mac's <c>custom.visualizerStyle</c>, default Silk Ribbons).</summary>
    public VisualizerStyle Style { get; init; } = VisualizerStyles.Default;

    /// <summary>How strongly scenes answer the sound, 0.3...3 (the Mac's <c>custom.audioSensitivity</c>, default 1).</summary>
    public double Sensitivity { get; init; } = 1;

    /// <summary>Beat punch, 0...2 (the Mac's <c>custom.visualizerPunch</c>, default 1). Calm settings override it.</summary>
    public double Punch { get; init; } = 1;

    /// <summary>Star Trails' sky takes the album cover's colours (true, the Mac's default) or keeps the original nebula.</summary>
    public bool StarTrailsAlbumColours { get; init; } = true;

    /// <summary>
    /// A "dim flashing lights" preference. Windows has no system setting for it (the Mac reads Accessibility › Display),
    /// so it is false unless the app sets it; Reduce Motion (Windows' Animation effects off) already makes scenes calm.
    /// </summary>
    public bool DimFlashingLights { get; init; }
}

/// <summary>Where the scenes' sound comes from: the analysis of the PC's sound, or a synthetic one under <c>--demo</c>.</summary>
public interface IVisualiserSound
{
    /// <summary>What the listener hears now (smoothing and delay included), as <see cref="VisualizerAudio.Latest()"/>.</summary>
    AudioSnapshot Latest();

    /// <summary>The monotonic clock the sound is stamped on, in seconds.</summary>
    double Now { get; }
}

/// <summary>Reads a <see cref="VisualizerAudio"/> on the clock it was made with.</summary>
public sealed class AnalysedSound(VisualizerAudio audio, Func<double>? clock = null) : IVisualiserSound
{
    private readonly Func<double> _clock = clock ?? AudioAnalyzer.Now;

    public VisualizerAudio Audio => audio;

    public AudioSnapshot Latest() => audio.Latest();

    public double Now => _clock();
}

/// <summary>Shared by every surface of one factory. Called only on the render thread, apart from the delegates' sources.</summary>
public sealed class VisualiserHost
{
    private double _flow, _lastWall = double.NaN;

    /// <param name="settings">The visualiser's settings now; read once per frame, so it must be cheap.</param>
    /// <param name="sound">The sound; null draws the quiet ambient spectrum (System audio off).</param>

    /// <param name="targetFps">The frame rate the wallpaper aims for, the GPU budget the scale ladder works to.</param>
    public VisualiserHost(Func<VisualiserSettings>? settings = null, IVisualiserSound? sound = null,
        Func<double>? targetFps = null, bool preview = false)
    {
        Settings = settings ?? (() => VisualiserSettings.Default);
        Sound = sound;

        TargetFps = targetFps ?? (() => 60);
        Preview = preview;
    }

    public Func<VisualiserSettings> Settings { get; }

    /// <summary>The sound source; swapped by the app as System audio goes on and off.</summary>
    public IVisualiserSound? Sound { get; set; }


    public Func<double> TargetFps { get; }

    /// <summary>A settings preview: a small pixel budget and its own scene clocks.</summary>
    public bool Preview { get; }

    public VisualizerTimelines Timelines { get; } = new();

    public ExposureMemory Exposure { get; } = new();

    /// <summary>
    /// The scenes' flow clock at <paramref name="wallSeconds"/>: wall time × 0.70 × Motion speed, integrated so a speed
    /// change never jumps the scene. Every surface drawing the same moment reads the same value; a moment earlier than
    /// one already read reads the newest value.
    /// </summary>
    public double Flow(double wallSeconds, double speed)
    {
        if (double.IsNaN(_lastWall))
        {
            _lastWall = wallSeconds;
            return _flow;
        }
        var step = wallSeconds - _lastWall;
        if (step > 0)
        {
            // A long gap (a paused wallpaper) is not motion; the timeline treats it as a pause anyway.
            _flow += Math.Min(step, 0.25) * StarTrailsSimulations.VisualizerRate * Math.Max(0, double.IsFinite(speed) ? speed : 1);
            _lastWall = wallSeconds;
        }
        return _flow;
    }
}
