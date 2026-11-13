using Lunalyr.Core.MusicVideo;

namespace Lunalyr.Core.Tests.MusicVideo;

/// <summary>The YouTube video's still: where it is, the largest one first full screen, and only YouTube's image host asked.</summary>
public sealed class YouTubeThumbnailTests
{
    // Ported from MusicVideoTests.swift: testThumbnailUsesTheEphemeralSession (the URL and the host; Windows' client keeps
    // no cookies or cache by construction, MusicVideoHttpClient)
    [Fact]
    public async Task ThumbnailUsesTheImageHostOnly()
    {
        Assert.Equal("https://i.ytimg.com/vi/4NRXx6U8ABQ/hqdefault.jpg", YouTubeThumbnail.Url("4NRXx6U8ABQ")?.AbsoluteUri);
        Assert.Null(YouTubeThumbnail.Url("../../x"));
        var http = new FakeHttp();
        var image = await YouTubeThumbnail.LoadAsync("4NRXx6U8ABQ", http, ct: TestContext.Current.CancellationToken);
        Assert.Null(image);
        Assert.Equal(["i.ytimg.com"], http.Urls.Select(u => u.Host));
    }

    // Ported from MusicVideoTests.swift: testFullScreenThumbnailTriesTheLargestStillFirst
    [Fact]
    public async Task FullScreenThumbnailTriesTheLargestStillFirst()
    {
        // Reduce Motion shows the still full screen: the 1280 px one when the video has it, else the usual one.
        Assert.Equal(["maxresdefault.jpg", "hqdefault.jpg"], YouTubeThumbnail.Urls("4NRXx6U8ABQ", large: true).Select(u => u.Segments[^1]));
        Assert.Equal(["hqdefault.jpg"], YouTubeThumbnail.Urls("4NRXx6U8ABQ", large: false).Select(u => u.Segments[^1]));
        Assert.Empty(YouTubeThumbnail.Urls("../../x", large: true));
        var http = new FakeHttp();
        _ = await YouTubeThumbnail.LoadAsync("4NRXx6U8ABQ", http, large: true, TestContext.Current.CancellationToken);
        Assert.Equal(["i.ytimg.com", "i.ytimg.com"], http.Urls.Select(u => u.Host)); // falls back to the usual still
    }

    [Fact]
    public async Task TheLargestStillThatExistsIsUsedAndAFailureIsNoStill()
    {
        var http = new FakeHttp();
        http.ByPath["/vi/4NRXx6U8ABQ/hqdefault.jpg"] = new FakeHttp.Reply(200, [1, 2, 3]);
        Assert.Equal([1, 2, 3], await YouTubeThumbnail.LoadAsync("4NRXx6U8ABQ", http, large: true, TestContext.Current.CancellationToken));
        http.ByPath["/vi/4NRXx6U8ABQ/maxresdefault.jpg"] = new FakeHttp.Reply(200, [9]);
        Assert.Equal([9], await YouTubeThumbnail.LoadAsync("4NRXx6U8ABQ", http, large: true, TestContext.Current.CancellationToken));
        http.Failing = true; // offline: no still, and no exception
        Assert.Null(await YouTubeThumbnail.LoadAsync("4NRXx6U8ABQ", http, large: true, TestContext.Current.CancellationToken));
    }
}
