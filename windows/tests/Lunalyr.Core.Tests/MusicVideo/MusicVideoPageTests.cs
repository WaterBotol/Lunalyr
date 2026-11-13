using Lunalyr.Core.MusicVideo;
using static Lunalyr.Core.Tests.MusicVideo.Sources;

namespace Lunalyr.Core.Tests.MusicVideo;

// Windows' own: YouTube's results page as recorded from youtube.com (Fixtures/YouTube), and the guardrail. A consent page,
// a bot check, a sign-in wall, a captcha, a redirect or a page without ytInitialData is "unavailable": never worked
// around, never retried in a loop, and the owner's key (if any) is used instead. Nothing here reaches the network.
public sealed class MusicVideoPageTests
{
    private readonly MusicVideoWorld _world = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Serve(FakeHttp http, string fixture, int status = 200) =>
        http.ByPath[PageFixtures.Path] = new FakeHttp.Reply(status, PageFixtures.Recorded(fixture));

    // ---- Recorded pages ----

    [Fact]
    public void RecordedPagesAreReadInPageOrder()
    {
        var shape = YouTubeSearchPage.Results(PageFixtures.RecordedText("results-ed-sheeran-shape-of-you.html"));
        Assert.Equal(19, shape.Count);
        Assert.Equal(new YouTubeVideoResult("JGwWNGJdvx8", "Ed Sheeran - Shape of You (Official Music Video)", "Ed Sheeran", true, 264, "6.8B", "9y ago"),
            shape[0]);
        Assert.Equal(19, shape.Select(r => r.VideoId).Distinct().Count());
        Assert.All(shape, r => Assert.True(YouTubeVideoId.IsValid(r.VideoId)));

        var idol = YouTubeSearchPage.Results(PageFixtures.RecordedText("results-yoasobi-idol.html"));
        Assert.Equal(18, idol.Count);
        Assert.Equal(("ZRtdQ81jPUQ", "YOASOBI「アイドル」 Official Music Video", true, 226.0),
            (idol[0].VideoId, idol[0].Title, idol[0].VerifiedArtist, idol[0].Duration));

        var blinding = YouTubeSearchPage.Results(PageFixtures.RecordedText("results-the-weeknd-blinding-lights-official-music-video.html"));
        Assert.Equal(18, blinding.Count);
        Assert.Equal(["4NRXx6U8ABQ", "fHI8X4OXluQ"], blinding.Take(2).Select(r => r.VideoId));
        // A plain verified tick is not the artist badge.
        Assert.False(idol.Single(r => r.VideoId == "JxvvUblf25A").VerifiedArtist);
    }

    [Fact]
    public void TheOfficialVideoIsPickedFromRecordedPages()
    {
        string? Official(string fixture, string title, string artist, double duration) =>
            YouTubeSearch.OfficialVideo(YouTubeSearchPage.Results(PageFixtures.RecordedText(fixture)), title, artist, duration)?.VideoId;
        Assert.Equal("4NRXx6U8ABQ", Official("results-the-weeknd-blinding-lights-official-music-video.html", "Blinding Lights", "The Weeknd", 200));
        Assert.Equal("JGwWNGJdvx8", Official("results-ed-sheeran-shape-of-you.html", "Shape of You", "Ed Sheeran", 234));
        Assert.Equal("ZRtdQ81jPUQ", Official("results-yoasobi-idol.html", "アイドル", "YOASOBI", 213));
    }

