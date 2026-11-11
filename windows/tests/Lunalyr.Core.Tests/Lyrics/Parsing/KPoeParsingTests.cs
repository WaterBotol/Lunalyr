using System.Globalization;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

/// <summary>The parsing side of the Mac's LyricProviderTests for Lyrics+ (the mirror handling belongs to the provider workstream).</summary>
public class KPoeParsingTests
{
    private const string Words = """{"type":"Word","metadata":{"source":"Apple (via Lyrics+)","title":"Our Song (Remastered)","artist":["Our Artist"]},"lyrics":[{"time":1000,"duration":2000,"text":"Sing along","syllabus":[{"time":1000,"duration":900,"text":"Sing "},{"time":2000,"duration":1000,"text":"along"}]}]}""";

    // Ported from LyricProviderTests.swift: testDeadMirrorsRestAndMatchedRecordingIsReported (the reply's reading)
    [Fact]
    public void TheMatchedRecordingAndUpstreamAreReported()
    {
        var hit = KPoeJsonParser.Parse(Words);
        Assert.NotNull(hit);
        Assert.Equal("Our Song (Remastered)", hit.Title);
        Assert.Equal("Our Artist", hit.Artist);
        Assert.Equal("Apple", hit.Source);
        Assert.Equal(["Sing", "along"], hit.Lines[0].Words.Select(word => word.Text));
    }

    // Ported from LyricProviderTests.swift: testLyricsPlusKeepsTheSyllablesOfAnUnspacedLineApart (cache policy v8; the
    // LyricRuns glue assertions belong to the display workstream)
    [Fact]
    public void LyricsPlusKeepsTheSyllablesOfAnUnspacedLineApart()
    {
        static string Entry(string text, params (string Text, int Time, int Duration)[] syllables)
        {
            var parts = syllables.Select(s => string.Create(CultureInfo.InvariantCulture, $$"""{"time":{{s.Time}},"duration":{{s.Duration}},"text":"{{s.Text}}"}"""));
            var start = syllables[0].Time;
            var end = syllables[^1].Time + syllables[^1].Duration;
            return string.Create(CultureInfo.InvariantCulture, $$"""{"time":{{start}},"duration":{{end - start}},"text":"{{text}}","syllabus":[{{string.Join(",", parts)}}]}""");
        }
        string[] entries =
        [
            Entry("夕焼けの帰り道", ("夕焼け", 1000, 600), ("の", 1600, 200), ("帰り", 1800, 400), ("道", 2200, 500)),
            Entry("我们一起走", ("我", 3000, 300), ("们", 3300, 300), ("一", 3600, 300), ("起", 3900, 300), ("走", 4200, 600)),
            Entry("แมวนอนบนโต๊ะ", ("แมว", 5000, 400), ("นอน", 5400, 400), ("บน", 5800, 300), ("โต๊ะ", 6100, 600)),
            Entry("고양이가 창문을 본다", ("고양", 7000, 300), ("이가 ", 7300, 300), ("창문", 7600, 400), ("을 ", 8000, 200), ("본다", 8200, 600)),
            Entry("So beautiful, tonight", ("So ", 9000, 300), ("beau", 9300, 300), ("ti", 9600, 200), ("ful", 9800, 300),
                (", ", 10100, 50), ("to", 10200, 300), ("night", 10500, 500)),
        ];
        var hit = KPoeJsonParser.Parse("""{"type":"Word","metadata":{"source":"Apple"},"lyrics":[""" + string.Join(",", entries) + "]}");
        Assert.NotNull(hit);
        var lines = hit.Lines;
        string[][] expected =
        [
            ["夕焼け", "の", "帰り", "道"], ["我", "们", "一", "起", "走"], ["แมว", "นอน", "บน", "โต๊ะ"],
            ["고양", "이가", "창문", "을", "본다"], ["So", "beautiful,", "tonight"],
        ];
        Assert.Equal(expected.Length, lines.Count);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], lines[i].Words.Select(word => word.Text));
        Assert.Equal([1, 1.6, 1.8, 2.2], lines[0].Words.Select(word => word.Start));
        Assert.Equal([1.6, 1.8, 2.2, 2.7], lines[0].Words.Select(word => word.End));
        Assert.Equal([5, 5.4, 5.8, 6.1], lines[2].Words.Select(word => word.Start));
        Assert.Equal(9.3, lines[4].Words[1].Start, 1e-9);
        Assert.Equal(10.1, lines[4].Words[1].End, 1e-9);
        Assert.Equal(10.2, lines[4].Words[2].Start, 1e-9);
        Assert.All(lines, line => Assert.True(LyricTimingRules.ValidWordTiming(line)));
        Assert.Equal("Word-by-word", LyricTimingRules.Precision(lines));
    }

    [Theory]
    [InlineData("""{"lyrics":[]}""")]
    [InlineData("""{"lyrics":[{"time":0,"duration":10,"text":"Line only"}]}""")]
    [InlineData("""{"lyrics":[{"time":"0","duration":10,"text":"Bad","syllabus":[]}]}""")]
    [InlineData("not json")]
    public void RepliesWithoutWordTimedLinesOfferNothing(string json)
    {
        Assert.Null(KPoeJsonParser.Parse(json));
    }
}
