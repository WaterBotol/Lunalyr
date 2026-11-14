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

    private AppController Controller(AppSettings? settings = null, bool reduceMotion = false, TimeSpan? onlineRetryDelay = null)
    {
        _controller = new AppController(_source, _lyrics, _renderer, settings ?? new AppSettings(), _offsets, ReadingServices.Basic,
            _guard, Logger.Null, _connectivity,
            new AppControllerOptions
            {
                TimeProvider = _time,
                TimelineWait = TimeSpan.FromMilliseconds(60),
                TimelinePoll = TimeSpan.FromMilliseconds(10),
                LateTimelinePoll = TimeSpan.FromMilliseconds(20),
                OnlineRetryDelay = onlineRetryDelay ?? TimeSpan.FromMilliseconds(150),
                ArtworkRetries = [],
                ReduceMotion = reduceMotion,
            });
        return _controller;
    }

    // The Mac's PlayerOptions.playersPublisher: a new choice moves now playing to that player at once (Settings › Music
    // players › Player); the other players are never read.
    [Fact]
    public async Task AChosenPlayerIsFollowedAndTheOthersAreLeftAlone()
    {
        _lyrics.Answer("Song A", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from A")));
        _lyrics.Answer("Song B", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from B")));
        var spotify = _source.Add(Playing("Song A"));
        var other = new FakeMediaSession("foobar2000.exe", _time);
        _sessions.Add(other);
        other.SetTrack("Song B").SetPlayback(PlaybackStatus.Playing).SetTimeline(5, 200);
        _source.Add(other);
        var controller = Controller(new AppSettings { Player = "spotify" });
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.Equal("spotify", controller.FollowedPlayer);
        Assert.Same(spotify, controller.Monitor.Session);
        Assert.Equal("Line from A", Assert.IsType<LyricResult.Synced>(_renderer.Last!.Lyrics).Lines[0].Text);
        Assert.Equal(0, other.Reads);

        controller.ApplySettings(controller.Settings with { Player = "automatic" });
        await controller.WhenIdleAsync();
        Assert.Same(other, controller.Monitor.Session); // Windows calls foobar2000 current
        Assert.Equal("Line from B", Assert.IsType<LyricResult.Synced>(_renderer.Last!.Lyrics).Lines[0].Text);
    }

    [Fact]
    public async Task ReduceMotionTravelsWithEveryScene()
    {
        // Off unless the app says the system asks for it.
        _lyrics.Answer("Song A", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from A")));
        _lyrics.Answer("Song B", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from B")));
        var first = _source.Add(Playing("Song A"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.All(_renderer.Scenes, scene => Assert.False(scene.ReduceMotion));

        // Switched on while a song shows: the scene on screen is handed over again at once, lyrics and all.
        var before = _renderer.Scenes.Count;
        controller.SetReduceMotion(true);
        Assert.Equal(before + 1, _renderer.Scenes.Count);
        Assert.True(_renderer.Last!.ReduceMotion);
        Assert.Equal("Line from A", Assert.IsType<LyricResult.Synced>(_renderer.Last.Lyrics).Lines[0].Text);
        controller.SetReduceMotion(true);
        Assert.Equal(before + 1, _renderer.Scenes.Count); // no change, nothing handed over

        // It stays through a settings change, a new song and the player going away.
        controller.ApplySettings(controller.Settings with { LyricComposition = "spoken" });
        _source.Add(Playing("Song B", appId: "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"));
        await WaitUntil(() => _renderer.Last?.Lyrics is LyricResult.Synced { Lines: [{ Text: "Line from B" }] });
        _source.Remove(first);
        await controller.WhenIdleAsync();
        Assert.All(_renderer.Scenes.Skip(before), scene => Assert.True(scene.ReduceMotion));

        controller.SetReduceMotion(false);
        Assert.False(_renderer.Last!.ReduceMotion);
    }

    [Fact]
    public async Task ReduceMotionAtStartUpIsInTheFirstScene()
    {
        _source.Add(Playing("Song", timeline: false));
        var controller = Controller(reduceMotion: true);
        controller.Start();
        await WaitUntil(() => _renderer.Last?.Status == StatusText.NoPosition);
        Assert.All(_renderer.Scenes, scene => Assert.True(scene.ReduceMotion));
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
        Assert.Equal("constellation", _renderer.Layers.Composition);
        // Custom Mix (the default type) gives the song one of the effects or patterns.
        Assert.True(TestBackgrounds.IsGpu(_renderer.Layers.Background), _renderer.Layers.Background);
        Assert.Equal(60, _renderer.FrameRate);
    }

    /// <summary>
    /// Synced lyrics that are only instrumental breaks (kept as untimed, as on the Mac: parity row 13) show as the song's
    /// lyrics with nothing sung: the presentation steps and the scene take them without a sung line.
    /// </summary>
    [Fact]
    public async Task SyncedLyricsOfInstrumentalBreaksAloneShow()
    {
        LyricLine Break(double start) => new() { Start = start, End = start + 60, Text = "", IsInstrumental = true };
        var breaks = new LyricResult.Synced([Break(0), Break(60), Break(120)], new LyricProvenance("Line-synced", "LRCLIB"));
        _lyrics.Answer("Interlude", TestLyrics.Found(breaks));
        _source.Add(Playing("Interlude"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();

        var shown = Assert.IsType<LyricResult.Synced>(_renderer.Last!.Lyrics);
        Assert.Equal(3, shown.Lines.Count);
        Assert.All(shown.Lines, line => Assert.True(line.IsInstrumental));
        Assert.Null(_renderer.Last.Status);
        Assert.Equal(StatusText.Showing, controller.Status.Status);
    }

    /// <summary>The player's genre goes into the lookup as a hint at the language sung (LyricLanguage), as on the Mac.</summary>
    [Fact]
    public async Task TheLookupCarriesThePlayersGenre()
    {
        _lyrics.Answer("Idol", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "First line", "Second line")));
        var session = new FakeMediaSession("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", _time);
        _sessions.Add(session);
        session.SetTrack("Idol", "YOASOBI", "Idol - Single", "J-Pop").SetPlayback(PlaybackStatus.Playing).SetTimeline(30, 213);
        _source.Add(session);
        var controller = Controller();

        controller.Start();
        await controller.WhenIdleAsync();

        var query = Assert.Single(_lyrics.Queries);
        Assert.Equal(("Idol", "J-Pop"), (query.Title, query.Genre));
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
    public async Task ABetterMatchWaitsForTheLineBeingSungToEnd()
    {
        // The player is at 30 s, in the middle of the first line (28–33 s). A better match (an upgrade, or LRCLIB
        // demoted by a later agreement) swaps in only once that line ends, never mid-line.
        static LyricResult.Synced Lines(string source, string first, string second) => new(
        [
            new LyricLine { Start = 28, End = 33, Text = first, Words = [new LyricWord(first, 28, 33)] },
            new LyricLine { Start = 36, End = 40, Text = second, Words = [new LyricWord(second, 36, 40)] },
        ], new LyricProvenance("Line-synced", source));
        var shown = Lines("LRCLIB", "Shown first", "Then this");
        var better = Lines("Lyrics+", "Better first", "Better then");
        var upgraded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lyrics.Script("Song", async (_, update, _) =>
        {
            await update!(new LyricLookupReport { Result = shown, Revision = 1, IsComplete = false });
            await update(new LyricLookupReport { Result = better, Revision = 2, IsComplete = false });
            upgraded.SetResult();
            await gate.Task;
            return new LyricLookupReport { Result = better, Revision = 2 };
        });
        _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();

        await upgraded.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("LRCLIB", _renderer.Last!.Lyrics!.Provenance.Source);
        // The lookup may end while the line is still sung: still no swap (the clock has not moved).
        gate.SetResult();
        Assert.Equal("LRCLIB", _renderer.Last!.Lyrics!.Provenance.Source);
        Assert.Equal(StatusText.LookingUp, controller.Status.Status);

        // The line ends at 33 s: the better lyrics take over between lines, and the lookup is done.
        _time.AdvanceSeconds(3.5);
        await WaitUntil(() => _renderer.Last?.Lyrics?.Provenance.Source == "Lyrics+");
        await controller.WhenIdleAsync();
        Assert.Equal("Better first", Assert.IsType<LyricResult.Synced>(_renderer.Last!.Lyrics).Lines[0].Text);
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
        // As on the Mac (§3.5): looked up again a moment (OnlineRetryDelay, 2 s in the app) after the connection returns.
        _connectivity.IsOnline = false;
        _lyrics.Answer("Song", new LyricLookupReport { Offline = true });
        _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.Equal(StatusText.Offline, _renderer.Last!.Status);
        Assert.Single(_lyrics.Queries);

        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Back")));
        _connectivity.Restore();
        Assert.Single(_lyrics.Queries); // not at once
        await controller.WhenIdleAsync();

        Assert.Equal(2, _lyrics.Queries.Count);
        Assert.Null(_renderer.Last!.Status);
        Assert.Equal("Back", Assert.IsType<LyricResult.Synced>(_renderer.Last.Lyrics).Lines[0].Text);
    }

    [Fact]
    public async Task AnOfflineAnswerWhileOnlineLooksAgainAtOnceButOncePerTrack()
    {
        // Windows reports a connection, but every service looked unreachable (one connection dropped on a working
        // link): the Mac looks again straight away, once per track, and still when the connection is next restored.
        _lyrics.Answer("Song", new LyricLookupReport { Offline = true });
        var session = Playing("Song");
        _source.Add(session);
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.Equal(2, _lyrics.Queries.Count);
        Assert.Equal(StatusText.Offline, _renderer.Last!.Status);

        _connectivity.Restore();
        await controller.WhenIdleAsync();
        Assert.Equal(3, _lyrics.Queries.Count);

        // The next track gets its own look again.
        _lyrics.Answer("Song B", new LyricLookupReport { Offline = true });
        _time.AdvanceSeconds(1);
        session.SetTrack("Song B", "Artist");
        session.SetTimeline(1, 200);
        await WaitUntil(() => _lyrics.Queries.Count(q => q.Title == "Song B") == 2);
        await controller.WhenIdleAsync();
        Assert.Equal(2, _lyrics.Queries.Count(q => q.Title == "Song B"));
    }

    [Fact]
    public async Task ARestoredConnectionDoesNotLookAgainWhenTheTrackHasChanged()
    {
        _connectivity.IsOnline = false;
        _lyrics.Answer("Song", new LyricLookupReport { Offline = true });
        var session = Playing("Song");
        _source.Add(session);
        // A long wait, so the track change always lands first; the change must cancel the pending look again.
        var controller = Controller(onlineRetryDelay: TimeSpan.FromSeconds(5));
        controller.Start();
        await controller.WhenIdleAsync();

        // Restored, then the track changes before the retry is due: only the new track is looked up.
        _lyrics.Answer("Song B", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "B")));
        _connectivity.Restore();
        _time.AdvanceSeconds(1);
        session.SetTrack("Song B", "Artist");
        session.SetTimeline(1, 200);
        await WaitUntil(() => _lyrics.Queries.Any(q => q.Title == "Song B"));
        await controller.WhenIdleAsync();
        Assert.Equal(["Song", "Song B"], _lyrics.Queries.Select(q => q.Title));
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
    public async Task MatchTheMusicGivesEachSongALayoutAndKeepsIt()
    {
        _lyrics.Answer("Song A", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from A")));
        _lyrics.Answer("Song B", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from B")));
        _renderer.LayerIds = ["gradient", "colour", "scattered", "spoken", "cascade"];
        var cursors = new List<int>();
        var session = _source.Add(Playing("Song A"));
        _controller = new AppController(_source, _lyrics, _renderer, new AppSettings { LyricComposition = "automatic" }, _offsets,
            ReadingServices.Basic, _guard, Logger.Null, _connectivity, new AppControllerOptions
            {
                TimeProvider = _time, TimelineWait = TimeSpan.FromMilliseconds(60), TimelinePoll = TimeSpan.FromMilliseconds(10),
                ArtworkRetries = [], LayoutCursorChanged = cursors.Add,
            });
        var controller = _controller;
        controller.Start();
        // Until a song is matched, the renderer is never handed "automatic", which it does not have.
        Assert.NotEqual("automatic", _renderer.Layers.Composition);
        await controller.WhenIdleAsync();
        var first = _renderer.Layers.Composition;
        Assert.Contains(first, new[] { "scattered", "spoken", "cascade" });
        Assert.Equal(first, controller.MatchedComposition);

        session.SetTrack("Song B");
        session.SetTimeline(1, 180);
        await WaitUntil(() => _lyrics.Queries.Count == 2);
        await controller.WhenIdleAsync();
        var second = _renderer.Layers.Composition;
        Assert.NotEqual(first, second); // each new song takes the next layout
        Assert.Equal(2, cursors.Count); // the rotation's place is saved

        // Coming back to the first song brings back its layout.
        session.SetTrack("Song A");
        session.SetTimeline(1, 200);
        await WaitUntil(() => _lyrics.Queries.Count == 3);
        await controller.WhenIdleAsync();
        Assert.Equal(first, _renderer.Layers.Composition);

        // A chosen layout is drawn as it is.
        controller.ApplySettings(controller.Settings with { LyricComposition = "spoken" });
        Assert.Equal("spoken", _renderer.Layers.Composition);
    }

    [Fact]
    public async Task ASongIsMatchedOnlyOnceItsLyricsAreKnown()
    {
        var held = _lyrics.Hold("Song");
        _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();
        await WaitUntil(() => _lyrics.Queries.Count == 1);
        // Turned to Custom Mix mid-lookup, then an unrelated change: nothing is decided without the lyrics.
        controller.ApplySettings(controller.Settings with { LyricComposition = "automatic" });
        controller.ApplySettings(controller.Settings with { LyricOffset = 0.5 });
        Assert.Null(controller.MatchedComposition);
        Assert.NotEqual("automatic", _renderer.Layers.Composition);

        held.SetResult(TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        await controller.WhenIdleAsync();
        Assert.NotNull(controller.MatchedComposition);
        Assert.Equal(controller.MatchedComposition, _renderer.Layers.Composition);
    }

    [Fact]
    public async Task TheSceneCarriesTheExtrasSettingsAndTheTrackLength()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        _source.Add(Playing("Song"));
        var controller = Controller(new AppSettings { LyricHighlightMode = "custom", LyricHighlightCustom = "#2bd96b", ShowInterludes = false });
        controller.Start();
        await controller.WhenIdleAsync();
        var scene = _renderer.Last!;
        Assert.True(scene.Style.Credits); // on by default, as on the Mac
        Assert.False(scene.Style.Interludes);
        Assert.Equal("custom", scene.Style.HighlightMode);
        Assert.Equal("#2BD96B", scene.Style.HighlightCustom);
        Assert.Equal(200, scene.Duration);

        controller.ApplySettings(controller.Settings with { CreditsLayer = false });
        Assert.False(_renderer.Last!.Style.Credits);
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
        Assert.Equal(32 + AppController.SyncLead, clock().Seconds, 2); // the lyrics run a little ahead while playing

        session.SetPlayback(PlaybackStatus.Paused);
        _time.AdvanceSeconds(5);
        var paused = clock();
        Assert.False(paused.Playing);
        Assert.Equal(32, paused.Seconds, 2);
        Assert.Equal(PlaybackStatus.Paused, controller.Status.Playback);

        session.SetPlayback(PlaybackStatus.Playing);
        _time.AdvanceSeconds(1);
        Assert.True(clock().Playing);
        Assert.Equal(33 + AppController.SyncLead, clock().Seconds, 2);
    }

    [Fact]
    public async Task TheClockAddsTheGlobalAndSongOffsets()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        _source.Add(Playing("Song"));
        var controller = Controller(new AppSettings { LyricOffset = 0.5 });
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.Equal(30.5 + AppController.SyncLead, _renderer.Clock!().Seconds, 3);

        // A song offset tuned to the source on screen.
        var key = new LyricQuery("Song", "Artist", "Album", 200).RecordingKey;
        _offsets.Set(1.25, key, "LRCLIB");
        Assert.Equal(31.75 + AppController.SyncLead, _renderer.Clock!().Seconds, 3);

        controller.ApplySettings(controller.Settings with { LyricOffset = -1 });
        Assert.Equal(30.25 + AppController.SyncLead, _renderer.Clock!().Seconds, 3);
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
    public async Task ReadingsAreAddedWordByWordForTheDisplayChosen()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "안녕하세요 친구", "Hello")));
        _source.Add(Playing("Song"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();

        var scene = _renderer.Last!;
        var lines = ((LyricResult.Synced)scene.Lyrics!).Lines;
        var words = scene.WordReadings[0];
        Assert.Equal(lines[0].Words.Count, words.Count);
        Assert.Contains("annyeong", words[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(scene.WordReadings.ContainsKey(1)); // already Latin

        // "Replace lyrics": the old readings go with the old style at once; the words to show follow.
        controller.ApplySettings(controller.Settings with { RomanizationDisplay = "replace" });
        Assert.Equal(Render.Scene.ReadingPlacement.Instead, _renderer.Last!.Style.Readings);
        await controller.WhenIdleAsync();
        Assert.Contains("annyeong", _renderer.Last!.WordReadings[0][0].Text, StringComparison.OrdinalIgnoreCase);

        controller.ApplySettings(controller.Settings with { RomanizationDisplay = "off" });
        await controller.WhenIdleAsync();
        Assert.Empty(_renderer.Last!.WordReadings);
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
        Assert.Equal("spoken", _renderer.Layers.Composition);
        Assert.True(TestBackgrounds.IsGpu(_renderer.Layers.Background), _renderer.Layers.Background);
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
