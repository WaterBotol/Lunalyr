using System.Text.Json;
using Lunalyr.Core.Floating;
using Lunalyr.Core.Logging;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// What a new install starts with (the Mac's ShippedDefaultsTests, 2fe4dd7b): the owner's look for a new install, and
/// an existing install's look kept.
/// </summary>
public sealed class ShippedDefaultsTests
{
    [Fact]
    public async Task ANewInstallStartsWithTheOwnersLook()
    {
        using var temp = new TempFolder();
        await using var store = AppSettingsStore.Open(temp.Path, Logger.Null);
        var s = store.Current;
        Assert.Equal("visualizer", s.BackgroundMode);
        Assert.Equal("starTrails", s.VisualizerStyle);
        Assert.Equal("hexGrid", s.PatternStyle);
        Assert.Equal("lightTrails", s.ShaderStyle);
        Assert.Equal(0.77, s.ScrimStrength);
        Assert.Equal("fit", s.MotionPlacement);
        Assert.False(s.UseMotionArtwork);
        Assert.Equal("original", s.WavefieldColours);
        Assert.Equal(2, s.VisualizerPunch);
        Assert.True(s.AudioReactiveEnabled);
        Assert.Equal(3, s.AudioSensitivity);
        Assert.Equal("trackName", s.NoLyrics);
        Assert.Equal("colourOnly", s.OnBattery);
        Assert.False(s.TextBackingAutomatic);
        Assert.Equal(0, s.TextBacking);
        Assert.Equal(0, s.FloatingBackdrop);
        Assert.Equal(93, s.FloatingTextSize);
        Assert.True(s.OpeningTitleAlways);
        Assert.Equal(1, s.OpeningTitleStrength);
        Assert.Equal(0.893, s.OpeningTitleScale, 3);
        Assert.Equal(0.0332, s.OpeningTitleLeft!.Value, 4);
        Assert.Equal(0.0450, s.OpeningTitleLift!.Value, 4);
        Assert.Equal("landscape", s.LyricCardAspect);
        Assert.Equal(["gif"], s.LyricCardFormats);
        Assert.Equal(1.1, s.LyricCardTextScale);
        Assert.Equal("fable", s.LyricCardComposition);
        Assert.Equal(1, s.LyricCardLinesBefore);
        Assert.Equal(1, s.LyricCardLinesAfter);
        Assert.Equal(2, s.RotateLayoutCounts["spoken"]);
        Assert.Equal(0, s.RotateLayoutCounts["echo"]);
    }

    [Fact]
    public void TheDesktopStyleIsTheOwners()
    {
        var s = ShippedDefaults.ForNewInstall();
        // Windows' system face plays SF Pro's part: no download, already the default.
        Assert.Equal("system", s.FontChoice);
        Assert.Equal("automatic", s.LyricComposition);
        Assert.Equal(1.5, s.LyricScale);
        Assert.Equal(0.7, s.ConstellationSpread, 6);
        Assert.Equal(0, s.ConstellationLinkOpacity);
        Assert.Equal(1.8, s.EchoStrength);
        Assert.Equal("composing", s.ThinkingOrbStyle);
        Assert.Equal("scattered", s.BackingVocalStyle);
        Assert.Equal("sungSweep", s.LyricColourMode);
        Assert.Equal(0.826, s.LyricColourSpeed, 3);
        Assert.Equal(2, s.LyricColourStrength);
        Assert.False(s.LyricColourPastel);
        Assert.Equal("album", s.LyricHighlightMode);
        // Stored in the mix's own order.
        Assert.Equal(["cutoutHighlight", "fade", "lift", "orbit"], s.WordEntranceMix.Order());
        Assert.Equal("glide", s.WordArrivalByStyle["cascade"]);
        Assert.True(s.WordRevealByStyle["constellation"]);
        Assert.True(s.NextWordPreviewByStyle["cutout"]);
    }

