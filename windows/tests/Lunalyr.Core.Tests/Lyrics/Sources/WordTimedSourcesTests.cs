using System.Collections.Concurrent;
using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

/// <summary>The Mac's WordTimedSourcesTests.swift (7ddb08a): QQ Music QRC and Kuwo lrcx word timing, from fixtures (no network).</summary>
public sealed class WordTimedSourcesTests
{
    // ---- Fixture builders ----

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    /// <summary>QQ's encoding: zlib, zero-padded to whole blocks, triple-DES, hex.</summary>
    private static string QrcCipher(string xml)
    {
        var plain = new List<byte>(WordTimedLrc.Deflate(Encoding.UTF8.GetBytes(xml)));
        while (plain.Count % 8 != 0) plain.Add(0);
        return Hex(QQMusicTripleDes.Encrypt(plain.ToArray())!);
    }

    private static string QrcXml(string content) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <QrcInfos>
        <QrcHeadInfo SaveTime="269" Version="100"/>
        <LyricInfo LyricCount="1">
        <Lyric_1 LyricType="1" LyricContent="{content}"/>
        </LyricInfo>
        </QrcInfos>
        """;

    private static string Download(string field) =>
        $"<!--\n<cmd value=\"1031\" verson=\"4\"><miniversion=\"1\" /><result>0</result><lyric musicid=\"7\"><content type=\"file\"><![CDATA[{field}]]></content><contentts><![CDATA[]]></contentts></lyric></cmd>\n-->";

    /// <summary>Kuwo's encoding: XOR "yeelion", base64, zlib, behind a <c>TP=content</c> header.</summary>
    private static byte[] Lrcx(string text)
    {
        var key = "yeelion"u8.ToArray();
        var xored = Encoding.UTF8.GetBytes(text).Select((b, i) => (byte)(b ^ key[i % key.Length])).ToArray();
        return [.. "TP=content\r\nlrcx=1\r\n\r\n"u8, .. WordTimedLrc.Deflate(Encoding.UTF8.GetBytes(Convert.ToBase64String(xored)))];
    }

    private static IReadOnlyList<LyricLine> Parse(string lrc) =>
        Assert.IsType<LyricResult.Synced>(LyricParser.Parse(new RawLyrics(LyricSourceId.QQMusic, LyricFormat.Lrc, lrc, "", "", 60))).Lines;

    private static string[] Words(LyricLine line) => [.. line.Words.Select(w => w.Text.Trim())];

    /// <summary>
    /// QRC content: the title and first credit lines are from a real reply for 晴天 by 周杰伦 (lyric_download.fcg, header
    /// trimmed); the last line is made up to carry an unescaped quote. The real ciphertext sample is the three-block
    /// vector in <see cref="TheTripleDesPortDecryptsRealQQCiphertext"/>.
    /// </summary>
    private const string RealQrcOpening = """
        [ti:晴天]
        [ar:周杰伦]
        [offset:0]
        [0,2250]晴(0,160)天(160,160) (320,160)-(480,160) (640,160)周(800,160)杰(960,160)伦(1120,160) (1280,160)((1440,160)Jay(1600,160) (1760,160)Chou(1920,160))(2080,160)
        [2250,2250]词(2250,450)：(2700,450)周(3150,450)杰(3600,450)伦(4050,450)
        [20000,3000]Hello (20000,500)"quoted" (20500,700)world(21200,800)
        """;

    // ---- QQ Music QRC ----

    // Ported from WordTimedSourcesTests.swift: testTheTripleDESPortDecryptsRealQQCiphertext
    [Fact]
    public void TheTripleDesPortDecryptsRealQQCiphertext()
    {
        // The first three blocks of the real 晴天 reply and what they decrypt to: a zlib header (78 9C) and deflate data.
        // Standard DES gives garbage here.
        var cipher = Convert.FromHexString("E5D94A70C91F702238E3DD4AEC7EC2A6247F320388EA1F8F");
        Assert.Equal("789C4D9A5BAF56D77586EFF91588ABF5495F9A35CF735A71", Hex(QQMusicTripleDes.Decrypt(cipher)!));
        Assert.Equal("E5D94A70C91F702238E3DD4AEC7EC2A6247F320388EA1F8F",
                     Hex(QQMusicTripleDes.Encrypt(Convert.FromHexString("789C4D9A5BAF56D77586EFF91588ABF5495F9A35CF735A71"))!));
    }

    // Ported from WordTimedSourcesTests.swift: testQRCDecryptsAndKeepsTheWholeLyricContent
    [Fact]
    public void QrcDecryptsAndKeepsTheWholeLyricContent()
    {
        var xml = Assert.IsType<string>(QQMusicQrc.Decrypt(QrcCipher(QrcXml(RealQrcOpening))));
        var content = Assert.IsType<string>(QQMusicQrc.LyricContent(xml));
        // A literal, unescaped quote inside the lyric does not end it early.
        Assert.Contains("\"quoted\"", content);
        Assert.EndsWith("world(21200,800)", content);
        Assert.Null(QQMusicQrc.Decrypt("nothex"));
        Assert.Null(QQMusicQrc.Decrypt(string.Concat(Enumerable.Repeat("AB", 16))));
    }

    // Ported from WordTimedSourcesTests.swift: testQRCBecomesWordTimedEnhancedLRC
    [Fact]
    public void QrcBecomesWordTimedEnhancedLrc()
    {
        var lrc = Assert.IsType<string>(QQMusicQrc.EnhancedLrcFromQrc(RealQrcOpening));
        Assert.Contains("[offset:0]", lrc);
        // Each word comes before its stamp in QRC and after it in enhanced LRC; stamps are absolute.
        Assert.Contains("[00:02.250]<00:02.250>词<00:02.700>：", lrc);
        var sung = Parse(lrc).First(line => line.Text.Contains("world", StringComparison.Ordinal));
        Assert.True(sung.WordTimed);
        Assert.Equal(["Hello", "\"quoted\"", "world"], Words(sung));
        Assert.Equal(20, sung.Start, 2);
        Assert.Equal(20.5, sung.Words[1].Start, 2);
        Assert.Equal(21.2, sung.Words[2].Start, 2);
        // The last word runs to the line's end, [20000,3000], as with Kugou KRC.
        Assert.Equal(23.0, sung.Words[2].End, 2);
        // Literal parentheses in the text stay text; only (start,length) pairs are stamps.
        Assert.Contains("<00:01.440>(<00:01.600>Jay", lrc);
    }

    // Ported from WordTimedSourcesTests.swift: testALyricDownloadReplyGivesWordTimingOrLineLRC
    [Fact]
    public void ALyricDownloadReplyGivesWordTimingOrLineLrc()
    {
        var qrc = Assert.IsType<QQMusicQrc.Download>(QQMusicQrc.DownloadOf(Download(QrcCipher(QrcXml(RealQrcOpening)))));
        Assert.NotNull(qrc.Enhanced);
        Assert.Null(qrc.Line);
        // A song without QRC is sent as line LRC through the same field.
        var lineOnly = Assert.IsType<QQMusicQrc.Download>(QQMusicQrc.DownloadOf(Download(QrcCipher("[00:01.00]Sing along\n[00:04.00]Next line"))));
        Assert.Null(lineOnly.Enhanced);
        Assert.Equal("[00:01.00]Sing along\n[00:04.00]Next line", lineOnly.Line);
        // Plain text (no lyric file) is not decryptable: nothing.
        Assert.Null(QQMusicQrc.DownloadOf(Download("Sing along")));
        Assert.Null(QQMusicQrc.DownloadOf("<html>blocked</html>"));
    }

    // ---- Kuwo lrcx ----

    private const string LrcxSample = """
        [kuwo:071]
        [ver:v1.0]
        [ti:Our Song]
        [offset:0]
        [00:19.205]<1183,-1183>I <2231,-541>never <3000,1000>stop
        [00:27.326]<0,0>我<0,0>从<0,0>未
        [00:27.326]<1113,-1113>Second <2000,-500>line
        """;

    // Ported from WordTimedSourcesTests.swift: testKuwoLrcxDecodes
    [Fact]
    public void KuwoLrcxDecodes()
    {
        Assert.Equal(LrcxSample, KuwoLrcx.Decode(Lrcx(LrcxSample)));
        Assert.Null(KuwoLrcx.Decode("<html>blocked</html>"u8));
        Assert.Null(KuwoLrcx.Decode("""{"lyrics":"Sing along"}"""u8));
    }

    // Ported from WordTimedSourcesTests.swift: testKuwoLrcxCoefficientsAreOctal
    [Fact]
    public void KuwoLrcxCoefficientsAreOctal()
    {
        // 071 octal = 57: k1 = 5, k2 = 7.
        Assert.Equal((5, 7), KuwoLrcx.Coefficients("[ver:v1.0]\n[kuwo:071]"));
        Assert.Equal((8, 7), KuwoLrcx.Coefficients("[kuwo:127]"));
        Assert.Null(KuwoLrcx.Coefficients("[kuwo:010]")); // a zero coefficient cannot be decoded
        Assert.Null(KuwoLrcx.Coefficients("[ti:no header]"));
    }

    // Ported from WordTimedSourcesTests.swift: testKuwoLrcxBecomesWordTimedLinesWithoutTheTranslation
    [Fact]
    public void KuwoLrcxBecomesWordTimedLinesWithoutTheTranslation()
    {
        var lrc = Assert.IsType<string>(KuwoLrcx.ToEnhancedLrc(Lrcx(LrcxSample)));
        Assert.DoesNotContain("我", lrc); // translation lines carry only <0,0> stamps and are dropped
        var lines = Parse(lrc).Where(l => !l.IsInstrumental).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.True(line.WordTimed));
        Assert.Equal(["I", "never", "stop"], Words(lines[0]));
        // <a,b>: start |a+b|/(2·k1), length |a−b|/(2·k2) ms after the line.
        Assert.Equal(19.205, lines[0].Words[0].Start, 2);
        Assert.Equal(19.205 + 0.169, lines[0].Words[1].Start, 2);
        Assert.Equal(19.205 + 0.4, lines[0].Words[2].Start, 2);
        Assert.Equal(27.326, lines[1].Start, 2);
    }

    // ---- Kugou KRC ----

    // Ported from WordTimedSourcesTests.swift: testKRCWordsThatStartBeforeTheirLineStayWordTimed
    [Fact]
    public void KrcWordsThatStartBeforeTheirLineStayWordTimed()
    {
        var lrc = Assert.IsType<string>(KugouKrc.EnhancedLrcFromText("[1000,2000]<-11,500,0>Early <500,600,0>word"));
        Assert.DoesNotContain("<-11", lrc);
        var line = Parse(lrc)[0];
        Assert.True(line.WordTimed);
        Assert.Equal(["Early", "word"], Words(line));
        Assert.Equal(1.0, line.Words[0].Start, 3);
    }

    // ---- Sources, with fallback to line LRC ----

    private readonly LyricQuery _query = new("Our Song", "Our Artist", null, 180);

    private static string QQSearch(params int[] ids) =>
        """{"data":{"song":{"list":[""" + string.Join(',', ids.Select(id =>
            $$"""{"songid":{{id}},"songmid":"mid{{id}}","songname":"Our Song","singer":[{"name":"Our Artist"}],"interval":180}""")) + "]}}}";

    private const string QQLine = """{"lyric":"[00:01.00]Sing along\n[00:04.00]Next line"}""";

    private static FixtureHandler Stub(Dictionary<string, string> replies, Func<HttpRequestMessage, Reply?>? dynamic = null)
    {
        var handler = new FixtureHandler { Fallback = Reply.Text(500, "") };
        foreach (var (address, body) in replies) handler.Set(address, Reply.Text(200, body));
        handler.Responder = dynamic;
        return handler;
    }

    private static IReadOnlyList<string> Paths(FixtureHandler handler) => handler.Addresses;

    private static int Count(FixtureHandler handler, string suffix) => Paths(handler).Count(p => p.EndsWith(suffix, StringComparison.Ordinal));

    // Ported from WordTimedSourcesTests.swift: testQQPrefersQRCWordTiming
    [Fact]
    public async Task QQPrefersQrcWordTiming()
    {
        var handler = Stub(new()
        {
            ["c.y.qq.com/soso/fcgi-bin/client_search_cp"] = QQSearch(7),
            ["c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg"] = Download(QrcCipher(QrcXml(RealQrcOpening))),
        });
        var found = Assert.IsType<RawLyrics>(await new QQMusicSource(handler.Client()).FetchAsync(_query, TestContext.Current.CancellationToken));
        Assert.Equal(LyricSourceId.QQMusic, found.Source);
        Assert.True(HeldLine.WordTimed(found));
        // Word timing needs one request beyond the search.
        Assert.Equal(["c.y.qq.com/soso/fcgi-bin/client_search_cp", "c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg"], Paths(handler));
        Assert.Equal(HttpMethod.Post, handler.Requests[^1].Method);
    }

    // Ported from WordTimedSourcesTests.swift: testQQFallsBackToLineLRCWhenThereIsNoQRC
    [Fact]
    public async Task QQFallsBackToLineLrcWhenThereIsNoQrc()
    {
        var handler = Stub(new()
        {
            ["c.y.qq.com/soso/fcgi-bin/client_search_cp"] = QQSearch(7),
            ["c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg"] = Download("not encrypted"),
            ["c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg"] = QQLine,
        });
        var found = Assert.IsType<RawLyrics>(await new QQMusicSource(handler.Client()).FetchAsync(_query, TestContext.Current.CancellationToken));
        Assert.Equal(2, Assert.IsType<LyricResult.Synced>(LyricParser.Parse(found)).Lines.Count(l => l.Text.Length > 0));
        Assert.False(HeldLine.WordTimed(found));
        // A failed QRC request is not an outage either.
        var again = Stub(new()
        {
            ["c.y.qq.com/soso/fcgi-bin/client_search_cp"] = QQSearch(7),
            ["c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg"] = QQLine,
        });
        Assert.NotNull(await new QQMusicSource(again.Client()).FetchAsync(_query, TestContext.Current.CancellationToken));
    }

    // Ported from WordTimedSourcesTests.swift: testQQUsesTheLineLRCInTheDownloadReplyWithoutAnotherRequest
    [Fact]
    public async Task QQUsesTheLineLrcInTheDownloadReplyWithoutAnotherRequest()
    {
        var handler = Stub(new()
        {
            ["c.y.qq.com/soso/fcgi-bin/client_search_cp"] = QQSearch(7),
            ["c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg"] = Download(QrcCipher("[00:01.00]Sing along\n[00:04.00]Next line")),
        });
        var found = Assert.IsType<RawLyrics>(await new QQMusicSource(handler.Client()).FetchAsync(_query, TestContext.Current.CancellationToken));
        Assert.Equal(2, Assert.IsType<LyricResult.Synced>(LyricParser.Parse(found)).Lines.Count(l => l.Text.Length > 0));
        Assert.DoesNotContain("c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg", Paths(handler));
    }

    // Ported from WordTimedSourcesTests.swift: testQQPrefersALaterHitWithQRCOverAnEarlierLineOnlyHit
    [Fact]
    public async Task QQPrefersALaterHitWithQrcOverAnEarlierLineOnlyHit()
    {
        string qrc = Download(QrcCipher(QrcXml(RealQrcOpening))), line = Download(QrcCipher("[00:01.00]Sing along\n[00:04.00]Next line"));
        var downloads = 0;
        var handler = Stub(new() { ["c.y.qq.com/soso/fcgi-bin/client_search_cp"] = QQSearch(7, 8, 9) }, request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("lyric_download.fcg", StringComparison.Ordinal)) return null;
            // Hit 7 has line LRC only, hit 8 has QRC.
            return Reply.Text(200, Interlocked.Increment(ref downloads) == 1 ? line : qrc);
        });
        var found = Assert.IsType<RawLyrics>(await new QQMusicSource(handler.Client()).FetchAsync(_query, TestContext.Current.CancellationToken));
        Assert.True(HeldLine.WordTimed(found));
        Assert.Equal(2, Count(handler, "lyric_download.fcg")); // stops at the first hit with QRC
    }

    private Dictionary<string, string> KuwoStubs() => new()
    {
        ["search.kuwo.cn/r.s"] = """{"abslist":[{"MUSICRID":"MUSIC_123","SONGNAME":"Our Song","ARTIST":"Our Artist","DURATION":"180"}]}""",
        ["www.kuwo.cn/openapi/v1/www/lyric/getlyric"] = """{"code":200,"data":{"lrclist":[{"time":"1.0","lineLyric":"Sing along"},{"time":"4.0","lineLyric":"Next line"}]}}""",
    };

    // Ported from WordTimedSourcesTests.swift: testKuwoPrefersLrcxWordTiming
    [Fact]
    public async Task KuwoPrefersLrcxWordTiming()
    {
        var reply = Lrcx(LrcxSample);
        var handler = Stub(KuwoStubs(), request => request.RequestUri!.Host == "mlyric.kuwo.cn" ? new Reply(200, reply) : null);
        var found = Assert.IsType<RawLyrics>(await new KuwoSource(handler.Client()).FetchAsync(_query, TestContext.Current.CancellationToken));
        Assert.Equal(LyricSourceId.Kuwo, found.Source);
        Assert.True(HeldLine.WordTimed(found));
        Assert.DoesNotContain("www.kuwo.cn/openapi/v1/www/lyric/getlyric", Paths(handler));
        var asked = handler.Requests.First(r => r.RequestUri!.Host == "mlyric.kuwo.cn");
        Assert.Equal("123", FixtureHandler.QueryOf(asked.RequestUri!, "rid"));
    }

    // Ported from WordTimedSourcesTests.swift: testKuwoFallsBackToTheLineList
    [Fact]
    public async Task KuwoFallsBackToTheLineList()
    {
        var stubs = KuwoStubs();
        stubs["mlyric.kuwo.cn/mobi.s"] = "<html>blocked</html>";
        var handler = Stub(stubs);
        var found = Assert.IsType<RawLyrics>(await new KuwoSource(handler.Client()).FetchAsync(_query, TestContext.Current.CancellationToken));
        Assert.Equal(2, Assert.IsType<LyricResult.Synced>(LyricParser.Parse(found)).Lines.Count(l => l.Text.Length > 0));
        Assert.False(HeldLine.WordTimed(found));
    }

    // Ported from WordTimedSourcesTests.swift: testKuwoAndQQAdvertiseWordTiming
    [Fact]
    public void KuwoAndQQAdvertiseWordTiming()
    {
        Assert.Equal("Word or line timing", LyricSourceId.QQMusic.TimingDescription());
        Assert.Equal("Word or line timing", LyricSourceId.Kuwo.TimingDescription());
        Assert.Equal(3, LyricSourceId.Kuwo.TimingCeiling());
        Assert.Equal(3, LyricSourceId.QQMusic.TimingCeiling());
    }
}
