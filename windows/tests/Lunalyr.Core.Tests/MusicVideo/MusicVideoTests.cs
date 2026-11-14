using Lunalyr.Core.MusicVideo;
using static Lunalyr.Core.Tests.MusicVideo.Sources;

namespace Lunalyr.Core.Tests.MusicVideo;

// Ported from MusicVideoTests.swift. Every seam is faked: no test reaches the network, a player, a file or the real
// settings. As on the Mac, YouTube's results page is faked (MusicVideoFakes.cs).
public sealed class MusicVideoTests
{
    private readonly MusicVideoWorld _world = new();
    private MusicVideoSong Song => _world.Song;
    private const string LocalPath = MusicVideoWorld.LocalPath;

    // ---- Source order ----

    // Ported from MusicVideoTests.swift: testLibraryVideoComesFirstAndNeedsNoNetwork
    [Fact]
    public async Task LibraryVideoComesFirstAndNeedsNoNetwork()
    {
        var http = new FakeHttp();
        var result = await _world.Resolver(new FakeLibrary(LocalPath), http: http).ResolveAsync(Song, online: true, TestContext.Current.CancellationToken);
        Assert.Equal(Found(Local(LocalPath)), result);
        Assert.Empty(http.Urls);
    }

    // Ported from MusicVideoTests.swift: testOnlineOffNeverTouchesTheNetwork
    [Fact]
    public async Task OnlineOffNeverTouchesTheNetwork()
    {
        var http = new FakeHttp();
        var result = await _world.Resolver(http: http).ResolveAsync(Song, online: false, TestContext.Current.CancellationToken);
        Assert.Equal(Missing(MusicVideoMiss.NotInLibrary), result);
        Assert.Empty(http.Urls);
    }

