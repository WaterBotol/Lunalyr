using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using static Lunalyr.Core.Lyrics.LyricSourceId;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>Recording checks explain every rejection, and the cache key only changes with settings that change the choice.</summary>
public sealed class LyricRankingTests
{
    private readonly LyricQuery _query = new("Our Song", "Our Artist", null, 180);

    private static LyricCandidate Candidate(LyricSourceId source, bool words, string title = "Our Song", string artist = "Our Artist",
        double? duration = 180, bool reported = true)
    {
        var line = L.Line("Sing along", 1, 3);
        if (words)
        {
            line = L.Timed(line, L.Word("Sing", 1, 2), L.Word("along", 2, 3));
        }
        return new LyricCandidate(source, L.Synced([line], "fixture", source.Title()), title, artist, duration, 0) { MetadataReported = reported };
    }

    // Ported from LyricRankingTests.swift: testFormerNearMissesAreAcceptedAndRealMismatchesExplained
    [Fact]
    public void FormerNearMissesAreAcceptedAndRealMismatchesExplained()
    {
        var options = L.Options(LyricMatchMode.AccuracyFirst, NetEase, Lrclib);
        var remaster = new LyricQuery("Our Song (2011 Remaster)", "Our Artist", null, 180);
        var screened = LyricRanking.Screen([Candidate(Lrclib, false, title: "Our Song - Remastered 2011")], remaster, options);
        Assert.Equal([Lrclib], screened.Accepted.Select(c => c.Source));

        // A six-second-longer edit with word timing used to beat the exact match.
        var longer = Candidate(NetEase, true, duration: 186);
        var result = LyricRanking.Screen([longer, Candidate(Lrclib, false)], _query, options);
        Assert.Equal([Lrclib], result.Accepted.Select(c => c.Source));
        Assert.Equal("Rejected: duration 3:06 vs 3:00", result.Rejected[0].Reason);
        Assert.Equal(Lrclib, LyricRanking.Choose(result.Accepted, _query, options)?.Source);
        Assert.Equal("Rejected: artist “Someone Else”", LyricRanking.Rejection(Candidate(QQMusic, false, artist: "Someone Else"), _query));
    }

    // Ported from LyricRankingTests.swift: testBlankArtistMatchesOnTitleAndDurationOnly
    [Fact]
    public void BlankArtistMatchesOnTitleAndDurationOnly()
    {
        var untagged = new LyricQuery("Our Song", " ", null, 180);
        Assert.Null(LyricRanking.Rejection(Candidate(Lrclib, false, artist: "Anyone"), untagged));
        Assert.Equal("Rejected: no artist or song length to compare", LyricRanking.Rejection(Candidate(Lrclib, false, duration: null), untagged));
        Assert.Equal(0.5, untagged.ArtistScore("Anyone"));
    }

    // Ported from LyricRankingTests.swift: testServiceThatReportsNoRecordingIsStillCheckedAgainstTheSongLength
    [Fact]
    public void ServiceThatReportsNoRecordingIsStillCheckedAgainstTheSongLength()
    {
        var @long = Candidate(LyricsPlus, true, duration: null, reported: false) with
        {
            Result = L.Synced([L.Line("Coda", 195, 199)], "", ""),
        };
        Assert.Equal("Rejected: lyrics run past the end of this recording", LyricRanking.Rejection(@long, _query));
        Assert.Null(LyricRanking.Rejection(Candidate(LyricsPlus, true, duration: null, reported: false), _query));
    }

