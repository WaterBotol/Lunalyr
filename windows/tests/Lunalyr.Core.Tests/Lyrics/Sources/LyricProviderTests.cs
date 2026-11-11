using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

/// <summary>
/// Providers report the recording they matched, and Lyrics+ copes with community mirrors that come and go.
/// Ported from the Mac's LyricProviderTests.swift (the provider parts).
/// </summary>
public class LyricProviderTests
{
    private static readonly LyricQuery OurSong = new("Our Song", "Our Artist", null, 180);

    // Ported from LyricProviderTests.swift: testNetEasePicksTheEditionThatFitsAndReportsIt
    [Fact]
    public void NetEasePicksTheEditionThatFitsAndReportsIt()
    {
        string[] artists = ["Our Artist"];
        NetEaseSearchHit[] songs =
        [
            new(1, "Our Song (Live)", artists, 186_000),
            new(2, "Our Song", artists, 185_000),
            new(3, "Our Song", ["Someone Else"], 180_000),
            new(4, "Our Song", [.. artists, "Guest"], 180_400),
            new(5, "Our Songbook", artists, 180_000),
        ];
        var match = NetEaseCatalog.BestSong(songs, "Our Song", "Our Artist", 180);
        Assert.Equal(new NetEaseSong(4, "Our Song", "Our Artist, Guest", 180.4), match);
        // Editions eight or more seconds apart are not offered at all.
        Assert.Null(NetEaseCatalog.BestSong([songs[0]], "Our Song", "Our Artist", 170));
        // The closest edit within reach is still reported, so ranking can say why it is rejected.
        Assert.Equal(185, NetEaseCatalog.BestSong([songs[1]], "Our Song", "Our Artist", 180)?.Duration);
    }

    private const string Words =
        """{"type":"Word","metadata":{"source":"Apple (via Lyrics+)","title":"Our Song (Remastered)","artist":["Our Artist"]},"lyrics":[{"time":1000,"duration":2000,"text":"Sing along","syllabus":[{"time":1000,"duration":900,"text":"Sing "},{"time":2000,"duration":1000,"text":"along"}]}]}""";

    private const string NotFound = """{"error":"Lyrics not found"}""";

