using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>
/// The owner's ranking decisions of 12 Nov 2026 (docs/LYRIC-MATCHING-NOTES.md): Apple Music's lyrics relayed by
/// Lyrics+ rank as Apple Music's own, and a later agreement of two other sources against LRCLIB's timing demotes a
/// LRCLIB match already showing. Windows tests written for the decisions; the Mac aligns with the same rules.
/// </summary>
public sealed class RankingDecisionsTests : IDisposable
{
    private readonly LyricQuery _query = new("Our Song", "Our Artist", null, 180);
    private readonly TempFolder _folder = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AppleRelay = "Apple via Lyrics+";

    private static readonly string[] Words =
        ["First line we sing", "Second line goes here", "Third one comes now", "Fourth before the end", "And the fifth closes"];

    /// <summary>Five sung lines, word-timed or line-timed, starting <paramref name="shift"/> seconds late.</summary>
    private static LyricCandidate Song(LyricSourceId source, string? label = null, double shift = 0, bool words = true,
        double? duration = 180)
    {
        var lines = Words.Select((text, i) =>
        {
            var start = 10 + (i * 4) + shift;
            var line = L.Line(text, start, start + 3);
            return words ? L.Timed(line, [.. text.Split(' ').Select((w, k) => L.Word(w, start + (k * 0.6), start + (k * 0.6) + 0.5))]) : line;
        }).ToList();
        return new LyricCandidate(source, L.Synced(lines, LyricTimingRules.Precision(lines), label ?? source.Title()), "Our Song", "Our Artist",
            duration, 0);
    }

    private static IEnumerable<LyricSourceSnapshot> Modes(params LyricSourceId[] order) =>
        LyricMatchModeInfo.All.Select(mode => new LyricSourceSnapshot(order, mode));

    /// <summary>The ranking, checked to be the same whichever order the matches arrive in.</summary>
    private IReadOnlyList<LyricSourceId> Ranking(LyricCandidate[] candidates, LyricSourceSnapshot options)
    {
        var ranked = LyricRanking.Ranked(candidates, _query, options).Select(c => c.Source).ToList();
        Assert.Equal(ranked, LyricRanking.Ranked(candidates.Reverse(), _query, options).Select(c => c.Source));
        Assert.Equal(ranked[0], LyricRanking.Choose(candidates.Reverse(), _query, options)?.Source);
        return ranked;
    }

    // ---- Decision 1: Apple Music's lyrics relayed by Lyrics+ rank as Apple Music ---------------------------------

    [Fact]
    public void AnAppleRelayRanksAsAppleMusicAndBeatsLrclibsWordTiming()
    {
        var relay = Song(LyricsPlus, AppleRelay);
        Assert.Equal(AppleMusicCache, relay.RankingSource);
        Assert.Equal(LyricsPlus, relay.Source);
        Assert.Equal("Word-by-word · Apple via Lyrics+", relay.Provenance.Label);
        // Whatever the configured order, LRCLIB first included.
        foreach (var options in Modes(Lrclib, NetEase, LyricsPlus).Concat(Modes(LyricsPlus, NetEase, Lrclib)))
        {
            Assert.Equal([LyricsPlus, Lrclib, NetEase], Ranking([Song(Lrclib), Song(NetEase), relay], options));
            // At line timing too, Apple Music leads.
            Assert.Equal(LyricsPlus,
                Ranking([Song(Lrclib, words: false), Song(NetEase, words: false), Song(LyricsPlus, AppleRelay, words: false)], options)[0]);
            // Timing still comes first: LRCLIB's word timing beats the relay's line timing.
            Assert.Equal([Lrclib, LyricsPlus], Ranking([Song(Lrclib), Song(LyricsPlus, AppleRelay, words: false)], options));
        }
        // The source list keeps the relay's label and says whose lyrics they are.
        var screened = LyricRanking.Screen([Song(Lrclib), relay], _query, new LyricSourceSnapshot([LyricsPlus, Lrclib], LyricMatchMode.AccuracyFirst));
        var states = LyricRanking.Annotate(new Dictionary<LyricSourceId, string>(), screened, LyricsPlus, _query);
        Assert.Equal("Match found · Apple Music’s lyrics · 1 other source agrees · showing", states[LyricsPlus]);
        Assert.Equal("Match found · 1 other source agrees", states[Lrclib]);
    }

