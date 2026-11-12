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

/// <summary>What the settings window changes reaches the wallpaper at once, without a restart.</summary>
public sealed class AppControllerSettingsWindowTests : IAsyncDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeLyricLookup _lyrics = new();
    private readonly FakeRenderer _renderer = new();
    private readonly List<FakeMediaSession> _sessions = [];
    private readonly Dictionary<string, double> _saved = [];
    private AppController? _controller;

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
        foreach (var session in _sessions) Assert.Empty(session.Commands);
    }

    private AppController Controller(AppSettings? settings = null)
    {
        var offsets = new SongOffsets(null, saved =>
        {
            _saved.Clear();
            foreach (var (key, value) in saved) _saved[key] = value;
        });
        _controller = new AppController(_source, _lyrics, _renderer, settings ?? new AppSettings(), offsets, ReadingServices.Basic,
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

    private FakeMediaSession Playing(string title)
    {
        var session = new FakeMediaSession("Spotify.exe", _time);
        _sessions.Add(session);
        session.SetTrack(title, "Artist").SetPlayback(PlaybackStatus.Playing);
        session.SetTimeline(30, 200);
        return session;
    }

    [Fact]
    public async Task TheFamilysSlidersMixAndEntrancesReachTheScene()
    {
        _lyrics.Answer("Song A", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "First line")));
        _source.Add(Playing("Song A"));
        var controller = Controller();
        controller.Start();
        await controller.WhenIdleAsync();
        var lookups = _lyrics.Queries.Count;

        var before = _renderer.Scenes.Count;
        controller.ApplySettings(controller.Settings with { EchoStrength = 0.4, ConstellationLinkOpacity = 0 });
        Assert.Equal(before + 1, _renderer.Scenes.Count);
        Assert.Equal((0.4, 0.0), (_renderer.Last!.Style.EchoStrength, _renderer.Last.Style.ConstellationLinkOpacity));
        Assert.IsType<LyricResult.Synced>(_renderer.Last.Lyrics); // the lyrics stay; nothing is looked up again
        Assert.Equal(lookups, _lyrics.Queries.Count);

        controller.ApplySettings(controller.Settings.WithEntranceForControls("pop") with { WordEntranceMix = ["glide"] });
        Assert.Equal("pop", _renderer.Last!.Style.RevealFor("editorial"));
        Assert.Equal(["glide"], _renderer.Last.Style.MixEntrances!);

        // The same settings again hand nothing over.
        var count = _renderer.Scenes.Count;
        controller.ApplySettings(controller.Settings with { WordEntranceMix = ["glide"] });
        Assert.Equal(count, _renderer.Scenes.Count);
    }

    [Fact]
    public async Task ThisSongsOffsetIsReadAndSetForTheSongOnScreen()
    {
        var controller = Controller();
        controller.Start();
        Assert.Null(controller.SongOffset);
        Assert.False(controller.SetSongOffset(1)); // no song: nothing to set

        _lyrics.Answer("Song A", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "First line")));
        _source.Add(Playing("Song A"));
        await WaitUntil(() => controller.SongOffset is not null && _renderer.Last?.Lyrics is LyricResult.Synced);
        Assert.Equal(0, controller.SongOffset);
        var (before, _) = controller.ReadClock();

        Assert.True(controller.SetSongOffset(1.5));
        Assert.Equal(1.5, controller.SongOffset);
        Assert.Single(_saved); // saved through the offsets' store
        var (after, _) = controller.ReadClock();
        Assert.InRange(after - before, 1.4, 1.6); // the clock shows lyrics 1.5 s earlier at once
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the controller.");
    }
}
