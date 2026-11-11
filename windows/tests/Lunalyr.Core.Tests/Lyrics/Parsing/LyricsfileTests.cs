using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

public class LyricsfileTests
{
    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lyrics", name));

    // Ported from NextVersionTests.swift: testLyricsfileRetainsRealWordTimingsAndIgnoresMalformedTimings
    [Fact]
    public void LyricsfileRetainsRealWordTimingsAndIgnoresMalformedTimings()
    {
        var text = """
            version: '1.0'
            lines:
              - text: "Stay here"
                start_ms: 1000
                end_ms: 3000
                words:
                  - text: 'Stay '
                    start_ms: 1000
                    end_ms: 2000
                  - text: here
                    start_ms: 2000
                    end_ms: 3000
            """;
        var lines = LyricsfileParser.Parse(text);
        Assert.Single(lines);
        Assert.True(lines[0].WordTimed);
        Assert.Equal(2, lines[0].Words[1].Start);
        Assert.False(LyricsfileParser.Parse(text.Replace("end_ms: 2000", "end_ms: 4000"))[0].WordTimed);
    }

    // Ported from NextVersionTests.swift: testLyricsfileInfersMissingWordEnds
    [Fact]
    public void LyricsfileInfersMissingWordEnds()
    {
        var text = """
            lines:
            - text: 'One two'
              start_ms: 1000
              end_ms: 4000
              words:
              - text: 'One '
                start_ms: 1000
              - text: two
                start_ms: 2000
            """;
        var lines = LyricsfileParser.Parse(text);
        Assert.True(lines[0].WordTimed);
        Assert.Equal([2.0, 4.0], lines[0].Words.Select(word => word.End));
    }

    // Ported from LyricGapTimingTests.swift: testSuppliedEndsAreNeverShortened (the lyricsfile assertion)
    [Fact]
    public void RowsWithoutAnEndAreLikeLrc()
    {
        var file = LyricsfileParser.Parse("lines:\n- text: Let it go\n  start_ms: 1000\n  end_ms: 13000\n- text: Next line here\n  start_ms: 13000\n");
        Assert.Equal([null, true], file.Select(line => line.EndFromNextLine));
    }

    [Fact]
    public void AWordTimedFileAsLrclibWritesItReadsWordByWord()
    {
        var lines = LyricsfileParser.Parse(Fixture("lrclib-lyricsfile-words.yaml"));
        Assert.Equal(["Hey", "I've been waiting here", "I said, \"Light the lanterns", "Time: it's late", "", "Goin' home"],
            lines.Select(line => line.Text));
        Assert.Equal(["I've", "been", "waiting", "here"], lines[1].Words.Select(word => word.Text));
        Assert.Equal(27.598, lines[1].Words[0].End, 1e-9);
        Assert.Equal(28.268, lines[1].Words[^1].End, 1e-9);
        Assert.True(lines[4].IsInstrumental);
        Assert.Equal([true, true, true, true, false, false], lines.Select(line => line.WordTimed));
        Assert.Equal(44.26, lines[^1].End, 1e-9);
        Assert.True(lines[^1].EndFromNextLine);
        Assert.Equal("Mixed timing", LyricLines.Precision(lines));
    }

    [Fact]
    public void ALineSyncedFileReadsLineByLine()
    {
        var lines = LyricsfileParser.Parse(Fixture("lrclib-lyricsfile-lines.yaml"));
        Assert.Equal(4, lines.Count);
        Assert.Equal([12.67, 17.17, 19.98, 26.03], lines.Select(line => line.Start));
        Assert.All(lines, line => Assert.False(line.WordTimed));
        Assert.Equal("Line-synced", LyricLines.Precision(lines));
    }

    [Fact]
    public void AnOffsetMovesEveryTime()
    {
        var lines = LyricsfileParser.Parse("metadata:\n  offset_ms: -500\nlines:\n- text: Hi\n  start_ms: 1000\n  end_ms: 2000\n");
        Assert.Equal(0.5, lines[0].Start, 1e-9);
        Assert.Equal(1.5, lines[0].End, 1e-9);
    }

    [Theory]
    [InlineData("lines:\n- text: Hi\n  start_ms: '1000'\n")] // a quoted number is a string
    [InlineData("lines:\n- start_ms: 1000\n")] // no text
    [InlineData("lines:\n- text: Hi\n")] // no start
    [InlineData("lines:\n- text: Hi\n  start_ms: 1000\n  start_ms: 2000\n")] // duplicate key
    [InlineData("lines: &rows\n- text: Hi\n  start_ms: 1000\n")] // anchors are not in the subset
    [InlineData("lines:\n- text: Hi\n  start_ms: 1000\n  words: nope\n")] // words must be a list
    [InlineData("lines:\n\t- text: Hi\n")] // tab indentation
    [InlineData("lines:\n- text: Hi: there\n  start_ms: 1000\n")] // ": " in a plain value
    public void AnythingOutsideTheSubsetMakesTheFileUnreadable(string yaml)
    {
        Assert.Empty(LyricsfileParser.Parse(yaml));
    }

    [Fact]
    public void TheYamlSubsetReadsQuotingBlocksAndFlows()
    {
        var yaml = """
            ---
            # a comment
            lines:
            - text: "Tab\there é \"quoted\""
              start_ms: 1.5e3   # trailing comment
              end_ms: ~
              words: []
            - text: 'Folded
                over two lines'
              start_ms: 3000
            - text: >-
                Folded block
                text
              start_ms: 5000
            - text: |-
                Literal
              start_ms: 7000
            extra: {a: 1, b: [x, 'y']}
            """;
        var lines = LyricsfileParser.Parse(yaml);
        Assert.Equal(["Tab\there é \"quoted\"", "Folded over two lines", "Folded block text", "Literal"], lines.Select(line => line.Text));
        Assert.Equal(1.5, lines[0].Start);
        Assert.Equal(3, lines[0].End);
    }
}
