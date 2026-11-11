using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Lyrics.Timing.TimingTestLines;

namespace Lunalyr.Core.Tests.Motion;

/// <summary>
/// The per-frame paths run for every word at up to 240 Hz: once warmed up, a frame must not allocate.
/// </summary>
public class PerFrameAllocationTests
{
    [Fact]
    public void AFrameOfLyricMotionAllocatesNothing()
    {
        LyricLine[] lines =
        [
            WordTimed(10, 11.9, "Hold the night", new LyricWord("Hold", 10, 10.35), new LyricWord("the", 10.35, 10.6),
                      new LyricWord("night", 10.6, 11.9)),
            WordTimed(11.7, 13.4, "and let it go", new LyricWord("and", 11.7, 11.9), new LyricWord("let", 11.9, 12.2),
                      new LyricWord("it", 12.2, 12.4), new LyricWord("go", 12.4, 13.4)),
        ];
        var index = new LyricLineIndex(lines);
        var windows = LyricGapTiming.Windows(lines);
        var offsets = new SongOffsets();
        offsets.Set(0.25, "song");
        var cache = new ConnectionCache();
        Box[] boxes = [new(20, 100, 100, 40), new(300, 100, 200, 40), new(600, 300, 120, 40)];
        var links = new Segment[8];
        var runs = StageRun.Make(lines[1].Words, new bool[lines[1].Words.Count]);
        var memory = new LyricSceneMemory();
        var key = new LyricSceneKey(LyricSceneKind.Synced, 1, 1);
        var content = new LyricSceneContent { Lines = lines, VocalWindows = windows };
        var clock = new LyricClock();
        var follower = new Follower();
        var options = new WordRevealOptions();
        double sink = 0;

        void Frame(double wall)
        {
            var time = clock.Time(wall, wall - 990, true) + offsets.OffsetFor("song");
            sink += follower.Follow(time, 1 / 240.0, 0.2, 2);
            var row = index.LineAt(time) ?? 0;
            var line = lines[row];
            var nextStart = LyricPhraseTiming.HandOver(row, lines);
            var phrase = new LyricPhraseTiming(line, nextStart, time);
            sink += phrase.Presence + LyricPhraseTiming.Sweep(line, time);
            for (var i = 0; i < line.Words.Count; i++)
            {
                var word = line.Words[i];
                var seed = TimedWordKey.SeedOf(word.Text, word.Start);
                var motion = LyricMotion.ForWord(time, word.Start, word.End, OrganicMotion.Cadence(line.Words, i),
                                                 phrase.ExitStart, i / 3.0, seed, 0.4, false);
                var shown = WordPresentation.Make(word, i, 0, time, options, motion);
                var drift = LyricMotion.IdleDrift(seed, time, 0.6);
                var pose = RollingMotion.Pose(RollingStyle.Column, i, time - word.Start, word.End - word.Start, seed, 0.4, false);
                sink += shown.Opacity + drift.Y + pose.Y + WordReveal.Visibility(time, word.Start, word.End, true, i == 0, 0.45);
            }
            sink += OrganicMotion.VocalIntensity(line.Words, time) + WordReveal.LineRevealed(line.Words, time);
            if (VocalGaps.Find(windows, time, 20) is { } gap) sink += gap.Presence(time);
            sink += cache.Connections(boxes, new Box(0, 0, 1200, 700), 6, links);
            Span<double> ranks = stackalloc double[8];
            var count = StageMotion.ShownCount(runs, time);
            StageMotion.Ranks(runs, count, time, 1, 3, false, ranks);
            sink += ranks[0];
            var transition = memory.Observe(key, content, time, wall, clock.LastJump);
            sink += transition.IncomingPresence + transition.OutgoingPresence;
        }

        // Warm up: JIT, static data, the caches' first build.
        for (var f = 0; f < 2000; f++) Frame(1000 + f / 240.0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 2000; f < 2960; f++) Frame(1000 + f / 240.0);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(double.IsFinite(sink));
        Assert.Equal(0, allocated);
    }
}