    [Fact]
    public void ANodeNamingAppleQAppleStillRanksAsAppleMusic()
    {
        // lyricsplus.binimum.org names Apple Music's lyrics "qApple" where lyricsplus.prjktla.my.id says "Apple" for the
        // very same lyrics (same lines and song parts; seen live on 13 Nov 2026 for "As It Was").
        Assert.True(LyricSourceRanking.IsAppleUpstream("qApple"));
        Assert.True(LyricSourceRanking.IsAppleUpstream("Apple"));
        Assert.False(LyricSourceRanking.IsAppleUpstream("QQ Music"));
        Assert.False(LyricSourceRanking.IsAppleUpstream(null));
        Assert.Equal(AppleMusicCache, Song(LyricsPlus, "qApple via Lyrics+").RankingSource);
    }

    [Fact]
    public void ARelayOfAnotherServiceRanksAsLyricsPlus()
    {
        var musixmatch = Song(LyricsPlus, "Musixmatch via Lyrics+");
        Assert.Equal(LyricsPlus, musixmatch.RankingSource);
        Assert.Equal(LyricsPlus, Song(LyricsPlus).RankingSource);
        // Only Lyrics+ relays: another source's label never makes it rank as Apple Music.
        Assert.Equal(NetEase, Song(NetEase, AppleRelay).RankingSource);
        Assert.Equal("Apple", LyricSourceRanking.RelayUpstream(LyricsPlus, AppleRelay));
        Assert.Null(LyricSourceRanking.RelayUpstream(LyricsPlus, "Lyrics+"));
        foreach (var options in Modes(LyricsPlus, NetEase, Lrclib))
        {
            Assert.Equal([Lrclib, LyricsPlus, NetEase], Ranking([Song(Lrclib), Song(NetEase), musixmatch], options));
            var screened = LyricRanking.Screen([musixmatch], _query, options);
            Assert.Equal("Match found", LyricRanking.Annotate(new Dictionary<LyricSourceId, string>(), screened, null, _query)[LyricsPlus]);
        }
    }

    [Fact]
    public void AppleMusicsOwnLyricsRankAboveTheirRelay()
    {
        var own = Song(AppleMusicCache, duration: 183);
        // The relay's length fits better and Lyrics+ comes first in the order: Apple Music's own still lead.
        var relay = Song(LyricsPlus, AppleRelay, duration: 180);
        foreach (var options in Modes(LyricsPlus, AppleMusicCache, Lrclib).Concat(Modes(Lrclib, AppleMusicCache, LyricsPlus)))
        {
            Assert.Equal([AppleMusicCache, LyricsPlus, Lrclib], Ranking([relay, Song(Lrclib), own], options));
            Assert.True(LyricRanking.Outranks(own, relay, _query, options));
            Assert.False(LyricRanking.Outranks(relay, own, _query, options));
        }
    }

    [Fact]
    public void AppleMusicAndItsRelayAreOneSourceInAgreement()
    {
        // Apple Music's own lyrics and their relay agree, two seconds from LRCLIB's timing: that is one source, not two.
        var options = new LyricSourceSnapshot([AppleMusicCache, LyricsPlus, Lrclib, NetEase], LyricMatchMode.AccuracyFirst);
        var weighed = LyricRanking.Screen([Song(Lrclib), Song(AppleMusicCache, shift: 2), Song(LyricsPlus, AppleRelay, shift: 2)], _query, options);
        var lrclib = weighed.Accepted.Single(c => c.Source == Lrclib);
        Assert.False(lrclib.Agreement.TimingOutlier);
        Assert.Equal(1, lrclib.Agreement.Peers);
        Assert.Equal(1, weighed.Accepted.Single(c => c.Source == LyricsPlus).Agreement.Peers);
        // A third, independent source agreeing with them makes it two.
        var three = LyricRanking.Screen([Song(Lrclib), Song(AppleMusicCache, shift: 2), Song(LyricsPlus, AppleRelay, shift: 2),
            Song(NetEase, shift: 2, words: false)], _query, options);
        Assert.True(three.Accepted.Single(c => c.Source == Lrclib).Agreement.TimingOutlier);
    }

