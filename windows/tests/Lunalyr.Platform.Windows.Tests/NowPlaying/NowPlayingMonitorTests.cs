using Lunalyr.Core.NowPlaying;
using Lunalyr.Platform.NowPlaying;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Platform.Windows.Tests.NowPlaying;

public sealed class NowPlayingMonitorTests : IDisposable
{
    private const double Tol = 0.001;

    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly List<FakeMediaSession> _created = [];
    private readonly NowPlayingMonitor _monitor;
    private readonly List<SeekDetectedEventArgs> _seeks = [];
    private int _sessionChanges;
    private int _trackChanges;
    private int _playbackChanges;

    public NowPlayingMonitorTests()
    {
        _monitor = new NowPlayingMonitor(_source, _time);
        _monitor.SessionChanged += (_, _) => _sessionChanges++;
        _monitor.TrackChanged += (_, _) => _trackChanges++;
        _monitor.PlaybackChanged += (_, _) => _playbackChanges++;
        _monitor.Clock.SeekDetected += (_, e) => _seeks.Add(e);
    }

    public void Dispose()
    {
        // The read-only rule: nothing in these tests may ever have sent a command to a player.
        foreach (var session in _created) Assert.Empty(session.Commands);
        _monitor.Dispose();
    }

    private FakeMediaSession NewSession(string appId = "Spotify.exe")
    {
        var session = new FakeMediaSession(appId, _time);
        _created.Add(session);
        return session;
    }

    // A session that is playing `title` at `position` s.
    private FakeMediaSession Playing(string title, double position, string appId = "Spotify.exe", double? duration = 240)
    {
        var session = NewSession(appId);
        session.SetTrack(title).SetPlayback(PlaybackStatus.Playing).SetTimeline(position, duration);
        return session;
    }

    private double Sec => _monitor.Clock.Position.TotalSeconds;

    [Fact]
    public void StartWithNothingPlaying()
    {
        _monitor.Start();
        Assert.Null(_monitor.Session);
        Assert.Null(_monitor.Track);
        Assert.Same(PlayerCapabilities.None, _monitor.Capabilities);
        Assert.Equal(PlaybackStatus.Closed, _monitor.PlaybackInfo.Status);
        Assert.Equal(TimeSpan.Zero, _monitor.Clock.Position);
        Assert.Equal(0, _sessionChanges);
    }

    [Fact]
    public void StartAttachesToTheCurrentSession()
    {
        var spotify = _source.Add(Playing("Lemon", 30));
        _monitor.Start();

        Assert.Same(spotify, _monitor.Session);
        Assert.Equal("Lemon", _monitor.Track?.Title);
        Assert.Equal(MediaSourceKind.Spotify, _monitor.Session!.SourceApp.Kind);
        Assert.True(_monitor.Capabilities.CanSeek);
        Assert.False(_monitor.Capabilities.CanShuffle);
        Assert.Equal(30, Sec, Tol);
        _time.AdvanceSeconds(2);
        Assert.Equal(32, Sec, Tol);
        Assert.Equal(1, _sessionChanges);
        Assert.Equal(1, _trackChanges);
        Assert.True(spotify.HasSubscribers);
    }

    [Fact]
    public void AttachingMidSongToASeekOnlyPlayerUsesTheOldTimestamp()
    {
        var edge = NewSession("MSEdge");
        edge.SetTrack("Song").SetPlayback(PlaybackStatus.Playing).SetTimeline(20, ageSeconds: 95);
        _source.Add(edge);
        _monitor.Start();
        Assert.Equal(115, Sec, Tol);
    }

    [Fact]
    public void TimelineEventsMoveTheClockAndSeeksAreReported()
    {
        var s = _source.Add(Playing("Lemon", 30));
        _monitor.Start();
        _time.AdvanceSeconds(5);
        s.SetTimeline(120);
        Assert.Equal(120, Sec, Tol);
        var seek = Assert.Single(_seeks);
        Assert.Equal(35, seek.From.TotalSeconds, Tol);
    }

    [Fact]
    public void RepeatedTimelineEventsWithoutChangesAreHarmless()
    {
        var s = _source.Add(Playing("Lemon", 30));
        _monitor.Start();
        _time.AdvanceSeconds(3);
        s.RaiseTimelineChanged();
        s.RaiseTimelineChanged();
        Assert.Equal(33, Sec, Tol);
        Assert.Empty(_seeks);
    }

