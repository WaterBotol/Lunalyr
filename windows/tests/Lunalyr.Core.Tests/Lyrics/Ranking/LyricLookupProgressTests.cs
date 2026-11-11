using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Storage;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>Progressive lookups: the first acceptable match is shown without waiting for the slowest source, and is replaced only by a higher-ranked one.</summary>
public sealed class LyricLookupProgressTests : IDisposable
{
    private readonly LyricQuery _query = new("Our Song", "Our Artist", null, 180);
    private readonly TempFolder _folder = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Tier 0 plain text, 1 line-synced, 3 complete word timing.</summary>
    private static LyricCandidate Candidate(LyricSourceId source, int tier, double? duration = 180, string title = "Our Song")
    {
        var line = L.Line("Sing along", 1, 3);
        if (tier == 3)
        {
            line = L.Timed(line, L.Word("Sing", 1, 2), L.Word("along", 2, 3));
        }
        LyricResult result = tier == 0 ? L.Plain(["Sing along"], "fixture", source.Title()) : L.Synced([line], "fixture", source.Title());
        return new LyricCandidate(source, result, title, "Our Artist", duration, 0);
    }

    private static LyricSourceSnapshot Options(params LyricSourceId[] enabled) => new(enabled, LyricMatchMode.AccuracyFirst);

    private string Key(LyricSourceSnapshot options) => LyricLookup.CacheKey(_query, options);

    private SavedLyrics Saved => new(_folder.Path, _clock);

    // MARK: - Publication policy

    // Ported from LyricLookupProgressTests.swift: testFirstMatchWaitsForTheGracePeriodUnlessNothingCouldBeatIt
    [Fact]
    public void FirstMatchWaitsForTheGracePeriodUnlessNothingCouldBeatIt()
    {
        var options = Options(LyricsPlus, Lrclib);
        var publication = new LyricPublication(options, _query);
        var line = Candidate(Lrclib, 1);
        Assert.Equal(PublicationStep.Wait, publication.Step(line, settled: false, graceOver: false, settleOver: false));
        Assert.Equal(PublicationStep.Publish, publication.Step(line, settled: false, graceOver: true, settleOver: false));
        Assert.Equal(1, publication.Revision);
        var settled = new LyricPublication(options, _query);
        Assert.Equal(PublicationStep.Publish, settled.Step(line, settled: true, graceOver: false, settleOver: false));
    }

    // Ported from LyricLookupProgressTests.swift: testOnlyAHigherRankedMatchReplacesWhatIsShowing
    [Fact]
    public void OnlyAHigherRankedMatchReplacesWhatIsShowing()
    {
        var publication = new LyricPublication(Options(LyricsPlus, NetEase, Lrclib, LyricsOvh), _query);
        publication.Step(Candidate(LyricsPlus, 0), settled: false, graceOver: true, settleOver: false);
        // Plain text from a source lower in the order never replaces it.
        Assert.Equal(PublicationStep.Wait, publication.Step(Candidate(LyricsOvh, 0), true, true, true));
        // Line timing could still be beaten by word timing: hold briefly first.
        Assert.Equal(PublicationStep.HoldForUpgrade, publication.Step(Candidate(Lrclib, 1), false, true, false));
        Assert.Equal(PublicationStep.Publish, publication.Step(Candidate(Lrclib, 1), false, true, true));
        Assert.Equal(Lrclib, publication.Shown?.Source);
        // Each higher-ranked arrival takes over in turn, so the lookup ends on the best.
        Assert.Equal(PublicationStep.Publish, publication.Step(Candidate(NetEase, 3), true, true, false));
        Assert.Equal(PublicationStep.Publish, publication.Step(Candidate(LyricsPlus, 3), true, true, false));
        Assert.Equal(PublicationStep.Wait, publication.Step(Candidate(NetEase, 3), true, true, true));
        Assert.Equal(LyricsPlus, publication.Shown?.Source);
        Assert.Equal(4, publication.Revision);
    }

