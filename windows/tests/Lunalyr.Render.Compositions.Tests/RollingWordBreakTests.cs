using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Lunalyr.Platform.Readings;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>Spoken's rolling words for unspaced lines, parted by the system's word breaker (ICU on Windows).</summary>
public class RollingWordBreakTests
{
    private static readonly IWordBreaker Breaker = IcuWordBreaker.TryCreate() ?? (IWordBreaker)SimpleWordBreaker.Instance;
    private static readonly WordRevealOptions Options = new();

    private static LyricLine Characters(string text, double start, double step, double length)
    {
        var words = text.Select((c, i) => new LyricWord(c.ToString(), start + i * step, start + i * step + length)).ToArray();
        return new LyricLine { Start = start, End = words[^1].End, Text = text, Words = words, WordTimed = true };
    }

    // Ported from LyricLayerFlowTests.swift: testARollingRunOfCharactersStillArrivesCharacterByCharacter
    [Fact]
    public void ARollingRunOfCharactersStillArrivesCharacterByCharacter()
    {
        var road = Characters("夕焼けの帰り道", 10, 0.4, 0.4);
        var words = road.Words;
        var window = new RollingSong([road], Breaker).Window(0, 10.1, 1.2);
        Assert.Equal(["夕焼けの", "帰り道"], window.Words.Select(word => word.Text));
        Assert.Equal([words[0].Start, words[4].Start], window.Words.Select(word => word.Start));
        var prepared = RollingWords.Prepare(window.Words, 60, parts: window.Parts);
        var run = prepared[0];
        Assert.Equal(words.Take(4), run.Parts);
        Assert.Equal(words.Skip(4), prepared[^1].Parts);
        Assert.Equal(run.Parts.Count + 1, run.Boundaries.Count);
        // Each character of a word still shows on its own onset rather than the whole word appearing at its first.
        double[] Reveal(double time)
        {
            var state = RollingTokenState.Make(run, null, 0, time, 60, 1000, 0, false, WordArrival.Glide, Options);
            var reveal = new double[run.Parts.Count];
            Assert.True(RollingTokenState.PartReveal(run, time, state.Presentation.Opacity, 0, false, Options, reveal));
            return reveal;
        }
        var early = Reveal(10.5);
        Assert.Equal(1, early[0]);
        Assert.True(early[1] > 0.5, "the second character has begun");
        Assert.True(early[3] < 0.3, $"later characters are not there yet: {string.Join(", ", early)}");
        Assert.Equal(1, Reveal(13).Min(), 0.02);
        // A sweeping colour follows the characters too.
        Assert.Equal(2.0 / 4, RollingTokenState.Sweep(run, 10.8), 0.02);
        Assert.Equal(1, RollingTokenState.Sweep(run, 13), 9);
        // A single word has nothing to reveal by part.
        var single = RollingWords.Prepare([words[0]], 60)[0];
        Assert.False(RollingTokenState.PartReveal(single, 10.5, 1, 0, false, Options, new double[4]));
    }

    private const string UnspacedTtml = """<tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" itunes:timing="Word" xml:lang="ja"><body><div><p begin="10.000" end="13.000"><span begin="10.000" end="10.600">夕焼け</span><span begin="10.600" end="10.800">の</span><span begin="10.900" end="11.500">帰り道</span><span begin="11.500" end="11.700">を</span><span begin="11.800" end="12.400">歩い</span><span begin="12.400" end="13.000">た</span></p></div></body></tt>""";