    [Fact]
    public void APendingLyricsPlusIsWaitedForOnlyWhereItsRelayCouldWin()
    {
        foreach (var options in Modes(AppleMusicCache, LyricsPlus, NetEase, Lrclib, QQMusic, Kugou, Kuwo, LyricsOvh))
        {
            // It might relay Apple Music's lyrics, which would lead LRCLIB's word timing.
            var lrclib = Song(Lrclib);
            Assert.True(LyricRanking.CouldBeat(lrclib, LyricsPlus, _query, options));
            Assert.False(LyricRanking.IsSettled(lrclib, [LyricsPlus], _query, options));
            Assert.True(LyricRanking.IsSettled(lrclib, [NetEase, QQMusic, Kugou, Kuwo, LyricsOvh], _query, options));
            // Nothing it relays can beat Apple Music's own word timing; Apple Music's own can beat the relay.
            var own = Song(AppleMusicCache);
            Assert.False(LyricRanking.CouldBeat(own, LyricsPlus, _query, options));
            Assert.False(LyricRanking.Matters(LyricsPlus, own, null, _query, options));
            var relay = Song(LyricsPlus, AppleRelay);
            Assert.True(LyricRanking.CouldBeat(relay, AppleMusicCache, _query, options));
            Assert.True(LyricRanking.IsSettled(relay, [Lrclib, NetEase, QQMusic, Kugou, Kuwo, LyricsOvh], _query, options));
            Assert.False(LyricRanking.IsSettled(relay, [AppleMusicCache], _query, options));
        }
    }

    [Fact]
    public async Task LrclibShowsAtGraceWhileLyricsPlusIsPendingAndItsAppleRelayTakesOver()
    {
        var options = new LyricSourceSnapshot([LyricsPlus, NetEase, Lrclib], LyricMatchMode.AccuracyFirst);
        var log = new LookupLog();
        Gate lyricsPlus = new(), never = new();
        var start = _clock.GetUtcNow();
        TimeSpan? shownAt = null;
        var lookup = Lookups.Create(_folder.Path, async (id, _, _, ct) =>
        {
            log.Call(id);
            switch (id)
            {
                case Lrclib:
                    return Song(Lrclib);
                case LyricsPlus:
                    await lyricsPlus.WaitAsync(ct);
                    return Song(LyricsPlus, AppleRelay);
                default:
                    await never.WaitAsync(ct);
                    return null;
            }
        }, _clock);
        var report = await lookup.LookupAsync(_query, options, onUpdate: async update =>
        {
            await log.Update(update);
            if (update.Revision == 1)
            {
                shownAt ??= _clock.GetUtcNow() - start;
                lyricsPlus.Release();
            }
        }, cancellationToken: Ct);
        // LRCLIB alone could be beaten by the pending Lyrics+, so it shows when the grace period ends, not at once.
        Assert.Equal(Lookups.Fast.Grace, shownAt);
        Assert.Equal(Lrclib.Title(), L.SourceOf(log.Shown(1)?.Result));
        Assert.Equal(2, report.Revision);
        Assert.Equal(AppleRelay, L.SourceOf(report.Result));
        // Once the relay shows, only Apple Music's own lyrics could beat it: NetEase is stopped.
        Assert.Equal(LyricLookup.CouldNotBeat, report.States[NetEase]);
        Assert.Equal(LyricsPlus, new Lunalyr.Core.Lyrics.Storage.SavedLyrics(_folder.Path, _clock)
            .ReadCandidate(LyricLookup.CacheKey(_query, options))?.Source);
    }

