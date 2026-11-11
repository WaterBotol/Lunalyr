using Lunalyr.Core.Lyrics;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>Names are compared without edition and credit qualifiers.</summary>
public sealed class LyricNameMatchingTests
{
    // Ported from LyricNameMatchingTests.swift: testEditionAndCreditQualifiersAreNormalisedBeforeComparing
    [Fact]
    public void EditionAndCreditQualifiersAreNormalisedBeforeComparing()
    {
        Assert.Equal(0.95, LyricQuery.Similarity("Song (2011 Remaster)", "Song - Remastered 2011"));
        Assert.Equal(0.95, LyricQuery.Similarity("Song (feat. Latto)", "Song (with Latto)"));
        Assert.Equal(0.95, LyricQuery.Similarity("Song ft. Latto", "Song"));
        Assert.Equal(0.95, LyricQuery.Similarity("Song – Live at Wembley", "Song [Radio Edit]"));
        Assert.Equal(0.95, LyricQuery.Similarity("Song (Mono)", "Song (Stereo Version)"));
        // Brackets that are part of the name stay part of it.
        Assert.Equal(LyricQuery.Normalize("(I Can't Get No) Satisfaction"), LyricQuery.Core("(I Can't Get No) Satisfaction"));
        Assert.Equal("part 1 the beginning", LyricQuery.Core("Part 1 - The Beginning"));
        Assert.Equal(1, LyricQuery.Similarity("Our Song", "Our Song"));
    }

    // Ported from LyricNameMatchingTests.swift: testContainmentNeedsWholeWordsExceptInUnspacedScripts
    [Fact]
    public void ContainmentNeedsWholeWordsExceptInUnspacedScripts()
    {
        Assert.True(LyricQuery.Similarity("Love", "Lovers") < 0.75);
        Assert.True(LyricQuery.Similarity("Pink", "Pinkfong") < 0.65);
        Assert.Equal(0.85, LyricQuery.Similarity("Coldplay", "Coldplay, BTS"));
        Assert.Equal(0.85, LyricQuery.Similarity("夜に駆ける", "夜に駆ける英語版"));
    }

    // Ported from LyricNameMatchingTests.swift: testNamesMadeOnlyOfSymbolsMatchAsWritten
    [Fact]
    public void NamesMadeOnlyOfSymbolsMatchAsWritten()
    {
        Assert.Equal(1, LyricQuery.Similarity("!!!", "!!!"));
        Assert.Equal(1, LyricQuery.Similarity("🦋", " 🦋 "));
        Assert.Equal(0, LyricQuery.Similarity("🦋", "🐝"));
        Assert.Equal(0, LyricQuery.Similarity("!!!", ""));
    }

    // Ported from LyricNameMatchingTests.swift: testRecordingKeysAreUnchangedSoSavedChoicesAndOffsetsStillMatch
    [Fact]
    public void RecordingKeysAreUnchangedSoSavedChoicesAndOffsetsStillMatch()
    {
        var key = new LyricQuery("Don’t Stop (2011 Remaster)", "Beyoncé", "Live", 221.4).RecordingKey;
        Assert.Equal("don t stop 2011 remaster|beyonce|live|221", key);
    }

    // Windows-only: the folding this port uses where Foundation's cannot be run. Pinned so a change is deliberate.
    [Fact]
    public void FoldingEdgeCasesArePinned()
    {
        Assert.Equal("strasse", LyricQuery.Normalize("Straße"));
        Assert.Equal("σοφια", LyricQuery.Normalize("ΣΟΦΙΑ"));
        // Kana voicing marks are non-spacing marks once decomposed, so they fold away like accents.
        Assert.Equal(LyricQuery.Normalize("カ"), LyricQuery.Normalize("ガ"));
        // Hangul decomposes into jamo, not marks, and recomposes unchanged.
        Assert.Equal("아이유", LyricQuery.Normalize("아이유"));
        // Supplementary-plane letters are letters, emoji are separators.
        Assert.Equal("𠮷野家 a", LyricQuery.Normalize("𠮷野家🦋a"));
        // Midpoints round away from zero, as Swift's rounded() does.
        Assert.EndsWith("|181", new LyricQuery("A", "B", null, 180.5).RecordingKey, StringComparison.Ordinal);
        Assert.Equal("3:01", LyricQuery.Clock(180.5));
    }

    [Fact]
    public void TitleSimilarityIsTheSimilarityOfTheTitleUntilAliasesExist()
    {
        var query = new LyricQuery("Our Song (Live)", "Our Artist", null, 180);
        Assert.Equal(LyricQuery.Similarity(query.Title, "Our Song"), query.TitleSimilarity("Our Song"));
        Assert.Equal([query.Title], query.MatchTitles());
    }
}