    // Ported from MusicVideoTests.swift: testProtectedLibraryVideoIsSkippedForYouTube
    [Fact]
    public async Task ProtectedLibraryVideoIsSkippedForYouTube()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeOfficialPage(http);
        var protectedFile = new FakePlayability(MusicVideoPlayability.Protected);
        var offline = await _world.Resolver(new FakeLibrary(LocalPath), protectedFile, new FakeHttp()).ResolveAsync(Song, online: false, TestContext.Current.CancellationToken);
        Assert.Equal(Missing(MusicVideoMiss.ProtectedInLibrary), offline);
        var online = await _world.Resolver(new FakeLibrary(LocalPath), protectedFile, http).ResolveAsync(Song, online: true, TestContext.Current.CancellationToken);
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), online);
    }

    // Ported from MusicVideoTests.swift: testYouTubeSearchSendsOnlyTitleAndArtist (without the still check, which isn't
    // ported, the results page is the only request: no i.ytimg.com frames)
    [Fact]
    public async Task YouTubeSearchSendsOnlyTitleAndArtist()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeOfficialPage(http);
        var result = await _world.Resolver(http: http).ResolveAsync(Song, online: true, TestContext.Current.CancellationToken);
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), result);
        Assert.Equal(YouTubeSearchPage.Host, http.Urls[0].Host);
        Assert.Equal([YouTubeSearchPage.Host], http.Urls.Select(u => u.Host));
        Assert.Equal("The Weeknd Blinding Lights official music video", FakeHttp.SearchQueryOf(http.Urls[0]));
        Assert.Empty(http.Headers[0]);
    }

    // Ported from MusicVideoTests.swift: testPastedLinkComesAfterTheLibraryAndNeedsNoSearch
    [Fact]
    public async Task PastedLinkComesAfterTheLibraryAndNeedsNoSearch()
    {
        var http = new FakeHttp();
        var links = new MusicVideoLinks(_world.Storage);
        Assert.False(links.Set("https://example.com/watch?v=4NRXx6U8ABQ", Song.Key));
        Assert.True(links.Set(" https://youtu.be/4NRXx6U8ABQ ", Song.Key));
        var pasted = await _world.Resolver(http: http).ResolveAsync(Song, online: false, TestContext.Current.CancellationToken);
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), pasted);
        Assert.Empty(http.Urls);
        var library = await _world.Resolver(new FakeLibrary(LocalPath), http: http).ResolveAsync(Song, online: false, TestContext.Current.CancellationToken);
        Assert.Equal(Found(Local(LocalPath)), library);
        new MusicVideoLinks(_world.Storage).Remove(Song.Key);
        Assert.Null(new MusicVideoLinks(_world.Storage).VideoId(Song.Key));
    }

    // Ported from MusicVideoTests.swift: testChangedPageFormatIsAFailureNotAMiss
    [Fact]
    public async Task ChangedPageFormatIsAFailureNotAMiss()
    {
        var http = new FakeHttp();
        http.ByPath[PageFixtures.Path] =new FakeHttp.Reply(200, "<html>Before you continue</html>"u8.ToArray());
        var resolver = _world.Resolver(http: http);
        Assert.Equal(Missing(MusicVideoMiss.LookupFailed), await resolver.ResolveAsync(Song, online: true, TestContext.Current.CancellationToken));
        Assert.Null(resolver.Cache.Lookup(Song.Key, MusicVideoWorld.Start));
    }

    // Ported from MusicVideoTests.swift: testNothingOfficialIsNotFoundAndCached
    [Fact]
    public async Task NothingOfficialIsNotFoundAndCached()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeSearch(http, ("BBBBBBBBBBB", "Blinding Lights (cover)", "Someone"));
        Assert.Equal(Missing(MusicVideoMiss.NotFound), await _world.Resolver(http: http).ResolveAsync(Song, online: true, TestContext.Current.CancellationToken));
        var count = http.Urls.Count;
        Assert.Equal(Missing(MusicVideoMiss.NotFound), await _world.Resolver(http: http).ResolveAsync(Song, online: true, TestContext.Current.CancellationToken));
        Assert.Equal(count, http.Urls.Count);
    }

    // ---- Caching ----

    // Ported from MusicVideoTests.swift: testFoundVideoIsCachedPerSong
    [Fact]
    public async Task FoundVideoIsCachedPerSong()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeOfficialPage(http);
        await _world.Resolver(http: http).ResolveAsync(Song, online: true, TestContext.Current.CancellationToken);
        var sent = http.Urls.Count;
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), await _world.Resolver(http: http).ResolveAsync(Song, online: true, TestContext.Current.CancellationToken));
        Assert.Equal(sent, http.Urls.Count);
        await _world.Resolver(http: http).ResolveAsync(Song with { Title = "Save Your Tears" }, online: true, TestContext.Current.CancellationToken);
        Assert.True(http.Urls.Count > sent, "another song is looked up");
    }

    // Ported from MusicVideoTests.swift: testCacheEntriesExpire
    [Fact]
    public void CacheEntriesExpire()
    {
        var cache = new MusicVideoCache(_world.Storage);
        var start = MusicVideoWorld.Start;
        cache.Store(Found(YouTube("4NRXx6U8ABQ")), "a", start);
        cache.Store(Missing(MusicVideoMiss.NotFound), "b", start);
        cache.Store(Missing(MusicVideoMiss.LookupFailed), "c", start);
        cache.Store(Found(Local(LocalPath)), "d", start);
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), cache.Lookup("a", start.AddDays(59)));
        Assert.Null(cache.Lookup("a", start.AddDays(61)));
        Assert.Equal(Missing(MusicVideoMiss.NotFound), cache.Lookup("b", start.AddDays(6)));
        Assert.Null(cache.Lookup("b", start.AddDays(8)));
        Assert.Null(cache.Lookup("c", start));
        Assert.Null(cache.Lookup("d", start));
    }

    // Ported from MusicVideoTests.swift: testCacheStaysBounded
    [Fact]
    public void CacheStaysBounded()
    {
        var cache = new MusicVideoCache(_world.Storage);
        var start = MusicVideoWorld.Start;
        for (var i = 0; i < MusicVideoCache.Capacity + 20; i++) cache.Store(Missing(MusicVideoMiss.NotFound), $"song{i}", start.AddSeconds(i));
        Assert.Equal(MusicVideoCache.Capacity, cache.Count);
        Assert.Null(cache.Lookup("song0", start.AddSeconds(1000)));
        Assert.NotNull(cache.Lookup($"song{MusicVideoCache.Capacity + 19}", start.AddSeconds(1000)));
    }

    // Ported from MusicVideoTests.swift: testNotEmbeddableIsRemembered
    [Fact]
    public async Task NotEmbeddableIsRemembered()
    {
        var http = new FakeHttp();
        var resolver = _world.Resolver(http: http);
        resolver.MarkNotEmbeddable(Song);
        Assert.Equal(Missing(MusicVideoMiss.NotEmbeddable), await resolver.ResolveAsync(Song, online: true, TestContext.Current.CancellationToken));
        Assert.Empty(http.Urls);
    }

    // ---- Failures and rate limits ----

    // Ported from MusicVideoTests.swift: testOfflineIsNotCachedAndReportsLookupFailed
    [Fact]
    public async Task OfflineIsNotCachedAndReportsLookupFailed()
    {
        var http = new FakeHttp { Failing = true };
        Assert.Equal(Missing(MusicVideoMiss.LookupFailed), await _world.Resolver(http: http).ResolveAsync(Song, online: true, TestContext.Current.CancellationToken));
        http.Failing = false;
        MusicVideoWorld.ServeOfficialPage(http);
        _world.Time += 10;
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), await _world.Resolver(http: http).ResolveAsync(Song, online: true, TestContext.Current.CancellationToken));
    }

    // Ported from MusicVideoTests.swift: testTooManyRequestsBacksOff
    [Fact]
    public async Task TooManyRequestsBacksOff()
    {
        var http = new FakeHttp();
        http.ByPath[PageFixtures.Path] = new FakeHttp.Reply(429, [], new() { ["Retry-After"] = "120" });
        double time = 0;
        var resolver = _world.Resolver(http: http, clock: () => time);
        Assert.Equal(Missing(MusicVideoMiss.RateLimited), await resolver.ResolveAsync(Song, online: true, TestContext.Current.CancellationToken));
        Assert.Null(resolver.Cache.Lookup(Song.Key, MusicVideoWorld.Start));
        time = 10;
        var sent = http.Urls.Count;
        Assert.Equal(Missing(MusicVideoMiss.RateLimited), await resolver.ResolveAsync(Song with { Title = "Save Your Tears" }, online: true, TestContext.Current.CancellationToken));
        Assert.Equal(sent, http.Urls.Count);
    }

    // Ported from MusicVideoTests.swift: testRateLimitSpacesAndCapsRequests
    [Fact]
    public void RateLimitSpacesAndCapsRequests()
    {
        var limit = new MusicVideoRateLimit(minimumGap: 2, window: 60, maximumPerWindow: 3, defaultBackOff: 60);
        Assert.Equal(0, limit.Delay(0));
        limit.Record(0);
        Assert.Equal(1.5, limit.Delay(0.5), 9);
        limit.Record(2);
        limit.Record(4);
        Assert.Equal(54, limit.Delay(6), 9);
        Assert.Equal(0, limit.Delay(61));
        limit.BackOff(100, null);
        Assert.Equal(60, limit.Delay(100));
        limit.BackOff(100, 10);
        Assert.Equal(60, limit.Delay(100));
    }

    // ---- Parsing ----

    // Ported from MusicVideoTests.swift: testYouTubeIDsFromLinks
    [Fact]
    public void YouTubeIdsFromLinks()
    {
        Assert.Equal("4NRXx6U8ABQ", YouTubeVideoId.FromUrl("https://www.youtube.com/watch?v=4NRXx6U8ABQ&t=3"));
        Assert.Equal("4NRXx6U8ABQ", YouTubeVideoId.FromUrl("https://youtu.be/4NRXx6U8ABQ"));
        Assert.Equal("4NRXx6U8ABQ", YouTubeVideoId.FromUrl("https://music.youtube.com/watch?v=4NRXx6U8ABQ"));
        Assert.Equal("4NRXx6U8ABQ", YouTubeVideoId.FromUrl("https://www.youtube-nocookie.com/embed/4NRXx6U8ABQ"));
        Assert.Null(YouTubeVideoId.FromUrl("https://evil.example/watch?v=4NRXx6U8ABQ"));
        Assert.Null(YouTubeVideoId.FromUrl("https://www.youtube.com/watch?v=4NRXx6U8AB'"));
        Assert.False(YouTubeVideoId.IsValid("4NRXx6U8AB<"));
    }

    // Ported from MusicVideoTests.swift: testYouTubeSearchPrefersTheOfficialVideo
    [Fact]
    public void YouTubeSearchPrefersTheOfficialVideo()
    {
        var results = YouTubeSearchPage.Results(System.Text.Encoding.UTF8.GetString(PageFixtures.SearchPage(
            ("CCCCCCCCCCC", "Blinding Lights (Live at the Grammys)", "The Weeknd"),
            ("DDDDDDDDDDD", "The Weeknd - Blinding Lights (Audio)", "The Weeknd - Topic"),
            ("EEEEEEEEEEE", "The Weeknd - Blinding Lights", "TheWeekndVEVO"),
            ("4NRXx6U8ABQ", "The Weeknd - Blinding Lights (Official Video)", "TheWeekndVEVO"))));
        Assert.Equal(4, results.Count);
        Assert.Equal("4NRXx6U8ABQ", YouTubeSearch.OfficialVideo(results, "Blinding Lights", "The Weeknd")?.VideoId);
        Assert.Null(YouTubeSearch.OfficialVideo(results, "Save Your Tears", "The Weeknd"));
        Assert.Empty(YouTubeSearchPage.Results("<html>consent</html>"));
    }

    // Ported from MusicVideoTests.swift: testSearchPageIsReadOffTheMainActor (Windows: off the calling thread, on the
    // thread pool; the call is made from a thread of its own so the two can be told apart)
    [Fact]
    public void SearchPageIsReadOffTheMainActor()
    {
        var page = PageFixtures.SearchPage(("4NRXx6U8ABQ", "Blinding Lights (Official Video)", "The Weeknd"));
        IReadOnlyList<YouTubeVideoResult> results = [];
        var caller = new Thread(() =>
        {
            var me = Environment.CurrentManagedThreadId;
            results = YouTubeSearchPage.ResultsOffThreadAsync(page, html =>
                Environment.CurrentManagedThreadId == me || !Thread.CurrentThread.IsThreadPoolThread ? [] : YouTubeSearchPage.Results(html))
                .GetAwaiter().GetResult();
        });
        caller.Start();
        caller.Join();
        Assert.Equal(["4NRXx6U8ABQ"], results.Select(r => r.VideoId));
    }

    // ---- Position mapping and drift ----

    // Ported from MusicVideoTests.swift: testPositionMappingWithOffsets
    [Fact]
    public void PositionMappingWithOffsets()
    {
        Assert.Equal(30, MusicVideoTiming.VideoTime(30, 0, 240));
        Assert.Equal(42.5, MusicVideoTiming.VideoTime(30, 12.5, 240));
        Assert.Equal(25, MusicVideoTiming.VideoTime(30, -5, 240));
        Assert.Null(MusicVideoTiming.VideoTime(2, -5, 240));
        Assert.Null(MusicVideoTiming.VideoTime(235, 10, 240));
        Assert.Equal(245, MusicVideoTiming.VideoTime(235, 10, null));
        Assert.Equal(310, MusicVideoTiming.VideoTime(10, 1000, null));
        Assert.Null(MusicVideoTiming.VideoTime(double.NaN, 0, null));
        Assert.Equal("+12.0 s", MusicVideoTiming.Format(12));
        Assert.Equal("−0.5 s", MusicVideoTiming.Format(-0.5));
        Assert.Equal("0.0 s", MusicVideoTiming.Format(0));
    }

    // Ported from MusicVideoTests.swift: testDriftDecisions
    [Fact]
    public void DriftDecisions()
    {
        var playing = new MusicVideoDrift.Sample(50, true);
        Assert.Equal(MusicVideoAction.None, MusicVideoDrift.Decide(50.2, playing, true, 10));
        Assert.Equal(MusicVideoAction.Seek(51.25, true), MusicVideoDrift.Decide(51, playing, true, 10, seekLead: 0.25));
        Assert.Equal(MusicVideoAction.None, MusicVideoDrift.Decide(51, playing, true, 0.5));
        Assert.Equal(MusicVideoAction.None, MusicVideoDrift.Decide(51, new(50, false, true), true, 10));
        Assert.Equal(MusicVideoAction.Play, MusicVideoDrift.Decide(50.1, new(50, false), true, 10));
        Assert.Equal(MusicVideoAction.Pause, MusicVideoDrift.Decide(50, playing, false, 10));
        Assert.Equal(MusicVideoAction.Seek(90, false), MusicVideoDrift.Decide(90, new(50, false), false, 10));
        Assert.Equal(MusicVideoAction.None, MusicVideoDrift.Decide(50.5, new(50, false), false, 10));
        Assert.Equal(MusicVideoAction.Pause, MusicVideoDrift.Decide(null, playing, true, 10));
        Assert.Equal(MusicVideoAction.None, MusicVideoDrift.Decide(null, new(239, false), true, 10));
    }

    private sealed class FakeSurface : IMusicVideoPlaybackSurface
    {
        public double Time;
        public bool Playing;
        public readonly List<double> Seeks = [];
        public double SeekLead => 0;
        public void Sample(Action<MusicVideoDrift.Sample?, double?> done) => done(new MusicVideoDrift.Sample(Time, Playing), 300);
        public void PlayVideo() => Playing = true;
        public void PauseVideo() => Playing = false;

        public void SeekVideo(double seconds)
        {
            Seeks.Add(seconds);
            Time = seconds;
        }
    }

    // Ported from MusicVideoTests.swift: testSyncLoopFollowsTheSong
    [Fact]
    public void SyncLoopFollowsTheSong()
    {
        double songTime = 10, clock = 0;
        var songPlaying = true;
        var surface = new FakeSurface();
        var loop = new MusicVideoSyncLoop(() => songTime, () => songPlaying, () => 4, () => clock) { Surface = surface };
        loop.Tick();
        Assert.Equal([14.0], surface.Seeks);
        Assert.True(surface.Playing);
        for (var i = 0; i < 8; i++)
        {
            clock += 0.25;
            songTime += 0.25;
            surface.Time += 0.25;
            loop.Tick();
        }
        Assert.True(Math.Abs(surface.Time - (songTime + 4)) < 0.5);
        Assert.Single(surface.Seeks);
        songTime = 120;
        clock += 0.25;
        loop.Tick();
        Assert.Equal(124, surface.Seeks[^1]);
        songPlaying = false;
        clock += 2;
        loop.Tick();
        Assert.False(surface.Playing);
        songPlaying = true;
        clock += 0.25;
        loop.Tick();
        Assert.True(surface.Playing);
        loop.SetSurfacePaused(true);
        loop.Tick();
        Assert.False(surface.Playing);
        loop.Stop();
    }

    /// <summary>A surface whose first sample never answers (a WebView2 call lost when its page went away).</summary>
    private sealed class LostFirstSampleSurface : IMusicVideoPlaybackSurface
    {
        public int Asked;
        public Action<MusicVideoDrift.Sample?, double?>? Lost;
        public bool Playing;
        public readonly List<double> Seeks = [];
        public double SeekLead => 0;

        public void Sample(Action<MusicVideoDrift.Sample?, double?> done)
        {
            if (Asked++ == 0) Lost = done;
            else done(new MusicVideoDrift.Sample(0, Playing), 300);
        }

        public void PlayVideo() => Playing = true;
        public void PauseVideo() => Playing = false;
        public void SeekVideo(double seconds) => Seeks.Add(seconds);
    }

    [Fact]
    public void SyncLoopCarriesOnAfterASampleThatNeverAnswers()
    {
        double clock = 0;
        var surface = new LostFirstSampleSurface();
        var loop = new MusicVideoSyncLoop(() => 30, () => true, () => 0, () => clock) { Surface = surface };
        loop.Tick();
        clock += 1;
        loop.Tick();
        Assert.Equal(1, surface.Asked); // still waiting
        clock += MusicVideoSyncLoop.SampleTimeout;
        loop.Tick();
        Assert.Equal(2, surface.Asked);
        Assert.True(surface.Playing);
        Assert.Equal([30.0], surface.Seeks);
        // The lost answer arriving late changes nothing.
        surface.Lost!(new MusicVideoDrift.Sample(999, false), 300);
        Assert.Equal([30.0], surface.Seeks);
        Assert.True(surface.Playing);
    }

    // ---- Controller and fallback ----

    // Ported from MusicVideoTests.swift: testControllerFallsBackAndReportsWhy
    [Fact]
    public async Task ControllerFallsBackAndReportsWhy()
    {
        var library = new FakeLibrary();
        var http = new FakeHttp();
        var controller = _world.Controller(_world.Resolver(library, http: http));
        var track = _world.Track;
        controller.Update(track, enabled: false, online: false);
        Assert.IsType<MusicVideoStatus.Off>(controller.Status);
        Assert.Empty(library.Asked);

        controller.Update(track, enabled: true, online: false);
        Assert.Equal(new MusicVideoStatus.Searching("Blinding Lights"), controller.Status);
        await controller.SettleAsync();
        Assert.Equal(new MusicVideoStatus.Missing(MusicVideoMiss.NotInLibrary, "Blinding Lights"), controller.Status);
        Assert.Null(controller.SourceFor(track));
        Assert.Empty(http.Urls);
        Assert.Contains("Showing Line patterns instead.", MusicVideoController.Note(controller.Status, false, "Line patterns"));

        library.Video = LocalPath;
        controller.Update(track, enabled: true, online: true);
        await controller.SettleAsync();
        Assert.Equal(Local(LocalPath), controller.SourceFor(track));
        Assert.Null(controller.SourceFor(track with { Title = "Save Your Tears" }));

        controller.ReportPlaybackFailure(Local(LocalPath));
        Assert.Equal(new MusicVideoStatus.Missing(MusicVideoMiss.LocalFileFailed, "Blinding Lights"), controller.Status);
        Assert.Null(controller.SourceFor(track));

        controller.Update(null, enabled: true, online: true);
        Assert.IsType<MusicVideoStatus.Idle>(controller.Status);
        controller.Update(track with { IsStream = true }, enabled: true, online: true);
        Assert.Equal(new MusicVideoStatus.Missing(MusicVideoMiss.NotASong, "Blinding Lights"), controller.Status);
    }

    // Ported from MusicVideoTests.swift: testPastedLinkFromSettingsReplacesTheFallback
    [Fact]
    public async Task PastedLinkFromSettingsReplacesTheFallback()
    {
        var http = new FakeHttp();
        var controller = _world.Controller(_world.Resolver(http: http));
        var track = _world.Track with { Album = null };
        controller.Update(track, enabled: true, online: false);
        await controller.SettleAsync();
        Assert.Null(controller.SourceFor(track));
        Assert.False(controller.UsePastedLink("not a link"));
        Assert.True(controller.UsePastedLink("https://www.youtube.com/watch?v=4NRXx6U8ABQ"));
        await controller.SettleAsync();
        Assert.Equal(YouTube("4NRXx6U8ABQ"), controller.SourceFor(track));
        Assert.Equal("https://www.youtube.com/watch?v=4NRXx6U8ABQ", controller.PastedLink);
        controller.ClearPastedLink();
        await controller.SettleAsync();
        Assert.Null(controller.SourceFor(track));
        Assert.Empty(http.Urls);
    }

    // Ported from MusicVideoTests.swift: testOffsetsArePerSongAndPersist
    [Fact]
    public async Task OffsetsArePerSongAndPersist()
    {
        var offsets = new MusicVideoOffsets(_world.Storage);
        var controller = new MusicVideoController(_world.Resolver(), offsets);
        var track = _world.Track with { Album = null };
        controller.Update(track, enabled: true, online: false);
        await controller.SettleAsync();
        controller.NudgeOffset(5);
        controller.NudgeOffset(0.5);
        Assert.Equal(5.5, controller.CurrentOffset);
        Assert.Equal(5.5, new MusicVideoOffsets(_world.Storage).Offset(track.Song.Key));
        Assert.Equal(0, controller.OffsetFor(track with { Title = "Save Your Tears" }));
        controller.ResetOffset();
        Assert.Equal(0, controller.CurrentOffset);
        Assert.Empty(offsets.Values);
    }

    // ---- Embed page ----

    // Ported from MusicVideoTests.swift: testEmbedPageIsMutedInlineAndCannotNavigateAway (the documented embed parameters)
    [Fact]
    public void EmbedPageIsMutedInlineAndCannotNavigateAway()
    {
        var html = YouTubeEmbed.Html("4NRXx6U8ABQ", 42.7);
        foreach (var needle in new[] { "embed/4NRXx6U8ABQ?", "mute=1", "controls=0", "playsinline=1", "loop=0", "start=42",
                     "youtube-nocookie.com", "iframe_api", "enablejsapi=1" })
            Assert.Contains(needle, html);
        Assert.True(YouTubeEmbed.Allows(true, YouTubeEmbed.BaseUrl));
        Assert.False(YouTubeEmbed.Allows(true, new Uri("https://www.youtube.com/watch?v=4NRXx6U8ABQ")));
        Assert.True(YouTubeEmbed.Allows(false, new Uri("https://www.youtube-nocookie.com/embed/x")));
        var sample = YouTubeEmbed.Sample("""{"time":12.5,"state":1,"duration":240}""");
        Assert.Equal(new MusicVideoDrift.Sample(12.5, true), sample?.Sample);
        Assert.Equal(240, sample?.Duration);
        Assert.Null(YouTubeEmbed.Sample(null));
        Assert.Null(YouTubeEmbed.Sample("null"));
        Assert.Contains(150, YouTubeEmbed.FatalErrors);
    }

    // Ported from MusicVideoTests.swift: testWebViewPlaysOnlyMutedMediaAndHasNoAirPlay (the page's half; the WebView2 mute
    // and InPrivate profile are the host's and aren't testable without a WebView2)
    [Fact]
    public void PageMutesBeforeEveryPlayAndSeekAndNamesItsVideoAndPage()
    {
        var html = YouTubeEmbed.Html("4NRXx6U8ABQ", 0, page: 3);
        Assert.Contains("mute=1", html);
        Assert.Contains("player.mute(); player.playVideo()", html);
        Assert.Contains("player.mute(); player.seekTo", html);
        Assert.Contains("video: '4NRXx6U8ABQ', page: 3", html);
    }

    // ---- The embed page's life ----

    private static YouTubeEmbedSession.Message Msg(YouTubeEmbedSession.MessageKind kind, int page, string video = "4NRXx6U8ABQ", int value = 0) =>
        new(kind, value, video, page);

    /// <summary>A session whose ready timeout never runs out by itself.</summary>
    private static YouTubeEmbedSession EmbedSession() => new(sleep: (_, ct) => Task.Delay(Timeout.Infinite, ct));

    // Ported from MusicVideoTests.swift: testOnlyThePagesOwnMainFrameIsBelieved
    [Fact]
    public void OnlyThePagesOwnMainFrameIsBelieved()
    {
        var b = YouTubeEmbed.BaseUrl;
        Assert.True(YouTubeEmbed.Trusts(true, b.Scheme, b.Host));
        Assert.False(YouTubeEmbed.Trusts(false, b.Scheme, b.Host));
        Assert.False(YouTubeEmbed.Trusts(true, "https", "www.youtube-nocookie.com"));
        Assert.False(YouTubeEmbed.Trusts(true, "http", b.Host));

        var session = EmbedSession();
        var load = session.Attach("4NRXx6U8ABQ");
        Assert.Equal(YouTubeEmbedSession.EffectKind.Load, load.Kind);
        var page = load.Page;
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.Receive(Msg(YouTubeEmbedSession.MessageKind.Error, page, value: 150), trusted: false));
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.Receive(Msg(YouTubeEmbedSession.MessageKind.Error, page, "BBBBBBBBBBB", 150), trusted: true));
        Assert.Equal(YouTubeEmbedSession.Effect.Ready, session.Receive(Msg(YouTubeEmbedSession.MessageKind.Ready, page), trusted: true));
        Assert.Equal(YouTubeEmbedSession.Effect.Fail("4NRXx6U8ABQ", MusicVideoPlaybackFailure.Refused),
            session.Receive(Msg(YouTubeEmbedSession.MessageKind.Error, page, value: 150), trusted: true));
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.Receive(Msg(YouTubeEmbedSession.MessageKind.Error, page, value: 150), trusted: true));
        Assert.Equal(Msg(YouTubeEmbedSession.MessageKind.Error, 2, value: 101),
            YouTubeEmbedSession.Message.Parse("""{"type":"error","value":101,"video":"4NRXx6U8ABQ","page":2}"""));
        Assert.Null(YouTubeEmbedSession.Message.Parse("\"error\""));
        session.Stop();
    }

    // Ported from MusicVideoTests.swift: testAStalePagesErrorIsNotBlamedOnTheNewVideo
    [Fact]
    public void AStalePagesErrorIsNotBlamedOnTheNewVideo()
    {
        var session = EmbedSession();
        var old = session.Attach("4NRXx6U8ABQ").Page;
        var fresh = session.Attach("BBBBBBBBBBB").Page;
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.Receive(Msg(YouTubeEmbedSession.MessageKind.Error, old, "4NRXx6U8ABQ", 150), true));
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.Receive(Msg(YouTubeEmbedSession.MessageKind.Error, old, "BBBBBBBBBBB", 150), true));
        Assert.Equal(YouTubeEmbedSession.Effect.Fail("BBBBBBBBBBB", MusicVideoPlaybackFailure.Refused),
            session.Receive(Msg(YouTubeEmbedSession.MessageKind.Error, fresh, "BBBBBBBBBBB", 101), true));
        session.Stop();
    }

    // Ported from MusicVideoTests.swift: testPlayerErrorFiveIsPassing
    [Fact]
    public void PlayerErrorFiveIsPassing()
    {
        Assert.Equal(MusicVideoPlaybackFailure.Transient, YouTubeEmbed.Failure(5));
        Assert.Equal(MusicVideoPlaybackFailure.Refused, YouTubeEmbed.Failure(150));
        Assert.Null(YouTubeEmbed.Failure(0));
    }

    // Ported from MusicVideoTests.swift: testReadyTimeoutReportsAPassingFailure
    [Fact]
    public async Task ReadyTimeoutReportsAPassingFailure()
    {
        var session = new YouTubeEmbedSession(readyTimeout: 12, sleep: (_, _) => Task.CompletedTask);
        var reported = new TaskCompletionSource<YouTubeEmbedSession.Effect>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnEffect = effect => reported.TrySetResult(effect);
        session.Attach("4NRXx6U8ABQ");
        Assert.Equal(YouTubeEmbedSession.Effect.Fail("4NRXx6U8ABQ", MusicVideoPlaybackFailure.Transient),
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        var answered = EmbedSession();
        var page = answered.Attach("4NRXx6U8ABQ").Page;
        Assert.Equal(YouTubeEmbedSession.Effect.Ready, answered.Receive(Msg(YouTubeEmbedSession.MessageKind.Ready, page), true));
        Assert.Equal(YouTubeEmbedSession.Effect.None, answered.TimedOut(page));
        Assert.Equal(YouTubeEmbedSession.Effect.None, answered.TimedOut(page - 1));
        answered.Stop();
    }

    // Ported from MusicVideoTests.swift: testNavigationFailuresArePassingButCancelledLoadsAreNot
    [Fact]
    public void NavigationFailuresArePassingButCancelledLoadsAreNot()
    {
        var session = EmbedSession();
        session.Attach("4NRXx6U8ABQ");
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.NavigationFailed(cancelled: true));
        Assert.Equal(YouTubeEmbedSession.Effect.Fail("4NRXx6U8ABQ", MusicVideoPlaybackFailure.Transient), session.NavigationFailed(cancelled: false));
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.NavigationFailed(cancelled: false));
        session.Stop();
    }

    // Ported from MusicVideoTests.swift: testCrashedPageIsReloadedOnlyALimitedNumberOfTimes
    [Fact]
    public void CrashedPageIsReloadedOnlyALimitedNumberOfTimes()
    {
        var session = EmbedSession();
        session.Attach("4NRXx6U8ABQ");
        for (var i = 0; i < YouTubeEmbedSession.DefaultReloadLimit; i++)
        {
            var effect = session.ContentProcessTerminated();
            Assert.Equal(YouTubeEmbedSession.EffectKind.Load, effect.Kind);
            Assert.Equal("4NRXx6U8ABQ", effect.VideoId);
        }
        Assert.Equal(YouTubeEmbedSession.Effect.Fail("4NRXx6U8ABQ", MusicVideoPlaybackFailure.Transient), session.ContentProcessTerminated());
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.ContentProcessTerminated());
        Assert.Equal(YouTubeEmbedSession.EffectKind.Load, session.Attach("BBBBBBBBBBB").Kind);
        Assert.Equal(0, session.Reloads);
        session.Stop();
    }

    // ---- Controller reports ----

    private async Task<MusicVideoController> ShowingController(FakeHttp? http = null, FakeLibrary? library = null, bool online = false)
    {
        var controller = _world.Controller(_world.Resolver(library, http: http));
        controller.Update(_world.Track, enabled: true, online: online);
        await controller.SettleAsync();
        return controller;
    }

    // Ported from MusicVideoTests.swift: testPassingPlayerFailureIsNotCachedAndIsRetried
    [Fact]
    public async Task PassingPlayerFailureIsNotCachedAndIsRetried()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeOfficialPage(http);
        var controller = await ShowingController(http, online: true);
        var track = _world.Track;
        Assert.Equal(YouTube("4NRXx6U8ABQ"), controller.SourceFor(track));
        controller.RetryDelay = TimeSpan.Zero;
        controller.ReportPlaybackFailure(YouTube("BBBBBBBBBBB"), MusicVideoPlaybackFailure.Transient);
        Assert.Equal(YouTube("4NRXx6U8ABQ"), controller.SourceFor(track));
        controller.ReportPlaybackFailure(YouTube("4NRXx6U8ABQ"), MusicVideoPlaybackFailure.Transient);
        Assert.Equal(new MusicVideoStatus.Missing(MusicVideoMiss.PlayerUnreachable, "Blinding Lights"), controller.Status);
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), new MusicVideoCache(_world.Storage).Lookup(track.Song.Key, MusicVideoWorld.Start));
        for (var i = 0; i < 200 && controller.SourceFor(track) is null; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(YouTube("4NRXx6U8ABQ"), controller.SourceFor(track));
        Assert.False(Missing(MusicVideoMiss.PlayerUnreachable).IsCacheable);
        Assert.Contains("trying again", MusicVideoController.Note(new MusicVideoStatus.Missing(MusicVideoMiss.PlayerUnreachable, "X"), true, "Line patterns"));
    }

    // Ported from MusicVideoTests.swift: testRefusedPastedLinkIsNotRememberedAndClearingForgetsTheSong
    [Fact]
    public async Task RefusedPastedLinkIsNotRememberedAndClearingForgetsTheSong()
    {
        var controller = await ShowingController();
        Assert.True(controller.UsePastedLink("https://youtu.be/4NRXx6U8ABQ"));
        await controller.SettleAsync();
        controller.ReportPlaybackFailure(YouTube("4NRXx6U8ABQ"), MusicVideoPlaybackFailure.Refused);
        Assert.Equal(new MusicVideoStatus.Missing(MusicVideoMiss.NotEmbeddable, "Blinding Lights"), controller.Status);
        var key = _world.Track.Song.Key;
        var now = MusicVideoWorld.Start;
        Assert.Null(new MusicVideoCache(_world.Storage).Lookup(key, now));

        new MusicVideoCache(_world.Storage).Store(Missing(MusicVideoMiss.NotEmbeddable), key, now);
        var poisoned = _world.Controller(_world.Resolver());
        poisoned.Update(_world.Track, enabled: true, online: true);
        await poisoned.SettleAsync();
        Assert.Equal(YouTube("4NRXx6U8ABQ"), poisoned.SourceFor(_world.Track));
        Assert.NotNull(new MusicVideoCache(_world.Storage).Lookup(key, now));
        poisoned.ClearPastedLink();
        await poisoned.SettleAsync();
        Assert.Null(new MusicVideoCache(_world.Storage).Lookup(key, now));
        Assert.NotEqual(new MusicVideoStatus.Missing(MusicVideoMiss.NotEmbeddable, "Blinding Lights"), poisoned.Status);
    }

    // Ported from MusicVideoTests.swift: testRefusedFoundVideoIsStillRemembered
    [Fact]
    public async Task RefusedFoundVideoIsStillRemembered()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeOfficialPage(http);
        var controller = await ShowingController(http, online: true);
        controller.ReportPlaybackFailure(YouTube("4NRXx6U8ABQ"));
        await controller.SettleAsync();
        Assert.Equal(new MusicVideoStatus.Missing(MusicVideoMiss.NotEmbeddable, "Blinding Lights"), controller.Status);
        Assert.Equal(Missing(MusicVideoMiss.NotEmbeddable), new MusicVideoCache(_world.Storage).Lookup(_world.Track.Song.Key, MusicVideoWorld.Start));
    }

    // Ported from MusicVideoTests.swift: testARefusedVideoMakesWayForTheSongsNextOfficialVideo
    [Fact]
    public async Task ARefusedVideoMakesWayForTheSongsNextOfficialVideo()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeSearch(http,
            ("4NRXx6U8ABQ", "The Weeknd - Blinding Lights (Official Video)", "TheWeekndVEVO"),
            ("CCCCCCCCCCC", "The Weeknd - Blinding Lights (Official Music Video)", "The Weeknd"));
        var controller = await ShowingController(http, online: true);
        var track = _world.Track;
        Assert.Equal(YouTube("CCCCCCCCCCC"), controller.SourceFor(track));
        controller.ReportPlaybackFailure(YouTube("CCCCCCCCCCC"));
        Assert.Null(controller.SourceFor(track));
        _world.Time += 10;
        await controller.SettleAsync();
        Assert.Equal(YouTube("4NRXx6U8ABQ"), controller.SourceFor(track));
        var key = track.Song.Key;
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), new MusicVideoCache(_world.Storage).Lookup(key, MusicVideoWorld.Start));
        controller.ReportPlaybackFailure(YouTube("4NRXx6U8ABQ"));
        await controller.SettleAsync();
        Assert.Equal(new MusicVideoStatus.Missing(MusicVideoMiss.NotEmbeddable, "Blinding Lights"), controller.Status);
        Assert.Equal(Missing(MusicVideoMiss.NotEmbeddable), new MusicVideoCache(_world.Storage).Lookup(key, MusicVideoWorld.Start));
    }

    // Ported from MusicVideoTests.swift: testLocalFileFailureSaysSo
    [Fact]
    public async Task LocalFileFailureSaysSo()
    {
        var controller = await ShowingController(library: new FakeLibrary(LocalPath));
        controller.ReportPlaybackFailure(Local(LocalPath));
        Assert.Equal(new MusicVideoStatus.Missing(MusicVideoMiss.LocalFileFailed, "Blinding Lights"), controller.Status);
        var note = MusicVideoController.Note(controller.Status, false, "Line patterns") ?? "";
        Assert.DoesNotContain("outside YouTube", note);
        Assert.Contains("wouldn’t play", note);
        Assert.Equal(0, new MusicVideoCache(_world.Storage).Count);
    }

    // Ported from MusicVideoTests.swift: testCancelledLookupUsesNoRateLimitSlot
    [Fact]
    public async Task CancelledLookupUsesNoRateLimitSlot()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeOfficialPage(http);
        var skipped = _world.Resolver(http: http);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(Missing(MusicVideoMiss.LookupFailed), await skipped.ResolveAsync(Song, true, cancelled.Token));
        Assert.Empty(http.Urls);
        Assert.Empty(skipped.RateLimit.Sent);
        Assert.Null(skipped.Cache.Lookup(Song.Key, MusicVideoWorld.Start));

        // Skipped while waiting its turn: the slot is not used either.
        double time = 0;
        CancellationTokenSource? current = null;
        var waiting = _world.Resolver(http: http, clock: () => time, sleep: (seconds, _) =>
        {
            time += seconds;
            current?.Cancel();
            return Task.CompletedTask;
        });
        current = new CancellationTokenSource();
        await waiting.ResolveAsync(Song with { Title = "Save Your Tears" }, true, current.Token);
        Assert.Single(waiting.RateLimit.Sent);
        var sent = http.Urls.Count;
        current = new CancellationTokenSource();
        Assert.Equal(Missing(MusicVideoMiss.LookupFailed), await waiting.ResolveAsync(Song, true, current.Token));
        Assert.Equal(sent, http.Urls.Count);
        Assert.Single(waiting.RateLimit.Sent);
    }

    // Ported from MusicVideoTests.swift: testFoundVideosFromBeforeTheStillCheckAreLookedUpAgain (the version rule)
    [Fact]
    public void FoundVideosFromBeforeTheCurrentVersionAreLookedUpAgain()
    {
        var now = MusicVideoWorld.Start;
        _world.Storage.Set(MusicVideoCache.Key, "{\"" + Song.Key + "\":{\"at\":" + now.ToUnixTimeSeconds() + ",\"video\":\"4NRXx6U8ABQ\"}}");
        Assert.Null(new MusicVideoCache(_world.Storage).Lookup(Song.Key, now));
        new MusicVideoCache(_world.Storage).Store(Found(YouTube("4NRXx6U8ABQ")), Song.Key, now);
        Assert.Equal(Found(YouTube("4NRXx6U8ABQ")), new MusicVideoCache(_world.Storage).Lookup(Song.Key, now));
    }

    // Ported from MusicVideoTests.swift: testAudioAndArtTrackUploadsAreRejectedAndMusicVideoRanksFirst
    [Fact]
    public void AudioAndArtTrackUploadsAreRejectedAndMusicVideoRanksFirst()
    {
        string[] rejected = ["Blinding Lights (Official Audio)", "Blinding Lights (Audio)", "Blinding Lights [Audio Only]",
            "Blinding Lights (Visualizer)", "Blinding Lights (Official Visualiser)", "Blinding Lights (Lyric Video)",
            "Blinding Lights LYRICS", "Blinding Lights (Art Track)"];
        foreach (var title in rejected)
            Assert.Null(YouTubeSearch.OfficialVideo([new YouTubeVideoResult("AAAAAAAAAAA", title, "The Weeknd")], "Blinding Lights", "The Weeknd"));
        YouTubeVideoResult[] results =
        [
            new("AAAAAAAAAAA", "Blinding Lights (Official Video)", "The Weeknd"),
            new("BBBBBBBBBBB", "Blinding Lights (Official Music Video)", "The Weeknd"),
            new("CCCCCCCCCCC", "Blinding Lights", "The Weeknd"),
        ];
        Assert.Equal(["BBBBBBBBBBB", "AAAAAAAAAAA", "CCCCCCCCCCC"],
            YouTubeSearch.OfficialVideos(results, "Blinding Lights", "The Weeknd").Select(r => r.VideoId));
    }

    // ---- Open on YouTube ----

    // Ported from MusicVideoTests.swift: testWatchLinkStartsAtTheSongsPosition
    [Fact]
    public void WatchLinkStartsAtTheSongsPosition()
    {
        Assert.Equal("https://www.youtube.com/watch?v=4NRXx6U8ABQ&t=65s", MusicVideoController.WatchUrl("4NRXx6U8ABQ", 61.9, 4));
        Assert.Equal("https://www.youtube.com/watch?v=4NRXx6U8ABQ", MusicVideoController.WatchUrl("4NRXx6U8ABQ", 0.4, 0));
        Assert.Equal("https://www.youtube.com/watch?v=4NRXx6U8ABQ", MusicVideoController.WatchUrl("4NRXx6U8ABQ", 3, -10));
        Assert.Equal("https://www.youtube.com/watch?v=4NRXx6U8ABQ", MusicVideoController.WatchUrl("4NRXx6U8ABQ", double.NaN, 0));
        Assert.Null(MusicVideoController.WatchUrl("x&t=1", 10, 0));
    }

    // Ported from MusicVideoTests.swift: testOpenLinkOnlyForYouTubeAndNeverInTests (the link half; opening is the app's,
    // behind the playback guard, and nothing here opens anything)
    [Fact]
    public async Task WatchLinkOnlyForYouTube()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeOfficialPage(http);
        var youTube = await ShowingController(http, online: true);
        Assert.Equal("4NRXx6U8ABQ", youTube.ShowingYouTubeId);
        var library = await ShowingController(library: new FakeLibrary(LocalPath));
        Assert.Null(library.ShowingYouTubeId);
        Assert.Null(library.CurrentWatchUrl);
    }
}
