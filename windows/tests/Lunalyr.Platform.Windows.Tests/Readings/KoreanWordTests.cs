using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Platform.Windows.Tests.Readings;

/// <summary>
/// Korean lines from Apple Music, timed syllable block by syllable block (the Mac's HangulWordTests.swift, 16 Nov 2026,
/// dd7bc1e, cbb4574, 1c9c348): a Korean word rolls whole from its first block, and each word's romanisation arrives with
/// its word, a Latin word counting once through its own hyphens and apostrophes.
/// </summary>
public class KoreanWordTests
{
    private static readonly ReadingTestLines T = new(TestReadingServices.Windows);
    private static IWordBreaker Breaker => TestReadingServices.Windows.WordBreaker;

    /// <summary>L18 exactly as Apple Music's TTML has it (wave to earth, "love.").</summary>
    private const string L18 =
        """<tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" itunes:timing="Word" xml:lang="en"><body dur="5:07.750"><div begin="3:20.286" end="3:38.890"><p begin="3:20.286" end="3:38.890" itunes:key="L18" ttm:agent="v1"><span begin="3:20.286" end="3:20.790">비</span><span begin="3:20.790" end="3:21.259">로</span><span begin="3:21.259" end="3:21.825">소</span> <span begin="3:21.825" end="3:23.307">우</span><span begin="3:23.307" end="3:23.825">리</span><span begin="3:23.825" end="3:27.122">의</span> <span begin="3:28.051" end="3:28.520">세</span><span begin="3:28.520" end="3:29.054">상</span><span begin="3:29.054" end="3:29.555">이</span> <span begin="3:29.555" end="3:30.968">완</span><span begin="3:30.968" end="3:31.502">벽</span><span begin="3:31.502" end="3:32.386">해</span> <span begin="3:34.790" end="3:35.825">Oh-</span><span begin="3:35.825" end="3:38.890">oh</span></p></div></body></tt>""";

    /// <summary>L6 of the same song: Ah-oh 비로소 내 세상이 되어줘.</summary>
    private const string L6 =
        """<tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" itunes:timing="Word" xml:lang="en"><body dur="5:07.750"><div begin="1:11.539" end="1:23.746"><p begin="1:11.539" end="1:23.746" itunes:key="L6" ttm:agent="v1"><span begin="1:11.539" end="1:12.919">Ah-oh</span> <span begin="1:15.974" end="1:16.406">비</span><span begin="1:16.406" end="1:16.907">로</span><span begin="1:16.907" end="1:17.408">소</span> <span begin="1:17.408" end="1:17.910">내</span> <span begin="1:17.910" end="1:18.374">세</span><span begin="1:18.374" end="1:18.859">상</span><span begin="1:18.859" end="1:19.291">이</span> <span begin="1:19.291" end="1:20.019">되</span><span begin="1:20.019" end="1:21.563">어</span><span begin="1:21.814" end="1:23.746">줘</span></p></div></body></tt>""";

    /// <summary>While 우 is sung (3:21.825 to 3:23.307).</summary>
    private const double During우 = 202.6;

    private static LyricLine Parse(string ttml) => TtmlParser.Parse(ttml, 307.75).First(line => line.WordTimed);

    // Ported from HangulWordTests.swift: testASyllableTimedKoreanLineRollsAsItsWords
    [Fact]
    public void A_syllable_timed_Korean_line_rolls_as_its_words()
    {
        var line = Parse(L18);
        Assert.Equal("비로소 우리의 세상이 완벽해 Oh-oh", line.Text);
        Assert.Equal(["비", "로", "소", "우", "리", "의"], line.Words.Take(6).Select(word => word.Text));
        Assert.Equal(13, line.Words.Count);
        Assert.Equal(["비로소", "우리의", "세상이", "완벽해", "Oh-oh"], LyricRuns.Labels(line.Words, LyricRuns.RollingGlue(line, line.Words, Breaker)));
        var window = new RollingSong([line], Breaker).Window(0, During우, 1.2);
        var ours = window.Words.First(word => word.Text == "우리의");
        Assert.Equal(["우", "리", "의"], window.Parts[ours].Select(part => part.Text));
    }

