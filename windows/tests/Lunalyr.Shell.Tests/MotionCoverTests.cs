using System.Collections.Concurrent;
using System.Net;
using Lunalyr.Core.Covers;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Media;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Pacing;
using Lunalyr.Shell.Covers;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// Animated covers in the app: where they are looked up, how they are held across an album, when they are drawn, the
/// Covers page's words and exclusions, and the whole chain over Apple's recorded replies. Nothing reaches the network or
/// a real player.
/// </summary>
public sealed class MotionCoverTests : IAsyncDisposable
{
    private const string AppleMusic = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeLyricLookup _lyrics = new();
    private readonly FakeRenderer _renderer = new();
    private readonly FakeCovers _covers = new();
    private AppController? _controller;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lunalyr-tests", Guid.NewGuid().ToString("N"));

    public MotionCoverTests() => Directory.CreateDirectory(_folder);

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Answers by album, recording each lookup; an album named in <see cref="Unreachable"/> fails to reach Apple.</summary>
    private sealed class FakeCovers : IMotionCoverSource
    {
        public ConcurrentQueue<string> Lookups { get; } = new();

        public Dictionary<string, MotionCoverFiles> Albums { get; } = [];

        public HashSet<string> Unreachable { get; } = [];

        public TaskCompletionSource? Hold { get; set; }

        public async Task<MotionCoverAnswer> FindAsync(MotionCoverRequest request, CancellationToken cancellationToken)
        {
            Lookups.Enqueue(request.Album ?? "");
            if (Hold is { } hold) await hold.Task.WaitAsync(cancellationToken);
            if (Unreachable.Contains(request.Album ?? "")) return new MotionCoverAnswer(null, Unreachable: true);
            return Albums.TryGetValue(request.Album ?? "", out var files) ? new MotionCoverAnswer(files) : MotionCoverAnswer.None;
        }
    }

    private AppController Start(AppSettings settings, IMotionCoverSource? covers = null)
    {
        _controller = new AppController(_source, _lyrics, _renderer, settings, new SongOffsets(), ReadingServices.Basic, new PlaybackGuard(),
            Logger.Null, options: new AppControllerOptions
            {
                TimeProvider = _time, ArtworkRetries = [], TimelineWait = TimeSpan.FromMilliseconds(60),
                TimelinePoll = TimeSpan.FromMilliseconds(10), LateTimelinePoll = TimeSpan.FromMilliseconds(20),
            });
        _controller.UseMotionCovers(covers ?? _covers);
        _controller.Start();
        return _controller;
    }

    private FakeMediaSession Playing(string title, string album, string app = AppleMusic, double? duration = 200, string artist = "Artist")
    {
        var session = new FakeMediaSession(app, _time);
        session.SetTrack(title, artist, album).SetPlayback(Core.NowPlaying.PlaybackStatus.Playing).SetTimeline(30, duration);
        return session;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the controller.");
    }

    private static readonly MotionCoverFiles Dawn = new(@"C:\covers\dawn.mp4", null);

    // Ported from NowPlayingMonitorTests.swift: testAnimatedCoversLookUpOnlyWhereShownAndHoldAcrossAnAlbum
    [Fact]
    public async Task AnimatedCoversLookUpOnlyWhereShownAndHoldAcrossAnAlbum()
    {
        _covers.Albums["Dawn"] = Dawn;
        var first = _source.Add(Playing("First Light", "Dawn"));
        // Neither the wallpaper nor the opening title could show one (the title plays it too since the Mac's 134455a).
        var controller = Start(new AppSettings { UseMotionArtwork = false, OpeningTitle = false });
        await WaitUntil(() => controller.Monitor.Track is not null);
        await controller.WhenIdleAsync();
        Assert.Equal(MotionCoverState.Off, controller.MotionStatus.State);
        Assert.Empty(_covers.Lookups); // not looked up while covers can't be shown

        controller.ApplySettings(controller.Settings with { UseMotionArtwork = true });
        await WaitUntil(() => controller.MotionCover is not null && _renderer.Layers.Background == "motion");
        Assert.Equal(Dawn.Square, controller.MotionCover!.SquarePath);
        Assert.Equal("motion", _renderer.Layers.Background);

        // The next song of the same album: the cover keeps playing, with no new lookup.
        _source.Add(Playing("Second Light", "Dawn"));
        await WaitUntil(() => controller.Monitor.Track?.Title == "Second Light");
        await controller.WhenIdleAsync();
        Assert.Equal("motion", _renderer.Layers.Background);
        Assert.NotEqual(MotionCoverState.LookingUp, controller.MotionStatus.State);
        Assert.Single(_renderer.Replaced);

        // Another album: kept until a different answer arrives, then gone.
        _covers.Hold = new TaskCompletionSource();
        _source.Add(Playing("Elsewhere", "Far"));
        await WaitUntil(() => controller.MotionStatus.State == MotionCoverState.LookingUp);
        Assert.Equal("motion", _renderer.Layers.Background);
        _covers.Hold.SetResult();
        await WaitUntil(() => controller.MotionStatus.State == MotionCoverState.NotFound);
        // The answer is seen here a moment before the lookup hands the renderer the wallpaper: wait for the lookup.
        await controller.WhenIdleAsync();
        Assert.NotEqual("motion", _renderer.Layers.Background);
        Assert.Equal(["Dawn", "Far"], _covers.Lookups);
        Assert.Single(_renderer.Replaced); // no cut away and back within an album
        GC.KeepAlive(first);
    }

