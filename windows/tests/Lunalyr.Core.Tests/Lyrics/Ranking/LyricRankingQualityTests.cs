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

    private LyricCandidate Candidate(LyricSourceId source, IReadOnlyList<LyricLine> lines, string? provenance = null) =>
        new(source, L.Synced(lines, LyricTimingRules.Precision(lines), provenance ?? source.Title()), _query.Title, _query.Artist, _query.Duration, 0);

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

    // ---- Merging text and timing (the Mac's 63662fe and bb9c434) ----

    /// <summary>
    /// QQ's word-timed lines (the donor), and Musixmatch's text for them, timed by the line 0.1 s later: lower-cased from
    /// line 3, punctuation dropped from line 5, an ad-lib "(oh)" on line 4, different words on line 6, and a translation
    /// on line 2.
    /// </summary>
    private (LyricCandidate Donor, LyricCandidate Recipient, List<LyricLine> DonorLines, List<LyricLine> RecipientLines) MergePair()
    {
        var donorLines = Lines(LyricRankingFixtures.QrcLatin);
        var recipientLines = donorLines.Select((line, index) =>
        {
            var text = line.Text;
            if (index >= 3) text = text.ToLowerInvariant();
            if (index >= 5) text = new string([.. text.Where(c => !",.?!\"".Contains(c, StringComparison.Ordinal))]);
            if (index == 4) text = string.Join(' ', text.Split(' ').Select((word, i) => i == 2 ? "(oh) " + word : word));
            if (index == 6) text = "Entirely other words sung here instead";
            var end = index + 1 < donorLines.Count ? donorLines[index + 1].Start + 0.1 : line.End;
            return L.Line(text, line.Start + 0.1, end) with { EndFromNextLine = true };
        }).ToList();
        recipientLines[2] = recipientLines[2] with { Translation = "A translation", Romanization = "a reading" };
        return (Candidate(QQMusic, donorLines, "QQ Music"), Candidate(Musixmatch, recipientLines, "Musixmatch"), donorLines, recipientLines);
    }

    private static bool IsAppleRelay(LyricCandidate candidate) =>
        candidate.Source != AppleMusicCache && candidate.RankingSource == AppleMusicCache;

    // Ported from LyricRankingQualityTests.swift: testLineTimedLeadingTextTakesWordTimingWhereItAligns
    [Fact]
    public void LineTimedLeadingTextTakesWordTimingWhereItAligns()
    {
        var pair = MergePair();
        foreach (var mode in LyricMatchModeInfo.All)
        {
            var top = LyricRanking.Screen([pair.Donor, pair.Recipient], _query, Options([QQMusic, Musixmatch], mode)).Accepted[0];
            Assert.Equal(Musixmatch, top.Source);
            var synced = Assert.IsType<LyricResult.Synced>(top.Result);
            Assert.Equal("Musixmatch text · QQ Music word timing", synced.Provenance.Source);
            Assert.Equal("Mixed timing", top.Provenance.Precision);
            Assert.Equal(2, LyricTimingRules.RankedTiming(top.Result));
            Assert.Equal("QQ Music", LyricTimingMerge.TimingDonor(top));
            Assert.False(IsAppleRelay(top));
            var lines = synced.Lines;
            // The text, line times, readings and translations are Musixmatch's own.
            Assert.Equal(pair.RecipientLines.Select(l => l.Text), lines.Select(l => l.Text));
            Assert.Equal(pair.RecipientLines.Select(l => l.Start), lines.Select(l => l.Start));
            Assert.Equal("A translation", lines[2].Translation);
            Assert.Equal("a reading", lines[2].Romanization);
            // Aligned lines carry QQ's word times; the line with other words keeps line timing.
            Assert.True(lines[3].WordTimed);
            Assert.Equal(pair.DonorLines[3].Words.Select(w => w.Start).Skip(1), lines[3].Words.Select(w => w.Start).Skip(1));
            Assert.True(lines[5].WordTimed, "punctuation dropped");
            Assert.False(lines[6].WordTimed, "other words");
            Assert.Equal(pair.RecipientLines[6].Words, lines[6].Words);
            Assert.True(lines.Count(l => l.WordTimed) > lines.Count * 3 / 4);
        }
    }

    // Ported from LyricRankingQualityTests.swift: testAnAdLibKeepsItsBracketsForBackingVocals
    [Fact]
    public void AnAdLibKeepsItsBracketsForBackingVocals()
    {
        var pair = MergePair();
        var top = LyricRanking.Screen([pair.Donor, pair.Recipient], _query, Options([Musixmatch, QQMusic])).Accepted[0];
        var lines = Assert.IsType<LyricResult.Synced>(top.Result).Lines;
        Assert.True(lines[4].WordTimed);
        Assert.True(LyricTimingRules.ValidWordTiming(lines[4]));
        var split = Lunalyr.Core.Lyrics.Parsing.BackingVocals.Split(lines[4].Words, [.. Enumerable.Repeat(false, lines[4].Words.Count)]);
        Assert.Equal(["oh"], split.Backing.Select(w => w.Text));
        Assert.Equal(lines[4].Words.Count - 1, split.Main.Count);
    }

    // Ported from LyricRankingQualityTests.swift: testUnitsCoverTheLineInEveryScript
    [Fact]
    public void UnitsCoverTheLineInEveryScript()
    {
        Assert.Equal(["Don't", "(oh)", "stop,", "now!"], LyricTimingMerge.MergeUnits("Don't (oh) stop, now!").Select(u => u.Text));
        Assert.Equal(["dont", "oh", "stop", "now"], LyricTimingMerge.MergeUnits("Don't (oh) stop, now!").Select(u => u.Key));
        Assert.Equal(["故", "事", "的", "小", "黄", "花", "（Yeah）"], LyricTimingMerge.MergeUnits("故事的小黄花（Yeah）").Select(u => u.Text));
        Assert.Equal(["夜", "に", "駆", "け", "る、", "君", "と"], LyricTimingMerge.MergeUnits("夜に駆ける、君と").Select(u => u.Text));
        Assert.Equal(["이", "밤", "그날의"], LyricTimingMerge.MergeUnits("이 밤 그날의").Select(u => u.Text));
        Assert.True(LyricTimingMerge.SameMergeWord("colour", "color"));
        Assert.True(LyricTimingMerge.SameMergeWord("thinkin", "thinking"));
        Assert.False(LyricTimingMerge.SameMergeWord("love", "live"));
    }

    // Ported from LyricRankingQualityTests.swift: testMergesIntoChineseTextFromKugou
    [Fact]
    public void MergesIntoChineseTextFromKugou()
    {
        var donorLines = Lines(LyricRankingFixtures.KrcChinese);
        var recipientLines = donorLines.Select(line => L.Line(line.Text, line.Start, line.End) with { EndFromNextLine = true }).ToList();
        var ranked = LyricRanking.Screen([Candidate(Kugou, donorLines, "Kugou Music"), Candidate(AppleMusicCache, recipientLines, "Apple Music")],
            _query, Options([AppleMusicCache, Kugou])).Accepted;
        Assert.Equal(AppleMusicCache, ranked[0].Source);
        Assert.Equal("Kugou Music", LyricTimingMerge.TimingDonor(ranked[0]));
        Assert.Equal(2, LyricTimingRules.RankedTiming(ranked[0].Result));
    }

    // Ported from LyricRankingQualityTests.swift: testNoMergeAcrossScriptsTranslationsOrTimelines
    [Fact]
    public void NoMergeAcrossScriptsTranslationsOrTimelines()
    {
        var pair = MergePair();
        var order = Options([Musixmatch, QQMusic, Kugou]);
        bool Merged(LyricCandidate donor, LyricCandidate? recipient = null) =>
            LyricRanking.Screen([donor, recipient ?? pair.Recipient], _query, order).Accepted.Any(c => LyricTimingMerge.TimingDonor(c) is not null);
        Assert.True(Merged(pair.Donor));
        // A translation timed alike: the words never pair.
        var translated = pair.DonorLines.Select((line, index) =>
        {
            var words = line.Words.Select((w, k) => L.Word($"tr{index}x{k}", w.Start, w.End)).ToList();
            return line with { Words = words, Text = string.Join(' ', words.Select(w => w.Text)) };
        }).ToList();
        Assert.False(Merged(Candidate(QQMusic, translated)));
        // Another script: Korean word timing never lends to Latin text, nor Latin to Korean.
        var korean = Lines(LyricRankingFixtures.QrcKorean);
        Assert.False(Merged(Candidate(QQMusic, korean)));
        var koreanText = korean.Select(l => L.Line(l.Text, l.Start, l.End)).ToList();
        Assert.False(Merged(pair.Donor, Candidate(Musixmatch, koreanText)));
        // Timed a second later: another master.
        var late = pair.DonorLines.Select(line => line with
        {
            Start = line.Start + 1, End = line.End + 1, Words = [.. line.Words.Select(w => L.Word(w.Text, w.Start + 1, w.End + 1))],
        }).ToList();
        Assert.False(Merged(Candidate(QQMusic, late)));
        // Word stamps a tool spread are no timing to lend.
        Assert.False(Merged(Candidate(Kugou, Spread(pair.DonorLines, equal: true))));
        // Another version's word timing is not this recording's.
        Assert.False(Merged(pair.Donor with { Title = "Our Song (Live)" }));
    }

    // Ported from LyricRankingQualityTests.swift: testOnlyALeadingSourceTakesTiming
    [Fact]
    public void OnlyALeadingSourceTakesTiming()
    {
        // NetEase's line timing leads nothing: QQ's own word timing shows, unmerged.
        var pair = MergePair();
        var ranked = LyricRanking.Screen([pair.Donor, pair.Recipient with { Source = NetEase }], _query, Options([NetEase, QQMusic])).Accepted;
        Assert.Equal(QQMusic, ranked[0].Source);
        Assert.All(ranked, c => Assert.Null(LyricTimingMerge.TimingDonor(c)));
    }

    // Ported from LyricRankingQualityTests.swift: testEarlyStoppingKeepsSourcesThatCouldLendTiming
    [Fact]
    public void EarlyStoppingKeepsSourcesThatCouldLendTiming()
    {
        var pair = MergePair();
        var options = Options([Musixmatch, QQMusic, Kugou, Vibe], LyricMatchMode.SourceOrder);
        // QQ's words do not pair with Musixmatch's text here, so QQ's word timing shows.
        var unrelated = pair.RecipientLines.Select((line, index) =>
            L.Line(index % 2 == 0 ? line.Text : $"Different words for line {index} here", line.Start, line.End)).ToList();
        var found = LyricRanking.Screen([pair.Donor, Candidate(Musixmatch, unrelated)], _query, options).Accepted;
        var qq = found[0];
        Assert.Equal(QQMusic, qq.Source);
        // Kugou cannot beat QQ, but its word timing might still pair with Musixmatch's text; VIBE has none.
        Assert.False(LyricRanking.CouldBeat(qq, Kugou, _query, options));
        Assert.True(LyricRanking.Matters(Kugou, qq, qq, _query, options, found));
        Assert.False(LyricRanking.IsSettled(qq, [Kugou], _query, options, found));
        Assert.False(LyricRanking.Matters(Vibe, qq, qq, _query, options, found));
        // Once merged, Musixmatch leads and Kugou is not needed.
        found = LyricRanking.Screen([pair.Donor, pair.Recipient], _query, options).Accepted;
        var merged = found[0];
        Assert.Equal(Musixmatch, merged.Source);
        Assert.False(LyricRanking.Matters(Kugou, merged, merged, _query, options, found));
        Assert.True(LyricRanking.IsSettled(merged, [Kugou], _query, options, found));
    }

    // Ported from LyricRankingQualityTests.swift: testTheMergeReplacesTheSameSourceOnScreen
    [Fact]
    public void TheMergeReplacesTheSameSourceOnScreen()
    {
        var pair = MergePair();
        var options = Options([Musixmatch, QQMusic]);
        var publication = new LyricPublication(options, _query);
        var first = LyricRanking.Screen([pair.Recipient], _query, options).Accepted;
        Assert.Equal(PublicationStep.Publish, publication.Step(first[0], false, true, false, first));
        var later = LyricRanking.Screen([pair.Recipient, pair.Donor], _query, options).Accepted;
        Assert.Equal(PublicationStep.Publish, publication.Step(later[0], true, true, false, later));
        Assert.Equal("QQ Music", publication.Shown is { } shown ? LyricTimingMerge.TimingDonor(shown) : null);
        // The same lyrics again change nothing.
        Assert.Equal(PublicationStep.Wait, publication.Step(later[0], true, true, true, later));
    }

    // Ported from LyricRankingQualityTests.swift: testTheLookupShowsAndSavesTheMerge
    [Fact]
    public async Task TheLookupShowsAndSavesTheMerge()
    {
        using var folder = new TempFolder();
        var pair = MergePair();
        var answers = new Dictionary<LyricSourceId, LyricCandidate> { [Musixmatch] = pair.Recipient, [QQMusic] = pair.Donor };
        var clock = new FakeClock();
        var options = Options([Musixmatch, QQMusic]);
        var report = await Lookups.Create(folder.Path, (id, _, _, _) => ValueTask.FromResult(answers.GetValueOrDefault(id)), clock)
            .LookupAsync(_query, options, cancellationToken: TestContext.Current.CancellationToken);
        var synced = Assert.IsType<LyricResult.Synced>(report.Result);
        Assert.Equal("Musixmatch text · QQ Music word timing", synced.Provenance.Source);
        Assert.True(report.States[Musixmatch].Contains("word timing from QQ Music", StringComparison.Ordinal), report.States[Musixmatch]);
        // Saved, the merge is what a replay shows.
        var again = await Lookups.Create(folder.Path, (_, _, _, _) => ValueTask.FromResult<LyricCandidate?>(null), clock)
            .LookupAsync(_query, options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(LyricTimingRules.SameResult(again.Result, report.Result));
    }

    // Ported from LyricRankingQualityTests.swift: testAMergedAppleRelayKeepsItsLead
    [Fact]
    public void AMergedAppleRelayKeepsItsLead()
    {
        Assert.Equal(AppleMusicCache, LyricSourceRanking.RankingSource(LyricsPlus,
            LyricTimingMerge.MergedProvenanceSource("Apple via Lyrics+", "QQ Music")));
        var pair = MergePair();
        var relay = pair.Recipient with
        {
            Source = LyricsPlus,
            Result = L.Synced(((LyricResult.Synced)pair.Recipient.Result).Lines, "", "Apple via Lyrics+"),
        };
        var ranked = LyricRanking.Screen([pair.Donor, relay], _query, Options([QQMusic, LyricsPlus])).Accepted;
        Assert.Equal(LyricsPlus, ranked[0].Source);
        Assert.True(IsAppleRelay(ranked[0]));
        Assert.Equal(1, LyricRanking.Lead(ranked[0], LyricTimingRules.RankedTiming(ranked[0].Result)));
    }
}
