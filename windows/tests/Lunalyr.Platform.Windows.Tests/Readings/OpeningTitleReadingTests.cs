using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics.Readings;

namespace Lunalyr.Platform.Windows.Tests.Readings;

/// <summary>The opening title's reading of a song title, with the Windows readings.</summary>
public class OpeningTitleReadingTests
{
    // Ported from OpeningTitleTests.swift: testAJapaneseTitleGetsItsReadingWhenReadingsShow
    [Fact]
    public void AJapaneseTitleGetsItsReadingWhenReadingsShow()
    {
        var memory = new OpeningTitleMemory();
        var romanizer = new LyricRomanizer(TestReadingServices.Windows);
        var below = new RomanizationOptions(RomanizationDisplay.Below, RomanizationLanguage.Automatic, ToneMarks: false, PreferSaved: true);
        var reading = memory.Reading("夜に駆ける", below, romanizer);
        Assert.Equal("yorunikakeru", reading?.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal));
        Assert.Null(memory.Reading("Human Nature", below, romanizer));
        var off = below with { Display = RomanizationDisplay.Off };
        Assert.Null(memory.Reading("夜に駆ける", off, romanizer));
        Assert.Equal("Yoru ni Kakeru", OpeningTitleMemory.Capitalised("yoru ni kakeru"));
    }
}
