using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Storage;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>
/// The order matches are ranked in. Word-by-word or mixed timing comes first: Apple Music's own lyrics, then LRCLIB,
/// then the other sources. Line timing is next, Apple Music first, then plain text. The configured order only orders
/// the other sources, in both match modes; saved choices and the recording check still come before any of it.
/// </summary>
public sealed class LyricPriorityTests : IDisposable
{
    private enum Timing
    {
        Word,
        Mixed,
        Line,
        Plain,
    }

    private readonly LyricQuery _query = new("Our Song", "Our Artist", "Our Album", 180);

    /// <summary>A saved order like the user's own, with LRCLIB before Apple Music.</summary>
    private static readonly LyricSourceId[] UserOrder = [Lrclib, AppleMusicCache, LyricsPlus, NetEase, QQMusic, Kugou, Kuwo, LyricsOvh];

    private static readonly string Music = AppleMusicCache.Title();
    private static readonly string LrclibTitle = Lrclib.Title();
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>Two lines: both word-timed, only the first, neither, or plain text.</summary>
    private static LyricCandidate Candidate(LyricSourceId source, Timing timing, string title = "Our Song", double? duration = 180)
    {
        static LyricLine Line(string text, double start, bool timed)
        {
            var line = L.Line(text, start, start + 2);
            return timed ? L.Timed(line, [.. text.Split(' ').Select((w, i) => L.Word(w, start + i, start + i + 1))]) : line;
        }
        LyricResult result = timing == Timing.Plain
            ? L.Plain(["Sing along", "Hold on"], "fixture", source.Title())
            : L.Synced([Line("Sing along", 1, timing != Timing.Line), Line("Hold on", 4, timing == Timing.Word)], "fixture", source.Title());
        return new LyricCandidate(source, result, title, "Our Artist", duration, 0);
    }

    /// <summary>Accuracy first and Source order over the same configured order.</summary>
    private static IEnumerable<LyricSourceSnapshot> Modes(params LyricSourceId[] order) =>
        LyricMatchModeInfo.All.Select(mode => new LyricSourceSnapshot(order, mode));

    /// <summary>The winner, which is the same whichever order the matches arrive in.</summary>
    private LyricSourceId? Winner(LyricCandidate[] candidates, LyricSourceSnapshot options)
    {
        var first = LyricRanking.Ranked(candidates, _query, options).FirstOrDefault()?.Source;
        Assert.Equal(first, LyricRanking.Ranked(candidates.Reverse(), _query, options).FirstOrDefault()?.Source);
        Assert.Equal(first, LyricRanking.Choose(candidates.Reverse(), _query, options)?.Source);
        return first;
    }

    private IEnumerable<LyricSourceId> Ranking(LyricCandidate[] candidates, LyricSourceSnapshot options) =>
        LyricRanking.Ranked(candidates, _query, options).Select(c => c.Source);

    // Ported from LyricPriorityTests.swift: testTheFixturesCarryTheTimingLabelsTheCardsShow
    [Fact]
    public void TheFixturesCarryTheTimingLabelsTheCardsShow()
    {
        Assert.Equal("Word-by-word", Candidate(Lrclib, Timing.Word).Provenance.Precision);
        Assert.Equal("Mixed timing", Candidate(Lrclib, Timing.Mixed).Provenance.Precision);
        Assert.Equal("Line-synced", Candidate(Lrclib, Timing.Line).Provenance.Precision);
        Assert.Equal(2, LyricTimingRules.RankedTiming(Candidate(Lrclib, Timing.Word).Result));
        Assert.Equal(2, LyricTimingRules.RankedTiming(Candidate(Lrclib, Timing.Mixed).Result));
        Assert.Equal(1, LyricTimingRules.RankedTiming(Candidate(Lrclib, Timing.Line).Result));
        Assert.Equal(0, LyricTimingRules.RankedTiming(Candidate(Lrclib, Timing.Plain).Result));
    }

    // Ported from LyricPriorityTests.swift: testMusicsWordTimingBeatsLRCLIBsWordTiming
    [Fact]
    public void MusicsWordTimingBeatsLrclibsWordTiming()
    {
        foreach (var options in Modes(UserOrder))
        {
            Assert.Equal(AppleMusicCache, Winner([Candidate(Lrclib, Timing.Word), Candidate(AppleMusicCache, Timing.Word)], options));
        }
    }