    private static string[] Hosts(int count)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        return [.. Enumerable.Range(0, count).Select(i => $"kpoe-{run}-{i}.test")];
    }

    private static LyricsPlusSource LyricsPlus(FixtureHandler handler, string[] hosts) =>
        new(handler.Client(), new LyricsPlusOptions { Hosts = [.. hosts.Select(h => "https://" + h)] });

    // Ported from LyricProviderTests.swift: testANodesNotFoundIsCheckedWithOneOtherMirror
    [Fact]
    public async Task ANodesNotFoundIsCheckedWithOneOtherMirror()
    {
        var names = Hosts(3);
        var handler = new FixtureHandler()
            .Set(names[0] + "/v2/lyrics/get", Reply.Text(404, NotFound))
            .Set(names[1] + "/v2/lyrics/get", Reply.Text(200, Words))
            .Set(names[2] + "/v2/lyrics/get", Reply.Text(200, Words));
        var source = LyricsPlus(handler, names);
        // The first node is up but has lost its upstream; the next one serves.
        var hit = await source.FetchAsync(OurSong, TestContext.Current.CancellationToken);
        Assert.NotNull(hit);
        Assert.Equal(LyricFormat.KPoeJson, hit.Format);
        Assert.Contains("\"Sing \"", hit.Payload, StringComparison.Ordinal);
        Assert.Equal(0, handler.Count(names[2]));
        // Contradicted, the empty-handed node rests: the serving one is asked first.
        await source.FetchAsync(OurSong, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Count(names[0]));
        Assert.Equal(2, handler.Count(names[1]));
    }

    // Ported from LyricProviderTests.swift: testNotFoundEverywhereEndsAfterOneSecondOpinion
    [Fact]
    public async Task NotFoundEverywhereEndsAfterOneSecondOpinion()
    {
        var names = Hosts(3);
        var handler = new FixtureHandler();
        foreach (var name in names)
        {
            handler.Set(name + "/v2/lyrics/get", Reply.Text(404, NotFound));
        }
        var hit = await LyricsPlus(handler, names).FetchAsync(OurSong, TestContext.Current.CancellationToken);
        Assert.Null(hit);
        Assert.Equal([1, 1, 0], names.Select(handler.Count));
    }

    // Ported from LyricProviderTests.swift: testDeadMirrorsRestAndMatchedRecordingIsReported
    [Fact]
    public async Task DeadMirrorsRestAndMatchedRecordingIsReported()
    {
        var names = Hosts(2);
        var handler = new FixtureHandler()
            .Set(names[0] + "/v2/lyrics/get", Reply.Text(404, "<html>error code: 1042</html>"))
            .Set(names[1] + "/v2/lyrics/get", Reply.Text(200, Words));
        var source = LyricsPlus(handler, names);
        var hit = await source.FetchAsync(OurSong, TestContext.Current.CancellationToken);
        Assert.NotNull(hit);
        Assert.Equal("Our Song (Remastered)", hit.ReportedTitle);
        Assert.Equal("Our Artist", hit.ReportedArtist);
        Assert.Equal("Apple", hit.Extras![RawLyricsExtras.Upstream]);
        Assert.Contains("\"along\"", hit.Payload, StringComparison.Ordinal);
        // The failed node is tried after the healthy one next time.
        await source.FetchAsync(OurSong, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Count(names[0]));
        Assert.Equal(2, handler.Count(names[1]));
    }

    // Ported from LyricProviderTests.swift: testNoUsableMirrorIsAnErrorRatherThanAMiss
    [Fact]
    public async Task NoUsableMirrorIsAnErrorRatherThanAMiss()
    {
        var names = Hosts(2);
        var error = await Assert.ThrowsAsync<LyricSourceException>(() =>
            LyricsPlus(new FixtureHandler(), names).FetchAsync(OurSong, TestContext.Current.CancellationToken));
        Assert.StartsWith("Lyrics+ mirrors unavailable", error.Message, StringComparison.Ordinal);
    }

    // Ported from LyricProviderTests.swift: testNetEaseOutagesAreErrorsButAMissingSongIsNot
    [Fact]
    public async Task NetEaseOutagesAreErrorsButAMissingSongIsNot()
    {
        var handler = new FixtureHandler();
        var source = new NetEaseSource(handler.Client());
        var ct = TestContext.Current.CancellationToken;
        // Outages throw, so a lookup asks again next time rather than remembering "no lyrics".
        handler.Set("music.163.com/api/search/get", Reply.Text(503, ""));
        await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(OurSong, ct)); // A 503 is an outage
        handler.Set("music.163.com/api/search/get", Reply.Text(200, """{"code":-460,"message":"Cheating"}"""));
        await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(OurSong, ct)); // An error reply is an outage
        handler.Set("music.163.com/api/search/get", Reply.Text(200, """{"result":{"songCount":0},"code":200}"""));
        Assert.Null(await source.FetchAsync(OurSong, ct));
        handler.Set("music.163.com/api/search/get", Reply.Text(200,
            """{"result":{"songs":[{"id":7,"name":"Our Song","artists":[{"name":"Our Artist"}],"duration":181000}]},"code":200}"""));
        handler.Set("music.163.com/api/song/lyric", Reply.Text(200, """{"lrc":{"lyric":"[00:01.00]Sing along\n[00:04.00]Next line"}}"""));
        var found = await source.FetchAsync(OurSong, ct);
        Assert.NotNull(found);
        Assert.Equal(181, found.ReportedDuration);
        Assert.Equal(LyricFormat.Lrc, found.Format);
        Assert.Equal("[00:01.00]Sing along\n[00:04.00]Next line", found.Payload);
        // The recording NetEase matched is reported.
        Assert.Equal("Our Song", found.ReportedTitle);
        Assert.Equal("Our Artist", found.ReportedArtist);
        handler.Set("music.163.com/api/song/lyric", Reply.Text(502, ""));
        await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(OurSong, ct)); // A failed lyric request is an outage
    }

    // Ported from LyricProviderTests.swift: testKugouSearchesItsWorkingHostThenFetchesTheLyrics
    [Fact]
    public async Task KugouSearchesItsWorkingHostThenFetchesTheLyrics()
    {
        // mobilecdn.kugou.com's certificate no longer names it, so the search goes to mobileservice.kugou.com.
        Assert.Equal("mobileservice.kugou.com", new Uri(new KugouOptions().SearchAddress).Host);
        var lrc = Convert.ToBase64String("[ti:Our Song]\n[00:01.00]Sing along\n[00:04.00]Next line"u8);
        var handler = new FixtureHandler()
            .Set("mobileservice.kugou.com/api/v3/search/song", Reply.Text(200,
                """{"status":1,"errcode":0,"data":{"total":1,"info":[{"filename":"Our Artist - Our Song","songname":"Our Song","singername":"Our Artist","duration":180,"hash":"b3a52a7a958bf0aed0ebfba2e9a818b7"}]}}"""))
            .Set("lyrics.kugou.com/search", Reply.Text(200,
                """{"status":200,"errcode":200,"candidates":[{"id":"274944371","accesskey":"2A7B35884B3C20E9D3281686BA59A3F8","duration":180000,"song":"Our Song","singer":"Our Artist"}]}"""))
            .Set("lyrics.kugou.com/download", Reply.Text(200, $$"""{"status":200,"fmt":"lrc","content":"{{lrc}}"}"""));
        var found = await new KugouSource(handler.Client()).FetchAsync(OurSong, TestContext.Current.CancellationToken);
        Assert.NotNull(found);
        Assert.Equal(LyricSourceId.Kugou, found.Source);
        Assert.Equal("Our Song", found.ReportedTitle);
        Assert.Equal(180, found.ReportedDuration);
        Assert.Equal(LyricFormat.Lrc, found.Format);
        Assert.Equal(2, LrcText.Lines(found.Payload).Count(LrcText.HasTimedLine));
        // KRC is asked for first, then plain LRC.
        Assert.Equal(["mobileservice.kugou.com/api/v3/search/song", "lyrics.kugou.com/search", "lyrics.kugou.com/download",
            "lyrics.kugou.com/download"], handler.Addresses);
        Assert.Equal("Our Song Our Artist", handler.Query(0, "keyword"));
        // The lyric search asks for the song the search found, by its hash and length.
        Assert.Equal("b3a52a7a958bf0aed0ebfba2e9a818b7", handler.Query(1, "hash"));
        Assert.Equal("180000", handler.Query(1, "duration"));
        Assert.Equal("2A7B35884B3C20E9D3281686BA59A3F8", handler.Query(2, "accesskey"));
        Assert.Equal("krc", handler.Query(2, "fmt"));
        Assert.Equal("lrc", handler.Query(3, "fmt"));
    }
}