    // Ported from LyricRankingTests.swift: testCacheFingerprintIgnoresInactiveSettingsAndLocalSources
    [Fact]
    public void CacheFingerprintIgnoresInactiveSettingsAndLocalSources()
    {
        static string Print(LyricMatchMode mode, params LyricSourceId[] enabled) => new LyricSourceSnapshot(enabled, mode, "", "x").Fingerprint;
        Assert.StartsWith("v18|", Print(LyricMatchMode.AccuracyFirst, Lrclib), StringComparison.Ordinal);
        Assert.NotEqual(Print(LyricMatchMode.AccuracyFirst, Lrclib), Print(LyricMatchMode.SourceOrder, Lrclib));
        Assert.Equal(Print(LyricMatchMode.AccuracyFirst, Lrclib), Print(LyricMatchMode.AccuracyFirst, Lyrimuse, Lrclib, AppleMusicCache));
        Assert.NotEqual(Print(LyricMatchMode.AccuracyFirst, Lrclib, NetEase), Print(LyricMatchMode.AccuracyFirst, NetEase, Lrclib));
        // The Mac's raw identifiers, so both apps key the same way.
        Assert.Equal("v18|priority|qqMusic,lyricsOVH,lrclib", Print(LyricMatchMode.SourceOrder, QQMusic, LyricsOvh, Lrclib));
    }

    // Ported from LyricRankingTests.swift: testLocalFilesWithTheArtistAreNotRejectedOnTheirLength
    // (the LocalLyricBridge.length assertions belong to the local-files workstream)
    [Fact]
    public void LocalFilesWithTheArtistAreNotRejectedOnTheirLength()
    {
        // Exported for the playing track: the artist matches, so [length:] does not decide.
        Assert.Null(LyricRanking.Rejection(Candidate(Lyrimuse, false, duration: 200), _query));
        // Without an artist, the length is all that confirms the recording.
        var untagged = new LyricQuery("Our Song", "", null, 180);
        Assert.Equal("Rejected: duration 3:20 vs 3:00", LyricRanking.Rejection(Candidate(Lyrimuse, false, artist: "", duration: 200), untagged));
    }

    // Ported from LyricRankingTests.swift: testReportedNamesInAnotherScriptFallBackToTheLibrarysNames
    [Fact]
    public void ReportedNamesInAnotherScriptFallBackToTheLibrarysNames()
    {
        Assert.Null(LyricQuery.Checkable("米津玄師", "Kenshi Yonezu"));
        Assert.Null(LyricQuery.Checkable(null, "Our Song"));
        Assert.Equal("Another Song", LyricQuery.Checkable("Another Song", "Our Song"));
        Assert.Equal("Señorita", LyricQuery.Checkable("Señorita", "Senorita"));
        Assert.Equal("夜に駆ける", LyricQuery.Checkable("夜に駆ける", "夜に駆ける"));
    }

    // Ported from LyricRankingTests.swift: testDurationsFromBrokenRepliesDoNotCrashTheClock
    [Fact]
    public void DurationsFromBrokenRepliesDoNotCrashTheClock()
    {
        Assert.Equal("3:41", LyricQuery.Clock(221));
        Assert.Equal("1:02:03", LyricQuery.Clock(3723));
        Assert.Equal("240:00:00", LyricQuery.Clock(1e19));
        Assert.Equal("0:00", LyricQuery.Clock(double.NegativeInfinity));
        Assert.Equal("0:00", LyricQuery.Clock(double.NaN));
    }

    // Ported from LyricRankingTests.swift: testSymbolOnlyNamesPassTheRecordingCheck
    [Fact]
    public void SymbolOnlyNamesPassTheRecordingCheck()
    {
        var butterfly = new LyricQuery("🦋", "!!!", null, 200);
        var match = new LyricCandidate(Lrclib, L.Plain(["la"], "", ""), "🦋", "!!!", 200, 0);
        Assert.Null(LyricRanking.Rejection(match, butterfly));
    }

    /// <summary>A chorus as a listener-made LRC sometimes stamps it: seventeen lines inside five seconds.</summary>
    private static List<LyricLine> BunchedChorus(double start = 92)
    {
        var lines = new List<LyricLine> { L.Line("No place I'd rather be", start - 3, start) };
        var time = start;
        for (var index = 0; index < 17; index++)
        {
            var next = time + 0.3;
            lines.Add(L.Line(index % 3 == 0 ? "N-n-no, no, no" : "No place I'd rather be", time, next));
            time = next;
        }
        lines.Add(L.Line("Sing it once again", time, time + 4));
        return lines;
    }

