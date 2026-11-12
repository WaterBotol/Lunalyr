using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

/// <summary>QQ Music, Kugou, Kuwo and lyrics.ovh against recorded replies.</summary>
public class CatalogueSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LyricQuery Qingtian = new("晴天", "周杰伦", null, 269);
    private static readonly LyricQuery Lemon = new("Lemon", "米津玄師", null, 256);
    private static readonly LyricQuery ShapeOfYou = new("Shape of You", "Ed Sheeran", null, 234);

    [Fact]
    public async Task QQMusicFetchesTheHitThatIsTheSong()
    {
        var handler = new FixtureHandler()
            .Set("c.y.qq.com/soso/fcgi-bin/client_search_cp", Reply.Fixture("qq/search-qingtian.json"))
            .Set("c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg", Reply.Fixture("qq/lyric.json"));
        var found = await new QQMusicSource(handler.Client()).FetchAsync(Qingtian, Ct);
        Assert.NotNull(found);
        Assert.Equal(LyricFormat.Lrc, found.Format);
        // Entities are decoded; QQ's empty translation is not passed on.
        Assert.Contains("[00:32.71]It's a made-up line", found.Payload, StringComparison.Ordinal);
        Assert.False(found.Extras!.ContainsKey(RawLyricsExtras.TranslationLrc));
        Assert.Equal(("晴天", "周杰伦", 269.0), (found.ReportedTitle, found.ReportedArtist, found.ReportedDuration));
        Assert.Equal("晴天 周杰伦", handler.Query(0, "w"));
        Assert.Equal("0039MnYb0qxYhV", handler.Query(1, "songmid"));
        Assert.Equal("1", handler.Query(1, "nobase64"));
        Assert.All(handler.Requests, r => Assert.Equal("https://y.qq.com/", r.Headers.Referrer?.ToString()));
    }

    [Fact]
    public async Task QQMusicDecodesBase64AndTriesTheNextSongWhenOneHasNoLyrics()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01.00]Sing along\n[00:04.00]Next line"));
        var handler = new FixtureHandler
        {
            Responder = request => FixtureHandler.QueryOf(request.RequestUri!, "songmid") switch
            {
                "0039MnYb0qxYhV" => Reply.Text(200, """{"retcode":0,"code":0,"lyric":""}"""),
                "004OmEt53sLWcn" => Reply.Text(200, $$"""{"retcode":0,"code":0,"lyric":"{{encoded}}","trans":"[00:01.00]Translated"}"""),
                null => Reply.Fixture("qq/search-qingtian.json"),
                _ => Reply.Text(200, """{"code":0,"lyric":""}"""),
            },
        };
        var found = await new QQMusicSource(handler.Client()).FetchAsync(Qingtian, Ct);
        Assert.NotNull(found);
        Assert.Equal("[00:01.00]Sing along\n[00:04.00]Next line", found.Payload);
        Assert.Equal(268, found.ReportedDuration);
        Assert.Equal("[00:01.00]Translated", found.Extras![RawLyricsExtras.TranslationLrc]);
    }

    [Fact]
    public async Task QQMusicErrorsAreOutages()
    {
        var handler = new FixtureHandler().Set("c.y.qq.com/soso/fcgi-bin/client_search_cp", Reply.Text(200, "callback({})"));
        var error = await Assert.ThrowsAsync<LyricSourceException>(() => new QQMusicSource(handler.Client()).FetchAsync(Qingtian, Ct));
        Assert.Equal(LyricSourceFailure.BadResponse, error.Failure);
    }

    private static FixtureHandler KugouRecorded() => new FixtureHandler()
        .Set("mobileservice.kugou.com/api/v3/search/song", Reply.Fixture("kugou/search-lemon.json"))
        .Set("lyrics.kugou.com/search", Reply.Fixture("kugou/lyric-search.json"))
        .Set("lyrics.kugou.com/download", Reply.Fixture("kugou/download-krc.json"));

    [Fact]
    public async Task KugouDecryptsWordTimedKrc()
    {
        var handler = KugouRecorded();
        var found = await new KugouSource(handler.Client()).FetchAsync(Lemon, Ct);
        Assert.NotNull(found);
        Assert.Equal(LyricFormat.Krc, found.Format);
        Assert.Contains("[851,5000]<0,600,0>Invented <600,700,0>first", found.Payload, StringComparison.Ordinal);
        Assert.Equal(("Lemon", "米津玄師", 255.0), (found.ReportedTitle, found.ReportedArtist, found.ReportedDuration));
        Assert.Equal("da60f11f69affc7107adede9dd17352a", found.Extras![RawLyricsExtras.SongId]);
        Assert.Equal(["mobileservice.kugou.com/api/v3/search/song", "lyrics.kugou.com/search", "lyrics.kugou.com/download"], handler.Addresses);
        Assert.Equal("256000", handler.Query(1, "duration"));
        Assert.Equal("221524943", handler.Query(2, "id"));
    }

    [Fact]
    public async Task KugouFallsBackToLrcWithoutItsByteOrderMark()
    {
        var handler = KugouRecorded();
        handler.Responder = request => FixtureHandler.QueryOf(request.RequestUri!, "fmt") switch
        {
            "krc" => Reply.Text(200, """{"status":200,"content":""}"""),
            "lrc" => Reply.Fixture("kugou/download-lrc.json"),
            _ => null,
        };
        var found = await new KugouSource(handler.Client()).FetchAsync(Lemon, Ct);
        Assert.NotNull(found);
        Assert.Equal(LyricFormat.Lrc, found.Format);
        Assert.StartsWith("[id:$00000000]", found.Payload, StringComparison.Ordinal);
        Assert.Contains("[00:00.85]Invented first line", found.Payload, StringComparison.Ordinal);
    }

    // Windows addition: asked in traditional script, Kugou lists the song in simplified script (reply recorded 13 Nov
    // 2026 for "晴天 周杰倫"). Before folding, every hit failed the artist check and nothing was fetched.
    [Fact]
    public async Task KugouFindsATraditionalScriptArtistUnderItsSimplifiedListing()
    {
        var handler = KugouRecorded().Set("mobileservice.kugou.com/api/v3/search/song", Reply.Fixture("kugou/search-qingtian-traditional.json"));
        var found = await new KugouSource(handler.Client()).FetchAsync(new LyricQuery("晴天", "周杰倫", "葉惠美", 269.747), Ct);
        Assert.NotNull(found);
        Assert.Equal(LyricFormat.Krc, found.Format);
        Assert.Equal(("晴天", "周杰伦", 269.0), (found.ReportedTitle, found.ReportedArtist, found.ReportedDuration));
        Assert.Equal("晴天 周杰倫", handler.Query(0, "keyword"));
        Assert.Equal("b3a52a7a958bf0aed0ebfba2e9a818b7", handler.Query(1, "hash"));
    }

    [Fact]
    public async Task QQMusicFindsATraditionalScriptArtistUnderItsSimplifiedListing()
    {
        var handler = new FixtureHandler()
            .Set("c.y.qq.com/soso/fcgi-bin/client_search_cp", Reply.Fixture("qq/search-qingtian.json"))
            .Set("c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg", Reply.Fixture("qq/lyric.json"));
        var found = await new QQMusicSource(handler.Client()).FetchAsync(new LyricQuery("晴天", "周杰倫", null, 269), Ct);
        Assert.NotNull(found);
        Assert.Equal("0039MnYb0qxYhV", handler.Query(1, "songmid"));
    }

    [Fact]
    public async Task KugouWithoutAFittingSongAsksNoFurther()
    {
        var handler = KugouRecorded();
        Assert.Null(await new KugouSource(handler.Client()).FetchAsync(new LyricQuery("Flamingo", "米津玄師", null, 230), Ct));
        Assert.Equal(["mobileservice.kugou.com/api/v3/search/song"], handler.Addresses);
    }

    [Fact]
    public async Task KuwoWritesItsLineListAsLrc()
    {
        var handler = new FixtureHandler()
            .Set("search.kuwo.cn/r.s", Reply.Fixture("kuwo/search-qingtian.json"))
            .Set("www.kuwo.cn/openapi/v1/www/lyric/getlyric", Reply.Fixture("kuwo/lyric.json"));
        var found = await new KuwoSource(handler.Client()).FetchAsync(Qingtian, Ct);
        Assert.NotNull(found);
        Assert.Equal("228908", handler.Query(1, "musicId"));
        Assert.Equal("晴天 周杰伦", handler.Query(0, "all"));
        Assert.Equal(LyricFormat.Lrc, found.Format);
        Assert.Equal("[00:00.000]晴天 - 周杰伦 (Jay Chou)\n[00:29.260]Invented first line\n[01:00.000]Second made-up line\n[01:02.500]Third & last",
            found.Payload);
        Assert.Equal(269, found.ReportedDuration);
    }

    [Fact]
    public async Task LyricsOvhIsAddressedByArtistAndTitle()
    {
        var handler = new FixtureHandler().Set("api.lyrics.ovh/v1/Ed%20Sheeran/Shape%20of%20You", Reply.Fixture("lyricsovh/shape-of-you.json"));
        var found = await new LyricsOvhSource(handler.Client()).FetchAsync(ShapeOfYou, Ct);
        Assert.NotNull(found);
        Assert.Equal("/v1/Ed%20Sheeran/Shape%20of%20You", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(LyricFormat.Plain, found.Format);
        Assert.Equal("Paroles de la chanson Shape of You par Ed Sheeran\n\nInvented first line\nSecond made-up line\n\nThird invented line", found.Payload);
        Assert.Equal(("Shape of You", "Ed Sheeran", null), (found.ReportedTitle, found.ReportedArtist, found.ReportedDuration));
    }

    [Fact]
    public async Task LyricsOvhMissesAndOutages()
    {
        var handler = new FixtureHandler { Fallback = Reply.Text(404, """{"error":"No lyrics found"}""") };
        var source = new LyricsOvhSource(handler.Client());
        Assert.Null(await source.FetchAsync(ShapeOfYou, Ct));
        // Without an artist there is nothing to ask.
        Assert.Null(await source.FetchAsync(new LyricQuery("Shape of You", "", null, 234), Ct));
        Assert.Single(handler.Requests);
        handler.Fallback = Reply.Text(200, """{"lyrics":"  \n "}""");
        Assert.Null(await source.FetchAsync(ShapeOfYou, Ct));
        handler.Fallback = Reply.Text(502, "Bad gateway");
        await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(ShapeOfYou, Ct));
    }
}
