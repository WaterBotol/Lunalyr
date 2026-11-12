using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>
/// Apple Music lists a Japanese song that also has an English recording under its English title ("Racing into the
/// Night" for 夜に駆ける), while the English recording says "English Version". Lyrics for the language version rank as
/// another version.
/// </summary>
public sealed class LanguageVersionTests
{
    private static void Tags(string name, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), VersionTags.Tags(name).Order(StringComparer.Ordinal));

    // Ported from LanguageVersionTests.swift: testLanguageVersionsAreTags
    [Fact]
    public void LanguageVersionsAreTags()
    {
        Tags("BABY (English Version)", "english");
        Tags("Idol (English ver.)", "english");
        Tags("Idol - Eng Ver.", "english");
        Tags("Idol (English)", "english");
        Tags("アイドル (英語ver.)", "english");
        Tags("Our Song (Japanese Ver.)", "japanese");
        Tags("Our Song [Korean Version]", "korean");
        Tags("Our Song (日本語ver.)", "japanese");
        Tags("Our Song (Live / English Version)", "live", "english");
        foreach (var name in new[] { "English Girl", "Our Song (Album Version)", "Our Song (JP)", "Our Song (English Rose Remaster)" })
        {
            Assert.False(VersionTags.Tags(name).Overlaps(VersionTags.Languages), name);
        }
        Assert.Equal("English", VersionTags.Describe(["english"]));
    }
}
