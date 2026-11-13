using Lunalyr.Core.MusicVideo;
using Lunalyr.Core.Player;
using static Lunalyr.Core.Tests.MusicVideo.Sources;

namespace Lunalyr.Core.Tests.MusicVideo;

// Ported from MusicVideoAlignTests.swift and MusicVideoCorrectionTests.swift: lining the video up by dragging its own
// bar, and the drift correction. Every seam is faked: no network, no player, no sound, no real settings.
public sealed class MusicVideoAlignTests
{
    private readonly MusicVideoWorld _world = new();
    private MusicVideoSong Song => _world.Song;
    private const string VideoId = "dQw4w9WgXcQ";
    private static MusicVideoSource Source => YouTube(VideoId);
    private const double Width = ScrubMath.BarWidth;

    private sealed class Clock
    {
        public double Song = 60;
        public bool Playing = true;
        public double Uptime = 100;
    }

    private async Task<(MusicVideoController Controller, MusicVideoOffsets Offsets)> Showing(Clock clock,
        IReadOnlyList<MusicVideoLyricLine>? lyrics = null)
    {
        var resolver = _world.Resolver();
        resolver.Links.Set(VideoId, Song.Key);
        var offsets = new MusicVideoOffsets(_world.Storage);
        var controller = new MusicVideoController(resolver, offsets, () => (clock.Song, clock.Playing), _ => lyrics, () => clock.Uptime)
        {
            ReduceMotion = () => false,
        };
        controller.Update(_world.Track, enabled: true, online: false);
        await controller.SettleAsync();
        controller.NoteVideoDuration(215, Source);
        controller.NoteVideoSample(clock.Song + 15, true, Source);
        return (controller, offsets);
    }

    // Ported from MusicVideoAlignTests.swift: testAlignNeedsAPlayingSeekableVideo
    [Fact]
    public void AlignNeedsAPlayingSeekableVideo()
    {
        Assert.Equal(MusicVideoAlignAvailability.NoVideo, MusicVideoAlignAvailability.Of(false, false, true, true).Reason);
        Assert.Equal(MusicVideoAlignAvailability.ReduceMotion, MusicVideoAlignAvailability.Of(true, true, true, true).Reason);
        Assert.Equal(MusicVideoAlignAvailability.NotPlaying, MusicVideoAlignAvailability.Of(true, false, false, true).Reason);
        Assert.Equal(MusicVideoAlignAvailability.NotResponding, MusicVideoAlignAvailability.Of(true, false, true, false).Reason);
        Assert.True(MusicVideoAlignAvailability.Of(true, false, true, true).IsAvailable);
        Assert.Null(MusicVideoAlignAvailability.Available.Reason);
    }

    // Ported from MusicVideoAlignTests.swift: testTheControllerSaysWhyAlignCantStart
    [Fact]
    public async Task TheControllerSaysWhyAlignCantStart()
    {
        var clock = new Clock();
        var (controller, _) = await Showing(clock);
        clock.Playing = false;
        Assert.Equal(MusicVideoAlignAvailability.NotPlaying, controller.AlignAvailability.Reason);
        Assert.False(controller.BeginAlign());
        Assert.False(controller.IsAligning);
        clock.Playing = true;
        clock.Uptime += MusicVideoController.RespondingWithin + 1;
        Assert.Equal(MusicVideoAlignAvailability.NotResponding, controller.AlignAvailability.Reason);
        controller.NoteVideoSample(76, true, Source);
        controller.ReduceMotion = () => true;
        Assert.Equal(MusicVideoAlignAvailability.ReduceMotion, controller.AlignAvailability.Reason);
        controller.ReduceMotion = () => false;
        Assert.True(controller.AlignAvailability.IsAvailable);
        controller.Update(null, enabled: true, online: false);
        Assert.Equal(MusicVideoAlignAvailability.NoVideo, controller.AlignAvailability.Reason);
    }

