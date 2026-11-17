using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Motion;

/// <summary>
/// Ported from LyricPhraseRunsTests.swift (the Mac's ddb1a3c5, e6959bca): lines sung close together form a run, and a run
/// breaks only after a real break.
/// </summary>
public sealed class LyricPhraseRunsTests
{
    /// <summary>A word-timed line of <paramref name="texts"/>, a word every <paramref name="step"/> seconds from <paramref name="start"/>.</summary>
    private static LyricLine Line(string[] texts, double start, double step = 0.4, bool wordTimed = true)
    {
        var words = texts.Select((text, i) => new LyricWord(text, start + i * step, start + i * step + step * 0.9)).ToList();
        return new LyricLine { Start = start, End = words.Count > 0 ? words[^1].End : start, Text = string.Join(" ", texts), Words = words, WordTimed = wordTimed };
    }

    /// <summary>Lines with the given gaps between one line's last word end and the next line's first word.</summary>
    private static List<LyricLine> Lines(params double[] gaps)
    {
        var result = new List<LyricLine> { Line(["we", "keep", "running"], 10) };
        foreach (var gap in gaps)
        {
            var end = result[^1].Words.Max(w => w.End);
            result.Add(Line(["through", "the", "night"], end + gap));
        }
        return result;
    }

    private static StageCarry? Carry(int row, List<LyricLine> lines) => StageCarry.Make(row, new LyricPhraseRuns(lines), lines, _ => true);

    [Fact]
    public void LinesSungCloseTogetherShareARun()
    {
        var lines = Lines(0.3, 2.2);
        var runs = new LyricPhraseRuns(lines);
        Assert.Equal([0, 0, 0], runs.Lines.Select(l => l?.Run));
        Assert.Equal([0, 1, 2], runs.Lines.Select(l => l?.Ordinal));
        Assert.Equal([0, 3, 6], runs.Lines.Select(l => l?.WordsBefore));
        Assert.Equal([10.0, 10, 10], runs.Lines.Select(l => l?.RunStart));
        Assert.Null(runs[0]!.Value.Incoming);
        Assert.Equal(new LyricPhraseRuns.Boundary(10.8, lines[1].Words[0].Start), runs[0]!.Value.Outgoing);
        Assert.Equal(runs[0]!.Value.Outgoing, runs[1]!.Value.Incoming);
        Assert.NotNull(runs[1]!.Value.Outgoing);
        Assert.Null(runs[2]!.Value.Outgoing); // the song's last line hands on to nothing
        Assert.Single(runs.Lines.Select(l => l?.Seed).Distinct()); // one seed for the whole run
    }

    [Fact]
    public void TheThresholdIsOneNamedGap()
    {
        Assert.Equal(2.4, LyricPhraseRuns.BreakGap);
        var under = new LyricPhraseRuns(Lines(LyricPhraseRuns.BreakGap - 0.01));
        var over = new LyricPhraseRuns(Lines(LyricPhraseRuns.BreakGap + 0.01));
        Assert.Equal(0, under[1]!.Value.Run);
        Assert.NotNull(under[1]!.Value.Incoming);
        Assert.Equal(1, over[1]!.Value.Run); // a pause over the threshold starts a new run
        Assert.Null(over[1]!.Value.Incoming);
        Assert.Null(over[0]!.Value.Outgoing);
        Assert.Equal(0, over[1]!.Value.Ordinal);
        Assert.Equal(0, over[1]!.Value.WordsBefore);
        Assert.NotEqual(over[0]!.Value.Seed, over[1]!.Value.Seed);
    }

    [Fact]
    public void AnInstrumentalBreaksTheRunEvenWhenItIsShort()
    {
        var song = Lines(0.5, 0.5);
        song.Insert(1, new LyricLine { Start = song[1].Start - 0.2, End = song[1].Start - 0.1, Text = "", Words = [], IsInstrumental = true });
        var runs = new LyricPhraseRuns(song);
        Assert.Null(runs[1]); // an instrumental is in no run
        Assert.Null(runs[0]!.Value.Outgoing);
        Assert.Null(runs[2]!.Value.Incoming);
        Assert.Equal(1, runs[2]!.Value.Run);
        Assert.Equal(1, runs[3]!.Value.Run); // and the next lines carry on a new run
        Assert.NotNull(runs[3]!.Value.Incoming);
    }