    // ---- Decision 2: a later agreement demotes LRCLIB showing ----------------------------------------------------

    [Fact]
    public void ALaterAgreementAgainstLrclibShowingReplacesIt()
    {
        foreach (var options in Modes(LyricsPlus, NetEase, Lrclib))
        {
            var lrclib = Song(Lrclib);
            var lyricsPlus = Song(LyricsPlus, shift: 2);
            var netEase = Song(NetEase, shift: 2, words: false);
            var publication = new LyricPublication(options, _query);
            var first = LyricRanking.Screen([lrclib], _query, options).Accepted;
            Assert.Equal(PublicationStep.Publish, publication.Step(first[0], settled: true, graceOver: true, settleOver: false, first));
            // One other source disagreeing is not enough: LRCLIB keeps its lead.
            var two = LyricRanking.Screen([lrclib, lyricsPlus], _query, options).Accepted;
            Assert.Equal(Lrclib, two[0].Source);
            Assert.Equal(PublicationStep.Wait, publication.Step(two[0], true, true, false, two));
            // A second one agreeing with the first: LRCLIB is the odd one out, loses its lead, and the best of the
            // others replaces it, through the usual upgrade (held while a pending source could still beat it).
            var three = LyricRanking.Screen([lrclib, lyricsPlus, netEase], _query, options).Accepted;
            Assert.True(three.Single(c => c.Source == Lrclib).Agreement.TimingOutlier);
            Assert.Equal(LyricsPlus, three[0].Source);
            Assert.Equal(PublicationStep.HoldForUpgrade, publication.Step(three[0], settled: false, graceOver: true, settleOver: false, three));
            Assert.Equal(PublicationStep.Publish, publication.Step(three[0], settled: true, graceOver: true, settleOver: false, three));
            Assert.Equal(LyricsPlus, publication.Shown?.Source);
            Assert.Equal(2, publication.Revision);
            // Judged as it was when it showed (no agreement yet), LRCLIB would have stayed: the old pairwise rule.
            var pairwise = new LyricPublication(options, _query);
            pairwise.Step(first[0], true, true, false);
            Assert.Equal(PublicationStep.Wait, pairwise.Step(three[0], true, true, false));
        }
    }