    [Fact]
    public void TheFloatingStyleIsTheOwnersHinge()
    {
        var floating = ShippedDefaults.ForNewInstall().WithLyricsSurface(LyricsSurface.Floating);
        Assert.Equal("hinge", floating.LyricComposition);
        Assert.Equal(1.0, floating.LyricScale);
        Assert.Equal("listening", floating.ThinkingOrbStyle);
        Assert.Equal(2.0, floating.LyricColourSpeed);
        Assert.True(floating.LyricColourPastel);
        Assert.True(floating.EditorialTypography);
        Assert.Equal(0.6, floating.ConstellationLinkOpacity);
        Assert.Equal("mix", floating.WordArrivalByStyle["spoken"]);
        // Switching back finds the Desktop style as it was.
        Assert.Equal("automatic", floating.WithLyricsSurface(LyricsSurface.Desktop).LyricComposition);
    }

    [Fact]
    public void NothingPrivateIsOnInANewInstall()
    {
        var s = ShippedDefaults.ForNewInstall();
        Assert.False(s.DiscordPresence);
        Assert.False(s.MusicVideoOnline);
        Assert.False(s.TrayLyric);
    }

    [Fact]
    public async Task AnExistingInstallKeepsItsLook()
    {
        using var temp = new TempFolder();
        // A file from before, missing some keys (as one written before they existed would be).
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "settings.json"),
            """{ "schemaVersion": 1, "lyricComposition": "spoken", "onboardingDone": true }""", TestContext.Current.CancellationToken);
        await using var store = AppSettingsStore.Open(temp.Path, Logger.Null);
        var s = store.Current;
        Assert.Equal("spoken", s.LyricComposition);
        // Keys it never stored read the defaults it had.
        Assert.Equal("auto", s.BackgroundMode);
        Assert.Equal(44, s.FloatingTextSize);
        Assert.Equal("albumDrift", s.LyricColourMode);
        Assert.Null(s.LyricStyleFloating);
    }

    [Fact]
    public void EveryNewDefaultSurvivesNormalising()
    {
        // A value the settings would reset as unknown would quietly undo the owner's look.
        var old = JsonSerializer.SerializeToNode(new AppSettings(), AppSettingsJsonContext.Default.AppSettings)!.AsObject();
        var raw = JsonSerializer.SerializeToNode(ShippedDefaults.NewInstall(), AppSettingsJsonContext.Default.AppSettings)!.AsObject();
        var normalised = JsonSerializer.SerializeToNode(ShippedDefaults.ForNewInstall(), AppSettingsJsonContext.Default.AppSettings)!.AsObject();
        var changed = raw.Where(p => !System.Text.Json.Nodes.JsonNode.DeepEquals(p.Value, old[p.Key])).Select(p => p.Key).ToList();
        Assert.True(changed.Count > 40, $"only {changed.Count} keys changed");
        // The entrance mix is kept in its own order, and the per-layout maps keep only the layouts Windows offers a choice for.
        string[] reordered = ["wordEntranceMix", "wordRevealByStyle", "nextWordPreviewByStyle", "wordArrivalByStyle"];
        foreach (var key in changed.Where(k => !reordered.Contains(k)))
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(raw[key], normalised[key]), $"{key} was reset by Normalized(): {raw[key]?.ToJsonString()} -> {normalised[key]?.ToJsonString()}");
        Assert.True((bool)normalised["wordRevealByStyle"]!["constellation"]!);
        Assert.True((bool)normalised["nextWordPreviewByStyle"]!["cutout"]!);
        Assert.Equal("glide", (string?)normalised["wordArrivalByStyle"]!["cascade"]);
    }

    [Fact]
    public void TheOldDefaultsStepFillsOnlyMissingKeys()
    {
        var file = new System.Text.Json.Nodes.JsonObject { ["lyricComposition"] = "spoken", ["floatingTextSize"] = 60.0 };
        ShippedDefaults.KeepOldDefaults.Apply(file);
        Assert.Equal("spoken", (string?)file["lyricComposition"]);
        Assert.Equal(60.0, (double?)file["floatingTextSize"]);
        Assert.Equal("auto", (string?)file["backgroundMode"]);
    }
}