    // The Mac's 134455a and 297b2f2: the opening title plays the album's animated cover whatever Use animated covers
    // says, and only the playing song's own (never the last album's while the new one is looked up).
    [Fact]
    public async Task TheTitleCardPlaysTheSongsOwnAnimatedCoverWhateverTheSwitch()
    {
        _covers.Albums["Dawn"] = Dawn;
        _source.Add(Playing("First Light", "Dawn"));
        var controller = Start(new AppSettings { UseMotionArtwork = false });
        await WaitUntil(() => controller.Scene.MotionCover is not null);
        Assert.Equal(Dawn.Square, controller.Scene.MotionCover);
        Assert.NotEqual("motion", _renderer.Layers.Background); // the wallpaper's switch is still off
        Assert.Equal(["Dawn"], _covers.Lookups);

        // Another album: the card lets go at once, while the new one is looked up.
        _covers.Hold = new TaskCompletionSource();
        _source.Add(Playing("Elsewhere", "Far"));
        await WaitUntil(() => controller.Monitor.Track?.Title == "Elsewhere" && controller.Scene.Title == "Elsewhere");
        await WaitUntil(() => _covers.Lookups.Count == 2);
        Assert.Null(controller.Scene.MotionCover);
        _covers.Hold.SetResult();
        await controller.WhenIdleAsync();
        Assert.Null(controller.Scene.MotionCover); // that album has none

        // The title's cover off: no animated cover for it either.
        _covers.Hold = null;
        _source.Add(Playing("Second Light", "Dawn"));
        await WaitUntil(() => controller.Scene.MotionCover == Dawn.Square);
        controller.ApplySettings(controller.Settings with { OpeningTitleCover = false });
        await WaitUntil(() => controller.Scene.MotionCover is null);
    }

    // Ported from SpotifyPlayerTests.swift: testSpotifyListeningIsNeverSentToApplesCatalogue (the animated-cover half;
    // Windows has no catalogue cover search)
    [Fact]
    public async Task SpotifyListeningIsNeverSentToApplesCatalogue()
    {
        _covers.Albums["Dawn"] = Dawn;
        _source.Add(Playing("Breathe", "Dawn", app: "Spotify.exe"));
        var controller = Start(new AppSettings());
        await WaitUntil(() => controller.Monitor.Track is not null);
        await controller.WhenIdleAsync();
        Assert.Empty(_covers.Lookups); // animated covers are Apple Music's
        Assert.Equal(MotionCoverState.NotAppleMusic, controller.MotionStatus.State);
        Assert.NotEqual("motion", _renderer.Layers.Background);
    }

