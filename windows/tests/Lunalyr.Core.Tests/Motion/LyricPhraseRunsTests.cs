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
}
