using Lunalyr.Core.NowPlaying;
using Lunalyr.Platform.NowPlaying;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Platform.Windows.Tests.NowPlaying;

/// <summary>
/// Following one chosen player (Settings › Music players › Player): the monitor gets only that player's session and never
/// subscribes to or reads the others. Silent fakes only.
/// </summary>
public sealed class FollowedSessionSourceTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();

    public void Dispose() => _source.Dispose();

    [Fact]
    public void AutomaticFollowsWhatWindowsCallsCurrent()
    {
        var spotify = _source.Add(new FakeMediaSession("Spotify.exe", _time));
        var edge = _source.Add(new FakeMediaSession("MSEdge", _time));
        using var followed = new FollowedSessionSource(_source);
        Assert.Equal(PlayerChoice.Automatic, followed.Choice);
        Assert.Same(edge, followed.CurrentSession);
        Assert.Equal([spotify, edge], followed.Sessions);
    }

    [Fact]
    public void AChosenPlayerIsTheOnlyOneFollowedAndTheOthersAreNeverRead()
    {
        var spotify = _source.Add(new FakeMediaSession("Spotify.exe", _time).SetTrack("Spotify song").SetPlayback(PlaybackStatus.Paused));
        var edge = _source.Add(new FakeMediaSession("MSEdge", _time).SetTrack("A video").SetPlayback(PlaybackStatus.Playing));
        using var followed = new FollowedSessionSource(_source, "spotify");
        using var monitor = new NowPlayingMonitor(followed, _time);
        monitor.Start();

        Assert.Same(spotify, monitor.Session); // Windows calls Edge current; Spotify is the one chosen
        Assert.Equal([spotify], followed.Sessions);
        Assert.Equal("Spotify song", monitor.Track?.Title);
        Assert.False(edge.HasSubscribers);
        Assert.Equal(0, edge.Reads);
        Assert.Equal(0, edge.PropertyReads);

        // Windows moves to another player and back: Lunalyr stays with Spotify.
        _source.SetCurrent(edge);
        _source.SetCurrent(spotify);
        Assert.Same(spotify, monitor.Session);
        Assert.Equal(0, edge.Reads);
        Assert.Empty(spotify.Commands);
        Assert.Empty(edge.Commands);
    }

    // Ported from SpotifyPlayerTests.swift: testChoosingOnePlayerNeverMessagesTheOther (Windows' sessions for the Mac's
    // scripted apps, silent fakes whose commands only record that they were called; the Mac's idle prompt, "Play a
    // song in Spotify", has no Windows counterpart).
    [Fact]
    public async Task ChoosingOnePlayerNeverMessagesTheOther()
    {
        var spotify = _source.Add(new FakeMediaSession("Spotify.exe", _time).SetTrack("Blinding Lights").SetPlayback(PlaybackStatus.Paused));
        var music = _source.Add(new FakeMediaSession("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", _time).SetTrack("Song")
            .SetPlayback(PlaybackStatus.Playing));
        using var followed = new FollowedSessionSource(_source);
        using var monitor = new NowPlayingMonitor(followed, _time);
        monitor.Start();
        Assert.Same(music, monitor.Session);

        followed.SetChoice("spotify");
        Assert.Same(spotify, monitor.Session);
        Assert.Equal("Blinding Lights", monitor.Track?.Title);
        var reads = (music.Reads, music.PropertyReads);
        music.SetTrack("Song").SetPlayback(PlaybackStatus.Playing);
        music.RaiseTimelineChanged();
        await monitor.Session!.TryTogglePlayPauseAsync(); // the fake only records it
        Assert.Equal(reads, (music.Reads, music.PropertyReads)); // an unchosen player is never read
        Assert.Empty(music.Commands); // nor sent anything
        Assert.Equal(["toggle"], spotify.Commands);

        followed.SetChoice(PlayerChoice.Automatic);
        Assert.Same(music, monitor.Session);
        Assert.True(music.Reads > reads.Reads);
    }

    [Fact]
    public void AChosenPlayerThatIsntSharingMeansNothingIsFollowedUntilItIs()
    {
        var edge = _source.Add(new FakeMediaSession("MSEdge", _time).SetTrack("A video").SetPlayback(PlaybackStatus.Playing));
        using var followed = new FollowedSessionSource(_source, "appleMusic");
        using var monitor = new NowPlayingMonitor(followed, _time);
        monitor.Start();
        Assert.Null(monitor.Session);
        Assert.Empty(followed.Sessions);
        Assert.Equal(0, edge.Reads);

        var music = _source.Add(new FakeMediaSession("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", _time).SetTrack("Song"), makeCurrent: false);
        Assert.Same(music, monitor.Session);
        _source.Remove(music);
        Assert.Null(monitor.Session);
    }

    [Fact]
    public void TwoSessionsOfTheChosenPlayerFollowWindowsBetweenThem()
    {
        var first = _source.Add(new FakeMediaSession("MSEdge", _time));
        var other = _source.Add(new FakeMediaSession("Spotify.exe", _time));
        var second = _source.Add(new FakeMediaSession("MSEdge", _time), makeCurrent: false);
        using var followed = new FollowedSessionSource(_source, "edge");
        Assert.Same(first, followed.CurrentSession); // Spotify is current: Edge's first session
        _source.SetCurrent(second);
        Assert.Same(second, followed.CurrentSession); // Windows' current one when it is Edge's
        Assert.Equal([first, second], followed.Sessions);
        Assert.DoesNotContain(other, followed.Sessions);
    }

    [Fact]
    public void ANewChoiceMovesTheMonitorAtOnce()
    {
        var spotify = _source.Add(new FakeMediaSession("Spotify.exe", _time).SetTrack("Spotify song"));
        var player = _source.Add(new FakeMediaSession("foobar2000.exe", _time).SetTrack("Local file"));
        using var followed = new FollowedSessionSource(_source);
        using var monitor = new NowPlayingMonitor(followed, _time);
        monitor.Start();
        Assert.Same(player, monitor.Session);

        followed.SetChoice("spotify");
        Assert.Same(spotify, monitor.Session);
        Assert.False(player.HasSubscribers);

        followed.SetChoice(PlayerChoice.Only(player.SourceApp));
        Assert.Equal("app:foobar2000.exe", followed.Choice);
        Assert.Same(player, monitor.Session);
        Assert.False(spotify.HasSubscribers);

        followed.SetChoice("winamp"); // unknown: Automatic
        Assert.Equal(PlayerChoice.Automatic, followed.Choice);
        Assert.Same(player, monitor.Session);
    }

    [Fact]
    public void DisposingLeavesTheInnerSourceToItsOwner()
    {
        var followed = new FollowedSessionSource(_source, "spotify");
        Assert.True(_source.HasSubscribers);
        followed.Dispose();
        Assert.False(_source.HasSubscribers);
    }
}