    [Fact]
    public async Task CoversPlayOnlyInMatchTheMusicAndPauseForPowerSaving()
    {
        _covers.Albums["Dawn"] = Dawn;
        _source.Add(Playing("First Light", "Dawn"));
        var controller = Start(new AppSettings());
        await WaitUntil(() => _renderer.Layers.Background == "motion");
        Assert.Equal(MotionCoverState.Available, controller.MotionStatus.State);

        controller.SetPowerConstraint(PowerConstraint.EnergySaver);
        Assert.NotEqual("motion", _renderer.Layers.Background);
        Assert.Equal(MotionCoverState.PausedForPower, controller.MotionStatus.State);
        Assert.Equal("Energy saver is on, so animated covers are paused.", SettingsCopy.CoverStatus(controller.MotionStatus));
        controller.SetPowerConstraint(PowerConstraint.OnBattery(PowerFallback.LightEffects)); // effects stay on: so do covers
        Assert.Equal("motion", _renderer.Layers.Background);

        controller.ApplySettings(controller.Settings with { BackgroundMode = "pattern" });
        Assert.NotEqual("motion", _renderer.Layers.Background);
        Assert.Equal(MotionCoverState.NotMatchTheMusic, controller.MotionStatus.State);
        Assert.Equal("The wallpaper is Line patterns now, so animated covers don’t show.", SettingsCopy.CoversWithMatch("pattern"));
        controller.ApplySettings(controller.Settings with { BackgroundMode = "auto" });
        await WaitUntil(() => _renderer.Layers.Background == "motion");
    }

    [Fact]
    public async Task ExcludingTheAlbumStopsItsCoverAndAllowingBringsItBack()
    {
        _covers.Albums["Dawn"] = Dawn;
        _source.Add(Playing("First Light", "Dawn"));
        var controller = Start(new AppSettings());
        await WaitUntil(() => _renderer.Layers.Background == "motion");
        var status = controller.MotionStatus;
        Assert.Equal(("Artist", "Dawn"), (status.Artist, status.Album));

        controller.ApplySettings(controller.Settings.WithMotionExcluded(true, "Artist", "Dawn"));
        Assert.Equal(["artist|dawn"], controller.Settings.MotionExclusions);
        await WaitUntil(() => _renderer.Layers.Background != "motion");
        Assert.Equal(MotionCoverState.Excluded, controller.MotionStatus.State);

        controller.ApplySettings(controller.Settings.WithMotionExcluded(false, "Artist", "Dawn"));
        await WaitUntil(() => _renderer.Layers.Background == "motion");
    }

    [Fact]
    public async Task APlacementChangeHandsOverANewLayer()
    {
        _covers.Albums["Dawn"] = Dawn;
        _source.Add(Playing("First Light", "Dawn"));
        var controller = Start(new AppSettings());
        await WaitUntil(() => controller.MotionCover is not null && _renderer.Layers.Background == "motion");
        controller.ApplySettings(controller.Settings with { MotionPlacement = "fillTop" });
        await WaitUntil(() => _renderer.Replaced.Count == 2);
        Assert.Equal(Core.Colour.WallpaperPlacement.FillTop, Assert.IsType<MotionCoverLayerFactory>(_renderer.Replaced[^1]).Placement);
        // Softening needs no new layer: it reaches the cover through the scene.
        controller.ApplySettings(controller.Settings with { MotionBlur = 12 });
        Assert.Equal(12, _renderer.Last!.Adjustments.Softening);
        Assert.Equal(2, _renderer.Replaced.Count);
    }

    /// <summary>
    /// A cover found while the song's matched wallpaper is being handed to the renderer is not lost to it. The layers used
    /// to be worked out first and handed over after, so a slower hand-over of the wallpaper picked before the cover was
    /// found could land after the cover's and stay (seen as these tests timing out waiting for "motion" under load).
    /// Here the wallpaper's hand-over is held until the cover's has had its chance.
    /// </summary>
    [Fact]
    public async Task ACoverFoundDuringTheWallpapersHandOverIsNotLostToIt()
    {
        _covers.Albums["Dawn"] = Dawn;
        _covers.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lyrics = _lyrics.Hold("First Light");
        _source.Add(Playing("First Light", "Dawn"));
        // A chosen layout: the lyrics' arrival hands over only the matched wallpaper.
        var controller = Start(new AppSettings { LyricComposition = "editorial" });
        await WaitUntil(() => !_covers.Lookups.IsEmpty && _lyrics.Queries.Count == 1);
        var held = 0;
        _renderer.BeforeSetLayers = (background, _) =>
        {
            if (background == "motion" || Interlocked.Exchange(ref held, 1) != 0) return;
            // The wallpaper's hand-over, worked out before the cover is found: let the cover arrive meanwhile. (Fixed, the
            // cover's hand-over waits for this one to finish, so this wait runs out; how long it is changes nothing then.)
            _covers.Hold.SetResult();
            SpinWait.SpinUntil(() => _renderer.Layers.Background == "motion", TimeSpan.FromMilliseconds(500));
        };
        lyrics.SetResult(TestLyrics.Found(TestLyrics.Synced("LRCLIB", "A line sung slowly")));
        await WaitUntil(() => controller.MatchedWallpaper is not null && controller.MotionStatus.State == MotionCoverState.Available);
        await controller.WhenIdleAsync();
        Assert.Equal(1, held);
        Assert.Equal("motion", _renderer.Layers.Background);
    }

