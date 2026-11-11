using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

public class NetEaseSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LyricQuery Lemon = new("Lemon", "米津玄師", null, 256);

    private const string AmllFile = "raw.githubusercontent.com/amll-dev/amll-ttml-db/refs/heads/main/ncm-lyrics/536622304.ttml";
    private const string AmllMirror = "raw.githubusercontent.com/Steve-xmh/amll-ttml-db/refs/heads/main/ncm-lyrics/536622304.ttml";

    private static FixtureHandler Recorded() => new FixtureHandler()
        .Set("music.163.com/api/search/get", Reply.Fixture("netease/search-lemon.json"))
        .Set("music.163.com/api/song/lyric", Reply.Fixture("netease/lyric-lemon.json"));

    [Fact]
    public async Task TheSearchHitThatIsTheSongBringsItsLyricsReadingsAndTranslation()
    {
        var handler = Recorded();
        var found = await new NetEaseSource(handler.Client()).FetchAsync(Lemon, Ct);
        Assert.NotNull(found);
        Assert.Equal(LyricFormat.Lrc, found.Format);
        Assert.Contains("[00:00.851]Invented first line", found.Payload, StringComparison.Ordinal);
        Assert.Equal(("Lemon", "米津玄師", 256.0), (found.ReportedTitle, found.ReportedArtist, found.ReportedDuration));
        Assert.Equal("536622304", found.Extras![RawLyricsExtras.SongId]);
        Assert.StartsWith("[00:00.851]in ven ted", found.Extras[RawLyricsExtras.RomanizationLrc], StringComparison.Ordinal);
        Assert.StartsWith("[00:00.851]Translated", found.Extras[RawLyricsExtras.TranslationLrc], StringComparison.Ordinal);
        Assert.Equal(["music.163.com/api/search/get", "music.163.com/api/song/lyric"], handler.Addresses);
        Assert.Equal("Lemon 米津玄師", handler.Query(0, "s"));
        Assert.Equal("1", handler.Query(0, "type"));
        Assert.Equal("8", handler.Query(0, "limit"));
        Assert.Equal("536622304", handler.Query(1, "id"));
        Assert.All(["lv", "tv", "rv", "yv"], name => Assert.Equal("-1", handler.Query(1, name)));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("https://music.163.com/", request.Headers.Referrer?.ToString());
            Assert.Equal(LyricHttpOptions.DefaultUserAgent, string.Join(' ', request.Headers.GetValues("User-Agent")));
        });
    }

    [Fact]
    public async Task WordTimedYrcIsPreferredWithTheLineLrcAlongside()
    {
        var handler = Recorded().Set("music.163.com/api/song/lyric", Reply.Fixture("netease/lyric-yrc.json"));
        var found = await new NetEaseSource(handler.Client()).FetchAsync(Lemon, Ct);
        Assert.NotNull(found);
        Assert.Equal(LyricFormat.Yrc, found.Format);
        Assert.Contains("[851,5000](851,600,0)Invented", found.Payload, StringComparison.Ordinal);
        Assert.StartsWith("[00:00.851]Invented first line", found.Extras![RawLyricsExtras.Lrc], StringComparison.Ordinal);
        // Empty readings and translations are not passed on.
        Assert.False(found.Extras.ContainsKey(RawLyricsExtras.RomanizationLrc));
    }

    [Fact]
    public async Task CoversAndOtherArtistsDoNotPass()
    {
        // NetEase's catalogue lacks the original here: covers, and a "Jay" whose 112 s edit is not the song.
        var handler = new FixtureHandler().Set("music.163.com/api/search/get", Reply.Fixture("netease/search-qingtian.json"));
        Assert.Null(await new NetEaseSource(handler.Client()).FetchAsync(new LyricQuery("晴天", "周杰伦", null, 269), Ct));
        Assert.Equal(["music.163.com/api/search/get"], handler.Addresses);
        // The library's Latin name for a Japanese artist is not the catalogue's; aliases are the lookup's job.
        Assert.Null(await new NetEaseSource(Recorded().Client()).FetchAsync(new LyricQuery("Lemon", "Kenshi Yonezu", null, 256), Ct));
    }

    [Fact]
    public async Task AnEmptyLyricIsAMissAndBrokenRepliesAreOutages()
    {
        var handler = Recorded().Set("music.163.com/api/song/lyric", Reply.Text(200, """{"nolyric":true,"code":200}"""));
        Assert.Null(await new NetEaseSource(handler.Client()).FetchAsync(Lemon, Ct));
        handler.Set("music.163.com/api/song/lyric", Reply.Text(200, "{\"lrc\":"));
        var error = await Assert.ThrowsAsync<LyricSourceException>(() => new NetEaseSource(handler.Client()).FetchAsync(Lemon, Ct));
        Assert.Equal(LyricSourceFailure.BadResponse, error.Failure);
        handler.Set("music.163.com/api/search/get", Reply.Text(200, "not json"));
        error = await Assert.ThrowsAsync<LyricSourceException>(() => new NetEaseSource(handler.Client()).FetchAsync(Lemon, Ct));
        Assert.Equal(LyricSourceFailure.BadResponse, error.Failure);
    }

    [Fact]
    public async Task AmllReadsTheFileOfTheSongNetEaseFound()
    {
        var handler = Recorded().Set(AmllFile, Reply.Fixture("amll/536622304.ttml"));
        var found = await new AmllSource(handler.Client()).FetchAsync(Lemon, Ct);
        Assert.NotNull(found);
        Assert.Equal(LyricSourceId.Amll, found.Source);
        Assert.Equal(LyricFormat.Ttml, found.Format);
        Assert.Contains("itunes:timing=\"Word\"", found.Payload, StringComparison.Ordinal);
        Assert.Equal(("Lemon", "米津玄師", 256.0), (found.ReportedTitle, found.ReportedArtist, found.ReportedDuration));
        Assert.Equal(["music.163.com/api/search/get", AmllFile], handler.Addresses);
    }

    [Fact]
    public async Task AmllFallsBackToTheMirrorAndAMissingFileIsAMiss()
    {
        var handler = Recorded().Set(AmllFile, Reply.Text(404, "404: Not Found")).Set(AmllMirror, Reply.Fixture("amll/536622304.ttml"));
        Assert.NotNull(await new AmllSource(handler.Client()).FetchAsync(Lemon, Ct));
        handler.Set(AmllMirror, Reply.Broken);
        Assert.Null(await new AmllSource(handler.Client()).FetchAsync(Lemon, Ct));
        // Offline, the outage is reported rather than remembered as a miss.
        var error = await Assert.ThrowsAsync<LyricSourceException>(() =>
            new AmllSource(handler.Client(networkUp: () => false)).FetchAsync(Lemon, Ct));
        Assert.True(error.IsOffline);
    }
}
