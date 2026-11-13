using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Shell.Player;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// The desktop player's two ways out of the card: the scrub preview (the wallpaper's clock only) and the commands
/// (through the guard, to the silent fake player only).
/// </summary>
public sealed class PlayerTests : IAsyncDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeRenderer _renderer = new();
    private readonly PlaybackGuard _guard = new();
    private AppController? _controller;

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
    }

    private FakeMediaSession Playing(MediaControls controls = MediaControls.Play | MediaControls.Pause | MediaControls.Next
        | MediaControls.Previous | MediaControls.Seek)
    {
        var session = new FakeMediaSession("Spotify.exe", _time);
        session.SetTrack("Song", "Artist").SetPlayback(PlaybackStatus.Playing, controls: controls);
        session.SetTimeline(30, 200);
        _source.Add(session);
        return session;
    }

    private AppController Controller(double globalOffset = 0)
    {
        _controller = new AppController(_source, new FakeLyricLookup(), _renderer, new AppSettings { LyricOffset = globalOffset },
            new SongOffsets(), ReadingServices.Basic, _guard, Logger.Null, null,
            new AppControllerOptions { TimeProvider = _time, TimelineWait = TimeSpan.FromMilliseconds(30), ArtworkRetries = [] });
        _controller.Start();
        return _controller;
    }

    [Fact]
    public async Task TheWallpaperFollowsTheScrubAndThePlayerIsLeftAlone()
    {
        var session = Playing();
        var controller = Controller(globalOffset: 0.5);
        await WaitUntil(() => controller.ReadPlayer().HasSong);
        var clock = _renderer.Clock!;
        Assert.Equal(30.5 + AppController.SyncLead, clock().Seconds, 1e-6);
        Assert.True(clock().Playing);

        // The drag points at 120 s: the wallpaper shows that moment (with the lyric offset), held still.
        controller.SetScrubPreview(120);
        Assert.True(controller.IsPreviewing);
        Assert.Equal((120.5, false), clock());
        // Lines on screen keep following the player itself (a better match waits for the sung line, not the drag).
        Assert.Equal(30.5 + AppController.SyncLead, controller.ReadClock().Seconds, 1e-6);
        controller.SetScrubPreview(121.25);
        Assert.Equal(121.75, clock().Seconds, 1e-9);

        controller.ClearScrubPreview();
        Assert.False(controller.IsPreviewing);
        Assert.Equal(30.5 + AppController.SyncLead, clock().Seconds, 1e-6);
        Assert.Empty(session.Commands); // the preview never reaches the player
    }

    [Fact]
    public async Task WhatThePlayerReadsOfTheSong()
    {
        Playing();
        var controller = Controller();
        await WaitUntil(() => controller.ReadPlayer().Duration > 0);
        var now = controller.ReadPlayer();
        Assert.Equal("Song", now.Title);
        Assert.Equal("Artist", now.Artist);
        Assert.True(now.IsPlaying);
        Assert.Equal(30, now.Position, 1e-6);
        Assert.Equal(200, now.Duration);
        Assert.True(now.Capabilities.CanSeek);
    }

    [Fact]
    public async Task WithNoPlaybackEveryCommandIsRefusedBeforeThePlayer()
    {
        var session = Playing();
        _guard.Block("--demo");
        var commands = new PlayerCommands(() => session, () => PlayerCapabilities.From(session.GetPlaybackInfo(), session.SourceApp),
            _guard, Logger.Null);
        Assert.True(commands.AreOff);
        foreach (var result in new[]
                 {
                     await commands.PlayPauseAsync(playing: true), await commands.SkipAsync(forward: true),
                     await commands.SkipAsync(forward: false), await commands.SeekAsync(42),
                 })
        {
            Assert.Equal(CommandOutcome.Off, result.Outcome);
            Assert.Equal(PlayerCommands.OffNote, result.Note);
        }

        Assert.Empty(session.Commands);
        Assert.Equal(4, _guard.RefusedCount);
    }

    [Fact]
    public async Task CommandsReachOnlyTheFakePlayerAndOnlyWhatItAllows()
    {
        // Silent fake only: its commands record and do nothing.
        var session = Playing(MediaControls.Play | MediaControls.Pause | MediaControls.Seek);
        var commands = new PlayerCommands(() => session, () => PlayerCapabilities.From(session.GetPlaybackInfo(), session.SourceApp),
            _guard, Logger.Null);
        await commands.SeekAsync(41.8);
        await commands.PlayPauseAsync(playing: true);
        var next = await commands.SkipAsync(forward: true);
        Assert.Equal(CommandOutcome.Unsupported, next.Outcome); // this player doesn't allow skipping: nothing sent
        Assert.NotNull(next.Note);
        Assert.Equal(["seek 00:00:41.8000000", "pause"], session.Commands);
    }

    [Fact]
    public async Task ARefusalSaysSoCalmly()
    {
        var session = Playing();
        var commands = new PlayerCommands(() => session, () => PlayerCapabilities.From(session.GetPlaybackInfo(), session.SourceApp),
            _guard, Logger.Null);
        var result = await commands.PlayPauseAsync(playing: false); // the fake answers false
        Assert.Equal(CommandOutcome.Refused, result.Outcome);
        Assert.Contains("didn't take that", result.Note);
        var none = new PlayerCommands(() => null, () => PlayerCapabilities.None, _guard, Logger.Null);
        Assert.Equal(CommandOutcome.Unsupported, (await none.SeekAsync(3)).Outcome);
    }

    [Fact]
    public void TheDebugFlagAndThePlayerSettings()
    {
        var options = ShellOptions.Parse(["--demo", "--player-debug", "Show, light,scrub=0.6"]);
        Assert.Equal(["show", "light", "scrub=0.6"], options.PlayerDebug);
        Assert.Empty(options.Problems);
        Assert.Empty(ShellOptions.Parse(["--demo"]).PlayerDebug);
        var defaults = new AppSettings();
        Assert.True(defaults.ShowControlsOnHover);
        Assert.Equal("remaining", defaults.DesktopPlayerTimeMode);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the controller.");
    }
}
