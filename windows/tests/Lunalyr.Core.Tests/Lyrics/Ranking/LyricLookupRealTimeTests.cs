using System.Diagnostics;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Storage;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>
/// Lookups as they ship: the system clock, no idle hook, and answers arriving from other threads. Waits are short and
/// the margins wide, so these check behaviour rather than speed.
/// </summary>
public sealed class LyricLookupRealTimeTests : IDisposable
{
    private readonly LyricQuery _query = new("Our Song", "Our Artist", null, 180);
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LyricLookupTiming Quick = new()
    {
        Grace = TimeSpan.FromMilliseconds(30),
        UpgradeSettle = TimeSpan.FromMilliseconds(20),
        AppleRetry = TimeSpan.FromMilliseconds(50),
        LrclibLimit = TimeSpan.FromMilliseconds(400),
        SourceLimit = TimeSpan.FromSeconds(10),
        MusicFirst = TimeSpan.FromMilliseconds(30),
    };

    private static LyricCandidate Candidate(LyricSourceId source, bool words) =>
        new(source, L.Synced([words ? L.Timed(L.Line("Sing along", 1, 3), L.Word("Sing", 1, 2), L.Word("along", 2, 3)) : L.Line("Sing along", 1, 3)],
            "fixture", source.Title()), "Our Song", "Our Artist", 180, 0);

    private LyricLookup Create(LyricSourceFetch fetch) => new(new SavedLyrics(_folder.Path), fetch, new FakeConnectivity(), Quick);

    [Fact]
    public async Task ASlowSourceDoesNotHoldBackAFastMatch()
    {
        var updates = new LookupLog();
        var lookup = Create(async (id, _, _, ct) =>
        {
            await Task.Yield();
            if (id == Lrclib)
            {
                return Candidate(Lrclib, false);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            return null;
        });
        var report = await lookup.LookupAsync(_query, L.Options(LyricMatchMode.AccuracyFirst, LyricsPlus, Lrclib), onUpdate: updates.Update,
            cancellationToken: Ct);
        var shown = updates.Shown(1);
        Assert.NotNull(shown);
        Assert.False(shown.IsComplete);
        Assert.Equal(LyricLookup.Searching, shown.States[LyricsPlus]);
        Assert.Equal(1, report.Revision);
        Assert.Equal(LyricLookup.NoUsableMatch, report.States[LyricsPlus]);
    }

    [Fact]
    public async Task ASourceTimesOutWhileAnotherIsStopped()
    {
        var cancelled = new LookupLog();
        var lookup = Create(async (id, _, _, ct) =>
        {
            await Task.Yield();
            if (id == LyricsPlus)
            {
                return Candidate(LyricsPlus, true);
            }
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.Cancel(id);
                throw;
            }
            return null;
        });
        var options = L.Options(LyricMatchMode.AccuracyFirst, LyricsPlus, NetEase, Lrclib);
        var report = await lookup.LookupAsync(_query, options, cancellationToken: Ct);
        Assert.Equal(LyricsPlus.Title(), L.SourceOf(report.Result));
        // NetEase could only tie, so it is stopped; LRCLIB's word timing could still lead, so it runs to its limit.
        Assert.Equal(LyricLookup.CouldNotBeat, report.States[NetEase]);
        Assert.Equal(LyricLookup.TimedOut, report.States[Lrclib]);
        for (var i = 0; i < 100 && cancelled.Cancelled.Count < 2; i++)
        {
            await Task.Delay(10, Ct);
        }
        Assert.Equal(new HashSet<LyricSourceId> { NetEase, Lrclib }, cancelled.Cancelled.ToHashSet());
        // As on the Mac, only sources stopped early count against caching; one that timed out does not.
        Assert.Equal(LyricsPlus, new SavedLyrics(_folder.Path).ReadCandidate(LyricLookup.CacheKey(_query, options))?.Source);
    }

    private sealed class BlockingSource(LyricSourceId id, TimeSpan block, LyricCandidate? answer) : ILyricSource
    {
        public LyricSourceId Id => id;

        public ValueTask<LyricCandidate?> FetchAsync(LyricQuery query, string localFolder, CancellationToken cancellationToken)
        {
            Thread.Sleep(block); // A provider that blocks before its first await, like a slow file read.
            return ValueTask.FromResult(answer);
        }
    }

    [Fact]
    public async Task AProviderThatBlocksHoldsUpNothingElse()
    {
        var sources = new LyricSourceSet([
            new BlockingSource(NetEase, TimeSpan.FromMilliseconds(600), null),
            new BlockingSource(Lrclib, TimeSpan.Zero, Candidate(Lrclib, false)),
        ]);
        var lookup = Create(sources.Fetch);
        var clock = Stopwatch.StartNew();
        TimeSpan? firstShown = null;
        var report = await lookup.LookupAsync(_query, L.Options(LyricMatchMode.AccuracyFirst, NetEase, Lrclib), onUpdate: update =>
        {
            if (update.Revision == 1)
            {
                firstShown ??= clock.Elapsed;
            }
            return ValueTask.CompletedTask;
        }, cancellationToken: Ct);
        Assert.Equal(LrclibTitle, L.SourceOf(report.Result));
        Assert.NotNull(firstShown);
        Assert.True(firstShown < TimeSpan.FromMilliseconds(450), $"shown after {firstShown}");
    }

    private static readonly string LrclibTitle = Lrclib.Title();
}
