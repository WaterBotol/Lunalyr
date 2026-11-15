using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>
/// The parity check of 13 Nov 2026 against the Mac's lookup spec (docs/MAC-LYRIC-LOOKUP-SPEC.md, next/integration
/// 7544de6). The read-only Mac checkout (79b84db) predates these Swift tests, so they are written from the spec's stated
/// rules and examples, named after the Swift tests the spec cites. Lyric texts are invented (the spec cites real songs
/// but gives no texts); names, lengths and expected results are the spec's.
/// </summary>
public class MacSpecParityTests
{
    private readonly LyricQuery _song = new("Our Song", "Our Artist", null, 180);

    private static IEnumerable<LyricSourceSnapshot> Modes(params LyricSourceId[] order) =>
        LyricMatchModeInfo.All.Select(mode => new LyricSourceSnapshot(order, mode));

    /// <summary>A line timed as one token: a whole line (or phrase) dressed as a word, as LRCLIB's Lyricsfile rows are.</summary>
    private static LyricLine PhraseRow(string text, double start) =>
        L.Timed(L.Line(text, start, start + 3), L.Word(text, start, start + 3));

    /// <summary>A line timed character by character, as Kugou's KRC times Japanese.</summary>
    private static LyricLine Syllables(string text, double start)
    {
        var line = L.Line(text, start, start + 3);
        var step = 3.0 / text.Length;
        return L.Timed(line, [.. text.Select((c, i) => L.Word(c.ToString(), start + (i * step), start + ((i + 1) * step)))]);
    }

    /// <summary>A line timed word by word.</summary>
    private static LyricLine Words(string text, double start)
    {
        var line = L.Line(text, start, start + 3);
        var words = text.Split(' ');
        return L.Timed(line, [.. words.Select((w, i) => L.Word(w, start + (i * 0.6), start + (i * 0.6) + 0.5))]);
    }

    private static readonly string[] Japanese =
        ["春の風が吹いて", "遠くの空を見た", "あなたの声がした", "忘れないでいて", "夜が明けるまで", "ここで待っている"];

    private static LyricCandidate Candidate(LyricSourceId source, IEnumerable<LyricLine> lines, string title = "Our Song",
        double? duration = 180, string? label = null)
    {
        var list = lines.ToList();
        return new LyricCandidate(source, L.Synced(list, LyricTimingRules.Precision(list), label ?? source.Title()), title, "Our Artist",
            duration, 0);
    }

    // ---- §5.1 Phrase timing is line timing ------------------------------------------------------------------------

    // Written from the spec (§9 "Lemon — Kenshi Yonezu"): testKugouWordTimingBeatsLRCLIBPhraseRows
    [Fact]
    public void KugouWordTimingBeatsLrclibPhraseRows()
    {
        var lrclib = Candidate(Lrclib, Japanese.Select((text, i) => PhraseRow(text, 10 + (i * 4))));
        var kugou = Candidate(Kugou, Japanese.Select((text, i) => Syllables(text, 10 + (i * 4))));
        Assert.Equal(1, LyricTimingRules.RankedTiming(lrclib.Result));
        Assert.Equal(2, LyricTimingRules.RankedTiming(kugou.Result));
        foreach (var options in Modes(Lrclib, Kugou).Concat(Modes(Kugou, Lrclib)))
        {
            Assert.Equal(Kugou, LyricRanking.Choose([lrclib, kugou], _song, options)?.Source);
            Assert.Equal(Kugou, LyricRanking.Choose([kugou, lrclib], _song, options)?.Source);
            // LRCLIB's phrase rows are line timing, so a pending Kugou could still beat them.
            Assert.True(LyricRanking.CouldBeat(lrclib, Kugou, _song, options));
        }
    }

    // Written from the spec (§5.1, §9 "Lemon — Kenshi Yonezu"): testLemonOnLRCLIBIsLineSynced
    [Fact]
    public void LemonOnLrclibIsLineSynced()
    {
        // Lyricsfile rows timed as their two spaced phrases (Lemon's LRCLIB upload), with invented text.
        var rows = string.Join('\n', Japanese.Take(4).Select((text, i) => $"""
            - text: '{text} {text}'
              start_ms: {10000 + (i * 4000)}
              end_ms: {13000 + (i * 4000)}
              words:
              - text: '{text} '
                start_ms: {10000 + (i * 4000)}
                end_ms: {11500 + (i * 4000)}
              - text: '{text}'
                start_ms: {11500 + (i * 4000)}
                end_ms: {13000 + (i * 4000)}
            """));
        var lines = LyricsfileParser.Parse("lines:\n" + rows);
        Assert.Equal(4, lines.Count);
        Assert.All(lines, line => Assert.False(line.WordTimed));
        var result = new LyricResult.Synced(lines, new LyricProvenance("", "LRCLIB"));
        Assert.Equal(1, LyricTimingRules.TimingTier(result));
        Assert.Equal(LyricLines.LineSynced, LyricTimingRules.Precision(lines));
    }

