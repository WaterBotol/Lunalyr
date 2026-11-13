using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Core.Compositions;
using Lunalyr.Shell.Wallpaper;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary>The wallpaper types: the settings, the layer each draws, the adjustments in the scene, and importing.</summary>
public sealed class WallpaperTypesTests : IAsyncDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeLyricLookup _lyrics = new();
    private readonly FakeRenderer _renderer = new();
    private AppController? _controller;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lunalyr-tests", Guid.NewGuid().ToString("N"));

    public WallpaperTypesTests() => Directory.CreateDirectory(_folder);

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Invented effects and patterns: a grid over energy and organic (the real ones come from their layers).</summary>
    private static WallpaperCandidate[] Styles(string prefix) =>
        [.. Enumerable.Range(0, 12).Select(i => new WallpaperCandidate($"{prefix}{i:00}", new WallpaperCharacter((i % 4) / 3.0, (i % 4) / 3.0, (i / 4) / 2.0)))];

    private AppController Start(AppSettings settings, bool styles = false)
    {
        _controller = new AppController(_source, _lyrics, _renderer, settings, new SongOffsets(), ReadingServices.Basic, new PlaybackGuard(),
            Logger.Null, options: new AppControllerOptions
            {
                TimeProvider = _time, ArtworkRetries = [], TimelineWait = TimeSpan.FromMilliseconds(60),
                TimelinePoll = TimeSpan.FromMilliseconds(10), LateTimelinePoll = TimeSpan.FromMilliseconds(20),
                WallpaperStyles = styles ? () => (Styles("e"), Styles("p")) : null,
            });
        _controller.Start();
        return _controller;
    }

    private static async Task<byte[]> Png(int width = 4, int height = 2)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)204, (byte)102, (byte)51, (byte)255);
        using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new global::Windows.Storage.Streams.DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    // ---- Settings ----

    [Fact]
    public void WindowsOldGradientReadsAsColourOnlyWithoutTheAlbumGradient()
    {
        var old = new AppSettings { BackgroundMode = "gradient" }.Normalized();
        Assert.Equal(("colourOnly", false), (old.BackgroundMode, old.AlbumGradientWallpaper));
        var colour = new AppSettings { BackgroundMode = "colour" }.Normalized();
        Assert.Equal(("colourOnly", true), (colour.BackgroundMode, colour.AlbumGradientWallpaper));
        Assert.Equal("visualizer", new AppSettings { BackgroundMode = "visualiser" }.Normalized().BackgroundMode);
        Assert.Equal("auto", new AppSettings { BackgroundMode = "somethingNewer" }.Normalized().BackgroundMode);
        Assert.Equal("auto", new AppSettings().BackgroundMode);
        foreach (var mode in AppSettings.BackgroundModes) Assert.Equal(mode, new AppSettings { BackgroundMode = mode }.Normalized().BackgroundMode);
    }

    [Fact]
    public void AdjustmentsKeepTheMacsRangesAndDefaults()
    {
        var defaults = new AppSettings();
        Assert.Equal((1.0, 1.0, 1.0, 0.85, 5.0), (defaults.EffectSpeed, defaults.Vividness, defaults.ScrimStrength, defaults.PatternOpacity, defaults.MotionBlur));
        Assert.True(defaults.TextBackingAutomatic);
        Assert.Equal(0.35, defaults.TextBacking);
        Assert.True(defaults.AdjustmentsAreDefault);
        var wild = new AppSettings
        {
            EffectSpeed = 9, Vividness = 0, ScrimStrength = -1, PatternOpacity = 2, MotionBlur = double.NaN, TextBacking = 3,
            WallpaperPlacement = "stretch", MotionPlacement = "fitTop",
        }.Normalized();
        Assert.Equal((2.0, 0.4, 0.0, 1.0, 5.0, 1.0), (wild.EffectSpeed, wild.Vividness, wild.ScrimStrength, wild.PatternOpacity, wild.MotionBlur, wild.TextBacking));
        Assert.Equal(("fill", "fill"), (wild.WallpaperPlacement, wild.MotionPlacement));
        Assert.False(wild.AdjustmentsAreDefault);
        var reset = wild.WithDefaultAdjustments();
        Assert.True(reset.AdjustmentsAreDefault);
        Assert.Equal(1.0, reset.TextBacking); // the backing is Legibility's, not an adjustment
    }

    [Fact]
    public void TheScenesAdjustmentsFollowTheSettings()
    {
        var adjustments = new AppSettings { EffectSpeed = 1.5, Vividness = 1.2, ScrimStrength = 0.3, PatternOpacity = 0.5 }.Adjustments();
        Assert.Equal((1.5, 1.2, 0.3, 0.5), (adjustments.Speed, adjustments.Vividness, adjustments.Darkening, adjustments.PatternOpacity));
        Assert.Null(adjustments.ManualBacking);
        Assert.Equal(0.6, new AppSettings { TextBackingAutomatic = false, TextBacking = 0.6 }.Adjustments().ManualBacking);
    }

    // ---- The layer each type draws ----

    [Fact]
    public async Task EachTypeDrawsItsLayerAndTheMissingOnesTheAlbumColours()
    {
        var picture = Path.Combine(_folder, "picture.png");
        await File.WriteAllBytesAsync(picture, await Png(), TestContext.Current.CancellationToken);
        var controller = Start(new AppSettings());
        // Match the music, GPU effects and Line patterns draw one of the effects or patterns; Visualisers comes later.
        Assert.True(TestBackgrounds.IsGpu(_renderer.Layers.Background), _renderer.Layers.Background);
        string Draws(AppSettings settings)
        {
            controller.ApplySettings(settings);
            return _renderer.Layers.Background;
        }
        Assert.StartsWith("shader.", Draws(new AppSettings { BackgroundMode = "shader" }), StringComparison.Ordinal);
        Assert.Equal("shader.aurora", Draws(new AppSettings { BackgroundMode = "shader", ShaderStyle = "aurora" }));
        Assert.StartsWith("pattern.", Draws(new AppSettings { BackgroundMode = "pattern" }), StringComparison.Ordinal);
        Assert.Equal("pattern.bubbles", Draws(new AppSettings { BackgroundMode = "pattern", PatternStyle = "3" }));
        Assert.Equal("visualizer", Draws(new AppSettings { BackgroundMode = "visualizer" })); // the engine falls back while it lacks one
        Assert.Equal("colour", Draws(new AppSettings { BackgroundMode = "colourOnly" }));
        Assert.Equal("gradient", Draws(new AppSettings { BackgroundMode = "colourOnly", AlbumGradientWallpaper = false }));
        Assert.Equal("desktop", Draws(new AppSettings { BackgroundMode = "desktop" }));
        Assert.Equal("gradient", Draws(new AppSettings { BackgroundMode = "custom" })); // nothing imported
        Assert.Equal("gradient", Draws(new AppSettings { BackgroundMode = "custom", WallpaperPath = Path.Combine(_folder, "gone.png") }));
        Assert.Equal("custom", Draws(new AppSettings { BackgroundMode = "custom", WallpaperPath = picture }));
        // Without a music video controller (or a video), Music video draws its fallback, Match the music by default.
        Assert.True(TestBackgrounds.IsGpu(Draws(new AppSettings { BackgroundMode = "musicVideo" })));
        Assert.Equal("colour", Draws(new AppSettings { BackgroundMode = "musicVideo", MusicVideoFallback = "colourOnly" }));
    }

    [Fact]
    public async Task TheImportedFileReachesTheRendererAndFollowsItsPlacement()
    {
        var picture = Path.Combine(_folder, "picture.png");
        await File.WriteAllBytesAsync(picture, await Png(), TestContext.Current.CancellationToken);
        var controller = Start(new AppSettings { BackgroundMode = "custom", WallpaperPath = picture });
        var first = Assert.IsType<CustomLayerFactory>(Assert.Single(_renderer.Replaced));
        Assert.Equal((picture, Core.Colour.WallpaperPlacement.Fill), (first.Path, first.Placement));
        Assert.Same(first, controller.CustomWallpaper);
        controller.ApplySettings(controller.Settings with { WallpaperPlacement = "fillTop" });
        Assert.Equal(Core.Colour.WallpaperPlacement.FillTop, Assert.IsType<CustomLayerFactory>(_renderer.Replaced[^1]).Placement);
        // Other changes leave it alone.
        controller.ApplySettings(controller.Settings with { EffectSpeed = 1.5 });
        Assert.Equal(2, _renderer.Replaced.Count);
    }

    [Fact]
    public void ASliderReachesTheWallpaperThroughTheScene()
    {
        var controller = Start(new AppSettings());
        controller.ApplySettings(controller.Settings with { ScrimStrength = 1.6, Vividness = 0.5 });
        var scene = _renderer.Last!;
        Assert.Equal((1.6, 0.5), (scene.Adjustments.Darkening, scene.Adjustments.Vividness));
        Assert.True(TestBackgrounds.IsGpu(_renderer.Layers.Background), _renderer.Layers.Background);
    }

    private readonly List<FakeMediaSession> _sessions = [];

    private FakeMediaSession Playing(string title, string artist = "Artist")
    {
        var session = new FakeMediaSession("Spotify.exe", _time);
        _sessions.Add(session);
        session.SetTrack(title, artist).SetPlayback(Lunalyr.Core.NowPlaying.PlaybackStatus.Playing).SetTimeline(30, 200);
        return session;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the controller.");
    }

    [Fact]
    public async Task MatchTheMusicGivesEachSongItsWallpaperAndKeepsIt()
    {
        _lyrics.Answer("Song A", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "A line sung slowly")));
        _lyrics.Answer("Song B", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Quick words tumbling out faster and faster here", "and more of them right away", "and more")));
        var a = _source.Add(Playing("Song A"));
        var controller = Start(new AppSettings(), styles: true);
        await WaitUntil(() => controller.MatchedWallpaper is not null);
        // The pick is made (and seen here) a moment before the lookup hands the renderer its layer: wait for the lookup.
        await controller.WhenIdleAsync();
        var first = controller.MatchedWallpaper!.Value;
        Assert.NotNull(first.LayerId);
        Assert.Equal(first.LayerId, _renderer.Layers.Background);

        // Another song, then the first again: it looks as it did (the choice is deterministic and its character kept).
        var b = _source.Add(Playing("Song B"));
        await WaitUntil(() => _renderer.Last?.Lyrics is Core.Lyrics.LyricResult.Synced { Lines.Count: 3 });
        await controller.WhenIdleAsync();
        _source.SetCurrent(a);
        await WaitUntil(() => _renderer.Last?.Lyrics is Core.Lyrics.LyricResult.Synced { Lines.Count: 1 });
        await controller.WhenIdleAsync();
        Assert.Equal(first, controller.MatchedWallpaper);
        Assert.Equal(first.LayerId, _renderer.Layers.Background);

        // A pinned effect applies at once, and GPU effects always draws an effect.
        controller.ApplySettings(controller.Settings with { BackgroundMode = "shader", ShaderStyle = "e05" });
        Assert.Equal("shader.e05", _renderer.Layers.Background);
        controller.ApplySettings(controller.Settings with { ShaderStyle = "" });
        Assert.StartsWith("shader.", _renderer.Layers.Background, StringComparison.Ordinal);
        controller.ApplySettings(controller.Settings with { BackgroundMode = "pattern", PatternStyle = "p02" });
        Assert.Equal("pattern.p02", _renderer.Layers.Background);
        _ = b;
        foreach (var session in _sessions) Assert.Empty(session.Commands);
    }

    // ---- Importing ----

    [Fact]
    public async Task ImportingAPictureMakesItTheWallpaperAndDropsTheCopyItReplaced()
    {
        var settings = new AppSettings();
        var importer = new WallpaperImporter(_folder, () => settings, change => settings = change(settings));
        var first = Path.Combine(_folder, "first.png");
        var second = Path.Combine(_folder, "second.png");
        await File.WriteAllBytesAsync(first, await Png(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(second, await Png(8, 4), TestContext.Current.CancellationToken);
        Assert.True(await importer.ImportAsync(first, TestContext.Current.CancellationToken));
        Assert.Equal(("custom", "first.png"), (settings.BackgroundMode, settings.WallpaperName));
        var copy = settings.WallpaperPath;
        Assert.Equal(Path.Combine(_folder, "Wallpapers"), Path.GetDirectoryName(copy));
        Assert.True(await importer.ImportAsync(second, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(settings.WallpaperPath) && File.Exists(first) && File.Exists(second));
        Assert.Null(importer.Message);
        Assert.False(importer.Importing);
    }

    [Fact]
    public async Task AFileThatCantBeUsedLeavesTheWallpaperAsItWas()
    {
        var settings = new AppSettings { BackgroundMode = "colourOnly" };
        var importer = new WallpaperImporter(_folder, () => settings, change => settings = change(settings));
        var bad = Path.Combine(_folder, "bad.png");
        await File.WriteAllTextAsync(bad, "not a picture", TestContext.Current.CancellationToken);
        var changes = 0;
        importer.Changed += () => changes++;
        Assert.False(await importer.ImportAsync(bad, TestContext.Current.CancellationToken));
        Assert.Equal(("colourOnly", ""), (settings.BackgroundMode, settings.WallpaperPath));
        Assert.Equal("This image could not be opened.", importer.Message);
        Assert.Equal(2, changes); // started, finished
        Assert.False(Directory.Exists(Path.Combine(_folder, "Wallpapers")));
    }

    [Fact]
    public void TheFileRowSaysWhatIsChosen()
    {
        Assert.Equal("None chosen", WallpaperImporter.FileLabel("", ""));
        Assert.Equal("“sky.jpg” is missing. Choose it again.", WallpaperImporter.FileLabel("sky.jpg", "C:\\gone\\x.jpg", _ => false));
        Assert.Equal("The imported file is missing. Choose it again.", WallpaperImporter.FileLabel("", "C:\\gone\\x.jpg", _ => false));
        Assert.Equal("sky.jpg", WallpaperImporter.FileLabel("sky.jpg", "C:\\x.jpg", _ => true));
        Assert.Equal("Imported file", WallpaperImporter.FileLabel("", "C:\\x.jpg", _ => true));
        Assert.Contains("Videos loop silently.", WallpaperImporter.Detail(reduceMotion: false), StringComparison.Ordinal);
        Assert.Contains("still frame", WallpaperImporter.Detail(reduceMotion: true), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCataloguesMenusListMatchTheMusicFirst()
    {
        // The hooks compile away until the GPU effects, patterns and visualisers implement them.
        Assert.Equal(WallpaperCatalog.Effects.Count, WallpaperCatalog.Effects.Select(e => e.Id).Distinct().Count());
        Assert.StartsWith("visualizer", WallpaperCatalog.VisualiserLayerFor(new AppSettings()), StringComparison.Ordinal);
        Assert.Equal(["", .. WallpaperCatalog.Effects.Select(e => e.Id)],
            SettingsCopy.StyleChoices(WallpaperCatalog.Effects.Select(e => (e.Id, e.Title, e.Detail))).Select(c => c.Value));
    }
}
