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
        // Searched, but no entry of that length; since the Mac's 1cdde21 Japan's storefront is then searched by title
        // too (the same invented reply here, with no native-script entry), so four requests where the Mac test had three.
        Assert.Equal(4, _catalogue.Requests.Count);
        Assert.Equal(("jp", "Racing into the Night"), (_catalogue.Query(3, "country"), _catalogue.Query(3, "term")));
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
        // Japan's own storefront goes straight to its title search (the Mac's 1cdde21; until then nothing was asked),
        // which fails here too and is asked again.
        Assert.Null(await Titles("JP").OriginalAsync(Query("Racing into the Night"), Ct));
        Assert.Equal(3, _catalogue.Requests.Count);
        Assert.Equal(("jp", "Racing into the Night"), (_catalogue.Query(2, "country"), _catalogue.Query(2, "term")));
    }

    // ---- The Mac's next/integration 1cdde21, from the Mac session's description (its tests aren't in the checkout) ----

    /// <summary>
    /// Recorded 13 Nov 2026 (iTunes Search API, the generic agent): Australia's storefront has no "Racing into the
    /// Night" by YOASOBI and no native-script YOASOBI entry of its length, so Japan's storefront is searched by the title
    /// alone, where YOASOBI's first native-script entry of the playing length is 夜に駆ける (261.013 s). Shayne Orok's
    /// 夜に駆ける (258.2 s) is another artist's. Nothing is looked up by id.
    /// </summary>
    [Fact]
    public async Task RecordedRacingIntoTheNightIsFoundInJapansStorefront()
    {
        UseRecordedRacingIntoTheNight();
        var playing = new LyricQuery("Racing into the Night", "YOASOBI", null, 261.013);
        var found = await Titles("au").OriginalAsync(playing, Ct);
        Assert.Equal(new OriginalTitle("夜に駆ける", "夜に駆ける - Single"), found);
        Assert.Equal(["itunes.apple.com/search", "itunes.apple.com/search"], _catalogue.Addresses);
        Assert.Equal(("au", "YOASOBI Racing into the Night"), (_catalogue.Query(0, "country"), _catalogue.Query(0, "term")));
        Assert.Equal(("jp", "Racing into the Night"), (_catalogue.Query(1, "country"), _catalogue.Query(1, "term")));
        // Australia's reply (romanised and translated names) has no native-script YOASOBI entry of that length, and
        // neither has Japan's reply to the artist + title search (also recorded): only the title alone finds the single.
        Assert.Null(OriginalTitles.NativeEntry(ItunesCatalogue.Parse(Fixtures.Bytes("itunes/search-au-yoasobi-racing-into-the-night.json"))!, playing));
        Assert.Null(OriginalTitles.NativeEntry(ItunesCatalogue.Parse(Fixtures.Bytes("itunes/search-jp-yoasobi-racing-into-the-night.json"))!, playing));
    }

    private void UseRecordedRacingIntoTheNight() =>
        _catalogue.Responder = request => request.RequestUri!.AbsolutePath == "/search"
            ? Reply.Fixture(FixtureHandler.QueryOf(request.RequestUri, "country") == "jp"
                ? "itunes/search-jp-racing-into-the-night.json"
                : "itunes/search-au-yoasobi-racing-into-the-night.json")
            : null;

    /// <summary>
    /// Step (b): when this storefront has no entry with the listed title, the artist's first native-script entry of
    /// the playing length in the same reply is the original title, with no further request. Another version, another
    /// artist, a length outside max(3 s, 2%) or no length at all don't count.
    /// </summary>
    [Fact]
    public void TheArtistsNativeEntryOfThePlayingLengthIsTheOriginalTitle()
    {
        var playing = new LyricQuery("Racing into the Night", "YOASOBI", null, 261);
        CatalogueSong Song(string name, string artist, long? millis) => new(1, name, artist, name + " - Single", millis);
        Assert.Equal(new OriginalTitle("夜に駆ける", "夜に駆ける - Single"), OriginalTitles.NativeEntry(
        [
            Song("夜に駆ける (Live)", "YOASOBI", 261_000), Song("夜に駆ける", "Someone Else", 261_000), Song("ハルカ", "YOASOBI", 243_613),
            Song("夜に駆ける", "YOASOBI", null), Song("Into The Night", "YOASOBI", 261_000), Song("夜に駆ける", "YOASOBI", 266_100),
            Song("あの夢をなぞって", "YOASOBI", 256_000),
        ], playing));
        // 2% of 261 s is 5.22 s; 266.3 s is past it.
        Assert.Null(OriginalTitles.NativeEntry([Song("夜に駆ける", "YOASOBI", 266_300)], playing));
        // Within 3 s for a short song.
        Assert.NotNull(OriginalTitles.NativeEntry([Song("夜", "YOASOBI", 62_900)], new LyricQuery("Night", "YOASOBI", null, 60)));
    }

    [Fact]
    public async Task ThisStorefrontsNativeEntryNeedsNoJapaneseSearch()
    {
        _catalogue.Responder = request => Reply.Text(200, """
            {"resultCount":1,"results":[{"wrapperType":"track","kind":"song","trackId":7,"artistName":"YOASOBI","collectionName":"夜に駆ける - Single","trackName":"夜に駆ける","trackTimeMillis":261013}]}
            """);
        Assert.Equal("夜に駆ける", (await Titles("us").OriginalAsync(new LyricQuery("Racing into the Night", "YOASOBI", null, 261.013), Ct))?.Title);
        Assert.Single(_catalogue.Requests);
    }

    /// <summary>A "none" stored by the earlier rule (no "rule" field) is asked again; a title it found still stands.</summary>
    [Fact]
    public async Task ANoneFromTheEarlierRuleIsAskedAgain()
    {
        UseRecordedRacingIntoTheNight();
        var playing = new LyricQuery("Racing into the Night", "YOASOBI", null, 261.013);
        var idol = new LyricQuery("Idol", "YOASOBI", null, 213.234);
        var stored = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        File.WriteAllText(Path.Combine(_folder.Path, "OriginalTitles.json"), $$$"""
            {"{{{OriginalTitles.Key(playing)}}}":{"storedAt":"{{{stored}}}"},
             "{{{OriginalTitles.Key(idol)}}}":{"found":{"title":"アイドル","album":"アイドル - Single"},"storedAt":"{{{stored}}}"}}
            """);
        var titles = Titles("au");
        Assert.Equal("アイドル", (await titles.OriginalAsync(idol, Ct))?.Title);
        Assert.Empty(_catalogue.Requests);
        Assert.Equal("夜に駆ける", (await titles.OriginalAsync(playing, Ct))?.Title);
        Assert.Equal(2, _catalogue.Requests.Count);
        // Stored under the current rule: not asked again, also after a restart.
        Assert.Equal("夜に駆ける", (await Titles("au").OriginalAsync(playing, Ct))?.Title);
        Assert.Equal(2, _catalogue.Requests.Count);
        Assert.Contains("\"rule\":2", File.ReadAllText(Path.Combine(_folder.Path, "OriginalTitles.json")), StringComparison.Ordinal);
    }

    /// <summary>
    /// The original title is also looked for when the listed title found only Latin-script lyrics and the artist is
    /// East Asian: an East Asian genre, an artist or album in native script, or a MusicBrainz country of JP, KR, CN,
    /// TW, HK or MO. Here no genre is given; MusicBrainz (YOASOBI, recorded 13 Nov 2026) says JP.
    /// </summary>
    [Fact]
    public async Task LatinLyricsOfAnEastAsianArtistAreSearchedAgain()
    {
        var japanese = Candidate(Lrclib, LanguageVersionTests.Japanese, "夜に駆ける", words: true);
        var english = Candidate(Lrclib, LanguageVersionTests.English, "Racing into the Night", words: true);
        var search = new CountingAliasSearch("musicbrainz/artist-yoasobi.json");
        var aliases = new ArtistAliases(_folder.Path, search);
        // The artist's country isn't waited for (that held every new artist's lyrics back): learned in the background,
        // it counts from the next lookup on.
        await aliases.CountryAsync("YOASOBI", Ct);
        var found = await OriginalTitles.WithOriginalTitleAsync(Lrclib, Query("Racing into the Night"), Titles(), enabled: true,
            (q, _) => Answer(q.Title == "夜に駆ける" ? japanese : english), Ct, aliases);
        Assert.True(found?.ViaOriginalTitle);
        Assert.Equal(1, search.Count);

        // Without the country, and with no genre, English lyrics stand and nothing is looked up.
        var none = await OriginalTitles.WithOriginalTitleAsync(Lrclib, Query("Racing into the Night"), Titles(), enabled: true,
            (q, _) => Answer(q.Title == "夜に駆ける" ? japanese : english), Ct);
        Assert.False(none?.ViaOriginalTitle);
        Assert.Equal(2, _catalogue.Requests.Count); // the first lookup's search and lookup only
    }

    [Fact]
    public async Task WhoCountsAsAnEastAsianArtist()
    {
        var yoasobi = new CountingAliasSearch("musicbrainz/artist-yoasobi.json");
        var aliases = new ArtistAliases(_folder.Path, yoasobi);
        Assert.True(await OriginalTitles.IsEastAsianAsync(new LyricQuery("Song", "Someone", null, 200) { Genre = "J-Pop" }, null, Ct));
        Assert.True(await OriginalTitles.IsEastAsianAsync(new LyricQuery("Song", "米津玄師", null, 200), null, Ct));
        Assert.True(await OriginalTitles.IsEastAsianAsync(new LyricQuery("Song", "Someone", "夜に駆ける - Single", 200), null, Ct));
        Assert.Equal(0, yoasobi.Count); // genre and script are read first
        // An artist not asked about yet isn't waited for: no for now, and its country is learned in the background.
        Assert.False(await OriginalTitles.IsEastAsianAsync(new LyricQuery("Song", "YOASOBI", null, 200), aliases, Ct));
        await aliases.CountryAsync("YOASOBI", Ct); // joins the background request already in flight
        Assert.True(await OriginalTitles.IsEastAsianAsync(new LyricQuery("Song", "YOASOBI", null, 200), aliases, Ct));
        Assert.True(await OriginalTitles.IsEastAsianAsync(new LyricQuery("Other", "yoasobi", null, 200), aliases, Ct));
        Assert.Equal(1, yoasobi.Count); // kept with the artist's other names
        Assert.Contains("\"country\":\"JP\"", File.ReadAllText(Path.Combine(_folder.Path, "ArtistAliases.json")), StringComparison.Ordinal);

        var american = new CountingAliasSearch(null, """{"artists":[{"name":"Wallows","score":100,"country":"US","aliases":[]}]}""");
        var wallows = new ArtistAliases(_folder.Path, american);
        await wallows.CountryAsync("Wallows", Ct);
        Assert.False(await OriginalTitles.IsEastAsianAsync(new LyricQuery("Pleaser", "Wallows", null, 188), wallows, Ct));
        Assert.False(await OriginalTitles.IsEastAsianAsync(new LyricQuery("Pleaser", "Wallows", null, 188), null, Ct));
    }

    /// <summary>An alias entry stored before countries were kept still gives names, and is asked again for the country.</summary>
    [Fact]
    public async Task AnAliasEntryWithoutACountryIsAskedAgainForIt()
    {
        File.WriteAllText(Path.Combine(_folder.Path, "ArtistAliases.json"),
            $$$"""{"yoasobi":{"names":[],"storedAt":"{{{DateTimeOffset.UtcNow.AddDays(-1):O}}}"}}""");
        var search = new CountingAliasSearch("musicbrainz/artist-yoasobi.json");
        var aliases = new ArtistAliases(_folder.Path, search);
        Assert.Empty(await aliases.NamesAsync("YOASOBI", Ct));
        Assert.Equal(0, search.Count);
        Assert.Equal("JP", await aliases.CountryAsync("YOASOBI", Ct));
        Assert.Equal(1, search.Count);
        // MusicBrainz naming no country is remembered as none.
        var nowhere = new CountingAliasSearch(null, """{"artists":[{"name":"Nobody","score":100,"aliases":[]}]}""");
        var other = new ArtistAliases(_folder.Path, nowhere);
        Assert.Null(await other.CountryAsync("Nobody", Ct));
        Assert.Null(await other.CountryAsync("Nobody", Ct));
        Assert.Equal(1, nowhere.Count);
    }

    private sealed class CountingAliasSearch(string? fixture, string? json = null) : IArtistAliasSearch
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task<ArtistAliasReply?> SearchAsync(string artist, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(ArtistAliasReply.Parse(fixture is null ? System.Text.Encoding.UTF8.GetBytes(json!) : Fixtures.Bytes(fixture)));
        }
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
