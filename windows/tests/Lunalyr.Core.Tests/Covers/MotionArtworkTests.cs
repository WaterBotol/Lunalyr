// Animated covers: the Mac's tests of MotionArtworkProvider (ArtworkLookupTests.swift, RenderingPerformanceTests.swift,
// DesktopSurfaceTests.swift, IdleEnergyTests.swift) with their inputs, and Windows' own over replies recorded from Apple's
// public pages. The recorded replies (Fixtures/MotionArtwork) were fetched once on 13 Nov 2026 with Lunalyr's generic
// User-Agent ("Lunalyr/0.1 (Windows port; dev)"), no cookies and nothing personal: the iTunes search for Taylor Swift's
// "Midnights" (an album the Mac's tests use), its public music.apple.com page, and the square cover's master and
// 768-pixel playlists. No test touches the network.

using System.Text;
using Lunalyr.Core.Covers;
using Lunalyr.Core.Tests.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Covers;

public sealed class MotionArtworkTests
{
    private const string CoverHost = "https://mvod.itunes.apple.com";

    internal static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "MotionArtwork", name);

    internal static string FixtureText(string name) => File.ReadAllText(FixturePath(name), Encoding.UTF8);

    /// <summary>A byte-range playlist over one complete MP4, as Apple publishes most covers.</summary>
    private static string ProgressivePlaylist(string file) => $"""
        #EXTM3U
        #EXT-X-MAP:URI="{file}",BYTERANGE="895@0"
        #EXTINF:3,
        #EXT-X-BYTERANGE:1000@895
        {file}
        #EXT-X-ENDLIST
        """;

    /// <summary>Replies for one album whose page carries the given renditions (the Mac's albumRoutes).</summary>
    private static FixtureHandler AlbumRoutes(string? square, string? tall)
    {
        const string search = """{"results":[{"collectionViewUrl":"https://music.apple.com/us/album/dawn/123","collectionName":"Dawn","artistName":"Artist"}]}""";
        var renditions = string.Join(",", new[] { ("motionDetailSquare", square), ("motionDetailTall", tall) }
            .Where(r => r.Item2 is not null).Select(r => $"\"{r.Item1}\":{{\"video\":\"{CoverHost}{r.Item2}\"}}"));
        var page = "<script id=\"serialized-server-data\" type=\"application/json\">\n"
            + "{\"data\":[{\"data\":{\"sections\":[{\"items\":[{\"videoArtwork\":{\"dictionary\":{" + renditions + "}}}]}]}}]}\n</script>";
        return new FixtureHandler()
            .Set("itunes.apple.com/search", Reply.Text(200, search))
            .Set("music.apple.com/us/album/dawn/123", Reply.Text(200, page))
            .With(Reply.Text(404, ""));
    }

    private static MotionArtworkFinder Finder(FixtureHandler handler, string storefront = "us") => new(handler.Client(), storefront);

    private static async Task<MotionArtwork> Motion(FixtureHandler handler)
    {
        var found = await Finder(handler).FindAsync("Dawn", "Artist", "First Light", TestContext.Current.CancellationToken);
        Assert.NotNull(found);
        return found;
    }

    private static int PlaylistRequests(FixtureHandler handler, string path) =>
        handler.Requests.Count(r => r.RequestUri!.AbsolutePath == path);

    // Ported from RenderingPerformanceTests.swift: testMotionCoverUsesTheAlbumHeaderAndMatchesTheRelease
    [Fact]
    public void MotionCoverUsesTheAlbumHeaderAndMatchesTheRelease()
    {
        var wrong = new MotionSearchResult("https://music.apple.com/au/album/1989/1", "1989", "Taylor Swift");
        var correct = new MotionSearchResult("https://music.apple.com/au/album/midnights/2", "Midnights", "Taylor Swift");
        var pages = MotionArtworkPages.MatchingPages([wrong, correct], "Midnights", "Taylor Swift", song: null);
        Assert.Equal(["2"], pages.Select(p => p.Segments[^1]));

        const string video = "https://mvod.itunes.apple.com/art/cover.m3u8";
        const string artwork = "{\"videoArtwork\":{\"dictionary\":{\"motionDetailSquare\":{\"video\":\"" + video + "\"}}}}";
        static string Page(string first, string second) => "<script id=\"serialized-server-data\" type=\"application/json\">\n"
            + "{\"data\":[{\"data\":{\"sections\":[{\"items\":[" + first + "]},{\"items\":[" + second + "]}]}}]}\n</script>";
        Assert.Null(MotionArtworkPages.Parse(Page("{}", artwork)));
        Assert.Equal(video, MotionArtworkPages.Parse(Page(artwork, "{}"))?.Square.AbsoluteUri);
    }

    // Ported from RenderingPerformanceTests.swift: testOnlyCompleteUnencryptedMP4RenditionsAreUsedDirectly
    [Fact]
    public void OnlyCompleteUnencryptedMP4RenditionsAreUsedDirectly()
    {
        var baseUri = new Uri("https://mvod.itunes.apple.com/art/cover.m3u8");
        const string playlist = """
            #EXTM3U
            #EXT-X-MAP:URI="cover.mp4",BYTERANGE="895@0"
            #EXTINF:3,
            #EXT-X-BYTERANGE:1000@895
            cover.mp4
            #EXTINF:3,
            #EXT-X-BYTERANGE:1000@1895
            cover.mp4
            #EXT-X-ENDLIST
            """;
        Assert.Equal("https://mvod.itunes.apple.com/art/cover.mp4", MotionArtworkPages.SingleMp4(playlist, baseUri)?.AbsoluteUri);
        Assert.Null(MotionArtworkPages.SingleMp4(playlist + "\n#EXT-X-KEY:METHOD=SAMPLE-AES", baseUri));
        Assert.Null(MotionArtworkPages.SingleMp4(playlist + "\nother.mp4", baseUri));
    }

    // Ported from ArtworkLookupTests.swift: testTheTallAnimatedCoverIsResolvedForPortraitDisplays
    [Fact]
    public async Task TheTallAnimatedCoverIsResolvedForPortraitDisplays()
    {
        var handler = AlbumRoutes("/a/square/master.m3u8", "/a/tall/master.m3u8")
            .Set("mvod.itunes.apple.com/a/square/master.m3u8", Reply.Text(200, ProgressivePlaylist("square.mp4")))
            .Set("mvod.itunes.apple.com/a/tall/master.m3u8", Reply.Text(200, ProgressivePlaylist("tall.mp4")));
        var motion = await Motion(handler);
        Assert.Equal(CoverHost + "/a/square/square.mp4", motion.Square.AbsoluteUri);
        Assert.Equal(CoverHost + "/a/tall/tall.mp4", motion.Tall?.AbsoluteUri);
        Assert.Equal(motion.Tall, motion.Rendition(portrait: true)); // the tall cover reaches portrait displays
        Assert.Equal(motion.Square, motion.Rendition(portrait: false));
    }

    // Ported from ArtworkLookupTests.swift: testATallCoverThatIsNoSingleFileLeavesPortraitDisplaysTheSquareOne
    [Fact]
    public async Task ATallCoverThatIsNoSingleFileLeavesPortraitDisplaysTheSquareOne()
    {
        var handler = AlbumRoutes("/b/square/master.m3u8", "/b/tall/master.m3u8")
            .Set("mvod.itunes.apple.com/b/square/master.m3u8", Reply.Text(200, ProgressivePlaylist("square.mp4")))
            .Set("mvod.itunes.apple.com/b/tall/master.m3u8", Reply.Text(200, """
                #EXTM3U
                #EXTINF:3,
                part1.ts
                #EXTINF:3,
                part2.ts
                #EXT-X-ENDLIST
                """));
        var motion = await Motion(handler);
        Assert.Equal(CoverHost + "/b/tall/master.m3u8", motion.Tall?.AbsoluteUri); // kept as Apple published it
        Assert.Equal(motion.Square, motion.Rendition(portrait: true));
    }

    // Ported from ArtworkLookupTests.swift: testBothRenditionsAreResolvedAtOnce
    [Fact]
    public async Task BothRenditionsAreResolvedAtOnce()
    {
        var handler = AlbumRoutes("/d/square/master.m3u8", "/d/tall/master.m3u8");
        // Each master playlist is held until the other is asked for too.
        var square = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Set("mvod.itunes.apple.com/d/square/master.m3u8", Reply.Text(200, ProgressivePlaylist("square.mp4")) with { Wait = tall.Task });
        handler.Set("mvod.itunes.apple.com/d/tall/master.m3u8", Reply.Text(200, ProgressivePlaylist("tall.mp4")) with { Wait = square.Task });
        handler.Responder = request =>
        {
            if (request.RequestUri!.AbsolutePath == "/d/square/master.m3u8") square.TrySetResult();
            if (request.RequestUri!.AbsolutePath == "/d/tall/master.m3u8") tall.TrySetResult();
            return null;
        };
        var motion = await Finder(handler).FindAsync("Dawn", "Artist", "First Light", TestContext.Current.CancellationToken);
        Assert.Equal(CoverHost + "/d/square/square.mp4", motion?.Square.AbsoluteUri);
        Assert.Equal(CoverHost + "/d/tall/tall.mp4", motion?.Tall?.AbsoluteUri);
        // The handler gives up waiting after 10 s; met together, neither waited.
        Assert.True(square.Task.IsCompleted && tall.Task.IsCompleted, "The square cover waited for the tall one's playlist");
    }

    // Ported from ArtworkLookupTests.swift: testAnAlbumWithOnlyATallCoverResolvesItOnce
    [Fact]
    public async Task AnAlbumWithOnlyATallCoverResolvesItOnce()
    {
        var handler = AlbumRoutes(null, "/c/tall/master.m3u8")
            .Set("mvod.itunes.apple.com/c/tall/master.m3u8", Reply.Text(200, ProgressivePlaylist("tall.mp4")));
        var motion = await Motion(handler);
        Assert.Equal(CoverHost + "/c/tall/tall.mp4", motion.Square.AbsoluteUri);
        Assert.Equal(motion.Square, motion.Tall);
        Assert.Equal(1, PlaylistRequests(handler, "/c/tall/master.m3u8")); // one rendition, one fetch
    }

    // Ported from ArtworkLookupTests.swift: MotionArtworkCacheTests.testAnAnsweredMissIsRememberedForTheSession
    [Fact]
    public async Task AnAnsweredMissIsRememberedForTheSession()
    {
        var handler = new FixtureHandler().With(Reply.Text(200, """{"results":[]}"""));
        var finder = Finder(handler);
        Assert.Null(await finder.FindAsync("Quiet Album", "Quiet Artist", "One", TestContext.Current.CancellationToken));
        var asked = handler.Requests.Count;
        Assert.True(asked > 0);
        Assert.Null(await finder.FindAsync("Quiet Album", "Quiet Artist", "Two", TestContext.Current.CancellationToken));
        Assert.Equal(asked, handler.Requests.Count); // the next song of the album is not searched again
    }

    // Ported from ArtworkLookupTests.swift: MotionArtworkCacheTests.testFailingToReachAppleIsNotCachedAsAMiss
    [Fact]
    public async Task FailingToReachAppleIsNotCachedAsAMiss()
    {
        var handler = new FixtureHandler().With(Reply.Text(503, ""));
        var finder = Finder(handler);
        Assert.Null(await finder.FindAsync("Busy Album", "Busy Artist", "One", TestContext.Current.CancellationToken));
        var asked = handler.Requests.Count;
        await finder.FindAsync("Busy Album", "Busy Artist", "One", TestContext.Current.CancellationToken);
        Assert.True(handler.Requests.Count > asked);
    }

    // Ported from ArtworkLookupTests.swift: MotionArtworkCacheTests.testTheAlbumKeyIgnoresAnEmptyAlbum
    [Fact]
    public void TheAlbumKeyIgnoresAnEmptyAlbum()
    {
        Assert.Equal(MotionArtworkPages.CacheKey("", "A", "T"), MotionArtworkPages.CacheKey(null, "A", "T"));
        // Album-less singles do not share one cover.
        Assert.NotEqual(MotionArtworkPages.CacheKey("", "A", "T"), MotionArtworkPages.CacheKey("", "A", "U"));
        Assert.Equal(MotionArtworkPages.CacheKey("LP", "A", "T"), MotionArtworkPages.CacheKey("LP", "A", "U"));
    }

    // Ported from DesktopSurfaceTests.swift: testPortraitDisplaysTakeTheTallCoverOnlyAsAProgressiveFile
    [Fact]
    public void PortraitDisplaysTakeTheTallCoverOnlyAsAProgressiveFile()
    {
        var square = new Uri("https://mvod.itunes.apple.com/a/square.mp4");
        var rawTall = new Uri("https://mvod.itunes.apple.com/a/tall/master.m3u8");
        var progressiveTall = new Uri("https://mvod.itunes.apple.com/a/tall.MP4");
        var raw = new MotionArtwork(square, rawTall);
        Assert.Equal(square, raw.Rendition(portrait: true)); // a raw HLS playlist may never start or loop cleanly
        Assert.Equal(square, raw.Rendition(portrait: false));
        var ready = new MotionArtwork(square, progressiveTall);
        Assert.Equal(progressiveTall, ready.Rendition(portrait: true));
        Assert.Equal(square, ready.Rendition(portrait: false));
        Assert.Equal(square, new MotionArtwork(square, null).Rendition(portrait: true));
    }

    // Ported from IdleEnergyTests.swift (the motion-artwork cache's bound, one assertion of its test)
    [Fact]
    public void TheAlbumCacheIsBounded() => Assert.True(MotionArtworkFinder.CacheCapacity <= 512);

    // ---- Windows' own, over the recorded replies ----

    [Fact]
    public void TheRecordedAlbumPageGivesItsSquareCover()
    {
        var motion = MotionArtworkPages.Parse(FixtureText("music-apple-com-album-midnights.html"));
        Assert.NotNull(motion);
        Assert.Equal("https://mvod.itunes.apple.com/itunes-assets/HLSMusic112/v4/1f/76/00/1f7600ac-161b-fbce-1b5d-6c45c079090e/P517392561_default.m3u8",
            motion.Square.AbsoluteUri);
        // Apple's page keeps the tall rendition under "tallVideoArtwork", which the Mac's parser doesn't read, so both
        // apps see this album as square only (docs/milestones/M6.md, "Animated covers").
        Assert.Null(motion.Tall);
    }

    [Fact]
    public void TheRecordedMasterPlaylistOffersThe768PixelRenditionFirst()
    {
        var master = new Uri("https://mvod.itunes.apple.com/itunes-assets/HLSMusic112/v4/1f/76/00/1f7600ac-161b-fbce-1b5d-6c45c079090e/P517392561_default.m3u8");
        var variants = MotionArtworkPages.Variants(FixtureText("midnights-square-master.m3u8"), master);
        Assert.Equal(3, variants.Count);
        Assert.EndsWith("_768x768_-.m3u8", variants[0].AbsoluteUri, StringComparison.Ordinal);
        Assert.All(variants, v => Assert.DoesNotContain("hvc1", v.AbsoluteUri, StringComparison.Ordinal));
        Assert.Null(MotionArtworkPages.SingleMp4(FixtureText("midnights-square-master.m3u8"), master));
        Assert.Equal("https://mvod.itunes.apple.com/itunes-assets/HLSMusic122/v4/a2/f4/9d/a2f49dc9-9321-eedf-a86d-f1f50844f59e/P517392561_Anull_video_gr240_sdr_768x768_-.mp4",
            MotionArtworkPages.SingleMp4(FixtureText("midnights-square-768.m3u8"), variants[0])?.AbsoluteUri);
    }

    /// <summary>The four recorded replies, served by address.</summary>
    internal static FixtureHandler Recorded() => new FixtureHandler()
        .Set("itunes.apple.com/search", Reply.Text(200, FixtureText("itunes-search-midnights-album.json")))
        .Set("music.apple.com/us/album/midnights/1649434996", Reply.Text(200, FixtureText("music-apple-com-album-midnights.html")))
        .Set("mvod.itunes.apple.com/itunes-assets/HLSMusic112/v4/1f/76/00/1f7600ac-161b-fbce-1b5d-6c45c079090e/P517392561_default.m3u8",
            Reply.Text(200, FixtureText("midnights-square-master.m3u8")))
        .Set("mvod.itunes.apple.com/itunes-assets/HLSMusic122/v4/a2/f4/9d/a2f49dc9-9321-eedf-a86d-f1f50844f59e/P517392561_Anull_video_gr240_sdr_768x768_-.m3u8",
            Reply.Text(200, FixtureText("midnights-square-768.m3u8")))
        .With(Reply.Text(404, ""));

    [Fact]
    public async Task TheRecordedRepliesLeadToOneProgressiveMp4()
    {
        var handler = Recorded();
        var motion = await Finder(handler).FindAsync("Midnights", "Taylor Swift", "Lavender Haze", TestContext.Current.CancellationToken);
        Assert.NotNull(motion);
        Assert.EndsWith("/P517392561_Anull_video_gr240_sdr_768x768_-.mp4", motion.Square.AbsoluteUri, StringComparison.Ordinal);
        Assert.Null(motion.Tall);
        // The album search, the first matching page, the master and the 768-pixel playlist: four requests.
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("album", FixtureHandler.QueryOf(handler.Requests[0].RequestUri!, "entity"));
        Assert.Equal("Taylor Swift Midnights", FixtureHandler.QueryOf(handler.Requests[0].RequestUri!, "term"));
        Assert.Equal("us", FixtureHandler.QueryOf(handler.Requests[0].RequestUri!, "country"));
        // Public pages only: plain HTTPS GETs to Apple's public hosts, with no cookie or authorisation.
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal(Uri.UriSchemeHttps, r.RequestUri!.Scheme);
            Assert.Contains(r.RequestUri.Host, new[] { "itunes.apple.com", "music.apple.com", "mvod.itunes.apple.com" });
            Assert.False(r.Headers.Contains("Cookie"));
            Assert.Null(r.Headers.Authorization);
            Assert.StartsWith("Lunalyr/", string.Join(" ", r.Headers.UserAgent.Select(u => u.ToString())), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AnotherStorefrontIsSearchedFirstThenTheUSOne()
    {
        var handler = Recorded();
        handler.Responder = request => FixtureHandler.QueryOf(request.RequestUri!, "country") == "jp" ? Reply.Text(200, """{"results":[]}""") : null;
        var motion = await Finder(handler, storefront: "JP").FindAsync("Midnights", "Taylor Swift", "Lavender Haze", TestContext.Current.CancellationToken);
        Assert.NotNull(motion);
        var countries = handler.Requests.Where(r => r.RequestUri!.Host == "itunes.apple.com")
            .Select(r => FixtureHandler.QueryOf(r.RequestUri!, "country")).ToList();
        Assert.Equal(["jp", "jp", "us"], countries); // album then song in Japan, then the album in the US
    }

    [Fact]
    public void OnlyApplesVideoHostsOverHttpsAreAccepted()
    {
        Assert.NotNull(MotionArtworkPages.MediaUri("https://mvod.itunes.apple.com/a/b.m3u8"));
        Assert.NotNull(MotionArtworkPages.MediaUri("https://is1-ssl.mzstatic.com/a/b.M3U8"));
        Assert.Null(MotionArtworkPages.MediaUri("http://mvod.itunes.apple.com/a/b.m3u8"));
        Assert.Null(MotionArtworkPages.MediaUri("https://example.com/a/b.m3u8"));
        Assert.Null(MotionArtworkPages.MediaUri("https://mvod.itunes.apple.com.example.com/a/b.m3u8"));
        Assert.Null(MotionArtworkPages.MediaUri("https://mvod.itunes.apple.com/a/b.mp4"));
    }

    [Fact]
    public void NamesMatchIgnoringCaseAccentsAndPunctuation()
    {
        Assert.Equal(MotionArtworkPages.Normalized("Beyoncé"), MotionArtworkPages.Normalized("BEYONCE"));
        Assert.Equal(MotionArtworkPages.Normalized("folklore (deluxe version)"), MotionArtworkPages.Normalized("Folklore Deluxe Version"));
        Assert.NotEqual(MotionArtworkPages.Normalized("Midnights"), MotionArtworkPages.Normalized("Midnights (3am Edition)"));
    }
}

internal static class FixtureHandlerFallback
{
    /// <summary>Answers every address not set with <paramref name="reply"/>.</summary>
    public static FixtureHandler With(this FixtureHandler handler, Reply reply)
    {
        handler.Fallback = reply;
        return handler;
    }
}