    // Ported from LyricPriorityTests.swift: testMixedTimingRanksWithWordTimingSoMusicsMixedTimingStillWins
    [Fact]
    public void MixedTimingRanksWithWordTimingSoMusicsMixedTimingStillWins()
    {
        foreach (var options in Modes(UserOrder))
        {
            Assert.Equal(AppleMusicCache, Winner([Candidate(Lrclib, Timing.Word), Candidate(AppleMusicCache, Timing.Mixed)], options));
        }
    }

    // Ported from LyricPriorityTests.swift: testAnyWordOrMixedTimingBeatsMusicsLineTiming
    [Fact]
    public void AnyWordOrMixedTimingBeatsMusicsLineTiming()
    {
        foreach (var options in Modes(UserOrder))
        {
            Assert.Equal(NetEase, Winner([Candidate(AppleMusicCache, Timing.Line), Candidate(NetEase, Timing.Word)], options));
            Assert.Equal(QQMusic, Winner([Candidate(AppleMusicCache, Timing.Line), Candidate(QQMusic, Timing.Mixed)], options));
        }
    }

    // Ported from LyricPriorityTests.swift: testMusicLeadsLineTimingAndLRCLIBTakesItsPlaceInTheOrderThere
    [Fact]
    public void MusicLeadsLineTimingAndLrclibTakesItsPlaceInTheOrderThere()
    {
        LyricCandidate[] lines = [Candidate(QQMusic, Timing.Line), Candidate(Lrclib, Timing.Line), Candidate(AppleMusicCache, Timing.Line)];
        foreach (var options in Modes(UserOrder))
        {
            Assert.Equal(AppleMusicCache, Winner(lines, options));
            Assert.Equal([AppleMusicCache, Lrclib, QQMusic], Ranking(lines, options));
        }
        // With line timing, LRCLIB has no lead over the sources before it.
        foreach (var options in Modes(QQMusic, AppleMusicCache, Lrclib))
        {
            Assert.Equal([AppleMusicCache, QQMusic, Lrclib], Ranking(lines, options));
        }
    }

    // Ported from LyricPriorityTests.swift: testLRCLIBsMixedTimingBeatsOtherWordTiming
    [Fact]
    public void LrclibsMixedTimingBeatsOtherWordTiming()
    {
        foreach (var options in Modes(NetEase, LyricsPlus, Lrclib))
        {
            Assert.Equal(Lrclib, Winner([Candidate(NetEase, Timing.Word), Candidate(Lrclib, Timing.Mixed)], options));
        }
    }

    // Ported from LyricPriorityTests.swift: testWithoutMusicsLyricsLRCLIBsWordTimingLeads
    [Fact]
    public void WithoutMusicsLyricsLrclibsWordTimingLeads()
    {
        // Apple Music is switched off, or on with nothing for this song.
        foreach (var order in new[] { new[] { NetEase, LyricsPlus, Lrclib }, [AppleMusicCache, NetEase, Lrclib] })
        {
            foreach (var options in Modes(order))
            {
                Assert.Equal(Lrclib, Winner([Candidate(NetEase, Timing.Word), Candidate(Lrclib, Timing.Word)], options));
            }
        }
    }

    // Ported from LyricPriorityTests.swift: testTheConfiguredOrderOnlyOrdersTheOtherSources
    [Fact]
    public void TheConfiguredOrderOnlyOrdersTheOtherSources()
    {
        var words = new[] { Kugou, QQMusic, NetEase, LyricsPlus, Lrclib, AppleMusicCache }.Select(s => Candidate(s, Timing.Word)).ToArray();
        // The user's order puts LRCLIB first; Apple Music's own lyrics still lead.
        foreach (var options in Modes(UserOrder))
        {
            Assert.Equal(AppleMusicCache, Winner(words, options));
            Assert.Equal([AppleMusicCache, Lrclib, LyricsPlus, NetEase, QQMusic, Kugou], Ranking(words, options));
        }
        // Reordering changes which of the other sources wins, never the lead.
        foreach (var options in Modes(Kugou, NetEase, QQMusic, LyricsPlus, Lrclib, AppleMusicCache))
        {
            Assert.Equal([AppleMusicCache, Lrclib, Kugou, NetEase, QQMusic, LyricsPlus], Ranking(words, options));
            Assert.Equal(Kugou, Winner([Candidate(NetEase, Timing.Word), Candidate(Kugou, Timing.Mixed)], options));
        }
    }

