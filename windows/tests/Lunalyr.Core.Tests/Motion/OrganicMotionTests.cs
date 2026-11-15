using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Lyrics.Timing.TimingTestLines;

namespace Lunalyr.Core.Tests.Motion;

/// <summary>Lyric motion is sampled far faster than any display refreshes, so a jump a 60 Hz frame could hide still fails here.</summary>
public class OrganicMotionTests
{
    private static readonly LyricLine First = WordTimed(10, 11.9, "Hold the night",
        new LyricWord("Hold", 10, 10.35), new LyricWord("the", 10.35, 10.6), new LyricWord("night", 10.6, 11.9));

    /// <summary>Starts while the first line's last word is still being sung, so the first line is interrupted mid-motion.</summary>
    private static readonly LyricLine Second = WordTimed(11.7, 13.4, "and let it go",
        new LyricWord("and", 11.7, 11.9), new LyricWord("let", 11.9, 12.2), new LyricWord("it", 12.2, 12.4),
        new LyricWord("go", 12.4, 13.4));

    // Ported from OrganicMotionTests.swift: testLinesAreOnlyDroppedOnceEveryWordHasLeft (the timing part; the word
    // opacities come from the Scattered layout's view code)
    [Fact]
    public void LinesAreOnlyDroppedOnceEveryWordHasLeft()
    {
        var end = LyricPhraseTiming.ExitStartOf(First, Second.Start) + LyricPhraseTiming.ExitSpan;
        Assert.True(LyricPhraseTiming.StillLeaving(First, Second.Start, end - 0.001));
        Assert.False(LyricPhraseTiming.StillLeaving(First, Second.Start, end));
        // Every word's own exit is over by then.
        var exitStart = LyricPhraseTiming.ExitStartOf(First, Second.Start);
        for (var i = 0; i < First.Words.Count; i++)
        {
            var word = First.Words[i];
            var motion = LyricMotion.ForWord(end - 0.001, word.Start, word.End, OrganicMotion.Cadence(First.Words, i), exitStart,
                                             i / 2.0, TimedWordKey.SeedOf(word.Text, word.Start), 0, false);
            Assert.Equal(1, motion.Leave, 0.0001);
        }
    }

    // Ported from OrganicMotionTests.swift: testThirtyAndSixtyFramesPerSecondReachTheSameState (the clock part)
    [Fact]
    public void ThirtyAndSixtyFramesPerSecondReachTheSameState()
    {
        // Music re-anchors every two seconds with a few tens of ms of error.
        static double Raw(double wall) => wall - 100 + ((int)(wall / 2) % 3 - 1) * 0.045;
        LyricClock thirty = new(), sixty = new();
        thirty.Time(100, Raw(100), true);
        sixty.Time(100, Raw(100), true);
        var word = First.Words[2];
        for (var frame = 1; frame <= 720; frame++)
        {
            var wall = 100 + frame / 60.0;
            var fast = sixty.Time(wall, Raw(wall), true);
            if (frame % 2 != 0) continue;
            var slow = thirty.Time(wall, Raw(wall), true);
            Assert.Equal(fast, slow, 0.002);
            var a = LyricMotion.ForWord(slow + 9, word.Start, word.End, 1.3, double.PositiveInfinity, 1, 7, 0, false);
            var b = LyricMotion.ForWord(fast + 9, word.Start, word.End, 1.3, double.PositiveInfinity, 1, 7, 0, false);
            Assert.Equal(a.Heat, b.Heat, 0.02);
            Assert.Equal(a.Reveal, b.Reveal, 0.02);
        }
    }

    // Ported from OrganicMotionTests.swift: testReanchoringNeverJumpsTheLyricClock
    [Fact]
    public void ReanchoringNeverJumpsTheLyricClock()
    {
        var clock = new LyricClock();
        var last = clock.Time(50, 0, true);
        for (var frame = 1; frame <= 600; frame++)
        {
            var wall = 50 + frame / 60.0;
            // A 60 ms correction every two seconds, alternating direction.
            var raw = wall - 50 + ((int)((wall - 50) / 2) % 2 == 0 ? 0.06 : -0.06);
            var time = clock.Time(wall, raw, true);
            var step = time - last;
            Assert.True(step > 0, "lyrics ran backwards");
            Assert.True(Math.Abs(step - 1.0 / 60) < 0.15 / 60, "pace bent more than 15%");
            last = time;
        }
        Assert.Null(clock.LastJump);
        clock.Time(61, 95, true);
        Assert.Equal(95, clock.LastJump!.Value.To);
    }