    // Ported from LyricLookupProgressTests.swift: testSavedChoicesAreNeverReplacedAndMusicsOwnLyricsLeadOnlyTheirTiming
    [Fact]
    public void SavedChoicesAreNeverReplacedAndMusicsOwnLyricsLeadOnlyTheirTiming()
    {
        var options = Options(AppleMusicCache, NetEase);
        var locked = new LyricPublication(options, _query, locked: true);
        Assert.Equal(PublicationStep.Wait, locked.Step(Candidate(NetEase, 3), true, true, true));
        Assert.Equal(1, locked.Revision);
        var publication = new LyricPublication(options, _query);
        Assert.True(publication.IsUpgrade(Candidate(AppleMusicCache, 3), Candidate(NetEase, 3)));
        Assert.False(publication.IsUpgrade(Candidate(NetEase, 3), Candidate(AppleMusicCache, 3)));
        // Apple Music's line timing gives way to word timing, and leads other line timing.
        Assert.True(publication.IsUpgrade(Candidate(NetEase, 3), Candidate(AppleMusicCache, 1)));
        Assert.False(publication.IsUpgrade(Candidate(AppleMusicCache, 1), Candidate(NetEase, 3)));
        Assert.True(publication.IsUpgrade(Candidate(AppleMusicCache, 1), Candidate(NetEase, 1)));
        Assert.False(publication.IsUpgrade(Candidate(NetEase, 1), Candidate(AppleMusicCache, 1)));
    }

    // Ported from LyricLookupProgressTests.swift: testMusicsOwnWordTimingTakesOverAMatchThatWasAlreadyUpgraded
    [Fact]
    public void MusicsOwnWordTimingTakesOverAMatchThatWasAlreadyUpgraded()
    {
        var options = Options(AppleMusicCache, LyricsPlus, Lrclib);
        var publication = new LyricPublication(options, _query);
        publication.Step(Candidate(Lrclib, 1), true, true, false);
        Assert.Equal(PublicationStep.Publish, publication.Step(Candidate(LyricsPlus, 3), true, true, false));
        // Nothing could beat Apple Music's own word timing, so it takes over at once.
        var own = Candidate(AppleMusicCache, 3);
        var settled = LyricRanking.IsSettled(own, [LyricsPlus, Lrclib], _query, options);
        Assert.True(settled);
        Assert.Equal(PublicationStep.Publish, publication.Step(own, settled, true, false));
        Assert.Equal(AppleMusicCache, publication.Shown?.Source);
        Assert.Equal(3, publication.Revision);
    }

    // Ported from LyricLookupProgressTests.swift: testSettlesOnlyWhenNoPendingSourceCouldWin
    [Fact]
    public void SettlesOnlyWhenNoPendingSourceCouldWin()
    {
        var options = Options(LyricsPlus, NetEase, Lrclib, Kuwo);
        var words = Candidate(NetEase, 3);
        // Lyrics+ ranks first on a tie, and LRCLIB's word timing leads every other source's.
        Assert.False(LyricRanking.IsSettled(words, [LyricsPlus], _query, options));
        Assert.False(LyricRanking.IsSettled(words, [Lrclib], _query, options));
        // Kuwo only ever has line timing.
        Assert.True(LyricRanking.IsSettled(words, [Kuwo], _query, options));
        Assert.True(LyricRanking.IsSettled(Candidate(Lrclib, 3), [LyricsPlus, NetEase, Kuwo], _query, options));
        Assert.False(LyricRanking.IsSettled(Candidate(Lrclib, 1), [LyricsPlus], _query, options));
    }

    // MARK: - Lookups