    // Ported from MusicVideoAlignTests.swift: testTheBarDrawsTheSongInsideTheVideo
    [Fact]
    public void TheBarDrawsTheSongInsideTheVideo()
    {
        Assert.Equal(new MusicVideoAlignBar.Layout(40, 340, 100), MusicVideoAlignBar.LayoutOf(200, 150, 20, 30, 400));
        Assert.Equal(new MusicVideoAlignBar.Layout(0, 100, 0), MusicVideoAlignBar.LayoutOf(100, 150, -10, 5, 100));
        Assert.Equal(215, MusicVideoAlignBar.VideoLength(215, 200, 15));
        Assert.Equal(215, MusicVideoAlignBar.VideoLength(null, 200, 15));
        Assert.Equal(200, MusicVideoAlignBar.VideoLength(double.NaN, 200, -5));
        Assert.Equal(0.5, MusicVideoAlignBar.SecondsPerPoint(228, 456));
        Assert.Equal("Video +3.4 s", MusicVideoAlignBar.Title(3.44));
        Assert.Equal("Video −0.5 s", MusicVideoAlignBar.Title(-0.5));
        Assert.Equal("The video runs 3.4 s ahead of the song", MusicVideoAlignBar.Spoken(3.44));
        Assert.Equal("The video runs 2.0 s behind the song", MusicVideoAlignBar.Spoken(-2));
        Assert.Equal("The video runs with the song", MusicVideoAlignBar.Spoken(0.01));
    }

    // Ported from MusicVideoAlignTests.swift: testDragBoundsKeepTheKnobInsideTheVideo
    [Fact]
    public void DragBoundsKeepTheKnobInsideTheVideo()
    {
        var bounds = MusicVideoAlignBar.Bounds(215, 60, 15);
        Assert.Equal(-60, bounds.Lower);
        Assert.Equal(215 - MusicVideoAlignBar.EndGuard - 60, bounds.Upper, 9);
        Assert.Equal(-30, MusicVideoAlignBar.Bounds(215, 10, -30).Lower);
    }

    // Ported from MusicVideoAlignTests.swift: testTheDragMovesTheOffsetByTheHandsTravelWithFineSteps (Alt for ⌥)
    [Fact]
    public void TheDragMovesTheOffsetByTheHandsTravelWithFineSteps()
    {
        var drag = new MusicVideoAlignDrag(100, 0, 104, 10, 0.5, (-50, 50));
        Assert.Equal(10, drag.Offset);
        Assert.False(drag.Changed);
        drag.Move(110, 0);
        Assert.Equal(15, drag.Offset);
        Assert.Null(drag.RateLabel);
        drag.Move(110, 130);
        Assert.Equal("Fine", drag.RateLabel);
        Assert.Equal(15, drag.Offset);
        drag.Move(112, 130);
        Assert.Equal(15.1, drag.Offset, 9);
        drag.Move(112, 0, fine: true);
        drag.Move(114, 0, fine: true);
        Assert.Equal(15.2, drag.Offset, 9);
        Assert.True(drag.Changed);
        Assert.Equal(10, drag.OffsetAtPress);
    }

    // Ported from MusicVideoAlignTests.swift: testAPressAwayFromTheKnobBringsItThere
    [Fact]
    public void APressAwayFromTheKnobBringsItThere()
    {
        Assert.Equal(60, new MusicVideoAlignDrag(200, 0, 100, 10, 0.5, (-50, 100)).Offset);
        Assert.Equal(100, new MusicVideoAlignDrag(400, 0, 100, 10, 0.5, (-50, 100)).Offset);
    }

    // Ported from MusicVideoAlignTests.swift: testTheEndsStopTheDragAndAreFeltOnce
    [Fact]
    public void TheEndsStopTheDragAndAreFeltOnce()
    {
        var drag = new MusicVideoAlignDrag(100, 0, 100, 0, 1, (-5, 5));
        Assert.False(drag.Move(103, 0));
        Assert.True(drag.Move(110, 0));
        Assert.Equal(5, drag.Offset);
        Assert.False(drag.Move(120, 0));
        Assert.Equal(5, drag.Offset);
        drag.Move(118, 0);
        Assert.Equal(3, drag.Offset);
    }

