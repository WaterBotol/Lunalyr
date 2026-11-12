using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Shell.Demo;
using Lunalyr.Shell.Flyout;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// The flyout's transport and Search YouTube, against the silent fake player, the demo player and a recording launcher
/// only. Each test makes its own <see cref="PlaybackGuard"/>; the process-wide one is never touched.
/// </summary>
public sealed class TrayFlyoutCommandTests
{
    private const MediaControls Full = MediaControls.Play | MediaControls.Pause | MediaControls.Next | MediaControls.Previous;

    private readonly ManualTimeProvider _time = new();

    private (FakeMediaSession Session, PlayerCapabilities Capabilities) Player(MediaControls controls, PlaybackStatus status = PlaybackStatus.Playing)
    {
        var session = new FakeMediaSession("Spotify.exe", _time);
        session.SetTrack("Midnight Rain", "Taylor Swift").SetPlayback(status, controls: controls);
        return (session, PlayerCapabilities.From(session.GetPlaybackInfo(), session.SourceApp));
    }

    [Fact]
    public async Task ABlockedGuardRefusesEveryCommandBeforeThePlayerIsAsked()
    {
        var guard = new PlaybackGuard();
        guard.Block("--no-playback");
        var log = new List<string>();
        var commands = new PlaybackCommands(guard, log.Add);
        var (session, capabilities) = Player(Full);

        foreach (var command in Enum.GetValues<TransportCommand>())
            Assert.Equal(TransportOutcome.Refused, await commands.SendAsync(session, capabilities, command, playing: true));

        Assert.Empty(session.Commands);
        Assert.Equal(3, guard.RefusedCount);
        Assert.All(log, line => Assert.Contains("playback commands are off (--no-playback)", line, StringComparison.Ordinal));
        // Even with nothing playing, a blocked run says so first.
        Assert.Equal(TransportOutcome.Refused, await commands.SendAsync(null, PlayerCapabilities.None, TransportCommand.Next, false));
    }

    [Fact]
    public async Task TheDemoPlayerIsRefusedByTheGuardAndByItself()
    {
        var guard = new PlaybackGuard();
        guard.Block("--demo");
        using var demo = new DemoMediaSessionSource(_time);
        var session = demo.Session;
        var capabilities = PlayerCapabilities.From(session.GetPlaybackInfo(), session.SourceApp);
        var commands = new PlaybackCommands(guard);
        Assert.Equal(TransportOutcome.Refused, await commands.SendAsync(session, capabilities, TransportCommand.PlayPause, playing: true));
        Assert.Equal(1, guard.RefusedCount);
        // The demo takes nothing from other apps either, so an unblocked guard still sends nothing to it.
        Assert.Equal(TransportOutcome.Unsupported, await new PlaybackCommands(new PlaybackGuard()).SendAsync(session, capabilities, TransportCommand.Next, true));
    }

    [Fact]
    public async Task AnAllowedPressReachesTheFakePlayerTheWayItTakesIt()
    {
        var commands = new PlaybackCommands(new PlaybackGuard());

        var (playing, caps) = Player(Full);
        // The fake answers false to everything (it plays nothing), which reads as the player not taking it.
        Assert.Equal(TransportOutcome.Failed, await commands.SendAsync(playing, caps, TransportCommand.PlayPause, playing: true));
        await commands.SendAsync(playing, caps, TransportCommand.Next, playing: true);
        await commands.SendAsync(playing, caps, TransportCommand.Previous, playing: true);
        Assert.Equal(["pause", "next", "previous"], playing.Commands);

        var (paused, pausedCaps) = Player(Full, PlaybackStatus.Paused);
        await commands.SendAsync(paused, pausedCaps, TransportCommand.PlayPause, playing: false);
        Assert.Equal(["play"], paused.Commands);

        // A player that only offers the toggle gets the toggle.
        var (toggleOnly, toggleCaps) = Player(MediaControls.PlayPauseToggle);
        await commands.SendAsync(toggleOnly, toggleCaps, TransportCommand.PlayPause, playing: true);
        Assert.Equal(["toggle"], toggleOnly.Commands);
    }

    [Fact]
    public async Task UnsupportedCommandsAndNoPlayerSendNothing()
    {
        var log = new List<string>();
        var commands = new PlaybackCommands(new PlaybackGuard(), log.Add);
        var (session, caps) = Player(MediaControls.Play | MediaControls.Pause);
        Assert.Equal(TransportOutcome.Unsupported, await commands.SendAsync(session, caps, TransportCommand.Next, playing: true));
        Assert.Equal(TransportOutcome.Unsupported, await commands.SendAsync(session, caps, TransportCommand.Previous, playing: true));
        Assert.Empty(session.Commands);
        Assert.Contains(log, line => line.Contains("doesn't allow skipping", StringComparison.Ordinal));
        Assert.Equal(TransportOutcome.NoPlayer, await commands.SendAsync(null, PlayerCapabilities.None, TransportCommand.PlayPause, playing: false));
    }

    // ---- Search YouTube ----

    private sealed class RecordingLauncher : IUrlLauncher
    {
        public List<Uri> Opened { get; } = [];

        public bool Launch(Uri url)
        {
            Opened.Add(url);
            return true;
        }
    }

    [Fact]
    public void TheSearchAddressCarriesTheArtistAndTitle()
    {
        Assert.Equal("https://www.youtube.com/results?search_query=Taylor%20Swift%20Midnight%20Rain",
            YouTubeSearch.UrlFor(" Midnight Rain ", "Taylor Swift")!.AbsoluteUri);
        Assert.Equal("https://www.youtube.com/results?search_query=%E5%A4%95%E6%9A%AE%E3%82%8C%E3%81%AE%E7%AA%93",
            YouTubeSearch.UrlFor("夕暮れの窓", "")!.AbsoluteUri);
        Assert.Equal("https://www.youtube.com/results?search_query=AC%2FDC%20Rock%20%26%20Roll%20%23",
            YouTubeSearch.UrlFor("Rock & Roll #", "AC/DC")!.AbsoluteUri);
        Assert.Null(YouTubeSearch.UrlFor(" ", "Artist"));
    }

    [Fact]
    public void SearchYouTubeOpensOnlyThroughTheLauncherAndNeverWhilePlaybackIsOff()
    {
        var launcher = new RecordingLauncher();
        Assert.True(YouTubeSearch.Open(launcher, new PlaybackGuard(), "Midnight Rain", "Taylor Swift"));
        Assert.Single(launcher.Opened);

        var blocked = new PlaybackGuard();
        blocked.Block("--demo");
        var log = new List<string>();
        Assert.False(YouTubeSearch.Open(launcher, blocked, "Midnight Rain", "Taylor Swift", log.Add));
        Assert.Single(launcher.Opened);
        Assert.Contains("off for this run (--demo)", Assert.Single(log), StringComparison.Ordinal);

        Assert.False(YouTubeSearch.Open(launcher, new PlaybackGuard(), null, "Taylor Swift"));
        Assert.Single(launcher.Opened);
        Assert.False(YouTubeSearch.Open(RefusingUrlLauncher.Instance, new PlaybackGuard(), "Midnight Rain", "Taylor Swift"));
    }
}