    [Fact]
    public async Task AFailedLookupIsSaidCalmlyAndTheWallpaperStays()
    {
        _covers.Unreachable.Add("Dawn");
        _source.Add(Playing("First Light", "Dawn"));
        var controller = Start(new AppSettings());
        await WaitUntil(() => controller.MotionStatus.State == MotionCoverState.Unreachable);
        Assert.NotEqual("motion", _renderer.Layers.Background);
        Assert.Empty(_renderer.Replaced);
    }

    [Fact]
    public async Task AStreamIsNeverLookedUp()
    {
        _source.Add(Playing("Radio", "", duration: null));
        var controller = Start(new AppSettings());
        // (The lyric lookup keeps waiting for a length, so the controller is never idle here.)
        await WaitUntil(() => controller.MotionStatus.State == MotionCoverState.Stream);
        Assert.True(_covers.Lookups.Count <= 1, "at most the lookup started before the player said it was a stream");
        Assert.Equal("Radio streams have no animated cover.", SettingsCopy.CoverStatus(controller.MotionStatus));
    }

    [Fact]
    public async Task WithoutASourceNothingIsLookedUpAndThePageSaysWhy()
    {
        _source.Add(Playing("First Light", "Dawn"));
        var controller = Start(new AppSettings(), covers: null);
        controller.UseMotionCovers(null, "--no-network");
        await WaitUntil(() => controller.Monitor.Track is not null);
        Assert.Equal(MotionCoverState.Unavailable, controller.MotionStatus.State);
        Assert.Equal("Animated covers aren’t looked up in this run (--no-network).",
            SettingsCopy.CoverStatus(controller.MotionStatus, controller.MotionCoversOffReason));
    }

    [Fact]
    public void ExclusionsAreKeptByAlbumInTheMacsForm()
    {
        var settings = new AppSettings();
        Assert.Same(settings, settings.WithMotionExcluded(true, "", "")); // nothing to name it by
        var excluded = settings.WithMotionExcluded(true, "Taylor Swift", "Midnights").WithMotionExcluded(true, "Taylor Swift", "Midnights");
        Assert.Equal(["taylor swift|midnights"], excluded.MotionExclusions);
        Assert.True(excluded.ExcludesMotion("TAYLOR SWIFT", "midnights"));
        Assert.False(excluded.ExcludesMotion("Taylor Swift", "folklore"));
        Assert.Equal("taylor swift · midnights", AppSettings.MotionExclusionTitle(excluded.MotionExclusions[0]));
        var json = System.Text.Json.JsonSerializer.Serialize(excluded, AppSettingsJsonContext.Default.AppSettings);
        Assert.Contains("\"motionExclusions\"", json, StringComparison.Ordinal);
        Assert.Empty(excluded.WithMotionExcluded(false, "Taylor Swift", "Midnights").MotionExclusions);
    }

    [Fact]
    public void TheCoversPageSaysWhatCoversAreDoing()
    {
        Assert.Equal("Animated covers are off.", SettingsCopy.CoverStatus(new(MotionCoverState.Off)));
        Assert.Equal("Checking Apple Music for an animated cover…", SettingsCopy.CoverStatus(new(MotionCoverState.LookingUp)));
        Assert.Equal("No animated cover found for this album.", SettingsCopy.CoverStatus(new(MotionCoverState.NotFound)));
        Assert.Equal("Animated cover available.", SettingsCopy.CoverStatus(new(MotionCoverState.Available)));
        Assert.Equal("This album is excluded.", SettingsCopy.CoverStatus(new(MotionCoverState.Excluded)));
        Assert.Equal("Animated covers come from Apple Music, so they play only for songs playing in Apple Music.",
            SettingsCopy.CoverStatus(new(MotionCoverState.NotAppleMusic)));
    }

    // ---- The whole chain over the recorded replies: Apple's pages → the cache → a playable file ----

