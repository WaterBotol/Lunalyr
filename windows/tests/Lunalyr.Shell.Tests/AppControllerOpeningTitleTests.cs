using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Shell.Demo;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary>What the opening title needs from the app: the album, the track's key, its cover and the title's reading.</summary>
public sealed class AppControllerOpeningTitleTests : IAsyncDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeLyricLookup _lyrics = new();
    private readonly FakeRenderer _renderer = new();
    private readonly List<FakeMediaSession> _sessions = [];
    private AppController? _controller;

    private static readonly byte[] RedCover = DemoCover.Paint(new(210, 30, 40), new(190, 20, 50), new(220, 40, 30), new(200, 30, 60), new(230, 50, 50), 64);

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
        foreach (var session in _sessions) Assert.Empty(session.Commands);
    }

    private AppController Controller(AppSettings? settings = null)
    {
        var options = new AppControllerOptions
        {
            TimeProvider = _time,
            TimelineWait = TimeSpan.FromMilliseconds(60),
            TimelinePoll = TimeSpan.FromMilliseconds(10),
            LateTimelinePoll = TimeSpan.FromMilliseconds(20),
            ArtworkRetries = [],
            ArtworkRechecks = [],
        };
        _controller = new AppController(_source, _lyrics, _renderer, settings ?? new AppSettings(), new SongOffsets(), ReadingServices.Basic,
            new PlaybackGuard(), Logger.Null, null, options);
        return _controller;
    }

    private FakeMediaSession Playing(string title, byte[]? cover, string album = "Rare Hymns")
    {
        var session = new FakeMediaSession("Spotify.exe", _time) { Thumbnail = cover };
        _sessions.Add(session);
        session.SetTrack(title, "Yot Club", album).SetPlayback(PlaybackStatus.Playing).SetTimeline(1, 200);
        return session;
    }

    private void NextTrack(FakeMediaSession session, string title, byte[]? cover)
    {
        _time.AdvanceSeconds(1);
        session.Thumbnail = cover;
        session.SetTrack(title, "Yot Club", "Rare Hymns");
        session.SetTimeline(1, 180);
    }

    [Fact]
    public async Task TheSceneCarriesTheSongsAlbumKeyAndCover()
    {
        _lyrics.Answer("Human Nature", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        _source.Add(Playing("Human Nature", RedCover));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();

        var scene = controller.Scene;
        Assert.Equal("Rare Hymns", scene.Album);
        Assert.False(string.IsNullOrEmpty(scene.TrackKey));
        var cover = Assert.IsType<Core.Colour.CoverArt>(scene.Cover);
        Assert.Equal((64, 64), (cover.Width, cover.Height)); // never enlarged
        Assert.Equal(64 * 64 * 4, cover.Pixels.Length);
        Assert.True(scene.Style.OpeningTitle);
        Assert.True(scene.Style.OpeningTitleCover);
        // A red cover, premultiplied BGRA: red is the third byte.
        Assert.True(cover.Pixels[2] > cover.Pixels[0] + 60);
    }

    [Fact]
    public async Task TheNextSongNeverShowsThePreviousCoverAndAKnownSongGetsItsBack()
    {
        _lyrics.Answer("Song A", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        _lyrics.Answer("Song B", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        var session = Playing("Song A", RedCover);
        _source.Add(session);
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.NotNull(controller.Scene.Cover);

        // A song with no cover: the previous one's never shows on its card, from its first scene on.
        NextTrack(session, "Song B", cover: null);
        await controller.WhenIdleAsync();
        Assert.Equal("Song B", controller.Scene.Title);
        Assert.Null(controller.Scene.Cover);
        Assert.All(_renderer.Scenes.Where(scene => scene.Title == "Song B"), scene => Assert.Null(scene.Cover));

        // Back to the first song: its colours and its cover come from the cache.
        NextTrack(session, "Song A", RedCover);
        await controller.WhenIdleAsync();
        Assert.NotNull(controller.Scene.Cover);
    }

    // The Mac's 297b2f2: while the song's own cover is read, the card keeps its place; once read (or none is shared), not.
    [Fact]
    public async Task TheCoversPlaceIsKeptOnlyWhileItIsRead()
    {
        _lyrics.Answer("Song A", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        _lyrics.Answer("Song B", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        var session = Playing("Song A", RedCover);
        _source.Add(session);
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.NotNull(controller.Scene.Cover);
        Assert.False(controller.Scene.CoverPending);
        Assert.Contains(_renderer.Scenes, scene => scene.Title == "Song A" && scene.CoverPending && scene.Cover is null);

        // A song that shares no cover: kept for it only while it is looked for.
        NextTrack(session, "Song B", cover: null);
        await controller.WhenIdleAsync();
        Assert.Null(controller.Scene.Cover);
        Assert.False(controller.Scene.CoverPending);
    }

    [Fact]
    public async Task ATitleInAnotherScriptGetsItsReadingCapitalised()
    {
        _lyrics.Answer("사랑해", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        _source.Add(Playing("사랑해", RedCover));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.Equal("Saranghae", controller.Scene.TitleReading);

        // Readings turned off: no reading under the title either.
        controller.ApplySettings(controller.Settings with { RomanizationDisplay = "off" });
        await controller.WhenIdleAsync();
        Assert.Null(controller.Scene.TitleReading);
    }

    [Fact]
    public async Task ALatinTitleHasNoReading()
    {
        _lyrics.Answer("Human Nature", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line")));
        _source.Add(Playing("Human Nature", RedCover));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.Null(controller.Scene.TitleReading);
    }
}