    [Fact]
    public void ASwitchBetweenWordAndLineTimingBreaksTheRun()
    {
        var song = Lines(0.5);
        song[1] = song[1] with { WordTimed = false };
        var runs = new LyricPhraseRuns(song);
        Assert.Null(runs[0]!.Value.Outgoing);
        Assert.Equal(1, runs[1]!.Value.Run);
    }

    /// <summary>The gap counts from when the singing so far stopped: an earlier, longer note sung over a short line keeps the run going.</summary>
    [Fact]
    public void TheGapCountsFromTheLatestSinging()
    {
        var longNote = new LyricLine { Start = 10, End = 16, Text = "oh", Words = [new("oh", 10, 16)], WordTimed = true };
        var shortLine = Line(["yeah"], 10.2);
        var after = Line(["again"], 18.2);
        var runs = new LyricPhraseRuns([longNote, shortLine, after]);
        Assert.Equal(0, runs[2]!.Value.Run); // 18.2 is 2.2 s after the note ends at 16, not 7.6 s after "yeah"
        Assert.Null(runs[0]!.Value.Outgoing); // a line still sung over the next is held, not carried
    }

    /// <summary>A line-timed line's end that only marks the next line's start is not singing: the gap counts from its estimated words' end.</summary>
    [Fact]
    public void ALineTimedLineEndThatIsOnlyTheNextStartIsNotSinging()
    {
        var first = new LyricLine
        {
            Start = 10, End = 30, EndFromNextLine = true, Text = "a short line",
            Words = [.. EstimatedWords.Distribute("a short line", 10, 12)], WordTimed = false,
        };
        var second = new LyricLine
        {
            Start = 30, End = 32, Text = "after a break", Words = [.. EstimatedWords.Distribute("after a break", 30, 32)], WordTimed = false,
        };
        var runs = new LyricPhraseRuns([first, second]);
        Assert.Equal(1, runs[1]!.Value.Run);
    }

    /// <summary>Worked out from the lyrics alone: built again (after a seek, a new scene) it is the same, line for line.</summary>
    [Fact]
    public void RunsAreTheSameHoweverTheyAreReached()
    {
        var song = Lines(0.4, 5, 1, 0.2, 9, 0.6);
        Assert.Equal(new LyricPhraseRuns(song), new LyricPhraseRuns(song));
        Assert.Equal([0, 0, 1, 1, 1, 2, 2], new LyricPhraseRuns(song).Lines.Select(l => l?.Run));
    }

    [Fact]
    public void ACarryNamesBothHandOversWhenTheNeighboursShareTheLayout()
    {
        var song = Lines(0.5, 0.5);
        var middle = Carry(1, song)!;
        Assert.NotNull(middle.Incoming);
        Assert.NotNull(middle.Outgoing);
        Assert.Equal(song[2].Start, middle.Outgoing!.Value.LaterStart);
        Assert.Equal(3, middle.WordsBefore);
        // A neighbour in another layout (Custom Mix changed it) hands over nothing.
        var alone = StageCarry.Make(1, new LyricPhraseRuns(song), song, row => row == 1);
        Assert.Null(alone!.Incoming);
        Assert.Null(alone.Outgoing);
        // After a long gap there is nothing to carry.
        Assert.Null(Carry(0, Lines(6))!.Outgoing);
    }

    // ---- Holding the stage until the hand-over ----

