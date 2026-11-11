using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;
using static Lunalyr.Core.Tests.Lyrics.Timing.TimingTestLines;

namespace Lunalyr.Core.Tests.Lyrics.Timing;

public class LyricGapTimingTests
{
    private const double Tol = 1e-9;

    // A stand-in for the ranking code's normaliser: lower case, letters, digits and spaces.
    private static string Normalize(string text) =>
        new(text.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray());

    private static double NoSimilarity(string a, string b) => 0;

    // Ported from LyricGapTimingTests.swift: testGenuinelyRepeatedLinesWithSeparateWindowsArePreserved
    [Fact]
    public void GenuinelyRepeatedLinesWithSeparateWindowsArePreserved()
    {
        var first = Make("Hold on", 1, 1.45);
        var next = WordTimed(1.5, 2.3, "Hold on", new LyricWord("Hold", 1.5, 1.85), new LyricWord("on", 1.85, 2.3));
        var result = LyricLineIndex.RemovingMixedTimingCopies([first, next], Normalize, NoSimilarity);
        Assert.Equal([first, next], result);
    }

    // Ported from LyricGapTimingTests.swift: testNativeWordEndMarksActualVocalWindow
    [Fact]
    public void NativeWordEndMarksActualVocalWindow()
    {
        var line = WordTimed(1, 30, "Hold", new LyricWord("Hold", 1.2, 4));
        foreach (var inferring in new[] { false, true })
        {
            var windows = LyricGapTiming.Windows([line], inferring);
            Assert.Equal(1.2, windows[0].Start);
            Assert.Equal(4, windows[0].End);
        }
    }

    // Ported from LyricGapTimingTests.swift: testEstimatedWordsDoNotInventAnEarlierVocalEnd
    [Fact]
    public void EstimatedWordsDoNotInventAnEarlierVocalEnd()
    {
        var line = new LyricLine
        {
            Start = 1, End = 30, EndFromNextLine = true, Text = "Hold", Words = [new LyricWord("Hold", 1, 25)],
        };
        Assert.Equal(30, LyricGapTiming.Windows([line])[0].End);
        // The inferred end comes from the text, never from the spread-out estimated words.
        Assert.Equal(6, LyricGapTiming.Windows([line], inferringPauses: true)[0].End);
    }

    // Ported from LyricGapTimingTests.swift: testPauseInferenceIsOptInAndFindsALongBreak
    [Fact]
    public void PauseInferenceIsOptInAndFindsALongBreak()
    {
        var lines = Lrc("[00:01]Hold on\n[00:35]The song returns", 40);
        Assert.All(lines, l => Assert.True(l.EndFromNextLine));
        // Off unless the caller asks, so QQ's own opt-out is never overridden.
        Assert.Equal([35.0, 40], LyricGapTiming.Windows(lines).Select(w => w.End));
        var windows = LyricGapTiming.Windows(lines, inferringPauses: true);
        Assert.Equal([1.0, 35], windows.Select(w => w.Start));
        Assert.Equal([6.0, 40], windows.Select(w => w.End));
        var gap = VocalGaps.Find(windows, 20, 40, 2.4);
        Assert.NotNull(gap);
        Assert.Equal(6, gap.Value.Start);
        Assert.Equal(35, gap.Value.End);
    }

    // Ported from LyricGapTimingTests.swift: testChorusLineBeforeAnInterludeEndsWithItsText
    [Fact]
    public void ChorusLineBeforeAnInterludeEndsWithItsText()
    {
        // NetEase LRC: the chorus ends about 115 s, then music until 2:08.
        var lines = Lrc("[01:50.350]谁说站在光里的才算英雄\n[02:08.410]\n[02:08.710]他们说 要戒了你的狂 就像擦掉了污垢\n[02:16.250]他们说", 260);
        var windows = LyricGapTiming.Windows(lines, inferringPauses: true);
        Assert.Equal(11, LyricGapTiming.EstimatedSyllables("谁说站在光里的才算英雄"));
        // The song's own pace: 16 syllables over 7.54 s.
        var pace = LyricGapTiming.SecondsPerSyllable(lines);
        Assert.Equal(7.54 / 16, pace, 0.001);
        Assert.Equal(110.35 + 11 * pace + 3, windows[0].End, 0.001);
        Assert.Equal(128.71, windows[1].Start, 0.001);
        var gap = VocalGaps.Find(windows, 125, 260, 2.4);
        Assert.NotNull(gap);
        Assert.Equal(128.71, gap.Value.End, 0.001);
    }

