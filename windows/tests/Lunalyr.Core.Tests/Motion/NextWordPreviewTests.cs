using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Lyrics.Timing.TimingTestLines;

namespace Lunalyr.Core.Tests.Motion;

public class NextWordPreviewTests
{
    // Ported from NextWordPreviewTests.swift: testEveryStyleCanPreviewExactlyOneFutureWord (the style loop becomes the
    // options a style passes; the Windows port has no layouts yet)
    [Fact]
    public void AStyleCanPreviewExactlyOneFutureWord()
    {
        var options = new WordRevealOptions(RevealsWords: true, PreviewsNextWord: true, PreviewLead: 0.5);
        var next = WordPresentation.Make(new LyricWord("next", 2, 3), 1, 1, 1.75, options);
        var later = WordPresentation.Make(new LyricWord("later", 3, 4), 2, 1, 1.75, options);
        Assert.Equal(0.125, next.Opacity, 0.001);
        Assert.True(next.Preview);
        Assert.Equal(0, later.Opacity, 0.001);
    }

    // Ported from NextWordPreviewTests.swift: testEveryStyleCanHideThePreview
    [Fact]
    public void AStyleCanHideThePreview()
    {
        var options = new WordRevealOptions(RevealsWords: true, PreviewsNextWord: false, PreviewLead: 0.5);
        var next = WordPresentation.Make(new LyricWord("next", 2, 3), 1, 1, 1.75, options);
        Assert.Equal(0, next.Opacity, 0.001);
    }

    // Ported from NextWordPreviewTests.swift: testPreviewUsesTheNextOnsetEvenAcrossALongGap
    [Fact]
    public void PreviewUsesTheNextOnsetEvenAcrossALongGap()
    {
        Assert.Equal(0, WordReveal.Visibility(1, 10, 11, true, true, 0.5));
        Assert.Equal(0.125, WordReveal.Visibility(9.75, 10, 11, true, true, 0.5), 0.001);
        Assert.Equal(0.25, WordReveal.Visibility(9.99, 10, 11, true, true, 0.5), 0.001);
        Assert.Equal(0.25, WordReveal.Visibility(10, 10, 11, true, false, 0.5), 0.001);
    }

    // Ported from NextWordPreviewTests.swift: testFirstWordOfNextLineUsesRealOnsetOrLineTimestamp
    [Fact]
    public void FirstWordOfNextLineUsesRealOnsetOrLineTimestamp()
    {
        var next = WordTimed(10, 12, "… first word",
            new LyricWord("…", 10, 10.05), new LyricWord("first", 10.2, 11), new LyricWord("word", 11, 12));
        Assert.Equal(10.2, LyricPhraseTiming.FirstWordOnset(next));
        Assert.Equal(10, LyricPhraseTiming.FirstWordOnset(next with { WordTimed = false }));
    }

    // Ported from NextWordPreviewTests.swift: testTimedWordIdentitySurvivesMovingBetweenLineWindows
    [Fact]
    public void TimedWordIdentitySurvivesMovingBetweenLineWindows()
    {
        var original = new TimedWordKey(new LyricWord("light", 8.2, 8.8));
        var moved = new TimedWordKey(new LyricWord("light", 8.2, 8.8));
        Assert.Equal(original, moved);
        Assert.Equal(original.VisualSeed, moved.VisualSeed);
    }

    [Fact]
    public void ThePreviewHandsOverWithoutABlinkAndRampsInFromWhenItWasAllowed()
    {
        var options = new WordRevealOptions(PreviewLead: 0.5);
        var word = new LyricWord("light", 1, 2);
        // At the onset the preview's quarter carries on into the word's own reveal.
        Assert.Equal(0.25, WordPresentation.Make(word, 0, 0, 1, options).Opacity, 1e-9);
        // Allowed only 40 ms ago: the preview is still ramping in, not part-way up at once.
        var ramping = WordPresentation.Make(word, 0, 0, 0.9, options, eligibleSince: 0.86);
        Assert.True(ramping.Opacity < WordPresentation.Make(word, 0, 0, 0.9, options).Opacity);
        // Words shown whole when the style doesn't reveal them.
        Assert.Equal(WordPresentation.Whole, WordPresentation.Make(word, 0, 0, 0, options with { RevealsWords = false }));
        // A phrase sung as one piece, or punctuation alone, is never previewed.
        Assert.True(PreviewWords.IsPreviewWord("don't"));
        Assert.False(PreviewWords.IsPreviewWord("hold on"));
        Assert.False(PreviewWords.IsPreviewWord("…"));
        Assert.Equal(0, WordPresentation.Make(new LyricWord("hold on", 1, 2), 0, 0, 0.9, options).Opacity);
    }
}
