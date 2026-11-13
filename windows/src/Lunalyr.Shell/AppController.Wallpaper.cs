using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Platform.Wallpaper;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Wallpaper;

namespace Lunalyr.Shell;

// The wallpaper type (backgroundMode) as a background layer id: docs/APP-SHELL.md, "Wallpaper types (milestone 6)".
// "Match the music", GPU effects and Line patterns draw the effect or pattern WallpaperMatch picks for the song. A song's
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

    /// <summary>The effect or pattern "Match the music" draws for the song on screen, or null before one is chosen.</summary>
    public WallpaperPick? MatchedWallpaper
    {
        get
        {
            lock (_gate) return _wallpaperCharacter is { } song ? Pick(_settings, song) : null;
        }
    }

    /// <summary>The background layer the wallpaper draws now (the settings preview draws the same).</summary>
    public string BackgroundLayer => BackgroundLayerFor(Settings);

    /// <summary>The imported picture or video's layer as last handed to the renderer, or null.</summary>
    public CustomLayerFactory? CustomWallpaper { get; private set; }

    /// <summary>The background layer id <paramref name="settings"/> draw for the song on screen.</summary>
    private string BackgroundLayerFor(AppSettings settings)
    {
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
        }
        // Match the music: an animated cover, when the album has one, wins (AppController.MotionArtwork.cs).
        if (settings.BackgroundMode == "auto" && MotionLayerFor(settings) is { } motion) return motion;
        // Match the music, GPU effects, Line patterns (and Music video, not in this build, as Match the music).
        TrackCharacter? song;
        lock (_gate) song = _wallpaperCharacter;
        return Pick(settings, song ?? TrackCharacter.Neutral).LayerId ?? FallbackBackground;
    }

    private WallpaperPick Pick(AppSettings settings, TrackCharacter song)
    {
        var (effects, patterns) = _options.WallpaperStyles?.Invoke()
            ?? ([.. WallpaperCatalog.Effects.Select(s => s.Candidate)], [.. WallpaperCatalog.Patterns.Select(s => s.Candidate)]);
        return WallpaperMatch.Pick(settings.BackgroundMode is "shader" or "pattern" ? settings.BackgroundMode : "auto", song,
            settings.ShaderStyle, settings.PatternStyle, effects, patterns);
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
            settings = _settings;
            before = _wallpaperCharacter is { } old ? Pick(settings, old).LayerId ?? FallbackBackground : FallbackBackground;
            _wallpaperCharacter = character;
            after = Pick(settings, character).LayerId ?? FallbackBackground;
            title = track.Title;
        }
        if (settings.BackgroundMode is not ("auto" or "shader" or "pattern" or "musicVideo") || before == after) return;
        _app.Info($"Match the music: \"{title}\" takes the {after} wallpaper.");
        _renderer.SetLayers(BackgroundLayerFor(settings), CompositionFor(settings));
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
