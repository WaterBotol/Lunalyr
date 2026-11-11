using System.IO.Compression;
using System.Text;
using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

public class AmllAndKrcTests
{
    private static readonly byte[] Key = [0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69];

    /// <summary>A KRC file as the Mac test builds one: "krc1", then 78 9C and raw deflate, XOR-ed with the key.</summary>
    private static byte[] Krc(string text)
    {
        using var raw = new MemoryStream();
        using (var deflate = new DeflateStream(raw, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(Encoding.UTF8.GetBytes(text));
        var bytes = new byte[] { 0x78, 0x9C }.Concat(raw.ToArray()).ToArray();
        for (var index = 0; index < bytes.Length; index++) bytes[index] ^= Key[index % Key.Length];
        return [.. "krc1"u8, .. bytes];
    }

    private const string Sample =
        "[id:$00000000]\n[ar:An Artist]\n[1000,2000]<0,500,0>Hello <500,600,0>world <1100,900,0>again\n[4000,1500]<0,700,0>Second<700,800,0> line";

    // Ported from AMLLAndKRCTests.swift: testKRCDecodesToTheLinesAndWords
    [Fact]
    public void KrcDecodesToTheLinesAndWords()
    {
        var text = KugouKrc.Decode(Krc(Sample));
        Assert.NotNull(text);
        Assert.Contains("<0,500,0>Hello", text);
    }

    // Ported from AMLLAndKRCTests.swift: testKRCBecomesWordTimedLines
    [Fact]
    public void KrcBecomesWordTimedLines()
    {
        var lrc = KugouKrc.EnhancedLrc(Krc(Sample));
        Assert.NotNull(lrc);
        var lines = LrcParser.Parse(lrc, 10);
        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].WordTimed);
        Assert.Equal(["Hello", "world", "again"], lines[0].Words.Select(word => word.Text.Trim()));
        Assert.Equal(1.5, lines[0].Words[1].Start, 0.02);
        Assert.Equal(1.5, lines[0].Words[0].End, 0.1);
    }

    // Ported from AMLLAndKRCTests.swift: testAnythingThatIsNotKRCIsRefused
    [Fact]
    public void AnythingThatIsNotKrcIsRefused()
    {
        Assert.Null(KugouKrc.Decode("[00:01.00]plain"u8));
        Assert.Null(KugouKrc.EnhancedLrc(Enumerable.Repeat((byte)1, 40).ToArray()));
    }

    // Ported from LyricCoreReviewTests.swift: testConvertedStampsNeverSaySixtySeconds
    [Fact]
    public void ConvertedStampsNeverSaySixtySeconds()
    {
        Assert.Equal("01:00.000", KugouKrc.Clock(59.9996));
        Assert.Equal("01:59.998", KugouKrc.Clock(119.998));
        Assert.Equal("00:00.000", KugouKrc.Clock(0));
        Assert.Equal("00:00.000", KugouKrc.Clock(-2));
        Assert.Equal("62:05.250", KugouKrc.Clock(3725.25));
    }

    // Ported from LyricCoreReviewTests.swift: testAKRCLineOrWordAtAMinuteBoundaryIsKept
    [Fact]
    public void AKrcLineOrWordAtAMinuteBoundaryIsKept()
    {
        var krc = "[59996,3000]<0,1000,0>Hello <1000,1000,0>world\n[118000,3000]<0,1996,0>Once <1996,1000,0>more";
        var lrc = KugouKrc.EnhancedLrcFromText(krc);
        Assert.NotNull(lrc);
        var lines = LrcParser.Parse(lrc, 200).Where(line => !line.IsInstrumental).ToList();
        Assert.Equal(["Hello world", "Once more"], lines.Select(line => line.Text));
        Assert.Equal(59.996, lines[0].Start, 0.001);
        Assert.All(lines, line => Assert.True(line.WordTimed));
        Assert.Equal(["Once", "more"], lines[1].Words.Select(word => word.Text));
        Assert.Equal(119.996, lines[1].Words[1].Start, 0.001);
    }
}