    // Ported from OrganicMotionTests.swift: testSeekCrossFadesInsteadOfCutting
    [Fact]
    public void SeekCrossFadesInsteadOfCutting()
    {
        var memory = new LyricSceneMemory();
        var key = new LyricSceneKey(LyricSceneKind.Synced, 1, 7);
        var content = new LyricSceneContent { Lines = [First, Second] };
        var wall = 200.0;
        for (var i = 0; i < 30; i++)
        {
            wall += 1.0 / 60;
            var t = memory.Observe(key, content, wall - 190, wall, null);
            Assert.Null(t.Outgoing);
            Assert.Equal(1, t.IncomingPresence);
        }
        var jump = new LyricClock.Jump(wall - 190, 80, wall + 1.0 / 60);
        double lastOut = 1, lastIn = 0;
        for (var frame = 1; frame <= 60; frame++)
        {
            wall += 1.0 / 60;
            var t = memory.Observe(key, content, 80, wall, jump);
            if (frame == 1)
            {
                Assert.NotNull(t.Outgoing);
                Assert.Equal(jump.From, t.Outgoing!.Time, 0.0001);
            }
            Assert.True(t.OutgoingPresence <= lastOut + 1e-9);
            Assert.True(t.IncomingPresence >= lastIn - 1e-9);
            Assert.True(Math.Abs(t.OutgoingPresence - lastOut) < 0.2, "outgoing cut");
            Assert.True(Math.Abs(t.IncomingPresence - lastIn) < 0.2, "incoming cut");
            lastOut = t.OutgoingPresence;
            lastIn = t.IncomingPresence;
        }
        Assert.Equal(1, lastIn);
        Assert.Equal(0, lastOut);
    }

    // Ported from OrganicMotionTests.swift: testAPlaybackCorrectionCannotAdvanceTheFadingSceneToAnotherLyric
    [Fact]
    public void APlaybackCorrectionCannotAdvanceTheFadingSceneToAnotherLyric()
    {
        var memory = new LyricSceneMemory();
        var key = new LyricSceneKey(LyricSceneKind.Synced, 1, 7);
        var content = new LyricSceneContent { Lines = [First, Second] };
        var before = Second.Start - 0.08;
        memory.Observe(key, content, before, 200, null);
        var jump = new LyricClock.Jump(before, before - 0.5, 200.02);
        var transition = memory.Observe(key, content, jump.To, 200.22, jump);
        var outgoing = transition.Outgoing;
        Assert.NotNull(outgoing);
        Assert.True(transition.OutgoingPresence > 0);
        var time = outgoing.RenderTime(200.22);
        Assert.Equal(Math.BitDecrement(Second.Start), time);
        Assert.Equal(0, new LyricLineIndex(content.Lines).LineAt(time));
    }

    // Ported from OrganicMotionTests.swift: testTimingUpgradeUsesOneLyricSheetAtATime
    [Fact]
    public void TimingUpgradeUsesOneLyricSheetAtATime()
    {
        var memory = new LyricSceneMemory();
        var line = Make("Hold on", 10, 12);
        var wordTimed = line with { WordTimed = true };
        var old = new LyricSceneKey(LyricSceneKind.Synced, 1, 7);
        var updated = new LyricSceneKey(LyricSceneKind.Synced, 1, 8);
        memory.Observe(old, new LyricSceneContent { Lines = [line] }, 10.7, 200, null);
        var swap = memory.Observe(updated, new LyricSceneContent { Lines = [wordTimed] }, 10.8, 200.1, null);
        Assert.Null(swap.Outgoing);
        Assert.Equal(1, swap.IncomingPresence);
    }

