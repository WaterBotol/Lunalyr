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

    public EngineWallpaperRenderer(RenderEngine engine, Logger? logger = null, string fallbackBackground = "gradient",
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

    /// <summary>The layers registered with the engine: new compositions join "Custom Mix" as they are registered.</summary>
    public IReadOnlyCollection<string> LayerIds => _engine.LayerIds;

    /// <summary>
    /// The background layer for a <c>backgroundMode</c> setting: the Mac's raw values where Windows has the layer
    /// ("colourOnly" is the "colour" layer, the Mac's default "auto" the "gradient"), Windows' own ids as they are.
    /// </summary>
    public static string BackgroundLayerFor(string? backgroundMode) => backgroundMode switch
    {
        null or "" or "auto" => "gradient",
        "colourOnly" or "colorOnly" => "colour",
        _ => backgroundMode,
    };

    public void SetLayers(string background, string composition)
    {
        var ids = _engine.LayerIds;
        background = BackgroundLayerFor(background);
        var bg = ids.Contains(background) ? background : ids.Contains(_fallbackBackground) ? _fallbackBackground : "solid";
        var lyric = ids.Contains(composition) ? composition : _fallbackComposition;
        if (bg != background) _log.Warn($"Background \"{background}\" isn't available yet; showing \"{bg}\".");
        if (lyric != composition) _log.Warn($"Composition \"{composition}\" isn't available yet; showing \"{lyric}\".");
        _engine.SetLayers(bg, ids.Contains(lyric) ? lyric : null);
    }

    public void ReplaceLayer(IRenderLayerFactory factory) => _engine.ReplaceLayer(factory);

    public void SetFloating(Lunalyr.Platform.Desktop.FloatingLyricsOptions options) => _engine.SetFloating(options);

    public void SetFloatingWindows(IReadOnlyList<Lunalyr.Platform.Desktop.FloatingLyricsOptions> windows) => _engine.SetFloating(windows);

    public void SetDesktopLyrics(IReadOnlyCollection<string> monitorsWithout) => _engine.SetDesktopLyrics(monitorsWithout);

    public void SetLooks(IReadOnlyDictionary<string, SurfaceLook> floating, IReadOnlyDictionary<string, SurfaceLook> desktop)
    {
        // A composition or background not registered (yet) draws the shared one.
        var ids = _engine.LayerIds;
        SurfaceLook Known(SurfaceLook look) => look with
        {
            LyricId = look.LyricId is { } id && ids.Contains(id) ? id : null,
            BackgroundId = look.BackgroundId is { } bg && ids.Contains(BackgroundLayerFor(bg)) ? BackgroundLayerFor(bg) : null,
        };
        _engine.SetLooks(floating.ToDictionary(p => p.Key, p => Known(p.Value)), desktop.ToDictionary(p => p.Key, p => Known(p.Value)));
    }

    public void SetLyricsShown(bool shown, bool animate = true) => _engine.SetLyricsShown(shown, animate);

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
