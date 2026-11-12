using System.IO.Compression;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

public class LyricsPlusSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LyricQuery ShapeOfYou = new("Shape of You", "Ed Sheeran", "÷", 233.7);

    private static readonly string[] Hosts = ["a.lyricsplus.test", "b.lyricsplus.test"];

    private static LyricsPlusSource Source(FixtureHandler handler, TimeProvider? time = null, TimeSpan? timeout = null) =>
        new(handler.Client(time: time), new LyricsPlusOptions
        {
            Hosts = [.. Hosts.Select(h => "https://" + h)],
            RequestTimeout = timeout ?? TimeSpan.FromSeconds(4),
        });

    [Fact]
    public async Task TheRecordedNodesDeadMirrorIsSkippedAndTheMatchReported()
    {
        var handler = new FixtureHandler()
            .Set("a.lyricsplus.test/v2/lyrics/get", Reply.Fixture("lyricsplus/dead-node.txt", 404))
            .Set("b.lyricsplus.test/v2/lyrics/get", Reply.Fixture("lyricsplus/shape-of-you.json"));
        var found = await Source(handler).FetchAsync(ShapeOfYou, Ct);
        Assert.NotNull(found);
        Assert.Equal(LyricFormat.KPoeJson, found.Format);
        Assert.Equal(("Shape of You", "Ed Sheeran", null), (found.ReportedTitle, found.ReportedArtist, found.ReportedDuration));
        Assert.Equal("Apple", found.Extras![RawLyricsExtras.Upstream]); // the node says "qApple" for Apple Music's lyrics
        Assert.Contains("\"syllabus\"", found.Payload, StringComparison.Ordinal);
        // Title, artist, whole seconds and album, in that order.
        Assert.Equal("?title=Shape%20of%20You&artist=Ed%20Sheeran&duration=233&album=%C3%B7", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task AGzipBodyWithoutAHeaderIsInflated()
    {
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Fixtures.Bytes("lyricsplus/shape-of-you.json"));
        }
        var handler = new FixtureHandler().Set("a.lyricsplus.test/v2/lyrics/get", new Reply(200, packed.ToArray()));
        var found = await Source(handler).FetchAsync(ShapeOfYou, Ct);
        Assert.Equal(Fixtures.Text("lyricsplus/shape-of-you.json"), found?.Payload);
    }

    [Fact]
    public async Task LinesWithoutSyllablesAreNotFound()
    {
        const string lineOnly = """{"type":"Line","metadata":{"source":"Musixmatch"},"lyrics":[{"time":1000,"duration":2000,"text":"Sing along","syllabus":[]}]}""";
        var handler = new FixtureHandler { Fallback = Reply.Text(200, lineOnly) };
        Assert.Null(await Source(handler).FetchAsync(ShapeOfYou, Ct));
        Assert.Equal(2, handler.Requests.Count); // one second opinion
    }

    [Fact]
    public async Task ABusyMirrorRestsAMinute()
    {
        var time = new ManualTimeProvider();
        var handler = new FixtureHandler()
            .Set("a.lyricsplus.test/v2/lyrics/get", Reply.Text(429, ""))
            .Set("b.lyricsplus.test/v2/lyrics/get", Reply.Fixture("lyricsplus/shape-of-you.json"));
        var source = Source(handler, time);
        Assert.NotNull(await source.FetchAsync(ShapeOfYou, Ct));
        Assert.NotNull(await source.FetchAsync(ShapeOfYou, Ct));
        Assert.Equal(1, handler.Count(Hosts[0])); // resting, and the healthy one answered
        time.AdvanceSeconds(61);
        await source.FetchAsync(ShapeOfYou, Ct);
        Assert.Equal(2, handler.Count(Hosts[0]));
    }

    [Fact]
    public async Task ASlowMirrorTimesOutAndTheNextAnswers()
    {
        var handler = new FixtureHandler()
            .Set("a.lyricsplus.test/v2/lyrics/get", Reply.Never)
            .Set("b.lyricsplus.test/v2/lyrics/get", Reply.Fixture("lyricsplus/shape-of-you.json"));
        Assert.NotNull(await Source(handler, timeout: TimeSpan.FromMilliseconds(100)).FetchAsync(ShapeOfYou, Ct));
        handler.Set("b.lyricsplus.test/v2/lyrics/get", Reply.Never);
        var error = await Assert.ThrowsAsync<LyricSourceException>(() =>
            Source(handler, timeout: TimeSpan.FromMilliseconds(100)).FetchAsync(ShapeOfYou, Ct));
        Assert.Equal("Lyrics+ mirrors unavailable (timed out, timed out)", error.Message);
    }

    [Fact]
    public async Task OfflineStopsAtTheFirstMirror()
    {
        var handler = new FixtureHandler { Fallback = Reply.Broken };
        var source = new LyricsPlusSource(handler.Client(networkUp: () => false),
            new LyricsPlusOptions { Hosts = [.. Hosts.Select(h => "https://" + h)] });
        var error = await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(ShapeOfYou, Ct));
        Assert.True(error.IsOffline);
        Assert.Single(handler.Requests);
    }
}
