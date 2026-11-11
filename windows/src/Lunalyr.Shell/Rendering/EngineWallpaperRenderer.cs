using Lunalyr.Core.Logging;
using Lunalyr.Render;
using Lunalyr.Render.Pacing;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell.Rendering;

/// <summary>
/// The real wallpaper: <see cref="IWallpaperRenderer"/> over <see cref="RenderEngine"/>. Layer ids the engine doesn't
/// know fall back to ones it does (and say so in the log), so a setting from a newer version never leaves the desktop
/// blank.
/// </summary>
public sealed class EngineWallpaperRenderer : IWallpaperRenderer
{
    private readonly RenderEngine _engine;
    private readonly CategoryLogger _log;
    private readonly string _fallbackBackground;
    private readonly string _fallbackComposition;
    private readonly Lock _gate = new();

    public EngineWallpaperRenderer(RenderEngine engine, Logger? logger = null, string fallbackBackground = "solid",
        string fallbackComposition = "editorial")
    {
        _engine = engine;
        _log = (logger ?? Logger.Null).Render;
        _fallbackBackground = fallbackBackground;
        _fallbackComposition = fallbackComposition;
    }

    public RenderEngine Engine => _engine;

    public void Start()
    {
        lock (_gate)
        {
            if (!_engine.IsRunning) _engine.Start();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_engine.IsRunning) _engine.Stop();
        }
    }

    public void SetScene(SceneSnapshot scene) => _engine.SetScene(scene);

    public void SetLayers(string background, string composition)
    {
        var ids = _engine.LayerIds;
        var bg = ids.Contains(background) ? background : _fallbackBackground;
        var lyric = ids.Contains(composition) ? composition : _fallbackComposition;
        if (bg != background) _log.Warn($"Background \"{background}\" isn't available yet; showing \"{bg}\".");
        if (lyric != composition) _log.Warn($"Composition \"{composition}\" isn't available yet; showing \"{lyric}\".");
        _engine.SetLayers(bg, ids.Contains(lyric) ? lyric : null);
    }

    public void SetClock(Func<(double Seconds, bool Playing)> clock) => _engine.SongClock = clock;

    public void SetFrameRate(int framesPerSecond) => _engine.SetFrameRate(framesPerSecond switch
    {
        <= 0 => FrameRateCap.Display,
        <= 15 => FrameRateCap.Fps15,
        <= 30 => FrameRateCap.Fps30,
        _ => FrameRateCap.Fps60,
    });

    public void Dispose() => _engine.Dispose();
}
