using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Platform.Wallpaper;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Wallpaper;

namespace Lunalyr.Shell;

// The wallpaper type (backgroundMode) as a background layer id: docs/APP-SHELL.md, "Wallpaper types (milestone 6)".
// "Custom Mix", GPU effects and Line patterns draw the effect or pattern WallpaperMatch picks for the song. A song's
// character is read once, when its lyrics (or the verdict that it has none) arrive, and kept for the session, so a late
// cover or a second lookup never changes its wallpaper mid-song; the pick itself follows the settings at once.
public sealed partial class AppController
{
    /// <summary>The layer every build has: the album colour field, drawn wherever a type's own layer is missing.</summary>
    public const string FallbackBackground = "gradient";

    private const int RememberedCharacters = 512;
    private readonly Dictionary<string, TrackCharacter> _characters = new(StringComparer.Ordinal);
    private readonly Queue<string> _characterOrder = new();

    /// <summary>The character the wallpaper is matched to: the song on screen's once read, else the last one's (kept while nothing plays).</summary>
    private TrackCharacter? _wallpaperCharacter;

    /// <summary>The effect or pattern "Custom Mix" draws for the song on screen, or null before one is chosen.</summary>
    public WallpaperPick? MatchedWallpaper
    {
        get
        {
            lock (_gate) return _wallpaperCharacter is { } song ? Pick(_settings, song, WallpaperPower.SparesGpu(_power)) : null;
        }
    }

    /// <summary>The background layer the wallpaper draws now (the settings preview draws the same).</summary>
    public string BackgroundLayer => BackgroundLayerFor(Settings);

    /// <summary>The imported picture or video's layer as last handed to the renderer, or null.</summary>
    public CustomLayerFactory? CustomWallpaper { get; private set; }

    /// <summary>Held while the layers are worked out and handed to the renderer (<see cref="PublishLayers"/>).</summary>
    private readonly Lock _layersGate = new();

    /// <summary>
    /// Hands the renderer the background and composition the settings and the song on screen draw now. Whatever changes
    /// them (settings, a song's matched wallpaper or layout, an animated cover, power saving, the music video) calls this
    /// after the change. The layers are worked out and handed over under one lock, so the last hand-over always reflects
    /// the latest state: worked out first and handed over after, a slower thread's older layers could land after a newer
    /// pick (a song's animated cover or matched wallpaper replaced by the wallpaper before it, until the next change).
    /// Never called holding <see cref="_gate"/> (the monitor's lock, then the controller's, are taken inside).
    /// </summary>
    private void PublishLayers()
    {
        lock (_layersGate)
        {
            var settings = Settings;
            _renderer.SetLayers(BackgroundLayerFor(settings), CompositionFor(settings));
        }
        // A new layout may bring its own floating size, and each monitor's own wallpaper follows the same song and power.
        if (FloatsAnywhere(Settings) || DisplayKeys.Count > 0) UpdateFloating();
    }

    /// <summary>The background layer id <paramref name="settings"/> draw for the song on screen.</summary>
    private string BackgroundLayerFor(AppSettings settings)
    {
        // Power saving may draw a lighter type than the one chosen (WallpaperPower); "Without a video" goes through it too.
        var power = Power;
        var drawn = WallpaperPower.EffectiveMode(settings.BackgroundMode, power);
        if (drawn != settings.BackgroundMode) settings = settings with { BackgroundMode = drawn };
        switch (settings.BackgroundMode)
        {
            case "colourOnly":
                return settings.AlbumGradientWallpaper ? "colour" : FallbackBackground;
            case "desktop":
                return "desktop";
            case "custom":
                return CustomWallpaperStore.IsAvailable(settings.WallpaperPath) ? "custom" : FallbackBackground;
            case "visualizer":
                return WallpaperCatalog.VisualiserLayerFor(settings);
            case "musicVideo":
            {
                // The video (or the album colours while it is looked for), else the type chosen as "Without a video".
                string? video = null;
                MusicVideoBackground(settings, ref video);
                return video ?? BackgroundLayerFor(settings with { BackgroundMode = settings.MusicVideoFallback });
            }
        }
        // Custom Mix: an animated cover, when the album has one, wins (AppController.MotionArtwork.cs).
        if (settings.BackgroundMode == "auto" && MotionLayerFor(settings) is { } motion) return motion;
        // Custom Mix, GPU effects, Line patterns.
        TrackCharacter? song;
        lock (_gate) song = _wallpaperCharacter;
        return Pick(settings, song ?? TrackCharacter.Neutral, WallpaperPower.SparesGpu(power)).LayerId ?? FallbackBackground;
    }

