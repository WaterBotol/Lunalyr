using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// Ported from InstalledFontsTests.swift (the Mac's 7fa2f68, 86af9d7, eda0f37): which families Typeface › Installed font
/// offers, what "Find new fonts" counts, and that a family uninstalled since falls back to the default typeface. The PC's
/// own fonts are only read, never changed.
/// </summary>
public sealed class InstalledFontsTests
{
    // Ported from InstalledFontsTests.swift: testHiddenUnusableAndRepeatedFamiliesAreLeftOut
    [Fact]
    public void HiddenUnusableAndRepeatedFamiliesAreLeftOut()
    {
        string[] raw = ["Zeta Sans", ".SF NS Rounded", "alpha Serif", "Wingdings", "Zeta Sans", "", "Beta Mono", "@MS Gothic"];
        var usable = InstalledFontLibrary.UsableFamilies(raw, family => family != "Wingdings");
        Assert.Equal(["alpha Serif", "Beta Mono", "Zeta Sans"], usable);
    }

    // Ported from InstalledFontsTests.swift: testSymbolAndDingbatFontsCantSetLyrics, testFontsForOtherScriptsAreListedButSymbolFontsAreNot
    [Fact]
    public void SymbolFontsCantSetLyricsButOtherScriptsCan()
    {
        var real = InstalledFonts.Families();
        Assert.Contains("Segoe UI", real);
        foreach (var symbol in new[] { "Wingdings", "Webdings", "Symbol", "Segoe MDL2 Assets" })
            Assert.DoesNotContain(symbol, real);
        Assert.DoesNotContain(real, family => family.StartsWith('@') || family.StartsWith('.'));
        // Faces for other scripts' lyrics: Arabic, Japanese, Korean (each shipped with Windows).
        foreach (var script in new[] { "Arial", "Yu Gothic", "Malgun Gothic" })
            if (InstalledFonts.IsInstalled(script)) Assert.Contains(script, real);
        Assert.False(InstalledFonts.IsInstalled("No Such Family 4821"));
        Assert.True(InstalledFonts.IsInstalled("segoe ui")); // any case
    }

    // Ported from InstalledFontsTests.swift: testSearchMatchesPartOfTheNameIgnoringCaseAndAccents
    [Fact]
    public void SearchMatchesPartOfTheNameIgnoringCaseAndAccents()
    {
        string[] families = ["Avenir Next", "Helvetica Neue", "Didot", "Café Sans"];
        Assert.Equal(["Helvetica Neue"], InstalledFontLibrary.Filter(families, "neue"));
        Assert.Equal(["Café Sans"], InstalledFontLibrary.Filter(families, "cafe"));
        Assert.Equal(families, InstalledFontLibrary.Filter(families, "  "));
    }

    // Ported from InstalledFontsTests.swift: testFindNewFontsCountsOnlyFamiliesAddedSinceTheLastReading
    [Fact]
    public void FindNewFontsCountsOnlyFamiliesAddedSinceTheLastReading()
    {
        var installed = new List<string> { "Avenir Next", "Didot" };
        var library = new InstalledFontLibrary(_ => [.. installed]);
        Assert.Equal(0, library.Refresh()); // the first reading finds nothing new
        Assert.Equal(["Avenir Next", "Didot"], library.Families);

        installed.AddRange(["Mona Sans", "Hubot Sans"]);
        installed.Remove("Didot");
        Assert.Equal(2, library.Refresh());
        Assert.Equal(0, library.Refresh());

        // A font a reading already picked up still counts when the button is pressed afterwards.
        Assert.Equal(2, library.FindNewFonts());
        installed.Add("Inter");
        library.Refresh();
        Assert.Equal(1, library.FindNewFonts());
        Assert.Equal(0, library.FindNewFonts());

        Assert.Equal(["C", "D"], InstalledFontLibrary.NewFamilies(["A", "B"], ["B", "C", "D"]));
        Assert.Equal("No new fonts found.", InstalledFontLibrary.FoundMessage(0));
        Assert.Equal("Found 1 new family.", InstalledFontLibrary.FoundMessage(1));
        Assert.Equal("Found 3 new families.", InstalledFontLibrary.FoundMessage(3));
    }

    // Ported from InstalledFontsTests.swift: testTheChosenInstalledFamilyIsKeptAndDrawn, testAnUninstalledFamilyFallsBackToTheDefaultTypeface
    [Fact]
    public void AnUninstalledFamilyFallsBackToTheDefaultTypeface()
    {
        var chosen = new AppSettings { FontChoice = "installed", FontFamily = "Segoe UI" };
        Assert.False(chosen.InstalledFontMissing);
        Assert.Equal("Segoe UI", chosen.Style().FontFamily);
        var gone = new AppSettings { FontChoice = "installed", FontFamily = "No Such Family 4821" };
        Assert.True(gone.InstalledFontMissing);
        Assert.Equal(Lunalyr.Render.Scene.LyricStyle.Default.FontFamily, gone.Style().FontFamily); // drawn in the default
        Assert.Equal("No Such Family 4821", gone.Normalized().FontFamily); // kept, for when it comes back
        // Not chosen: never missing.
        Assert.False((gone with { FontChoice = "system" }).InstalledFontMissing);
    }
}