    // Ported from LyricPriorityTests.swift: testPlainTextIsOnlyAFallbackInTheConfiguredOrder
    [Fact]
    public void PlainTextIsOnlyAFallbackInTheConfiguredOrder()
    {
        foreach (var options in Modes(UserOrder))
        {
            Assert.Equal(Kuwo, Winner([Candidate(Lrclib, Timing.Plain), Candidate(LyricsOvh, Timing.Plain), Candidate(Kuwo, Timing.Line)], options));
            Assert.Equal(Lrclib, Winner([Candidate(LyricsOvh, Timing.Plain), Candidate(Lrclib, Timing.Plain)], options));
        }
        foreach (var options in Modes(LyricsOvh, Lrclib))
        {
            Assert.Equal(LyricsOvh, Winner([Candidate(Lrclib, Timing.Plain), Candidate(LyricsOvh, Timing.Plain)], options));
        }
    }

    // Ported from LyricPriorityTests.swift: testAccuracyFirstPrefersTheCloserMatchAmongTheOtherSourcesOnly
    [Fact]
    public void AccuracyFirstPrefersTheCloserMatchAmongTheOtherSourcesOnly()
    {
        LyricSourceId[] order = [NetEase, LyricsPlus, Lrclib, AppleMusicCache];
        var accuracy = new LyricSourceSnapshot(order, LyricMatchMode.AccuracyFirst);
        var sourceOrder = new LyricSourceSnapshot(order, LyricMatchMode.SourceOrder);
        // A credit is not a version, so only Accuracy first weighs it.
        var credited = Candidate(NetEase, Timing.Word, "Our Song (feat. Guest)");
        var exact = Candidate(LyricsPlus, Timing.Mixed);
        Assert.Equal(LyricsPlus, Winner([credited, exact], accuracy));
        Assert.Equal(NetEase, Winner([credited, exact], sourceOrder));
        // A closer match never lifts another source above LRCLIB or Apple Music.
        var remaster = Candidate(Lrclib, Timing.Mixed, "Our Song - Remastered 2011");
        foreach (var options in new[] { accuracy, sourceOrder })
        {
            Assert.Equal(Lrclib, Winner([exact, remaster], options));
            Assert.Equal(AppleMusicCache, Winner([exact, remaster, Candidate(AppleMusicCache, Timing.Word)], options));
        }
    }

    // Ported from LyricPriorityTests.swift: testMusicsLineOnlyCacheGivesWayToLRCLIBsWordTiming
    // (adapted: the Apple Music TTML and LRCLIB fixtures are built as the lines their parsers give, so no parser or
    // HTTP is involved)
    [Fact]
    public void MusicsLineOnlyCacheGivesWayToLrclibsWordTiming()
    {
        var song = new LyricQuery("A Song", "A Singer", "An Album", 180);
        LyricCandidate Make(LyricSourceId source, IReadOnlyList<LyricLine> lines, double duration) =>
            new(source, L.Synced(lines, LyricTimingRules.Precision(lines), source.Title()), "A Song", "A Singer", duration, 0);
        var lines = Make(AppleMusicCache, [L.Line("One two", 1, 3), L.Line("Three four", 5, 7)], 180.1);
        Assert.Equal("Line-synced", lines.Provenance.Precision);
        var words = Make(Lrclib, [
            L.Timed(L.Line("One two", 1, 5), L.Word("One", 1, 2), L.Word("two", 2, 3)),
            L.Timed(L.Line("Three four", 5, 180), L.Word("Three", 5, 6), L.Word("four", 6, 7)),
        ], 180);
        Assert.Equal("Word-by-word", words.Provenance.Precision);
        var syllables = Make(AppleMusicCache, [L.Timed(L.Line("One two", 1, 3), L.Word("One", 1, 2), L.Word("two", 2, 3))], 180.1);
        Assert.Equal("Word-by-word", syllables.Provenance.Precision);
        foreach (var options in Modes(UserOrder))
        {
            Assert.Equal([Lrclib, AppleMusicCache], LyricRanking.Ranked([lines, words], song, options).Select(c => c.Source));
            Assert.Equal([AppleMusicCache, Lrclib], LyricRanking.Ranked([words, syllables], song, options).Select(c => c.Source));
        }
    }