    [Fact]
    public async Task AnAutomaticLookupKeepsTheAgreeingSourcesAndDemotesLrclib()
    {
        // Lyrics+ (relaying another service) and NetEase answer only once LRCLIB shows, and agree against it.
        var options = new LyricSourceSnapshot([LyricsPlus, NetEase, Lrclib], LyricMatchMode.SourceOrder);
        var log = new LookupLog();
        var later = new Gate();
        var lookup = Lookups.Create(_folder.Path, async (id, _, _, ct) =>
        {
            log.Call(id);
            if (id == Lrclib)
            {
                return Song(Lrclib);
            }
            await later.WaitAsync(ct);
            if (ct.IsCancellationRequested)
            {
                log.Cancel(id);
            }
            return id == LyricsPlus ? Song(LyricsPlus, "Musixmatch via Lyrics+", shift: 2) : Song(NetEase, shift: 2, words: false);
        }, _clock);
        var report = await lookup.LookupAsync(_query, options, onUpdate: async update =>
        {
            await log.Update(update);
            if (update.Revision == 1)
            {
                later.Release();
            }
        }, cancellationToken: Ct);
        Assert.Equal(Lrclib.Title(), L.SourceOf(log.Shown(1)?.Result));
        // NetEase could not beat LRCLIB itself, but it could agree against it: it was not stopped.
        Assert.Equal(LyricLookup.Searching, log.Shown(1)?.States[NetEase]);
        Assert.Empty(log.Cancelled);
        Assert.Equal(2, report.Revision);
        Assert.Equal("Musixmatch via Lyrics+", L.SourceOf(report.Result));
        Assert.Contains("timing differs from other sources", report.States[Lrclib], StringComparison.Ordinal);
        Assert.StartsWith(LyricLookup.MatchFound, report.States[NetEase], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourcesThatCouldDemoteLrclibKeepAnsweringUpToTheirLimit()
    {
        // Only LRCLIB answers. NetEase can give word timing, so it could still agree against LRCLIB: it keeps answering
        // up to its own limit, as does Kuwo (word timing from lrcx since the Mac's 7ddb08a). lyrics.ovh (no timing)
        // stops as soon as LRCLIB shows.
        var options = new LyricSourceSnapshot([LyricsPlus, NetEase, Kuwo, Lrclib, LyricsOvh], LyricMatchMode.AccuracyFirst);
        var start = _clock.GetUtcNow();
        var stoppedAt = new Dictionary<LyricSourceId, TimeSpan>();
        var never = new Gate();
        var lookup = Lookups.Create(_folder.Path, async (id, _, _, ct) =>
        {
            if (id == Lrclib)
            {
                return Song(Lrclib);
            }
            await never.WaitAsync(ct);
            lock (stoppedAt)
            {
                stoppedAt[id] = _clock.GetUtcNow() - start;
            }
            return null;
        }, _clock);
        var log = new LookupLog();
        var report = await lookup.LookupAsync(_query, options, onUpdate: log.Update, cancellationToken: Ct);
        var fast = Lookups.Fast;
        Assert.Equal(LyricLookup.Searching, log.Shown(1)?.States[NetEase]);
        Assert.Equal(fast.SourceLimit, stoppedAt[NetEase]);
        Assert.Equal(LyricLookup.TimedOut, report.States[NetEase]);
        // Kuwo's lrcx word timing could agree against LRCLIB too (the Mac's 7ddb08a).
        Assert.Equal(fast.SourceLimit, stoppedAt[Kuwo]);
        Assert.Equal(LyricLookup.TimedOut, report.States[Kuwo]);
        Assert.Equal(fast.Grace, stoppedAt[LyricsOvh]);
        Assert.Equal(LyricLookup.CouldNotBeat, report.States[LyricsOvh]);
        // Lyrics+ might relay Apple Music's lyrics: it runs to its own limit too.
        Assert.Equal(LyricLookup.TimedOut, report.States[LyricsPlus]);
        Assert.Equal(1, report.Revision);
    }

    [Fact]
    public async Task ALineTimedSourceIsKeptWhenAWordTimedRivalIsInHand()
    {
        // Kugou's word timing, two seconds from LRCLIB's, is in hand; LRCLIB still leads. Kuwo answers with line timing
        // only, but with Kugou in hand its agreement would demote LRCLIB, so it keeps answering, and it does agree.
        var options = new LyricSourceSnapshot([Kugou, Kuwo, Lrclib], LyricMatchMode.AccuracyFirst);
        var log = new LookupLog();
        var later = new Gate();
        var lookup = Lookups.Create(_folder.Path, async (id, _, _, ct) =>
        {
            switch (id)
            {
                case Lrclib:
                    return Song(Lrclib);
                case Kugou:
                    return Song(Kugou, shift: 2);
            }
            await later.WaitAsync(ct);
            return ct.IsCancellationRequested ? null : Song(Kuwo, shift: 2, words: false);
        }, _clock);
        var report = await lookup.LookupAsync(_query, options, onUpdate: async update =>
        {
            await log.Update(update);
            if (update.Revision == 1)
            {
                later.Release();
            }
        }, cancellationToken: Ct);
        Assert.Equal(Lrclib.Title(), L.SourceOf(log.Shown(1)?.Result));
        Assert.Equal(LyricLookup.Searching, log.Shown(1)?.States[Kuwo]);
        Assert.Equal(2, report.Revision);
        Assert.Equal(Kugou.Title(), L.SourceOf(report.Result));
        Assert.Equal([Kugou, Lrclib, Kuwo], report.Candidates.Select(c => c.Source));
    }

    [Fact]
    public void ADemotedLrclibRanksAfterEveryOtherSourceAtItsTimingButKeepsItsTiming()
    {
        // Lyrics+ and NetEase agree against LRCLIB; QQ Music's word timing agrees with no one. LRCLIB, first in the
        // configured order, now ranks after QQ Music and Lyrics+ too (lead 4, after the others' 3).
        var candidates = new[] { Song(Lrclib), Song(LyricsPlus, shift: 2), Song(NetEase, shift: 2, words: false), Song(QQMusic, shift: 5) };
        foreach (var options in Modes(Lrclib, QQMusic, NetEase, LyricsPlus))
        {
            // (Ranked weighs agreement; Choose, on unweighed candidates, would not.)
            Assert.Equal([QQMusic, LyricsPlus, Lrclib, NetEase], LyricRanking.Ranked(candidates, _query, options).Select(c => c.Source));
            Assert.Equal([QQMusic, LyricsPlus, Lrclib, NetEase], LyricRanking.Ranked(candidates.Reverse(), _query, options).Select(c => c.Source));
        }
        // When the pair agreeing against it has only line timing, LRCLIB's word timing still wins.
        var linePair = new[] { Song(Lrclib), Song(NetEase, shift: 2, words: false), Song(Kugou, shift: 2, words: false) };
        foreach (var options in Modes(NetEase, Kugou, Lrclib))
        {
            var ranked = LyricRanking.Ranked(linePair, _query, options);
            Assert.True(ranked[0].Agreement.TimingOutlier);
            Assert.Equal(Lrclib, ranked[0].Source);
        }
    }

    [Fact]
    public async Task NothingIsKeptForADemotionOnceAnotherSourceAgreesWithLrclib()
    {
        // Kugou already agrees with LRCLIB's timing, so LRCLIB can never be the odd one out: NetEase stops at once.
        var options = new LyricSourceSnapshot([LyricsPlus, NetEase, Lrclib, Kugou], LyricMatchMode.AccuracyFirst);
        var start = _clock.GetUtcNow();
        TimeSpan? netEaseStopped = null;
        var never = new Gate();
        var lookup = Lookups.Create(_folder.Path, async (id, _, _, ct) =>
        {
            switch (id)
            {
                case Lrclib:
                    return Song(Lrclib);
                case Kugou:
                    return Song(Kugou, words: false);
            }
            await never.WaitAsync(ct);
            if (id == NetEase)
            {
                netEaseStopped = _clock.GetUtcNow() - start;
            }
            return null;
        }, _clock);
        var report = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Equal(Lrclib.Title(), L.SourceOf(report.Result));
        Assert.Equal(Lookups.Fast.Grace, netEaseStopped);
        Assert.Equal(LyricLookup.CouldNotBeat, report.States[NetEase]);
        // couldDemote, source by source.
        var lrclib = Song(Lrclib);
        Assert.False(LyricRanking.CouldDemote(lrclib, NetEase, [lrclib, Song(Kugou, words: false)], _query));
        Assert.True(LyricRanking.CouldDemote(lrclib, NetEase, [lrclib], _query));
        Assert.True(LyricRanking.CouldDemote(lrclib, NetEase, [lrclib, Song(Kugou, shift: 2, words: false)], _query));
        // Kuwo's lrcx word timing could be the first of two, as could any word-capable source (the Mac's 7ddb08a).
        Assert.True(LyricRanking.CouldDemote(lrclib, Kuwo, [lrclib, Song(Kugou, shift: 2, words: false)], _query));
        Assert.True(LyricRanking.CouldDemote(lrclib, Kuwo, [lrclib, Song(Kugou, shift: 2)], _query));
        Assert.False(LyricRanking.CouldDemote(lrclib, LyricsOvh, [lrclib, Song(Kugou, shift: 2)], _query));
        // Only LRCLIB's word or mixed timing showing keeps sources for a demotion.
        Assert.False(LyricRanking.CouldDemote(Song(Lrclib, words: false), NetEase, [], _query));
        Assert.False(LyricRanking.CouldDemote(Song(NetEase), QQMusic, [], _query));
    }
}