    private static List<LyricLine> Steady(int count = 20, double gap = 2.5) =>
        [.. Enumerable.Range(0, count).Select(i => L.Line("No place I'd rather be", 10 + i * gap, 10 + (i + 1) * gap))];

    // Ported from LyricRankingTests.swift: testABunchedChorusIsFoundAndSteadyLinesAreNot
    [Fact]
    public void ABunchedChorusIsFoundAndSteadyLinesAreNot()
    {
        var run = LyricGapTiming.FindBunchedRun(BunchedChorus());
        Assert.Equal(18, run?.Count);
        Assert.Equal(92, run?.Start ?? 0, 2);
        Assert.Null(LyricGapTiming.FindBunchedRun(Steady()));
        // Fast but singable: eight syllables a second over 1 s lines.
        Assert.Null(LyricGapTiming.FindBunchedRun(Steady(gap: 1)));
        // Four quick ad-libs in a row are a stutter, not a broken file.
        var stutter = Steady();
        for (var index = 0; index < 3; index++)
        {
            stutter.Insert(9 + index, L.Line("Hey", 30.1 + index * 0.1, 30.2));
        }
        Assert.Null(LyricGapTiming.FindBunchedRun([.. stutter.OrderBy(l => l.Start)]));
        // Credits stamped half a second apart before the song are not lyrics.
        var credits = new[] { "Human Nature - Yot Club", "Lyrics by：Ryan Kaiser", "Composed by：Ryan Kaiser", "Produced by：Ryan Kaiser" }
            .Select((text, i) => L.Line(text, i * 0.5, (i + 1) * 0.5));
        Assert.Null(LyricGapTiming.FindBunchedRun([.. credits, .. Steady()]));
        // Instrumental markers and blank lines do not count as sung.
        var marked = Steady();
        marked.Insert(5, new LyricLine { Start = 20.1, End = 22, Text = "", IsInstrumental = true });
        Assert.Null(LyricGapTiming.FindBunchedRun(marked));
    }

    // Windows-only: credits stamped a moment apart, long enough to be a bunched run if they were counted.
    [Fact]
    public void ARunOfCreditLinesIsNotABunchedRun()
    {
        var credits = new[] { "作词：方文山", "作曲：周杰伦", "编曲：林迈可", "制作人：周杰伦", "混音：杨瑞代", "Producer: Max Martin", "Lyrics by: Tove Lo" }
            .Select((text, i) => L.Line(text, i * 0.1, (i + 1) * 0.1));
        Assert.Null(LyricGapTiming.FindBunchedRun([.. credits, .. Steady()]));
        // The same rhythm for sung lines is a fault.
        var sung = Enumerable.Range(0, 7).Select(i => L.Line("No place I'd rather be", i * 0.1, (i + 1) * 0.1));
        Assert.Equal(7, LyricGapTiming.FindBunchedRun([.. sung, .. Steady()])?.Count);
    }

    // Ported from LyricRankingTests.swift: testSoundTimingOutranksBunchedTimingButNotPlainText
    [Fact]
    public void SoundTimingOutranksBunchedTimingButNotPlainText()
    {
        var options = L.Options(LyricMatchMode.SourceOrder, Lrclib, NetEase);
        var bunched = Candidate(Lrclib, false) with { Result = L.Synced(BunchedChorus(), "Line-synced", "LRCLIB") };
        var sound = Candidate(NetEase, false) with { Result = L.Synced(Steady(), "Line-synced", "NetEase") };
        Assert.Equal([NetEase, Lrclib], LyricRanking.Ranked([bunched, sound], _query, options).Select(c => c.Source));
        // Still shown when it is the only timing found.
        var plain = Candidate(NetEase, false) with { Result = L.Plain(["No place I'd rather be"], "Not synced", "NetEase") };
        Assert.Equal([Lrclib, NetEase], LyricRanking.Ranked([plain, bunched], _query, options).Select(c => c.Source));
    }