    // Ported from LyricPhraseRunsTests.swift: testACarryingLineStaysOnStageUntilItsHandOver
    [Fact]
    public void ACarryingLineStaysOnStageUntilItsHandOver()
    {
        var song = Lines(2);
        var rules = new StageRules(RollsWordTimed: false);
        double? Held(int row) => StageCarryMotion.HeldUntil(Carry(row, song), CompositionKind.Torque);
        var handOver = StageCarryMotion.TorqueHandover(Carry(0, song)!.Outgoing!.Value);
        Assert.True(handOver >= song[1].Start);
        var during = handOver + 0.2;
        Assert.False(LineFlow.IsLeaving(0, 1, song, during, rules)); // on its own the line left soon after its last word
        Assert.True(LineFlow.IsLeaving(0, 1, song, during, rules, heldUntil: Held));
        // The exit the renderer draws, and the one the frame rate follows, agree.
        var hold = Held(0)!.Value;
        var pose = new LyricPhraseTiming(song[0], song[1].Start, during, hold);
        Assert.Equal(hold, LineFlow.StageExitStart(song[0], pose, hold));
        Assert.Equal(hold, LineFlow.RenderedExitStart(0, song, song[1].Start, rolls: false, fillsStage: true, heldUntil: hold));
        // Once the hand-over has played and the line has left, it is no longer drawn.
        Assert.False(LineFlow.IsLeaving(0, 1, song, hold + LyricPhraseTiming.ExitSpan + 0.01, rules, heldUntil: Held));
        // Layouts that do not carry keep their own exits.
        Assert.Null(StageCarryMotion.HeldUntil(Carry(0, song), CompositionKind.Mosaic));
        Assert.Null(StageCarryMotion.HeldUntil(Carry(0, song), CompositionKind.Fable));
        // After a long gap nothing is held.
        var apart = Lines(6);
        Assert.Null(StageCarryMotion.HeldUntil(Carry(0, apart), CompositionKind.Torque));
        Assert.Null(StageCarryMotion.HandoverStart(Carry(0, apart), CompositionKind.Torque));
        // Text beside the stage (a translation) leaves as the hand-over starts.
        foreach (var kind in new[] { CompositionKind.Torque, CompositionKind.Hinge, CompositionKind.Portal })
            Assert.True(StageCarryMotion.HandoverStart(Carry(0, song), kind) <= StageCarryMotion.HeldUntil(Carry(0, song), kind));
    }

    // ---- Torque ----

    private static TorquePlan Torque(LyricLine line) =>
        TorqueGeometry.Plan(StageRun.Make(line.Words, new bool[line.Words.Count]));

    private static double TorqueAngle(int face, TorquePlan plan, StageCarry? carry, double exitStart, double time) =>
        (face - TorqueGeometry.Rotation(plan, time, 0, false, carry)) * 90 + TorqueGeometry.Swing(plan, time, exitStart, false, carry);

    // Ported from LyricPhraseRunsTests.swift: testTorqueTurnsOnIntoTheNextLine
    [Fact]
    public void TorqueTurnsOnIntoTheNextLine()
    {
        var song = Lines(1.5);
        var outPlan = Torque(song[0]);
        var inPlan = Torque(song[1]);
        var outCarry = Carry(0, song)!;
        var inCarry = Carry(1, song)!;
        var hold = StageCarryMotion.HeldUntil(outCarry, CompositionKind.Torque)!.Value;
        var start = StageCarryMotion.TorqueHandover(outCarry.Outgoing!.Value);
        var last = outPlan.Faces.Length - 1;
        double? previous = null, previousStep = null;
        for (var step = -30; step <= 60; step++)
        {
            var t = start + step / 120.0;
            var outgoing = TorqueAngle(last, outPlan, outCarry, hold, t);
            var incoming = TorqueAngle(0, inPlan, inCarry, double.PositiveInfinity, t);
            Assert.Equal(90, incoming - outgoing, 9); // one column
            if (previous is { } before)
            {
                var size = outgoing - before;
                Assert.True(Math.Abs(size) < 12, $"the column jumped at {t}");
                if (previousStep is { } was) Assert.True(Math.Abs(size - was) < 2.5, $"the turn lurched at {t}");
                previousStep = size;
            }
            previous = outgoing;
        }
        Assert.Equal(0, TorqueAngle(last, outPlan, outCarry, hold, start - 0.01), 9); // faces the viewer until the hand-over
        Assert.Equal(0, TorqueAngle(0, inPlan, inCarry, double.PositiveInfinity, start + 1), 6); // and the next line faces it after
        Assert.Equal(0, TorqueGeometry.Fading(hold - 0.01, hold));
    }