    // Ported from LyricPriorityTests.swift: testTheRecordingCheckStillAppliesToEverySource
    [Fact]
    public void TheRecordingCheckStillAppliesToEverySource()
    {
        foreach (var options in Modes(UserOrder))
        {
            var screened = LyricRanking.Screen([Candidate(AppleMusicCache, Timing.Word, "Another Song"),
                Candidate(Lrclib, Timing.Word, duration: 186), Candidate(NetEase, Timing.Line)], _query, options);
            Assert.Equal([NetEase], screened.Accepted.Select(c => c.Source));
            Assert.Equal([Lrclib, AppleMusicCache], screened.Rejected.Select(r => r.Candidate.Source));
        }
    }

    // Ported from LyricPriorityTests.swift: testOnlyTheBestPossibleMatchSettlesTheLookup
    [Fact]
    public void OnlyTheBestPossibleMatchSettlesTheLookup()
    {
        foreach (var options in Modes(UserOrder))
        {
            var services = options.Enabled.Where(s => s != AppleMusicCache).ToHashSet();
            // Nothing can rank above Apple Music's own word or mixed timing.
            foreach (var timing in new[] { Timing.Word, Timing.Mixed })
            {
                Assert.True(LyricRanking.IsSettled(Candidate(AppleMusicCache, timing), services, _query, options));
            }
            // Only Apple Music's own lyrics can rank above LRCLIB's.
            Assert.False(LyricRanking.IsSettled(Candidate(Lrclib, Timing.Word), [AppleMusicCache], _query, options));
            // Changed for the ranking decision of 12 Nov 2026: Lyrics+ might relay Apple Music's lyrics, so it is waited for.
            Assert.True(LyricRanking.IsSettled(Candidate(Lrclib, Timing.Mixed), services.Where(s => s is not (Lrclib or LyricsPlus)), _query, options));
            Assert.False(LyricRanking.IsSettled(Candidate(Lrclib, Timing.Mixed), [LyricsPlus], _query, options));
            // Apple Music's line timing waits for every source that can have word timing.
            var ownLines = Candidate(AppleMusicCache, Timing.Line);
            foreach (var source in new[] { Lrclib, LyricsPlus, NetEase, QQMusic, Kugou })
            {
                Assert.True(LyricRanking.CouldBeat(ownLines, source, _query, options), source.ToString());
            }
            Assert.True(LyricRanking.IsSettled(ownLines, [Kuwo, LyricsOvh], _query, options));
            // Another source's word timing waits for LRCLIB and Apple Music.
            var other = Candidate(NetEase, Timing.Word);
            Assert.True(LyricRanking.CouldBeat(other, Lrclib, _query, options));
            Assert.True(LyricRanking.CouldBeat(other, AppleMusicCache, _query, options));
        }
    }

    // Ported from LyricPriorityTests.swift: testOnlyMusicsWordOrMixedTimingMakesEveryServiceUnnecessary
    [Fact]
    public void OnlyMusicsWordOrMixedTimingMakesEveryServiceUnnecessary()
    {
        foreach (var options in Modes(UserOrder))
        {
            foreach (var timing in new[] { Timing.Word, Timing.Mixed })
            {
                Assert.False(LyricRanking.Matters(Lrclib, Candidate(AppleMusicCache, timing), null, _query, options));
            }
            var ownLines = Candidate(AppleMusicCache, Timing.Line);
            foreach (var source in new[] { Lrclib, NetEase })
            {
                Assert.True(LyricRanking.Matters(source, ownLines, null, _query, options));
            }
            var words = Candidate(Lrclib, Timing.Word);
            Assert.False(LyricRanking.Matters(NetEase, words, words, _query, options));
            Assert.True(LyricRanking.Matters(AppleMusicCache, words, words, _query, options));
            // Another source's word timing never makes LRCLIB unnecessary.
            var other = Candidate(NetEase, Timing.Word);
            Assert.True(LyricRanking.Matters(Lrclib, other, other, _query, options));
        }
    }

    // MARK: - Lookups

    private sealed record Stub(LyricLookup Lookup, string Cache, FakeClock Clock)
    {
        public LyricSourceId? Saved(LyricQuery query, LyricSourceSnapshot options) =>
            new SavedLyrics(Cache, Clock).ReadCandidate(LyricLookup.CacheKey(query, options))?.Source;
    }

