using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

public sealed class AppControllerTests : IAsyncDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeLyricLookup _lyrics = new();
    private readonly FakeRenderer _renderer = new();
    private readonly FakeConnectivity _connectivity = new();
    private readonly PlaybackGuard _guard = new();
    private readonly List<FakeMediaSession> _sessions = [];
    private SongOffsets _offsets = new();
    private AppController? _controller;

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
        // The read-only rule: nothing the controller did may have sent a command to a player.
        foreach (var session in _sessions) Assert.Empty(session.Commands);
    }

    private AppController Controller(AppSettings? settings = null)
    {
        _controller = new AppController(_source, _lyrics, _renderer, settings ?? new AppSettings(), _offsets, ReadingServices.Basic,
            _guard, Logger.Null, _connectivity,
            new AppControllerOptions
            {
                TimeProvider = _time,
                TimelineWait = TimeSpan.FromMilliseconds(60),
                TimelinePoll = TimeSpan.FromMilliseconds(10),
                LateTimelinePoll = TimeSpan.FromMilliseconds(20),
                ArtworkRetries = [],
            });
        return _controller;
    }

    private FakeMediaSession Playing(string title, string artist = "Artist", string appId = "Spotify.exe", double? duration = 200, bool timeline = true)
    {
        var session = new FakeMediaSession(appId, _time);
        _sessions.Add(session);
        session.SetTrack(title, artist).SetPlayback(PlaybackStatus.Playing);
        if (timeline) session.SetTimeline(30, duration);
        return session;
    }

    [Fact]
    public async Task TrackChangeLooksUpAndBuildsTheScene()
    {
        _lyrics.Answer("Song A", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "First line", "Second line")));
        _source.Add(Playing("Song A", "Artist A"));
        var controller = Controller();

        controller.Start();
        await controller.WhenIdleAsync();

        var query = Assert.Single(_lyrics.Queries);
        Assert.Equal(("Song A", "Artist A", 200.0), (query.Title, query.Artist, query.Duration));
        // First a calm "looking up", then the lyrics.
        Assert.Contains(_renderer.Scenes, scene => scene.Title == "Song A" && scene.Status == StatusText.LookingUp && scene.Lyrics is null);
        var last = _renderer.Last!;
        Assert.Equal(("Song A", "Artist A"), (last.Title, last.Artist));
        Assert.Null(last.Status);
        var synced = Assert.IsType<LyricResult.Synced>(last.Lyrics);
        Assert.Equal(["First line", "Second line"], synced.Lines.Select(line => line.Text));
        Assert.Equal("LRCLIB", synced.Provenance.Source);
        Assert.Equal(StatusText.Showing, controller.Status.Status);
        Assert.Equal("Spotify", controller.Status.Player);
        Assert.True(_renderer.IsRunning);
        Assert.Equal(("gradient", "editorial"), _renderer.Layers);
        Assert.Equal(60, _renderer.FrameRate);
    }

    [Fact]
    public async Task ProgressUpdatesShowLyricsBeforeTheLookupEnds()
    {
        var early = TestLyrics.Synced("NetEase", "Early");
        var better = TestLyrics.Synced("Apple Music", "Better");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lyrics.Script("Song", async (_, update, _) =>
        {
            await update!(new LyricLookupReport { Result = early, Revision = 1, IsComplete = false });
            await gate.Task;
            return new LyricLookupReport { Result = better, Revision = 2 };
        });
        _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();

        await WaitUntil(() => _renderer.Last?.Lyrics is not null);
        Assert.Equal("NetEase", _renderer.Last!.Lyrics!.Provenance.Source);
        Assert.Equal(StatusText.LookingUp, controller.Status.Status);

        gate.SetResult();
        await controller.WhenIdleAsync();
        Assert.Equal("Apple Music", _renderer.Last!.Lyrics!.Provenance.Source);
        Assert.Equal(StatusText.Showing, controller.Status.Status);
    }

    [Fact]
    public async Task NothingFoundSaysSoCalmly()
    {
        _lyrics.Answer("Obscure", new LyricLookupReport());
        _source.Add(Playing("Obscure"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();

        Assert.Equal(StatusText.NoLyrics, _renderer.Last!.Status);
        Assert.Null(_renderer.Last.Lyrics);
        Assert.Equal("Obscure", _renderer.Last.Title);
        Assert.Equal(StatusText.NoLyrics, controller.Status.Status);
    }

    [Fact]
    public async Task OfflineWaitsAndLooksAgainWhenTheConnectionReturns()
    {
        _lyrics.Answer("Song", new LyricLookupReport { Offline = true });
        _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.Equal(StatusText.Offline, _renderer.Last!.Status);
        Assert.Single(_lyrics.Queries);

        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Back")));
        _connectivity.Restore();
        await controller.WhenIdleAsync();

        Assert.Equal(2, _lyrics.Queries.Count);
        Assert.Null(_renderer.Last!.Status);
        Assert.Equal("Back", Assert.IsType<LyricResult.Synced>(_renderer.Last.Lyrics).Lines[0].Text);
    }

    [Fact]
    public async Task ARestoredConnectionDoesNothingWhenTheLastLookupWasntOffline()
    {
        _lyrics.Answer("Song", new LyricLookupReport());
        _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();

        _connectivity.Restore();
        await controller.WhenIdleAsync();
        Assert.Single(_lyrics.Queries);
    }

    [Fact]
    public async Task PlayerSwitchCancelsTheOldLookupAndIgnoresItsLateAnswer()
    {
        var held = _lyrics.Hold("Song A");
        _lyrics.Answer("Song B", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from B")));
        var spotify = _source.Add(Playing("Song A"));
        var controller = Controller();
        controller.Start();
        await WaitUntil(() => _lyrics.Queries.Count == 1);

        _source.Add(Playing("Song B", appId: "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"));
        await WaitUntil(() => _lyrics.Queries.Count == 2);
        Assert.True(_lyrics.Tokens[0].IsCancellationRequested);

        // Song A's answer arrives late: it must not replace Song B's lyrics.
        held.SetResult(TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from A")));
        await controller.WhenIdleAsync();

        var last = _renderer.Last!;
        Assert.Equal("Song B", last.Title);
        Assert.Equal("Line from B", Assert.IsType<LyricResult.Synced>(last.Lyrics).Lines[0].Text);
        Assert.DoesNotContain(_renderer.Scenes.SkipWhile(s => s.Title != "Song B"), s => s.Title == "Song A");
        Assert.Equal("Apple Music", controller.Status.Player);
        Assert.Empty(spotify.Commands);
    }

    [Fact]
    public async Task ThePlayerGoingAwayClearsTheScene()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        var session = _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();

        _source.Remove(session);
        await controller.WhenIdleAsync();

        Assert.Null(_renderer.Last!.Title);
        Assert.Null(_renderer.Last.Lyrics);
        Assert.Equal(StatusText.NothingPlaying, controller.Status.Status);
        Assert.Null(controller.Status.Player);
        Assert.True(double.IsNaN(controller.ReadClock().Seconds));
    }

    [Fact]
    public async Task APlayerWithoutAPositionGetsItsOwnStatusAndNoLookup()
    {
        _source.Add(Playing("Song", timeline: false));
        var controller = Controller();
        controller.Start();
        await WaitUntil(() => _renderer.Last?.Status == StatusText.NoPosition);

        await Task.Delay(100, TestContext.Current.CancellationToken); // it keeps waiting, quietly
        Assert.Empty(_lyrics.Queries);
        Assert.Equal(StatusText.NoPosition, _renderer.Last!.Status);
        Assert.Equal("Song", _renderer.Last.Title);
        Assert.Equal(StatusText.NoPosition, controller.Status.Status);
    }

    [Fact]
    public async Task ATimelineThatArrivesLateStillGetsTheSongItsLyrics()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Late but here")));
        var session = _source.Add(Playing("Song", timeline: false));
        var controller = Controller();
        controller.Start();
        await WaitUntil(() => _renderer.Last?.Status == StatusText.NoPosition);

        session.SetTimeline(3, 200);
        await WaitUntil(() => _renderer.Last?.Lyrics is not null);
        await controller.WhenIdleAsync();

        Assert.Equal(200, Assert.Single(_lyrics.Queries).Duration);
        Assert.Null(_renderer.Last!.Status);
        Assert.Equal(StatusText.Showing, controller.Status.Status);
    }

    [Fact]
    public async Task AStreamWithoutALengthIsNotLookedUp()
    {
        _source.Add(Playing("Radio Station", duration: null));
        var controller = Controller();
        controller.Start();
        await WaitUntil(() => _renderer.Last?.Status == StatusText.Stream);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(_lyrics.Queries);
        Assert.Equal(StatusText.Stream, _renderer.Last!.Status);
    }

    [Fact]
    public async Task ANewTrackOnTheSamePlayerReplacesALookupStillRunning()
    {
        var held = _lyrics.Hold("Song A");
        _lyrics.Answer("Song B", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from B")));
        var session = _source.Add(Playing("Song A"));
        var controller = Controller();
        controller.Start();
        await WaitUntil(() => _lyrics.Queries.Count == 1);

        // The player moves on: new metadata, then its timeline for the new track.
        session.SetTrack("Song B");
        session.SetTimeline(1, 180);
        await WaitUntil(() => _lyrics.Queries.Count == 2);
        Assert.True(_lyrics.Tokens[0].IsCancellationRequested);
        Assert.Equal(180, _lyrics.Queries[1].Duration);

        held.SetResult(TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from A")));
        await controller.WhenIdleAsync();

        Assert.Equal("Song B", _renderer.Last!.Title);
        Assert.Equal("Line from B", Assert.IsType<LyricResult.Synced>(_renderer.Last.Lyrics).Lines[0].Text);
        Assert.Equal("Song B", controller.Status.Title);
        Assert.Equal(StatusText.Showing, controller.Status.Status);
    }

    [Fact]
    public async Task LookupsSwitchedOffSaySoAndAskNothing()
    {
        _lyrics.DisabledReason = "--no-network";
        _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();

        Assert.Empty(_lyrics.Queries);
        Assert.Equal(StatusText.LookupsOff, _renderer.Last!.Status);
        Assert.Equal("Song", controller.Status.Title);
    }

    [Fact]
    public async Task AFailingLookupEndsInACalmStatus()
    {
        _lyrics.Script("Song", (_, _, _) => Task.FromException<LyricLookupReport>(new InvalidOperationException("boom")));
        _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();

        Assert.Equal(StatusText.LookupFailed, _renderer.Last!.Status);
    }

    [Fact]
    public async Task PausingTheWallpaperStopsTheRendererNotTheMusic()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        var session = _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.Equal(1, _renderer.Starts);

        controller.ApplySettings(controller.Settings with { WallpaperPaused = true });
        Assert.False(_renderer.IsRunning);
        Assert.True(controller.Status.WallpaperPaused);

        controller.ApplySettings(controller.Settings with { WallpaperPaused = false });
        Assert.True(_renderer.IsRunning);
        Assert.Equal(2, _renderer.Starts);
        Assert.False(controller.Status.WallpaperPaused);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task AWallpaperPausedLastTimeStartsPaused()
    {
        _source.Add(Playing("Song"));
        var controller = Controller(new AppSettings { WallpaperPaused = true });
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.Equal(0, _renderer.Starts);
        Assert.True(controller.Status.WallpaperPaused);
    }

    [Fact]
    public async Task ThePlayerPausingFreezesTheClockForTheRenderer()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        var session = _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();

        var clock = _renderer.Clock!;
        Assert.True(clock().Playing);
        _time.AdvanceSeconds(2);
        Assert.Equal(32, clock().Seconds, 2);

        session.SetPlayback(PlaybackStatus.Paused);
        _time.AdvanceSeconds(5);
        var paused = clock();
        Assert.False(paused.Playing);
        Assert.Equal(32, paused.Seconds, 2);
        Assert.Equal(PlaybackStatus.Paused, controller.Status.Playback);

        session.SetPlayback(PlaybackStatus.Playing);
        _time.AdvanceSeconds(1);
        Assert.True(clock().Playing);
        Assert.Equal(33, clock().Seconds, 2);
    }

    [Fact]
    public async Task TheClockAddsTheGlobalAndSongOffsets()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        _source.Add(Playing("Song"));
        var controller = Controller(new AppSettings { LyricOffset = 0.5 });
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.Equal(30.5, _renderer.Clock!().Seconds, 3);

        // A song offset tuned to the source on screen.
        var key = new LyricQuery("Song", "Artist", "Album", 200).RecordingKey;
        _offsets.Set(1.25, key, "LRCLIB");
        Assert.Equal(31.75, _renderer.Clock!().Seconds, 3);

        controller.ApplySettings(controller.Settings with { LyricOffset = -1 });
        Assert.Equal(30.25, _renderer.Clock!().Seconds, 3);
    }

    [Fact]
    public async Task ReadingsAreAddedPerLine()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "안녕하세요", "Hello")));
        _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();

        var readings = _renderer.Last!.Readings;
        Assert.Contains("annyeong", readings[0], StringComparison.OrdinalIgnoreCase);
        Assert.False(readings.ContainsKey(1)); // already Latin

        controller.ApplySettings(controller.Settings with { RomanizationDisplay = "off" });
        await controller.WhenIdleAsync();
        Assert.Empty(_renderer.Last!.Readings);
        Assert.Equal(Render.Scene.ReadingPlacement.Off, _renderer.Last.Style.Readings);
    }

    [Fact]
    public async Task ChangingTheLyricSourcesLooksTheSongUpAgain()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();

        controller.ApplySettings(controller.Settings with { LyricMatchMode = "priority" });
        await controller.WhenIdleAsync();
        Assert.Equal(2, _lyrics.Queries.Count);

        // Style changes only restyle.
        controller.ApplySettings(controller.Settings with { LyricScale = 1.5, LyricComposition = "spoken", AnimationRate = 30 });
        await controller.WhenIdleAsync();
        Assert.Equal(2, _lyrics.Queries.Count);
        Assert.Equal(0.075f * 1.5f, _renderer.Last!.Style.SizeFraction, 4);
        Assert.Equal(("gradient", "spoken"), _renderer.Layers);
        Assert.Equal(30, _renderer.FrameRate);
    }

    [Fact]
    public async Task NoPlaybackIsReportedAndNoCommandIsEverSent()
    {
        _guard.Block("--no-playback");
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        var session = _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();
        controller.ApplySettings(controller.Settings with { WallpaperPaused = true });
        controller.ApplySettings(controller.Settings with { WallpaperPaused = false });
        session.SetPlayback(PlaybackStatus.Paused);

        Assert.Equal("--no-playback", controller.Status.PlaybackCommandsBlocked);
        Assert.Empty(session.Commands);
        Assert.Equal(0, _guard.RefusedCount); // nothing even tried
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the controller.");
    }
}
