using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Demo;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary>The album colours: the player's cover → a palette in the scene, off the caller's thread, cached per track.</summary>
public sealed class AppControllerArtworkTests : IAsyncDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeLyricLookup _lyrics = new();
    private readonly FakeRenderer _renderer = new();
    private readonly List<FakeMediaSession> _sessions = [];
    private AppController? _controller;

    /// <summary>A red cover, as a player would share it (BMP bytes).</summary>
    private static readonly byte[] RedCover = DemoCover.Paint(new(210, 30, 40), new(190, 20, 50), new(220, 40, 30), new(200, 30, 60), new(230, 50, 50), 64);

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
        foreach (var session in _sessions) Assert.Empty(session.Commands);
    }

    private AppController Controller(Func<Stream, CancellationToken, Task<Palette?>>? palette = null, TimeSpan[]? retries = null)
    {
        var options = new AppControllerOptions
        {
            TimeProvider = _time,
            TimelineWait = TimeSpan.FromMilliseconds(60),
            TimelinePoll = TimeSpan.FromMilliseconds(10),
            LateTimelinePoll = TimeSpan.FromMilliseconds(20),
            ArtworkRetries = retries ?? [],
        };
        if (palette is not null) options = options with { ArtworkPalette = palette };
        _controller = new AppController(_source, _lyrics, _renderer, new AppSettings(), new SongOffsets(), ReadingServices.Basic,
            new PlaybackGuard(), Logger.Null, null, options);
        return _controller;
    }

    private FakeMediaSession Playing(string title, byte[]? cover)
    {
        var session = new FakeMediaSession("Spotify.exe", _time) { Thumbnail = cover };
        _sessions.Add(session);
        session.SetTrack(title).SetPlayback(PlaybackStatus.Playing).SetTimeline(30, 200);
        return session;
    }

    /// <summary>The player moves on: new metadata, then its timeline for the new track (as the existing tests do).</summary>
    private void NextTrack(FakeMediaSession session, string title)
    {
        _time.AdvanceSeconds(1);
        session.SetTrack(title);
        session.SetTimeline(1, 180);
    }

    /// <summary>A palette step the test completes by hand, one call at a time.</summary>
    private sealed class HeldPalettes
    {
        private readonly Lock _gate = new();
        private readonly List<(TaskCompletionSource<Palette?> Answer, CancellationToken Token)> _calls = [];

        public int Count
        {
            get { lock (_gate) return _calls.Count; }
        }

        public (TaskCompletionSource<Palette?> Answer, CancellationToken Token) this[int index]
        {
            get { lock (_gate) return _calls[index]; }
        }

        public Task<Palette?> Read(Stream stream, CancellationToken token)
        {
            var answer = new TaskCompletionSource<Palette?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _calls.Add((answer, token));
            return answer.Task;
        }
    }

    private static Palette Named(float red) =>
        new(new System.Numerics.Vector4(red, 0, 0, 1), [], System.Numerics.Vector4.One, System.Numerics.Vector4.One);

    [Fact]
    public async Task TheCoversColoursGoInTheScene()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        _source.Add(Playing("Song", RedCover));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();

        var last = _renderer.Last!;
        Assert.NotNull(last.Lyrics);
        Assert.NotSame(PaletteMapper.Fallback, last.Palette);
        Assert.Equal(9, last.Palette.ArtworkStops.Count);
        var accent = last.Palette.Accents[0];
        Assert.True(accent.X > accent.Z + 0.2f && accent.X > accent.Y + 0.2f, $"a red cover gives red colours: {accent}");
        Assert.Same(last.Palette, controller.Scene.Palette);
    }

    [Fact]
    public async Task WithoutACoverTheDefaultColoursAreUsed()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        var controller = Controller();
        // Before anything plays, too.
        Assert.Same(PaletteMapper.Fallback, controller.Scene.Palette);
        _source.Add(Playing("Song", cover: null));
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.All(_renderer.Scenes, scene => Assert.Same(PaletteMapper.Fallback, scene.Palette));
    }

    [Fact]
    public async Task ACoverThatIsNotAnImageGivesTheDefaultColours()
    {
        _source.Add(Playing("Song", [1, 2, 3, 4, 5]));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.Same(PaletteMapper.Fallback, _renderer.Last!.Palette);
    }

    [Fact]
    public async Task ACoverSharedAMomentAfterTheTitleIsStillUsed()
    {
        var palettes = new HeldPalettes();
        var session = Playing("Song", cover: null);
        _source.Add(session);
        var controller = Controller(palettes.Read, [.. Enumerable.Repeat(TimeSpan.FromMilliseconds(30), 20)]);
        controller.Start();
        await Task.Delay(80, TestContext.Current.CancellationToken); // the first looks find nothing
        session.Thumbnail = RedCover;
        await WaitUntil(() => palettes.Count == 1);
        var red = Named(1);
        palettes[0].Answer.SetResult(red);
        await WaitUntil(() => ReferenceEquals(_renderer.Last?.Palette, red));
    }

    [Fact]
    public async Task ColoursAreCachedPerTrackAndTheNextTrackCancelsACoverStillLoading()
    {
        var palettes = new HeldPalettes();
        var session = Playing("Song A", RedCover);
        _source.Add(session);
        var controller = Controller(palettes.Read);
        controller.Start();
        await WaitUntil(() => palettes.Count == 1);
        NextTrack(session, "Song B");
        await WaitUntil(() => palettes.Count == 2);
        Assert.True(palettes[0].Token.IsCancellationRequested, "the old track's cover is no longer wanted");
        var b = Named(0.2f);
        palettes[1].Answer.SetResult(b);
        await WaitUntil(() => ReferenceEquals(controller.Scene.Palette, b));

        // A was cancelled before it was read, so it is read now; B comes from the cache.
        NextTrack(session, "Song A");
        await WaitUntil(() => palettes.Count == 3);
        var a = Named(0.8f);
        palettes[2].Answer.SetResult(a);
        await WaitUntil(() => ReferenceEquals(controller.Scene.Palette, a));
        NextTrack(session, "Song B");
        await WaitUntil(() => ReferenceEquals(_renderer.Last?.Palette, b));
        await controller.WhenIdleAsync();
        Assert.Equal(3, palettes.Count);
    }

    [Fact]
    public async Task ALateCoverForAnEarlierTrackIsIgnored()
    {
        var palettes = new HeldPalettes();
        var session = Playing("Song A", RedCover);
        _source.Add(session);
        var controller = Controller(palettes.Read);
        controller.Start();
        await WaitUntil(() => palettes.Count == 1);
        NextTrack(session, "Song B");
        await WaitUntil(() => palettes.Count == 2);
        var b = Named(0.2f);
        palettes[1].Answer.SetResult(b);
        await WaitUntil(() => ReferenceEquals(controller.Scene.Palette, b));

        // A's decode ignores its cancellation and answers anyway.
        palettes[0].Answer.SetResult(Named(0.9f));
        await controller.WhenIdleAsync();
        Assert.Same(b, controller.Scene.Palette);
        Assert.Same(b, _renderer.Last!.Palette);
    }

    [Fact]
    public async Task LookingTheSongUpAgainKeepsItsColours()
    {
        var palettes = new HeldPalettes();
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        _source.Add(Playing("Song", RedCover));
        var controller = Controller(palettes.Read);
        controller.Start();
        await WaitUntil(() => palettes.Count == 1);
        var red = Named(1);
        palettes[0].Answer.SetResult(red);
        await controller.WhenIdleAsync();

        controller.ApplySettings(controller.Settings with { LyricMatchMode = "priority" });
        await controller.WhenIdleAsync();
        Assert.Equal(2, _lyrics.Queries.Count);
        Assert.Equal(1, palettes.Count);
        Assert.Same(red, _renderer.Last!.Palette);
    }

    [Fact]
    public async Task ThePlayerGoingAwayBringsBackTheDefaultColours()
    {
        var session = _source.Add(Playing("Song", RedCover));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.NotSame(PaletteMapper.Fallback, _renderer.Last!.Palette);

        _source.Remove(session);
        await controller.WhenIdleAsync();
        Assert.Same(PaletteMapper.Fallback, _renderer.Last!.Palette);
    }

    [Fact]
    public async Task APlayerStillSharingThePreviousCoverIsLookedAtAgain()
    {
        var palettes = new HeldPalettes();
        var session = Playing("Song A", RedCover);
        _source.Add(session);
        var controller = Controller(palettes.Read);
        controller.Start();
        await WaitUntil(() => palettes.Count == 1);
        var a = Named(0.8f);
        palettes[0].Answer.SetResult(a);
        await WaitUntil(() => ReferenceEquals(controller.Scene.Palette, a));

        // Song B starts while the player still hands over Song A's cover; B's own arrives a moment later.
        NextTrack(session, "Song B");
        await WaitUntil(() => palettes.Count == 2);
        var stale = Named(0.81f);
        palettes[1].Answer.SetResult(stale);
        await WaitUntil(() => ReferenceEquals(controller.Scene.Palette, stale));
        session.Thumbnail = BlueCover;
        await WaitUntil(() =>
        {
            _time.AdvanceMilliseconds(250); // until a further look's wait is over
            return palettes.Count == 3;
        });
        var b = Named(0.1f);
        palettes[2].Answer.SetResult(b);
        await WaitUntil(() => ReferenceEquals(_renderer.Last?.Palette, b));

        // B's own colours were cached, the stale ones weren't: going back to A and on to B decodes nothing more.
        NextTrack(session, "Song A");
        await WaitUntil(() => ReferenceEquals(controller.Scene.Palette, a));
        NextTrack(session, "Song B");
        await WaitUntil(() => ReferenceEquals(controller.Scene.Palette, b));
        await controller.WhenIdleAsync();
        Assert.Equal(3, palettes.Count);
    }

    [Fact]
    public async Task TheSameAlbumsCoverOnTheNextTrackKeepsItsColoursAndIsCached()
    {
        var palettes = new HeldPalettes();
        var session = Playing("Track 1", RedCover);
        _source.Add(session);
        var controller = Controller(palettes.Read);
        controller.Start();
        await WaitUntil(() => palettes.Count == 1);
        var red = Named(0.8f);
        palettes[0].Answer.SetResult(red);
        await WaitUntil(() => ReferenceEquals(controller.Scene.Palette, red));

        NextTrack(session, "Track 2");
        await WaitUntil(() => palettes.Count == 2);
        var again = Named(0.8f);
        palettes[1].Answer.SetResult(again);
        await WaitUntil(() => ReferenceEquals(controller.Scene.Palette, again));
        for (var i = 0; i < 20; i++) // both further looks find the same cover: nothing more is decoded
        {
            _time.AdvanceMilliseconds(250);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await controller.WhenIdleAsync();
        Assert.Equal(2, palettes.Count);
        Assert.Same(again, controller.Scene.Palette);
        NextTrack(session, "Track 1");
        await WaitUntil(() => ReferenceEquals(controller.Scene.Palette, red));
        NextTrack(session, "Track 2");
        await WaitUntil(() => ReferenceEquals(controller.Scene.Palette, again));
        Assert.Equal(2, palettes.Count);
    }

    /// <summary>
    /// Tester feedback on 0.9.0: the previous album's cover showed, and stayed, when the player kept sending it for a song
    /// from another album. The colours carry on, but the picture isn't shown for the new song or kept for it.
    /// </summary>
    [Fact]
    public async Task AnotherAlbumsCoverKeptByThePlayerIsNeitherShownNorKept()
    {
        var palettes = new HeldPalettes();
        var session = Playing("Song A", RedCover);
        _source.Add(session);
        var controller = Controller(palettes.Read);
        controller.Start();
        await WaitUntil(() => palettes.Count == 1);
        var red = Named(0.8f);
        palettes[0].Answer.SetResult(red);
        await WaitUntil(() => ReferenceEquals(controller.Scene.Palette, red));
        await WaitUntil(() => controller.Scene.Cover is not null);

        _time.AdvanceSeconds(1);
        session.SetTrack("Song B", album: "Another album");
        session.SetTimeline(1, 180);
        await WaitUntil(() => palettes.Count == 2);
        palettes[1].Answer.SetResult(Named(0.81f));
        for (var i = 0; i < 20; i++) // the further looks find the same cover again
        {
            _time.AdvanceMilliseconds(250);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        await controller.WhenIdleAsync();
        Assert.Null(controller.Scene.Cover);
        // Nothing was kept for Song B: coming back to it reads its cover again.
        NextTrack(session, "Song A");
        await WaitUntil(() => ReferenceEquals(controller.Scene.Palette, red));
        _time.AdvanceSeconds(1);
        session.SetTrack("Song B", album: "Another album");
        session.SetTimeline(1, 180);
        await WaitUntil(() => palettes.Count == 3);
    }

    /// <summary>A blue cover (BMP bytes).</summary>
    private static readonly byte[] BlueCover = DemoCover.Paint(new(30, 40, 210), new(20, 50, 190), new(40, 30, 220), new(30, 60, 200), new(50, 50, 230), 64);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the controller.");
    }
}
