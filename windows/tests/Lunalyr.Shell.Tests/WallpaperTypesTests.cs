using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
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

    private AppController Start(AppSettings settings)
    {
        _controller = new AppController(_source, _lyrics, _renderer, settings, new SongOffsets(), ReadingServices.Basic, new PlaybackGuard(),
            Logger.Null, options: new AppControllerOptions { TimeProvider = _time, ArtworkRetries = [] });
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
        // Match the music, GPU effects, Line patterns and Visualisers: their layers come later; the album colours meanwhile.
        Assert.Equal(AppController.FallbackBackground, _renderer.Layers.Background);
        string Draws(AppSettings settings)
        {
            controller.ApplySettings(settings);
            return _renderer.Layers.Background;
        }
        Assert.Equal("gradient", Draws(new AppSettings { BackgroundMode = "shader" }));
        Assert.Equal("gradient", Draws(new AppSettings { BackgroundMode = "pattern" }));
        Assert.Equal("visualizer", Draws(new AppSettings { BackgroundMode = "visualizer" })); // the engine falls back while it lacks one
        Assert.Equal("colour", Draws(new AppSettings { BackgroundMode = "colourOnly" }));
        Assert.Equal("gradient", Draws(new AppSettings { BackgroundMode = "colourOnly", AlbumGradientWallpaper = false }));
        Assert.Equal("desktop", Draws(new AppSettings { BackgroundMode = "desktop" }));
        Assert.Equal("gradient", Draws(new AppSettings { BackgroundMode = "custom" })); // nothing imported
        Assert.Equal("gradient", Draws(new AppSettings { BackgroundMode = "custom", WallpaperPath = Path.Combine(_folder, "gone.png") }));
        Assert.Equal("custom", Draws(new AppSettings { BackgroundMode = "custom", WallpaperPath = picture }));
        Assert.Equal("gradient", Draws(new AppSettings { BackgroundMode = "musicVideo" })); // not in this build
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
        Assert.Equal("gradient", _renderer.Layers.Background);
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
