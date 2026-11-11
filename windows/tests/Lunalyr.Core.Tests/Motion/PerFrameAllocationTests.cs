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
    private static readonly LyricLine[] Lines =
    [
        WordTimed(10, 11.9, "Hold the night", new LyricWord("Hold", 10, 10.35), new LyricWord("the", 10.35, 10.6),
                  new LyricWord("night", 10.6, 11.9)),
        WordTimed(11.7, 13.4, "and let it go", new LyricWord("and", 11.7, 11.9), new LyricWord("let", 11.9, 12.2),
                  new LyricWord("it", 12.2, 12.4), new LyricWord("go", 12.4, 13.4)),
    ];

    /// <summary>Everything prepared once per lyric result or per stage, as the app would hold it.</summary>
    private sealed class Stage
    {
        public readonly LyricLineIndex Index = new(Lines);
        public readonly VocalWindow[] Windows = LyricGapTiming.Windows(Lines);
        public readonly SongOffsets Offsets = new();
        public readonly ConnectionCache Cache = new();
        public readonly Box[] Boxes = [new(20, 100, 100, 40), new(300, 100, 200, 40), new(600, 300, 120, 40)];
        public readonly Segment[] Links = new Segment[8];
        public readonly List<StageRun> Runs = StageRun.Make(Lines[1].Words, new bool[Lines[1].Words.Count]);
        public readonly LyricSceneMemory Memory = new();
        public readonly LyricSceneKey Key = new(LyricSceneKind.Synced, 1, 1);
        public readonly LyricSceneContent Content;
        public readonly LyricClock Clock = new();
        public readonly WordRevealOptions Options = new(RevealsWords: true, PreviewsNextWord: true, PreviewLead: 0.45);
        public Follower Follower;
        public double Sink;

        public Stage()
        {
            Content = new LyricSceneContent { Lines = Lines, VocalWindows = Windows };
            Offsets.Set(0.25, "song");
            Offsets.OffsetFor("song"); // the first lookup is remembered
        }
    }

    private static void Frame(Stage s, double wall)
    {
        var time = s.Clock.Time(wall, wall - 990, true) + s.Offsets.OffsetFor("song");
        s.Sink += s.Follower.Follow(time, 1 / 240.0, 0.2, 2);
        var row = s.Index.LineAt(time) ?? 0;
        var line = Lines[row];
        var nextStart = LyricPhraseTiming.HandOver(row, Lines);
        var phrase = new LyricPhraseTiming(line, nextStart, time);
        s.Sink += phrase.Presence + LyricPhraseTiming.Sweep(line, time);
        var next = -1;
        for (var i = 0; i < line.Words.Count; i++)
            if (line.Words[i].Start > time) { next = i; break; }
        for (var i = 0; i < line.Words.Count; i++)
        {
            var word = line.Words[i];
            var seed = TimedWordKey.SeedOf(word.Text, word.Start);
            var motion = LyricMotion.ForWord(time, word.Start, word.End, OrganicMotion.Cadence(line.Words, i),
                                             phrase.ExitStart, i / 3.0, seed, 0.4, false);
            var shown = WordPresentation.Make(word, i, next, time, s.Options, motion,
                                              eligibleSince: i > 0 ? line.Words[i - 1].Start : line.Start);
            var drift = LyricMotion.IdleDrift(seed, time, 0.6);
            var pose = RollingMotion.Pose(RollingStyle.Column, i, time - word.Start, word.End - word.Start, seed, 0.4, false);
            s.Sink += shown.Opacity + motion.Heat + motion.Leave + drift.Y + pose.Y
                + WordReveal.Visibility(time, word.Start, word.End, true, i == next, 0.45);
        }
        s.Sink += OrganicMotion.VocalIntensity(line.Words, time) + WordReveal.LineRevealed(line.Words, time);
        if (VocalGaps.Find(s.Windows, time, 20) is { } gap) s.Sink += gap.Presence(time);
        s.Sink += s.Cache.Connections(s.Boxes, new Box(0, 0, 1200, 700), 6, s.Links);
        Span<double> ranks = stackalloc double[8];
        var count = StageMotion.ShownCount(s.Runs, time);
        StageMotion.Ranks(s.Runs, count, time, 1, 3, false, ranks);
        s.Sink += ranks[0];
        var transition = s.Memory.Observe(s.Key, s.Content, time, wall, s.Clock.LastJump);
        s.Sink += transition.IncomingPresence + transition.OutgoingPresence;
    }

    [Fact]
    public void AFrameOfLyricMotionAllocatesNothing()
    {
        // Song time 9.5 to 14.5 s: before the first line, both lines' entrances, singing, previews, the hand-over and
        // the exits.
        const int frames = 5 * 240;
        var warm = new Stage();
        for (var f = 0; f < frames; f++) Frame(warm, 999.5 + f / 240.0);

        var stage = new Stage();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 0; f < frames; f++) Frame(stage, 999.5 + f / 240.0);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(double.IsFinite(stage.Sink) && stage.Sink > 0);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DefaultOptionsRevealWordsWithThePreview()
    {
        var options = new WordRevealOptions();
        Assert.True(options.RevealsWords);
        Assert.True(options.PreviewsNextWord);
        Assert.Equal(0.45, options.PreviewLead);
        Assert.Equal(1, new LyricVisibility().Presence(0));
    }
}
