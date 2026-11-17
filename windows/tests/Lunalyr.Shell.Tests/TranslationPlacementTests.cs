using Lunalyr.Core.Compositions;
using Lunalyr.Core.Floating;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary>Ported from TranslationPlacementTests.swift (the Mac's 5783d638): where a line's translation sits.</summary>
public sealed class TranslationPlacementTests
{
    // Ported from TranslationPlacementTests.swift: testStoredPlacementAndDefault
    [Fact]
    public void StoredPlacementAndDefault()
    {
        Assert.Equal(TranslationPlacement.Below, new AppSettings().Style().TranslationPlacement);
        Assert.Equal(TranslationPlacement.Top, (new AppSettings { TranslationPlacement = "top" }).Style().TranslationPlacement);
        var nonsense = (new AppSettings { TranslationPlacement = "nonsense" }).Normalized();
        Assert.Equal("below", nonsense.TranslationPlacement);
        Assert.Equal(TranslationPlacement.Below, nonsense.Style().TranslationPlacement);
    }

    // Ported from TranslationPlacementTests.swift: testEdgesAndSides
    [Fact]
    public void EdgesAndSides()
    {
        Assert.Equal([TranslationPlacement.Bottom, TranslationPlacement.Top], TranslationPlacements.All.Where(p => p.AtEdge()));
        Assert.Equal([TranslationPlacement.Above, TranslationPlacement.Top], TranslationPlacements.All.Where(p => p.OverWords()));
        Assert.Equal(["Under the line", "Above the line", "Bottom of the screen", "Top of the screen"], TranslationPlacements.All.Select(p => p.Title()));
        Assert.Equal("Show translations", TranslationsCopy.Title);
    }

    // Ported from TranslationPlacementTests.swift: testThePlaceIsPartOfALyricStyle
    [Fact]
    public void ThePlaceIsPartOfALyricStyle()
    {
        Assert.Contains("translationPlacement", LyricStyleProfiles.Keys);
        // Each style keeps its own: the floating lyrics' place stays when the desktop's changes.
        var settings = new AppSettings { TranslationPlacement = "top" }.WithLyricsSurface(LyricsSurface.Floating);
        settings = (settings with { TranslationPlacement = "above" }).WithLyricsSurface(LyricsSurface.Desktop);
        Assert.Equal("top", settings.TranslationPlacement);
        Assert.Equal("above", settings.WithLyricsSurface(LyricsSurface.Floating).TranslationPlacement);
    }
}
