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
}
