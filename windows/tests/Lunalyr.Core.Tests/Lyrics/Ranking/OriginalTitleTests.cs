using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Pipeline;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Core.Tests.Lyrics.Sources;
using static Lunalyr.Core.Lyrics.LyricSourceId;
using static Lunalyr.Core.Tests.Lyrics.Ranking.LanguageVersionTests;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>
/// A song listed under an English title is searched again under its original one, read from Apple's catalogue
/// through the shared HTTP client, replayed from fixtures (no network): the Mac test's catalogue replies, and replies
/// recorded from the iTunes Search API.
/// </summary>
public sealed class OriginalTitleTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FixtureHandler _catalogue = new FixtureHandler()
        .Set("itunes.apple.com/search", Reply.Fixture("itunes/mac-listed.json"))
        .Set("itunes.apple.com/lookup", Reply.Fixture("itunes/mac-original.json"));

    public void Dispose()
    {
        _catalogue.Dispose();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OriginalTitles Titles(string storefront = "us") =>
        new(_folder.Path, new ItunesCatalogue(_catalogue.Client()), storefront, TimeSpan.Zero);

    private static ValueTask<LyricCandidate?> Answer(LyricCandidate? candidate) => ValueTask.FromResult(candidate);

    private static string[] QueryNames(Uri uri) =>
        [.. uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => Uri.UnescapeDataString(part.Split('=')[0]))];

    // Ported from LanguageVersionTests.swift: testReadsTheOriginalTitleOfTheSameCatalogueEntry
    [Fact]
    public async Task ReadsTheOriginalTitleOfTheSameCatalogueEntry()
    {
        var titles = Titles();
        var found = await titles.OriginalAsync(Query("Racing into the Night"), Ct);
        Assert.Equal(new OriginalTitle("夜に駆ける", "夜に駆ける - Single"), found);
        Assert.Equal(2, _catalogue.Requests.Count);
        // Only the artist and title are sent, as for cover art.
        Assert.Equal(new HashSet<string> { "term", "entity", "country", "limit" }, QueryNames(_catalogue.Requests[0].RequestUri!).ToHashSet());
        Assert.Equal("YOASOBI Racing into the Night", _catalogue.Query(0, "term"));
        Assert.Equal("/lookup", _catalogue.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("1001", _catalogue.Query(1, "id"));
        Assert.Equal("jp", _catalogue.Query(1, "country"));
        Assert.Equal("ja_jp", _catalogue.Query(1, "lang"));
        // Remembered, also on disk: asked once.
        _ = await titles.OriginalAsync(Query("racing into the  NIGHT"), Ct);
        var reopened = Titles();
        var again = await reopened.OriginalAsync(Query("Racing into the Night"), Ct);
        Assert.Equal("夜に駆ける", again?.Title);
        Assert.Equal(2, _catalogue.Requests.Count);
    }

    // Ported from LanguageVersionTests.swift: testASongWithoutAnotherTitleIsRememberedAsNone
    [Fact]
    public async Task ASongWithoutAnotherTitleIsRememberedAsNone()
    {
        var titles = Titles();
        _catalogue.Set("itunes.apple.com/lookup", Reply.Fixture("itunes/mac-unchanged.json"));
        Assert.Null(await titles.OriginalAsync(Query("Racing into the Night"), Ct));
        _ = await titles.OriginalAsync(Query("Racing into the Night"), Ct);
        Assert.Equal(2, _catalogue.Requests.Count);
        // Nothing to ask for a title already in Japanese, or for a song whose length is far off.
        _ = await titles.OriginalAsync(Query("夜に駆ける"), Ct);
        var other = new LyricQuery("Racing into the Night", "YOASOBI", null, 300);
        _catalogue.Set("itunes.apple.com/lookup", Reply.Fixture("itunes/mac-original.json"));
        Assert.Null(await titles.OriginalAsync(other, Ct));
        // Searched, but no entry of that length.
        Assert.Equal(3, _catalogue.Requests.Count);
    }

    // Ported from LanguageVersionTests.swift: testFallbackSearchesUnderTheJapaneseTitle
    [Fact]
    public async Task FallbackSearchesUnderTheJapaneseTitle()
    {
        var asked = new List<string>();
        var playing = Query("Racing into the Night");
        var japanese = Candidate(Lrclib, LanguageVersionTests.Japanese, "夜に駆ける", words: true);
        var found = await OriginalTitles.WithOriginalTitleAsync(Lrclib, playing, Titles(), enabled: true, (q, _) =>
        {
            asked.Add(q.Title);
            return Answer(q.Title == "夜に駆ける" ? japanese : null);
        }, Ct);
        Assert.Equal(["Racing into the Night", "夜に駆ける"], asked);
        // Recorded under the listed title, so the recording check and the cache key hold.
        Assert.NotNull(found);
        Assert.Equal("Racing into the Night", found.Title);
        Assert.True(found.ViaOriginalTitle);
        Assert.Null(LyricRanking.Rejection(found, playing));
    }

    // Ported from LanguageVersionTests.swift: testFallbackReplacesAnEnglishVersionOrEnglishOnlyLyricsForAnEastAsianGenre
    [Fact]
    public async Task FallbackReplacesAnEnglishVersionOrEnglishOnlyLyricsForAnEastAsianGenre()
    {
        var titles = Titles();
        var japanese = Candidate(Kugou, LanguageVersionTests.Japanese, "夜に駆ける", words: true);
        // "English Version" under the listed title.
        var tagged = Candidate(Kugou, LanguageVersionTests.English, "Racing into the Night (English Version)", words: true);
        var found = await OriginalTitles.WithOriginalTitleAsync(Kugou, Query("Racing into the Night"), titles, enabled: true,
            (q, _) => Answer(q.Title == "夜に駆ける" ? japanese : tagged), Ct);
        Assert.True(found?.ViaOriginalTitle);
        // English lyrics with no tag, for a J-Pop song.
        var untagged = Candidate(Kugou, LanguageVersionTests.English, "Racing into the Night", words: true);
        found = await OriginalTitles.WithOriginalTitleAsync(Kugou, Query("Racing into the Night", genre: "J-Pop"), titles, enabled: true,
            (q, _) => Answer(q.Title == "夜に駆ける" ? japanese : untagged), Ct);
        Assert.NotNull(found);
        Assert.True(found.ViaOriginalTitle);
        Assert.Equal(SungScript.Native, LyricLanguage.ScriptOf(found));
    }

    // Ported from LanguageVersionTests.swift: testFallbackAsksNothingWhenTheListedTitleFoundTheSong
    [Fact]
    public async Task FallbackAsksNothingWhenTheListedTitleFoundTheSong()
    {
        var titles = Titles();
        var japanese = Candidate(NetEase, LanguageVersionTests.Japanese, "Racing into the Night", words: true);
        var first = await OriginalTitles.WithOriginalTitleAsync(NetEase, Query("Racing into the Night", genre: "J-Pop"), titles, enabled: true,
            (_, _) => Answer(japanese), Ct);
        Assert.False(first?.ViaOriginalTitle);
        // English lyrics for a song that names its English version are wanted.
        var searches = 0;
        var second = await OriginalTitles.WithOriginalTitleAsync(Lrclib, Query("Idol (English Version)", genre: "J-Pop"), titles, enabled: true,
            (_, _) =>
            {
                searches++;
                return Answer(null);
            }, Ct);
        Assert.Null(second);
        // Not a source searched again, or the setting is off.
        _ = await OriginalTitles.WithOriginalTitleAsync(LyricsOvh, Query("Racing into the Night"), titles, enabled: true, (_, _) => Answer(null), Ct);
        _ = await OriginalTitles.WithOriginalTitleAsync(Lrclib, Query("Racing into the Night"), titles, enabled: false, (_, _) => Answer(null), Ct);
        Assert.Equal(1, searches);
        Assert.Empty(_catalogue.Requests);
    }

    // Ported from LanguageVersionTests.swift: testFallbackKeepsTheFirstAnswerWhenTheOriginalTitleFindsOnlyEnglish
    [Fact]
    public async Task FallbackKeepsTheFirstAnswerWhenTheOriginalTitleFindsOnlyEnglish()
    {
        var english = Candidate(Lrclib, LanguageVersionTests.English, "夜に駆ける", words: true);
        var first = Candidate(Lrclib, LanguageVersionTests.English, "Racing into the Night (English Version)", words: true);
        var found = await OriginalTitles.WithOriginalTitleAsync(Lrclib, Query("Racing into the Night"), Titles(), enabled: true,
            (q, _) => Answer(q.Title == "夜に駆ける" ? english : first), Ct);
        Assert.Equal("Racing into the Night (English Version)", found?.Title);
        Assert.False(found?.ViaOriginalTitle);
    }

    // ---- Windows ----

    /// <summary>
    /// Replies recorded from the iTunes Search API (12 Nov 2026): Australia's storefront lists YOASOBI's アイドル and its
    /// English recording both as "Idol", on singles of the same length. The first of the equally near entries is the
    /// English one, which Japan's storefront also calls "Idol", so no original title is guessed and "none" is
    /// remembered.
    /// </summary>
    [Fact]
    public async Task RecordedAustralianCatalogueListsBothIdolsAlike()
    {
        _catalogue.Set("itunes.apple.com/search", Reply.Fixture("itunes/search-au-yoasobi-idol.json"));
        _catalogue.Responder = request => request.RequestUri!.AbsolutePath == "/lookup"
            ? Reply.Fixture($"itunes/lookup-jp-{FixtureHandler.QueryOf(request.RequestUri, "id")}.json")
            : null;
        var playing = new LyricQuery("Idol", "YOASOBI", "Idol - Single", 213.234) { Genre = "J-Pop" };
        var titles = Titles("au");
        Assert.Null(await titles.OriginalAsync(playing, Ct));
        Assert.Equal(2, _catalogue.Requests.Count);
        Assert.Equal("au", _catalogue.Query(0, "country"));
        Assert.Equal("1688334537", _catalogue.Query(1, "id"));
        _ = await titles.OriginalAsync(playing, Ct);
        Assert.Equal(2, _catalogue.Requests.Count);

        // The recorded replies read as the catalogue sent them, ids past 32 bits included.
        var listed = ItunesCatalogue.Parse(Fixtures.Bytes("itunes/search-au-yoasobi-idol.json"));
        Assert.NotNull(listed);
        Assert.Equal(15, listed.Count);
        Assert.Contains(listed, song => song.TrackId == 6826993592);
        Assert.Equal(1688334537, OriginalTitles.Match(listed, playing)?.TrackId);
        // The other "Idol" single is the Japanese recording.
        var japanese = ItunesCatalogue.Parse(Fixtures.Bytes("itunes/lookup-jp-1679278167.json"));
        Assert.Equal(new OriginalTitle("アイドル", "アイドル - Single"), OriginalTitles.OriginalName(japanese?.Single(), playing));
    }

    /// <summary>A request that fails is not remembered, so it is asked again next time (the Mac's comment).</summary>
    [Fact]
    public async Task AFailedRequestIsAskedAgain()
    {
        _catalogue.Set("itunes.apple.com/search", Reply.Text(503, ""));
        var titles = Titles();
        Assert.Null(await titles.OriginalAsync(Query("Racing into the Night"), Ct));
        Assert.Null(await titles.OriginalAsync(Query("Racing into the Night"), Ct));
        Assert.Equal(2, _catalogue.Requests.Count);
        // Japan's own storefront already lists the original title: nothing to ask.
        Assert.Null(await Titles("JP").OriginalAsync(Query("Racing into the Night"), Ct));
        Assert.Equal(2, _catalogue.Requests.Count);
    }

    /// <summary>The first name of a credit is searched for, and artists match by whole names.</summary>
    [Fact]
    public void CatalogueArtistsMatchByWholeNames()
    {
        Assert.Equal("YOASOBI", OriginalTitles.PrimaryArtist("YOASOBI & Ayase"));
        Assert.Equal("Kenshi Yonezu", OriginalTitles.PrimaryArtist(" Kenshi Yonezu , Daoko"));
        Assert.Equal(" & ", OriginalTitles.PrimaryArtist(" & "));
        Assert.True(OriginalTitles.ArtistMatches("YOASOBI", "YOASOBI feat. Someone"));
        Assert.True(OriginalTitles.ArtistMatches("Beyoncé & JAY-Z", "beyonce"));
        Assert.False(OriginalTitles.ArtistMatches("Ava Max", "Max"));
        Assert.False(OriginalTitles.ArtistMatches(null, "YOASOBI"));
    }

    /// <summary>
    /// Through the source set, as a lookup asks: LRCLIB finds nothing under the English title, the original title is
    /// read from the catalogue, and LRCLIB's Japanese lyrics come back under the listed title. Off, nothing more is asked.
    /// </summary>
    [Fact]
    public async Task TheSourceSetSearchesUnderTheOriginalTitle()
    {
        var lrclib = new TitleSource(Lrclib, "夜に駆ける");
        var set = new LyricSourceSet([lrclib], titles: Titles());
        var found = await set.Fetch(Lrclib, Query("Racing into the Night"), "", Ct);
        Assert.NotNull(found);
        Assert.Equal("Racing into the Night", found.Title);
        Assert.True(found.ViaOriginalTitle);
        Assert.Equal(["Racing into the Night", "夜に駆ける"], lrclib.Asked);

        var off = new LyricSourceSet([new TitleSource(Lrclib, "夜に駆ける")], titles: Titles()) { UsesOriginalTitles = false };
        Assert.Null(await off.Fetch(Lrclib, Query("Racing into the Night"), "", Ct));
        Assert.Equal(2, _catalogue.Requests.Count);
    }

    /// <summary>A source that has lyrics only under one title, reporting that title and the playing artist and length.</summary>
    private sealed class TitleSource(LyricSourceId id, string title) : ILyricSource
    {
        private readonly List<string> _asked = [];

        public LyricSourceId Id => id;

        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (_asked)
                {
                    return [.. _asked];
                }
            }
        }

        public Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
        {
            lock (_asked)
            {
                _asked.Add(query.Title);
            }
            if (query.Title != title)
            {
                return Task.FromResult<RawLyrics?>(null);
            }
            var lrc = string.Join('\n', LanguageVersionTests.Japanese.Select((line, i) => $"[00:{(i * 4) + 1:00}.00]{line}"));
            return Task.FromResult<RawLyrics?>(new RawLyrics(id, LyricFormat.Lrc, lrc, title, query.Artist, query.Duration));
        }
    }
}