    // Ported from MusicVideoAlignTests.swift: testTheDragSeeksAsTheOffsetMovesAtEachPlayersPace
    [Fact]
    public void TheDragSeeksAsTheOffsetMovesAtEachPlayersPace()
    {
        var sample = new MusicVideoDrift.Sample(75, true);
        var youTube = new MusicVideoAlignSeeker(MusicVideoCorrectionStyle.LedSeek, 15);
        Assert.Equal(MusicVideoAction.None, youTube.Decide(15, 75, sample, true, 10, 0.3));
        Assert.Equal(MusicVideoAction.Seek(77.3, true), youTube.Decide(17, 77, sample, true, 10, 0.3));
        Assert.Equal(MusicVideoAction.None, youTube.Decide(18, 78, sample, true, 10.2, 0.3));
        var later = youTube.Decide(18, 78.2, sample, true, 10.4, 0.3);
        Assert.True(later.IsSeek);
        Assert.Equal(78.5, later.Value, 9);
        Assert.Equal(MusicVideoAction.None, youTube.Decide(18.01, 78.3, sample, true, 11, 0.3));
        Assert.Equal(MusicVideoAction.None, youTube.Decide(18, 78.4, new MusicVideoDrift.Sample(60, true), true, 20, 0.3));

        var library = new MusicVideoAlignSeeker(MusicVideoCorrectionStyle.Rate, 15);
        Assert.Equal(75.55, library.Decide(15.5, 75.5, sample, true, 10, 0.05).Value, 9);
        Assert.Equal(76.05, library.Decide(16, 76, sample, true, 10.1, 0.05).Value, 9);
        Assert.Equal(MusicVideoAction.Play, library.Decide(16, 76, new MusicVideoDrift.Sample(76, false), true, 10.2, 0.05));
        Assert.Equal(MusicVideoAction.Pause, library.Decide(16, null, sample, true, 10.3, 0.05));
    }

    private sealed class RateSurface(double time, MusicVideoCorrectionStyle style = MusicVideoCorrectionStyle.Rate, double? duration = 215)
        : IMusicVideoPlaybackSurface
    {
        public double Time = time;
        public bool Playing = true;
        public readonly List<double> Seeks = [];
        public readonly List<double> Rates = [];
        public double SeekLead => 0.05;
        public MusicVideoCorrectionStyle CorrectionStyle => style;
        public void Sample(Action<MusicVideoDrift.Sample?, double?> done) => done(new MusicVideoDrift.Sample(Time, Playing), duration);
        public void PlayVideo() => Playing = true;
        public void PauseVideo() => Playing = false;

        public void SeekVideo(double seconds)
        {
            Seeks.Add(seconds);
            Time = seconds;
        }

        public void SetVideoRate(double rate) => Rates.Add(rate);
    }

    // Ported from MusicVideoAlignTests.swift: testTheSyncLoopSeeksWithTheHandThenCorrectionResumes
    [Fact]
    public void TheSyncLoopSeeksWithTheHandThenCorrectionResumes()
    {
        var surface = new RateSurface(75);
        double offset = 15, now = 100;
        var dragging = false;
        var loop = new MusicVideoSyncLoop(() => 60, () => true, () => offset, () => now) { AlignDragging = () => dragging, Surface = surface };
        loop.Tick();
        Assert.Empty(surface.Seeks);
        dragging = true;
        offset = 17;
        now += 0.07;
        loop.Tick();
        Assert.Equal([77.05], surface.Seeks);
        Assert.NotNull(loop.Seeker);
        surface.Time = 74;
        now += 0.07;
        loop.Tick();
        Assert.Single(surface.Seeks);
        Assert.All(surface.Rates, r => Assert.Equal(1, r));
        dragging = false;
        surface.Time = 77.1;
        now += 0.07;
        loop.Tick();
        Assert.Null(loop.Seeker);
        Assert.Single(surface.Seeks);
        surface.Time = 79;
        now += MusicVideoDrift.SeekSettle;
        loop.Tick();
        Assert.Equal(77.05, surface.Seeks[^1], 9);
    }

