using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Tests.Shared;
using static Lunalyr.Tests.Shared.ReadingTestLines;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Lyrics.Readings;

/// <summary>The parts of the Mac's LyricRunReadingTests and StageReading(Reveal)Tests that are pure logic and need no
/// system services. View code (layouts, cards, rendering) is not ported.</summary>
public class LyricRunReadingTests
{
    private readonly ReadingTestLines _t = new(ReadingServices.Basic);

    private string[] RunReadings(LyricLine line, RomanizationOptions? options = null)
    {
        var glue = LyricRuns.Glue(line, line.Words);
        var pairs = LyricReadingPairs.Match(_t.Readings(line, options), line.Words, glue);
        return line.Words.Where(pairs.ContainsKey).Select(word => pairs[word]).ToArray();
    }

    // Ported from LyricRunReadingTests.swift: testRomanizationSkipsMatchingWordsWithDifferentCaseOrPunctuation
    [Fact]
    public void RomanizationSkipsMatchingWordsWithDifferentCaseOrPunctuation()
    {
        LyricWord[] words = [new("Okay!", 1, 1.5), new("夜", 1.5, 2)];
        LyricWord[] readings = [new("okay", 1, 1.5), new("yoru", 1.5, 2)];
        var annotations = LyricReadingPairs.Match(readings, words, [false, false]);
        Assert.False(annotations.ContainsKey(words[0]));
        Assert.Equal("yoru", annotations[words[1]]);
    }

    // Ported from LyricRunReadingTests.swift: testAnImportedJyutpingRunKeepsItsSyllablesApart
    [Fact]
    public void AnImportedJyutpingRunKeepsItsSyllablesApart()
    {
        var friends = Line(["佢哋", "係", "我", "嘅", "朋友"], wordTimed: false, reading: "keoi5 dei6 hai6 ngo5 ge3 pang4 jau5");
        Assert.Equal(["keoi5 dei6 hai6 ngo5 ge3 pang4 jau5"], RunReadings(friends));
    }

    // Ported from LyricRunReadingTests.swift: testTheSyllablesOfAnAlphabeticWordStillReadAsOneWord
    [Fact]
    public void TheSyllablesOfAnAlphabeticWordStillReadAsOneWord()
    {
        var love = new LyricLine
        {
            Start = 10, End = 11, Text = "люблю", Words = [new LyricWord("лю", 10, 10.5), new LyricWord("блю", 10.5, 11)], WordTimed = true,
        };
        LyricWord[] readings = [new("lyu", 10, 10.5), new("blyu", 10.5, 11)];
        var pairs = LyricReadingPairs.Match(readings, love.Words, [false, true]);
        Assert.Equal(new Dictionary<LyricWord, string> { [love.Words[0]] = "lyublyu" }, pairs);
    }

    // Ported from LyricRunReadingTests.swift: testThaiIsWrittenWithoutSpacesAndKoreanIsNot (the script checks, the
    // whole-line glue and the run reading; the rolling glue needs a dictionary and is in the Windows tests)
    [Fact]
    public void ThaiIsWrittenWithoutSpacesAndKoreanIsNot()
    {
        Assert.True(LyricRuns.WrittenWithoutSpaces("แมว"));
        Assert.True(LyricRuns.WrittenWithoutSpaces("夕焼け"));
        Assert.False(LyricRuns.WrittenWithoutSpaces("고양이"));
        Assert.False(LyricRuns.WrittenWithoutSpaces("cat"));
        var syllables = new[] { "แมว", "นอน", "บน", "โต๊ะ" }.Select((text, index) => new LyricWord(text, index, index + 1)).ToList();
        var cat = new LyricLine { Start = 0, End = 4, Text = "แมวนอนบนโต๊ะ", Words = syllables, WordTimed = true };
        Assert.Equal(["แมวนอนบนโต๊ะ"], LyricRuns.Labels(syllables, LyricRuns.Glue(cat, syllables)));
        // A run's reading is spaced by syllable, not run together.
        var read = syllables.Zip(new[] { "maeo", "non", "bon", "to" })
            .ToDictionary(pair => pair.First, pair => new LyricWord(pair.Second, pair.First.Start, pair.First.End));
        Assert.Equal("maeo non bon to", LyricRuns.Reading(syllables, read));
    }

    // Ported from LyricRunReadingTests.swift: testLatinReadingsOfChineseAndJapaneseCharactersStandApart (the part
    // with given readings; the generated part is in the Windows tests)
    [Fact]
    public void ACharacterLeftAsItIsStillKeepsToItself()
    {
        var dream = Line(["君", "の", "梦", "を"]);
        LyricWord[] partly = [new("kimi", 0, 0.5), new("no", 0.5, 1), new("梦", 1, 1.5), new("wo", 1.5, 2)];
        Assert.Equal([false, false, false, false], LyricRuns.Glue(dream, partly));
    }

    // Ported from LyricRunReadingTests.swift: testTheGlueOfALineIsWorkedOutAgainForTheWordsShown (the glue values;
    // the layer cache is view code)
    [Fact]
    public void TheGlueOfALineIsWorkedOutForTheWordsShown()
    {
        var line = Line(["Baby", "夢", "なら", "ば"]) with { Text = "Baby 夢ならば" };
        LyricWord[] shown = [line.Words[0], new("yume", 0.5, 1), new("nara", 1, 1.5), new("ba", 1.5, 2)];
        Assert.Equal([false, false, true, true], LyricRuns.Glue(line, line.Words));
        Assert.Equal([false, false, false, false], LyricRuns.Glue(line, shown));
    }