    // Ported from LyricPhraseRunsTests.swift: testTorqueStartsAgainAfterALongGap
    [Fact]
    public void TorqueStartsAgainAfterALongGap()
    {
        var song = Lines(5);
        Assert.Null(Carry(0, song)!.Outgoing);
        Assert.Null(Carry(1, song)!.Incoming);
        var plan = Torque(song[1]);
        var first = song[1].Words[0].Start - StageMotion.Lead;
        Assert.Equal(45, TorqueAngle(0, plan, Carry(1, song), double.PositiveInfinity, first), 9);
    }

    // ---- Hinge ----

    // Ported from LyricPhraseRunsTests.swift: testHingeTurnsItsLastPageOntoTheNextLine
    [Fact]
    public void HingeTurnsItsLastPageOntoTheNextLine()
    {
        var song = Lines(1.2);
        var outCarry = Carry(0, song)!;
        var inCarry = Carry(1, song)!;
        var hand = outCarry.Outgoing!.Value;
        var start = StageCarryMotion.HingeHandover(hand);
        double[] pageStarts = [song[0].Words[0].Start];
        double Turn(double t) => HingeGeometry.Turns(pageStarts, false, t, 1, StageCarryMotion.HingeHandoverTurn(hand, t))[^1];
        Assert.Equal(0, Turn(start - 0.01));
        Assert.Equal(1, Turn(start + HingeGeometry.Turn), 9);
        var previous = Turn(start - 0.05);
        for (var step = 1; step <= 120; step++)
        {
            var now = Turn(start - 0.05 + step / 120.0);
            Assert.True(Math.Abs(now - previous) < 0.06, "the page jumped");
            previous = now;
        }
        // On its own the last page never turns: the book closes instead.
        Assert.Equal(0, HingeGeometry.Turns(pageStarts, false, start + 1, 1)[^1]);
        var hold = StageCarryMotion.HeldUntil(outCarry, CompositionKind.Hinge)!.Value;
        var exit = HingeGeometry.Exit(hold, hold + 0.2, carried: true);
        Assert.Equal(0, exit.Fold); // a carried line does not close the book
        Assert.Equal(0, exit.Travel);
        Assert.True(HingeGeometry.Carries(outCarry) && HingeGeometry.Carries(inCarry)); // both lines open on the middle spine
        Assert.False(HingeGeometry.Carries(Carry(1, Lines(6))));
    }

    // ---- Portal ----

    // Ported from LyricPhraseRunsTests.swift: testPortalKeepsItsColumnThroughARun
    [Fact]
    public void PortalKeepsItsColumnThroughARun()
    {
        var song = Lines(0.6, 0.6, 8);
        // A different value for every line's own seed, as its chances give.
        static double Own(int row, int salt) => (row * 37 + salt * 11) % 100 / 100.0;
        (double FlipX, double Reach, double ShiftX, double Lift) Placement(int row) =>
            PortalGeometry.Placement(Carry(row, song), Own(row, 1), Own(row, 3), Own(row, 4), Own(row, 5));
        for (var row = 1; row <= 2; row++) Assert.Equal(Placement(0), Placement(row));
        Assert.Equal(6, Carry(2, song)!.WordsBefore); // the float's phase runs on word after word
        var runs = Enumerable.Range(0, 40).Select(i => PortalGeometry.Placement(new StageCarry(i * 7919, 0, null, null), 0, 0, 0, 0).ShiftX);
        Assert.True(runs.Distinct().Count() > 10); // runs stand in different columns
        Assert.Null(Carry(3, song)!.Incoming);
    }
}