    // Ported from OrganicMotionTests.swift: testSongChangeKeepsTheVisibleLyricsLeavingThroughATwoStepUpdate
    [Fact]
    public void SongChangeKeepsTheVisibleLyricsLeavingThroughATwoStepUpdate()
    {
        var memory = new LyricSceneMemory();
        var old = new LyricSceneKey(LyricSceneKind.Synced, 1, 7);
        var firstOnly = new LyricSceneContent { Lines = [First] };
        memory.Observe(old, firstOnly, 11, 300, null);
        memory.Observe(old, firstOnly, 11.02, 300.02, null);
        // The title changes first, while the old lines are still loaded...
        var renamed = new LyricSceneKey(LyricSceneKind.Synced, 2, 7);
        var a = memory.Observe(renamed, firstOnly, 0.1, 300.04, null);
        Assert.Equal(old, a.Outgoing?.Key);
        // ...then the lines are cleared a frame later.
        var loading = new LyricSceneKey(LyricSceneKind.Loading, 2, 0);
        var b = memory.Observe(loading, new LyricSceneContent(), 0.12, 300.06, null);
        Assert.Equal(old, b.Outgoing?.Key); // the scene actually on screen keeps fading out
        Assert.Equal(11.02, b.Outgoing!.Time, 0.0001);
        Assert.True(b.OutgoingPresence > 0.9);
    }

    // Ported from OrganicMotionTests.swift: testRepeatedWordsAndLinesNeverMoveIdentically
    [Fact]
    public void RepeatedWordsAndLinesNeverMoveIdentically()
    {
        TimedWordKey[] chorus = [new("again", 20, 20.6), new("again", 41, 41.6)];
        Assert.NotEqual(chorus[0].VisualSeed, chorus[1].VisualSeed);
        var a = LyricMotion.ForWord(20.08, 20, 20.6, 0.6, double.PositiveInfinity, 0, chorus[0].VisualSeed, 0, false);
        var b = LyricMotion.ForWord(41.08, 41, 41.6, 0.6, double.PositiveInfinity, 0, chorus[1].VisualSeed, 0, false);
        Assert.NotEqual(a.Enter, b.Enter);
        Assert.NotEqual(LyricMotion.IdleDrift(chorus[0].VisualSeed, 30, 1).Y, LyricMotion.IdleDrift(chorus[1].VisualSeed, 30, 1).Y);
    }

    [Fact]
    public void TheVisualSeedIsTheMacsFnvHash()
    {
        // FNV-1a over the UTF-8 bytes, starting from the start time's bits xor the FNV offset basis.
        ulong expected = unchecked((ulong)BitConverter.DoubleToInt64Bits(8.2) ^ 1469598103934665603UL);
        foreach (var b in "é光"u8.ToArray()) expected = unchecked((expected ^ b) * 1099511628211UL);
        Assert.Equal(expected, new TimedWordKey("é光", 8.2, 9).VisualSeed);
    }

    // Ported from OrganicMotionTests.swift: testMotionFollowsTheVoice
    [Fact]
    public void MotionFollowsTheVoice()
    {
        // Held notes swell further and longer than clipped syllables.
        double held = 0, clipped = 0;
        for (var step = 0; step < 400; step++)
        {
            var t = 10 + step * 0.005;
            held = Math.Max(held, LyricMotion.ForWord(t, 10, 11.6, 1.6, double.PositiveInfinity, 0, 3, 0, false).Heat);
            clipped = Math.Max(clipped, LyricMotion.ForWord(t, 10, 10.06, 0.1, double.PositiveInfinity, 0, 3, 0, false).Heat);
        }
        Assert.True(held > clipped + 0.1);
        // Quick words arrive faster than slow ones.
        Assert.True(LyricMotion.ForWord(10.08, 10, 10.15, 0.15, double.PositiveInfinity, 0, 3, 0, false).Enter
                    > LyricMotion.ForWord(10.08, 10, 11.5, 1.5, double.PositiveInfinity, 0, 3, 0, false).Enter);
        // Dense delivery reads as more intense than a sparse line.
        var dense = Enumerable.Range(0, 12).Select(i => new LyricWord($"w{i}", 10 + i * 0.18, 10 + i * 0.18 + 0.16)).ToArray();
        var sparse = Enumerable.Range(0, 3).Select(i => new LyricWord($"w{i}", 10 + i * 1.1, 10 + i * 1.1 + 0.3)).ToArray();
        Assert.True(OrganicMotion.VocalIntensity(dense, 11) > OrganicMotion.VocalIntensity(sparse, 11) + 0.1);
    }