    // Ported from LyricGapTimingTests.swift: testShortLinesInModestWindowsKeepThem
    [Fact]
    public void ShortLinesInModestWindowsKeepThem()
    {
        // QQGapRepair leaves windows of 12 s or less alone; so does the inference.
        var lines = Lrc("[00:00.00]Oh\n[00:11.00]Hold on\n[00:22.50]I love you so\n[00:34.00]Let it go\n[00:45.00]Next", 49);
        Assert.Equal([11.0, 22.5, 34, 45, 49], LyricGapTiming.Windows(lines, true).Select(w => w.End));
    }

    // Ported from LyricGapTimingTests.swift: testSlowSongsGetLongerEstimates
    [Fact]
    public void SlowSongsGetLongerEstimates()
    {
        var quick = Lrc("[00:00]We keep on running through the night\n[00:03]Never looking back at all\n[00:06]Stay with me tonight\n[00:20]Every heartbeat calling out\n[00:23]End", 26);
        var slow = Lrc("[00:00]Hold me closer now\n[00:06]Under fading light\n[00:12]Stay with me tonight\n[00:26]Close your eyes\n[00:32]End", 35);
        Assert.Equal(0.4, LyricGapTiming.SecondsPerSyllable(quick), 0.001);
        Assert.Equal(1, LyricGapTiming.SecondsPerSyllable(slow), 0.001);
        // The same five-syllable line in a fourteen-second window: a break in a quick song, but a held line in a ballad.
        Assert.Equal(11, LyricGapTiming.Windows(quick, true)[2].End, 0.001);
        Assert.Equal(26, LyricGapTiming.Windows(slow, true)[2].End, 0.001);
    }

    // Ported from LyricGapTimingTests.swift: testOrdinaryVerseAndLongSungLinesKeepTheirWindows
    [Fact]
    public void OrdinaryVerseAndLongSungLinesKeepTheirWindows()
    {
        var verse = Lrc("[00:01]Keep it moving\n[00:04]One more phrase\n[00:07]Hold the light\n[00:10]x", 14);
        Assert.Equal([4.0, 7, 10, 14], LyricGapTiming.Windows(verse, true).Select(w => w.End));
        // Twenty-odd syllables can fill fifteen seconds: no break is invented.
        var longLine = Lrc("[00:01]I have been waiting all of my life for a moment exactly like this one\n[00:16]Next", 20);
        Assert.Equal(16, LyricGapTiming.Windows(longLine, true)[0].End);
        Assert.True(LyricGapTiming.EstimatedSyllables("I have been waiting all of my life for a moment exactly like this one") > 15);
    }

    // Ported from LyricGapTimingTests.swift: testSuppliedEndsAreNeverShortened
    // (The Lyricsfile parsing assertion at its end belongs to the parser workstream.)
    [Fact]
    public void SuppliedEndsAreNeverShortened()
    {
        var held = WordTimed(1, 40, "Stay", new LyricWord("Stay", 1, 38));
        Assert.Equal(38, LyricGapTiming.Windows([held], true)[0].End);
        // Line-synced TTML often runs one paragraph into the next: that end is still the source's own.
        LyricLine[] ttml = [Make("Let it go", 1, 13), Make("Next line here", 13, 16)];
        Assert.All(ttml, l => Assert.Null(l.EndFromNextLine));
        Assert.Equal([13.0, 16], LyricGapTiming.Windows(ttml, true).Select(w => w.End));
        LyricLine[] lines = [Make("Oh", 1, 30), Make("Next", 40, 42)];
        Assert.Equal([30.0, 42], LyricGapTiming.Windows(lines, true).Select(w => w.End));
    }