    // Written from the spec (§5.1, §9 "晴天 — 周杰倫"): testOneRealLineInTenIsNotEnoughEvidence
    [Fact]
    public void OneRealLineInTenIsNotEnoughEvidence()
    {
        // hasWordTimingEvidence: at least one sung line, and at least a quarter of them, with two or more real words.
        static int Tier(int real, int total) => LyricTimingRules.TimingTier(new LyricResult.Synced(
            [.. Enumerable.Range(0, total).Select(i => i < real ? Words("we sing these words", 10 + (i * 4)) : PhraseRow("故事的小黃花從出生那年就飄著", 10 + (i * 4)))],
            new LyricProvenance("", "LRCLIB")));
        Assert.Equal(1, Tier(1, 10));
        Assert.Equal(1, Tier(2, 10));
        Assert.Equal(2, Tier(3, 10));
        Assert.Equal(2, Tier(1, 4));
        Assert.Equal(3, Tier(4, 4));
        // A genuine one-word line ("Yeah!", "晴天") is still word timing, but not evidence of it on its own.
        Assert.True(LyricTimingRules.ValidWordTiming(PhraseRow("晴天", 10)));
        Assert.Equal(1, LyricTimingRules.TimingTier(new LyricResult.Synced([PhraseRow("晴天", 10), PhraseRow("Yeah!", 14)],
            new LyricProvenance("", "LRCLIB"))));
    }

    // ---- §2.3 / §5.3 The Apple relay ------------------------------------------------------------------------------

    // Written from the spec (§2.3): isAppleRelay is "apple" or "apple music", any case.
    [Fact]
    public void AppleAndAppleMusicUpstreamsAreTheAppleRelay()
    {
        foreach (var name in new[] { "Apple", "apple", "Apple Music", "APPLE MUSIC", "qApple" })
        {
            Assert.True(LyricSourceRanking.IsAppleUpstream(name), name);
            Assert.Equal(AppleMusicCache, LyricSourceRanking.RankingSource(LyricsPlus, name + LyricSourceRanking.RelaySuffix));
        }
        foreach (var name in new[] { "Applesauce", "Musixmatch", "Spotify", "QQ", "Lyrics+" })
        {
            Assert.False(LyricSourceRanking.IsAppleUpstream(name), name);
            Assert.Equal(LyricsPlus, LyricSourceRanking.RankingSource(LyricsPlus, name + LyricSourceRanking.RelaySuffix));
        }
        // Only Lyrics+ relays.
        Assert.Equal(NetEase, LyricSourceRanking.RankingSource(NetEase, "Apple Music via Lyrics+"));
        // Labels: "qApple" reads "Apple" (a Windows addition); "Apple Music" stays as the Mac labels it.
        Assert.Equal("Apple", LyricSourceRanking.RelayLabel("qApple"));
        Assert.Equal("Apple Music", LyricSourceRanking.RelayLabel("Apple Music"));
        Assert.Equal("Musixmatch", LyricSourceRanking.RelayLabel("Musixmatch"));
    }

    // Written from the spec (§5.3, §9 "Apple's lyrics via Lyrics+ vs LRCLIB word timing"): LyricAppleRelayTests
    [Fact]
    public void ANodeNamingAppleMusicRelaysAppleAndLeadsLrclibWordTimingInAnyOrder()
    {
        var json = """
            {"type":"Word","metadata":{"source":"Apple Music (via Lyrics+)"},"lyrics":[
            {"time":10000,"duration":3000,"text":"We sing these words","syllabus":[
            {"time":10000,"duration":600,"text":"We "},{"time":10600,"duration":600,"text":"sing "},
            {"time":11200,"duration":600,"text":"these "},{"time":11800,"duration":600,"text":"words"}]}]}
            """;
        Assert.Equal("Apple Music", KPoeJsonParser.Parse(json)?.Source);
        var lines = Enumerable.Range(0, 4).Select(i => Words("we sing these words", 10 + (i * 4))).ToList();
        var relay = Candidate(LyricsPlus, lines, duration: null, label: "Apple Music via Lyrics+");
        var lrclib = Candidate(Lrclib, lines);
        Assert.Equal(AppleMusicCache, relay.RankingSource);
        foreach (var options in Modes(Lrclib, LyricsPlus).Concat(Modes(LyricsPlus, Lrclib)))
        {
            Assert.Equal(LyricsPlus, LyricRanking.Choose([lrclib, relay], _song, options)?.Source);
            Assert.Equal(LyricsPlus, LyricRanking.Choose([relay, lrclib], _song, options)?.Source);
            // Shown, the relay settles every service (§7): nothing but Apple Music's own lyrics could beat it.
            Assert.True(LyricRanking.IsSettled(relay, [Lrclib, NetEase, QQMusic, Kugou, Amll], _song, options));
        }
    }

