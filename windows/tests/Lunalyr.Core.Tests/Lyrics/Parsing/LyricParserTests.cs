using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

public class LyricParserTests
{
    private static LyricResult.Synced Synced(LyricResult? result) => Assert.IsType<LyricResult.Synced>(result);

    // Ported from AMLLAndKRCTests.swift: testAnAMLLFileBecomesAWordTimedCandidate (the parsing and labelling)
    [Fact]
    public void AnAmllFileBecomesWordTimedLyrics()
    {
        var ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" itunes:timing="Word">
            <body><div><p begin="1.000" end="3.000"><span begin="1.000" end="1.500">Hello</span> <span begin="1.500" end="2.200">world</span></p></div></body></tt>
            """;
        var result = Synced(LyricParser.Parse(new RawLyrics(LyricSourceId.Amll, LyricFormat.Ttml, ttml), new LyricParseOptions(200)));
        Assert.Contains(result.Lines, line => line.WordTimed);
        Assert.Equal("AMLL TTML DB", result.Provenance.Source);
        Assert.Equal("Word-by-word · AMLL TTML DB", result.Provenance.Label);
    }

    [Fact]
    public void TtmlWithOnlyCreditsOffersNothing()
    {
        var ttml = "<tt><body><div><p begin=\"1\" end=\"2\">Producer: Max Martin</p></div></body></tt>";
        Assert.Null(LyricParser.Parse(new RawLyrics(LyricSourceId.Amll, LyricFormat.Ttml, ttml)));
    }

    [Fact]
    public void LrcIsLabelledByItsTiming()
    {
        var result = Synced(LyricParser.Parse(new RawLyrics(LyricSourceId.Lrclib, LyricFormat.Lrc, "[00:01]One\n[00:03]Two", ReportedDuration: 9)));
        Assert.Equal("Line-synced · LRCLIB", result.Provenance.Label);
        // The reported length ends the last line when the caller gives none.
        Assert.Equal(9, result.Lines[^1].End);
        var mixed = Synced(LyricParser.Parse(new RawLyrics(LyricSourceId.QQMusic, LyricFormat.Lrc,
            "[00:01.00]<00:01.00>Hold <00:02.00>on<00:04.00>\n[00:06.00]Next line"), new LyricParseOptions(12)));
        Assert.Equal("Mixed timing · QQ Music", mixed.Provenance.Label);
    }

    [Fact]
    public void ALyricsfileFallsBackToItsLrcThenItsPlainText()
    {
        var extras = new Dictionary<string, string> { [LyricExtraKeys.Lrc] = "[00:01]From the LRC", [LyricExtraKeys.Plain] = "Plain one\nPlain two" };
        var fromLrc = Synced(LyricParser.Parse(new RawLyrics(LyricSourceId.Lrclib, LyricFormat.Lyricsfile, "lines: [", Extras: extras)));
        Assert.Equal(["From the LRC"], fromLrc.Lines.Select(line => line.Text));
        var plainOnly = new Dictionary<string, string> { [LyricExtraKeys.Plain] = "Plain one\nPlain two" };
        var plain = Assert.IsType<LyricResult.Plain>(LyricParser.Parse(new RawLyrics(LyricSourceId.Lrclib, LyricFormat.Lyricsfile, "", Extras: plainOnly)));
        Assert.Equal(["Plain one", "Plain two"], plain.Lines);
        Assert.Equal("Not synced · LRCLIB", plain.Provenance.Label);
        var words = Synced(LyricParser.Parse(new RawLyrics(LyricSourceId.Lrclib, LyricFormat.Lyricsfile, LyricsfileTests.Fixture("lrclib-lyricsfile-words.yaml"))));
        Assert.Equal("Mixed timing · LRCLIB", words.Provenance.Label);
    }

    [Fact]
    public void YrcTakesItsReadingsAndFallsBackToLrc()
    {
        var extras = new Dictionary<string, string> { [LyricExtraKeys.Romanization] = "[00:00.273]mu te ki" };
        var yrc = Synced(LyricParser.Parse(new RawLyrics(LyricSourceId.NetEase, LyricFormat.Yrc, "[273,3010](273,300,0)無(573,300,0)敵", Extras: extras)));
        Assert.Equal("mu te ki", yrc.Lines[0].Romanization);
        Assert.Equal("Word-by-word · NetEase", yrc.Provenance.Label);
        var lrcExtras = new Dictionary<string, string> { [LyricExtraKeys.Lrc] = "[00:01]Line timed" };
        var fallback = Synced(LyricParser.Parse(new RawLyrics(LyricSourceId.NetEase, LyricFormat.Yrc, "", Extras: lrcExtras)));
        Assert.Equal("Line-synced · NetEase", fallback.Provenance.Label);
    }

    [Fact]
    public void KrcAndLyricsPlusAreLabelled()
    {
        var krc = Synced(LyricParser.Parse(new RawLyrics(LyricSourceId.Kugou, LyricFormat.Krc, "[1000,2000]<0,500,0>Hello <500,600,0>world"), new LyricParseOptions(10)));
        Assert.Equal("Word-by-word · Kugou Music", krc.Provenance.Label);
        var json = """{"metadata":{"source":"Apple (via Lyrics+)"},"lyrics":[{"time":1000,"duration":2000,"text":"Sing along","syllabus":[{"time":1000,"duration":900,"text":"Sing "},{"time":2000,"duration":1000,"text":"along"}]}]}""";
        var plus = Synced(LyricParser.Parse(new RawLyrics(LyricSourceId.LyricsPlus, LyricFormat.KPoeJson, json)));
        Assert.Equal("Word-by-word · Apple via Lyrics+", plus.Provenance.Label);
    }

    [Fact]
    public void PlainTextIsNotSynced()
    {
        var plain = Assert.IsType<LyricResult.Plain>(LyricParser.Parse(new RawLyrics(LyricSourceId.LyricsOvh, LyricFormat.Plain, "One\nTwo")));
        Assert.Equal("Plain text · no timing · lyrics.ovh", plain.Provenance.Label);
        Assert.Null(LyricParser.Parse(new RawLyrics(LyricSourceId.LyricsOvh, LyricFormat.Plain, "  \n ")));
        Assert.Null(LyricParser.Parse(new RawLyrics(LyricSourceId.Lrclib, LyricFormat.Lrc, "Plain untimed text")));
    }
}
