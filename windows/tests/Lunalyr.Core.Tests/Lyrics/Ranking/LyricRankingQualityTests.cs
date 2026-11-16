using System.Globalization;
using System.Text;

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>
/// How lyrics are weighed beyond their sources' leads: word stamps a tool spread over each line rank as line timing.
/// Ported from the Mac's LyricRankingQualityTests.swift (4c0d3ce).
/// </summary>
public sealed class LyricRankingQualityTests
{
    private readonly LyricQuery _query = new("Our Song", "Our Artist", null, 240);

    private static LyricSourceSnapshot Options(LyricSourceId[] order, LyricMatchMode mode = LyricMatchMode.AccuracyFirst) => new(order, mode);

    /// <summary>Lines from a <see cref="LyricRankingFixtures"/> sample.</summary>
    internal static List<LyricLine> Lines(string fixture)
    {
        var lines = new List<LyricLine>();
        foreach (var row in fixture.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var bar = row.IndexOf('|', StringComparison.Ordinal);
            if (bar >= 0)
            {
                var times = Times(row[..bar], ',');
                var text = new StringBuilder();
                var words = new List<LyricWord>();
                foreach (var token in row[(bar + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var glued = token.StartsWith('+');
                    var body = glued ? token[1..] : token;
                    var at = body.LastIndexOf('@');
                    var span = Times(body[(at + 1)..], '-');
                    var word = body[..at];
                    text.Append(text.Length == 0 || glued ? "" : " ").Append(word);
                    words.Add(L.Word(word, span[0], span[1]));
                }
                lines.Add(L.Timed(L.Line(text.ToString(), times[0], times[1]), [.. words]));
                continue;
            }
            var semi = row.IndexOf(';', StringComparison.Ordinal);
            if (semi < 0) continue;
            var lineTimes = Times(row[..semi], ',');
            lines.Add(L.Line(row[(semi + 1)..].Replace('_', ' '), lineTimes[0], lineTimes[1]));
        }
        return lines;
    }

    private static double[] Times(string text, char separator) =>
        [.. text.Split(separator).Select(part => double.Parse(part, CultureInfo.InvariantCulture))];

    /// <summary>
    /// The same lines with each word-timed line's words spread by a tool over the line: equal shares, or in proportion
    /// to their letters plus one. Rounded to centiseconds, as an LRC file keeps them.
    /// </summary>
    internal static List<LyricLine> Spread(IReadOnlyList<LyricLine> lines, bool equal) =>
    [
        .. lines.Select(line =>
        {
            if (!line.WordTimed || line.Words.Count == 0) return line;
            var weights = line.Words.Select(w => equal ? 1.0 : w.Text.EnumerateRunes().Count(Rune.IsLetterOrDigit) + 1.0).ToList();
            double total = weights.Sum(), span = line.Words[^1].End - line.Start, cursor = line.Start;
            var words = new List<LyricWord>();
            for (var i = 0; i < line.Words.Count; i++)
            {
                var start = Math.Round(cursor * 100, MidpointRounding.AwayFromZero) / 100;
                cursor += span * weights[i] / total;
                words.Add(L.Word(line.Words[i].Text, start, Math.Min(line.End, Math.Round(cursor * 100, MidpointRounding.AwayFromZero) / 100)));
            }
            return line with { Words = words };
        }),
    ];

    private LyricCandidate Candidate(LyricSourceId source, IReadOnlyList<LyricLine> lines) =>
        new(source, L.Synced(lines, LyricTimingRules.Precision(lines), source.Title()), _query.Title, _query.Artist, _query.Duration, 0);

    public static TheoryData<string, string> Genuine => new()
    {
        { "Apple TTML via Lyrics+", LyricRankingFixtures.AppleTTMLLatin }, { "QQ QRC", LyricRankingFixtures.QrcLatin },
        { "NetEase YRC", LyricRankingFixtures.YrcLatin }, { "LRCLIB word", LyricRankingFixtures.LrclibWordLatin },
        { "PetitLyrics", LyricRankingFixtures.PetitLatin }, { "Kugou KRC", LyricRankingFixtures.KrcLatin },
        { "Kugou KRC, Chinese", LyricRankingFixtures.KrcChinese }, { "AMLL TTML, Japanese", LyricRankingFixtures.TtmlJapanese },
        { "QQ QRC, Korean", LyricRankingFixtures.QrcKorean },
    };

    // Ported from LyricRankingQualityTests.swift: testGenuineWordTimingFromEveryServiceKeepsItsTier
    [Theory]
    [MemberData(nameof(Genuine))]
    public void GenuineWordTimingFromEveryServiceKeepsItsTier(string name, string fixture)
    {
        var lines = Lines(fixture);
        var result = L.Synced(lines, "", name);
        Assert.True(LyricTimingRules.TimingTier(result) >= 2, $"{name} parses as word timing");
        Assert.False(LyricSpreadTiming.HasEvenlySpreadWordTiming(lines), name);
        Assert.Equal(2, LyricTimingRules.RankedTiming(result));
    }

    // Ported from LyricRankingQualityTests.swift: testWordsSpreadByAToolRankAsLineTiming (the Mac's lookup-trace
    // label "spread" has no Windows counterpart: the source list's note is checked instead).
    [Theory]
    [MemberData(nameof(Genuine))]
    public void WordsSpreadByAToolRankAsLineTiming(string name, string fixture)
    {
        foreach (var equal in new[] { true, false })
        {
            var lines = Spread(Lines(fixture), equal);
            var result = L.Synced(lines, "", name);
            Assert.True(LyricTimingRules.TimingTier(result) >= 2, $"{name} still looks word-timed");
            Assert.True(LyricSpreadTiming.HasEvenlySpreadWordTiming(lines), $"{name} spread {(equal ? "equally" : "by length")}");
            Assert.Equal(1, LyricTimingRules.RankedTiming(result));
        }
    }

    [Fact]
    public void TheSourceListSaysSpreadWordTimingRanksAsLineTiming()
    {
        var spread = Candidate(Lrclib, Spread(Lines(LyricRankingFixtures.QrcLatin), equal: true));
        var genuine = Candidate(QQMusic, Lines(LyricRankingFixtures.QrcLatin));
        var options = Options([Lrclib, QQMusic]);
        var states = new Dictionary<LyricSourceId, string> { [Lrclib] = "Found", [QQMusic] = "Found" };
        var notes = LyricRanking.Annotate(states, LyricRanking.Screen([spread, genuine], _query, options), QQMusic, _query);
        Assert.Contains("word timing spread evenly over each line (ranked as line timing)", notes[Lrclib], StringComparison.Ordinal);
        Assert.DoesNotContain("spread evenly", notes[QQMusic], StringComparison.Ordinal);
    }

    // Ported from LyricRankingQualityTests.swift: testAFewSpreadLinesDoNotDemoteAFile
    [Fact]
    public void AFewSpreadLinesDoNotDemoteAFile()
    {
        var lines = Lines(LyricRankingFixtures.QrcLatin);
        var spread = Spread(lines, equal: true);
        for (var i = 0; i < lines.Count / 3; i++) lines[i] = spread[i];
        Assert.False(LyricSpreadTiming.HasEvenlySpreadWordTiming(lines));
    }

    // Ported from LyricRankingQualityTests.swift: testSpreadWordTimingLosesToGenuineAndCouldBeatAgrees
    [Fact]
    public void SpreadWordTimingLosesToGenuineAndCouldBeatAgrees()
    {
        LyricSourceId[] order = [Lrclib, QQMusic, Musixmatch, Vibe];
        var real = Lines(LyricRankingFixtures.QrcLatin);
        var fake = Candidate(Lrclib, Spread(real, equal: false));
        var genuine = Candidate(QQMusic, real);
        foreach (var mode in LyricMatchModeInfo.All)
        {
            var options = Options(order, mode);
            Assert.Equal(QQMusic, LyricRanking.Screen([fake, genuine], _query, options).Accepted.FirstOrDefault()?.Source);
            // A word-capable source could still beat it; a line-only source with a lower lead could not.
            Assert.True(LyricRanking.CouldBeat(fake, QQMusic, _query, options));
            Assert.True(LyricRanking.CouldBeat(fake, Musixmatch, _query, options));
            Assert.False(LyricRanking.IsSettled(fake, [QQMusic], _query, options));
            // LRCLIB's genuine word timing leads QQ's, so QQ is not needed.
            Assert.False(LyricRanking.CouldBeat(Candidate(Lrclib, real), QQMusic, _query, options));
        }
        // Spread, it ranks with line timing: Musixmatch's line timing leads it.
        var musixmatch = Candidate(Musixmatch, [.. real.Select(line => L.Line(line.Text, line.Start, line.End))]);
        Assert.Equal(Musixmatch, LyricRanking.Screen([fake, musixmatch], _query, Options(order)).Accepted.FirstOrDefault()?.Source);
    }

    // ---- Shifted timeline (the Mac's 265d728) ----

    private static readonly string[] Verses =
    [
        "Morning comes around again", "Walking down the empty street", "Every light is turning green", "I can hear you calling out",
        "Hold the line and wait for me", "Nothing ever stays the same", "Count the stars above the town", "Never let the music fade",
    ];

    /// <summary>Line-timed verses, every line <paramref name="shift"/> seconds later, plus <paramref name="jitter"/>.</summary>
    private LyricCandidate TimedVerses(LyricSourceId source, double shift = 0, Func<int, double>? jitter = null) =>
        Candidate(source, [.. Verses.Select((text, index) =>
        {
            var start = (index * 4.5) + 10 + shift + (jitter?.Invoke(index) ?? 0);
            return L.Line(text, start, start + 4);
        })]);

    /// <summary>Whether a pending source keeps answering: it could beat what shows, or show it shifted or outvoted.</summary>
    private bool KeepsAnswering(LyricSourceId source, LyricCandidate best, IReadOnlyList<LyricCandidate> found, LyricSourceSnapshot options) =>
        LyricRanking.Matters(source, best, best, _query, options) || LyricRanking.CouldDemote(best, source, found, _query);

    // Ported from LyricRankingQualityTests.swift: testATimelineOffFromTwoThatAgreeIsShifted
    [Fact]
    public void ATimelineOffFromTwoThatAgreeIsShifted()
    {
        LyricSourceId[] order = [Musixmatch, NetEase, QQMusic];
        foreach (var mode in LyricMatchModeInfo.All)
        {
            var screened = LyricRanking.Screen([TimedVerses(Musixmatch, 2.1, i => i % 2 == 0 ? 0.1 : -0.1), TimedVerses(NetEase), TimedVerses(QQMusic, 0.2)],
                _query, Options(order, mode)).Accepted;
            Assert.Equal([NetEase, QQMusic, Musixmatch], screened.Select(c => c.Source));
            Assert.Equal(2.0, screened[^1].Agreement.Shift ?? 0, 0.15);
            Assert.Null(screened[0].Agreement.Shift);
            Assert.Null(screened[1].Agreement.Shift);
        }
    }

    // Ported from LyricRankingQualityTests.swift: testTwoThatDisagreeCannotTellWhichIsShifted
    [Fact]
    public void TwoThatDisagreeCannotTellWhichIsShifted()
    {
        LyricSourceId[] order = [Musixmatch, NetEase, QQMusic, Kugou];
        var pair = LyricRanking.Screen([TimedVerses(Musixmatch, 2.5), TimedVerses(NetEase)], _query, Options(order)).Accepted;
        Assert.Equal(Musixmatch, pair[0].Source);
        Assert.All(pair, c => Assert.Null(c.Agreement.Shift));
        // Two against two: no telling either.
        var even = LyricRanking.Screen([TimedVerses(Musixmatch, 2.5), TimedVerses(Kugou, 2.4), TimedVerses(NetEase), TimedVerses(QQMusic)],
            _query, Options(order)).Accepted;
        Assert.All(even, c => Assert.Null(c.Agreement.Shift));
        Assert.Equal(Musixmatch, even[0].Source);
    }

    // Ported from LyricRankingQualityTests.swift: testAnUnsteadyOffsetIsNotAShift
    [Fact]
    public void AnUnsteadyOffsetIsNotAShift()
    {
        LyricSourceId[] order = [Musixmatch, NetEase, QQMusic];
        // Off by two seconds at the median, but scattered from one to four: badly synced, not another master.
        double[] scatter = [1, 4, 2, 3.5, 1.2, 2.1, 3.8, 1.5];
        var screened = LyricRanking.Screen([TimedVerses(Musixmatch, jitter: i => scatter[i]), TimedVerses(NetEase), TimedVerses(QQMusic)],
            _query, Options(order)).Accepted;
        Assert.All(screened, c => Assert.Null(c.Agreement.Shift));
    }

    // Ported from LyricRankingQualityTests.swift: testMusicsOwnTimingIsTheReference
    [Fact]
    public void MusicsOwnTimingIsTheReference()
    {
        LyricSourceId[] order = [AppleMusicCache, Musixmatch, NetEase];
        var screened = LyricRanking.Screen([TimedVerses(AppleMusicCache), TimedVerses(Musixmatch, -1.8)], _query, Options(order)).Accepted;
        Assert.Equal(AppleMusicCache, screened[0].Source);
        Assert.Equal(-1.8, screened[^1].Agreement.Shift ?? 0, 0.01);
        // Apple Music's own is never the shifted one, whatever the others say.
        var outvoted = LyricRanking.Screen([TimedVerses(AppleMusicCache, 3), TimedVerses(Musixmatch), TimedVerses(NetEase, 0.1)],
            _query, Options(order)).Accepted;
        Assert.Null(outvoted.First(c => c.Source == AppleMusicCache).Agreement.Shift);
        Assert.NotNull(outvoted.First(c => c.Source == Musixmatch).Agreement.Shift);
    }

    // Ported from LyricRankingQualityTests.swift: testAPendingSourceThatCouldShowTheShiftStillMatters (the Mac's
    // matters(among:) is Matters or CouldDemote here, as the lookup asks them)
    [Fact]
    public void APendingSourceThatCouldShowTheShiftStillMatters()
    {
        var options = Options([Musixmatch, NetEase, QQMusic, Vibe]);
        var found = LyricRanking.Screen([TimedVerses(Musixmatch), TimedVerses(NetEase, 2)], _query, options).Accepted;
        var best = found[0];
        Assert.Equal(Musixmatch, best.Source);
        // VIBE (line timing only) cannot outrank Musixmatch, but agreeing with NetEase it would show Musixmatch shifted.
        Assert.False(LyricRanking.CouldBeat(best, Vibe, _query, options));
        Assert.True(KeepsAnswering(Vibe, best, found, options));
        // With another service backing Musixmatch's timing, nothing could.
        var backed = LyricRanking.Screen([TimedVerses(Musixmatch), TimedVerses(NetEase, 2), TimedVerses(QQMusic, 0.1)], _query, options).Accepted;
        Assert.False(KeepsAnswering(Vibe, backed[0], backed, options));
    }

    // Ported from LyricRankingQualityTests.swift: testTheSourceListSaysWhyAShiftedMatchRanksLower
    [Fact]
    public async Task TheSourceListSaysWhyAShiftedMatchRanksLower()
    {
        using var folder = new TempFolder();
        var answers = new Dictionary<LyricSourceId, LyricCandidate>
        {
            [Musixmatch] = TimedVerses(Musixmatch, 2), [NetEase] = TimedVerses(NetEase), [QQMusic] = TimedVerses(QQMusic, 0.3),
        };
        var lookup = Lookups.Create(folder.Path, (id, _, _, _) => ValueTask.FromResult(answers.GetValueOrDefault(id)), new FakeClock());
        var report = await lookup.LookupAsync(_query, Options([Musixmatch, NetEase, QQMusic]), force: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(NetEase, report.Candidates[0].Source);
        Assert.True(report.States[Musixmatch].Contains("timed to another recording (1.8 s later than the others)", StringComparison.Ordinal), report.States[Musixmatch]);
    }
}
