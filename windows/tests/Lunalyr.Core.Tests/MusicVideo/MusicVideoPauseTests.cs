using Lunalyr.Core.MusicVideo;

namespace Lunalyr.Core.Tests.MusicVideo;

// Ported from MusicVideoPauseTests.swift: why the music video stopped, and a long pause closing YouTube's player. Every seam
// is faked: no web view, no player, no network, no settings. The Mac's frozen-frame tests are not ported: Windows keeps
// no frozen frame for YouTube, because YouTube's API policies don't allow covering the embedded player's interface (a
// paused player stays on screen as YouTube draws it).
public sealed class MusicVideoPauseTests
{
    private sealed class FakeIdleHost : IMusicVideoIdleHost
    {
        public List<string> Events = [];

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
    }

    private static (MusicVideoIdlePlayer Idle, FakeIdleHost Host, Timers Timers) MakeIdle()
    {
        var timers = new Timers();
        var idle = new MusicVideoIdlePlayer(600, (delay, work) =>
        {
            Assert.Equal(600, delay);
            return timers.Add(delay, work);
        });
        var host = new FakeIdleHost();
        idle.Host = host;
        return (idle, host, timers);
    }

    /// <summary>Playing, then the user pauses the song: the loop asks for a pause.</summary>
    private static void UserPause(MusicVideoIdlePlayer idle)
    {
        idle.NoteHold(MusicVideoHold.Live);
        idle.NoteHold(MusicVideoHold.UserPaused);
        idle.Pause();
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

    // Windows' own, in place of testUserPauseTakesThePictureThenPauses (not ported): a pause just pauses. No picture is
    // taken or shown; the paused player stays on screen as YouTube draws it.
    [Fact]
    public void APauseJustPauses()
    {
        var (idle, host, _) = MakeIdle();
        UserPause(idle);
        Assert.Equal(["pause"], host.Events);
        idle.NoteHold(MusicVideoHold.WallpaperPaused);
        idle.Pause();
        Assert.Equal(["pause", "pause"], host.Events);
    }

    // Adapted from MusicVideoPauseTests.swift: testLongPauseClosesThePlayerAndResumeReopensIt (no frozen frame)
    [Fact]
    public void LongPauseClosesThePlayerAndResumeReopensIt()
    {
        var (idle, host, timers) = MakeIdle();
        UserPause(idle);
        host.Events = [];
        timers.Fire(600);
        Assert.Equal(["release"], host.Events);
        Assert.True(idle.Released);
        idle.NoteHold(MusicVideoHold.UserPaused);
        idle.Pause();
        Assert.Equal(["release"], host.Events);
        idle.NoteHold(MusicVideoHold.Live);
        Assert.Equal(["release", "recreate"], host.Events);
        idle.NoteHold(MusicVideoHold.Live);
        Assert.Single(host.Events, e => e == "recreate");
        Assert.False(idle.Released);
    }

    // Adapted from MusicVideoPauseTests.swift: testResumeBeforeTheLongPauseKeepsThePlayer (no frozen frame)
    [Fact]
    public void ResumeBeforeTheLongPauseKeepsThePlayer()
    {
        var (idle, host, timers) = MakeIdle();
        UserPause(idle);
        idle.NoteHold(MusicVideoHold.Live);
        timers.Fire(600);
        Assert.DoesNotContain("release", host.Events);
    }

    // Adapted from MusicVideoPauseTests.swift: testCoveredForALongTimeClosesThePlayerAndShowingReopensIt (no frozen frame)
    [Fact]
    public void CoveredForALongTimeClosesThePlayerAndShowingReopensIt()
    {
        var (idle, host, timers) = MakeIdle();
        idle.NoteHold(MusicVideoHold.Live);
        idle.NoteHold(MusicVideoHold.WallpaperPaused);
        idle.Pause();
        Assert.Equal(["pause"], host.Events);
        idle.NoteHold(MusicVideoHold.WallpaperPaused);
        timers.Fire(600);
        Assert.Equal(["pause", "release"], host.Events);
        idle.NoteHold(MusicVideoHold.WallpaperPaused);
        Assert.Equal("release", host.Events[^1]);
        timers.Fire(600);
        Assert.Single(host.Events, e => e == "release");
        idle.NoteHold(MusicVideoHold.Live);
        Assert.Equal("recreate", host.Events[^1]);
    }

    // Adapted from MusicVideoPauseTests.swift: testCoveredAfterAUserPauseClosesAtTheLongPauseToo (no frozen frame)
    [Fact]
    public void CoveredAfterAUserPauseClosesAtTheLongPauseToo()
    {
        var (idle, host, timers) = MakeIdle();
        UserPause(idle);
        idle.NoteHold(MusicVideoHold.WallpaperPaused);
        timers.Fire(600);
        Assert.Equal("release", host.Events[^1]);
        idle.NoteHold(MusicVideoHold.UserPaused);
        Assert.DoesNotContain("recreate", host.Events);
    }

    // Adapted from MusicVideoPauseTests.swift: testCoveredBrieflyKeepsThePlayer (no frozen frame)
    [Fact]
    public void CoveredBrieflyKeepsThePlayer()
    {
        var (idle, host, timers) = MakeIdle();
        idle.NoteHold(MusicVideoHold.Live);
        idle.NoteHold(MusicVideoHold.WallpaperPaused);
        idle.NoteHold(MusicVideoHold.Live);
        timers.Fire(600);
        Assert.DoesNotContain("release", host.Events);
    }

    // Windows' own: a new video, a failure or a closed player forgets the long pause.
    [Fact]
    public void ResetForgetsTheLongPause()
    {
        var (idle, host, timers) = MakeIdle();
        UserPause(idle);
        idle.Reset();
        timers.Fire(600);
        Assert.DoesNotContain("release", host.Events);
        Assert.False(idle.Released);
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