    // Ported from LyricGapTimingTests.swift: testQQRepairLeavesNothingForTheGenericInference
    [Fact]
    public void QQRepairLeavesNothingForTheGenericInference()
    {
        var lines = Lrc("[00:01]Hold on\n[00:35]The song returns\n[00:40]And on", 60);
        var repaired = QQGapRepair.Repair(lines);
        Assert.All(repaired, l => Assert.Null(l.EndFromNextLine));
        Assert.Equal(LyricGapTiming.Windows(repaired).Select(w => w.End), LyricGapTiming.Windows(repaired, true).Select(w => w.End));
    }

    // Ported from LyricGapTimingTests.swift: testShownLineLyricsHaveTheirPausesInferredUnlessTheyAreQQs
    // (Its plain-text and no-result assertions belong to the lyrics model, not the timing.)
    [Fact]
    public void ShownLineLyricsHaveTheirPausesInferredUnlessTheyAreQQs()
    {
        var lines = Lrc("[00:01]Hold on\n[00:35]The song returns", 40);
        PreparedTiming Shown(string source, bool repairing = true) => LyricPauseTiming.Prepare(lines, source, repairing);
        // LRCLIB, NetEase's LRC, Kugou, Kuwo, local files and imports: the 34-second break after "Hold on" is no longer
        // one held line.
        foreach (var source in new[] { "LRCLIB", "NetEase", "Kugou Music", "Kuwo Music", "Lyrimuse local files", "Imported lyrics" })
        {
            Assert.True(Shown(source).InfersPauses, source);
            // Its estimated words are lit over the singing, not the break.
            var expected = lines.ToList();
            expected[0] = expected[0] with { Words = EstimatedWords.Distribute("Hold on", 1, 6) };
            Assert.Equal(expected, Shown(source).Lines);
            Assert.Equal(6, EstimatedWords.EstimatedSweepEnd(Shown(source).Lines[0]), Tol);
            Assert.Equal([6.0, 40], Shown(source).VocalWindows().Select(w => w.End));
        }
        // QQ Music's lyrics are split by QQGapRepair instead, not both.
        var qq = Shown("QQ Music");
        Assert.False(qq.InfersPauses);
        Assert.Contains(qq.Lines, l => l.IsInstrumental);
        Assert.True(qq.VocalWindows()[0].End < 35);
        // The one setting turns both estimates off.
        foreach (var source in new[] { "NetEase", "QQ Music" })
        {
            var off = Shown(source, repairing: false);
            Assert.False(off.InfersPauses, source);
            Assert.Equal(lines, off.Lines);
            Assert.Equal([35.0, 40], off.VocalWindows().Select(w => w.End));
        }
        // Word timing is never shortened.
        var words = new LyricLine
        {
            Start = 1, End = 35, EndFromNextLine = true, Text = "Hold on", WordTimed = true,
            Words = [new LyricWord("Hold", 1, 2), new LyricWord("on", 2, 30)],
        };
        Assert.Equal([30.0], LyricPauseTiming.Prepare([words], "NetEase", true).VocalWindows().Select(w => w.End));
    }

    // Ported from LyricGapTimingTests.swift: testCJKSyllablesCountPerCharacter
    [Fact]
    public void CJKSyllablesCountPerCharacter()
    {
        Assert.Equal(4, LyricGapTiming.EstimatedSyllables("君の名は"));
        Assert.Equal(5, LyricGapTiming.EstimatedSyllables("사랑해 너를"));
        Assert.Equal(2, LyricGapTiming.EstimatedSyllables("Hold on"));
    }

    // Ported from LyricGapTimingTests.swift: testExplicitInstrumentalLinesAreExcluded
    [Fact]
    public void ExplicitInstrumentalLinesAreExcluded()
    {
        var line = new LyricLine { Start = 5, End = 20, Text = "", IsInstrumental = true };
        Assert.Empty(LyricGapTiming.Windows([line], true));
    }

    // Ported from LyricGapTimingTests.swift: testInvalidOrUntimedEmptyLinesAreIgnored
    [Fact]
    public void InvalidOrUntimedEmptyLinesAreIgnored()
    {
        var line = new LyricLine
        {
            Start = double.NaN, End = 20, EndFromNextLine = true, Text = "Hold", Words = [new LyricWord("Hold", 1, 3)],
        };
        Assert.Empty(LyricGapTiming.Windows([line], true));
    }
}