    /// <summary>
    /// Each source waits for its gate, if it has one, then gives its answer; Apple Music gives
    /// <paramref name="musicLater"/> on its second look. Each stub has its own cache.
    /// </summary>
    private Stub Make(Dictionary<LyricSourceId, LyricCandidate?> answers, LookupLog log, Dictionary<LyricSourceId, Gate>? gates = null,
        LyricCandidate? musicLater = null, LyricLookupTiming? timing = null)
    {
        var cache = _folder.Sub(Guid.NewGuid().ToString("N"));
        var clock = new FakeClock();
        var lookup = Lookups.Create(cache, async (id, _, _, ct) =>
        {
            log.Call(id);
            if (gates?.GetValueOrDefault(id) is { } gate)
            {
                await gate.WaitAsync(ct);
            }
            if (ct.IsCancellationRequested)
            {
                log.Cancel(id);
            }
            if (id == AppleMusicCache && log.Count(id) > 1 && musicLater is not null)
            {
                return musicLater;
            }
            return answers.GetValueOrDefault(id);
        }, clock, timing);
        return new Stub(lookup, cache, clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Ported from LyricPriorityTests.swift: testLRCLIBsWordTimingReplacesOtherWordTimingShownFirst
    [Fact]
    public async Task LrclibsWordTimingReplacesOtherWordTimingShownFirst()
    {
        foreach (var options in Modes(NetEase, Lrclib, QQMusic))
        {
            var log = new LookupLog();
            var late = new Gate();
            var stub = Make(new() { [NetEase] = Candidate(NetEase, Timing.Word), [Lrclib] = Candidate(Lrclib, Timing.Word) }, log,
                new() { [Lrclib] = late });
            var report = await stub.Lookup.LookupAsync(_query, options, onUpdate: async update =>
            {
                await log.Update(update);
                if (update.Revision == 1)
                {
                    late.Release();
                }
            }, cancellationToken: Ct);
            var first = log.Shown(1);
            Assert.Equal(NetEase.Title(), L.SourceOf(first?.Result));
            // LRCLIB's answer could still rank higher, so it is not stopped.
            Assert.Equal(LyricLookup.Searching, first?.States[Lrclib]);
            Assert.Equal(2, report.Revision);
            Assert.Equal(LrclibTitle, L.SourceOf(report.Result));
            Assert.Equal("Match found · showing", report.States[Lrclib]);
            Assert.Equal(Lrclib, stub.Saved(_query, options));
        }
    }

    // Ported from LyricPriorityTests.swift: testLRCLIBsWordTimingReplacesMusicsLineTimingShownFirst
    [Fact]
    public async Task LrclibsWordTimingReplacesMusicsLineTimingShownFirst()
    {
        foreach (var options in Modes(UserOrder))
        {
            var log = new LookupLog();
            var late = new Gate();
            var stub = Make(new() { [AppleMusicCache] = Candidate(AppleMusicCache, Timing.Line), [Lrclib] = Candidate(Lrclib, Timing.Word) }, log,
                new() { [Lrclib] = late });
            var report = await stub.Lookup.LookupAsync(_query, options, onUpdate: async update =>
            {
                await log.Update(update);
                if (update.Revision == 1)
                {
                    late.Release();
                }
            }, cancellationToken: Ct);
            var first = log.Shown(1);
            Assert.Equal(Music, L.SourceOf(first?.Result));
            // Apple Music's line timing stops none of the sources that can have word timing.
            Assert.Equal(LyricLookup.Searching, first?.States[Lrclib]);
            Assert.Equal(2, report.Revision);
            Assert.Equal(LrclibTitle, L.SourceOf(report.Result));
            Assert.Equal("Match found", report.States[AppleMusicCache]);
            Assert.Equal(UserOrder.ToHashSet(), log.Calls.ToHashSet());
        }
    }

    // Ported from LyricPriorityTests.swift: testALowerRankedArrivalNeverReplacesTheMatchShowing
    [Fact]
    public async Task ALowerRankedArrivalNeverReplacesTheMatchShowing()
    {
        foreach (var options in Modes(UserOrder))
        {
            // A search asks every source: Apple Music's line timing and NetEase's word timing arrive after LRCLIB's
            // word timing is showing.
            var log = new LookupLog();
            var late = new Gate();
            var search = Make(new()
            {
                [Lrclib] = Candidate(Lrclib, Timing.Word),
                [AppleMusicCache] = Candidate(AppleMusicCache, Timing.Line),
                [NetEase] = Candidate(NetEase, Timing.Word),
            }, log, new() { [AppleMusicCache] = late, [NetEase] = late });
            var report = await search.Lookup.LookupAsync(_query, options, force: true, onUpdate: async update =>
            {
                await log.Update(update);
                if (update.Revision == 1)
                {
                    late.Release();
                }
            }, cancellationToken: Ct);
            Assert.Equal(1, report.Revision);
            Assert.Equal(LrclibTitle, L.SourceOf(report.Result));
            Assert.Equal([Lrclib, NetEase, AppleMusicCache], report.Candidates.Select(c => c.Source));

            // Nor does Apple Music's line timing on its second look.
            var again = new LookupLog();
            var automatic = Make(new() { [Lrclib] = Candidate(Lrclib, Timing.Word) }, again, musicLater: Candidate(AppleMusicCache, Timing.Line));
            var replay = await automatic.Lookup.LookupAsync(_query, options, cancellationToken: Ct);
            Assert.Equal(1, replay.Revision);
            Assert.Equal(LrclibTitle, L.SourceOf(replay.Result));
            Assert.Equal("Match found", replay.States[AppleMusicCache]);
            Assert.Equal(2, again.Count(AppleMusicCache));
        }
    }

    // Ported from LyricPriorityTests.swift: testMusicsWordOrMixedTimingNeedsNoService
    [Fact]
    public async Task MusicsWordOrMixedTimingNeedsNoService()
    {
        foreach (var options in Modes(UserOrder))
        {
            foreach (var timing in new[] { Timing.Word, Timing.Mixed })
            {
                var log = new LookupLog();
                var stub = Make(new() { [AppleMusicCache] = Candidate(AppleMusicCache, timing), [Lrclib] = Candidate(Lrclib, Timing.Word) }, log);
                var report = await stub.Lookup.LookupAsync(_query, options, cancellationToken: Ct);
                Assert.Equal(Music, L.SourceOf(report.Result));
                Assert.Equal(1, report.Revision);
                Assert.Equal(LyricLookup.AppleMusicShowing, report.States[Lrclib]);
                Assert.Equal([AppleMusicCache], log.Calls);
            }
        }
    }

    // Ported from LyricPriorityTests.swift: testLRCLIBsWordTimingStopsTheOtherServicesOnceMusicHasNone
    [Fact]
    public async Task LrclibsWordTimingStopsTheOtherServicesOnceMusicHasNone()
    {
        LyricSourceId[] others = [LyricsPlus, NetEase, QQMusic, Kugou, Kuwo, LyricsOvh];
        foreach (var options in Modes(UserOrder))
        {
            // Apple Music has line timing, or nothing even on its second look.
            foreach (var own in new[] { Candidate(AppleMusicCache, Timing.Line), null })
            {
                var log = new LookupLog();
                var never = new Gate();
                var stub = Make(new() { [Lrclib] = Candidate(Lrclib, Timing.Mixed), [AppleMusicCache] = own }, log,
                    others.ToDictionary(id => id, _ => never));
                var report = await stub.Lookup.LookupAsync(_query, options, cancellationToken: Ct);
                Assert.Equal(LrclibTitle, L.SourceOf(report.Result));
                Assert.Equal(1, report.Revision);
                foreach (var id in others.Where(id => id != LyricsPlus))
                {
                    Assert.Equal(LyricLookup.CouldNotBeat, report.States[id]);
                }
                // Changed for the ranking decision of 12 Nov 2026: Lyrics+ might relay Apple Music's lyrics, which would
                // rank above LRCLIB's, so it is not stopped; here it never answers and is given up at its limit.
                Assert.Equal(LyricLookup.TimedOut, report.States[LyricsPlus]);
                Assert.Equal(others.ToHashSet(), log.Cancelled.ToHashSet());
                // Nothing stopped could have beaten it, so replays use it straight away.
                Assert.Equal(Lrclib, stub.Saved(_query, options));
            }
        }
    }

    // Ported from LyricPriorityTests.swift: testMusicsSecondLookStillRunsAfterLRCLIBStopsTheOthers
    [Fact]
    public async Task MusicsSecondLookStillRunsAfterLrclibStopsTheOthers()
    {
        LyricSourceId[] others = [LyricsPlus, NetEase, QQMusic, Kugou, Kuwo, LyricsOvh];
        foreach (var options in Modes(UserOrder))
        {
            var log = new LookupLog();
            var never = new Gate();
            // Changed for the ranking decision of 12 Nov 2026: the others now stop once the agreement window closes
            // (grace over, and one settle period after LRCLIB showed), so Apple Music's second look comes after that.
            var stub = Make(new() { [Lrclib] = Candidate(Lrclib, Timing.Word) }, log, others.ToDictionary(id => id, _ => never),
                musicLater: Candidate(AppleMusicCache, Timing.Mixed), timing: Lookups.Fast with { AppleRetry = TimeSpan.FromSeconds(1) });
            var report = await stub.Lookup.LookupAsync(_query, options, onUpdate: log.Update, cancellationToken: Ct);
            var first = log.Shown(1);
            Assert.Equal(LrclibTitle, L.SourceOf(first?.Result));
            Assert.Equal(LyricLookup.Searching, first?.States[NetEase]);
            Assert.Equal(LyricLookup.CouldNotBeat, log.Updates.Last(u => u.Revision == 1).States[NetEase]);
            Assert.Equal(2, report.Revision);
            Assert.Equal(Music, L.SourceOf(report.Result));
            Assert.Equal("Match found · showing", report.States[AppleMusicCache]);
        }
    }

    // Ported from LyricPriorityTests.swift: testPlainTextShowsOnlyWhenNothingIsTimed
    [Fact]
    public async Task PlainTextShowsOnlyWhenNothingIsTimed()
    {
        foreach (var options in Modes(UserOrder))
        {
            var plain = Make(new() { [LyricsOvh] = Candidate(LyricsOvh, Timing.Plain), [Lrclib] = Candidate(Lrclib, Timing.Plain) }, new LookupLog());
            var report = await plain.Lookup.LookupAsync(_query, options, cancellationToken: Ct);
            Assert.Equal(LrclibTitle, L.SourceOf(report.Result));
            var timed = Make(new()
            {
                [LyricsOvh] = Candidate(LyricsOvh, Timing.Plain),
                [Lrclib] = Candidate(Lrclib, Timing.Plain),
                [Kuwo] = Candidate(Kuwo, Timing.Line),
            }, new LookupLog());
            var lines = await timed.Lookup.LookupAsync(_query, options, cancellationToken: Ct);
            Assert.Equal(Kuwo.Title(), L.SourceOf(lines.Result));
        }
    }

    // Ported from LyricPriorityTests.swift: testASavedChoiceOutranksEverything
    [Fact]
    public async Task ASavedChoiceOutranksEverything()
    {
        foreach (var options in Modes(UserOrder))
        {
            var log = new LookupLog();
            var stub = Make(new() { [AppleMusicCache] = Candidate(AppleMusicCache, Timing.Word), [Lrclib] = Candidate(Lrclib, Timing.Word) }, log);
            Assert.True(new SavedLyrics(stub.Cache, stub.Clock).Save(Candidate(QQMusic, Timing.Line).Result, _query.RecordingKey, pin: true));
            var automatic = await stub.Lookup.LookupAsync(_query, options, cancellationToken: Ct);
            Assert.True(automatic.Pinned);
            Assert.Equal(QQMusic.Title(), L.SourceOf(automatic.Result));
            Assert.Empty(log.Calls);
            // A search lists the higher-ranked matches but keeps the saved choice showing.
            var search = await stub.Lookup.LookupAsync(_query, options, force: true, cancellationToken: Ct);
            Assert.True(search.Pinned);
            Assert.Equal(QQMusic.Title(), L.SourceOf(search.Result));
            Assert.Equal(AppleMusicCache, search.Candidates[0].Source);
        }
    }

    // Ported from LyricPriorityTests.swift: testTimingAndSourcesStatesTheOrderInTheLabelsItShows
    [Fact]
    public void TimingAndSourcesStatesTheOrderInTheLabelsItShows()
    {
        var text = LyricSourceSnapshot.RankingText;
        // Word and mixed timing, Apple Music, LRCLIB, the rest, then line timing -- in that order.
        string[] labels = ["Word-by-word", "Mixed timing", Music, LrclibTitle, "your other sources", "Line-synced"];
        var positions = labels.Select(label => text.IndexOf(label, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
        foreach (var mode in LyricMatchModeInfo.All)
        {
            Assert.Contains(mode.Title(), mode.Detail(), StringComparison.Ordinal);
            Assert.Contains("other sources", mode.Detail(), StringComparison.Ordinal);
        }
    }
}