    private sealed class Timers
    {
        public readonly List<double> Scheduled = [];

        public IDisposable Schedule(double interval, Action _)
        {
            Scheduled.Add(interval);
            return new Nothing();
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    // Ported from MusicVideoAlignTests.swift: testTheLoopChecksQuicklyOnlyWhileTheBarIsDragged
    [Fact]
    public void TheLoopChecksQuicklyOnlyWhileTheBarIsDragged()
    {
        var timers = new Timers();
        var dragging = false;
        var loop = new MusicVideoSyncLoop(() => 0, () => true, () => 0, () => 0, timers.Schedule)
        {
            AlignDragging = () => dragging, Surface = new RateSurface(0, MusicVideoCorrectionStyle.Seek, null),
        };
        loop.Start();
        Assert.Equal(MusicVideoSyncLoop.Interval, loop.CheckInterval);
        dragging = true;
        loop.Tick();
        Assert.Equal(MusicVideoSyncLoop.DragInterval, loop.CheckInterval);
        dragging = false;
        loop.Tick();
        Assert.Equal(MusicVideoSyncLoop.Interval, loop.CheckInterval);
        loop.Stop();
    }

    // Ported from MusicVideoAlignTests.swift: testAlignKeys (Windows: Alt for ⌥, Ctrl for ⌘)
    [Fact]
    public void AlignKeys()
    {
        static MusicVideoAlignKey? Key(ushort code, bool shift = false, bool alt = false, bool ctrl = false, bool editing = false) =>
            MusicVideoAlignKey.Action(code, shift, alt, ctrl, editing);
        Assert.Equal(MusicVideoAlignKey.Nudge(-0.1), Key(PlayerKeyMap.KeyCode.Left));
        Assert.Equal(MusicVideoAlignKey.Nudge(0.1), Key(PlayerKeyMap.KeyCode.Right));
        Assert.Equal(MusicVideoAlignKey.Nudge(-1), Key(PlayerKeyMap.KeyCode.Left, shift: true));
        Assert.Equal(MusicVideoAlignKey.Nudge(1), Key(PlayerKeyMap.KeyCode.Right, shift: true));
        Assert.Equal(MusicVideoAlignKey.Done, Key(PlayerKeyMap.KeyCode.Return));
        Assert.Equal(MusicVideoAlignKey.Cancel, Key(PlayerKeyMap.KeyCode.Escape));
        Assert.Null(Key(PlayerKeyMap.KeyCode.Right, alt: true));
        Assert.Null(Key(PlayerKeyMap.KeyCode.Right, ctrl: true));
        Assert.Null(Key(PlayerKeyMap.KeyCode.Space));
        Assert.Null(Key(PlayerKeyMap.KeyCode.Left, editing: true));
    }

    // Ported from MusicVideoAlignTests.swift: testDraggingTheVideosBarKeepsTheOffsetForThatVideo
    [Fact]
    public async Task DraggingTheVideosBarKeepsTheOffsetForThatVideo()
    {
        var clock = new Clock();
        var (controller, offsets) = await Showing(clock);
        Assert.Equal(15, controller.CurrentOffset);
        Assert.Equal(MusicVideoOffsetOrigin.Estimated, controller.OffsetOrigin);
        Assert.True(controller.BeginAlign());
        Assert.True(controller.IsAligning);
        var display = controller.AlignDisplay()!;
        Assert.Equal(215, display.VideoLength);
        Assert.Equal(75, display.VideoTime);
        Assert.Equal("Video +15.0 s", display.Title);
        Assert.Equal("Drag until the picture matches the music", display.Detail);
        var perPoint = 215 / Width;
        var knob = display.Layout(Width).Knob;

        controller.AlignMouseDown(knob, 0, Width);
        Assert.True(controller.IsAlignDragging);
        controller.AlignMouseDragged(knob + 10, 0);
        Assert.Equal(15 + 10 * perPoint, controller.CurrentOffset, 9);
        Assert.Equal(MusicVideoOffsetOrigin.Estimated, controller.OffsetOrigin);
        Assert.False(offsets.HasManual(Song.Key));
        controller.AlignMouseDragged(knob + 10, 130);
        controller.AlignMouseDragged(knob + 20, 130);
        Assert.Equal("Fine", controller.AlignDisplay()?.RateLabel);
        Assert.True(controller.AlignDisplay()?.Dragging);
        clock.Song = 61.5;
        var expected = 15 + 10 * perPoint + 1 * perPoint;
        Assert.Equal(expected, controller.CurrentOffset, 9);
        controller.AlignMouseUp(knob + 20, 130);
        Assert.False(controller.IsAlignDragging);
        Assert.Equal(Math.Round(expected * 100, MidpointRounding.AwayFromZero) / 100, controller.CurrentOffset, 9);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, controller.OffsetOrigin);
        Assert.True(offsets.HasManual(Song.Key, "yt:" + VideoId));
        Assert.False(offsets.HasManual(Song.Key, "yt:BBBBBBBBBBB"));
        Assert.Equal("Kept for this video", controller.AlignDisplay()?.Detail);
        Assert.True(controller.IsAligning);
        controller.AlignDone();
        Assert.False(controller.IsAligning);
        Assert.Null(controller.AlignDisplay());
        Assert.Equal(MusicVideoOffsetOrigin.Manual, controller.OffsetOrigin);
    }

