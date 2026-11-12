using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Lunalyr.Tests.Shared;
using static Lunalyr.Tests.Shared.ReadingTestLines;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>Word-by-word readings as the stages set them: under each run, once under the line, or in the words' place.</summary>
public class LineReadingsTests
{
    private readonly ReadingTestLines _t = new(ReadingServices.Basic);

    private static LyricWord[] Read(LyricLine line, params string[] texts) =>
        [.. line.Words.Zip(texts, (word, text) => new LyricWord(text, word.Start, word.End))];

    [Fact]
    public void BelowPairsEachRunWithItsReading()
    {
        var line = Line(["夕焼け", "の", "空"], 10, spaced: true);
        var set = LineReadings.For(line, Read(line, "yuuyake", "no", "sora"), RomanizationDisplay.Below);
        Assert.False(set.Replaced);
        Assert.Same(line.Words, set.Words);
        Assert.Null(set.LineReading);
        Assert.Equal(["yuuyake", "no", "sora"], set.RunReadings(set.Words, set.Glue)!);
    }

    [Fact]
    public void AReadingThatReadsLikeTheWordIsNotDrawnTwice()
    {
        var line = Line(["Baby", "夢"], 10, spaced: true);
        var set = LineReadings.For(line, Read(line, "baby", "yume"), RomanizationDisplay.Below);
        Assert.Equal([null, "yume"], set.RunReadings(set.Words, set.Glue)!);
    }

    [Fact]
    public void AGluedRunReadsAsOnePhraseOnItsFirstToken()
    {
        var today = CharacterTimed("今日も");
        // The phrase 今日 shares its time over its characters, as the romanizer gives it.
        LyricWord[] readings = [new("kyo", 10, 10.8), new("u", 10, 10.8), new("mo", 10.8, 11.2)];
        var set = LineReadings.For(today, readings, RomanizationDisplay.Below);
        Assert.Equal([false, true, true], set.Glue);
        Assert.Equal(["kyou mo"], set.RunReadings(set.Words, set.Glue)!);
    }

    [Fact]
    public void AReadingThatCannotBeSplitGoesUnderTheWholeLine()
    {
        var line = Line(["佢哋", "係", "我"], 10, wordTimed: false);
        LyricWord[] whole = [new("keoi5 dei6 hai6 ngo5", 10, 11.5)];
        var set = LineReadings.For(line, whole, RomanizationDisplay.Below);
        Assert.Equal("keoi5 dei6 hai6 ngo5", set.LineReading);
        Assert.Null(set.RunReadings(set.Words, set.Glue));
    }

    [Fact]
    public void InsteadSetsTheReadingsInTheWordsPlace()
    {
        var line = Line(["君の", "名は"], 10);
        LyricWord[] shown = [new("kimi", 10, 10.25), new("no", 10.25, 10.5), new("na", 10.5, 10.75), new("wa", 10.75, 11)];
        var set = LineReadings.For(line, shown, RomanizationDisplay.Replace);
        Assert.True(set.Replaced);
        Assert.Equal(shown, set.Words);
        Assert.Equal(4, set.Glue.Count);
        // A Latin reading of an unspaced script is a word of its own.
        Assert.DoesNotContain(true, set.Glue);
        Assert.Empty(set.Annotations);
        Assert.Null(set.LineReading);
    }

    [Fact]
    public void WithoutReadingsOrWithReadingsOffTheLineIsAsItIs()
    {
        var line = Line(["one", "two"], 10, spaced: true);
        var off = LineReadings.For(line, Read(line, "uno", "dos"), RomanizationDisplay.Off);
        Assert.Same(line.Words, off.Words);
        Assert.Empty(off.Annotations);
        var none = LineReadings.For(line, null, RomanizationDisplay.Replace);
        Assert.False(none.Replaced);
        Assert.Same(line.Words, none.Words);
    }

    [Fact]
    public void ARollingSongCarriesEachWordsReading()
    {
        var line = Line(["夕焼け", "の", "空"], 10, spaced: true);
        var readings = new Dictionary<int, IReadOnlyList<LyricWord>> { [0] = Read(line, "yuuyake", "no", "sora") };
        var below = new RollingSong([line], SimpleWordBreaker.Instance, readings, RomanizationDisplay.Below);
        var window = below.Window(0, 10.2, 1.2);
        Assert.Equal("yuuyake", window.Readings[line.Words[0]]);
        Assert.Equal("sora", window.Readings[line.Words[2]]);

        var instead = new RollingSong([line], SimpleWordBreaker.Instance, readings, RomanizationDisplay.Replace);
        Assert.Equal(["yuuyake", "no", "sora"], instead.WordsOf(0).Select(word => word.Text));
        Assert.Empty(instead.Window(0, 10.2, 1.2).Readings);

        var off = new RollingSong([line], SimpleWordBreaker.Instance, readings, RomanizationDisplay.Off);
        Assert.Empty(off.Window(0, 10.2, 1.2).Readings);
    }