    // Ported from LyricLayerFlowTests.swift: testARollingStageBuildsAnUnspacedLineWordByWord (the words and their
    // arrival; per-word readings need the Japanese reader, and the snapshot gives one reading per line)
    [Fact]
    public void ARollingStageBuildsAnUnspacedLineWordByWord()
    {
        var lines = TtmlParser.Parse(UnspacedTtml, 60);
        var line = Assert.Single(lines);
        Assert.Equal(["夕焼け", "の", "帰り道", "を", "歩い", "た"], line.Words.Select(word => word.Text));
        // One run as written, so Editorial still sets it unbroken...
        Assert.Equal([line.Text], LyricRuns.Labels(line.Words, LyricRuns.Glue(line, line.Words)));
        // ...while Spoken builds it word by word.
        var window = new RollingSong(lines, Breaker).Window(0, 10.1, 1.2);
        Assert.Equal(["夕焼けの", "帰り道を", "歩いた"], window.Words.Select(word => word.Text));
        Assert.Equal([10, 10.9, 11.8], window.Words.Select(word => word.Start));
        Assert.Equal(["帰り道", "を"], window.Parts[window.Words[1]].Select(word => word.Text));
        // While the first word is sung the last is not there yet; each arrives on its own onset.
        var prepared = RollingWords.Prepare(window.Words, 60, parts: window.Parts);
        double Opacity(int index, double time)
        {
            int? next = null;
            for (var i = 0; i < prepared.Length; i++) if (prepared[i].Source.Start > time) { next = i; break; }
            return WordPresentation.Make(prepared[index].Source, index, next, time, Options,
                eligibleSince: index > 0 ? prepared[index - 1].Source.Start : double.NegativeInfinity).Opacity;
        }
        Assert.True(Opacity(0, 10.4) > 0.5);
        Assert.True(Opacity(1, 10.4) <= 0.25, "at most the next word's faint preview");
        Assert.Equal(0, Opacity(2, 10.4));
        Assert.True(Opacity(1, 11.3) > 0.5);
        Assert.Equal(0, Opacity(2, 11.3), 0.25);
        Assert.True(Opacity(2, 12.3) > 0.5);

        // A Chinese line timed by character and a Thai one by syllable roll as their words too; Korean words, already
        // spaced, as they are, and Latin syllables as the word they spell.
        static LyricLine Sung(string[] tokens, string text) => new()
        {
            Start = 20, End = 20 + tokens.Length * 0.4, Text = text, WordTimed = true,
            Words = [.. tokens.Select((token, i) => new LyricWord(token, 20 + i * 0.4, 20.4 + i * 0.4))],
        };
        static string[] Rolled(LyricLine line) =>
            [.. new RollingSong([line], Breaker).Window(0, 20.1, 1.2).Words.Select(word => word.Text)];
        Assert.Equal(["我们", "一起", "走过", "春天"], Rolled(Sung(["我", "们", "一", "起", "走", "过", "春", "天"], "我们一起走过春天")));
        Assert.Equal(["แมว", "นอน", "บน", "โต๊ะ"], Rolled(Sung(["แมว", "นอน", "บน", "โต๊ะ"], "แมวนอนบนโต๊ะ")));
        Assert.Equal(["고양이가", "창문을", "본다"], Rolled(Sung(["고양", "이가", "창문", "을", "본다"], "고양이가 창문을 본다")));
        Assert.Equal(["So", "beautiful"], Rolled(Sung(["So", "beau", "ti", "ful"], "So beautiful")));
    }

    // Ported from LyricLayerFlowTests.swift: testNeighbouringWordsAcrossGluedLinesKeepApartOnTheColours
    [Fact]
    public void NeighbouringWordsAcrossGluedLinesKeepApartOnTheColours()
    {
        LyricLine[] lines =
        [
            Characters("夕焼けの帰り道を歩いた", 10, 0.3, 0.3), Characters("窓辺に座って海を見ていた", 14, 0.3, 0.3),
            Characters("朝の電車で手紙を読んだ", 21, 0.3, 0.3),
        ];
        Assert.Equal([11, 12, 11], lines.Select(line => line.Words.Count));
        var song = new RollingSong(lines, Breaker);
        var counts = Enumerable.Range(0, 3).Select(row => song.WordsOf(row).Count).ToArray();
        // Places count the words shown, so the offsets follow the counts (the Mac's segmentation gives 3, 4, 4).
        Assert.Equal([0, counts[0], counts[0] + counts[1], counts.Sum()], song.WordOffsets);
        var window = song.Window(2, 21.5, 1.2);
        Assert.Equal(counts.Sum(), window.Words.Count);
        // Apple's segmentation makes every word a glued run; ICU leaves the odd single character (see docs/READINGS-WINDOWS.md).
        Assert.True(window.Words.Count(word => window.Parts.ContainsKey(word)) >= window.Words.Count - 2,
                    string.Join(" | ", window.Words.Select(word => word.Text)));
        var slots = RollingWords.Prepare(window.Words, 50, ordinals: window.Ordinals).Select(word => word.Slot).ToArray();
        for (var k = 1; k < slots.Length; k++)
        {
            var apart = Math.Abs(slots[k] - slots[k - 1]);
            Assert.True(Math.Min(apart, 1 - apart) > 0.3, string.Join(", ", slots));
        }
        // Spaced words and punctuation: one place per word shown.
        var spaced = new LyricLine
        {
            Start = 30, End = 31.9, Text = "Hold on , now", WordTimed = true,
            Words = [new("Hold", 30, 30.4), new("on", 30.5, 30.9), new(",", 31, 31.4), new("now", 31.5, 31.9)],
        };
        Assert.Equal([0, 3, 3 + counts[0]], new RollingSong([spaced, lines[0]], Breaker).WordOffsets);
    }
}