    // Ported from MusicVideoAlignTests.swift: testAClickWithoutAMoveKeepsNothing
    [Fact]
    public async Task AClickWithoutAMoveKeepsNothing()
    {
        var (controller, offsets) = await Showing(new Clock());
        controller.BeginAlign();
        var knob = controller.AlignDisplay()!.Layout(Width).Knob;
        controller.AlignMouseDown(knob + 2, 0, Width);
        controller.AlignMouseUp(knob + 2, 0);
        Assert.False(offsets.HasManual(Song.Key));
        Assert.Equal(15, controller.CurrentOffset);
    }

    // Ported from MusicVideoAlignTests.swift: testEscDuringADragSpringsBackAndEscAfterPutsTheOldOffsetBack
    [Fact]
    public async Task EscDuringADragSpringsBackAndEscAfterPutsTheOldOffsetBack()
    {
        var (controller, offsets) = await Showing(new Clock());
        controller.NudgeOffset(1);
        controller.BeginAlign();
        var knob = controller.AlignDisplay()!.Layout(Width).Knob;
        controller.AlignMouseDown(knob, 0, Width);
        controller.AlignMouseDragged(knob + 40, 0);
        Assert.NotEqual(16, controller.CurrentOffset);
        controller.AlignCancel();
        Assert.False(controller.IsAlignDragging);
        Assert.True(controller.IsAligning);
        Assert.Equal(16, controller.CurrentOffset);

        controller.AlignMouseDown(knob, 0, Width);
        controller.AlignMouseDragged(knob + 40, 0);
        controller.AlignMouseUp(knob + 40, 0);
        controller.AlignNudge(MusicVideoAlignKey.BigStep);
        Assert.NotEqual(16, controller.CurrentOffset);
        controller.AlignCancel();
        Assert.False(controller.IsAligning);
        Assert.Equal(16, controller.CurrentOffset);
        Assert.Equal(new MusicVideoOffsets.ManualEntry(16, "yt:" + VideoId), offsets.ManualEntryFor(Song.Key));
    }

    // Ported from MusicVideoAlignTests.swift: testCancelGoesBackToTheEstimateWhenThereWasNoOffsetOfTheUsers
    [Fact]
    public async Task CancelGoesBackToTheEstimateWhenThereWasNoOffsetOfTheUsers()
    {
        var (controller, offsets) = await Showing(new Clock());
        controller.BeginAlign();
        controller.AlignNudge(-MusicVideoAlignKey.Step);
        Assert.Equal(14.9, controller.CurrentOffset, 9);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, controller.OffsetOrigin);
        controller.AlignCancel();
        Assert.False(offsets.HasManual(Song.Key));
        Assert.Equal(MusicVideoOffsetOrigin.Estimated, controller.OffsetOrigin);
        Assert.Equal(15, controller.CurrentOffset);
    }

