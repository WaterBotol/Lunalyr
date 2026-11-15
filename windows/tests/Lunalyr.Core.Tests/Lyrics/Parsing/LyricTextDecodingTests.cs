using System.Text;
using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

/// <summary>Imported and local lyric files in the encodings players commonly save.</summary>
public class LyricTextDecodingTests
{
    private static Encoding Legacy(int codePage) => CodePagesEncodingProvider.Instance.GetEncoding(codePage)!;

    // Ported from LyricTextDecodingTests.swift: testLegacyEncodingsDecodeToTheOriginalLyrics
    [Fact]
    public void LegacyEncodingsDecodeToTheOriginalLyrics()
    {
        (string Text, byte[] Data)[] samples =
        [
            ("[00:01.00]夜に駆ける\n[00:05.20]沈むように溶けてゆくように", Legacy(932).GetBytes("[00:01.00]夜に駆ける\n[00:05.20]沈むように溶けてゆくように")),
            ("[00:01.00]月亮代表我的心\n[00:05.20]你问我爱你有多深", Legacy(54936).GetBytes("[00:01.00]月亮代表我的心\n[00:05.20]你问我爱你有多深")),
            ("[00:01.00]月亮代表我的心\n[00:05.20]你問我愛你有多深", Legacy(950).GetBytes("[00:01.00]月亮代表我的心\n[00:05.20]你問我愛你有多深")),
            ("[00:01.00]사랑해요\n[00:05.20]너를 처음 만난 날", Legacy(51949).GetBytes("[00:01.00]사랑해요\n[00:05.20]너를 처음 만난 날")),
            ("[00:01.00]夜に駆ける\n[00:05.20]Sing along", Encoding.Unicode.GetBytes("[00:01.00]夜に駆ける\n[00:05.20]Sing along")),
            ("[00:01.00]夜に駆ける\n[00:05.20]Sing along", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("[00:01.00]夜に駆ける\n[00:05.20]Sing along")]),
            ("[00:01.00]夜に駆ける\n[00:05.20]Sing along", [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("[00:01.00]夜に駆ける\n[00:05.20]Sing along")]),
        ];
        foreach (var (text, data) in samples)
        {
            var result = LyricTextDecoder.Lyrics(data);
            Assert.Equal(text, result.Text);
        }
        // A UTF-8 byte-order mark no longer hides the first timestamp.
        byte[] marked = [0xEF, 0xBB, 0xBF, .. "[00:01.00]First\n[00:03.00]Second"u8];
        Assert.Equal(2, LrcParser.Parse(LyricTextDecoder.Lyrics(marked).Text!, 10).Count);
    }

    // Ported from LyricTextDecodingTests.swift: testImportFailuresAreReportedRatherThanIgnored
    [Fact]
    public void ImportFailuresAreReportedRatherThanIgnored()
    {
        Assert.Equal(LyricDecodeFailure.Unreadable, LyricTextDecoder.Lyrics([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]).Failure);
        Assert.Equal(LyricDecodeFailure.NoTimestamps, LyricTextDecoder.Lyrics("Just words, no timing"u8).Failure);
        Assert.Equal(LyricDecodeFailure.TooLarge, LyricTextDecoder.Lyrics(Enumerable.Repeat((byte)0x41, 20).ToArray(), limit: 10).Failure);
        Assert.False(string.IsNullOrEmpty(LyricDecodeResult.Message(LyricDecodeFailure.Unreadable)));
        // TTML that declares its legacy encoding still parses once decoded.
        var ttml = """<?xml version="1.0" encoding="Shift_JIS"?><tt><body><div><p begin="1" end="3">夜に駆ける</p></div></body></tt>""";
        var decoded = LyricTextDecoder.Lyrics(Legacy(932).GetBytes(ttml)).Text;
        Assert.NotNull(decoded);
        Assert.StartsWith("""<?xml version="1.0" encoding="UTF-8"?>""", decoded);
        Assert.Equal("夜に駆ける", TtmlParser.Parse(decoded, 10)[0].Text);
    }
}
