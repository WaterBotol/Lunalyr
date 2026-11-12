using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Platform.Windows.Tests.Readings;

/// <summary>
/// The Mac's ReadingSyllableRevealTests: "Below lyrics" readings fill in syllable by syllable with the sung word in
/// every layout, not only the stage ones: Editorial, Scattered, Constellation and Echo (one shaped run per glued word)
/// and the rolling stages Spoken, Cascade and Cutout (one rolling word per run). Each token keeps its share of its
/// run's reading; the shares written together are exactly the reading drawn before, so nothing reads differently, and
/// each share shows as its token is sung. Uses the real Apple Music lines of <see cref="StageReadingRevealTests"/>,
/// with the per-frame values worked out as the Windows layers work them out.
/// </summary>
public class ReadingSyllableRevealTests
{
    private static readonly ReadingTestLines T = new(TestReadingServices.Windows);
    private static readonly LyricLine[] Lines = StageReadingRevealTests.Lines;
    private static readonly Dictionary<int, List<LyricWord>> Readings = T.Song(Lines, StageReadingRevealTests.Options);
    private static IWordBreaker Breaker => TestReadingServices.Windows.WordBreaker;

    /// <summary>The part of <paramref name="shares"/> that shows given each share's opacity (empty: the whole).</summary>
    private static string Shown(ReadingShares? shares, ReadOnlySpan<double> opacities) => shares?.Shown(opacities) ?? "";

    // ---- The text is what it was ------------------------------------------------------------------------------------

    // Ported from ReadingSyllableRevealTests.swift: testSharesReadAsTheRunsReadingDid
    [Fact]
    public void SharesReadAsTheRunsReadingDid()
    {
        // For every line and every way a layout groups it (the line's runs, a constellation's phrases, a rolling
        // stage's words), each run's shares written together are the reading Match wrote on its first token before.
        for (var index = 0; index < Lines.Length; index++)
        {
            var line = Lines[index];
            var readings = Readings[index];
            var joined = LyricRuns.Glue(line, line.Words);
            foreach (var glue in new[] { joined, LyricRuns.SplittingWords(line.Words, joined, Breaker), LyricRuns.RollingGlue(line, line.Words, Breaker) })
            {
                var before = LyricReadingPairs.Match(readings, line.Words, glue);
                var shares = LyricReadingPairs.Shares(readings, line.Words, glue);
                foreach (var (start, end) in LyricRuns.Ranges(glue, line.Words.Count))
                {
                    var tokens = line.Words.Skip(start).Take(end - start).ToList();
                    Assert.Equal(before.GetValueOrDefault(tokens[0]), ReadingShares.Of(tokens, shares)?.Text);
                }
            }
        }
    }

    // ---- Editorial, Scattered, Constellation, Echo ------------------------------------------------------------------

