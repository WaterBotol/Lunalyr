using System.Diagnostics;
using System.Text;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

public class LyricHttpClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Uri Address = new("https://service.test/api");

    [Fact]
    public void AddressesAreBuiltSortedAndEncoded()
    {
        var uri = LyricHttpClient.BuildUri("https://service.test/api",
            [new("w", "晴天 周杰伦"), new("a", "x&y=z"), new("format", "json")]);
        Assert.Equal("https://service.test/api?a=x%26y%3Dz&format=json&w=%E6%99%B4%E5%A4%A9%20%E5%91%A8%E6%9D%B0%E4%BC%A6", uri.AbsoluteUri);
        Assert.Equal("晴天 周杰伦", FixtureHandler.QueryOf(uri, "w"));
        var kept = LyricHttpClient.BuildUri("https://service.test/api", [new("title", "t"), new("artist", "a")], sorted: false);
        Assert.Equal("?title=t&artist=a", kept.Query);
    }

    [Fact]
    public async Task RequestsCarryTheGenericAgentAndTheReferer()
    {
        var handler = new FixtureHandler { Fallback = Reply.Text(200, "{}") };
        await handler.Client().GetAsync("Test", Address, TimeSpan.FromSeconds(5), "https://referer.test/", Ct);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Lunalyr/0.1 (Windows; desktop lyrics)", string.Join(' ', request.Headers.GetValues("User-Agent")));
        Assert.Equal("https://referer.test/", request.Headers.Referrer?.ToString());
    }

    [Fact]
    public async Task JsonRepliesAreCheckedForStatusShapeAndSize()
    {
        var handler = new FixtureHandler { Fallback = Reply.Text(503, "") };
        var client = handler.Client();
        var error = await Assert.ThrowsAsync<LyricSourceException>(() => client.GetJsonAsync("Test", Address, TimeSpan.FromSeconds(5), null, Ct));
        Assert.Equal((LyricSourceFailure.HttpStatus, 503), (error.Failure, error.StatusCode));
        Assert.StartsWith("Test: HTTP 503", error.Message, StringComparison.Ordinal);
        handler.Fallback = Reply.Text(200, "<html>");
        error = await Assert.ThrowsAsync<LyricSourceException>(() => client.GetJsonAsync("Test", Address, TimeSpan.FromSeconds(5), null, Ct));
        Assert.Equal(LyricSourceFailure.BadResponse, error.Failure);
        // A byte-order mark does not upset the JSON reader.
        handler.Fallback = new Reply(200, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""{"ok":1}""")]);
        using (var document = await client.GetJsonAsync("Test", Address, TimeSpan.FromSeconds(5), null, Ct))
        {
            Assert.Equal(1, document.RootElement.GetProperty("ok").GetInt32());
        }
        handler.Fallback = Reply.Text(200, new string('x', 5000));
        var small = new LyricHttpClient(new HttpClient(handler), new LyricHttpOptions { MaxResponseBytes = 1000, MinimumHostInterval = TimeSpan.Zero });
        error = await Assert.ThrowsAsync<LyricSourceException>(() => small.GetAsync("Test", Address, TimeSpan.FromSeconds(5), null, Ct));
        Assert.Equal("Test: response too large", error.Message);
    }

    [Fact]
    public async Task TimeoutsCancellationAndConnectionFailuresAreToldApart()
    {
        var handler = new FixtureHandler { Fallback = Reply.Never };
        var error = await Assert.ThrowsAsync<LyricSourceException>(() =>
            handler.Client().GetAsync("Test", Address, TimeSpan.FromMilliseconds(100), null, Ct));
        Assert.Equal(LyricSourceFailure.TimedOut, error.Failure);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = handler.Client().GetAsync("Test", Address, TimeSpan.FromMinutes(5), null, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        handler.Fallback = Reply.Broken;
        error = await Assert.ThrowsAsync<LyricSourceException>(() => handler.Client().GetAsync("Test", Address, TimeSpan.FromSeconds(5), null, Ct));
        Assert.Equal(LyricSourceFailure.Unreachable, error.Failure);
        error = await Assert.ThrowsAsync<LyricSourceException>(() =>
            handler.Client(networkUp: () => false).GetAsync("Test", Address, TimeSpan.FromSeconds(5), null, Ct));
        Assert.True(error.IsOffline);
    }

    [Fact]
    public async Task RequestsToOneHostAreSpacedPolitely()
    {
        var handler = new FixtureHandler { Fallback = Reply.Text(200, "{}") };
        var client = new LyricHttpClient(new HttpClient(handler), new LyricHttpOptions { MinimumHostInterval = TimeSpan.FromMilliseconds(200) });
        var clock = Stopwatch.StartNew();
        await client.GetAsync("Test", Address, TimeSpan.FromSeconds(5), null, Ct);
        await client.GetAsync("Test", Address, TimeSpan.FromSeconds(5), null, Ct);
        await client.GetAsync("Test", new Uri("https://other.test/"), TimeSpan.FromSeconds(5), null, Ct);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(180), TimeSpan.FromSeconds(5));
        Assert.Equal(3, handler.Requests.Count);
    }
}
