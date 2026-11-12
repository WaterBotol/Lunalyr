using Lunalyr.Core.MusicVideo;

namespace Lunalyr.Core.Tests.MusicVideo;

// Ported from MusicVideoPauseTests.swift: a paused song holds the music video's frame. Every seam is faked: no web view,
// no player, no network, no settings.
public sealed class MusicVideoPauseTests
{
    private sealed class FakeFreezeHost : IMusicVideoFreezeHost
    {
        public List<string> Events = [];
        public object? Picture = new object();
        public bool DeferCapture;
        public Action<object?>? PendingCapture;

        public void CaptureFrame(Action<object?> done)
        {
            Events.Add("capture");
            if (DeferCapture) PendingCapture = done;
            else done(Picture);
        }

        public void ShowFrozenFrame(object image) => Events.Add("show");
        public void HideFrozenFrame(bool handOver) => Events.Add(handOver ? "handOver" : "hide");
        public void SendPause() => Events.Add("pause");
        public void ReleasePlayer() => Events.Add("release");
        public void RecreatePlayer() => Events.Add("recreate");
    }

    /// <summary>Timers by delay, fired by hand.</summary>
    private sealed class Timers
    {
        private readonly List<(int Id, double Delay, Action Work)> _pending = [];
        private int _next;

        public Action Add(double delay, Action work)
        {
            var id = ++_next;
            _pending.Add((id, delay, work));
            return () => _pending.RemoveAll(p => p.Id == id);
        }

        public void Fire(double delay)
        {
            var due = _pending.Where(p => p.Delay == delay).ToList();
            _pending.RemoveAll(p => p.Delay == delay);
            foreach (var p in due) p.Work();
        }

        public int Count(double delay) => _pending.Count(p => p.Delay == delay);
    }