    /// <summary>"Hurts Me" as one LRCLIB word-timed upload has it: a line's eleven words inside 0.04 s.</summary>
    private static LyricLine Crammed(double start = 99.09)
    {
        const string text = "I don't need it (I'm obsessed when I don't have it)";
        var words = text.Split(' ').Select((word, i) => L.Word(word, start + i * 0.004, start + (i + 1) * 0.004)).ToArray();
        return L.Timed(new LyricLine { Start = start, End = start + 0.04, Text = text }, words);
    }

    private static List<LyricLine> WordTimed(IEnumerable<LyricLine> lines) =>
        [.. lines.Select(line =>
        {
            var parts = line.Text.Split(' ');
            var step = Math.Min(0.3, (line.End - line.Start) / parts.Length);
            return L.Timed(line, [.. parts.Select((p, i) => L.Word(p, line.Start + i * step, line.Start + (i + 1) * step))]);
        })];

    // Ported from LyricRankingTests.swift: testCrammedWordsAreFoundAndSungWordsAreNot
    [Fact]
    public void CrammedWordsAreFoundAndSungWordsAreNot()
    {
        var song = WordTimed(Steady());
        Assert.Null(LyricTimingRules.TimingFault(song));
        Assert.Equal([99.09], LyricGapTiming.CrushedLines([.. song, Crammed()]));
        Assert.Equal("a line crammed at 1:39", LyricTimingRules.TimingFault([.. song, Crammed()]));
        Assert.Equal("2 lines crammed from 1:39", LyricTimingRules.TimingFault([.. song, Crammed(), Crammed(120)]));
        // A quick "oh-oh-oh" is three syllables: not enough to judge.
        var quick = L.Timed(new LyricLine { Start = 5, End = 5.1, Text = "oh oh oh" },
            L.Word("oh", 5, 5.03), L.Word("oh", 5.03, 5.06), L.Word("oh", 5.06, 5.1));
        Assert.Null(LyricTimingRules.TimingFault([quick, .. song]));
    }

    // Ported from LyricRankingTests.swift: testCrammedLRCLIBWordTimingLosesItsLeadToSoundWordTiming
    [Fact]
    public void CrammedLrclibWordTimingLosesItsLeadToSoundWordTiming()
    {
        var options = L.Options(LyricMatchMode.AccuracyFirst, Lrclib, Kugou);
        var lrclib = Candidate(Lrclib, true) with { Result = L.Synced([.. WordTimed(Steady()), Crammed()], "Word-by-word", "LRCLIB") };
        var kugou = Candidate(Kugou, true) with { Result = L.Synced(WordTimed(Steady()), "Word-by-word", "Kugou Music") };
        Assert.Equal([Kugou, Lrclib], LyricRanking.Ranked([lrclib, kugou], _query, options).Select(c => c.Source));
        // Sound, LRCLIB's word timing keeps its lead.
        lrclib = lrclib with { Result = L.Synced(WordTimed(Steady()), "Word-by-word", "LRCLIB") };
        Assert.Equal([Lrclib, Kugou], LyricRanking.Ranked([kugou, lrclib], _query, options).Select(c => c.Source));
    }

    // Windows-only: the explicit raw identifier table round-trips, and the source facts the lookup relies on.
    [Fact]
    public void SourceIdentifiersAndFactsMatchTheMac()
    {
        foreach (var source in LyricSourceInfo.All)
        {
            Assert.True(LyricSourceInfo.TryParseRaw(source.RawValue(), out var parsed));
            Assert.Equal(source, parsed);
        }
        Assert.False(LyricSourceInfo.TryParseRaw("QQMusic", out _));
        Assert.Equal(["appleMusicCache", "lyrimuse"], LyricSourceInfo.All.Where(s => s.IsLocal()).Select(s => s.RawValue()));
        Assert.Equal(0, LyricsOvh.TimingCeiling());
        Assert.Equal(1, Kuwo.TimingCeiling());
        Assert.Equal(3, Amll.TimingCeiling());
        Assert.True(LyricMatchModeInfo.TryParseRaw("smart", out var mode) && mode == LyricMatchMode.AccuracyFirst);
        Assert.True(LyricMatchModeInfo.TryParseRaw("priority", out mode) && mode == LyricMatchMode.SourceOrder);
    }
}