    // Ported from OrganicMotionTests.swift: testReduceMotionKeepsOnlyCrossFades
    [Fact]
    public void ReduceMotionKeepsOnlyCrossFades()
    {
        var calm = LyricMotion.ForWord(10.2, 10, 11.6, 1.6, 12, 0.5, 9, 1, true);
        Assert.Equal(0, calm.X);
        Assert.Equal(0, calm.Y);
        Assert.Equal(0, calm.Angle);
        Assert.Equal(1, calm.Scale);
        Assert.Equal(calm.Reveal, calm.Enter);
        Assert.Equal(0, OrganicMotion.IdleAmplitude(OrganicMotion.ConnectedLayoutFloat, 0.5, reduceMotion: true));
        var still = LyricMotion.IdleDrift(9, 40, 0);
        Assert.Equal(0, still.X);
        Assert.Equal(0, still.Y);
        Assert.Equal(1, still.Scale);
    }

    // Ported from OrganicMotionTests.swift: testEchoRevealIsContinuousAcrossOnsets
    [Fact]
    public void EchoRevealIsContinuousAcrossOnsets()
    {
        var last = WordReveal.LineRevealed(Second.Words, 11.5);
        Assert.Equal(0, last);
        for (var step = 1; step <= 2500; step++)
        {
            var value = WordReveal.LineRevealed(Second.Words, 11.5 + step / 1000.0);
            Assert.True(value >= last - 1e-12);
            Assert.True(value - last < 0.02);
            last = value;
        }
        Assert.Equal(1, last, 0.0001);
    }

    // Ported from OrganicMotionTests.swift: testInterludeDotsWanderIndependentlyAndSmoothly
    [Fact]
    public void InterludeDotsWanderIndependentlyAndSmoothly()
    {
        var gap = new VocalGap(30, 46, 4);
        var previous = gap.InterludeCue(32, 0.5)!.Value;
        var differs = false;
        for (var step = 1; step <= 4000; step++)
        {
            var cue = gap.InterludeCue(32 + step / 1000.0, 0.5)!.Value;
            for (var dot = 0; dot < 3; dot++)
            {
                Assert.True(Math.Abs(cue.Bob(dot) - previous.Bob(dot)) < 0.02);
                Assert.True(Math.Abs(cue.Bob(dot)) <= 1);
            }
            if (Math.Abs(cue.Bob(0) - cue.Bob(1)) > 0.2) differs = true;
            previous = cue;
        }
        Assert.True(differs, "the dots should not move in lockstep");
    }

    // Ported from OrganicMotionTests.swift: testRightToLeftAndJoinedScriptsAreRecognised
    [Fact]
    public void RightToLeftAndJoinedScriptsAreRecognised()
    {
        Assert.True(ScriptDirection.IsRightToLeft("سلام"));
        Assert.True(ScriptDirection.IsRightToLeft("שלום"));
        Assert.False(ScriptDirection.IsRightToLeft("hello"));
        Assert.False(ScriptDirection.AllowsGlyphMotion("سلام")); // Arabic joins; glyphs must not move apart
        Assert.True(ScriptDirection.AllowsGlyphMotion("שלום"));
        Assert.True(ScriptDirection.AllowsGlyphMotion("LIGHT"));
        Assert.False(ScriptDirection.AllowsGlyphMotion("नमस्ते"));
        Assert.True(ScriptDirection.LineIsRightToLeft(["אני", "אוהב", "you"]));
        Assert.False(ScriptDirection.LineIsRightToLeft(["I", "love", "שלום"]));
    }
}