    // Ported from MusicVideoAlignTests.swift: testResetReturnsToTheEstimateAndStaysInAlignMode
    [Fact]
    public async Task ResetReturnsToTheEstimateAndStaysInAlignMode()
    {
        var (controller, offsets) = await Showing(new Clock());
        controller.BeginAlign();
        controller.AlignNudge(2);
        Assert.Equal(17, controller.CurrentOffset);
        controller.AlignReset();
        Assert.True(controller.IsAligning);
        Assert.False(offsets.HasManual(Song.Key));
        Assert.Equal(15, controller.CurrentOffset);
        Assert.Equal(MusicVideoOffsetOrigin.Estimated, controller.OffsetOrigin);
        Assert.Equal("Estimated", controller.AlignDisplay()?.Detail);
    }

    // Ported from MusicVideoAlignTests.swift: testDragNudgesAndTapShareOneSavedOffset
    [Fact]
    public async Task DragNudgesAndTapShareOneSavedOffset()
    {
        var clock = new Clock { Song = 2 };
        MusicVideoLyricLine[] lines = [new(15, "I've been tryna call"), new(20, "I've been on my own for long enough")];
        var (controller, offsets) = await Showing(clock, lines);
        controller.TapSyncButton();
        clock.Song = 23.6;
        controller.NoteVideoSample(23.5, true, Source);
        clock.Uptime += 0.1;
        controller.TapSyncButton();
        Assert.Equal(8.4, controller.CurrentOffset, 9);

        Assert.True(controller.BeginAlign());
        var knob = controller.AlignDisplay()!.Layout(Width).Knob;
        controller.AlignMouseDown(knob, 0, Width);
        Assert.Equal(8.4, controller.CurrentOffset, 9);
        controller.AlignMouseDragged(knob + 1, 130);
        controller.AlignMouseUp(knob + 1, 130);
        var dragged = controller.CurrentOffset;
        Assert.Equal(8.4 + 0.1 * 215 / Width, dragged, 0.006);
        Assert.Equal(MusicVideoTapState.Idle, controller.TapState);
        controller.VideoEarlier();
        Assert.Equal(dragged + MusicVideoController.FineNudge, controller.CurrentOffset, 0.006);
        controller.AlignNudge(-MusicVideoAlignKey.Step);
        Assert.Equal(dragged + 0.15, controller.CurrentOffset, 0.006);
        Assert.Single(offsets.Values);
        Assert.Equal("yt:" + VideoId, offsets.ManualVideos[Song.Key]);
    }

    // Ported from MusicVideoAlignTests.swift: testASongChangeEndsAlignModeKeepingWhatWasSet
    [Fact]
    public async Task ASongChangeEndsAlignModeKeepingWhatWasSet()
    {
        var (controller, offsets) = await Showing(new Clock());
        controller.BeginAlign();
        controller.AlignNudge(1);
        var knob = controller.AlignDisplay()!.Layout(Width).Knob;
        controller.AlignMouseDown(knob, 0, Width);
        controller.AlignMouseDragged(knob + 30, 0);
        controller.Update(new MusicVideoTrack("Save Your Tears", "The Weeknd", 215), enabled: true, online: false);
        Assert.False(controller.IsAligning);
        Assert.False(controller.IsAlignDragging);
        Assert.Null(controller.Align);
        Assert.Equal(16, offsets.Offset(Song.Key, "yt:" + VideoId));
    }