    /// <summary>The song's effect or pattern; while power is being saved, heavy GPU effects give way to light ones.</summary>
    private WallpaperPick Pick(AppSettings settings, TrackCharacter song, bool savingPower)
    {
        var (effects, patterns) = _options.WallpaperStyles?.Invoke()
            ?? ([.. WallpaperCatalog.Effects.Select(s => s.Candidate)], [.. WallpaperCatalog.Patterns.Select(s => s.Candidate)]);
        return WallpaperMatch.Pick(settings.BackgroundMode is "shader" or "pattern" ? settings.BackgroundMode : "auto", song,
            settings.ShaderStyle, settings.PatternStyle, effects, patterns, savingPower);
    }

    /// <summary>
    /// Reads the song on screen's character once (when its lyrics are shown or its lookup has ended) and draws its
    /// wallpaper. Called wherever the layout is matched.
    /// </summary>
    private void ChooseWallpaper(LyricResult? lyrics, bool onlyWhenSettled)
    {
        string before, after, title;
        AppSettings settings;
        lock (_gate)
        {
            if (_disposed || _lookupTrack is not { } track) return;
            if (onlyWhenSettled && _shown is null && _lookupCts is not null) return;
            var key = track.TrackKey;
            if (!_characters.TryGetValue(key, out var character))
            {
                lyrics ??= _shown;
                var cover = _palettes.TryGetValue(key, out var known) ? known.Palette.ArtworkStops : [];
                character = TrackCharacter.Of((lyrics as LyricResult.Synced)?.Lines, cover, track.Title, track.Artist);
                _characters[key] = character;
                _characterOrder.Enqueue(key);
                while (_characterOrder.Count > RememberedCharacters) _characters.Remove(_characterOrder.Dequeue());
            }
            if (_wallpaperCharacter == character) return;
            var saving = WallpaperPower.SparesGpu(_power);
            settings = _settings with { BackgroundMode = WallpaperPower.EffectiveMode(_settings.BackgroundMode, _power) };
            before = _wallpaperCharacter is { } old ? Pick(settings, old, saving).LayerId ?? FallbackBackground : FallbackBackground;
            _wallpaperCharacter = character;
            after = Pick(settings, character, saving).LayerId ?? FallbackBackground;
            title = track.Title;
        }
        if (settings.BackgroundMode is not ("auto" or "shader" or "pattern" or "musicVideo") || before == after)
        {
            // Another monitor's own Custom Mix may still take the song's wallpaper.
            if (DisplayKeys.Count > 0) UpdateFloating();
            return;
        }
        _app.Info($"Custom Mix: \"{title}\" takes the {after} wallpaper.");
        PublishLayers();
    }

    /// <summary>
    /// Hands the renderer the imported picture or video (its file and placement) whenever they change, before the
    /// layers are set, so the type's own layer exists when it is chosen.
    /// </summary>
    private void ApplyCustomWallpaper(AppSettings? old, AppSettings settings)
    {
        if (!CustomWallpaperStore.IsAvailable(settings.WallpaperPath)) return;
        if (old is not null && old.WallpaperPath == settings.WallpaperPath && old.WallpaperPlacement == settings.WallpaperPlacement
            && CustomWallpaperStore.IsAvailable(old.WallpaperPath)) return;
        CustomWallpaper = new CustomLayerFactory(settings.WallpaperPath, WallpaperPlacements.Parse(settings.WallpaperPlacement), log: _logger);
        _renderer.ReplaceLayer(CustomWallpaper);
        _app.Info($"Imported wallpaper: {(CustomWallpaperStore.IsVideo(settings.WallpaperPath) ? "a video" : "a picture")}, placed {settings.WallpaperPlacement}.");
    }
}
