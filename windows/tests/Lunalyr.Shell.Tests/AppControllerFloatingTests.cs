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

/// <summary>What the controller tells the renderer about the floating lyrics: the Mac's FloatingLyricsController.update.</summary>
public sealed class AppControllerFloatingTests : IAsyncDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeLyricLookup _lyrics = new();
    private readonly FakeRenderer _renderer = new();
    private readonly List<FakeMediaSession> _sessions = [];
    private AppController? _controller;

    private static readonly AppSettings Floating = new AppSettings().WithLyricsSurface(LyricsSurface.Floating);

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
        foreach (var session in _sessions) Assert.Empty(session.Commands);
    }

    private AppController Controller(AppSettings settings)
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
        _controller = new AppController(_source, _lyrics, _renderer, settings, new SongOffsets(), ReadingServices.Basic,
            new PlaybackGuard(), Logger.Null, null, options);
        return _controller;
    }

    private FakeMediaSession Playing(string title)
    {
        var session = new FakeMediaSession("Spotify.exe", _time);
        _sessions.Add(session);
        session.SetTrack(title, "Yot Club", "Rare Hymns").SetPlayback(PlaybackStatus.Playing).SetTimeline(1, 200);
        return session;
    }

    private async Task<AppController> PlayingWithLyrics(AppSettings settings)
    {
        _lyrics.Answer("Human Nature", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "First line", "Second line")));
        _source.Add(Playing("Human Nature"));
        var controller = Controller(settings);
        controller.Start();
        await controller.WhenIdleAsync();
        return controller;
    }

    [Fact]
    public async Task OnTheDesktopNothingFloats()
    {
        await PlayingWithLyrics(new AppSettings());
        Assert.False(_renderer.Floating.Enabled);
        Assert.False(_renderer.Floating.Shown);
    }

    [Fact]
    public async Task FloatingShowsWhileASongWithLyricsPlays()
    {
        await PlayingWithLyrics(Floating with { FloatingTextSize = 64, FloatingBackdrop = 0.3, FloatingPlacement = "topRight" });
        var floating = _renderer.Floating;
        Assert.True(floating.Enabled);
        Assert.True(floating.Shown);
        Assert.False(floating.Editing);
        Assert.Equal(64, floating.TextSize);
        Assert.Equal(0.3, floating.Backdrop);
        Assert.Equal(FloatingPlacement.TopRight, floating.Placement);
    }

    [Fact]
    public async Task APauseKeepsThemUpForItsGraceThenHides()
    {
        var controller = await PlayingWithLyrics(Floating);
        _sessions[0].SetPlayback(PlaybackStatus.Paused);
        await controller.WhenIdleAsync();
        Assert.True(_renderer.Floating.Shown);
        _time.AdvanceSeconds(FloatingVisibility.PauseGrace + 1);
        controller.ApplySettings(controller.Settings); // what the grace timer does when it fires
        Assert.False(_renderer.Floating.Shown);
        Assert.True(_renderer.Floating.Enabled); // still floating: the wallpaper keeps drawing no lyrics
        _sessions[0].SetPlayback(PlaybackStatus.Playing);
        await controller.WhenIdleAsync();
        Assert.True(_renderer.Floating.Shown);
    }

    [Fact]
    public async Task NoLyricsNoWindow()
    {
        _source.Add(Playing("Unknown Song"));
        var controller = Controller(Floating);
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.True(_renderer.Floating.Enabled);
        Assert.False(_renderer.Floating.Shown);
    }

    [Fact]
    public async Task EditPositionShowsTheWindowWithNothingPlayingAndNeverOutlivesFloating()
    {
        var controller = Controller(Floating);
        controller.Start();
        await controller.WhenIdleAsync();
        Assert.False(_renderer.Floating.Shown);
        controller.SetFloatingEditing(true);
        Assert.True(_renderer.Floating.Shown);
        Assert.True(_renderer.Floating.Editing);
        Assert.True(_renderer.Floating.Placeholder); // "Lyrics appear here"
        controller.ApplySettings(controller.Settings.WithLyricsSurface(LyricsSurface.Desktop));
        Assert.False(controller.FloatingEditing);
        Assert.False(_renderer.Floating.Editing);
        controller.SetFloatingEditing(true); // nothing to place while the lyrics are on the desktop
        Assert.False(controller.FloatingEditing);
    }

    [Fact]
    public async Task PauseWallpaperLeavesTheFloatingLyricsRunning()
    {
        var controller = await PlayingWithLyrics(Floating);
        Assert.True(_renderer.IsRunning);
        controller.ApplySettings(controller.Settings with { WallpaperPaused = true });
        Assert.True(_renderer.IsRunning); // the wallpapers freeze (PauseWallpaper); the floating lyrics go on
        controller.ApplySettings(controller.Settings.WithLyricsSurface(LyricsSurface.Desktop));
        Assert.False(_renderer.IsRunning); // back on the desktop, a paused wallpaper stops the renderer as before
        controller.ApplySettings(controller.Settings with { WallpaperPaused = false });
        Assert.True(_renderer.IsRunning);
    }

    [Fact]
    public async Task FramesPlacedByHandReachTheRenderer()
    {
        // Dragging one display's window makes only that display Custom (the Mac's 0da0eef7).
        var controller = await PlayingWithLyrics(new AppSettings().WithDisplayChoice("A", DisplayLyrics.Floating).WithDisplayChoice("B", DisplayLyrics.Floating));
        controller.SetDisplays([new ConnectedDisplay("A", @"\\.\DISPLAY1"), new ConnectedDisplay("B", @"\\.\DISPLAY2")]);
        var placed = controller.Settings.WithCustomFrame(new FloatingRect(40, 60, 500, 160), "A");
        controller.ApplySettings(placed);
        var windows = _renderer.FloatingWindows!;
        Assert.Equal(FloatingPlacement.Custom, windows.Single(w => w.Display == "A").Placement);
        Assert.Equal(FloatingPlacement.Bottom, windows.Single(w => w.Display == "B").Placement);
        Assert.Equal([40.0, 60, 500, 160], windows[0].Frames["A"]);
    }
}