    // Ported from MusicVideoAlignTests.swift: testAPressWhoseReleaseNeverCameHoldsNothing
    [Fact]
    public async Task APressWhoseReleaseNeverCameHoldsNothing()
    {
        var (controller, offsets) = await Showing(new Clock());
        controller.BeginAlign();
        var knob = controller.AlignDisplay()!.Layout(Width).Knob;
        controller.AlignMouseDown(knob, 0, Width);
        controller.AlignMouseDragged(knob + 50, 0);
        Assert.True(controller.IsAlignDragging);
        controller.ButtonsHeld = () => false;
        Assert.False(controller.IsAlignDragging);
        Assert.Equal(15, controller.CurrentOffset);
        controller.ButtonsHeld = () => true;
        controller.AlignMouseDown(knob, 0, Width);
        Assert.Equal(15, controller.CurrentOffset);
        controller.AlignMouseUp(knob, 0);
        Assert.False(offsets.HasManual(Song.Key));
    }

    // Ported from MusicVideoAlignTests.swift: testRestoringAManualEntry
    [Fact]
    public void RestoringAManualEntry()
    {
        var offsets = new MusicVideoOffsets(_world.Storage);
        Assert.Null(offsets.ManualEntryFor("song"));
        offsets.Set(3.25, "song", "yt:AAAAAAAAAAA");
        var entry = offsets.ManualEntryFor("song");
        Assert.Equal(new MusicVideoOffsets.ManualEntry(3.25, "yt:AAAAAAAAAAA"), entry);
        offsets.Set(9, "song", "yt:BBBBBBBBBBB");
        offsets.RestoreManual(entry, "song");
        Assert.Equal(3.25, offsets.Offset("song", "yt:AAAAAAAAAAA"));
        Assert.False(offsets.HasManual("song", "yt:BBBBBBBBBBB"));
        offsets.RestoreManual(null, "song");
        Assert.False(offsets.HasManual("song"));
        Assert.Null(new MusicVideoOffsets(_world.Storage).ManualEntryFor("song"));
    }

    // Ported from MusicVideoAlignTests.swift: testTheDisplaysWords
    [Fact]
    public void TheDisplaysWords()
    {
        var display = new MusicVideoAlignDisplay(215, 200, 3.4, 10, MusicVideoOffsetOrigin.Estimated);
        Assert.Equal("Drag until the picture matches the music", display.Detail);
        display = display with { Origin = MusicVideoOffsetOrigin.Manual };
        Assert.Equal("Set by you · drag to adjust", display.Detail);
        display = display with { Dragging = true };
        Assert.Equal("Release to keep", display.Detail);
        display = display with { RateLabel = "Quarter speed" };
        Assert.Equal("Quarter speed", display.Detail);
        display = display with { Dragging = false, Touched = true };
        Assert.Equal("Kept for this video", display.Detail);
        display = display with { Origin = MusicVideoOffsetOrigin.Estimated };
        Assert.Equal("Estimated", display.Detail);
        display = display with { Origin = MusicVideoOffsetOrigin.None };
        Assert.Equal("No offset", display.Detail);
        display = display with { SongTime = 300 };
        Assert.Equal(215, display.VideoTime);
    }

    // ---- MusicVideoCorrectionTests.swift ----

    // Ported from MusicVideoCorrectionTests.swift: testRateNudgeEasesSmallDriftWithHysteresis
    [Fact]
    public void RateNudgeEasesSmallDriftWithHysteresis()
    {
        var nudge = new MusicVideoRateNudge();
        Assert.Equal(1, nudge.Rate(0.08));
        Assert.Equal(0.97, nudge.Rate(0.3), 4);
        Assert.True(nudge.Rate(0.3) < 1);
        Assert.Equal(0.97, nudge.Rate(1.2), 4);
        Assert.Equal(1.03, nudge.Rate(-1.2), 4);
        Assert.True(nudge.Rate(0.08) < 1);
        Assert.Equal(1, nudge.Rate(0.02));
        Assert.Equal(1, nudge.Rate(0.1));
    }

