using Lunalyr.Core.Colour;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// Remembered styles reach the wallpaper: a layout and colour remembered for the song playing (or its artist) win over
/// the chosen ones while it plays, and the chosen ones come back for other music. Silent fakes: no player is commanded.
/// </summary>
public sealed class AppControllerStyleMemoryTests : IAsyncDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeLyricLookup _lyrics = new();
    private readonly FakeRenderer _renderer = new();
    private readonly List<FakeMediaSession> _sessions = [];
    private AppController? _controller;

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
        foreach (var session in _sessions) Assert.Empty(session.Commands);
    }

    private AppController Controller(AppSettings settings)
    {
        _controller = new AppController(_source, _lyrics, _renderer, settings, new SongOffsets(), ReadingServices.Basic,
            new PlaybackGuard(), Logger.Null, new FakeConnectivity(),
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

    private FakeMediaSession Playing(string title, string artist)
    {
        var session = new FakeMediaSession("Spotify.exe", _time);
        _sessions.Add(session);
        session.SetTrack(title, artist).SetPlayback(PlaybackStatus.Playing);
        session.SetTimeline(30, 200);
        return session;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    /// <summary>Windows' own: the Mac's LyricLayerView reads the remembered composition and colour the same way.</summary>
    [Fact]
    public async Task ARememberedStyleWinsWhileItsMusicPlays()
    {
        _lyrics.Answer("Song A", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from A")));
        _lyrics.Answer("Song B", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from B")));
        _lyrics.Answer("Song C", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from C")));
        var songA = new LyricQuery("Song A", "Artist A", "Album", 200); // as the fake player names it
        var settings = new AppSettings { LyricComposition = "spoken" };
        settings = StyleMemory.Remember(settings, new RememberedStyle("echo", "sungSweep"), songA, StyleScope.Song);
        settings = StyleMemory.Remember(settings, new RememberedStyle(null, "customGradient"),
                                        new LyricQuery("Anything", "Band C", null, 1), StyleScope.Artist);

        _source.Add(Playing("Song A", "Artist A"));
        var controller = Controller(settings);
        controller.Start();
        await WaitUntil(() => _renderer.Last?.Lyrics is LyricResult.Synced { Lines: [{ Text: "Line from A" }] });
        Assert.Equal("echo", _renderer.Layers.Composition);
        Assert.Equal(LyricColourMode.SungSweep, _renderer.Last!.Style.ColourMode);
        Assert.Equal(StyleScope.Song, controller.RememberedStyle?.Scope);

        // Another artist's song: the chosen layout and colour.
        _source.Add(Playing("Song B", "Artist B"));
        await WaitUntil(() => _renderer.Last?.Lyrics is LyricResult.Synced { Lines: [{ Text: "Line from B" }] });
        Assert.Equal("spoken", _renderer.Layers.Composition);
        Assert.Equal(LyricColourMode.AlbumDrift, _renderer.Last!.Style.ColourMode);
        Assert.Null(controller.RememberedStyle);

        // An artist remembered for its colour only: the colour is theirs, the layout still the chosen one.
        _source.Add(Playing("Song C", "Band C"));
        await WaitUntil(() => _renderer.Last?.Lyrics is LyricResult.Synced { Lines: [{ Text: "Line from C" }] });
        Assert.Equal("spoken", _renderer.Layers.Composition);
        Assert.Equal(LyricColourMode.CustomGradient, _renderer.Last!.Style.ColourMode);
        Assert.Equal(StyleScope.Artist, controller.RememberedStyle?.Scope);

        // Switched off: the chosen style everywhere.
        controller.ApplySettings(controller.Settings with { StyleMemoryEnabled = false });
        Assert.Equal(LyricColourMode.AlbumDrift, _renderer.Last!.Style.ColourMode);
    }

    /// <summary>Windows' own: remembering a style for the song on screen applies it at once, and forgetting it returns.</summary>
    [Fact]
    public async Task RememberingAndForgettingApplyAtOnce()
    {
        _lyrics.Answer("Song A", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Line from A")));
        _source.Add(Playing("Song A", "Artist A"));
        var controller = Controller(new AppSettings { LyricComposition = "editorial" });
        controller.Start();
        await WaitUntil(() => _renderer.Last?.Lyrics is LyricResult.Synced && controller.CurrentQuery is not null);
        Assert.Equal("editorial", _renderer.Layers.Composition);

        var song = controller.CurrentQuery!;
        controller.ApplySettings(StyleMemory.Remember(controller.Settings, new RememberedStyle("cascade", "musicReactive"), song, StyleScope.Artist));
        Assert.Equal("cascade", _renderer.Layers.Composition);
        Assert.Equal(LyricColourMode.MusicReactive, _renderer.Last!.Style.ColourMode);

        controller.ApplySettings(StyleMemory.Forget(controller.Settings, song, StyleScope.Artist));
        Assert.Equal("editorial", _renderer.Layers.Composition);
        Assert.Equal(LyricColourMode.AlbumDrift, _renderer.Last!.Style.ColourMode);
    }
}