    // ---- §3.4 couldDemote ------------------------------------------------------------------------------------------

    // Written from the spec (§3.4): couldDemote holds only for LRCLIB's word or mixed timing that fits the version.
    [Fact]
    public void CouldDemoteOnlyKeepsSourcesForLrclibOfThePlayingVersion()
    {
        var lines = Enumerable.Range(0, 5).Select(i => Words("we sing these words", 10 + (i * 4))).ToList();
        var playing = Candidate(Lrclib, lines);
        Assert.True(LyricRanking.CouldDemote(playing, NetEase, [playing], _song));
        var live = Candidate(Lrclib, lines, title: "Our Song (Live)");
        Assert.False(LyricRanking.CouldDemote(live, NetEase, [live], _song));
        // CouldBeat still keeps every timed source for another version's lyrics.
        Assert.True(LyricRanking.CouldBeat(live, NetEase, _song, new LyricSourceSnapshot([Lrclib, NetEase], LyricMatchMode.AccuracyFirst)));
    }

    // ---- §4.2 The recording check -----------------------------------------------------------------------------------

    // Written from the spec (§4.2, §9 "Our Song 3:00 vs a word-timed 3:06 edit"): 180 vs 183 matches, 186 fails.
    [Fact]
    public void OurSongAtThreeMinutesTakesA183EditAndRejectsA186One()
    {
        var words = Enumerable.Range(0, 5).Select(i => Words("we sing these words", 10 + (i * 4))).ToList();
        var lineTimed = words.Select(line => line with { WordTimed = false }).ToList();
        Assert.Null(LyricRanking.Rejection(Candidate(Lrclib, words, duration: 183), _song));
        Assert.Equal("Rejected: duration 3:06 vs 3:00", LyricRanking.Rejection(Candidate(NetEase, words, duration: 186), _song));
        foreach (var options in Modes(NetEase, Lrclib))
        {
            // The word-timed longer edit never wins over line timing for the playing recording.
            var screened = LyricRanking.Screen([Candidate(NetEase, words, duration: 186), Candidate(Lrclib, lineTimed)], _song, options);
            Assert.Equal([Lrclib], screened.Accepted.Select(c => c.Source));
            Assert.Equal([NetEase], screened.Rejected.Select(r => r.Candidate.Source));
        }
    }

    // ---- §0 / §1.1 Sources and the recording key ------------------------------------------------------------------

    // Ported from AMLLAndKRCTests.swift: testAMLLIsAListedSourceAndOnByDefault
    [Fact]
    public void AmllIsAListedSourceAndOnByDefault()
    {
        Assert.Contains(Amll, LyricSourceInfo.All);
        Assert.Contains(Amll, LyricSourceSnapshot.RecommendedOrder);
        Assert.False(Amll.IsLocal());
    }

    // Written from the spec (§1.1): the Mac's own test for this (LyricParsingTests.swift,
    // testRecordingKeyIncludesAlbumAndDuration and testNonFiniteTrackDurationCannotCrashTheCacheKey) traces to the
    // Verso import (a99fd65), so it is not translated; these inputs are this port's.
    [Fact]
    public void TheRecordingKeyTellsAlbumsAndLengthsApartAndSurvivesABrokenLength()
    {
        var studio = new LyricQuery("Rain Again", "Clouds", "First Light", 201.6);
        Assert.Equal("rain again|clouds|first light|202", studio.RecordingKey);
        Assert.NotEqual(studio.RecordingKey, new LyricQuery("Rain Again", "Clouds", "Live at Home", 201.6).RecordingKey);
        Assert.NotEqual(studio.RecordingKey, new LyricQuery("Rain Again", "Clouds", "First Light", 171.6).RecordingKey);
        var broken = new LyricQuery("Rain Again", "Clouds", null, double.NaN);
        Assert.Equal(0, broken.Duration);
        Assert.Equal("rain again|clouds||0", broken.RecordingKey);
    }
}
