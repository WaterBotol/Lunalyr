using Lunalyr.Core.Logging;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell.Rendering;

/// <summary>
/// What the app needs from the desktop wallpaper renderer (built separately in <c>Lunalyr.Render</c>; integration comes
/// after both land). Every member may be called from any thread and must return quickly; the renderer applies changes
/// on its own render thread.
/// </summary>
public interface IWallpaperRenderer : IDisposable
{
    /// <summary>Attaches to the desktop and starts drawing (again, after <see cref="Stop"/>).</summary>
    void Start();

    /// <summary>Stops drawing and detaches from the desktop, leaving the system wallpaper. Calling it twice is harmless.</summary>
    void Stop();

    /// <summary>Swaps in what to show. The snapshot is immutable; the renderer keeps the latest one.</summary>
    void SetScene(SceneSnapshot scene);

    /// <summary>Chooses the layers by factory id: a background (e.g. "gradient") and a lyric composition (e.g. "editorial").</summary>
    void SetLayers(string background, string composition);

    /// <summary>
    /// The song clock, read once per frame on the render thread: the lyric time in seconds (offsets already applied;
    /// NaN when nothing is playing) and whether it is moving. It never allocates or blocks.
    /// </summary>
    void SetClock(Func<(double Seconds, bool Playing)> clock);

    /// <summary>Frame-rate cap in frames a second; 0 follows the display.</summary>
    void SetFrameRate(int framesPerSecond);
}

/// <summary>
/// A renderer that draws nothing and remembers what it was told: the stand-in until the real renderer is wired in.
/// </summary>
public sealed class NullWallpaperRenderer(Logger? logger = null) : IWallpaperRenderer
{
    private readonly CategoryLogger _log = (logger ?? Logger.Null).Render;
    private readonly Lock _gate = new();
    private SceneSnapshot? _scene;
    private Func<(double Seconds, bool Playing)>? _clock;

    public bool IsRunning { get; private set; }

    public string Background { get; private set; } = "";

    public string Composition { get; private set; } = "";

    public int FrameRate { get; private set; }

    public SceneSnapshot? Scene
    {
        get { lock (_gate) return _scene; }
    }

    public (double Seconds, bool Playing) ReadClock() => _clock?.Invoke() ?? (double.NaN, false);

    public void Start()
    {
        IsRunning = true;
        _log.Info("Wallpaper renderer (placeholder): started.");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _log.Info("Wallpaper renderer (placeholder): stopped.");
    }

    public void SetScene(SceneSnapshot scene)
    {
        lock (_gate) _scene = scene;
    }

    public void SetLayers(string background, string composition)
    {
        Background = background;
        Composition = composition;
        _log.Info($"Wallpaper renderer (placeholder): layers {background} + {composition}.");
    }

    public void SetClock(Func<(double Seconds, bool Playing)> clock) => _clock = clock;

    public void SetFrameRate(int framesPerSecond)
    {
        FrameRate = framesPerSecond;
        _log.Info($"Wallpaper renderer (placeholder): frame-rate cap {(framesPerSecond == 0 ? "follows the display" : framesPerSecond + " fps")}.");
    }

    public void Dispose() => Stop();
}
