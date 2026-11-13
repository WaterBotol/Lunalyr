using Lunalyr.Render.Pacing;
using Lunalyr.Render.Visualisers;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Visualisers;

/// <summary>
/// What every visualiser layer reads from the app: the settings, the sound and power saving. The layers are made by a
/// static hook (<see cref="Rendering.WallpaperLayers"/>), so the app sets these up once at start-up
/// (<see cref="SystemAudioService"/>); until then a layer draws the default scene's quiet ambient spectrum.
/// </summary>
public static class VisualiserServices
{
    private static volatile Func<AppSettings>? _settings;
    private static volatile IVisualiserSound? _sound;
    private static volatile SystemAudioService? _audio;
    private static volatile VisualiserSettings _current = VisualiserSettings.Default;
    private static AppSettings? _from;
    private static int _targetFps = 60;
    private static volatile bool _replaced;

    /// <summary>The running System audio service (status, levels, Reconnect), or null before start-up or in tests.</summary>
    public static SystemAudioService? Audio => _audio;

    private static volatile Func<bool>? _reduceMotion;

    /// <summary>Windows' Animation effects are off (the Mac's Reduce Motion): scenes are calm and the limiter runs.</summary>
    public static bool ReduceMotion => _reduceMotion?.Invoke() ?? false;

    /// <summary>Where the settings page reads Reduce Motion from.</summary>
    public static void UseReduceMotion(Func<bool> reduceMotion) => _reduceMotion = reduceMotion;

    /// <summary>Connects the layers to the app's settings.</summary>
    public static void UseSettings(Func<AppSettings> settings) => _settings = settings;

    internal static void UseAudio(SystemAudioService? audio, IVisualiserSound? sound)
    {
        _audio = audio;
        _sound = sound;
    }

    /// <summary>Power saving replaced the visualiser (its fallback is line patterns or colour only).</summary>
    internal static void SetPower(PowerConstraint? power)
    {
        var shown = Wallpaper.WallpaperPower.EffectiveMode("visualizer", power);
        _shownForPower = Wallpaper.WallpaperPower.Title(shown);
        _replaced = shown != "visualizer";
    }

    private static volatile string _shownForPower = "Line patterns";

    public static bool ReplacedForPower => _replaced;

    /// <summary>What power saving shows instead of the visualiser ("Line patterns", "Colour only").</summary>
    public static string ShownForPower => _shownForPower;

    /// <summary>A host for one factory's layers (the desktop's, or the settings preview's).</summary>
    public static VisualiserHost CreateHost(bool preview = false) => new(Settings, targetFps: () => Volatile.Read(ref _targetFps), preview: preview)
    {
        SoundSource = () => _sound,
        ReplacedForPower = () => _replaced,
    };

    /// <summary>The visualiser's settings as the layers read them (rebuilt only when the app's settings change).</summary>
    public static VisualiserSettings Settings()
    {
        if (_settings?.Invoke() is not { } app) return _current;
        if (ReferenceEquals(app, _from)) return _current;
        _from = app;
        Volatile.Write(ref _targetFps, app.AnimationRate is > 0 and <= 240 ? app.AnimationRate : 60);
        _current = For(app);
        return _current;
    }

    /// <summary>The visualiser's settings in <paramref name="app"/>.</summary>
    public static VisualiserSettings For(AppSettings app) => new()
    {
        Style = app.Visualizer(),
        Sensitivity = app.AudioSensitivity,
        Punch = app.VisualizerPunch,
        StarTrailsAlbumColours = app.StarTrailsColours != "original",
        WavefieldAlbumColours = app.WavefieldColours != "original",
        FosforaAlbumColours = app.WavecoreColours != "original",
    };
}
