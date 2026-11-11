using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Lyrics.Timing.TimingTestLines;

namespace Lunalyr.Core.Tests.Lyrics.Timing;

/// <summary>
/// Lyrics without word timing must not race through a line's words ahead of the singing. Only an abnormally long
/// window (the last line running through the outro, a line before a long break) is shortened.
/// </summary>
public class EstimatedWordPaceTests
{
    private const double Tol = 1e-6;

    private static IReadOnlyList<LyricLine> Shown(IReadOnlyList<LyricLine> lines, bool repairing = true) =>
        LyricPauseTiming.Prepare(lines, "LRCLIB", repairing).Lines;

    /// <summary>A slow song: short lines, each held for six to eight seconds.</summary>
    private const string Ballad = """
        [00:10.00]Hold on
        [00:17.00]Don't let go
        [00:24.00]Stay
        [00:31.00]I will always love you
        [00:38.50]Oh, my darling
        [00:45.00]我爱你
        [00:52.00]君の名は
        [00:59.00]Hold on
        """;

    // Ported from EstimatedWordPaceTests.swift: testAnOrdinarySlowLineFillsItsWindow
    [Fact]
    public void AnOrdinarySlowLineFillsItsWindow()
    {
        var line = Make("Hold on", 10, 16);
        Assert.Equal(10 + 6 * 0.88, line.Words[^1].End, Tol); // the words take the line's window, not 2.5 s
        Assert.Equal(0.5, LyricPhraseTiming.Sweep(line, 13), 1e-9);
        Assert.True(LyricPhraseTiming.Sweep(line, 15) < 1);
        Assert.Equal(16, EstimatedWords.EstimatedSweepEnd(line), 1e-9);
        // Stage layouts clear a line once its words are sung: not mid-line.
        Assert.True(LyricPhraseTiming.StageHeldUntil(line) > 15);
    }

    // Ported from EstimatedWordPaceTests.swift: testNoLineOfASlowSongIsShortened
    [Fact]
    public void NoLineOfASlowSongIsShortened()
    {
        var parsed = Lrc(Ballad, 66);
        foreach (var repairing in new[] { true, false })
        {
            var lines = Shown(parsed, repairing);
            Assert.Equal(8, lines.Count);
            foreach (var line in lines)
            {
                var window = line.End - line.Start;
                Assert.Equal(Math.Min(window, Math.Max(line.Words.Count * 0.42, window * 0.88)), line.Words[^1].End - line.Start, Tol);
                Assert.Equal(line.End, EstimatedWords.EstimatedSweepEnd(line), Tol);
                // Half-way through the window, the sweep is half-way through the line.
                Assert.Equal(0.5, LyricPhraseTiming.Sweep(line, line.Start + window / 2), Tol);
            }
        }
    }

    // Ported from EstimatedWordPaceTests.swift: testTheLastLineAndALineBeforeALongBreakAreShortened
    [Fact]
    public void TheLastLineAndALineBeforeALongBreakAreShortened()
    {
        const string lrc = """
            [00:10.00]Every night in my dreams
            [00:14.00]I see you, I feel you
            [00:18.00]That is how I know you go on
            [01:20.00]Far across the distance
            [01:24.00]And spaces between us
            [01:28.00]You have come to show you go on
            """;
        var lines = Shown(Lrc(lrc, 300));
        Assert.Equal(6, lines.Count);
        // Before a 62 s break, and running to the end of a 300 s track.
        foreach (var index in new[] { 2, 5 })
        {
            var line = lines[index];
            var sung = line.Words[^1].End - line.Start;
            Assert.True(sung > 4, line.Text);
            Assert.True(sung < 20, line.Text);
            Assert.True(EstimatedWords.EstimatedSweepEnd(line) < line.Start + 25, line.Text);
            Assert.Equal(1, LyricPhraseTiming.Sweep(line, line.Start + 25));
        }
        Assert.Equal(80, lines[2].End); // the line's own times are unchanged
        Assert.Equal(300, lines[5].End);
        // The lines around them keep their windows.
        foreach (var index in new[] { 0, 1, 3, 4 })
            Assert.Equal(lines[index].End, EstimatedWords.EstimatedSweepEnd(lines[index]), Tol);
    }

    // Ported from EstimatedWordPaceTests.swift: testNothingIsShortenedWithPauseInferenceOff
    [Fact]
    public void NothingIsShortenedWithPauseInferenceOff()
    {
        var lines = Shown(Lrc("[00:10.00]Every night in my dreams\n[00:14.00]I see you, I feel you\n[00:18.00]Hold on", 120), repairing: false);
        Assert.Equal(18 + 102 * 0.88, lines[2].Words[^1].End, Tol);
        Assert.Equal(120, EstimatedWords.EstimatedSweepEnd(lines[2]), Tol);
    }

    // Ported from EstimatedWordPaceTests.swift: testASuppliedLineEndIsNeverShortened
    [Fact]
    public void ASuppliedLineEndIsNeverShortened()
    {
        // A line-timed TTML line: the source says it is sung for 15 s.
        var line = Make("Stay", 30, 45);
        var lines = Shown([line, Make("Stay with me", 46, 50)]);
        Assert.Equal(30 + 15 * 0.88, lines[0].Words[^1].End, Tol);
        Assert.Equal(0.5, LyricPhraseTiming.Sweep(lines[0], 37.5), 1e-9);
    }

    // Ported from EstimatedWordPaceTests.swift: testWordsSavedByTheCappedEstimateAreSpreadAgain
    [Fact]
    public void WordsSavedByTheCappedEstimateAreSpreadAgain()
    {
        var parsed = Lrc(Ballad, 66).Select(l => l.IsInstrumental ? l : l with
        {
            Words = EstimatedWords.Distribute(l.Text, l.Start, l.Start + 2.5),
        }).ToList();
        var lines = Shown(parsed);
        Assert.Equal("Hold on", lines[0].Text);
        Assert.Equal(10 + 7 * 0.88, lines[0].Words[^1].End, Tol);
        Assert.Equal(0.5, LyricPhraseTiming.Sweep(lines[0], 13.5), 1e-9);
    }

    // Ported from EstimatedWordPaceTests.swift: testSuppliedWordTimingIsLeftAlone
    [Fact]
    public void SuppliedWordTimingIsLeftAlone()
    {
        var timed = WordTimed(5, 40, "Hold on", new LyricWord("Hold", 5, 6), new LyricWord("on", 6, 30));
        var lines = Shown([timed]);
        Assert.Equal(timed.Words, lines[0].Words);
        Assert.True(lines[0].WordTimed);
    }

    [Fact]
    public void TheDefaultTokeniserKeepsPunctuationWithItsWordAndSplitsCjk()
    {
        Assert.Equal(["Oh,", "my", "darling"], EstimatedWords.Tokenize("Oh, my darling"));
        Assert.Equal(["我", "爱", "你"], EstimatedWords.Tokenize("我爱你"));
        Assert.Equal(["Hold", "on -", "yeah"], EstimatedWords.Tokenize("Hold on - yeah"));
        Assert.Empty(EstimatedWords.Tokenize("♪"));
    }
}
