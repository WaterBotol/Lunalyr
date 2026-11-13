using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Shell.MusicVideo;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// The Music video type in the app controller: which background it draws (the video file, YouTube's see-through layer,
/// the album colours while it is looked for, or the type chosen without a video), what the layers read (the player's
/// own position plus the video offset), the fallback remembered, and the lines Tap to sync times against. A fake player
/// and fake lyrics; nothing reaches the network, no file is decoded and nothing plays.
/// </summary>
public sealed class MusicVideoWiringTests : IAsyncDisposable
{
    private const string VideoFile = @"C:\Videos\Song A.mp4";
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeLyricLookup _lyrics = new();
    private readonly FakeRenderer _renderer = new();
    private readonly MemoryMusicVideoStorage _storage = new();
    private readonly MusicVideoFeed _feed = new();
    private AppController? _controller;

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
    }

    private sealed class Playable : IMusicVideoPlayabilityCheck
    {
        public Task<MusicVideoPlayability> CheckAsync(string path, CancellationToken ct) => Task.FromResult(MusicVideoPlayability.Playable);
    }

    private (AppController App, MusicVideoController Video) Start(AppSettings settings, bool youTube = true, TimeSpan? wait = null,
        bool connectAfterStart = false)
    {
        var app = new AppController(_source, _lyrics, _renderer, settings, new SongOffsets(), ReadingServices.Basic, new PlaybackGuard(),
            Logger.Null, options: new AppControllerOptions
            {
                TimeProvider = _time, ArtworkRetries = [], TimelineWait = TimeSpan.FromMilliseconds(60),
                TimelinePoll = TimeSpan.FromMilliseconds(10), LateTimelinePoll = TimeSpan.FromMilliseconds(20),
            });
        _controller = app;
        var files = new MusicVideoFiles(_storage);
        var resolver = new MusicVideoResolver(files, new Playable(), null, new MusicVideoCache(_storage), new MusicVideoLinks(_storage));
        var video = new MusicVideoController(resolver, new MusicVideoOffsets(_storage), app.ReadSongPosition, app.MusicVideoLyrics, files: files);
        app.MusicVideoPendingWait = wait ?? TimeSpan.FromSeconds(30);
        if (!connectAfterStart) app.UseMusicVideo(video, _feed, youTube);
        app.Start();
        _connect = () => app.UseMusicVideo(video, _feed, youTube);
        return (app, video);
    }

    private Action? _connect;

    // The app connects the music video after the controller has started (AppHost.StartMusicVideo, as the visualisers):
    // a song already known by then is taken up at once.
    [Fact]
    public async Task ConnectedAfterStartUpTheSongAlreadyPlayingIsTakenUp()
    {
        new MusicVideoFiles(_storage).Set(VideoFile, SongA.Key);
        _source.Add(Playing("Song A"));
        var (app, video) = Start(new AppSettings { BackgroundMode = "musicVideo" }, connectAfterStart: true);
        await WaitUntil(() => app.Status.Duration is not null);
        _connect!();
        await WaitUntil(() => _renderer.Layers.Background == "musicVideo" && _feed.Read().LocalPath == VideoFile);
        Assert.Equal(new MusicVideoStatus.Showing(new MusicVideoSource.Local(VideoFile), "Song A"), video.Status);
    }

    private FakeMediaSession Playing(string title, double position = 30)
    {
        var session = new FakeMediaSession("Spotify.exe", _time);
        session.SetTrack(title, "Artist").SetPlayback(Lunalyr.Core.NowPlaying.PlaybackStatus.Playing).SetTimeline(position, 200);
        return session;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the controller.");
    }

    private static MusicVideoSong SongA => new("Song A", "Artist", "Album", 200);

    [Fact]
    public async Task ASongWithAVideoFileDrawsItOnTheSongsOwnClock()
    {
        new MusicVideoFiles(_storage).Set(VideoFile, SongA.Key);
        _source.Add(Playing("Song A"));
        var (app, video) = Start(new AppSettings { BackgroundMode = "musicVideo", LyricOffset = 0.5 });
        await WaitUntil(() => _renderer.Layers.Background == "musicVideo" && _feed.Read().LocalPath is not null);
        var inputs = _feed.Read();
        Assert.Equal(VideoFile, inputs.LocalPath);
        // The player's own position: not the lyrics' clock, which runs half a second early here.
        Assert.Equal(30, inputs.SongTime, 1);
        Assert.Equal(app.ReadClock().Seconds - 0.5, inputs.SongTime, 1);
        video.NudgeOffset(5);
        Assert.Equal(5, _feed.Read().Offset);
        Assert.Equal(0.35, inputs.Dim);
        // The layer's reports reach the controller: the length estimate, and a sample for Align.
        _feed.Report(VideoFile, 215, 35, true);
        Assert.True(video.AlignAvailability.IsAvailable || video.AlignAvailability.Reason == MusicVideoAlignAvailability.NotPlaying);
        // A file that can't be shown falls back.
        _feed.Failed(VideoFile, "test");
        await WaitUntil(() => _renderer.Layers.Background != "musicVideo");
        Assert.Equal(new MusicVideoStatus.Missing(MusicVideoMiss.LocalFileFailed, "Song A"), video.Status);
    }

    [Fact]
    public async Task WithoutAVideoTheChosenFallbackDrawsAfterTheAlbumColours()
    {
        _source.Add(Playing("Song A"));
        var (_, video) = Start(new AppSettings { BackgroundMode = "musicVideo", MusicVideoFallback = "colourOnly" }, wait: TimeSpan.FromMilliseconds(300));
        // Looked up offline (search off), nothing found: the type chosen without a video.
        await WaitUntil(() => video.Status is MusicVideoStatus.Missing);
        await WaitUntil(() => _renderer.Layers.Background == "colour");
        Assert.Contains("Showing", MusicVideoController.Note(video.Status, false, "Colour only"));
    }

    [Fact]
    public async Task WhileTheVideoIsLookedForTheAlbumColoursShowThenTheFallbackOnceOverdue()
    {
        _lyrics.Answer("Song A", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "A line")));
        var session = Playing("Song A");
        // No length yet: the song can't be looked up, so the wallpaper waits on the album colours…
        session.SetTimeline(30, null);
        _source.Add(session);
        Start(new AppSettings { BackgroundMode = "musicVideo", MusicVideoFallback = "colourOnly" }, wait: TimeSpan.FromMilliseconds(400));
        await WaitUntil(() => _renderer.Layers.Background == "musicVideo");
        Assert.Null(_feed.Read().LocalPath);
        // …and once that has lasted the wait, the fallback.
        await WaitUntil(() => _renderer.Layers.Background == "colour");
    }

    [Fact]
    public async Task AYouTubeVideoDrawsTheSeeThroughLayerOnlyWithAPlayer()
    {
        new MusicVideoLinks(_storage).Set("https://youtu.be/4NRXx6U8ABQ", SongA.Key);
        _source.Add(Playing("Song A"));
        Start(new AppSettings { BackgroundMode = "musicVideo" });
        await WaitUntil(() => _renderer.Layers.Background == "musicVideo.youtube");
        Assert.Null(_feed.Read().LocalPath);
    }

    [Fact]
    public async Task WithoutAPlayerThisRunAYouTubeVideoFallsBack()
    {
        new MusicVideoLinks(_storage).Set("https://youtu.be/4NRXx6U8ABQ", SongA.Key);
        _source.Add(Playing("Song A"));
        var (_, video) = Start(new AppSettings { BackgroundMode = "musicVideo", MusicVideoFallback = "desktop" }, youTube: false);
        await WaitUntil(() => video.ShowingYouTubeId is not null);
        await WaitUntil(() => _renderer.Layers.Background == "desktop");
    }

    [Fact]
    public async Task AnotherTypeLooksNothingUp()
    {
        new MusicVideoFiles(_storage).Set(VideoFile, SongA.Key);
        _source.Add(Playing("Song A"));
        var (app, video) = Start(new AppSettings { BackgroundMode = "colourOnly" });
        await WaitUntil(() => app.MusicVideoTrack is not null);
        Assert.IsType<MusicVideoStatus.Off>(video.Status);
        app.ApplySettings(app.Settings with { BackgroundMode = "musicVideo" });
        await WaitUntil(() => _renderer.Layers.Background == "musicVideo" && _feed.Read().LocalPath == VideoFile);
    }

    [Fact]
    public async Task TapToSyncTimesAgainstTheLinesInTheSongsOwnTime()
    {
        _lyrics.Answer("Song A", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "First line", "Second line")));
        _source.Add(Playing("Song A"));
        var (app, _) = Start(new AppSettings { BackgroundMode = "musicVideo", LyricOffset = 0.5 });
        await WaitUntil(() => app.MusicVideoLyrics(SongA) is not null);
        var lines = app.MusicVideoLyrics(SongA)!;
        var shown = Assert.IsType<Lunalyr.Core.Lyrics.LyricResult.Synced>(_renderer.Last!.Lyrics);
        Assert.Equal(shown.Lines[0].Start - 0.5, lines[0].Start, 6);
        Assert.Null(app.MusicVideoLyrics(SongA with { Title = "Other" }));
    }

    // ---- Settings (the Mac's testSettingsRememberThePreviousTypeAsTheFallback) ----

    // Ported from MusicVideoTests.swift: testSettingsRememberThePreviousTypeAsTheFallback
    [Fact]
    public void SettingsRememberThePreviousTypeAsTheFallback()
    {
        var settings = new AppSettings();
        Assert.False(settings.MusicVideoOnline);
        var visualiser = settings with { BackgroundMode = "visualizer" };
        var video = AppSettings.RememberMusicVideoFallback(visualiser, visualiser with { BackgroundMode = "musicVideo" });
        Assert.Equal("visualizer", video.MusicVideoFallback);
        Assert.Equal("visualizer", AppSettings.RememberMusicVideoFallback(video, video).MusicVideoFallback);
        Assert.Equal("auto", (video with { MusicVideoFallback = "musicVideo" }).Normalized().MusicVideoFallback);
        Assert.DoesNotContain("musicVideo", AppSettings.MusicVideoFallbacks);
        Assert.Equal(AppSettings.BackgroundModes.Where(m => m != "musicVideo"), AppSettings.MusicVideoFallbacks);
    }

    // Windows' own: YouTube's site is searched by default (no key); "Use my API key" is the other choice; anything else
    // reads as the site.
    [Fact]
    public void YouTubeIsSearchedOnItsSiteUnlessTheOwnerChoosesTheKey()
    {
        Assert.Equal("site", new AppSettings().Normalized().MusicVideoSearch);
        Assert.Equal("apiKey", new AppSettings { MusicVideoSearch = "apiKey" }.Normalized().MusicVideoSearch);
        Assert.Equal("site", new AppSettings { MusicVideoSearch = "something else" }.Normalized().MusicVideoSearch);
        Assert.Equal(["site", "apiKey"], AppSettings.MusicVideoSearchModes);
    }

    [Fact]
    public void DimmingAndBlurStayInTheMacsRanges()
    {
        var defaults = new AppSettings().Normalized();
        Assert.Equal((0.35, 0.0, "auto"), (defaults.MusicVideoDim, defaults.MusicVideoBlur, defaults.MusicVideoFallback));
        var wild = new AppSettings { MusicVideoDim = 3, MusicVideoBlur = -1, MusicVideoFallback = "nonsense" }.Normalized();
        Assert.Equal((0.85, 0.0, "auto"), (wild.MusicVideoDim, wild.MusicVideoBlur, wild.MusicVideoFallback));
        Assert.Equal(24, new AppSettings { MusicVideoBlur = 99 }.Normalized().MusicVideoBlur);
        Assert.Equal(0.35, new AppSettings { MusicVideoDim = double.NaN }.Normalized().MusicVideoDim);
    }
}
