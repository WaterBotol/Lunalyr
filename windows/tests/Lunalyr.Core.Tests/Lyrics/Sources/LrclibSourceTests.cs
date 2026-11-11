using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

public class LrclibSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LyricQuery ShapeOfYou = new("Shape of You", "Ed Sheeran", "Divide", 233.7);

    [Fact]
    public async Task AnExactMatchWithTimingEndsTheLookupAndSendsItsLyricsfile()
    {
        var handler = new FixtureHandler().Set("lrclib.net/api/get", Reply.Fixture("lrclib/get-shape-of-you.json"));
        var found = await new LrclibSource(handler.Client()).FetchAsync(ShapeOfYou, Ct);
        Assert.NotNull(found);
        Assert.Equal(["lrclib.net/api/get"], handler.Addresses);
        Assert.Equal("Shape of You", FixtureHandler.QueryOf(handler.Requests[0].RequestUri!, "track_name"));
        Assert.Equal("Ed Sheeran", handler.Query(0, "artist_name"));
        Assert.Equal("Divide", handler.Query(0, "album_name"));
        Assert.Equal("234", handler.Query(0, "duration")); // rounded to whole seconds
        Assert.Equal(LyricFormat.Lyricsfile, found.Format);
        Assert.Contains("start_ms: 11650", found.Payload, StringComparison.Ordinal);
        Assert.Equal("[00:11.65] Invented first line\n[00:14.05] Second made-up line", found.Extras![RawLyricsExtras.Lrc]);
        Assert.Equal("Invented first line\nSecond made-up line", found.Extras[RawLyricsExtras.Plain]);
        Assert.Equal("1470135", found.Extras[RawLyricsExtras.SongId]);
        Assert.Equal(("Shape of You", "Ed Sheeran", 234.0), (found.ReportedTitle, found.ReportedArtist, found.ReportedDuration));
    }

    [Fact]
    public async Task WithoutAnExactMatchTheSearchHitThatFitsTheLengthWins()
    {
        // The ÷ (Divide) row names the album but is 29 s longer; the "Pop" row fits the length and the title.
        var handler = new FixtureHandler()
            .Set("lrclib.net/api/get", Reply.Text(404, """{"code":404,"name":"TrackNotFound","message":"Failed to find specified track"}"""))
            .Set("lrclib.net/api/search", Reply.Fixture("lrclib/search-shape-of-you.json"));
        var query = new LyricQuery("Shape of You", "Ed Sheeran", "÷ (Divide)", 234);
        var found = await new LrclibSource(handler.Client()).FetchAsync(query, Ct);
        Assert.NotNull(found);
        Assert.Equal("8352991", found.Extras![RawLyricsExtras.SongId]);
        Assert.Equal(LyricFormat.Lrc, found.Format);
        Assert.Equal(["lrclib.net/api/get", "lrclib.net/api/search"], handler.Addresses);
        Assert.Equal("Shape of You", handler.Query(1, "track_name"));
        Assert.Equal("Ed Sheeran", handler.Query(1, "artist_name"));
        Assert.Null(handler.Query(1, "album_name"));
    }

    [Fact]
    public async Task WithoutAnArtistOnlySearchIsAsked()
    {
        var handler = new FixtureHandler().Set("lrclib.net/api/search", Reply.Fixture("lrclib/search-shape-of-you.json"));
        var found = await new LrclibSource(handler.Client()).FetchAsync(new LyricQuery("Shape of You", " ", null, 234), Ct);
        Assert.NotNull(found);
        Assert.Equal(["lrclib.net/api/search"], handler.Addresses);
        Assert.Null(handler.Query(0, "artist_name"));
    }

    [Fact]
    public async Task UntimedRowsArriveAsPlainText()
    {
        var handler = new FixtureHandler()
            .Set("lrclib.net/api/get", Reply.Text(404, "{}"))
            .Set("lrclib.net/api/search", Reply.Text(200,
                """[{"id":1,"trackName":"Shape of You","artistName":"Ed Sheeran","duration":234,"plainLyrics":"Just words","syncedLyrics":null}]"""));
        var found = await new LrclibSource(handler.Client()).FetchAsync(ShapeOfYou, Ct);
        Assert.Equal(LyricFormat.Plain, found?.Format);
        Assert.Equal("Just words", found?.Payload);
    }

    [Fact]
    public async Task TimedRowsBeatPlainRowsAndUnsoundTimingRanksBelowSound()
    {
        // Same names and length: the plain row loses 12, a row the probe calls unsound loses 6.
        var rows = """
            [{"id":1,"trackName":"Shape of You","artistName":"Ed Sheeran","duration":234,"plainLyrics":"Plain"},
             {"id":2,"trackName":"Shape of You","artistName":"Ed Sheeran","duration":234,"syncedLyrics":"[00:01.00]bunched"},
             {"id":3,"trackName":"Shape of You","artistName":"Ed Sheeran","duration":234,"syncedLyrics":"[00:01.00]sound"}]
            """;
        var handler = new FixtureHandler().Set("lrclib.net/api/get", Reply.Text(404, "")).Set("lrclib.net/api/search", Reply.Text(200, rows));
        var found = await new LrclibSource(handler.Client(), probe: new BunchedProbe()).FetchAsync(ShapeOfYou, Ct);
        Assert.Equal("3", found?.Extras?[RawLyricsExtras.SongId]);
        found = await new LrclibSource(handler.Client()).FetchAsync(ShapeOfYou, Ct);
        Assert.Equal("2", found?.Extras?[RawLyricsExtras.SongId]); // the first of equals stays
    }

    [Fact]
    public async Task AnExactMatchWithUnsoundTimingIsCheckedAgainstTheSearch()
    {
        var handler = new FixtureHandler()
            .Set("lrclib.net/api/get", Reply.Text(200, """{"id":9,"trackName":"Shape of You","artistName":"Ed Sheeran","albumName":"Divide","duration":234,"syncedLyrics":"[00:01.00]bunched"}"""))
            .Set("lrclib.net/api/search", Reply.Text(200, """[{"id":10,"trackName":"Shape of You","artistName":"Ed Sheeran","albumName":"Divide","duration":234,"syncedLyrics":"[00:01.00]sound"}]"""));
        var found = await new LrclibSource(handler.Client(), probe: new BunchedProbe()).FetchAsync(ShapeOfYou, Ct);
        Assert.Equal(["lrclib.net/api/get", "lrclib.net/api/search"], handler.Addresses);
        Assert.Equal("10", found?.Extras?[RawLyricsExtras.SongId]);
    }

    [Fact]
    public async Task NothingFoundAndUnreadableSearchRepliesAreMisses()
    {
        var handler = new FixtureHandler().Set("lrclib.net/api/get", Reply.Text(404, "")).Set("lrclib.net/api/search", Reply.Text(200, "[]"));
        Assert.Null(await new LrclibSource(handler.Client()).FetchAsync(ShapeOfYou, Ct));
        handler.Set("lrclib.net/api/search", Reply.Text(200, "<html>not json"));
        Assert.Null(await new LrclibSource(handler.Client()).FetchAsync(ShapeOfYou, Ct));
        // Rows naming another song do not pass the pre-filter.
        handler.Set("lrclib.net/api/search", Reply.Text(200, """[{"trackName":"Perfect","artistName":"Ed Sheeran","duration":263,"syncedLyrics":"[00:01.00]x"}]"""));
        Assert.Null(await new LrclibSource(handler.Client()).FetchAsync(ShapeOfYou, Ct));
    }

    [Fact]
    public async Task ServerErrorsAreOutages()
    {
        var handler = new FixtureHandler { Fallback = Reply.Text(500, "") };
        var error = await Assert.ThrowsAsync<LyricSourceException>(() => new LrclibSource(handler.Client()).FetchAsync(ShapeOfYou, Ct));
        Assert.Equal(LyricSourceFailure.HttpStatus, error.Failure);
        Assert.Equal(500, error.StatusCode);
    }

    [Fact]
    public async Task ALongBusyPauseIsReportedButSearchStillRuns()
    {
        var wait = new Dictionary<string, string> { ["Retry-After"] = "30" };
        var handler = new FixtureHandler()
            .Set("lrclib.net/api/get", Reply.Text(429, "", wait))
            .Set("lrclib.net/api/search", Reply.Fixture("lrclib/search-shape-of-you.json"));
        Assert.NotNull(await new LrclibSource(handler.Client()).FetchAsync(ShapeOfYou, Ct));
        handler.Set("lrclib.net/api/search", Reply.Text(429, "", wait));
        var error = await Assert.ThrowsAsync<LyricSourceException>(() => new LrclibSource(handler.Client()).FetchAsync(ShapeOfYou, Ct));
        Assert.Equal(LyricSourceFailure.Busy, error.Failure);
    }

    [Fact]
    public async Task ASlowServiceTimesOutAndACancelledLookupIsCancelled()
    {
        var handler = new FixtureHandler { Fallback = Reply.Never };
        var source = new LrclibSource(handler.Client(), new LrclibOptions { RequestTimeout = TimeSpan.FromMilliseconds(100) });
        var error = await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(ShapeOfYou, Ct));
        Assert.Equal(LyricSourceFailure.TimedOut, error.Failure);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var slow = new LrclibSource(handler.Client(), new LrclibOptions { RequestTimeout = TimeSpan.FromMinutes(5) });
        var pending = slow.FetchAsync(ShapeOfYou, cancel.Token);
        cancel.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    /// <summary>Calls "bunched" timing unsound, as a timing-fault check would.</summary>
    private sealed class BunchedProbe : ILyricPayloadProbe
    {
        public bool IsUsable(LyricFormat format, string payload) => BasicLyricPayloadProbe.Instance.IsUsable(format, payload);

        public bool HasSoundTiming(LyricFormat format, string payload) =>
            BasicLyricPayloadProbe.Instance.HasSoundTiming(format, payload) && !payload.Contains("bunched", StringComparison.Ordinal);
    }
}
