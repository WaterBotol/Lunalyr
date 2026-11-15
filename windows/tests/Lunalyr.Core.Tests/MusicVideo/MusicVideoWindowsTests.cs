using Lunalyr.Core.MusicVideo;
using static Lunalyr.Core.Tests.MusicVideo.Sources;

namespace Lunalyr.Core.Tests.MusicVideo;

// Windows' own: the YouTube Data API client's replies and failures (the fallback, and "Use my API key"), the key's handling, the frame-by-frame sync of a local
// file, the per-song file choice and the file storage. Fixtures are hand-built in the API's documented shapes; nothing
// reaches YouTube.
public sealed class MusicVideoWindowsTests
{
    private readonly MusicVideoWorld _world = new();

    [Fact]
    public async Task WithoutAKeyNothingIsSentAndTheNoteSaysWhy()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeOfficialPage(http);
        var resolver = _world.Resolver(http: http, key: "  ", mode: MusicVideoSearchMode.ApiKey);
        Assert.False(resolver.CanSearch);
        Assert.Equal(Missing(MusicVideoMiss.NoApiKey), await resolver.ResolveAsync(_world.Song, online: true, TestContext.Current.CancellationToken));
        Assert.Empty(http.Urls);
        Assert.Null(resolver.Cache.Lookup(_world.Song.Key, MusicVideoWorld.Start));
        Assert.Contains("API key", MusicVideoController.Note(new MusicVideoStatus.Missing(MusicVideoMiss.NoApiKey, "X"), true, "Line patterns"));
    }

    [Theory]
    [InlineData(400, "keyInvalid", YouTubeApiFailure.KeyRejected)]
    [InlineData(403, "accessNotConfigured", YouTubeApiFailure.KeyRejected)]
    [InlineData(403, "quotaExceeded", YouTubeApiFailure.QuotaExceeded)]
    [InlineData(403, "dailyLimitExceeded", YouTubeApiFailure.QuotaExceeded)]
    [InlineData(403, "rateLimitExceeded", YouTubeApiFailure.RateLimited)]
    [InlineData(500, "backendError", YouTubeApiFailure.Failed)]
    public void ErrorRepliesAreClassified(int status, string reason, YouTubeApiFailure expected) =>
        Assert.Equal(expected, YouTubeDataApi.Classify(new MusicVideoHttpReply(status, ApiFixtures.Error(status, reason))).Failure);

    [Fact]
    public void TheDetailedReasonWins()
    {
        var body = """{"error":{"code":400,"errors":[{"reason":"badRequest"}],"details":[{"reason":"API_KEY_INVALID"}]}}"""u8.ToArray();
        Assert.Equal("API_KEY_INVALID", YouTubeDataApi.ErrorReason(body));
        Assert.Equal(YouTubeApiFailure.KeyRejected, YouTubeDataApi.Classify(new MusicVideoHttpReply(400, body)).Failure);
    }

    [Fact]
    public async Task ARefusedKeyIsNeitherCachedNorRetriedAndAUsedUpQuotaWaitsForTheReset()
    {
        var http = new FakeHttp();
        http.ByPath[ApiFixtures.SearchPath] = new FakeHttp.Reply(400, ApiFixtures.Error(400, "keyInvalid"));
        var resolver = _world.ApiResolver(http);
        Assert.Equal(Missing(MusicVideoMiss.KeyRejected), await resolver.ResolveAsync(_world.Song, online: true, TestContext.Current.CancellationToken));
        Assert.Null(resolver.Cache.Lookup(_world.Song.Key, MusicVideoWorld.Start));

        http.ByPath[ApiFixtures.SearchPath] = new FakeHttp.Reply(403, ApiFixtures.Error(403, "quotaExceeded"));
        _world.Time += 10;
        Assert.Equal(Missing(MusicVideoMiss.QuotaExceeded), await resolver.ResolveAsync(_world.Song, online: true, TestContext.Current.CancellationToken));
        Assert.True(resolver.ApiRateLimit.BlockedUntil - _world.Time > 3600, "backs off until the quota resets, not a minute");
        var sent = http.Urls.Count;
        _world.Time += 600;
        Assert.Equal(Missing(MusicVideoMiss.RateLimited), await resolver.ResolveAsync(_world.Song with { Title = "Other" }, online: true, TestContext.Current.CancellationToken));
        Assert.Equal(sent, http.Urls.Count);
    }

    [Fact]
    public void TheQuotaResetsAtMidnightPacific()
    {
        // 2027-01-15 08:00 UTC is midnight in Los Angeles (PST): a full day to the next.
        var seconds = YouTubeQuota.SecondsUntilReset(new DateTimeOffset(2027, 1, 15, 8, 0, 1, TimeSpan.Zero));
        Assert.InRange(seconds, 86_000, 86_400);
        Assert.InRange(YouTubeQuota.SecondsUntilReset(new DateTimeOffset(2027, 1, 15, 7, 0, 0, TimeSpan.Zero)), 3540, 3660);
    }

    [Fact]
    public async Task VideosTheApiSaysCantBeEmbeddedAreLeftOut()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeApiSearch(http,
            ("4NRXx6U8ABQ", "The Weeknd - Blinding Lights (Official Video)", "TheWeekndVEVO"),
            ("CCCCCCCCCCC", "The Weeknd - Blinding Lights (Official Music Video)", "The Weeknd"));
        http.ByPath[ApiFixtures.VideosPath] = new FakeHttp.Reply(200, ApiFixtures.Videos(
            ("4NRXx6U8ABQ", "PT3M22S", 5, true), ("CCCCCCCCCCC", "PT3M20S", 5, false)));
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), await _world.ApiResolver(http).ResolveAsync(_world.Song, online: true, TestContext.Current.CancellationToken));
        Assert.Contains("videoEmbeddable=true", http.Urls[0].Query);
        Assert.Contains("type=video", http.Urls[0].Query);
    }

    [Fact]
    public void ApiTitlesAreDecodedAndOnlyVideosKept()
    {
        var results = YouTubeDataApi.ParseSearch("""
            {"items":[{"id":{"kind":"youtube#video","videoId":"4NRXx6U8ABQ"},"snippet":{"title":"Don&#39;t Stop &amp; Go","channelTitle":"A &amp; B",
            "thumbnails":{"default":{"url":"http://i.ytimg.com/x.jpg"}}}},
            {"id":{"kind":"youtube#channel","channelId":"UC"}},{"id":{"kind":"youtube#video","videoId":"bad"}}]}
            """u8.ToArray());
        var only = Assert.Single(results);
        Assert.Equal("Don't Stop & Go", only.Title);
        Assert.Equal("A & B", only.Channel);
    }

    [Theory]
    [InlineData("PT4M22S", 262.0)]
    [InlineData("PT1H2M3S", 3723.0)]
    [InlineData("PT45S", 45.0)]
    [InlineData("P1DT1S", 86401.0)]
    [InlineData("P0D", null)]
    [InlineData("4:22", null)]
    [InlineData("PT", null)]
    [InlineData("PT4X", null)]
    public void Iso8601Lengths(string text, double? expected) => Assert.Equal(expected, YouTubeDataApi.Iso8601Seconds(text));

    [Fact]
    public void ViewsAndAgesAsYouTubeShowsThem()
    {
        Assert.Equal("843 views", YouTubeDataApi.ViewsText(843));
        Assert.Equal("1 view", YouTubeDataApi.ViewsText(1));
        Assert.Equal("1.2K views", YouTubeDataApi.ViewsText(1_299));
        Assert.Equal("12M views", YouTubeDataApi.ViewsText(12_900_000));
        Assert.Equal("1.1B views", YouTubeDataApi.ViewsText(1_190_000_000));
        Assert.Null(YouTubeDataApi.ViewsText(null));
        var now = MusicVideoWorld.Start;
        Assert.Equal("5 days ago", YouTubeDataApi.Age(now.AddDays(-5.5), now));
        Assert.Equal("1 month ago", YouTubeDataApi.Age(now.AddDays(-40), now));
        Assert.Equal("1 hour ago", YouTubeDataApi.Age(now.AddMinutes(-61), now));
        Assert.Null(YouTubeDataApi.Age(now.AddDays(1), now));
    }

    // ---- The local file, frame by frame ----

    [Fact]
    public void TheFrameShownIsTheOneTheSongsClockAsksFor()
    {
        // In step, a little ahead of what is decoded: decode on and show.
        Assert.Equal(MusicVideoFrameSync.StepKind.Show, MusicVideoFrameSync.Decide(10.5, 10.4, 10.43, 10.5, null).Kind);
        // Outside the video: hold the frame shown.
        Assert.Equal(MusicVideoFrameSync.StepKind.Hold, MusicVideoFrameSync.Decide(null, 10.4, 10.43, 10.5, null).Kind);
        // A scrub forward past what decoding catches up with quickly: seek.
        Assert.Equal(new MusicVideoFrameSync.Step(MusicVideoFrameSync.StepKind.Seek, 40), MusicVideoFrameSync.Decide(40, 10.4, 10.43, 10.5, null));
        // Back (a scrub, a nudge later): seek.
        Assert.Equal(new MusicVideoFrameSync.Step(MusicVideoFrameSync.StepKind.Seek, 5), MusicVideoFrameSync.Decide(5, 10.4, 10.43, 10.5, null));
        // A tiny step back is shown as the frame there is, not sought.
        Assert.Equal(MusicVideoFrameSync.StepKind.Show, MusicVideoFrameSync.Decide(10.35, 10.4, 10.43, 10.5, null).Kind);
        // First frame of a song joined part-way: seek straight there; from the start: just decode.
        Assert.Equal(MusicVideoFrameSync.StepKind.Seek, MusicVideoFrameSync.Decide(75, null, null, null, null).Kind);
        Assert.Equal(MusicVideoFrameSync.StepKind.Show, MusicVideoFrameSync.Decide(0.3, null, null, null, null).Kind);
        // A seek in flight is waited for, unless the target moved well away.
        Assert.Equal(MusicVideoFrameSync.StepKind.Show, MusicVideoFrameSync.Decide(40.2, 10.4, null, null, 40).Kind);
        Assert.Equal(MusicVideoFrameSync.StepKind.Seek, MusicVideoFrameSync.Decide(90, 10.4, null, null, 40).Kind);
    }

    [Fact]
    public async Task AChosenFileIsUsedForItsSongOnly()
    {
        var files = new MusicVideoFiles(_world.Storage);
        var resolver = new MusicVideoResolver(files, new FakePlayability(), null, new MusicVideoCache(_world.Storage),
            new MusicVideoLinks(_world.Storage), now: () => MusicVideoWorld.Start, uptime: () => 0);
        var controller = new MusicVideoController(resolver, new MusicVideoOffsets(_world.Storage), files: files);
        controller.Update(_world.Track, enabled: true, online: true);
        await controller.SettleAsync();
        Assert.Equal(Missing(MusicVideoMiss.NoApiKey), MusicVideoResolution.Of(((MusicVideoStatus.Missing)controller.Status).Miss));
        Assert.True(controller.UseVideoFile(MusicVideoWorld.LocalPath));
        await controller.SettleAsync();
        Assert.Equal(Local(MusicVideoWorld.LocalPath), controller.SourceFor(_world.Track));
        Assert.Equal(MusicVideoWorld.LocalPath, new MusicVideoFiles(_world.Storage).Path(_world.Song.Key));
        Assert.Null(controller.SourceFor(_world.Track with { Title = "Save Your Tears" }));
        controller.ClearVideoFile();
        await controller.SettleAsync();
        Assert.Null(controller.SourceFor(_world.Track));
    }

    [Fact]
    public void TheFileStorageKeepsEveryKeyAcrossRuns()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lunalyr-mv-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "music-video.json");
            var first = new FileMusicVideoStorage(path);
            new MusicVideoOffsets(first).Set(8.4, "song", "yt:AAAAAAAAAAA");
            new MusicVideoLinks(first).Set("https://youtu.be/4NRXx6U8ABQ", "song");
            var again = new FileMusicVideoStorage(path);
            Assert.Equal(8.4, new MusicVideoOffsets(again).Offset("song", "yt:AAAAAAAAAAA"));
            Assert.Equal("4NRXx6U8ABQ", new MusicVideoLinks(again).VideoId("song"));
            File.WriteAllText(path, "not json");
            Assert.Null(new MusicVideoLinks(new FileMusicVideoStorage(path)).VideoId("song"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void TapKeysReachTheControllerOnlyWhileArmed()
    {
        var controller = _world.Controller(_world.Resolver());
        Assert.False(controller.TapKey(0x20, false, false, false));
    }
}