    /// <summary>Serves the recorded replies by address, and the generated clip as the cover's MP4. Records every request.</summary>
    private sealed class Recorded : HttpMessageHandler
    {
        private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

        public ConcurrentQueue<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            string? file = uri.Host switch
            {
                "itunes.apple.com" => "MotionArtwork/itunes-search-midnights-album.json",
                "music.apple.com" when uri.AbsolutePath == "/us/album/midnights/1649434996" => "MotionArtwork/music-apple-com-album-midnights.html",
                "mvod.itunes.apple.com" when uri.AbsolutePath.EndsWith("P517392561_default.m3u8", StringComparison.Ordinal) => "MotionArtwork/midnights-square-master.m3u8",
                "mvod.itunes.apple.com" when uri.AbsolutePath.EndsWith("_768x768_-.m3u8", StringComparison.Ordinal) => "MotionArtwork/midnights-square-768.m3u8",
                "mvod.itunes.apple.com" when uri.AbsolutePath.EndsWith("_768x768_-.mp4", StringComparison.Ordinal) => "clip.mp4",
                _ => null,
            };
            var response = file is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(Fixture(file))) };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task TheRecordedRepliesEndInAPlayableCachedCover()
    {
        var handler = new Recorded();
        var http = new HttpClient(handler);
        var finder = new MotionArtworkFinder(new LyricHttpClient(http, new LyricHttpOptions { MinimumHostInterval = TimeSpan.Zero, IsNetworkAvailable = () => true }), "us");
        var cache = new MotionCoverCache(Path.Combine(_folder, "AnimatedCovers"), http, "Lunalyr/0.1 (Windows port; dev)");
        var service = new MotionCoverService(finder, cache, path => VideoReader.HasVideo(path, out _));
        _source.Add(Playing("Lavender Haze", "Midnights", artist: "Taylor Swift"));
        var controller = Start(new AppSettings(), service);
        await WaitUntil(() => controller.MotionCover is not null && _renderer.Layers.Background == "motion");
        var path = controller.MotionCover!.SquarePath;
        Assert.StartsWith(Path.Combine(_folder, "AnimatedCovers"), path, StringComparison.OrdinalIgnoreCase);
        Assert.True(VideoReader.HasVideo(path, out _));
        Assert.Equal("motion", _renderer.Layers.Background);
        Assert.Equal(5, handler.Requests.Count); // search, page, master, 768-pixel playlist, the MP4

        // The next launch's lookup finds the file in the cache: the video isn't downloaded again.
        var again = await new MotionCoverService(new MotionArtworkFinder(new LyricHttpClient(http,
                new LyricHttpOptions { MinimumHostInterval = TimeSpan.Zero, IsNetworkAvailable = () => true }), "us"), cache,
            p => VideoReader.HasVideo(p, out _)).FindAsync(new MotionCoverRequest("Midnights", "Taylor Swift", "Lavender Haze", false),
            TestContext.Current.CancellationToken);
        Assert.Equal(path, again.Files?.Square);
        Assert.Single(handler.Requests, u => u.AbsolutePath.EndsWith(".mp4", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSettingsPreviewDrawsTheCoverItIsHanded()
    {
        // A file that can't play, so the layer shows the song's still cover: a red one, unlike the album colours.
        var pixels = new byte[32 * 32 * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (30, 30, 230, 255);
        var scene = new Render.Scene.SceneSnapshot { Cover = new Core.Colour.CoverArt(32, 32, pixels) };
        byte[] Draw(bool handed)
        {
            using var renderer = new Rendering.SettingsPreviewRenderer(160, 90,
                Rendering.WallpaperLayers.Factories(ReadingServices.Basic.WordBreaker), hardware: false);
            if (handed) renderer.Use(new MotionCoverLayerFactory(Path.Combine(_folder, "missing.mp4"), null, Core.Colour.WallpaperPlacement.Fill));
            var frame = new byte[160 * 90 * 4];
            renderer.Render(scene, "motion", "editorial", double.NaN, false, wallSeconds: 1, frame);
            return frame;
        }
        var centre = (45 * 160 + 80) * 4;
        var cover = Draw(handed: true);
        Assert.True(cover[centre + 2] > cover[centre + 1] + 20, "the preview shows the cover's red");
        var without = Draw(handed: false);
        Assert.False(without.AsSpan().SequenceEqual(cover), "without the cover's layer the preview falls back to the album colours");
    }
}