    // Ported from LyricRunReadingTests.swift: testEchoAndCardsSetTheLineAsItReads (the run text; the card is view code)
    [Fact]
    public void EchoSetsTheLineAsItReads()
    {
        var voice = Line(["君", "の", "声", "が", "沈", "む", "よ", "う", "に"]) with { Text = "君の声が 沈むように" };
        var glue = LyricRuns.Glue(voice, voice.Words);
        Assert.Equal("君の声が 沈むように", LyricRuns.Text(voice.Words, glue));
        var tonight = new LyricLine
        {
            Start = 0, End = 2, Text = "So beautiful, tonight", WordTimed = true,
            Words = [new("So", 0, 0.3), new("beau", 0.3, 0.6), new("ti", 0.6, 0.9), new("ful", 0.9, 1.2), new(",", 1.2, 1.2), new("tonight", 1.2, 2)],
        };
        Assert.Equal(["So", "beautiful,", "tonight"], LyricRuns.Labels(tonight.Words, LyricRuns.Glue(tonight, tonight.Words)));
    }
}

/// <summary>The pure parts of the Mac's StageReadingRevealTests and StageReadingTests.</summary>
public class StageReadingTests
{
    private static List<LyricWord> Words(string[] texts, double step) =>
        texts.Select((text, index) => new LyricWord(text, 1 + index * step, 1 + index * step + step * 0.9)).ToList();

    // Ported from StageReadingRevealTests.swift: testReadingsArriveWithTheirRuns
    [Fact]
    public void ReadingsArriveWithTheirRuns()
    {
        LyricWord[] words = [new("過ぎてゆく", 14.88, 16.11), new("時間", 16.33, 17.23), new("の", 17.23, 17.45), new("中", 17.45, 18.2)];
        var line = new LyricLine { Start = 14.88, End = 18.2, Text = "過ぎてゆく時間の中", Words = words, WordTimed = true };
        var runs = StageRun.Make(line.Words, LyricRuns.Glue(line, line.Words));
        Assert.Single(StageRun.Shown(runs, 15.0));
        Assert.Equal(2, StageRun.Shown(runs, 16.4).Count);
        Assert.Equal(3, StageRun.Shown(runs, 17.5).Count);
    }

    // Ported from StageReadingRevealTests.swift: testStageGlueMatchesMadeRuns
    [Fact]
    public void StageGlueMatchesMadeRuns()
    {
        string[][] samples =
        [
            ["過ぎてゆく", "時間", "の", "中"],
            ["あ", "な", "た", "を", "思", "い", "出", "す"],
            ["beau", "ti", "ful", "day"],
            ["Hel", "lo", " ", "world", "愛", "し", "て", "る"],
        ];
        foreach (var texts in samples)
        {
            var words = texts.Select((text, index) => new LyricWord(text, index, index + 1)).ToList();
            var glue = new[] { false }.Concat(Enumerable.Repeat(true, Math.Max(0, words.Count - 1))).ToList();
            var runs = StageRun.Make(words, glue).Select(run => run.Text);
            var stageGlue = StageRun.Glue(words, glue);
            var rebuilt = LyricRuns.Ranges(stageGlue, words.Count)
                .Select(range => string.Concat(words.Skip(range.Start).Take(range.End - range.Start).Select(word => word.Text.Trim())))
                .Where(text => text.Length > 0);
            Assert.Equal(runs, rebuilt);
        }
    }

    // Ported from StageReadingTests.swift: testNoReadingsDrawNothing (the reading lookups; drawing is view code)
    [Fact]
    public void NoReadingsDrawNothing()
    {
        var w = Words(["君", "の", "名", "前", "を", "呼", "ん", "で", "夜", "空", "に", "消", "え", "た"], 0.22);
        bool[] glue = [false, true, false, true, true, false, true, true, false, true, true, false, true, true];
        var readings = new Dictionary<LyricWord, string>
        {
            [w[0]] = "kimi no", [w[2]] = "namae wo", [w[5]] = "yonde", [w[8]] = "yozora ni", [w[11]] = "kieta",
        };
        var stranger = new LyricWord("雨", 40, 41);
        var run = StageRun.Make(w, glue)[0];
        Assert.Null(StageRun.Reading(run, new Dictionary<LyricWord, string>()));
        Assert.Null(StageRun.Reading(run, new Dictionary<LyricWord, string> { [stranger] = "ame" }));
        Assert.Equal("kimi no", StageRun.Reading(run, readings));
    }

    // Ported from StageReadingTests.swift: testARunsReadingJoinsItsParts
    [Fact]
    public void ARunsReadingJoinsItsParts()
    {
        var words = Words(["ka", "ze"], 0.3);
        var run = StageRun.Make(words, [false, true]);
        Assert.Single(run);
        Assert.Equal("ka ze", StageRun.Reading(run[0], new Dictionary<LyricWord, string> { [words[0]] = "ka", [words[1]] = "ze " }));
    }

    // Ported from StageReadingTests.swift: testReplaceSetsTheRomanisedWords (the runs; drawing is view code)
    [Fact]
    public void ReplaceSetsTheRomanisedWords()
    {
        var original = Words(["君", "の", "名", "前"], 0.3);
        var line = new LyricLine { Start = 1, End = 2.2, Text = "君の名前", Words = original, WordTimed = true };
        var replaced = original.Zip(new[] { "kimi", "no", "na", "mae" }).Select(pair => pair.First with { Text = pair.Second }).ToList();
        var glue = LyricRuns.Glue(line, replaced);
        Assert.Equal(["kimi", "no", "na", "mae"], StageRun.Make(replaced, glue).Select(run => run.Text));
    }
}