    [Fact]
    public void PlaybackEventsPauseTheClockAndUpdateCapabilities()
    {
        var s = _source.Add(Playing("Lemon", 30));
        s.SetPlayback(PlaybackStatus.Playing, controls: MediaControls.Pause);
        _monitor.Start();
        Assert.True(_monitor.Capabilities.CanPlayPause);
        var before = _playbackChanges;

        _time.AdvanceSeconds(4);
        s.SetPlayback(PlaybackStatus.Paused, controls: MediaControls.Pause);
        Assert.Equal(before + 1, _playbackChanges);
        Assert.False(_monitor.Clock.IsPlaying);
        Assert.Equal(34, Sec, Tol);
        _time.AdvanceSeconds(10);
        Assert.Equal(34, Sec, Tol);
        Assert.False(_monitor.Capabilities.CanPlayPause); // Paused, and the player only accepts Pause.
        Assert.Contains("starting playback", _monitor.Capabilities[PlayerControl.PlayPause].Reason);
    }

    [Fact]
    public void PlaybackRateComesFromThePlayer()
    {
        var s = _source.Add(Playing("Lemon", 30));
        _monitor.Start();
        s.SetPlayback(PlaybackStatus.Playing, rate: 1.5);
        _time.AdvanceSeconds(2);
        Assert.Equal(33, Sec, Tol);
    }

    [Fact]
    public void TrackChangeWithMetadataFirstResetsTheClock()
    {
        var s = _source.Add(Playing("Lemon", 200));
        _monitor.Start();
        _time.AdvanceSeconds(1);

        s.SetTrack("Flamingo");
        Assert.Equal(2, _trackChanges);
        Assert.Equal("Flamingo", _monitor.Track?.Title);
        Assert.Equal(0, Sec, Tol); // The old track's timeline was not reused.
        _time.AdvanceMilliseconds(300);
        s.SetTimeline(0.3, duration: 180);
        Assert.Equal(0.3, Sec, Tol);
        Assert.Equal(180, _monitor.Clock.Read().Duration!.Value.TotalSeconds, Tol);
        Assert.Empty(_seeks);
    }

    [Fact]
    public void TrackChangeWithTimelineFirstKeepsTheNewTimeline()
    {
        var s = _source.Add(Playing("Lemon", 200));
        _monitor.Start();
        _time.AdvanceSeconds(1);

        s.SetTimeline(0.05, duration: 180);
        _time.AdvanceMilliseconds(200);
        s.SetTrack("Flamingo");
        Assert.Equal(0.25, Sec, Tol);
        Assert.Empty(_seeks);
    }

    [Fact]
    public void MetadataRefreshForTheSameTrackDoesNotReset()
    {
        var s = _source.Add(Playing("Lemon", 30));
        _monitor.Start();
        _time.AdvanceSeconds(5);
        s.SetTrack("Lemon"); // E.g. the artwork arrived.
        Assert.Equal(1, _trackChanges);
        Assert.Equal(35, Sec, Tol);
    }

    [Fact]
    public void EmptyTitleBetweenTracksIsIgnored()
    {
        var s = _source.Add(Playing("Lemon", 30));
        _monitor.Start();
        _time.AdvanceSeconds(5);
        s.SetTrack("");
        Assert.Equal("Lemon", _monitor.Track?.Title);
        Assert.Equal(35, Sec, Tol);
        Assert.Equal(1, _trackChanges);
    }

    [Fact]
    public void OutOfOrderMetadataReadsAreDropped()
    {
        var s = Playing("Lemon", 30);
        s.HoldPropertyReads = true;
        _source.Add(s);
        _monitor.Start();
        s.ReleaseProperties(0, new MediaTrackInfo("Lemon", "Artist", "Album", "Artist"));

        s.SetTrack("Flamingo");   // Read #1 starts...
        s.SetTrack("Kick Back");  // ...read #2 starts.
        Assert.Equal(2, s.PendingPropertyReads);
        s.ReleaseProperties(1, new MediaTrackInfo("Kick Back", "Artist", "Album", "Artist"));
        s.ReleaseProperties(0, new MediaTrackInfo("Flamingo", "Artist", "Album", "Artist")); // Stale: dropped.
        Assert.Equal("Kick Back", _monitor.Track?.Title);
        Assert.Equal(2, _trackChanges);
    }

    [Fact]
    public void SeekOnlyPlayerKeepsExtrapolatingThroughSilence()
    {
        var chrome = NewSession("Chrome");
        chrome.SetTrack("Song").SetPlayback(PlaybackStatus.Playing).SetTimeline(10, duration: null);
        _source.Add(chrome);
        _monitor.Start();
        for (var i = 0; i < 60; i++) _time.AdvanceSeconds(1);
        var r = _monitor.Clock.Read();
        Assert.Equal(TimelineQuality.SeekOnly, r.Quality);
        Assert.True(r.IsPlaying);
        Assert.Equal(70, r.Position.TotalSeconds, Tol);
        Assert.True(r.Confidence >= 0.85);
    }

