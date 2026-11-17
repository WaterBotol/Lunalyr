using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Compositions.StageLines;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>The shared phrase analysis for the stage layouts (the Mac's StagePhrasesTests, same inputs and values).</summary>
public class StagePhrasesTests
{
    private static (StagePhrasing Phrasing, List<StageRun> Runs) Phrasing(IReadOnlyList<LyricWord> words, IReadOnlyList<bool>? glue = null,
                                                                        bool? lineTimed = null)
    {
        var runs = Runs(words, glue);
        return (StagePhrases.Analyse(runs, lineTimed), runs);
    }

    private static string[] Texts(StagePhrasing phrasing, List<StageRun> runs) =>
        [.. phrasing.Phrases.Select(phrase => string.Join(" ", phrase.Runs.Select(id => runs[id].Text)))];

    private static int[][] Spans(StagePhrasing phrasing) => [.. phrasing.Phrases.Select(phrase => new[] { phrase.From, phrase.To })];

    /// <summary>Every run belongs to exactly one phrase, in reading order, and every phrase has one focal run of its own.</summary>
    private static void AssertWellFormed(StagePhrasing phrasing, List<StageRun> runs)
    {
        Assert.Equal(runs.Count, phrasing.Words.Count);
        Assert.Equal(Enumerable.Range(0, runs.Count), phrasing.Phrases.SelectMany(phrase => phrase.Runs));
        for (var index = 0; index < phrasing.Phrases.Count; index++)
        {
            var phrase = phrasing.Phrases[index];
            Assert.Equal(index, phrase.Index);
            Assert.True(phrase.Count > 0);
            Assert.True(phrase.Count <= StagePhraseRules.Default.MaxRuns);
            Assert.True(phrase.Contains(phrase.Focal));
            Assert.Equal([phrase.Focal], phrase.Runs.Where(id => phrasing.Words[id].IsFocal));
            Assert.True(phrase.Start <= phrase.End);
            foreach (var id in phrase.Runs)
            {
                Assert.Equal(index, phrasing.Words[id].Phrase);
                Assert.True(phrase.Start <= runs[id].Start);
                Assert.True(phrase.End >= runs[id].End);
            }
        }
    }

    // Ported from StagePhrasesTests.swift: testShortEnglishLineIsTwoPhrasesOfThree
    [Fact]
    public void ShortEnglishLineIsTwoPhrasesOfThree()
    {
        var (p, runs) = Phrasing(Even("we keep dreaming under neon rain"));
        AssertWellFormed(p, runs);
        Assert.Equal(["we keep dreaming", "under neon rain"], Texts(p, runs));
        Assert.Equal([StagePhraseBreak.LineStart, StagePhraseBreak.Length], p.Phrases.Select(phrase => phrase.BreakBefore));
        Assert.Equal(["dreaming", "rain"], p.Phrases.Select(phrase => runs[phrase.Focal].Text)); // Equal holds: the longer, then the later
        Assert.Equal([true, false, false, true, false, false], p.Words.Select(word => word.IsFunction));
        Assert.Equal(StageWordRole.Function, p.Words[0].Role);
        Assert.Equal(StageWordRole.Content, p.Words[1].Role);
        Assert.DoesNotContain(p.Words, word => word.IsHeld || word.RunGroup is not null);
        Assert.False(p.LineTimed);
        Assert.False(p.RightToLeft);
        Assert.Equal(1, p.Phrases[0].Start, 1e-9);
        Assert.Equal(runs[3].Start, p.Phrases[1].Start, 1e-9);
        Assert.Equal(runs[5].End, p.Phrases[1].End, 1e-9);
    }

    // Ported from StagePhrasesTests.swift: testLongLineWithARunAHeldWordAndAPause
    [Fact]
    public void LongLineWithARunAHeldWordAndAPause()
    {
        var (p, runs) = Phrasing(Mixed);
        AssertWellFormed(p, runs);
        Assert.Equal(["and we keep running", "through the city lights", "until the morning comes"], Texts(p, runs));
        Assert.Equal([StagePhraseBreak.LineStart, StagePhraseBreak.Length, StagePhraseBreak.Gap], p.Phrases.Select(phrase => phrase.BreakBefore));
        // The quick run arrives as one group.
        Assert.Equal([0, 0, 0], p.Words.Take(3).Select(word => word.RunGroup));
        Assert.Equal([StageWordRole.Run, StageWordRole.Run, StageWordRole.Run], p.Words.Take(3).Select(word => word.Role));
        Assert.Null(p.Words[3].RunGroup);
        // The held word is the first phrase's focal word.
        Assert.True(p.Words[3].IsHeld);
        Assert.Equal(1.25, p.Words[3].Duration, 1e-9);
        Assert.Equal(3, p.Phrases[0].Focal);
        Assert.Equal(StageWordRole.Focal, p.Words[3].Role);
        Assert.Equal("lights", runs[p.Phrases[1].Focal].Text);
        Assert.Equal("morning", runs[p.Phrases[2].Focal].Text);
        Assert.Equal(0.6, p.Words[8].GapBefore, 1e-9);
        Assert.Equal([3], p.Words.Where(word => word.IsHeld).Select(word => word.Run));
        Assert.Equal(StageWordRole.Function, p.Words[5].Role);
        // Phrase spans and lookup by time.
        Assert.Equal(5.1, p.Phrases[2].Start, 1e-9);
        Assert.Equal(6.8, p.Phrases[2].End, 1e-9);
        Assert.Null(p.PhraseIndex(0.5));
        Assert.Equal(0, p.PhraseIndex(2.0));
        Assert.Equal(1, p.PhraseIndex(3.0));
        Assert.Equal(1, p.PhraseIndex(4.8)); // Still the second phrase during the pause
        Assert.Equal(2, p.PhraseIndex(5.2));
        Assert.Equal(2, p.PhraseOf(9)?.Index);
        Assert.Null(p.PhraseOf(12));
    }

    // Ported from StagePhrasesTests.swift: testAQuickLineIsOneRunPerPhraseAndNeverSplitsARunWhenItCanHelpIt
    [Fact]
    public void AQuickLineIsOneRunPerPhraseAndNeverSplitsARunWhenItCanHelpIt()
    {
        // Eight words 0.15 s apart, then a pause and two slow ones.
        var words = Even("hey hey we gotta go go go now", step: 0.15);
        words.AddRange(Even("slow down", step: 0.8, from: 3));
        var (p, runs) = Phrasing(words);
        AssertWellFormed(p, runs);
        Assert.Equal(["hey hey we gotta", "go go go now", "slow down"], Texts(p, runs));
        Assert.Equal(6, p.Words.Take(8).Count(word => word.Role == StageWordRole.Run)); // Each phrase's focal word leads it
        Assert.Equal([0], p.Words.Take(8).Select(word => word.RunGroup).OfType<int>().Distinct());
        Assert.Null(p.Words[8].RunGroup);
        // A cut moves by one to keep a function word with the word after it.
        var cuts = StagePhrases.LengthCuts(0, 6, [false, false, true, false, false, false], new int?[6], [0, 0, 0, 0, 0, 0], 4);
        Assert.Equal([2], cuts); // Not "x x the | y"
        // ... and to land on a pause.
        var paused = StagePhrases.LengthCuts(0, 6, new bool[6], new int?[6], [0, 0, 0, 0, 0.5, 0], 4);
        Assert.Equal([4], paused);
    }

    // Ported from StagePhrasesTests.swift: testAHeldNoteIsFocalAndHeld
    [Fact]
    public void AHeldNoteIsFocalAndHeld()
    {
        var (p, runs) = Phrasing([new("oh", 1, 1.3), new("baby", 1.35, 1.7), new("stay", 1.75, 3.4)]);
        AssertWellFormed(p, runs);
        Assert.Single(p.Phrases);
        Assert.Equal(2, p.Phrases[0].Focal);
        Assert.True(p.Words[2].IsHeld);
        Assert.Equal(1.65, p.Words[2].Duration, 1e-9);
        Assert.False(p.Words[1].IsHeld);
        // A held function word is still a function word; if a phrase has nothing else, it leads.
        var (alone, aloneRuns) = Phrasing([new("you", 1, 2.5)]);
        AssertWellFormed(alone, aloneRuns);
        Assert.Equal(0, alone.Phrases[0].Focal);
        Assert.True(alone.Words[0].IsFunction && alone.Words[0].IsHeld);
        Assert.Equal(StageWordRole.Focal, alone.Words[0].Role);
        // A word's end past the next onset does not count as held.
        var (o, _) = Phrasing([new("light", 1, 4), new("fades", 1.4, 1.9)]);
        Assert.Equal(0.4, o.Words[0].Duration, 1e-9);
        Assert.False(o.Words[0].IsHeld);
    }

    // Ported from StagePhrasesTests.swift: testPunctuationEndsAPhrase
    [Fact]
    public void PunctuationEndsAPhrase()
    {
        var (p, runs) = Phrasing(Even("I love you, but I can't stay."));
        AssertWellFormed(p, runs);
        Assert.Equal(["I love you,", "but I can't stay."], Texts(p, runs));
        Assert.Equal(StagePhraseBreak.Punctuation, p.Phrases[1].BreakBefore);
        Assert.True(StagePhrases.EndsPhrase("go!”"));
        Assert.True(StagePhrases.EndsPhrase("君、"));
        Assert.False(StagePhrases.EndsPhrase("don't"));
        Assert.True(StagePhrases.IsFunctionWord("I’m"));
        Assert.True(StagePhrases.IsFunctionWord("The,"));
        Assert.False(StagePhrases.IsFunctionWord("neon"));
    }

    // Ported from StagePhrasesTests.swift: testJapaneseGluedRunsStayWhole
    [Fact]
    public void JapaneseGluedRunsStayWhole()
    {
        var (p, runs) = Phrasing(Japanese, JapaneseGlue);
        AssertWellFormed(p, runs);
        Assert.Equal(["君の", "名前を", "呼んで", "夜空に", "消えた"],
                     runs.Select(run => run.Text));
        // Unspaced words carry more each: at most two to a phrase (StagePhraseRules.UnspacedMaxRuns, the Mac's 7c2493a3).
        Assert.Equal([[0, 2], [2, 3], [3, 5]], Spans(p));
        Assert.DoesNotContain(p.Words, word => word.IsFunction);
        Assert.DoesNotContain(p.Words, word => word.RunGroup is not null); // Glued characters are one run, not a quick run
        // The analysis through the stage's inputs gives the same.
        Assert.Equal(p, StagePhrases.Phrasing(Japanese, JapaneseGlue));
    }

    // Ported from StagePhrasesTests.swift: testRightToLeftKeepsReadingOrder
    [Fact]
    public void RightToLeftKeepsReadingOrder()
    {
        var (p, runs) = Phrasing(Even("אני אוהב אותך כל כך הרבה"));
        AssertWellFormed(p, runs);
        Assert.True(p.RightToLeft);
        Assert.Equal([[0, 3], [3, 6]], Spans(p)); // Logical order; the layout mirrors it
        var (arabic, arabicRuns) = Phrasing(Even("أحبك يا قلبي، تعال"));
        AssertWellFormed(arabic, arabicRuns);
        Assert.True(arabic.RightToLeft);
        Assert.Equal([[0, 3], [3, 4]], Spans(arabic)); // An Arabic comma ends a phrase
    }

    // Ported from StagePhrasesTests.swift: testLineTimedLinesArePhrasedByLengthAndPunctuationOnly
    [Fact]
    public void LineTimedLinesArePhrasedByLengthAndPunctuationOnly()
    {
        var words = "and we keep running through the city lights, until the morning comes".Split(' ')
            .Select(text => new LyricWord(text, 10, 14)).ToList();
        var (p, runs) = Phrasing(words);
        AssertWellFormed(p, runs);
        Assert.True(p.LineTimed);
        Assert.Equal(["and we keep running", "through the city lights,", "until the morning comes"], Texts(p, runs));
        Assert.Equal([StagePhraseBreak.LineStart, StagePhraseBreak.Length, StagePhraseBreak.Punctuation], p.Phrases.Select(phrase => phrase.BreakBefore));
        Assert.DoesNotContain(p.Words, word => word.IsHeld || word.RunGroup is not null);
        Assert.True(p.Phrases.All(phrase => phrase.Start == 10 && phrase.End == 14)); // All at once
        Assert.Equal("running", runs[p.Phrases[0].Focal].Text); // The longest word leads
        Assert.Equal(2, p.PhraseIndex(10)); // Every phrase is there from the start
        // Asked for explicitly, a timed line is treated the same way.
        var (forced, _) = Phrasing(Mixed, lineTimed: true);
        Assert.True(forced.LineTimed);
        Assert.Equal([[0, 4], [4, 8], [8, 12]], Spans(forced));
        Assert.DoesNotContain(forced.Words, word => word.IsHeld);
    }

    // Ported from StagePhrasesTests.swift: testWordsOutOfOrderStillGiveWellFormedPhrases
    [Fact]
    public void WordsOutOfOrderStillGiveWellFormedPhrases()
    {
        var (p, runs) = Phrasing([new("hello", 12, 13), new("world", 10, 11), new("again", 11, 12), new("tonight", 9, 9.5), new("forever", 13, 15)]);
        AssertWellFormed(p, runs);
        Assert.True(p.Words.All(word => word.GapBefore >= 0 && word.Duration >= 0));
        Assert.Equal(9, p.Phrases[0].Start, 1e-9);
        // A single word and an empty line.
        Assert.Equal(StagePhrasing.Empty, StagePhrases.Analyse([]));
        var (one, oneRuns) = Phrasing([new("rain", 1, 1.4)]);
        AssertWellFormed(one, oneRuns);
        Assert.Single(one.Phrases);
        Assert.False(one.LineTimed);
    }

    // Ported from StagePhrasesTests.swift: testWordsWithoutEndsFindPausesFromTheirOnsets
    [Fact]
    public void WordsWithoutEndsFindPausesFromTheirOnsets()
    {
        double[] starts = [1, 1.5, 2, 2.5, 4, 4.5];
        var words = "one two three four five six".Split(' ').Select((text, i) => new LyricWord(text, starts[i], starts[i])).ToList();
        var (p, runs) = Phrasing(words);
        AssertWellFormed(p, runs);
        Assert.Equal([[0, 4], [4, 6]], Spans(p));
        Assert.Equal(StagePhraseBreak.Gap, p.Phrases[1].BreakBefore);
    }

    // Ported from StagePhrasesTests.swift: testThePhrasingIsCachedPerLine
    [Fact]
    public void ThePhrasingIsCachedPerLine()
    {
        var runs = Runs(Mixed);
        var first = StagePhrases.Phrasing(runs);
        Assert.Equal(first, StagePhrases.Phrasing(Runs(Mixed)));
        Assert.Equal(StagePhrases.Analyse(runs), first);
        var other = Runs(Even("we keep dreaming under neon rain"));
        Assert.NotEqual(StagePhrases.Phrasing(other), first);
        // Different rules are a different entry.
        var rules = StagePhraseRules.Default with { MaxRuns = 2 };
        Assert.Equal(6, StagePhrases.Phrasing(runs, rules: rules).Phrases.Count);
        Assert.Equal(3, StagePhrases.Phrasing(runs).Phrases.Count);
    }
}