    /// <summary>Each run's reading at <paramref name="time"/>, as LyricCompositionLayer draws a word-timed line.</summary>
    private static List<string> Scattered(int index, CompositionKind composition, double time, bool reduceMotion = false)
    {
        var line = Lines[index];
        IReadOnlyList<bool> glue = LyricRuns.Glue(line, line.Words);
        if (composition.HasConnections()) glue = LyricRuns.SplittingWords(line.Words, glue, Breaker);
        var set = LineReadings.For(line, Readings[index], RomanizationDisplay.Below, glue);
        var runShares = set.RunShares(line.Words, glue);
        var typography = EditorialSetting.Layout(line.Words, glue, 60, 11, index, wordTimed: true,
                                                 editorial: composition.HasEditorialTypography(), set.RunReadings(line.Words, glue),
                                                 connections: composition.HasConnections());
        var tokens = typography.Tokens;
        int? next = null;
        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].Source.Start > time) { next = i; break; }
        var energy = OrganicMotion.VocalIntensity(line.Words, time);
        var options = new WordRevealOptions();
        var frames = new EditorialTokenFrame[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            frames[i] = EditorialTokenFrame.Make(tokens[i], next, time, energy, double.PositiveInfinity, reduceMotion,
                                                 i > 0 ? tokens[i - 1].Source.Start : double.NegativeInfinity, options);
        }
        var tokenOpacities = new double[tokens.Count];
        var result = new List<string>();
        foreach (var run in typography.Runs)
        {
            var runTokens = tokenOpacities.AsSpan(run.TokenStart, run.TokenCount);
            var frame = EditorialRunFrame.Make(run, frames, options.RevealsWords, runTokens);
            var shares = runShares?[run.Index];
            if (shares is null || !(frame.ReadingOpacity(runTokens) > 0)) { result.Add(""); continue; }
            var syllables = new double[run.TokenCount];
            if (frame.RevealsByToken) ReadingShares.RelativeToFirst(runTokens, syllables);
            var opacities = new double[shares.Shares.Count];
            result.Add(shares.Opacities(time, frame.RevealsByToken ? syllables : default, reduceMotion, opacities)
                ? Shown(shares, opacities) : shares.Text);
        }
        return result;
    }

    // Ported from ReadingSyllableRevealTests.swift: testScatteredReadingFillsInWithItsRun
    [Fact]
    public void ScatteredReadingFillsInWithItsRun()
    {
        // あなたを思い出す is one shaped run; its reading fills in as 思, い, 出 and す are sung (19.56, 20.57, 20.92,
        // 21.13).
        foreach (var composition in new[] { CompositionKind.Scattered, CompositionKind.Editorial, CompositionKind.Echo })
        {
            Assert.Equal(["anata o"], Scattered(1, composition, 19.0));
            Assert.Equal(["anata o omo"], Scattered(1, composition, 19.8));
            Assert.Equal(["anata o omoi"], Scattered(1, composition, 20.7));
            Assert.Equal(["anata o omoida"], Scattered(1, composition, 21.0));
            Assert.Equal(["anata o omoidasu"], Scattered(1, composition, 21.5));
        }
        // Reduce Motion: a share is there from its onset, with no fade.
        Assert.Equal(["anata o omo"], Scattered(1, CompositionKind.Scattered, 20.50, reduceMotion: true));
        Assert.Equal(["anata o omoi"], Scattered(1, CompositionKind.Scattered, 20.55, reduceMotion: true));
    }

    // Ported from ReadingSyllableRevealTests.swift: testScatteredReadingOfASharedPhrase
    [Fact]
    public void ScatteredReadingOfASharedPhrase()
    {
        // A phrase sung over several words fills in over them too.
        Assert.Equal(["wara"], Scattered(3, CompositionKind.Scattered, 27.8));
        Assert.Equal(["waratte"], Scattered(3, CompositionKind.Scattered, 28.4));
        Assert.Equal(["waratteta"], Scattered(3, CompositionKind.Scattered, 29.0));
    }

    // Ported from ReadingSyllableRevealTests.swift: testConstellationFragmentsFillIn
    [Fact]
    public void ConstellationFragmentsFillIn()
    {
        // A constellation parts the line into fragments; each fragment's reading fills in with it.
        var early = Scattered(1, CompositionKind.Constellation, 19.8);
        var late = Scattered(1, CompositionKind.Constellation, 22.0);
        Assert.Equal("anata o omoidasu", string.Join(" ", late));
        Assert.Contains(early, fragment => fragment.EndsWith("omo", StringComparison.Ordinal));
        Assert.DoesNotContain("omoi", string.Concat(early), StringComparison.Ordinal);
    }

    // Ported from ReadingSyllableRevealTests.swift: testShareFadesInAsAStageSyllableDoes
    [Fact]
    public void ShareFadesInAsAStageSyllableDoes()
    {
        // The fade matches the stage layouts': a share is clear before its onset, fades in over StageMotion.SyllableFade,
        // then holds.
        var line = Lines[1];
        var shares = LyricReadingPairs.Shares(Readings[1], line.Words, LyricRuns.Glue(line, line.Words));
        var ink = ReadingShares.Of(line.Words, shares);
        Assert.NotNull(ink);
        var onset = line.Words[2].Start;
        double Opacity(double time) => ink.Opacity(2, time, default, reduceMotion: false);
        Assert.Equal(0, Opacity(onset - 0.1));
        Assert.True(Opacity(onset + 0.03) > 0);
        Assert.True(Opacity(onset + 0.03) < 1);
        Assert.Equal(1, Opacity(onset + 0.5), 3);
        var stage = new StageRun.Part("い", onset);
        var first = new StageRun.Part("思", line.Words[1].Start);
        Assert.Equal(StageRun.PartOpacity(1, [first, stage], onset + 0.05), Opacity(onset + 0.05), 4);
    }

    // ---- Spoken, Cascade, Cutout -------------------------------------------------------------------------------------

    /// <summary>The rolling word holding <paramref name="text"/>, and what of its reading shows at <paramref name="time"/>.</summary>
    private static (string? Reading, string Shown) Rolling(int index, string text, CompositionKind composition, double time)
    {
        var line = Lines[index];
        var song = new RollingSong([line], Breaker,
                                   new Dictionary<int, IReadOnlyList<LyricWord>> { [0] = Readings[index] }, RomanizationDisplay.Below);
        var window = song.Window(0, time, 1.2);
        var prepared = RollingWords.Prepare(window.Words, 60, window.Readings, window.Following, window.Ordinals, window.Parts,
                                            composition);
        var token = prepared.First(word => word.Source.Text.Contains(text, StringComparison.Ordinal));
        // The reading's text is unchanged.
        Assert.Equal(window.Readings.GetValueOrDefault(token.Source), token.Reading);
        var reveal = new double[Math.Max(1, token.Parts.Count)];
        var revealed = RollingTokenState.PartReveal(token, time, 1, 0, false, new WordRevealOptions(), reveal);
        if (token.Shares is not { } shares) return (token.Reading, token.Reading ?? "");
        var opacities = new double[shares.Shares.Count];
        shares.Opacities(time, revealed ? reveal.AsSpan(0, token.Parts.Count) : default, false, opacities);
        return (token.Reading, Shown(shares, opacities));
    }

    // Ported from ReadingSyllableRevealTests.swift: testRollingWordsReadingFillsIn
    [Fact]
    public void RollingWordsReadingFillsIn()
    {
        foreach (var composition in new[] { CompositionKind.Spoken, CompositionKind.Cascade, CompositionKind.Cutout })
        {
            Assert.Equal("omo", Rolling(1, "思", composition, 19.8).Shown);
            Assert.Equal("omoi", Rolling(1, "思", composition, 20.7).Shown);
            Assert.Equal("omoida", Rolling(1, "思", composition, 21.0).Shown);
            var done = Rolling(1, "思", composition, 22.0);
            Assert.Equal(done.Reading, done.Shown);
        }
        Assert.Equal("waratte", Rolling(3, "笑", CompositionKind.Cutout, 28.4).Shown);
    }
}