    /// <summary>Answers after a delay on the fake clock, or not at all when cancelled first.</summary>
    private LyricLookup Fetcher(Dictionary<LyricSourceId, (double Delay, LyricCandidate? Candidate)> script, LookupLog calls,
        bool online = true, LyricMissCache? misses = null, LyricLookupTiming? timing = null, string? cache = null) =>
        Lookups.Create(cache ?? _folder.Path, async (id, _, _, ct) =>
        {
            calls.Call(id);
            if (!script.TryGetValue(id, out var step))
            {
                return null;
            }
            if (step.Delay > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(step.Delay), _clock, ct);
            }
            return step.Candidate;
        }, _clock, timing, misses ?? new LyricMissCache(timeProvider: _clock), online);

    /// <summary>Each source waits for its gate, if it has one, then answers.</summary>
    private LyricLookup Gated(Dictionary<LyricSourceId, LyricCandidate?> answers, Dictionary<LyricSourceId, Gate> gates, LookupLog calls) =>
        Lookups.Create(_folder.Path, async (id, _, _, ct) =>
        {
            calls.Call(id);
            if (gates.GetValueOrDefault(id) is { } gate)
            {
                await gate.WaitAsync(ct);
            }
            return answers.GetValueOrDefault(id);
        }, _clock);

    // Ported from LyricLookupProgressTests.swift: testSlowSourceDoesNotHoldBackAFastMatch
    [Fact]
    public async Task SlowSourceDoesNotHoldBackAFastMatch()
    {
        var options = Options(LyricsPlus, Lrclib);
        var calls = new LookupLog();
        var slow = new Gate();
        // Lyrics+ only answers once something is showing.
        var lookup = Gated(new() { [Lrclib] = Candidate(Lrclib, 1), [LyricsPlus] = null }, new() { [LyricsPlus] = slow }, calls);
        var report = await lookup.LookupAsync(_query, options, onUpdate: async update =>
        {
            await calls.Update(update);
            if (update.Revision == 1)
            {
                slow.Release();
            }
        }, cancellationToken: Ct);
        var shown = calls.Shown(1);
        Assert.Equal(Lrclib.Title(), L.SourceOf(shown?.Result));
        Assert.Equal(LyricLookup.Searching, shown?.States[LyricsPlus]);
        Assert.True(report.IsComplete);
        Assert.Equal(1, report.Revision);
        Assert.Equal("Match found · showing", report.States[Lrclib]);
        Assert.Equal(LyricLookup.NoUsableMatch, report.States[LyricsPlus]);
    }

    // Ported from LyricLookupProgressTests.swift: testEachHigherRankedMatchReplacesTheLastAndTheBestIsCached
    [Fact]
    public async Task EachHigherRankedMatchReplacesTheLastAndTheBestIsCached()
    {
        var options = Options(LyricsPlus, NetEase, Lrclib);
        var calls = new LookupLog();
        Gate netEase = new(), lyricsPlus = new();
        // Line timing shows first; NetEase's word timing replaces it; Lyrics+ then ties on timing and is first in the
        // order, so it replaces that.
        var lookup = Gated(new() { [Lrclib] = Candidate(Lrclib, 1), [NetEase] = Candidate(NetEase, 3), [LyricsPlus] = Candidate(LyricsPlus, 3) },
            new() { [NetEase] = netEase, [LyricsPlus] = lyricsPlus }, calls);
        var report = await lookup.LookupAsync(_query, options, onUpdate: async update =>
        {
            await calls.Update(update);
            if (update.Revision == 1)
            {
                netEase.Release();
            }
            if (update.Revision == 2)
            {
                lyricsPlus.Release();
            }
        }, cancellationToken: Ct);
        Assert.Equal([0, 1, 2], calls.Updates.Select(u => u.Revision).ToHashSet().Order());
        Assert.Equal(NetEase.Title(), L.SourceOf(calls.Shown(2)?.Result));
        Assert.Equal(3, report.Revision);
        Assert.Equal(LyricsPlus.Title(), L.SourceOf(report.Result));
        Assert.Equal(LyricsPlus, report.Candidates[0].Source);
        // Replays get the overall best straight from the cache.
        Assert.Equal(LyricsPlus, Saved.ReadCandidate(Key(options))?.Source);
        var replay = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.True(replay.Cached);
        Assert.Equal(LyricsPlus.Title(), L.SourceOf(replay.Result));
        Assert.Equal(3, calls.Calls.Count);
    }

    // Ported from LyricLookupProgressTests.swift: testMusicsOwnLyricsAreCheckedAgainAndReplaceTheFirstMatch
    [Fact]
    public async Task MusicsOwnLyricsAreCheckedAgainAndReplaceTheFirstMatch()
    {
        var options = Options(AppleMusicCache, Lrclib);
        var calls = new LookupLog();
        var apple = Candidate(AppleMusicCache, 3);
        var line = Candidate(Lrclib, 1);
        // A long grace period: the first match can only show early if Apple Music's pending second look does not
        // count as a source still to answer.
        var timing = Lookups.Fast with { Grace = TimeSpan.FromSeconds(30), AppleRetry = TimeSpan.FromSeconds(1) };
        var lookup = Lookups.Create(_folder.Path, (id, _, _, _) =>
        {
            calls.Call(id);
            if (id == Lrclib)
            {
                return ValueTask.FromResult<LyricCandidate?>(line);
            }
            return ValueTask.FromResult(calls.Count(AppleMusicCache) > 1 ? apple : null);
        }, _clock, timing);
        var report = await lookup.LookupAsync(_query, options, onUpdate: calls.Update, cancellationToken: Ct);
        Assert.Equal(Lrclib.Title(), L.SourceOf(calls.Shown(1)?.Result));
        Assert.Equal(2, report.Revision);
        Assert.Equal(AppleMusicCache.Title(), L.SourceOf(report.Result));
        Assert.Equal(2, calls.Count(AppleMusicCache));
        // Apple Music is read afresh each time; only the online choice is stored.
        Assert.Equal(Lrclib, Saved.ReadCandidate(Key(options))?.Source);
    }

    // Ported from LyricLookupProgressTests.swift: testMusicsLateLyricsReplaceAMatchEvenAfterTheUpgrade
    [Fact]
    public async Task MusicsLateLyricsReplaceAMatchEvenAfterTheUpgrade()
    {
        var options = Options(AppleMusicCache, LyricsPlus, Lrclib);
        var calls = new LookupLog();
        Gate lyricsPlus = new(), music = new();
        var line = Candidate(Lrclib, 1);
        var words = Candidate(LyricsPlus, 3);
        var own = Candidate(AppleMusicCache, 3);
        var timing = Lookups.Fast with { AppleRetry = TimeSpan.FromSeconds(0.05) };
        var lookup = Lookups.Create(_folder.Path, async (id, _, _, ct) =>
        {
            calls.Call(id);
            switch (id)
            {
                case Lrclib:
                    return line;
                case LyricsPlus:
                    await lyricsPlus.WaitAsync(ct);
                    return words;
                case AppleMusicCache:
                    // Apple Music has loaded its lyrics by its second look, which ends after Lyrics+ has already
                    // replaced the first match.
                    if (calls.Count(AppleMusicCache) <= 1)
                    {
                        return null;
                    }
                    await music.WaitAsync(ct);
                    return own;
                default:
                    return null;
            }
        }, _clock, timing);
        var report = await lookup.LookupAsync(_query, options, onUpdate: async update =>
        {
            await calls.Update(update);
            if (update.Revision == 1)
            {
                lyricsPlus.Release();
            }
            if (update.Revision == 2)
            {
                music.Release();
            }
        }, cancellationToken: Ct);
        Assert.Equal(LyricsPlus.Title(), L.SourceOf(calls.Shown(2)?.Result));
        Assert.Equal(3, report.Revision);
        Assert.Equal(AppleMusicCache.Title(), L.SourceOf(report.Result));
        Assert.Equal("Match found · showing", report.States[AppleMusicCache]);
    }

    // Ported from LyricLookupProgressTests.swift: testMusicsOwnLyricsNeedNoNetworkButASearchAsksEverySource
    [Fact]
    public async Task MusicsOwnLyricsNeedNoNetworkButASearchAsksEverySource()
    {
        var options = Options(AppleMusicCache, LyricsPlus, NetEase, Lrclib, QQMusic);
        var calls = new LookupLog();
        var lookup = Fetcher(new() { [AppleMusicCache] = (0, Candidate(AppleMusicCache, 3)) }, calls);
        var report = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Equal(AppleMusicCache.Title(), L.SourceOf(report.Result));
        Assert.Equal(1, report.Revision);
        Assert.Equal(LyricLookup.AppleMusicShowing, report.States[Lrclib]);
        Assert.Equal([AppleMusicCache], calls.Calls);
        Assert.Null(Saved.ReadCandidate(Key(options)));
        var search = await lookup.LookupAsync(_query, options, force: true, cancellationToken: Ct);
        Assert.Equal(AppleMusicCache.Title(), L.SourceOf(search.Result));
        Assert.Equal(options.Enabled.ToHashSet(), calls.Calls.ToHashSet());
    }

    // Ported from LyricLookupProgressTests.swift: testASettledMatchStopsTheSourcesThatCouldNotBeatIt
    [Fact]
    public async Task ASettledMatchStopsTheSourcesThatCouldNotBeatIt()
    {
        var options = Options(LyricsPlus, NetEase, Lrclib);
        var words = Candidate(LyricsPlus, 3);
        var netEase = new Gate();
        var stopped = false;
        // Lyrics+ has word timing and LRCLIB nothing better; NetEase, lower in the order, could only tie, so it is
        // stopped rather than waited for.
        var lookup = Lookups.Create(_folder.Path, async (id, _, _, ct) =>
        {
            switch (id)
            {
                case LyricsPlus:
                    return words;
                case NetEase:
                    await netEase.WaitAsync(ct);
                    stopped |= ct.IsCancellationRequested;
                    return null;
                default:
                    return null;
            }
        }, _clock);
        var report = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.True(report.IsComplete);
        Assert.Equal(LyricsPlus.Title(), L.SourceOf(report.Result));
        Assert.Equal("Not needed: it couldn’t beat the match showing", report.States[NetEase]);
        Assert.True(stopped);
        // Nothing stopped could have beaten it, so replays use it straight away.
        Assert.Equal(LyricsPlus, Saved.ReadCandidate(Key(options))?.Source);

        // A search asks every source, so all the alternatives are listed.
        var open = new Gate();
        var search = Lookups.Create(_folder.Path, async (id, _, _, ct) =>
        {
            if (id == NetEase)
            {
                await open.WaitAsync(ct);
                return null;
            }
            return id == LyricsPlus ? words : null;
        }, _clock);
        var searched = await search.LookupAsync(_query, options, force: true, onUpdate: update =>
        {
            if (update.Revision == 1)
            {
                open.Release();
            }
            return ValueTask.CompletedTask;
        }, cancellationToken: Ct);
        Assert.Equal(LyricLookup.NoUsableMatch, searched.States[NetEase]);
    }

    // Ported from LyricLookupProgressTests.swift: testMissesAreRememberedBrieflyButNotWhenOffline
    [Fact]
    public async Task MissesAreRememberedBrieflyButNotWhenOffline()
    {
        var options = Options(Lrclib, NetEase);
        var calls = new LookupLog();
        var lookup = Fetcher([], calls);
        var first = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Null(first.Result);
        Assert.False(first.Offline);
        var again = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Equal(LyricLookup.RecentMiss, again.States[Lrclib]);
        Assert.Equal(2, calls.Calls.Count);
        await lookup.LookupAsync(_query, options, force: true, cancellationToken: Ct);
        Assert.Equal(4, calls.Calls.Count);

        var offlineCalls = new LookupLog();
        var offline = Fetcher([], offlineCalls, online: false, cache: _folder.Sub("offline"));
        var lost = await offline.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.True(lost.Offline);
        await offline.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Equal(4, offlineCalls.Calls.Count);
        Assert.Empty(new LyricMissCache(TimeSpan.FromSeconds(600)).Answered("key").Sources);
        var expiring = new LyricMissCache(TimeSpan.FromSeconds(600));
        var epoch = DateTimeOffset.UnixEpoch;
        expiring.Insert("key", [Lrclib], now: epoch);
        expiring.Insert("key", [NetEase], now: epoch.AddSeconds(300));
        Assert.Equal(new HashSet<LyricSourceId> { Lrclib, NetEase }, expiring.Answered("key", epoch.AddSeconds(599)).Sources);
        // Each answer expires on its own clock.
        Assert.Equal(new HashSet<LyricSourceId> { NetEase }, expiring.Answered("key", epoch.AddSeconds(601)).Sources);
        Assert.Empty(expiring.Answered("key", epoch.AddSeconds(901)).Sources);
    }

    // Ported from LyricLookupProgressTests.swift: testAReplayDoesNotRenewAnOlderMiss
    [Fact]
    public async Task AReplayDoesNotRenewAnOlderMiss()
    {
        var options = Options(Lrclib, NetEase);
        var calls = new LookupLog();
        // LRCLIB has nothing; NetEase keeps failing, so it is asked every time.
        var lookup = Lookups.Create(_folder.Path, (id, _, _, _) =>
        {
            calls.Call(id);
            return id == NetEase ? throw new InvalidOperationException("Busy") : ValueTask.FromResult<LyricCandidate?>(null);
        }, _clock, misses: new LyricMissCache(TimeSpan.FromSeconds(600), _clock));
        await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        _clock.AdvanceSeconds(400);
        var replay = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Equal(LyricLookup.RecentMiss, replay.States[Lrclib]);
        // Ten minutes after its own answer LRCLIB is asked again, however often the song was replayed meanwhile.
        _clock.AdvanceSeconds(400);
        var later = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Equal(LyricLookup.NoUsableMatch, later.States[Lrclib]);
        Assert.Equal(2, calls.Count(Lrclib));
        Assert.Equal(3, calls.Count(NetEase));
    }

    // Ported from LyricLookupProgressTests.swift: testCloseMatchesAreListedAgainOnAReplayWithoutAsking
    [Fact]
    public async Task CloseMatchesAreListedAgainOnAReplayWithoutAsking()
    {
        var options = Options(Lrclib);
        var calls = new LookupLog();
        // Another edit, six seconds longer than the recording playing.
        var lookup = Fetcher(new() { [Lrclib] = (0, Candidate(Lrclib, 1, duration: 186)) }, calls);
        var first = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Null(first.Result);
        Assert.Equal(["Rejected: duration 3:06 vs 3:00"], first.Rejected.Select(r => r.Reason));
        var replay = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Equal(["Rejected: duration 3:06 vs 3:00"], replay.Rejected.Select(r => r.Reason));
        Assert.Equal("Rejected: duration 3:06 vs 3:00", replay.States[Lrclib]);
        Assert.Equal(1, calls.Count(Lrclib));
    }

    // Ported from LyricLookupProgressTests.swift: testOnlySourcesThatAnsweredAreSkippedOnAReplay
    [Fact]
    public async Task OnlySourcesThatAnsweredAreSkippedOnAReplay()
    {
        var options = Options(Lrclib, NetEase);
        var calls = new LookupLog();
        var lookup = Lookups.Create(_folder.Path, (id, _, _, _) =>
        {
            calls.Call(id);
            return id == Lrclib ? throw new InvalidOperationException("Busy") : ValueTask.FromResult<LyricCandidate?>(null);
        }, _clock);
        await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        var replay = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Equal(LyricLookup.RecentMiss, replay.States[NetEase]);
        Assert.Equal(2, calls.Count(Lrclib));
        Assert.Equal(1, calls.Count(NetEase));
    }

    // Ported from LyricLookupProgressTests.swift: testStreamsAndSavedChoicesNeedNoLookup
    [Fact]
    public async Task StreamsAndSavedChoicesNeedNoLookup()
    {
        var options = Options(Lrclib);
        var calls = new LookupLog();
        var lookup = Fetcher(new() { [Lrclib] = (0, Candidate(Lrclib, 1)) }, calls);
        var stream = await lookup.LookupAsync(new LyricQuery("Radio One", "", null, 0), options, cancellationToken: Ct);
        Assert.True(stream.SkippedStream);
        Assert.Null(stream.Result);
        Assert.True(Saved.Save(Candidate(NetEase, 3).Result, _query.RecordingKey, pin: true));
        var pinned = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.True(pinned.Pinned);
        Assert.Equal(NetEase.Title(), L.SourceOf(pinned.Result));
        Assert.Empty(calls.Calls);
    }

    /// <summary>The local lyric files for the playing track, as the local-files workstream would read them.</summary>
    private sealed class FakeLocalFiles
    {
        private int _edits;
        private IReadOnlyList<LyricLine>? _track;

        public void Write(IReadOnlyList<LyricLine> lines)
        {
            _track = lines;
            _edits++;
        }

        /// <summary>Covers only the track's own files: other files in the folder do not change it.</summary>
        public string? Fingerprint(LyricQuery query, string folder) => _track is null ? null : $"track:{_edits}";

        public LyricCandidate? Candidate(LyricQuery query) => _track is null
            ? null
            : new LyricCandidate(Lyrimuse, L.Synced(_track, LyricTimingRules.Precision(_track), "Lyrimuse local files"), "Our Song", "Our Artist", 180, 0);
    }

    // Ported from LyricLookupProgressTests.swift: testEditingTheTracksLocalFileReReadsOnlyThatSource
    // (adapted: the local files are a fake; reading real LRC files is the local-files workstream's)
    [Fact]
    public async Task EditingTheTracksLocalFileReReadsOnlyThatSource()
    {
        var options = new LyricSourceSnapshot([Lyrimuse, Lrclib], LyricMatchMode.AccuracyFirst, _folder.Sub("Lyrimuse"));
        var calls = new LookupLog();
        var files = new FakeLocalFiles();
        var plain = Candidate(Lrclib, 0);
        var lookup = Lookups.Create(_folder.Path, (id, query, _, _) =>
        {
            calls.Call(id);
            return ValueTask.FromResult(id == Lrclib ? plain : files.Candidate(query));
        }, _clock, localFingerprint: files.Fingerprint);
        var first = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Equal(Lrclib.Title(), L.SourceOf(first.Result));
        // A file for another song changes nothing for this one.
        Assert.Null(await lookup.RefreshLocalAsync(_query, first, options, Ct));
        files.Write([L.Line("Sing along", 1, 4), L.Line("Next line", 4, 180)]);
        var update = await lookup.RefreshLocalAsync(_query, first, options, Ct);
        Assert.Equal(first.Revision + 1, update?.Revision);
        Assert.True(update?.Candidates[0] is { Source: Lyrimuse, Duration: 180 });
        Assert.Equal(2, Assert.IsType<LyricResult.Synced>(update?.Result).Lines.Count);
        Assert.Equal(1, calls.Count(Lrclib));
    }

    // Ported from LyricLookupProgressTests.swift: testResultsThatDifferOnlyInTranslationsAreNotTheSame
    [Fact]
    public void ResultsThatDifferOnlyInTranslationsAreNotTheSame()
    {
        var line = L.Line("Sing along", 1, 3);
        var translated = line with { Translation = "Canta conmigo" };
        var timedByStart = line with { EndFromNextLine = true };
        Assert.True(LyricTimingRules.SameResult(L.Synced([line], "Line-synced", "Lyrimuse local files"), L.Synced([line], "Line-synced", "Lyrimuse local files")));
        Assert.False(LyricTimingRules.SameResult(L.Synced([line], "Line-synced", "x"), L.Synced([translated], "Line-synced", "x")));
        Assert.False(LyricTimingRules.SameResult(L.Synced([line], "Line-synced", "x"), L.Synced([timedByStart], "Line-synced", "x")));
    }

    // Ported from LyricLookupProgressTests.swift: testAnEditThatOnlyAddsTranslationsReplacesTheLines
    // (adapted: the local files are a fake)
    [Fact]
    public async Task AnEditThatOnlyAddsTranslationsReplacesTheLines()
    {
        var options = new LyricSourceSnapshot([Lyrimuse], LyricMatchMode.AccuracyFirst, _folder.Sub("Translated"));
        var files = new FakeLocalFiles();
        var lookup = Lookups.Create(_folder.Path, (_, query, _, _) => ValueTask.FromResult(files.Candidate(query)), _clock,
            localFingerprint: files.Fingerprint);
        files.Write([L.Line("Sing along", 1, 4), L.Line("Next line", 4, 180)]);
        var first = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Equal(1, first.Revision);
        // The same lines, each now with a translation at its timestamp.
        files.Write([L.Line("Sing along", 1, 4) with { Translation = "Canta conmigo" }, L.Line("Next line", 4, 180) with { Translation = "La siguiente" }]);
        var update = await lookup.RefreshLocalAsync(_query, first, options, Ct);
        Assert.Equal(first.Revision + 1, update?.Revision);
        Assert.Equal(["Canta conmigo", "La siguiente"], Assert.IsType<LyricResult.Synced>(update?.Result).Lines.Select(l => l.Translation));
    }

    // Windows-only: a source that never answers is given up at its limit, and LRCLIB has the longer one.
    [Fact]
    public async Task SourcesThatNeverAnswerTimeOutAtTheirOwnLimits()
    {
        var options = Options(Lrclib, NetEase);
        var never = new Gate();
        DateTimeOffset start = _clock.GetUtcNow(), netEaseDone = default, lrclibDone = default;
        var lookup = Lookups.Create(_folder.Path, async (id, _, _, ct) =>
        {
            await never.WaitAsync(ct);
            if (id == NetEase)
            {
                netEaseDone = _clock.GetUtcNow();
            }
            else
            {
                lrclibDone = _clock.GetUtcNow();
            }
            return null;
        }, _clock, Lookups.Fast with { LrclibLimit = TimeSpan.FromSeconds(20), SourceLimit = TimeSpan.FromSeconds(8) });
        var report = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Equal(LyricLookup.TimedOut, report.States[Lrclib]);
        Assert.Equal(LyricLookup.TimedOut, report.States[NetEase]);
        Assert.Equal(TimeSpan.FromSeconds(8), netEaseDone - start);
        Assert.Equal(TimeSpan.FromSeconds(20), lrclibDone - start);
        // A time-out is not an answer: nothing is remembered, so both are asked again.
        Assert.Empty(new LyricMissCache().Answered(Key(options)).Sources);
    }

    // Windows-only: an offline failure marks the report offline even when the connection monitor says online.
    [Fact]
    public async Task OfflineFailuresMarkTheLookupOfflineAndAreNotRemembered()
    {
        var options = Options(Lrclib);
        var calls = new LookupLog();
        var lookup = Lookups.Create(_folder.Path, (id, _, _, _) =>
        {
            calls.Call(id);
            throw new LyricSourceException("No connection", isOffline: true);
        }, _clock);
        var report = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.True(report.Offline);
        Assert.Equal("No connection", report.States[Lrclib]);
        await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Equal(2, calls.Count(Lrclib));
        Assert.True(LyricSourceException.IsOfflineError(new HttpRequestException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkUnreachable))));
        Assert.False(LyricSourceException.IsOfflineError(new HttpRequestException("503")));
    }

    // Windows-only: cancelling a lookup stops its sources and caches nothing.
    [Fact]
    public async Task ACancelledLookupStopsItsSourcesAndCachesNothing()
    {
        var options = Options(LyricsPlus, Lrclib);
        var never = new Gate();
        var cancelled = new LookupLog();
        using var cts = new CancellationTokenSource();
        var lookup = Lookups.Create(_folder.Path, async (id, _, _, ct) =>
        {
            if (id == Lrclib)
            {
                return Candidate(Lrclib, 1);
            }
            await never.WaitAsync(ct);
            if (ct.IsCancellationRequested)
            {
                cancelled.Cancel(id);
            }
            return null;
        }, _clock);
        var task = lookup.LookupAsync(_query, options, onUpdate: update =>
        {
            if (update.Revision == 1)
            {
                cts.Cancel();
            }
            return ValueTask.CompletedTask;
        }, cancellationToken: cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Contains(LyricsPlus, cancelled.Cancelled);
        Assert.Null(Saved.ReadCandidate(Key(options)));
    }
}
