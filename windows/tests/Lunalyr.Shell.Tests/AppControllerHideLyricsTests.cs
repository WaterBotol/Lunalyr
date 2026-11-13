using Lunalyr.Core.Floating;
using Lunalyr.Core.Logging;
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
/// Hide lyrics (the Mac's LunalyrExtras.lyricsHidden) as the controller hands it over: the wallpaper's lyrics fade, the
/// floating window hides, and a launch with them hidden starts hidden without a fade. Silent fakes only.
/// </summary>
public sealed class AppControllerHideLyricsTests : IAsyncDisposable
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
        foreach (var session in _sessions) Assert.Empty(session.Commands); // nothing here sends a command
    }

    private async Task<AppController> PlayingWithLyrics(AppSettings settings)
    {
        _lyrics.Answer("Human Nature", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "First line", "Second line")));
        var session = new FakeMediaSession("Spotify.exe", _time);
        _sessions.Add(session);
        session.SetTrack("Human Nature", "Yot Club", "Rare Hymns").SetPlayback(PlaybackStatus.Playing).SetTimeline(1, 200);
        _source.Add(session);
        _controller = new AppController(_source, _lyrics, _renderer, settings, new SongOffsets(), ReadingServices.Basic,
            new PlaybackGuard(), Logger.Null, null, new AppControllerOptions
            {
                TimeProvider = _time,
                TimelineWait = TimeSpan.FromMilliseconds(60),
                TimelinePoll = TimeSpan.FromMilliseconds(10),
                ArtworkRetries = [],
                ArtworkRechecks = [],
            });
        _controller.Start();
        await _controller.WhenIdleAsync();
        return _controller;
    }

    [Fact]
    public async Task HidingFadesTheWallpapersLyricsAndShowingBringsThemBack()
    {
        var controller = await PlayingWithLyrics(new AppSettings());
        Assert.Equal([(true, false)], _renderer.LyricsShown); // start-up: shown, at once
        Assert.True(controller.LyricsShown);

        controller.ApplySettings(controller.Settings with { LyricsHidden = true });
        Assert.Equal((false, true), _renderer.LyricsShown[^1]); // faded out
        Assert.False(controller.LyricsShown);

        controller.ApplySettings(controller.Settings with { LyricOffset = 0.5 }); // another setting: nothing handed over again
        Assert.Equal(2, _renderer.LyricsShown.Count);

        controller.ApplySettings(controller.Settings with { LyricsHidden = false });
        Assert.Equal((true, true), _renderer.LyricsShown[^1]);
    }

    [Fact]
    public async Task ALaunchWithThemHiddenStartsHidden()
    {
        await PlayingWithLyrics(new AppSettings { LyricsHidden = true });
        Assert.Equal([(false, false)], _renderer.LyricsShown);
    }

    [Fact]
    public async Task HiddenLyricsDontFloatEither()
    {
        var floating = new AppSettings().WithLyricsSurface(LyricsSurface.Floating);
        var controller = await PlayingWithLyrics(floating);
        Assert.True(_renderer.Floating.Shown);
        controller.ApplySettings(controller.Settings with { LyricsHidden = true });
        Assert.False(_renderer.Floating.Shown);
        Assert.True(_renderer.Floating.Enabled); // still floating: the wallpaper keeps drawing none
        controller.ApplySettings(controller.Settings with { LyricsHidden = false });
        Assert.True(_renderer.Floating.Shown);
    }

    // The Mac's hiddenByMirroring reaching the wallpapers and the floating window (LunalyrExtras decides it).
    [Fact]
    public async Task AMirroredDisplayHidesTheLyricsEverywhereUntilItEnds()
    {
        var floating = new AppSettings().WithLyricsSurface(LyricsSurface.Floating);
        var controller = await PlayingWithLyrics(floating);
        controller.SetHiddenByMirroring(true);
        Assert.Equal((false, true), _renderer.LyricsShown[^1]); // faded out
        Assert.False(controller.LyricsShown);
        Assert.False(_renderer.Floating.Shown);
        Assert.False(controller.Settings.LyricsHidden); // the user's own Hide is untouched
        controller.SetHiddenByMirroring(true);
        Assert.Equal(2, _renderer.LyricsShown.Count); // no change, nothing handed over

        // The user's own Hide during mirroring, then mirroring ends: still hidden.
        controller.ApplySettings(controller.Settings with { LyricsHidden = true });
        controller.SetHiddenByMirroring(false);
        Assert.False(_renderer.LyricsShown[^1].Shown);
        controller.ApplySettings(controller.Settings with { LyricsHidden = false });
        Assert.Equal((true, true), _renderer.LyricsShown[^1]);
        Assert.True(_renderer.Floating.Shown);
    }
}
