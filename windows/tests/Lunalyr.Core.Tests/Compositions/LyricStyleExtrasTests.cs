using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>The Lyric style extras the layouts read: Editorial amount, the backing-vocal switch and the sung sweep's fill.</summary>
public class LyricStyleExtrasTests
{
    private static readonly LyricWord[] Words =
    [
        new("Keep", 10, 10.3), new("the", 10.3, 10.6), new("light", 10.6, 13), new("♪", 13, 13.1),
    ];

    // Ported from RenderingPerformanceTests.swift: testEditorialAmountInvalidatesPreparedTypeWithToggleOn (the metrics:
    // Windows sets a line afresh rather than through a memo, so only the change of type is checked)
    [Fact]
    public void EditorialAmountChangesTheType()
    {
        EditorialLine Set(double amount) => EditorialSetting.Layout(Words, new bool[Words.Length], 44, 11, 0, wordTimed: true,
            editorial: true, evenRunsAlternate: true, editorialAmount: amount);
        var low = Set(0);
        var high = Set(1);
        Assert.NotEqual(low.Runs.Select(run => run.Metrics), high.Runs.Select(run => run.Metrics));
        Assert.DoesNotContain(low.Runs, run => run.Metrics.AlternateFace);
        // The same amount always sets the same way, and an amount past the ends is held to them.
        Assert.Equal(high.Runs.Select(run => run.Metrics), Set(1).Runs.Select(run => run.Metrics));
        Assert.Equal(high.Runs.Select(run => run.Metrics), Set(3).Runs.Select(run => run.Metrics));
        Assert.Equal(high.Runs.Select(run => run.Metrics), Set(double.NaN).Runs.Select(run => run.Metrics));
    }

    /// <summary>Windows' own: a run's second face spreads with the amount: none at 0, every eligible one at 1.</summary>
    [Fact]
    public void TheSecondFaceSpreadsWithTheAmount()
    {
        var words = Enumerable.Range(0, 40).Select(i => new LyricWord($"word{i}", i * 0.3, i * 0.3 + 0.25)).ToArray();
        int Alternates(double amount) => EditorialSetting.Layout(words, new bool[words.Length], 44, 11, 1, wordTimed: true,
            editorial: true, editorialAmount: amount).Runs.Count(run => run.Metrics.AlternateFace);
        Assert.Equal(0, Alternates(0));
        Assert.True(Alternates(0.5) <= Alternates(1));
        Assert.True(Alternates(1) > 0);
    }

    /// <summary>Windows' own: with "Backing vocals in the background" off, bracketed words roll with the rest.</summary>
    [Fact]
    public void BackingWordsStayInTheRollingLineWhenTheLayerIsOff()
    {
        LyricWord[] words = [new("Go", 10, 10.4), new("(oh", 10.4, 10.7), new("oh)", 10.7, 11), new("now", 11, 11.5)];
        LyricLine[] lines = [new() { Start = 10, End = 11.5, Text = "Go (oh oh) now", Words = words, WordTimed = true }];
        var lifted = new RollingSong(lines, SimpleWordBreaker.Instance, null, RomanizationDisplay.Off);
        var kept = new RollingSong(lines, SimpleWordBreaker.Instance, null, RomanizationDisplay.Off, liftsBacking: false);
        Assert.Single(lifted.Backing(0));
        Assert.Empty(kept.Backing(0));
        Assert.Equal(2, lifted.Window(0, 10.5, 1.2).Words.Count);
        Assert.Equal(4, kept.Window(0, 10.5, 1.2).Words.Count);
    }

    // Ported from SpokenTypographyView.swift's RollingTokenState.sweep and ScatteredLyricText's tint (no Mac test of
    // their own): a word fills over its own time; a run of parts fills part by part, by each part's share.
    [Fact]
    public void TheSungColourFillsEachWordOverItsTime()
    {
        var word = new LyricWord("light", 10, 12);
        Assert.Equal(0, SungSweep.Progress(word, 9));
        Assert.Equal(0.5, SungSweep.Progress(word, 11), 6);
        Assert.Equal(1, SungSweep.Progress(word, 13));
        Assert.Equal(1, SungSweep.Progress(new LyricWord("a", 5, 5), 5.01)); // no length: filled once passed

        var line = new LyricLine { Start = 4, End = 8, Text = "Hold the light", Words = [], WordTimed = true };
        Assert.Equal(0.5, SungSweep.Progress(line, 6), 6);

        // From the leading edge: the left of a left-to-right word, the right of a right-to-left one.
        Assert.Equal((double.NegativeInfinity, -25.0), SungSweep.Span(100, 0.25, rightToLeft: false));
        Assert.Equal((25.0, double.PositiveInfinity), SungSweep.Span(100, 0.25, rightToLeft: true));
        Assert.Equal((double.NegativeInfinity, double.PositiveInfinity), SungSweep.Span(100, 1, rightToLeft: false));
    }
}
