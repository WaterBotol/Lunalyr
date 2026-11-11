using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
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
        Assert.Equal(new LyricProvenance("Word-by-word", LyricsPlus.Title()), native.Result.Provenance);
        Assert.Null(LyricRanking.Rejection(native, _query));
        // Every other source is held to the names it reported.
        var reported = LyricCandidates.FromRaw(new RawLyrics(NetEase, LyricFormat.Yrc, "", "曲", "米津玄師", 200), parsed, _query, 110);
        Assert.Equal(("曲", "米津玄師", true), (reported.Title, reported.Artist, reported.MetadataReported));
        Assert.NotNull(LyricRanking.Rejection(reported, _query));
        var unnamed = LyricCandidates.FromRaw(new RawLyrics(NetEase, LyricFormat.Yrc, ""), parsed, _query, 96);
        Assert.Equal(("Kenshi Song", "Kenshi Yonezu", false), (unnamed.Title, unnamed.Artist, unnamed.MetadataReported));
        // Latin names are checked as reported.
        var other = LyricCandidates.FromRaw(new RawLyrics(Lrclib, LyricFormat.Lrc, "", "Other Song", "Kenshi Yonezu", 200), parsed, _query, 1, "LRCLIB");
        Assert.Equal(("Other Song", true), (other.Title, other.MetadataReported));
        Assert.StartsWith("Rejected: title", LyricRanking.Rejection(other, _query), StringComparison.Ordinal);
    }

    private sealed class Source(LyricSourceId id, LyricCandidate? answer) : ILyricSource
    {
        public LyricSourceId Id => id;

        public ValueTask<LyricCandidate?> FetchAsync(LyricQuery query, string localFolder, CancellationToken cancellationToken) =>
            ValueTask.FromResult(answer);
    }

    [Fact]
    public async Task ASourceSetDispatchesBySourceAndUnknownSourcesHaveNothing()
    {
        var hit = new LyricCandidate(Kugou, L.Plain(["la"], "", ""), "Kenshi Song", "Kenshi Yonezu", 200, 0);
        var fetch = new LyricSourceSet([new Source(Kugou, hit)]).Fetch;
        var ct = TestContext.Current.CancellationToken;
        Assert.Same(hit, await fetch(Kugou, _query, "", ct));
        Assert.Null(await fetch(Lrclib, _query, "", ct));
    }
}
