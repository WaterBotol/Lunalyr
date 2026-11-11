using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

/// <summary>MusicBrainz's artist search over the shared client, against replies recorded on 11 Nov 2026.</summary>
public class MusicBrainzArtistSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Search = "musicbrainz.org/ws/2/artist";

    [Fact]
    public async Task TheSearchAsksForTheQuotedNameWithTheGenericAgentAndReadsTheAliases()
    {
        var handler = new FixtureHandler().Set(Search, Reply.Fixture("musicbrainz/artist-kenshi-yonezu.json"));
        var reply = await new MusicBrainzArtistSearch(handler.Client()).SearchAsync("Kenshi Yonezu", Ct);
        Assert.NotNull(reply);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("\"Kenshi Yonezu\"", handler.Query(0, "query"));
        Assert.Equal(("5", "json"), (handler.Query(0, "limit"), handler.Query(0, "fmt")));
        Assert.Equal(LyricHttpOptions.DefaultUserAgent, request.Headers.UserAgent.ToString());
        Assert.Equal(["米津玄師", "켄시 요네즈"], ArtistAliases.Pick(reply, "Kenshi Yonezu"));
    }

    [Fact]
    public async Task NativeNamesComeFirstAndAtMostTwoAreKept()
    {
        var handler = new FixtureHandler().Set(Search, Reply.Fixture("musicbrainz/artist-jay-chou.json"));
        var reply = await new MusicBrainzArtistSearch(handler.Client()).SearchAsync("Jay Chou", Ct);
        Assert.NotNull(reply);
        // Primary aliases in Chinese, Japanese or Korean locales lead; the Latin spellings are what the library had.
        Assert.Equal(["周杰伦", "ジェイ・チョウ"], ArtistAliases.Pick(reply, "Jay Chou"));
    }

    [Fact]
    public async Task AnyFailureIsANullReplySoItIsAskedAgainLater()
    {
        foreach (var reply in new[] { Reply.Text(503, "{}"), Reply.Text(200, "<html>busy</html>"), Reply.Broken,
                     new Reply(200, new byte[MusicBrainzArtistSearch.MaxReplyBytes + 1]) })
        {
            var handler = new FixtureHandler().Set(Search, reply);
            Assert.Null(await new MusicBrainzArtistSearch(handler.Client()).SearchAsync("Kenshi Yonezu", Ct));
        }
    }
}
