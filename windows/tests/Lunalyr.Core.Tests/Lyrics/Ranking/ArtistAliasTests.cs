using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>Catalogue sources search again under an artist's native name, from a stubbed MusicBrainz (no network), remembered on disk.</summary>
public sealed class ArtistAliasTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Yonezu = """
        {"artists":[{"name":"米津玄師","score":100,"aliases":[
          {"name":"Kenshi Yonezu","locale":"en","primary":true},
          {"name":"よねづけんし","locale":null},
          {"name":"米津玄师","locale":"zh_Hans","primary":true},
          {"name":"ハチ","locale":"ja"}]},
         {"name":"Someone Else","score":40}]}
        """;

    private static ArtistAliasReply Reply(string json) => ArtistAliasReply.Parse(Encoding.UTF8.GetBytes(json))!;

    /// <summary>A MusicBrainz stand-in that answers every artist with the same reply and counts requests.</summary>
    private sealed class StubSearch(string body) : IArtistAliasSearch
    {
        public int Requests { get; private set; }

        public Task<ArtistAliasReply?> SearchAsync(string artist, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(ArtistAliasReply.Parse(Encoding.UTF8.GetBytes(body)));
        }
    }

    // Ported from ArtistAliasTests.swift: testPicksNativeNamesOfTheOneArtistNamed
    [Fact]
    public void PicksNativeNamesOfTheOneArtistNamed()
    {
        Assert.Equal(["米津玄师", "米津玄師"], ArtistAliases.Pick(Reply(Yonezu), "Kenshi Yonezu"));
        // Two artists strongly matching the name: nothing is guessed.
        Assert.Empty(ArtistAliases.Pick(Reply("""{"artists":[{"name":"Aurora","score":100,"aliases":[{"name":"オーロラ","locale":"ja"}]},{"name":"Aurora","score":100}]}"""), "Aurora"));
        // A weak match is not this artist.
        Assert.Empty(ArtistAliases.Pick(Reply("""{"artists":[{"name":"米津玄師","score":60,"aliases":[{"name":"Kenshi Yonezu"}]}]}"""), "Kenshi Yonezu"));
        Assert.True(ArtistAliases.IsOtherScript("아이유"));
        Assert.False(ArtistAliases.IsOtherScript("Björk"));
    }

    // Ported from ArtistAliasTests.swift: testAnswersAreRememberedOnDiskAndAskedOnce
    // (the User-Agent check belongs to the HTTP workstream's MusicBrainz client)
    [Fact]
    public async Task AnswersAreRememberedOnDiskAndAskedOnce()
    {
        var clock = new FakeClock();
        var search = new StubSearch(Yonezu);
        var aliases = new ArtistAliases(_folder.Path, search, clock);
        var first = await aliases.NamesAsync("Kenshi Yonezu", Ct);
        Assert.Equal("米津玄师", first[0]);
        await aliases.NamesAsync("kenshi  YONEZU", Ct);
        Assert.Equal(1, search.Requests);
        var reopened = new ArtistAliases(_folder.Path, search, clock);
        await reopened.NamesAsync("Kenshi Yonezu", Ct);
        Assert.Equal(1, search.Requests);
        // After 30 days the answer is asked again.
        clock.Advance(ArtistAliases.Lifetime);
        await reopened.NamesAsync("Kenshi Yonezu", Ct);
        Assert.Equal(2, search.Requests);
        Assert.Equal("https://musicbrainz.org/ws/2/artist?query=%22Kenshi%20Yonezu%22&limit=5&fmt=json",
            ArtistAliasReply.SearchUri("Kenshi \"Yonezu\"").AbsoluteUri);
    }

    // Ported from ArtistAliasTests.swift: testCatalogueSearchesAgainUnderTheNativeNameAndKeepsTheLibraryArtist
    [Fact]
    public async Task CatalogueSearchesAgainUnderTheNativeNameAndKeepsTheLibraryArtist()
    {
        var aliases = new ArtistAliases(_folder.Path, new StubSearch(Yonezu), new FakeClock());
        var query = new LyricQuery("Lemon", "Kenshi Yonezu", null, 255);
        var asked = new List<string>();
        var found = await ArtistAliases.WithAliasesAsync(NetEase, query, aliases, enabled: true, (q, _) =>
        {
            asked.Add(q.Artist);
            return ValueTask.FromResult(q.Artist == "米津玄師"
                ? new LyricCandidate(NetEase, L.Plain(["Lemon"], "", ""), "Lemon", "米津玄師", 255, 0)
                : null);
        }, Ct);
        Assert.Equal(["Kenshi Yonezu", "米津玄师", "米津玄師"], asked);
        Assert.Equal("Kenshi Yonezu", found?.Artist);
        Assert.Null(LyricRanking.Rejection(found!, query));
    }

    // Ported from ArtistAliasTests.swift: testNoSecondSearchWhenNotNeeded
    [Fact]
    public async Task NoSecondSearchWhenNotNeeded()
    {
        var search = new StubSearch(Yonezu);
        var aliases = new ArtistAliases(_folder.Path, search, new FakeClock());
        var query = new LyricQuery("Lemon", "Kenshi Yonezu", null, 255);
        var hit = new LyricCandidate(NetEase, L.Plain(["Lemon"], "", ""), "Lemon", "Kenshi Yonezu", 255, 0);
        var calls = 0;
        // Found under the library's name; a source that is not a catalogue; the setting off.
        await ArtistAliases.WithAliasesAsync(NetEase, query, aliases, true, (_, _) => { calls++; return ValueTask.FromResult<LyricCandidate?>(hit); }, Ct);
        await ArtistAliases.WithAliasesAsync(Lrclib, query, aliases, true, (_, _) => { calls++; return ValueTask.FromResult<LyricCandidate?>(null); }, Ct);
        await ArtistAliases.WithAliasesAsync(QQMusic, query, aliases, false, (_, _) => { calls++; return ValueTask.FromResult<LyricCandidate?>(null); }, Ct);
        Assert.Equal(3, calls);
        Assert.Equal(0, search.Requests);
    }

    // Windows-only: requests to MusicBrainz are spaced 1.1 s apart, and failures are not remembered.
    [Fact]
    public async Task RequestsAreSpacedAndFailuresAreAskedAgain()
    {
        var clock = new FakeClock();
        var failing = new StubSearch("not json");
        var aliases = new ArtistAliases(_folder.Path, failing, clock);
        Assert.Empty(await aliases.NamesAsync("Kenshi Yonezu", Ct));
        // The second request waits for its slot on the clock.
        var second = aliases.NamesAsync("Kenshi Yonezu", Ct);
        for (var i = 0; i < 200 && !second.IsCompleted; i++)
        {
            if (!clock.AdvanceToNextTimer())
            {
                await Task.Delay(5, Ct);
            }
        }
        Assert.Empty(await second);
        Assert.Equal(2, failing.Requests);
    }
}
