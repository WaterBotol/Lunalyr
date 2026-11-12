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

    /// <summary>
    /// LRCLIB, Lyrics+, NetEase and lyrics.ovh with the invented English lines. Lyrics+ relays
    /// <paramref name="lyricsPlusUpstream"/>: "Apple" ranks as Apple Music's own lyrics, so tests about LRCLIB's lead
    /// use another upstream.
    /// </summary>
    private static FixtureHandler EnglishWorld(bool lrclibWords = true, double othersShift = 0, IReadOnlyList<SungLine>? lrclibLines = null,
        string lyricsPlusUpstream = "Apple")
    {
        var lyricsPlus = ServiceReplies.LyricsPlus("Shape of You", "Ed Sheeran", Shifted(English, othersShift), lyricsPlusUpstream);
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
        // Lyrics+ relays another service's lyrics here: Apple Music's, relayed, would rank above LRCLIB's (the next test).
        var handler = EnglishWorld(lyricsPlusUpstream: "Musixmatch");
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
        Assert.Equal(["Word-by-word · LRCLIB", "Word-by-word · Musixmatch via Lyrics+", "Line-synced · NetEase", "Plain text · no timing · lyrics.ovh"],
            all.Candidates.Select(c => c.Provenance.Label));
        Assert.Equal(new LyricProvenance("Word-by-word", "LRCLIB"), all.Result!.Provenance);
    }

    [Fact]
    public async Task AppleLyricsRelayedByLyricsPlusRankAsAppleMusic()
    {
        // Ranking decision of 12 Nov 2026: Lyrics+ relaying Apple Music's lyrics ranks as Apple Music, so its word
        // timing leads LRCLIB's. It is still listed and shown as Lyrics+.
        var handler = EnglishWorld();
        var service = Service(handler);
        var report = await service.LookupAsync(ShapeOfYou, Everything, cancellationToken: Ct);
        Assert.Equal(new LyricProvenance("Word-by-word", "Apple via Lyrics+"), report.Result!.Provenance);
        Assert.EndsWith("showing", report.States[LyricsPlus], StringComparison.Ordinal);
        Assert.StartsWith("Match found" + LyricRanking.AppleRelayNote, report.States[LyricsPlus], StringComparison.Ordinal);
        Assert.Equal(LyricsPlus, report.Candidates[0].Source);
        Assert.Equal(AppleMusicCache, report.Candidates[0].RankingSource);

        var all = await service.LookupAsync(ShapeOfYou, Everything, force: true, cancellationToken: Ct);
        Assert.Equal([LyricsPlus, Lrclib, NetEase, LyricsOvh], all.Candidates.Select(c => c.Source));

        // Apple Music's own word timing still ranks above its relay.
        const string wordTtml = "<tt><body><div><p begin='11.65' end='13.9'><span begin='11.65' end='12.5'>Invented</span> <span begin='12.5' end='13'>first</span> <span begin='13' end='13.4'>line</span> <span begin='13.4' end='13.9'>here</span></p></div></body></tt>";
        var both = await Service(EnglishWorld(), new AppleMusic(wordTtml), cacheFolder: _cache.File("both"))
            .LookupAsync(ShapeOfYou, Everything, force: true, cancellationToken: Ct);
        Assert.Equal([AppleMusicCache, LyricsPlus, Lrclib, NetEase, LyricsOvh], both.Candidates.Select(c => c.Source));
        Assert.Equal(new LyricProvenance("Word-by-word", "Apple Music"), both.Result!.Provenance);
    }

    [Fact]
    public async Task LocalSourceWithNoFolderSaysSoWithoutBeingAsked()
    {
        // The local source alone, with no folder chosen: it can never answer, so the lookup ends at once with its
        // reason, whatever the timing. A folder chosen later is read as usual.
        var handler = Quiet();
        var service = Service(handler, timing: Quick with { Grace = TimeSpan.FromMinutes(5), SourceLimit = TimeSpan.FromMinutes(5) });
        var localOnly = LyricSourceSnapshot.From([Lyrimuse], new HashSet<LyricSourceId> { Lyrimuse }, LyricMatchMode.AccuracyFirst);
        var report = await service.LookupAsync(ShapeOfYou, localOnly, cancellationToken: Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Null(report.Result);
        Assert.Equal(LocalLyricsSource.ChooseFolder, report.States[Lyrimuse]);
        Assert.Empty(handler.Addresses);

        using var folder = new TempFolder();
        var withFolder = localOnly with { LocalFolder = folder.Path };
        var read = await service.LookupAsync(ShapeOfYou, withFolder, cancellationToken: Ct);
        Assert.NotEqual(LocalLyricsSource.ChooseFolder, read.States[Lyrimuse]);
    }

    [Fact]
    public async Task LrclibLosesItsLeadWhenTwoOtherSourcesAgreeAgainstItsTiming()
    {
        // Lyrics+ and NetEase agree with each other, two seconds from LRCLIB's listener-made timing. LRCLIB's word
        // timing then has no lead over Lyrics+'s: in Source order, Lyrics+ (earlier in the list) wins.
        // LRCLIB answers last here, so it is never shown; the next test has it answer first and be replaced.
        // Lyrics+ relays another service: Apple Music's lyrics, relayed, would outrank LRCLIB's without any agreement.
        var sourceOrder = Everything with { Mode = LyricMatchMode.SourceOrder };
        var othersIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var world = EnglishWorld(othersShift: 2, lyricsPlusUpstream: "Musixmatch");
        world.Set("lrclib.net/api/get", Reply.Text(200, ServiceReplies.LrclibRow(1470135, "Shape of You", "Ed Sheeran", "Divide", 234,
            English, true)) with { Wait = othersIn.Task });
        var report = await Service(world).LookupAsync(ShapeOfYou, sourceOrder, force: true, onUpdate: progress =>
        {
            if (progress.Candidates.Any(c => c.Source == LyricsPlus) && progress.Candidates.Any(c => c.Source == NetEase))
            {
                othersIn.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }, cancellationToken: Ct);
        Assert.Equal(new LyricProvenance("Word-by-word", "Musixmatch via Lyrics+"), report.Result!.Provenance);
        Assert.Equal([LyricsPlus, Lrclib, NetEase, LyricsOvh], report.Candidates.Select(c => c.Source));
        Assert.True(report.Candidates[1].Agreement.TimingOutlier);
        Assert.Contains("timing differs from other sources", report.States[Lrclib], StringComparison.Ordinal);
        // With the timing agreeing (the first test), LRCLIB leads in Source order too.
        var agreeing = await Service(EnglishWorld(lyricsPlusUpstream: "Musixmatch"), cacheFolder: _cache.File("agreeing"))
            .LookupAsync(ShapeOfYou, sourceOrder, force: true, cancellationToken: Ct);
        Assert.Equal(Lrclib, agreeing.Candidates[0].Source);
    }

    [Fact]
    public async Task LrclibShowingFirstIsReplacedWhenTwoOtherSourcesLaterAgreeAgainstItsTiming()
    {
        // Ranking decision of 12 Nov 2026. The same disagreement as above, but LRCLIB answers first and is shown; Lyrics+
        // and NetEase answer only once it shows. An automatic lookup keeps them answering (couldDemote), and
        // their agreement then demotes LRCLIB: a new revision switches to Lyrics+.
        var sourceOrder = Everything with { Mode = LyricMatchMode.SourceOrder };
        var lrclibShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var world = EnglishWorld(othersShift: 2, lyricsPlusUpstream: "Musixmatch");
        var lyricsPlus = ServiceReplies.LyricsPlus("Shape of You", "Ed Sheeran", Shifted(English, 2), "Musixmatch");
        world.Responder = request => request.RequestUri!.Host.StartsWith("lyricsplus.", StringComparison.Ordinal)
            ? Reply.Text(200, lyricsPlus) with { Wait = lrclibShown.Task }
            : null;
        world.Set("music.163.com/api/search/get", Reply.Text(200, ServiceReplies.NetEaseSearch((1, "Shape of You", "Ed Sheeran", 233.7)))
            with { Wait = lrclibShown.Task });
        // A short grace period shows LRCLIB promptly. Lyrics+ and NetEase can give word timing, so they keep answering
        // while LRCLIB shows (couldDemote), up to their 10 s limit here.
        var timing = Quick with { Grace = TimeSpan.FromMilliseconds(300) };
        var shown = new List<LyricLookupReport>();
        var report = await Service(world, timing: timing).LookupAsync(ShapeOfYou, sourceOrder, onUpdate: progress =>
        {
            if (progress.Revision > 0 && (shown.Count == 0 || shown[^1].Revision != progress.Revision))
            {
                shown.Add(progress);
            }
            if (progress.Result?.Provenance.Source == "LRCLIB")
            {
                lrclibShown.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }, cancellationToken: Ct);
        var lrclib = Assert.Single(shown, s => s.Result?.Provenance.Source == "LRCLIB");
        // When LRCLIB showed, the sources that could agree against it were still running.
        Assert.Equal(LyricLookup.Searching, lrclib.States[NetEase]);
        Assert.Equal(new LyricProvenance("Word-by-word", "Musixmatch via Lyrics+"), report.Result!.Provenance);
        Assert.Equal(lrclib.Revision + 1, report.Revision);
        // (lyrics.ovh, plain text, may have been stopped before answering, so only the timed matches are compared.)
        Assert.Equal([LyricsPlus, Lrclib, NetEase], report.Candidates.Take(3).Select(c => c.Source));
        Assert.True(report.Candidates[1].Agreement.TimingOutlier);
        Assert.Contains("timing differs from other sources", report.States[Lrclib], StringComparison.Ordinal);
        Assert.EndsWith("showing", report.States[LyricsPlus], StringComparison.Ordinal);
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
        // Lyrics+ relays another service here: Apple Music's lyrics, relayed, would lead LRCLIB's word timing too.
        var lines = await Service(EnglishWorld(lyricsPlusUpstream: "Musixmatch"), new AppleMusic(lineTtml))
            .LookupAsync(ShapeOfYou, Everything, force: true, cancellationToken: Ct);
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
        // Lyrics+ relays Apple Music's lyrics, which rank above LRCLIB's (ranking decision of 12 Nov 2026).
        Assert.Equal("Apple via Lyrics+", unpinned.Result!.Provenance.Source);
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

    // ---- A Japanese song listed under its English title ----------------------------------------------------------

    /// <summary>
    /// Apple Music lists 夜に駆ける as "Racing into the Night". Nothing answers under that name, so the sources ask Apple's
    /// catalogue (the Mac test's replies) once between them, and LRCLIB's Japanese lyrics, found under 夜に駆ける, show
    /// under the listed title, marked in the source list.
    /// </summary>
    [Fact]
    public async Task AnEnglishTitledJapaneseSongIsFoundUnderItsOriginalTitle()
    {
        SungLine[] japanese = [.. Ranking.LanguageVersionTests.Japanese.Select((text, i) => new SungLine(text, 11 + (i * 4), 14 + (i * 4)))];
        var handler = Quiet()
            .Set("itunes.apple.com/search", Reply.Fixture("itunes/mac-listed.json"))
            .Set("itunes.apple.com/lookup", Reply.Fixture("itunes/mac-original.json"));
        handler.Responder = request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host.StartsWith("lyricsplus.", StringComparison.Ordinal))
            {
                return Reply.Text(404, ServiceReplies.LyricsPlusNotFound);
            }
            return uri.AbsolutePath == "/api/get" && FixtureHandler.QueryOf(uri, "track_name") == "夜に駆ける"
                ? Reply.Text(200, ServiceReplies.LrclibRow(1, "夜に駆ける", "YOASOBI", "夜に駆ける - Single", 258, japanese, wordTimed: false))
                : null;
        };
        var http = handler.Client();
        var sources = new LyricSourceSet([.. LyricSourceCatalog.CreateDefault(http), new AppleMusic(null)],
            titles: new OriginalTitles(_cache.Path, new ItunesCatalogue(http), "us", TimeSpan.Zero));
        var service = new LyricService(sources, new Core.Lyrics.Storage.SavedLyrics(_cache.Path), timing: Quick);
        var playing = new LyricQuery("Racing into the Night", "YOASOBI", "Racing into the Night - Single", 258) { Genre = "J-Pop" };

        var report = await service.LookupAsync(playing, Everything, cancellationToken: Ct);
        Assert.Equal(new LyricProvenance("Line-synced", "LRCLIB"), report.Result!.Provenance);
        Assert.Equal(japanese.Select(l => l.Text), Synced(report.Result).Lines.Select(l => l.Text));
        var found = Assert.Single(report.Candidates);
        Assert.Equal(("Racing into the Night", true), (found.Title, found.ViaOriginalTitle));
        Assert.Equal("Match found" + LyricRanking.OriginalTitleNote + " · showing", report.States[Lrclib]);
        // Asked once between all the sources.
        Assert.Equal(1, handler.Addresses.Count(a => a == "itunes.apple.com/search"));
        Assert.Equal(1, handler.Addresses.Count(a => a == "itunes.apple.com/lookup"));
    }
}
