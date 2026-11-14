using Lunalyr.Core.Compositions;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>Icons for lyric words (the Mac's LyricWordIconsTests): the SF Symbol names the Mac picks, and Windows' glyph for each.</summary>
public class LyricWordIconsTests
{
    // Ported from LyricWordIconsTests.swift: testInflectionsCaseAndPunctuation
    [Fact]
    public void InflectionsCaseAndPunctuation()
    {
        foreach (var word in new[] { "love", "Love", "LOVES", "loved", "loving", "lovin'", "lover", "love,", "(love)" })
            Assert.Equal("heart.fill", LyricWordIcons.Symbol(word));
        foreach (var word in new[] { "home", "homes", "House!" }) Assert.Equal("house.fill", LyricWordIcons.Symbol(word));
        foreach (var word in new[] { "call", "calling", "called", "calls" }) Assert.Equal("phone.fill", LyricWordIcons.Symbol(word));
        Assert.Equal("moon.fill", LyricWordIcons.Symbol("nights"));
        Assert.Equal("figure.run", LyricWordIcons.Symbol("running"));
        Assert.Equal("drop.fill", LyricWordIcons.Symbol("cried"));
        Assert.Equal("figure.dance", LyricWordIcons.Symbol("dancing"));
        Assert.Equal("figure.stand.dress", LyricWordIcons.Symbol("her"));
        Assert.Equal("figure.stand", LyricWordIcons.Symbol("his"));
    }

    // Ported from LyricWordIconsTests.swift: testCJK
    [Fact]
    public void Cjk()
    {
        Assert.Equal("heart.fill", LyricWordIcons.Symbol("愛"));
        Assert.Equal("heart.fill", LyricWordIcons.Symbol("사랑"));
        Assert.Equal("house.fill", LyricWordIcons.Symbol("家"));
        Assert.Equal("house.fill", LyricWordIcons.Symbol("집"));
        Assert.Equal("moon.fill", LyricWordIcons.Symbol("夜"));
        Assert.Equal("moon.fill", LyricWordIcons.Symbol("밤"));
        Assert.Equal("cloud.rain.fill", LyricWordIcons.Symbol("雨"));
        Assert.Equal("cloud.rain.fill", LyricWordIcons.Symbol("비"));
        Assert.Equal("moon.fill", LyricWordIcons.Symbol("夜空に")); // 夜空に: the word with its particle
        Assert.Equal("moon.fill", LyricWordIcons.Symbol("밤에")); // 밤에
        Assert.Null(LyricWordIcons.Symbol("に")); // a particle alone
    }

    // Ported from LyricWordIconsTests.swift: testUnmatchedWordsHaveNone
    [Fact]
    public void UnmatchedWordsHaveNone()
    {
        foreach (var word in new[] { "you", "me", "the", "and", "seed", "shed", "willow", "", "...", "beautiful", "whatever" })
            Assert.Null(LyricWordIcons.Symbol(word));
    }

    // Ported from LyricWordIconsTests.swift: testEverySymbolResolves (on Windows: every SF Symbol name has a decided
    // Segoe Fluent Icons glyph, or is decided to have none; the font itself is checked in the render tests)
    [Fact]
    public void EverySymbolResolves()
    {
        var names = LyricWordIcons.Strong.Values.Concat(LyricWordIcons.Weak.Values).Concat(PortalGeometry.Ornaments).ToHashSet();
        Assert.True(LyricWordIcons.Strong.Count + LyricWordIcons.Weak.Count >= 80);
        foreach (var name in names) Assert.True(SegoeIcons.Glyphs.ContainsKey(name), $"{name} has no decision");
        // The ornaments are seven different glyphs, so "never the same as the three before" holds on screen too.
        var ornaments = PortalGeometry.Ornaments.Select(SegoeIcons.For).ToList();
        Assert.DoesNotContain(null, ornaments);
        Assert.Equal(ornaments.Count, ornaments.Distinct().Count());
        // The meanings the brief names keep their pictures.
        Assert.Equal("\uEB52", SegoeIcons.For(LyricWordIcons.Symbol("love")));
        Assert.Equal("\uEA8A", SegoeIcons.For(LyricWordIcons.Symbol("home")));
        Assert.Equal("\uE626", SegoeIcons.For(LyricWordIcons.Symbol("call")));
        Assert.Equal("\uF1DB", SegoeIcons.For(LyricWordIcons.Symbol("night")));
        Assert.Equal("\uEA91", SegoeIcons.For(LyricWordIcons.Symbol("rain")));
    }
}