    // ---- Session switching ----------------------------------------------------------------------------------------

    [Fact]
    public void SwitchingSessionsFollowsTheNewPlayerAndForgetsTheOld()
    {
        var chrome = NewSession("Chrome");
        chrome.SetTrack("Video song").SetPlayback(PlaybackStatus.Playing).SetTimeline(10);
        _source.Add(chrome);
        _monitor.Start();
        _time.AdvanceSeconds(10);
        Assert.Equal(TimelineQuality.SeekOnly, _monitor.Clock.Quality);

        var spotify = _source.Add(Playing("Lemon", 60));
        Assert.Same(spotify, _monitor.Session);
        Assert.Equal(2, _sessionChanges);
        Assert.Equal("Lemon", _monitor.Track?.Title);
        Assert.Equal(60, Sec, Tol);
        Assert.Equal(TimelineQuality.Unknown, _monitor.Clock.Quality); // Learned per player.
        Assert.Equal(MediaSourceKind.Spotify, _monitor.Capabilities.App?.Kind);
        Assert.Empty(_seeks); // A switch is not a seek.

        Assert.False(chrome.HasSubscribers);
        chrome.SetTimeline(5);                      // The old player keeps talking...
        chrome.SetPlayback(PlaybackStatus.Paused);
        chrome.SetTrack("Another video");
        _time.AdvanceSeconds(1);
        Assert.Equal(61, Sec, Tol);                // ...and is ignored.
        Assert.True(_monitor.Clock.IsPlaying);
        Assert.Equal("Lemon", _monitor.Track?.Title);
    }

    [Fact]
    public void EventsFromTheOldSessionRaisedAfterTheSwitchAreIgnored()
    {
        var a = _source.Add(Playing("A", 10));
        _monitor.Start();
        var b = _source.Add(Playing("B", 100));
        Assert.False(a.HasSubscribers);

        a.SetTimeline(50);
        a.RaiseInFlightTimelineChanged(); // Reaches the monitor's handler although it has unsubscribed.
        Assert.Equal(100, Sec, Tol);
        Assert.Same(b, _monitor.Session);
        Assert.Empty(_seeks);
    }

    [Fact]
    public void RemovingTheCurrentSessionFallsBackOrClears()
    {
        var a = _source.Add(Playing("A", 10, appId: "Spotify.exe"));
        var b = _source.Add(Playing("B", 100, appId: "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"));
        _monitor.Start();
        Assert.Same(b, _monitor.Session);

        _source.Remove(b);
        Assert.Same(a, _monitor.Session);
        Assert.Equal("A", _monitor.Track?.Title);
        Assert.Equal(10, Sec, Tol);

        _source.Remove(a);
        Assert.Null(_monitor.Session);
        Assert.Null(_monitor.Track);
        Assert.Same(PlayerCapabilities.None, _monitor.Capabilities);
        Assert.False(_monitor.Clock.IsPlaying);
        Assert.Equal(TimeSpan.Zero, _monitor.Clock.Position);
    }

    [Fact]
    public void SwitchingBackStartsOver()
    {
        var a = _source.Add(Playing("A", 10));
        _monitor.Start();
        _time.AdvanceSeconds(5);
        var b = _source.Add(Playing("B", 100, appId: "MSEdge"));
        _time.AdvanceSeconds(5);
        _source.SetCurrent(a);
        // A's timeline is still the 10 s snapshot from 10 s ago, and A has been playing all along.
        Assert.Equal(20, Sec, Tol);
        Assert.Equal(3, _sessionChanges);
        Assert.False(b.HasSubscribers);
    }

    [Fact]
    public void SessionsChangedWithoutACurrentChangeKeepsTheSession()
    {
        var a = _source.Add(Playing("A", 10));
        _monitor.Start();
        _source.Add(Playing("B", 100, appId: "MSEdge"), makeCurrent: false);
        Assert.Same(a, _monitor.Session);
        Assert.Equal(1, _sessionChanges);
    }

    [Fact]
    public void DisposeUnsubscribesEverything()
    {
        var a = _source.Add(Playing("A", 10));
        _monitor.Start();
        _monitor.Dispose();
        Assert.False(a.HasSubscribers);
        Assert.False(_source.HasSubscribers);
        a.SetTimeline(100); // No effect, no exception.
    }

    [Fact]
    public async Task FakeThumbnailIsReadable()
    {
        var a = NewSession();
        a.Thumbnail = [1, 2, 3];
        await using var stream = await a.OpenThumbnailAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(stream);
        Assert.Equal(3, stream.Length);
        Assert.Null(await a.OpenThumbnailAsync(maxBytes: 2, cancellationToken: TestContext.Current.CancellationToken));
    }
}