    private static (MusicVideoFrozenFrame Freeze, FakeFreezeHost Host, Timers Timers) MakeFreeze()
    {
        var timers = new Timers();
        var freeze = new MusicVideoFrozenFrame(600, (delay, work) =>
        {
            Assert.True(delay is 600 or MusicVideoFrozenFrame.SeekHold, delay.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return timers.Add(delay, work);
        });
        var host = new FakeFreezeHost();
        freeze.Host = host;
        return (freeze, host, timers);
    }

    /// <summary>Playing, then the user pauses the song: the loop asks for a pause; YouTube's state 2 hides the player.</summary>
    private static void UserPause(MusicVideoFrozenFrame freeze)
    {
        freeze.NoteHold(MusicVideoHold.Live);
        freeze.NotePlayer(true);
        freeze.NoteHold(MusicVideoHold.UserPaused);
        freeze.Pause();
        freeze.NotePlayer(false);
    }

    // Ported from MusicVideoPauseTests.swift: testHoldFollowsWhyTheVideoStopped
    [Fact]
    public void HoldFollowsWhyTheVideoStopped()
    {
        Assert.Equal(MusicVideoHold.Live, MusicVideoHolds.Of(false, true, 12));
        Assert.Equal(MusicVideoHold.UserPaused, MusicVideoHolds.Of(false, false, 12));
        Assert.Equal(MusicVideoHold.WallpaperPaused, MusicVideoHolds.Of(true, false, 12));
        Assert.Equal(MusicVideoHold.WallpaperPaused, MusicVideoHolds.Of(true, true, 12));
        Assert.Equal(MusicVideoHold.Other, MusicVideoHolds.Of(false, false, null));
        Assert.Equal(MusicVideoHold.Other, MusicVideoHolds.Of(true, true, null));
        Assert.Equal([MusicVideoHold.UserPaused, MusicVideoHold.WallpaperPaused, MusicVideoHold.Outgoing],
            Enum.GetValues<MusicVideoHold>().Where(h => h.KeepsFrame()));
    }

    // Ported from MusicVideoPauseTests.swift: testFrozenFrameShowsForHoldsThatKeepIt
    [Fact]
    public void FrozenFrameShowsForHoldsThatKeepIt()
    {
        Assert.True(MusicVideoFrozenFrame.ShowsFrame(MusicVideoHold.UserPaused, false, true));
        Assert.True(MusicVideoFrozenFrame.ShowsFrame(MusicVideoHold.UserPaused, true, true));
        Assert.True(MusicVideoFrozenFrame.ShowsFrame(MusicVideoHold.WallpaperPaused, false, true));
        Assert.True(MusicVideoFrozenFrame.ShowsFrame(MusicVideoHold.Outgoing, false, true));
        Assert.False(MusicVideoFrozenFrame.ShowsFrame(MusicVideoHold.Other, false, true));
        Assert.True(MusicVideoFrozenFrame.ShowsFrame(MusicVideoHold.Live, false, true));
        Assert.False(MusicVideoFrozenFrame.ShowsFrame(MusicVideoHold.Live, true, true));
        foreach (var hold in Enum.GetValues<MusicVideoHold>()) Assert.False(MusicVideoFrozenFrame.ShowsFrame(hold, false, false));
        Assert.Equal([1], new[] { -1, 0, 1, 2, 3, 5 }.Where(YouTubeEmbed.ShowsVideo));
    }

    private sealed class HoldSurface : IMusicVideoPlaybackSurface
    {
        public double Time = 30;
        public bool Playing = true;
        public readonly List<MusicVideoHold> Holds = [];
        public readonly List<string> Calls = [];
        public MusicVideoCorrectionStyle Style = MusicVideoCorrectionStyle.Seek;
        public double? Duration = 200;
        public double SeekLead => 0;
        public MusicVideoCorrectionStyle CorrectionStyle => Style;
        public void Sample(Action<MusicVideoDrift.Sample?, double?> done) => done(new MusicVideoDrift.Sample(Time, Playing), Duration);

        public void PlayVideo()
        {
            Calls.Add("play");
            Playing = true;
        }

        public void PauseVideo()
        {
            Calls.Add("pause");
            Playing = false;
        }

        public void SeekVideo(double seconds)
        {
            Calls.Add("seek " + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Time = seconds;
        }

        public void SetVideoRate(double rate) => Calls.Add("rate");
        public void NoteHold(MusicVideoHold hold) => Holds.Add(hold);
    }

    // Ported from MusicVideoPauseTests.swift: testSyncLoopTellsTheSurfaceWhy
    [Fact]
    public void SyncLoopTellsTheSurfaceWhy()
    {
        double songTime = 30, clock = 0;
        var songPlaying = true;
        var surface = new HoldSurface();
        var loop = new MusicVideoSyncLoop(() => songTime, () => songPlaying, () => 0, () => clock) { Surface = surface };
        loop.Tick();
        Assert.Equal(MusicVideoHold.Live, surface.Holds[^1]);
        songPlaying = false;
        clock += 1;
        loop.Tick();
        Assert.Equal(MusicVideoHold.UserPaused, surface.Holds[^1]);
        Assert.Equal("pause", surface.Calls[^1]);
        loop.SetSurfacePaused(true);
        Assert.Equal(MusicVideoHold.WallpaperPaused, surface.Holds[^1]);
        loop.Stop();
        loop.SetSurfacePaused(false);
        Assert.Equal(MusicVideoHold.UserPaused, surface.Holds[^1]);
        songTime = 250;
        clock += 1;
        loop.Tick();
        Assert.Equal(MusicVideoHold.Other, surface.Holds[^1]);
        songTime = -3;
        loop.Tick();
        Assert.Equal(MusicVideoHold.Other, surface.Holds[^1]);
        loop.Stop();
    }

    // Ported from MusicVideoPauseTests.swift: testLibraryVideoJustPausesAndStaysVisible
    [Fact]
    public void RatePlayerJustPauses()
    {
        var local = new HoldSurface { Style = MusicVideoCorrectionStyle.Rate };
        var loop = new MusicVideoSyncLoop(() => 30, () => false, () => 0, () => 0) { Surface = local };
        loop.Tick();
        Assert.False(local.Playing);
        loop.Stop();
    }

    // Ported from MusicVideoPauseTests.swift: testUserPauseTakesThePictureThenPauses
    [Fact]
    public void UserPauseTakesThePictureThenPauses()
    {
        var (freeze, host, _) = MakeFreeze();
        UserPause(freeze);
        Assert.Equal(["capture", "show", "pause"], host.Events);
        Assert.True(freeze.FrameShown);
        freeze.NotePlayer(true);
        freeze.Pause();
        Assert.Equal(["capture", "show", "pause", "pause"], host.Events);
    }

    // Ported from MusicVideoPauseTests.swift: testOtherPausesKeepTheFallback
    [Fact]
    public void OtherPausesKeepTheFallback()
    {
        var (outside, outsideHost, _) = MakeFreeze();
        outside.NotePlayer(true);
        outside.NoteHold(MusicVideoHold.Other);
        outside.Pause();
        Assert.Equal(["pause"], outsideHost.Events);
        var (freeze, host, _) = MakeFreeze();
        freeze.NotePlayer(false);
        freeze.NoteHold(MusicVideoHold.UserPaused);
        freeze.Pause();
        Assert.Equal(["pause"], host.Events);
        Assert.False(freeze.FrameShown);
    }

    // Ported from MusicVideoPauseTests.swift: testCoveringKeepsTheFrameAndLeavingTheVideoHidesIt
    [Fact]
    public void CoveringKeepsTheFrameAndLeavingTheVideoHidesIt()
    {
        var (freeze, host, _) = MakeFreeze();
        UserPause(freeze);
        freeze.NoteHold(MusicVideoHold.WallpaperPaused);
        Assert.Equal("pause", host.Events[^1]);
        Assert.True(freeze.FrameShown);
        freeze.NoteHold(MusicVideoHold.Other);
        Assert.Equal("hide", host.Events[^1]);
        freeze.NoteHold(MusicVideoHold.UserPaused);
        Assert.Equal("show", host.Events[^1]);
    }

    // Ported from MusicVideoPauseTests.swift: testPausingTheWallpaperTakesTheFrame
    [Fact]
    public void PausingTheWallpaperTakesTheFrame()
    {
        var (freeze, host, _) = MakeFreeze();
        freeze.NoteHold(MusicVideoHold.Live);
        freeze.NotePlayer(true);
        freeze.NoteHold(MusicVideoHold.WallpaperPaused);
        freeze.Pause();
        freeze.NotePlayer(false);
        Assert.Equal(["capture", "show", "pause"], host.Events);
        Assert.True(freeze.FrameShown);
        freeze.NoteHold(MusicVideoHold.Live);
        Assert.True(freeze.FrameShown);
        freeze.NotePlayer(true);
        Assert.Equal("handOver", host.Events[^1]);
    }

    // Ported from MusicVideoPauseTests.swift: testOutgoingVideoHoldsItsFrameThroughTheSongChange
    [Fact]
    public void OutgoingVideoHoldsItsFrameThroughTheSongChange()
    {
        var (freeze, host, _) = MakeFreeze();
        freeze.NoteHold(MusicVideoHold.Live);
        freeze.NotePlayer(true);
        freeze.NoteHold(MusicVideoHold.Outgoing);
        freeze.Pause();
        freeze.NotePlayer(false);
        Assert.Equal(["capture", "show", "pause"], host.Events);
        Assert.True(freeze.FrameShown);
    }

    // Ported from MusicVideoPauseTests.swift: testResumeHandsOverOnlyOncePlaying
    [Fact]
    public void ResumeHandsOverOnlyOncePlaying()
    {
        var (freeze, host, _) = MakeFreeze();
        UserPause(freeze);
        host.Events = [];
        freeze.NoteHold(MusicVideoHold.Live);
        freeze.NotePlayer(false);
        Assert.Empty(host.Events);
        Assert.True(freeze.FrameShown);
        freeze.NotePlayer(true);
        Assert.Equal(["handOver"], host.Events);
        Assert.Null(freeze.Image);
        host.Events = [];
        freeze.NoteHold(MusicVideoHold.UserPaused);
        freeze.Pause();
        Assert.Equal(["capture", "show", "pause"], host.Events);
    }

    // Ported from MusicVideoPauseTests.swift: testFailedSnapshotFallsBackToTheGradient
    [Fact]
    public void FailedSnapshotFallsBackToTheGradient()
    {
        var (freeze, host, timers) = MakeFreeze();
        host.Picture = null;
        UserPause(freeze);
        Assert.Equal(["capture", "pause"], host.Events);
        Assert.False(freeze.FrameShown);
        timers.Fire(600);
        Assert.Equal("release", host.Events[^1]);
        Assert.False(freeze.FrameShown);
        var black = new byte[4 * 4 * 4];
        Assert.True(MusicVideoFrozenFrame.LooksBlank(black, 4, 4));
        var grey = Enumerable.Repeat((byte)77, 4 * 4 * 4).ToArray();
        Assert.False(MusicVideoFrozenFrame.LooksBlank(grey, 4, 4));
    }

    // Ported from MusicVideoPauseTests.swift: testResumeDuringTheSnapshotDoesNotPause
    [Fact]
    public void ResumeDuringTheSnapshotDoesNotPause()
    {
        var (freeze, host, _) = MakeFreeze();
        host.DeferCapture = true;
        freeze.NotePlayer(true);
        freeze.NoteHold(MusicVideoHold.UserPaused);
        freeze.Pause();
        freeze.Pause();
        Assert.Equal(["capture"], host.Events);
        freeze.NoteHold(MusicVideoHold.Live);
        host.PendingCapture!(new object());
        Assert.Equal(["capture"], host.Events);
        Assert.False(freeze.FrameShown);
    }

    // Ported from MusicVideoPauseTests.swift: testNewVideoDropsTheFrame
    [Fact]
    public void NewVideoDropsTheFrame()
    {
        var (freeze, host, _) = MakeFreeze();
        UserPause(freeze);
        freeze.Reset();
        Assert.Equal("hide", host.Events[^1]);
        Assert.Null(freeze.Image);
        freeze.NoteHold(MusicVideoHold.UserPaused);
        Assert.Equal("hide", host.Events[^1]);
    }

    // Ported from MusicVideoPauseTests.swift: testLongPauseClosesThePlayerAndResumeReopensIt
    [Fact]
    public void LongPauseClosesThePlayerAndResumeReopensIt()
    {
        var (freeze, host, timers) = MakeFreeze();
        UserPause(freeze);
        host.Events = [];
        timers.Fire(600);
        Assert.Equal(["release"], host.Events);
        Assert.True(freeze.FrameShown);
        freeze.NoteHold(MusicVideoHold.UserPaused);
        Assert.Equal(["release"], host.Events);
        freeze.NoteHold(MusicVideoHold.Live);
        Assert.Equal(["release", "recreate"], host.Events);
        Assert.True(freeze.FrameShown);
        freeze.NoteHold(MusicVideoHold.Live);
        Assert.Single(host.Events, e => e == "recreate");
        freeze.NotePlayer(true);
        Assert.Equal("handOver", host.Events[^1]);
    }

    // Ported from MusicVideoPauseTests.swift: testResumeBeforeTheLongPauseKeepsThePlayer
    [Fact]
    public void ResumeBeforeTheLongPauseKeepsThePlayer()
    {
        var (freeze, host, timers) = MakeFreeze();
        UserPause(freeze);
        freeze.NoteHold(MusicVideoHold.Live);
        timers.Fire(600);
        Assert.DoesNotContain("release", host.Events);
    }

    // Ported from MusicVideoPauseTests.swift: testCoveredForALongTimeClosesThePlayerAndShowingReopensIt
    [Fact]
    public void CoveredForALongTimeClosesThePlayerAndShowingReopensIt()
    {
        var (freeze, host, timers) = MakeFreeze();
        freeze.NoteHold(MusicVideoHold.Live);
        freeze.NotePlayer(true);
        freeze.NoteHold(MusicVideoHold.WallpaperPaused);
        freeze.Pause();
        freeze.NotePlayer(false);
        Assert.Equal(["capture", "show", "pause"], host.Events);
        freeze.NoteHold(MusicVideoHold.WallpaperPaused);
        timers.Fire(600);
        Assert.Equal(["capture", "show", "pause", "release"], host.Events);
        Assert.True(freeze.FrameShown);
        freeze.NoteHold(MusicVideoHold.WallpaperPaused);
        Assert.Equal("release", host.Events[^1]);
        timers.Fire(600);
        Assert.Single(host.Events, e => e == "release");
        freeze.NoteHold(MusicVideoHold.Live);
        Assert.Equal("recreate", host.Events[^1]);
    }

    // Ported from MusicVideoPauseTests.swift: testCoveredAfterAUserPauseClosesAtTheLongPauseToo
    [Fact]
    public void CoveredAfterAUserPauseClosesAtTheLongPauseToo()
    {
        var (freeze, host, timers) = MakeFreeze();
        UserPause(freeze);
        freeze.NoteHold(MusicVideoHold.WallpaperPaused);
        timers.Fire(600);
        Assert.Equal("release", host.Events[^1]);
        freeze.NoteHold(MusicVideoHold.UserPaused);
        Assert.True(freeze.FrameShown);
        Assert.DoesNotContain("recreate", host.Events);
    }

    // Ported from MusicVideoPauseTests.swift: testCoveredBrieflyKeepsThePlayer
    [Fact]
    public void CoveredBrieflyKeepsThePlayer()
    {
        var (freeze, host, timers) = MakeFreeze();
        freeze.NoteHold(MusicVideoHold.Live);
        freeze.NoteHold(MusicVideoHold.WallpaperPaused);
        freeze.NoteHold(MusicVideoHold.Live);
        timers.Fire(600);
        Assert.DoesNotContain("release", host.Events);
    }

    // Ported from MusicVideoPauseTests.swift: testASeekThatBuffersShowsThePictureTakenBeforeIt
    [Fact]
    public void ASeekThatBuffersShowsThePictureTakenBeforeIt()
    {
        var (freeze, host, timers) = MakeFreeze();
        freeze.NoteHold(MusicVideoHold.Live);
        freeze.NotePlayer(true);
        freeze.NoteSeek();
        Assert.Equal(["capture"], host.Events);
        Assert.False(freeze.FrameShown);
        freeze.NotePlayer(false);
        Assert.Equal(["capture", "show"], host.Events);
        Assert.Equal(0, timers.Count(MusicVideoFrozenFrame.SeekHold));
        freeze.NoteHold(MusicVideoHold.Live);
        freeze.NotePlayer(true);
        Assert.Equal("handOver", host.Events[^1]);
        Assert.Null(freeze.Image);
    }

    // Ported from MusicVideoPauseTests.swift: testASeekThatLandsAtOnceShowsNothingAndDropsThePicture
    [Fact]
    public void ASeekThatLandsAtOnceShowsNothingAndDropsThePicture()
    {
        var (freeze, host, timers) = MakeFreeze();
        freeze.NoteHold(MusicVideoHold.Live);
        freeze.NotePlayer(true);
        freeze.NoteSeek();
        freeze.NoteHold(MusicVideoHold.Live);
        freeze.NotePlayer(true);
        Assert.NotNull(freeze.Image);
        timers.Fire(MusicVideoFrozenFrame.SeekHold);
        Assert.Null(freeze.Image);
        Assert.Equal(["capture"], host.Events);
        freeze.NoteHold(MusicVideoHold.UserPaused);
        freeze.Pause();
        Assert.Equal(["capture", "capture", "show", "pause"], host.Events);
    }

    // Ported from MusicVideoPauseTests.swift: testAPauseRightAfterASeekTakesAFreshPicture
    [Fact]
    public void APauseRightAfterASeekTakesAFreshPicture()
    {
        var (freeze, host, _) = MakeFreeze();
        freeze.NoteHold(MusicVideoHold.Live);
        freeze.NotePlayer(true);
        freeze.NoteSeek();
        freeze.NoteHold(MusicVideoHold.UserPaused);
        Assert.Null(freeze.Image);
        freeze.Pause();
        Assert.Equal(["capture", "capture", "show", "pause"], host.Events);
    }

    // Ported from MusicVideoPauseTests.swift: testNoSeekPictureUnlessPlaying
    [Fact]
    public void NoSeekPictureUnlessPlaying()
    {
        var (freeze, host, _) = MakeFreeze();
        freeze.NoteHold(MusicVideoHold.Live);
        freeze.NotePlayer(false);
        freeze.NoteSeek();
        freeze.NoteHold(MusicVideoHold.UserPaused);
        freeze.NotePlayer(true);
        freeze.NoteSeek();
        Assert.Empty(host.Events);
    }

    // Ported from MusicVideoPauseTests.swift: testBufferingHidesThePlayerOnlyAfterAGrace
    [Fact]
    public void BufferingHidesThePlayerOnlyAfterAGrace()
    {
        var grace = YouTubeEmbed.BufferingGrace;
        Assert.Equal(grace, YouTubeEmbed.HideDelay(true, MusicVideoHold.Live, false));
        Assert.InRange(grace, 0.1001, 0.5);
        Assert.Equal(0, YouTubeEmbed.HideDelay(true, MusicVideoHold.Live, true));
        Assert.Equal(0, YouTubeEmbed.HideDelay(false, MusicVideoHold.Live, false));
        foreach (var hold in new[] { MusicVideoHold.UserPaused, MusicVideoHold.WallpaperPaused, MusicVideoHold.Outgoing, MusicVideoHold.Other })
            Assert.Equal(0, YouTubeEmbed.HideDelay(true, hold, false));
        var html = YouTubeEmbed.Html("4NRXx6U8ABQ", 0);
        Assert.Contains("if (state === 3) return;", html);
        Assert.DoesNotContain("transition:opacity .5s}", html);
    }

    // Ported from MusicVideoPauseTests.swift: testAVideoFadingOutAfterASongChangeIsHeldNotSought
    [Fact]
    public void AVideoFadingOutAfterASongChangeIsHeldNotSought()
    {
        var current = false;
        var surface = new HoldSurface { Time = 95, Style = MusicVideoCorrectionStyle.Rate };
        var loop = new MusicVideoSyncLoop(() => 1, () => true, () => 0, () => 10) { IsCurrent = () => current, Surface = surface };
        loop.Tick();
        Assert.Equal(["pause"], surface.Calls);
        Assert.Equal(MusicVideoHold.Outgoing, surface.Holds[^1]);
        loop.Tick();
        Assert.Equal(["pause"], surface.Calls);
        current = true;
        loop.Tick();
        Assert.Equal(["seek 1", "play"], surface.Calls.TakeLast(2));
        Assert.Equal(MusicVideoHold.Live, surface.Holds[^1]);
        loop.Stop();
    }

    private sealed class EmptySurface : IMusicVideoPlaybackSurface
    {
        public int Samples;
        public double SeekLead => 0;

        public void Sample(Action<MusicVideoDrift.Sample?, double?> done)
        {
            Samples++;
            done(null, null);
        }

        public void PlayVideo()
        {
        }

        public void PauseVideo()
        {
        }

        public void SeekVideo(double seconds)
        {
        }
    }

    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }

    // Ported from MusicVideoPauseTests.swift: testAHiddenSurfaceWithNothingToSampleStopsChecking
    [Fact]
    public void AHiddenSurfaceWithNothingToSampleStopsChecking()
    {
        var loop = new MusicVideoSyncLoop(() => 30, () => true, () => 0, () => 0, (_, _) => new NoTimer()) { Surface = new EmptySurface() };
        loop.Start();
        Assert.True(loop.IsRunning);
        loop.SetSurfacePaused(true);
        loop.Tick();
        Assert.False(loop.IsRunning);
        loop.SetSurfacePaused(false);
        Assert.True(loop.IsRunning);
        loop.Stop();
    }
}