    [Fact]
    public async Task TheAutomaticLookupReadsARecordedPageWithOneRequestAndNoKey()
    {
        var http = new FakeHttp();
        Serve(http, "results-the-weeknd-blinding-lights-official-music-video.html");
        var resolver = _world.Resolver(http: http, key: MusicVideoWorld.ApiKey);
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), await resolver.ResolveAsync(_world.Song, online: true, Ct));
        var url = Assert.Single(http.Urls);
        Assert.Equal("https://www.youtube.com/results?search_query=The%20Weeknd%20Blinding%20Lights%20official%20music%20video", url.AbsoluteUri);
        Assert.False(http.Headers[0].ContainsKey(YouTubeDataApi.KeyHeader), "the page is never sent the key");
        Assert.Empty(http.Headers[0]);
    }

    // ---- The guardrail ----

    [Theory]
    [InlineData("consent.html", YouTubeWall.Consent)]
    [InlineData("bot-check.html", YouTubeWall.BotCheck)]
    [InlineData("captcha.html", YouTubeWall.Captcha)]
    public void WallPagesAreUnavailable(string fixture, YouTubeWall wall)
    {
        var reply = new MusicVideoHttpReply(200, PageFixtures.Recorded(fixture));
        Assert.Equal(new YouTubePageReply.Unavailable(wall), YouTubeSearchPage.Inspect(reply, YouTubeSearchPage.Results(PageFixtures.RecordedText(fixture))));
    }

    [Fact]
    public void RepliesAreToldApart()
    {
        static YouTubePageReply Inspect(int status, string body, string? location = null) =>
            YouTubeSearchPage.Inspect(new MusicVideoHttpReply(status, System.Text.Encoding.UTF8.GetBytes(body),
                location is null ? null : new Dictionary<string, string> { ["Location"] = location }),
                status == 200 ? YouTubeSearchPage.Results(body) : []);
        // A real results page mentions sign-in, consent and captcha in its scripts: its videos make it results.
        var recorded = PageFixtures.RecordedText("results-ed-sheeran-shape-of-you.html").Replace("</body>",
            "<script>var consent='https://consent.youtube.com'; var signIn='Sign in to confirm'; var captcha=1;</script></body>", StringComparison.Ordinal);
        Assert.Equal(19, Assert.IsType<YouTubePageReply.Results>(Inspect(200, recorded)).Videos.Count);
        // Results data without videos is "no videos" (the Mac's rule), not a wall.
        Assert.Empty(Assert.IsType<YouTubePageReply.Results>(Inspect(200, "<script>var ytInitialData = {\"contents\":{}};</script>")).Videos);
        Assert.Equal(new YouTubePageReply.Unavailable(YouTubeWall.NoData), Inspect(200, "<html>Something else</html>"));
        // Redirects are never followed: where they point says what they are.
        Assert.Equal(new YouTubePageReply.Unavailable(YouTubeWall.Consent), Inspect(302, "", "https://consent.youtube.com/m?continue=x"));
        Assert.Equal(new YouTubePageReply.Unavailable(YouTubeWall.SignIn), Inspect(303, "", "https://accounts.google.com/ServiceLogin"));
        Assert.Equal(new YouTubePageReply.Unavailable(YouTubeWall.BotCheck), Inspect(302, "", "https://www.google.com/sorry/index?continue=x"));
        Assert.Equal(new YouTubePageReply.Unavailable(YouTubeWall.Redirect), Inspect(301, "", "https://m.youtube.com/results"));
        Assert.Equal(new YouTubePageReply.Unavailable(YouTubeWall.BotCheck), Inspect(403, ""));
        Assert.Equal(new YouTubePageReply.Failed(404), Inspect(404, "Not found"));
        Assert.Equal(new YouTubePageReply.Failed(500), Inspect(500, ""));
    }

    [Theory]
    [InlineData("consent.html")]
    [InlineData("bot-check.html")]
    [InlineData("captcha.html")]
    public async Task AWallIsNeverWorkedAroundOrRetriedInALoop(string fixture)
    {
        var http = new FakeHttp();
        Serve(http, fixture);
        var resolver = _world.Resolver(http: http);
        var outcome = Assert.IsType<MusicVideoSearchOutcome.Failed>(await resolver.SearchResultsAsync("The Weeknd Blinding Lights", Ct));
        Assert.True(outcome.Unavailable);
        Assert.Single(http.Urls);
        Assert.Empty(http.Headers[0]);
        Assert.True(resolver.PageUnavailable);

        // Every search and lookup for a while: nothing sent, the calm note, nothing cached.
        _world.Time += 60;
        Assert.True(Assert.IsType<MusicVideoSearchOutcome.Failed>(await resolver.SearchResultsAsync("x", Ct)).Unavailable);
        Assert.Equal(Missing(MusicVideoMiss.LookupFailed), await resolver.ResolveAsync(_world.Song, online: true, Ct));
        Assert.Single(http.Urls);
        Assert.Null(resolver.Cache.Lookup(_world.Song.Key, MusicVideoWorld.Start));
        Assert.Equal("YouTube’s search isn’t available right now. Showing Line patterns instead. You can paste a YouTube link for it below.",
            MusicVideoController.Note(new MusicVideoStatus.Missing(MusicVideoMiss.LookupFailed, "X"), true, "Line patterns", resolver.PageUnavailable));

        // Once the stand-down has passed, the page is asked once more (a single request, as for any search).
        _world.Time += MusicVideoResolver.PageStandDown;
        Assert.False(resolver.PageUnavailable);
        MusicVideoWorld.ServeOfficialPage(http);
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), await resolver.ResolveAsync(_world.Song, online: true, Ct));
        Assert.Equal(2, http.Urls.Count);
    }

    [Fact]
    public async Task AnAutomaticLookupThatHitsAWallIsAFailureAndTheControllerSendsNothingMore()
    {
        var http = new FakeHttp();
        Serve(http, "consent.html");
        var resolver = _world.Resolver(http: http);
        var controller = _world.Controller(resolver);
        controller.RetryDelay = TimeSpan.Zero;
        controller.Update(_world.Track, enabled: true, online: true);
        await controller.SettleAsync();
        for (var i = 0; i < 50; i++) await Task.Delay(10, Ct);
        await controller.SettleAsync();
        Assert.Equal(new MusicVideoStatus.Missing(MusicVideoMiss.LookupFailed, "Blinding Lights"), controller.Status);
        Assert.True(controller.YouTubeSearchUnavailable);
        Assert.Single(http.Urls);
    }

    // ---- The key as the fallback, and by choice ----

    [Fact]
    public async Task WithAKeyAWallFallsBackToTheDataApiOnce()
    {
        var http = new FakeHttp();
        Serve(http, "bot-check.html");
        MusicVideoWorld.ServeApiOfficial(http);
        var resolver = _world.Resolver(http: http, key: MusicVideoWorld.ApiKey);
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), await resolver.ResolveAsync(_world.Song, online: true, Ct));
        Assert.Equal([PageFixtures.Path, ApiFixtures.SearchPath, ApiFixtures.VideosPath], http.Paths);
        Assert.Empty(http.Headers[0]);
        Assert.Equal(MusicVideoWorld.ApiKey, http.Headers[1][YouTubeDataApi.KeyHeader]);
        Assert.Equal("The Weeknd Blinding Lights official music video", FakeHttp.QueryOf(http.Urls[1]));

        // While the page stands down, the key is used straight away: the page isn't asked.
        _world.Time += 10;
        var found = Assert.IsType<MusicVideoSearchOutcome.Results>(await resolver.SearchResultsAsync("The Weeknd Blinding Lights", Ct));
        Assert.Equal(2, found.Videos.Count);
        Assert.Single(http.Paths, p => p == PageFixtures.Path);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    public async Task APageThatFailsOrIsBusyFallsBackToTheKey(int status)
    {
        var http = new FakeHttp();
        http.ByPath[PageFixtures.Path] = new FakeHttp.Reply(status, []);
        MusicVideoWorld.ServeApiOfficial(http);
        var resolver = _world.Resolver(http: http, key: MusicVideoWorld.ApiKey);
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), await resolver.ResolveAsync(_world.Song, online: true, Ct));
        Assert.Equal(PageFixtures.Path, http.Paths.First());
        Assert.Contains(ApiFixtures.SearchPath, http.Paths);
        Assert.False(resolver.PageUnavailable, "busy or failing is not a wall");
    }

    [Fact]
    public async Task WithoutAKeyAFailedPageSendsNothingElse()
    {
        var http = new FakeHttp();
        Serve(http, "consent.html");
        MusicVideoWorld.ServeApiOfficial(http);
        var resolver = _world.Resolver(http: http);
        Assert.Equal(Missing(MusicVideoMiss.LookupFailed), await resolver.ResolveAsync(_world.Song, online: true, Ct));
        Assert.Equal([PageFixtures.Path], http.Paths);
    }

    [Fact]
    public async Task UseMyApiKeyNeverAsksThePage()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeOfficialPage(http);
        MusicVideoWorld.ServeApiOfficial(http);
        var resolver = _world.ApiResolver(http);
        Assert.Equal(MusicVideoSearchMode.ApiKey, resolver.Mode);
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), await resolver.ResolveAsync(_world.Song, online: true, Ct));
        Assert.DoesNotContain(PageFixtures.Path, http.Paths);
        _world.Time += 10;
        Assert.IsType<MusicVideoSearchOutcome.Results>(await resolver.SearchResultsAsync("x", Ct));
        Assert.DoesNotContain(PageFixtures.Path, http.Paths);
        Assert.Empty(resolver.RateLimit.Sent);
    }

    [Fact]
    public async Task TheKeysQuotaNeverStopsThePage()
    {
        var http = new FakeHttp();
        http.ByPath[PageFixtures.Path] = new FakeHttp.Reply(500, []);
        http.ByPath[ApiFixtures.SearchPath] = new FakeHttp.Reply(403, ApiFixtures.Error(403, "quotaExceeded"));
        var resolver = _world.Resolver(http: http, key: MusicVideoWorld.ApiKey);
        Assert.Equal(Missing(MusicVideoMiss.QuotaExceeded), await resolver.ResolveAsync(_world.Song, online: true, Ct));
        Assert.True(resolver.ApiRateLimit.BlockedUntil > _world.Time + 3600);
        Assert.Equal(0, resolver.RateLimit.BlockedUntil);
        MusicVideoWorld.ServeOfficialPage(http);
        _world.Time += 10;
        var asked = http.Paths.Count(p => p == PageFixtures.Path);
        var found = Assert.IsType<MusicVideoSearchOutcome.Results>(await resolver.SearchResultsAsync("The Weeknd Blinding Lights", Ct));
        Assert.Equal(2, found.Videos.Count);
        Assert.Equal(asked + 1, http.Paths.Count(p => p == PageFixtures.Path));
    }

    [Fact]
    public async Task ThePageKeepsTheMacsRateLimit()
    {
        var resolver = _world.Resolver();
        Assert.Equal((2.0, 60.0, 8, 60.0, 3600.0),
            (resolver.RateLimit.MinimumGap, resolver.RateLimit.Window, resolver.RateLimit.MaximumPerWindow, resolver.RateLimit.DefaultBackOff,
                resolver.RateLimit.LongestBackOff));
        var http = new FakeHttp();
        MusicVideoWorld.ServeOfficialPage(http);
        var spaced = _world.Resolver(http: http);
        for (var i = 0; i < 8; i++) await spaced.SearchResultsAsync($"query {i}", Ct);
        Assert.Equal(8, http.Urls.Count);
        // Two seconds apart (the fake clock moves by each wait), and the ninth within the minute isn't sat out.
        Assert.Equal(14, _world.Time, 9);
        Assert.IsType<MusicVideoSearchOutcome.RateLimited>(await spaced.SearchResultsAsync("query 8", Ct));
        Assert.Equal(8, http.Urls.Count);
    }
}