    [Fact]
    public void ARollingLineWithoutWordReadingsFallsBackToItsReadingString()
    {
        var first = Line(["one", "two"], 10, spaced: true);
        var second = Line(["three", "four"], 12, spaced: true);
        var song = new RollingSong([first, second], SimpleWordBreaker.Instance,
            new Dictionary<int, IReadOnlyList<LyricWord>> { [0] = Read(first, "uno", "dos") }, RomanizationDisplay.Below,
            new Dictionary<int, string> { [1] = "tres cuatro" });
        var window = song.Window(1, 12.2, 1.2);
        Assert.Equal("uno", window.Readings[first.Words[0]]);
        Assert.Equal("tres", window.Readings[song.WordsOf(1)[0]]);
        Assert.Equal("cuatro", window.Readings[song.WordsOf(1)[1]]);
    }

    // Ported from LyricRunReadingTests.swift: testAGluedRunsReadingComesWithItsFirstSyllable (Editorial's runs: the
    // Mac's test sets the line in Scattered, whose run frames are the ones Editorial's are written from)
    [Fact]
    public void AGluedRunsReadingComesWithItsFirstSyllable()
    {
        var line = new LyricLine
        {
            Start = 10, End = 11.8, Text = "고양이가 창문을", WordTimed = true,
            Words = [new("고양", 10, 10.4), new("이가", 10.4, 10.8), new("창문", 11, 11.4), new("을", 11.4, 11.8)],
        };
        var glue = LyricRuns.Glue(line, line.Words);
        Assert.Equal([false, true, false, true], glue);
        var set = LineReadings.For(line, _t.Readings(line), RomanizationDisplay.Below, glue);
        var layout = EditorialSetting.Layout(line.Words, glue, 60, 11, 0, wordTimed: true, editorial: false,
                                             set.RunReadings(line.Words, glue));
        Assert.Equal(2, layout.Runs.Count);
        Assert.All(layout.Runs, run => Assert.False(string.IsNullOrEmpty(run.Reading)));
        var options = new WordRevealOptions();

        (EditorialRunFrame Frame, double Reading)[] Frames(double time)
        {
            int? next = null;
            for (var i = 0; i < layout.Tokens.Count; i++)
                if (layout.Tokens[i].Source.Start > time) { next = i; break; }
            var tokens = new EditorialTokenFrame[layout.Tokens.Count];
            for (var i = 0; i < tokens.Length; i++)
                tokens[i] = EditorialTokenFrame.Make(layout.Tokens[i], next, time, 0, double.PositiveInfinity, false,
                                                     i > 0 ? layout.Tokens[i - 1].Source.Start : double.NegativeInfinity, options);
            var opacities = new double[tokens.Length];
            return [.. layout.Runs.Select(run =>
            {
                var frame = EditorialRunFrame.Make(run, tokens, true, opacities.AsSpan(run.TokenStart, run.TokenCount));
                return (frame, frame.ReadingOpacity(opacities.AsSpan(run.TokenStart, run.TokenCount)));
            })];
        }

        var early = Frames(10.3);
        Assert.True(early[0].Reading > 0.5);
        Assert.Equal(0, early[1].Reading); // the second word is not sung yet
        Assert.Equal(1, early[1].Frame.Presentation.Opacity); // the run itself stays opaque for its reveal mask
        Assert.True(Frames(11.3)[1].Reading > 0.5);
    }
}

/// <summary>Readings from a companion file attached to the lines they read.</summary>
public class CompanionReadingTests
{
    // Ported from ReadingAndTTMLTests.swift: testReadingsAttachByTimestampRatherThanLineIndex
    [Fact]
    public void ReadingsAttachByTimestampRatherThanLineIndex()
    {
        var lines = Lunalyr.Core.Lyrics.Parsing.LrcParser.Parse("[00:01]one\n[00:03]two\n[00:05]three", 8);
        var attached = Lunalyr.Core.Lyrics.Parsing.LocalSidecars.AttachingReadings("[00:03.10]second reading\n[00:05]third reading", lines, 8);
        Assert.Null(attached[0].Romanization);
        Assert.Equal("second reading", attached[1].Romanization);
        Assert.Equal("third reading", attached[2].Romanization);
    }
}
