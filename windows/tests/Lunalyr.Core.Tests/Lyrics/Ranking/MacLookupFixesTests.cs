using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Pipeline;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Core.Tests.Lyrics.Sources;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>
/// The lookup fixes the Mac landed on next/integration 1cdde21 (its cache policy v16, Windows' v19), checked through
/// the ranking: written from the Mac session's description, since that code and its tests aren't in the read-only
/// checkout.
/// </summary>
public sealed class MacLookupFixesTests
{
    private static readonly LyricQuery Pleaser = new("Pleaser", "Wallows", null, 187.896);

    private static LyricCandidate Relay() =>
        LyricCandidateBuilder.Build(new RawLyrics(LyricsPlus, LyricFormat.KPoeJson, Fixtures.Text("lyricsplus/pleaser-line.json"), "Pleaser",
            "Wallows", null, new Dictionary<string, string> { [RawLyricsExtras.Upstream] = "Apple" }), Pleaser)!;

    private static LyricCandidate Lrclib(bool words)
    {
        var lines = Enumerable.Range(0, 8).Select(i =>
        {
            var start = 0.808 + (i * 3.2);
            var line = L.Line($"Invented line {i + 1} of a made-up song", start, start + 3);
            return words
                ? L.Timed(line, [.. line.Text.Split(' ').Select((w, k) => L.Word(w, start + ((k + L.Sung(k)) * 0.4), start + (k * 0.4) + 0.35))])
                : line;
        }).ToList();
        return new LyricCandidate(LyricSourceId.Lrclib, L.Synced(lines, LyricTimingRules.Precision(lines), "LRCLIB"), "Pleaser", "Wallows", 188, 0);
    }

    /// <summary>
    /// Apple's line-timed lyrics relayed by Lyrics+ (recorded for "Pleaser", text invented) are now a match: line timing,
    /// still ranked as Apple Music's (lead 1), so they beat other line timing but not LRCLIB's word timing.
    /// </summary>
    [Fact]
    public void AppleLineTimingRelayedByLyricsPlusRanksAsApple()
    {
        var relay = Relay();
        Assert.Equal("Line-synced · Apple via Lyrics+", relay.Provenance.Label);
        Assert.Equal(AppleMusicCache, relay.RankingSource);
        Assert.Equal(1, LyricTimingRules.RankedTiming(relay.Result));
        var options = new LyricSourceSnapshot([LyricSourceId.Lrclib, LyricsPlus], LyricMatchMode.AccuracyFirst);
        Assert.Equal([LyricsPlus, LyricSourceId.Lrclib], LyricRanking.Ranked([Lrclib(words: false), relay], Pleaser, options).Select(c => c.Source));
        Assert.Equal([LyricSourceId.Lrclib, LyricsPlus], LyricRanking.Ranked([Lrclib(words: true), relay], Pleaser, options).Select(c => c.Source));
    }

    /// <summary>A line-timed relay showing doesn't stop sources that could still give word timing.</summary>
    [Fact]
    public void ALineTimedRelayLeavesWordTimingSourcesRunning()
    {
        var relay = Relay();
        var options = new LyricSourceSnapshot([LyricsPlus, NetEase, LyricSourceId.Lrclib, Kuwo, LyricsOvh], LyricMatchMode.AccuracyFirst);
        Assert.True(LyricRanking.CouldBeat(relay, NetEase, Pleaser, options));
        Assert.True(LyricRanking.CouldBeat(relay, LyricSourceId.Lrclib, Pleaser, options));
        // Kuwo's lrcx word timing could (the Mac's 7ddb08a); a source without timing can't beat Apple's line timing.
        Assert.True(LyricRanking.CouldBeat(relay, Kuwo, Pleaser, options));
        Assert.False(LyricRanking.CouldBeat(relay, LyricsOvh, Pleaser, options));
    }

    /// <summary>
    /// Agreement compares sung lines only: a plain text's header and credits, at its start or end, no longer stand
    /// between it and the same words elsewhere.
    /// </summary>
    [Fact]
    public void CreditsAndHeadersDontCountInAgreement()
    {
        string[] sung = ["Invented first line here", "Second made up line", "A third line we wrote", "Fourth line for testing", "And the fifth to end"];
        var synced = new LyricCandidate(NetEase, L.Synced([.. sung.Select((text, i) => L.Line(text, 10 + (i * 4), 13 + (i * 4)))], "Line-synced", "NetEase"),
            "Pleaser", "Wallows", 188, 0);
        string[] plain = ["Wallows", "Lyrics by: Braeden Lemasters", .. sung, "Guitar: Cole Preston", "吉他：蔡科俊Again", "鼓：Gary Novak"];
        var ovh = new LyricCandidate(LyricsOvh, L.Plain(plain, "Plain text · no timing", "lyrics.ovh"), "Pleaser", "Wallows", null, 0);
        Assert.Equal(sung.Select(LyricQuery.Normalize), LyricAgreementRules.Text(ovh));
        var weighed = LyricAgreementRules.Weigh([synced, ovh]);
        Assert.All(weighed, candidate => Assert.Equal(1, candidate.Agreement.Peers));
    }
}