    // Ported from HangulWordTests.swift: testTheKoreanWordInFocusShowsWholeAndSweepsBySyllable (the reveal; the sweep's
    // share is the block timing's, unchanged)
    [Fact]
    public void The_Korean_word_in_focus_shows_whole()
    {
        var line = Parse(L18);
        var window = new RollingSong([line], Breaker).Window(0, During우, 1.2);
        foreach (var composition in new[] { CompositionKind.Spoken, CompositionKind.Cascade, CompositionKind.Cutout })
        {
            var prepared = RollingWords.Prepare(window.Words, 60, window.Readings, window.Following, window.Ordinals, window.Parts, composition);
            var ours = prepared.First(word => word.Source.Text == "우리의");
            Assert.Equal(3, ours.Parts.Count);
            var reveal = new double[ours.Parts.Count];
            var revealed = RollingTokenState.PartReveal(ours, During우, 1, 0, false, new WordRevealOptions(), reveal);
            Assert.True(!revealed || reveal.All(value => value >= 0.999), $"{composition}: 리 and 의 must not be hidden while 우 is sung");
        }
    }

    // Ported from HangulWordTests.swift: testUnspacedAndLatinRunsStillRevealByPart
    [Fact]
    public void Unspaced_and_Latin_runs_still_reveal_by_part()
    {
        Assert.False(LyricRuns.RevealsByPart("우리의"));
        Assert.False(LyricRuns.RevealsByPart("사랑해요!"));
        Assert.True(LyricRuns.RevealsByPart("夕焼けの"));
        Assert.True(LyricRuns.RevealsByPart("我们"));
        Assert.True(LyricRuns.RevealsByPart("beautiful"));
    }

    private static (LyricLine Line, Dictionary<int, List<LyricWord>> Readings) L6WithReading()
    {
        var ttml = L6.Replace("<body", """<head><metadata><iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal"><transliterations><transliteration xml:lang="ko-Latn"><text for="L6">Ah-oh  bi ro so  nae  se sang i  doe eo jwo</text></transliteration></transliterations></iTunesMetadata></metadata></head><body""", StringComparison.Ordinal);
        var line = Parse(ttml);
        return (line, T.Song([line], ReadingTestLines.Options()));
    }

    // Ported from HangulWordTests.swift: testAppleKoreanReadingIsPlacedWordByWord
    [Fact]
    public void Apples_Korean_reading_is_placed_word_by_word()
    {
        var (line, readings) = L6WithReading();
        Assert.Equal("Ah-oh bi ro so nae se sang i doe eo jwo", line.Romanization);
        var placed = readings[0];
        Assert.Equal(line.Words.Count, placed.Count);
        Assert.Null(LyricReadingPairs.Line(placed, line, RomanizationDisplay.Below));
        var window = new RollingSong([line], Breaker, readings.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<LyricWord>)pair.Value),
                                     RomanizationDisplay.Below).Window(0, 80, 1.2);
        Assert.Equal(["", "biroso", "nae", "sesangi", "doeeojwo"], window.Words.Select(word => window.Readings.GetValueOrDefault(word) ?? ""));
    }

    // Ported from HangulWordTests.swift: testEachReadingShowsWithItsWordOnTheRollingStages (each Korean word's reading
    // comes whole with it: no syllable reveal to share it out, so it shows at the word's strength)
    [Fact]
    public void Each_Korean_words_reading_comes_whole_with_it()
    {
        var (line, readings) = L6WithReading();
        var song = new RollingSong([line], Breaker, readings.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<LyricWord>)pair.Value),
                                   RomanizationDisplay.Below);
        foreach (var composition in new[] { CompositionKind.Spoken, CompositionKind.Cascade, CompositionKind.Cutout })
            foreach (var (word, time) in new[] { ("비로소", 76.8), ("세상이", 78.6), ("되어줘", 80.0) })
            {
                var window = song.Window(0, time, 1.2);
                var prepared = RollingWords.Prepare(window.Words, 60, window.Readings, window.Following, window.Ordinals, window.Parts, composition);
                var token = prepared.First(candidate => candidate.Source.Text == word);
                Assert.NotNull(token.Reading);
                var reveal = new double[Math.Max(1, token.Parts.Count)];
                Assert.False(RollingTokenState.PartReveal(token, time, 1, 0, false, new WordRevealOptions(), reveal),
                             $"{composition}: {word} shows whole, and its reading with it");
            }
    }

    // Ported from HangulWordTests.swift: testHyphenatedAndApostrophisedLatinWordsCountOnce
    [Fact]
    public void Hyphenated_and_apostrophised_Latin_words_count_once()
    {
        (string Text, double Start)[] words = [("Don't", 10.0), ("사", 11.0), ("랑", 11.3), ("Oh-oh", 12.0)];
        var line = new LyricLine
        {
            Start = 10, End = 13, Text = "Don't 사랑 Oh-oh", WordTimed = true, Romanization = "Don't sa rang Oh-oh",
            Words = [.. words.Select(word => new LyricWord(word.Text, word.Start, word.Start + 0.3))],
        };
        Assert.Equal(["Don't", "sa", "rang", "Oh-oh"], T.Texts(line));
    }
}
