using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Pipeline;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Sources;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>The seam between providers and the lookup.</summary>
public sealed class LyricSourceSeamTests
{
    private readonly LyricQuery _query = new("Kenshi Song", "Kenshi Yonezu", null, 200);

    [Fact]
    public void CandidatesFromRawLyricsCheckTheReportedNames()
    {
        var parsed = L.Synced([L.Timed(L.Line("Sing along", 1, 3), L.Word("Sing", 1, 2), L.Word("along", 2, 3))], "whatever", "parser");
        // Lyrics+ (the Mac's rule for it alone): names in another script stand in with the search's, and only the
        // length is checked.
        var native = LyricCandidates.FromRaw(new RawLyrics(LyricsPlus, LyricFormat.KPoeJson, "", "曲", "米津玄師", null), parsed, _query, 113,
            namesMayBeTransliterated: true);
        Assert.Equal(("Kenshi Song", "Kenshi Yonezu", false), (native.Title, native.Artist, native.MetadataReported));
        // The parser's label is kept; the precision is recomputed from the lines.
        Assert.Equal(new LyricProvenance("Word-by-word", "parser"), native.Result.Provenance);
        Assert.Null(LyricRanking.Rejection(native, _query));
        // Every other source is held to the names it reported.
        var reported = LyricCandidates.FromRaw(new RawLyrics(NetEase, LyricFormat.Yrc, "", "曲", "米津玄師", 200), parsed, _query, 110);
        Assert.Equal(("曲", "米津玄師", true), (reported.Title, reported.Artist, reported.MetadataReported));
        Assert.NotNull(LyricRanking.Rejection(reported, _query));
        var unnamed = LyricCandidates.FromRaw(new RawLyrics(NetEase, LyricFormat.Yrc, ""), parsed, _query, 96);
        Assert.Equal(("Kenshi Song", "Kenshi Yonezu", false), (unnamed.Title, unnamed.Artist, unnamed.MetadataReported));
        // Latin names are checked as reported; an explicit label replaces the parser's.
        var other = LyricCandidates.FromRaw(new RawLyrics(Lrclib, LyricFormat.Lrc, "", "Other Song", "Kenshi Yonezu", 200), parsed, _query, 1, "LRCLIB");
        Assert.Equal(("Other Song", true, "LRCLIB"), (other.Title, other.MetadataReported, other.Result.Provenance.Source));
        Assert.StartsWith("Rejected: title", LyricRanking.Rejection(other, _query), StringComparison.Ordinal);
    }

    private sealed class Source(LyricSourceId id, RawLyrics? answer, LyricSourceAvailability? availability = null) : ILyricSource
    {
        public LyricSourceId Id => id;

        public LyricSourceAvailability Availability => availability ?? LyricSourceAvailability.Available;

        public Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct) => Task.FromResult(answer);
    }

    [Fact]
    public async Task ASourceSetParsesWhatEachSourceFetchedAndUnknownSourcesHaveNothing()
    {
        var raw = new RawLyrics(Kugou, LyricFormat.Lrc, "[00:01.00]作词：某人\n[00:05.00]la la\n[00:09.00]la", "Kenshi Song", "Kenshi Yonezu", 200);
        var fetch = new LyricSourceSet([new Source(Kugou, raw)]).Fetch;
        var ct = TestContext.Current.CancellationToken;
        var hit = await fetch(Kugou, _query, "", ct);
        Assert.NotNull(hit);
        Assert.Equal(new LyricProvenance("Line-synced", "Kugou Music"), hit.Result.Provenance);
        // Credits are flagged on the way in, so they never count as sung lines.
        var lines = Assert.IsType<LyricResult.Synced>(hit.Result).Lines;
        Assert.True(lines[0].Credit);
        Assert.Equal(("Kenshi Song", 200.0), (hit.Title, hit.Duration!.Value));
        Assert.Null(await fetch(Lrclib, _query, "", ct));
    }

    [Fact]
    public void AnUnavailableSourceSaysWhy()
    {
        var set = new LyricSourceSet([new Source(AppleMusicCache, null, LyricSourceAvailability.Unavailable("Not on this PC.")), new Source(Kugou, null)]);
        Assert.Equal("Not on this PC.", set.UnavailableReason(AppleMusicCache));
        Assert.Null(set.UnavailableReason(Kugou));
        Assert.Null(set.UnavailableReason(Lrclib));
    }
}
