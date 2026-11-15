using Lunalyr.Render.Scene;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary>The settings behind the wallpaper extras: credits, interludes, highlight colours and Custom Mix.</summary>
public class ExtrasSettingsTests
{
    [Fact]
    public void TheDefaultsAreTheMacs()
    {
        var settings = new AppSettings();
        Assert.True(settings.CreditsLayer);
        Assert.True(settings.ShowInterludes);
        Assert.Equal("complement", settings.LyricHighlightMode);
        Assert.Equal("#FF92A3", settings.LyricHighlightCustom);
        Assert.True(settings.RotateLayouts);
        Assert.Empty(settings.RotateLayoutCounts);
        var style = settings.Style();
        Assert.True(style.Credits);
        Assert.Equal("complement", style.HighlightMode);
        // The contract's own default leaves credits off, so scenes made without the setting draw as before.
        Assert.False(LyricStyle.Default.Credits);
    }

    [Fact]
    public void UnknownOrOutOfRangeValuesAreRepaired()
    {
        var repaired = new AppSettings
        {
            LyricHighlightMode = "neon",
            LyricHighlightCustom = "pink",
            RotateLayoutCounts = new Dictionary<string, int> { ["echo"] = 9, ["cutout"] = -2 },
            RotateLayoutsCursor = -4,
            LyricComposition = "automatic",
        }.Normalized();
        Assert.Equal("complement", repaired.LyricHighlightMode);
        Assert.Equal("#FF92A3", repaired.LyricHighlightCustom);
        Assert.Equal(5, repaired.RotateLayoutCounts["echo"]);
        Assert.Equal(0, repaired.RotateLayoutCounts["cutout"]);
        Assert.Equal(0, repaired.RotateLayoutsCursor);
        Assert.Equal("automatic", repaired.LyricComposition); // Custom Mix is a choice of its own
        Assert.Equal("#2BD96B", new AppSettings { LyricHighlightCustom = " #2bd96b " }.Normalized().LyricHighlightCustom);
    }

    // Ported from OpeningTitleTests.swift: testTheCoverIsOnByDefaultAndOnlyWithArtwork (the setting's half; the
    // artwork half is OpeningTitleTests.TheCoverIsDrawnOnlyWithArtwork in Lunalyr.Core.Tests)
    [Fact]
    public void TheOpeningTitleSettingsAreOnByDefault()
    {
        var settings = new AppSettings();
        Assert.True(settings.OpeningTitle);
        Assert.True(settings.OpeningTitleCover);
        Assert.True(settings.Style().OpeningTitle);
        Assert.True(settings.Style().OpeningTitleCover);
        var off = settings with { OpeningTitleCover = false };
        Assert.False(off.Style().OpeningTitleCover);
        Assert.False((settings with { OpeningTitle = false }).Style().OpeningTitle);
        // The contract's own default leaves the card off, so scenes made without the setting draw as before.
        Assert.False(LyricStyle.Default.OpeningTitle);
    }

    [Fact]
    public void TheOpeningTitleSettingsKeepTheirKeysInTheFile()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { OpeningTitleCover = false }, AppSettingsJsonContext.Default.AppSettings);
        Assert.Contains("\"openingTitle\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"openingTitleCover\": false", json, StringComparison.Ordinal);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!.OpeningTitleCover);
        // (A file written before the settings existed reads them as on: the store lays every file over the defaults.)
    }
}
