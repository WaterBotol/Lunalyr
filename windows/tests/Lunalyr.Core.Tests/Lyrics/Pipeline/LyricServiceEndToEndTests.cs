using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Pipeline;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Core.Tests.Lyrics.Sources;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Pipeline;

/// <summary>
/// Whole lookups through <see cref="LyricService"/>: every built-in source over one HTTP client replaying recorded
/// replies (<see cref="FixtureHandler"/>, never the network), the real parsers, credits, ranking, lookup and disk cache.
/// The system clock with short limits, as the app runs.
/// </summary>
public sealed class LyricServiceEndToEndTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempFolder _cache = new();

    public void Dispose() => _cache.Dispose();

    private static readonly LyricLookupTiming Quick = new()
    {
        // Real time: generous enough that a busy machine (parallel test assemblies) still answers inside the grace
        // period. Fake sources answer at once, so the lookup completes as soon as nothing pending could win.
        Grace = TimeSpan.FromSeconds(3),
        UpgradeSettle = TimeSpan.FromMilliseconds(500),
        AppleRetry = TimeSpan.FromMilliseconds(100),
        MusicFirst = TimeSpan.FromMilliseconds(100),
        LrclibLimit = TimeSpan.FromSeconds(10),
        SourceLimit = TimeSpan.FromSeconds(10),
    };

    /// <summary>Every source switched on, in the order a fresh install uses, Accuracy first; no local folder chosen.</summary>
    private static readonly LyricSourceSnapshot Everything = LyricSourceSnapshot.From(LyricSourceSnapshot.RecommendedOrder,
        LyricSourceSnapshot.RecommendedOrder.ToHashSet(), LyricMatchMode.AccuracyFirst);

    private const string AppleUnavailable = "Apple Music for Windows keeps no lyrics Lunalyr can read.";

    /// <summary>A source the platform supplies: Apple Music, either unavailable (as on Windows) or answering with TTML.</summary>
    private sealed class AppleMusic(string? ttml, LyricQuery? reports = null) : ILyricSource
    {
        public LyricSourceId Id => AppleMusicCache;

        public LyricSourceAvailability Availability =>
            ttml is null ? LyricSourceAvailability.Unavailable(AppleUnavailable) : LyricSourceAvailability.Available;

        public int Asked { get; private set; }

        public Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
        {
            Asked++;
            var names = reports ?? query;
            return Task.FromResult<RawLyrics?>(new RawLyrics(AppleMusicCache, LyricFormat.Ttml, ttml!, names.Title, names.Artist, names.Duration));
        }
    }

    /// <summary>Every service answering "nothing here" unless a test says otherwise.</summary>
    private static FixtureHandler Quiet() => new FixtureHandler
    {
        Fallback = Reply.Text(404, "{}"),
        Responder = request => request.RequestUri!.Host.StartsWith("lyricsplus.", StringComparison.Ordinal)
            ? Reply.Text(404, ServiceReplies.LyricsPlusNotFound)
            : null,
    }
        .Set("lrclib.net/api/get", Reply.Text(404, ServiceReplies.LrclibNotFound))
        .Set("lrclib.net/api/search", Reply.Text(200, "[]"))
        .Set("music.163.com/api/search/get", Reply.Text(200, ServiceReplies.NetEaseNoSongs))
        .Set("c.y.qq.com/soso/fcgi-bin/client_search_cp", Reply.Text(200, ServiceReplies.QQNoSongs))
        .Set("mobileservice.kugou.com/api/v3/search/song", Reply.Text(200, ServiceReplies.KugouNoSongs))
        .Set("search.kuwo.cn/r.s", Reply.Text(200, ServiceReplies.KuwoNoSongs));

    private LyricService Service(FixtureHandler handler, ILyricSource? apple = null, bool online = true, IArtistAliasSearch? aliases = null,
        string? cacheFolder = null, LyricLookupTiming? timing = null) =>
        LyricService.CreateDefault(cacheFolder ?? _cache.Path, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, new LyricServiceOptions
        {
            PlatformSources = [apple ?? new AppleMusic(null)],
            Http = new LyricHttpOptions { MinimumHostInterval = TimeSpan.Zero, IsNetworkAvailable = () => online },
            Timing = timing ?? Quick,
            ArtistSearch = aliases ?? new NoAliases(),
        });

    private sealed class NoAliases : IArtistAliasSearch
    {
        public Task<ArtistAliasReply?> SearchAsync(string artist, CancellationToken cancellationToken) =>
            Task.FromResult<ArtistAliasReply?>(new ArtistAliasReply([]));
    }

    private static LyricResult.Synced Synced(LyricResult? result) => Assert.IsType<LyricResult.Synced>(result);

    // ---- (a) An English song: LRCLIB's word timing beats line-timed sources --------------------------------------

    private static readonly LyricQuery ShapeOfYou = new("Shape of You", "Ed Sheeran", "Divide", 233.7);

    private static readonly SungLine[] English =
    [
        new("Invented first line here", 11.65, 13.9),
        new("Second made up line", 14.05, 16.6),
        new("A third line we wrote", 16.77, 19.3),
        new("Fourth line for testing", 19.5, 22.1),
        new("And the fifth to end", 22.3, 25.0),
    ];

    private static SungLine[] Shifted(IEnumerable<SungLine> lines, double by) => [.. lines.Select(l => l with { Start = l.Start + by, End = l.End + by })];

    private static FixtureHandler EnglishWorld(bool lrclibWords = true, double othersShift = 0, IReadOnlyList<SungLine>? lrclibLines = null)
    {
        var lyricsPlus = ServiceReplies.LyricsPlus("Shape of You", "Ed Sheeran", Shifted(English, othersShift));
        var handler = Quiet()
            .Set("lrclib.net/api/get", Reply.Text(200, ServiceReplies.LrclibRow(1470135, "Shape of You", "Ed Sheeran", "Divide", 234,
                lrclibLines ?? English, lrclibWords)))
            .Set("music.163.com/api/search/get", Reply.Text(200, ServiceReplies.NetEaseSearch((1, "Shape of You", "Ed Sheeran", 233.7))))
            .Set("music.163.com/api/song/lyric", Reply.Text(200, ServiceReplies.NetEaseLyric(ServiceReplies.Lrc(Shifted(English, othersShift)))))
            .Set("api.lyrics.ovh/v1/Ed%20Sheeran/Shape%20of%20You", Reply.Text(200, ServiceReplies.LyricsOvh(English.Select(l => l.Text))));
        handler.Responder = request => request.RequestUri!.Host.StartsWith("lyricsplus.", StringComparison.Ordinal)
            ? Reply.Text(200, lyricsPlus)
            : null;
        return handler;
    }

    [Fact]
    public async Task EnglishSongLrclibWordTimingBeatsLineTimedSources()
    {
        var handler = EnglishWorld();
        var service = Service(handler);
        var report = await service.LookupAsync(ShapeOfYou, Everything, cancellationToken: Ct);
        var shown = Synced(report.Result);
        Assert.Equal(new LyricProvenance("Word-by-word", "LRCLIB"), shown.Provenance);
        Assert.Equal(English.Select(l => l.Text), shown.Lines.Select(l => l.Text));
        // The exact hit's timing is sound, so LRCLIB's search is not asked.
        Assert.DoesNotContain("lrclib.net/api/search", handler.Addresses);
        Assert.True(report.IsComplete);
        Assert.False(report.Offline);
        Assert.StartsWith("Match found", report.States[Lrclib], StringComparison.Ordinal);
        Assert.EndsWith("showing", report.States[Lrclib], StringComparison.Ordinal);
        // Apple Music cannot be read on Windows: listed with its reason, never asked.
        Assert.Equal(AppleUnavailable, report.States[AppleMusicCache]);
        // No local folder chosen: the local source says so.
        Assert.Equal("Choose the Lyrimuse lyrics folder", report.States[Lyrimuse]);

        // A search asks every source, so the whole ranking shows: word timing first (LRCLIB leads Lyrics+), then line
        // timing, then plain text.
        var all = await service.LookupAsync(ShapeOfYou, Everything, force: true, cancellationToken: Ct);
        Assert.Equal([Lrclib, LyricsPlus, NetEase, LyricsOvh], all.Candidates.Select(c => c.Source));
        Assert.Equal(["Word-by-word · LRCLIB", "Word-by-word · Apple via Lyrics+", "Line-synced · NetEase", "Plain text · no timing · lyrics.ovh"],
            all.Candidates.Select(c => c.Provenance.Label));
        Assert.Equal(new LyricProvenance("Word-by-word", "LRCLIB"), all.Result!.Provenance);
    }

    [Fact]
    public async Task LrclibLosesItsLeadWhenTwoOtherSourcesAgreeAgainstItsTiming()
    {
        // Lyrics+ and NetEase agree with each other, two seconds from LRCLIB's listener-made timing. LRCLIB's word
        // timing then has no lead over Lyrics+'s: in Source order, Lyrics+ (earlier in the list) wins.
        var sourceOrder = Everything with { Mode = LyricMatchMode.SourceOrder };
        var report = await Service(EnglishWorld(othersShift: 2)).LookupAsync(ShapeOfYou, sourceOrder, force: true, cancellationToken: Ct);
        Assert.Equal(new LyricProvenance("Word-by-word", "Apple via Lyrics+"), report.Result!.Provenance);
        Assert.Equal([LyricsPlus, Lrclib, NetEase, LyricsOvh], report.Candidates.Select(c => c.Source));
        Assert.True(report.Candidates[1].Agreement.TimingOutlier);
        Assert.Contains("timing differs from other sources", report.States[Lrclib], StringComparison.Ordinal);
        // With the timing agreeing (the first test), LRCLIB leads in Source order too.
        var agreeing = await Service(EnglishWorld(), cacheFolder: _cache.File("agreeing")).LookupAsync(ShapeOfYou, sourceOrder, force: true,
            cancellationToken: Ct);
        Assert.Equal(Lrclib, agreeing.Candidates[0].Source);
    }

    [Fact]
    public async Task BunchedTimingRanksBelowSoundTimingButIsUsedWhenNothingElseIsTimed()
    {
        // LRCLIB's upload stamps a run of lines a moment apart.
        SungLine[] bunched = [.. English.Select((l, i) => new SungLine(l.Text, 11 + (i * 0.1), 11 + (i * 0.1) + 0.09)),
            new("A sixth line arrives", 30, 32), new("Seventh and final words", 33, 36)];
        var handler = EnglishWorld(lrclibWords: false, lrclibLines: bunched);
        var report = await Service(handler).LookupAsync(ShapeOfYou, Everything, force: true, cancellationToken: Ct);
        // The exact hit's timing is unsound, so other uploads are searched too (the probe parses and checks it).
        Assert.Contains("lrclib.net/api/search", handler.Addresses);
        Assert.Equal(LyricsPlus, report.Candidates[0].Source);
        Assert.Equal(Lrclib, report.Candidates[^2].Source); // below NetEase's sound line timing, above plain text
        Assert.Contains("lines bunched", report.States[Lrclib], StringComparison.Ordinal);

        // With no other timed lyrics, the bunched upload is still what shows.
        var alone = Quiet()
            .Set("lrclib.net/api/get", Reply.Text(200, ServiceReplies.LrclibRow(1, "Shape of You", "Ed Sheeran", "Divide", 234, bunched, false)))
            .Set("api.lyrics.ovh/v1/Ed%20Sheeran/Shape%20of%20You", Reply.Text(200, ServiceReplies.LyricsOvh(English.Select(l => l.Text))));
        var fallback = await Service(alone, cacheFolder: _cache.File("alone")).LookupAsync(ShapeOfYou, Everything, cancellationToken: Ct);
        Assert.Equal(new LyricProvenance("Line-synced", "LRCLIB"), fallback.Result!.Provenance);
    }

    [Fact]
    public async Task AppleMusicLeadsAtEachTimingButLrclibWordTimingBeatsItsLineTiming()
    {
        const string lineTtml = "<tt><body><div><p begin='11.65' end='13.9'>Invented first line here</p><p begin='14.05' end='16.6'>Second made up line</p></div></body></tt>";
        const string wordTtml = "<tt><body><div><p begin='11.65' end='13.9'><span begin='11.65' end='12.5'>Invented</span> <span begin='12.5' end='13'>first</span> <span begin='13' end='13.4'>line</span> <span begin='13.4' end='13.9'>here</span></p></div></body></tt>";
        var lines = await Service(EnglishWorld(), new AppleMusic(lineTtml)).LookupAsync(ShapeOfYou, Everything, force: true, cancellationToken: Ct);
        Assert.Equal([Lrclib, LyricsPlus, AppleMusicCache, NetEase, LyricsOvh], lines.Candidates.Select(c => c.Source));
        Assert.Equal("Line-synced · Apple Music", lines.Candidates[2].Provenance.Label);

        var apple = new AppleMusic(wordTtml);
        // Apple Music is read before any service is asked; give it ample time so the check does not race the clock.
        var words = await Service(EnglishWorld(), apple, cacheFolder: _cache.File("words"), timing: Quick with { MusicFirst = TimeSpan.FromSeconds(5) })
            .LookupAsync(ShapeOfYou, Everything, cancellationToken: Ct);
        Assert.Equal(new LyricProvenance("Word-by-word", "Apple Music"), words.Result!.Provenance);
        // Nothing outranks Apple Music's own word timing, so an automatic lookup asks no service.
        Assert.All(new[] { Lrclib, LyricsPlus, NetEase, QQMusic, Kugou, Kuwo, LyricsOvh, Amll },
            id => Assert.Equal(LyricLookup.AppleMusicShowing, words.States[id]));
    }

    // ---- (b) A Japanese song -------------------------------------------------------------------------------------

    private static readonly LyricQuery Lemon = new("Lemon", "米津玄師", "Lemon", 255.9);

    private static FixtureHandler JapaneseWorld() => Quiet()
        .Set("music.163.com/api/search/get", Reply.Fixture("netease/search-lemon.json"))
        .Set("music.163.com/api/song/lyric", Reply.Fixture("netease/lyric-lemon.json"))
        .Set("raw.githubusercontent.com/amll-dev/amll-ttml-db/refs/heads/main/ncm-lyrics/536622304.ttml", Reply.Fixture("amll/536622304.ttml"))
        .Set("mobileservice.kugou.com/api/v3/search/song", Reply.Fixture("kugou/search-lemon.json"))
        .Set("lyrics.kugou.com/search", Reply.Fixture("kugou/lyric-search.json"))
        .Set("lyrics.kugou.com/download", Reply.Fixture("kugou/download-krc.json"));

    [Fact]
    public async Task JapaneseSongWordTimingFromAmllWinsAndNetEaseKeepsItsReadings()
    {
        var report = await Service(JapaneseWorld()).LookupAsync(Lemon, Everything, force: true, cancellationToken: Ct);
        var shown = Synced(report.Result);
        Assert.Equal(new LyricProvenance("Word-by-word", "AMLL TTML DB"), shown.Provenance);
        Assert.Equal("Translated line", shown.Lines[0].Translation);
        Assert.Equal([Amll, Kugou, NetEase], report.Candidates.Select(c => c.Source));
        Assert.Equal(["Word-by-word", "Word-by-word", "Line-synced"], report.Candidates.Select(c => c.Provenance.Precision));
        // NetEase's credit lines are set apart, and its reading LRC is attached to the lines it reads.
        var netEase = Synced(report.Candidates[2].Result).Lines;
        Assert.Equal([true, true], netEase.Take(2).Select(line => line.Credit == true));
        Assert.Equal("in ven ted fa su to", netEase.First(line => line.Text == "Invented first line").Romanization);
    }

    [Fact]
    public async Task ALatinArtistNameFindsTheCataloguesUnderItsNativeName()
    {
        // The library says "Kenshi Yonezu"; NetEase and Kugou list "米津玄師". MusicBrainz's reply (recorded) links them.
        var aliases = new RecordedAliases();
        var query = new LyricQuery("Lemon", "Kenshi Yonezu", "Lemon", 255.9);
        var report = await Service(JapaneseWorld(), aliases: aliases).LookupAsync(query, Everything, force: true, cancellationToken: Ct);
        Assert.Contains("Kenshi Yonezu", aliases.Asked);
        Assert.Contains(Kugou, report.Candidates.Select(c => c.Source));
        Assert.Contains(NetEase, report.Candidates.Select(c => c.Source));
        // A match found under the other name is recorded under the library's.
        Assert.All(report.Candidates, c => Assert.Equal("Kenshi Yonezu", c.Artist));
        Assert.Equal("Word-by-word", report.Result!.Provenance.Precision);
    }

    private sealed class RecordedAliases : IArtistAliasSearch
    {
        public List<string> Asked { get; } = [];

        public Task<ArtistAliasReply?> SearchAsync(string artist, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add(artist);
            }
            return Task.FromResult(ArtistAliasReply.Parse(Fixtures.Bytes("musicbrainz/artist-kenshi-yonezu.json")));
        }
    }

    // ---- (c) A Chinese song with NetEase YRC ---------------------------------------------------------------------

    private static readonly LyricQuery Hero = new("孤勇者", "陈奕迅", null, 256.2);

    [Fact]
    public async Task ChineseSongNetEaseYrcWordTimingBeatsQqLineTiming()
    {
        var handler = Quiet()
            .Set("music.163.com/api/search/get", Reply.Text(200, ServiceReplies.NetEaseSearch((1901371647, "孤勇者", "陈奕迅", 256.0))))
            .Set("music.163.com/api/song/lyric", Reply.Fixture("netease/lyric-yrc.json"))
            .Set("c.y.qq.com/soso/fcgi-bin/client_search_cp", Reply.Text(200, ServiceReplies.QQSearch("002hLLdh0jMdbo", "孤勇者", "陈奕迅", 256)))
            .Set("c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg", Reply.Text(200,
                ServiceReplies.QQLyric("[ti:孤勇者]\n[ar:陈奕迅]\n[00:00.00]孤勇者 - 陈奕迅\n[00:00.85]Invented first line\n[00:06.65]Second made-up line\n")));
        var report = await Service(handler).LookupAsync(Hero, Everything, force: true, cancellationToken: Ct);
        var shown = Synced(report.Result);
        Assert.Equal(new LyricProvenance("Word-by-word", "NetEase"), shown.Provenance);
        // The 作词 / 作曲 credits are flagged, not sung; the sung lines carry NetEase's own word timing.
        Assert.Equal([true, true, false, false], shown.Lines.Select(line => line.Credit == true));
        Assert.Equal(["Invented", "first", "line"], shown.Lines[2].Words.Select(w => w.Text));
        Assert.True(shown.Lines[2].WordTimed);
        Assert.Equal([NetEase, QQMusic], report.Candidates.Select(c => c.Source));
        Assert.Equal("Line-synced · QQ Music", report.Candidates[1].Provenance.Label);
    }

    // ---- (d) Nothing anywhere ------------------------------------------------------------------------------------

    [Fact]
    public async Task NothingFoundAnywhereGivesACalmReportAndIsRememberedBriefly()
    {
        var handler = Quiet();
        var service = Service(handler);
        var query = new LyricQuery("An Unknown Song", "Nobody", null, 200);
        var report = await service.LookupAsync(query, Everything, cancellationToken: Ct);
        Assert.Null(report.Result);
        Assert.Empty(report.Candidates);
        Assert.Empty(report.Rejected);
        Assert.False(report.Offline);
        Assert.True(report.IsComplete);
        Assert.Equal(0, report.Revision);
        foreach (var id in new[] { Lrclib, LyricsPlus, NetEase, QQMusic, Kugou, Kuwo, LyricsOvh, Amll })
        {
            Assert.Equal(LyricLookup.NoUsableMatch, report.States[id]);
        }
        Assert.Equal(AppleUnavailable, report.States[AppleMusicCache]);
        // Asked again within minutes, the services that answered are not asked again.
        var asked = handler.Requests.Count;
        var again = await service.LookupAsync(query, Everything, cancellationToken: Ct);
        Assert.Equal(asked, handler.Requests.Count);
        Assert.Equal(LyricLookup.RecentMiss, again.States[Lrclib]);
    }

    // ---- (e) Offline ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AllSourcesOfflineGivesAnOfflineReportAndNothingIsRemembered()
    {
        var handler = new FixtureHandler { Fallback = Reply.Broken };
        var service = Service(handler, online: false);
        var report = await service.LookupAsync(ShapeOfYou, Everything, cancellationToken: Ct);
        Assert.Null(report.Result);
        Assert.True(report.Offline);
        Assert.True(report.IsComplete);
        Assert.Equal("No internet connection", report.States[Lrclib]);
        Assert.Equal("No internet connection", report.States[NetEase]);
        // Nothing was remembered as a miss: the next lookup asks the services again.
        var asked = handler.Requests.Count;
        Assert.True(asked > 0);
        var again = await service.LookupAsync(ShapeOfYou, Everything, cancellationToken: Ct);
        Assert.True(handler.Requests.Count > asked);
        Assert.True(again.Offline);
    }

    // ---- (f) A pinned choice -------------------------------------------------------------------------------------

    [Fact]
    public async Task APinnedChoiceWinsWithoutAskingAnySource()
    {
        var handler = EnglishWorld();
        var service = Service(handler);
        var mine = new LyricResult.Plain(["My own words"], new LyricProvenance("Not synced", "Imported"));
        Assert.True(service.Pin(ShapeOfYou, mine));
        var report = await service.LookupAsync(ShapeOfYou, Everything, cancellationToken: Ct);
        Assert.True(report.Pinned);
        Assert.Equal(mine, report.Result);
        Assert.Empty(handler.Requests);
        Assert.Equal(AppleUnavailable, report.States[AppleMusicCache]);
        service.Unpin(ShapeOfYou);
        var unpinned = await service.LookupAsync(ShapeOfYou, Everything, cancellationToken: Ct);
        Assert.False(unpinned.Pinned);
        Assert.Equal("LRCLIB", unpinned.Result!.Provenance.Source);
    }

    // ---- (g) A cache hit -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ACacheHitAsksNoServiceAndKeepsCreditsAndTiming()
    {
        var handler = Quiet()
            .Set("music.163.com/api/search/get", Reply.Text(200, ServiceReplies.NetEaseSearch((1901371647, "孤勇者", "陈奕迅", 256.0))))
            .Set("music.163.com/api/song/lyric", Reply.Fixture("netease/lyric-yrc.json"));
        var first = await Service(handler).LookupAsync(Hero, Everything, cancellationToken: Ct);
        Assert.False(first.Cached);
        var asked = handler.Requests.Count;
        // A new service over the same folder, as after a restart.
        var second = await Service(handler).LookupAsync(Hero, Everything, cancellationToken: Ct);
        Assert.True(second.Cached);
        Assert.Equal(asked, handler.Requests.Count);
        Assert.Equal(LyricLookup.SavedMatch + " · showing", second.States[NetEase]);
        Assert.Equal(first.Result, second.Result);
        var lines = Synced(second.Result).Lines;
        Assert.True(lines[0].Credit);
        Assert.True(lines[2].WordTimed);
    }
}