    // Ported from MusicVideoCorrectionTests.swift: testAVPlayerDriftIsEasedAndOnlyJumpsAreSought
    [Fact]
    public void RatePlayerDriftIsEasedAndOnlyJumpsAreSought()
    {
        var corrector = new MusicVideoCorrector(MusicVideoCorrectionStyle.Rate);
        var playing = new MusicVideoDrift.Sample(50.5, true);
        var eased = corrector.Decide(50, playing, true, 10, 0.05);
        Assert.Equal(MusicVideoActionKind.Rate, eased.Kind);
        Assert.InRange(eased.Value, 0.97, 0.9999);
        Assert.Equal(MusicVideoAction.None, corrector.Decide(50, playing, true, 10.25, 0.05));
        Assert.Equal(MusicVideoAction.Rate(1), corrector.Decide(50, new(50.01, true), true, 10.5, 0.05));
        Assert.Equal(MusicVideoAction.Rate(0.97), corrector.Decide(50, new(51.4, true), true, 10.75, 0.05));
        Assert.Equal(MusicVideoAction.Seek(80.05, true), corrector.Decide(80, new(51.4, true), true, 11, 0.05));
        Assert.Equal(1, corrector.CurrentRate);
        Assert.Equal(MusicVideoAction.Pause, corrector.Decide(80, new(80, true), false, 13, 0.05));
    }

    // Ported from MusicVideoCorrectionTests.swift: testSyncLoopSetsTheRateOnAVPlayer
    [Fact]
    public void SyncLoopSetsTheRateOnARatePlayer()
    {
        var surface = new RateSurface(30.6);
        var durations = new List<double>();
        var loop = new MusicVideoSyncLoop(() => 30, () => true, () => 0, () => 100) { Surface = surface, OnDuration = durations.Add };
        loop.Tick();
        Assert.Empty(surface.Seeks);
        Assert.Single(surface.Rates);
        Assert.True(surface.Rates[0] < 1);
        Assert.Equal([215.0], durations);
    }

    // Ported from MusicVideoCorrectionTests.swift: testYouTubeSeeksRarelyWithHysteresis
    [Fact]
    public void YouTubeSeeksRarelyWithHysteresis()
    {
        var corrector = new MusicVideoCorrector(MusicVideoCorrectionStyle.LedSeek);
        static MusicVideoDrift.Sample Playing(double drift) => new(100 + drift, true);
        Assert.Equal(MusicVideoAction.None, corrector.Decide(100, Playing(0.8), true, 10, 0));
        Assert.Equal(MusicVideoAction.None, corrector.Decide(100, Playing(1.2), true, 10.25, 0));
        Assert.Equal(MusicVideoAction.Seek(100.25, true), corrector.Decide(100, Playing(1.2), true, 10.5, 0));
        Assert.Equal(MusicVideoAction.None, corrector.Decide(100, Playing(-0.35), true, 11, 0));
        Assert.Equal(0.6, corrector.Latency.Lead, 4);
        var again = corrector.Decide(100, Playing(-0.35), true, 12, 0);
        Assert.True(again.IsSeek);
        Assert.Equal(100.6, again.Value, 9);
        Assert.Equal(MusicVideoAction.None, corrector.Decide(100, Playing(0.1), true, 14, 0));
        Assert.Equal(MusicVideoAction.None, corrector.Decide(100, Playing(0.5), true, 15, 0));
        Assert.Equal(MusicVideoAction.Seek(100 + corrector.Latency.Lead, true), corrector.Decide(100, Playing(4), true, 16, 0));
    }

    // Ported from MusicVideoCorrectionTests.swift: testSeekLatencyIsAMedianOfLandings
    [Fact]
    public void SeekLatencyIsAMedianOfLandings()
    {
        var latency = new MusicVideoSeekLatency();
        Assert.Equal(MusicVideoSeekLatency.Initial, latency.Lead);
        latency.Record(0.25, -0.25);
        latency.Record(0.5, 0.1);
        latency.Record(0.4, -0.1);
        Assert.Equal(0.5, latency.Lead, 4);
        latency.Record(0.5, -30);
        Assert.Equal(3, latency.Samples.Count);
        for (var i = 0; i < 10; i++) latency.Record(0.3, 0);
        Assert.Equal(MusicVideoSeekLatency.Kept, latency.Samples.Count);
        Assert.Equal(0.3, latency.Lead, 4);
    }
}
