using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>The ranking parts of the Mac's lyric core review (10 Nov): Accuracy first tie-breaks that depended on answer order.</summary>
public sealed class LyricCoreReviewTests : IDisposable
{
    private readonly LyricQuery _song = new("Our Song", "Our Artist", "Our Album", 180);
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static LyricCandidate Words(LyricSourceId source, double? duration) =>
        new(source, L.Synced([L.Timed(L.Line("Sing along", 1, 3), L.Word("Sing", 1, 2), L.Word("along", 2, 3))], "Word-by-word", source.Title()),
            "Our Song", "Our Artist", duration, 0);

    // Ported from LyricCoreReviewTests.swift: testAMatchWithoutALengthWaitsForOneWhoseLengthFits
    [Fact]
    public void AMatchWithoutALengthWaitsForOneWhoseLengthFits()
    {
        var options = L.Options(LyricMatchMode.AccuracyFirst, LyricsPlus, NetEase);
        var unmeasured = Words(LyricsPlus, null);
        var measured = Words(NetEase, 180);
        // Ranking puts the fitting length first...
        Assert.True(LyricRanking.Outranks(measured, unmeasured, _song, options));
        // ...so a source that may still return one is worth waiting for.
        Assert.True(LyricRanking.CouldBeat(unmeasured, NetEase, _song, options));
        Assert.False(LyricRanking.IsSettled(unmeasured, [NetEase], _song, options));
        // An exact match with a fitting length is beaten only by the order.
        Assert.False(LyricRanking.CouldBeat(measured, Amll, _song, L.Options(LyricMatchMode.AccuracyFirst, NetEase, Amll)));
        Assert.True(LyricRanking.CouldBeat(measured, LyricsPlus, _song, options));
    }

    // Ported from LyricCoreReviewTests.swift: testTheSameMatchWinsWhicheverSourceAnswersFirst
    [Fact]
    public async Task TheSameMatchWinsWhicheverSourceAnswersFirst()
    {
        var options = L.Options(LyricMatchMode.AccuracyFirst, LyricsPlus, NetEase);
        var timing = new LyricLookupTiming
        {
            Grace = TimeSpan.FromSeconds(0.1),
            UpgradeSettle = TimeSpan.FromSeconds(0.05),
            AppleRetry = TimeSpan.FromSeconds(0.1),
            LrclibLimit = TimeSpan.FromSeconds(3),
            SourceLimit = TimeSpan.FromSeconds(3),
            MusicFirst = TimeSpan.FromSeconds(0.1),
        };
        var unmeasured = Words(LyricsPlus, null);
        var measured = Words(NetEase, 180);
        foreach (var slow in new[] { LyricsPlus, NetEase })
        {
            var clock = new FakeClock();
            var lookup = Lookups.Create(_folder.Sub(slow.RawValue()), async (id, _, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(id == slow ? 0.4 : 0.02), clock, ct);
                return id == LyricsPlus ? unmeasured : measured;
            }, clock, timing);
            var report = await lookup.LookupAsync(_song, options, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(NetEase.Title(), Assert.IsType<LyricResult.Synced>(report.Result).Provenance.Source);
        }
    }

    // Windows-only: a service's own choice among its hits prefers the one that is the playing recording.
    [Fact]
    public void BestOfferPrefersTheHitThatIsThePlayingRecording()
    {
        // The exact title, four seconds too long, outscores an edition that fits; it fails the recording check.
        var tooLong = new LyricCandidate(Lrclib, L.Plain(["Sing"], "", ""), "Our Song", "Our Artist", 184, 99);
        var fits = new LyricCandidate(Lrclib, L.Plain(["Sing"], "", ""), "Our Song - Remastered", "Our Artist & Friend", 180, 80);
        Assert.Same(fits, LyricRanking.BestOffer([tooLong, fits], _song));
        Assert.False(LyricRanking.NamesRecording(Lrclib, "Our Song", "Our Artist", 184, _song));
        Assert.True(LyricRanking.NamesRecording(Lrclib, "Our Song", "Our Artist", 180, _song));
        // Among equals the first stays.
        var twin = tooLong with { };
        Assert.Same(tooLong, LyricRanking.BestOffer([tooLong, twin], new LyricQuery("Our Song", "Our Artist", null, 184)));
    }
}
