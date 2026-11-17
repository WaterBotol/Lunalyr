using System.Text.Json;
using Lunalyr.Core.Floating;
using Lunalyr.Core.Logging;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// Floating lyrics' settings and the two separate lyric styles: ported from the Mac's FloatingLyricsTests.swift (its
/// settings half) and OpeningTitleTests.swift. Settings live in memory or in temp folders only.
/// </summary>
public class FloatingSettingsTests
{
    private static AppSettings Switch(AppSettings s, LyricsSurface surface) => s.WithLyricsSurface(surface);

    // A relaunch: the settings as written to settings.json and read back.
    private static AppSettings Relaunched(AppSettings s) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(s, AppSettingsJsonContext.Default.AppSettings),
            AppSettingsJsonContext.Default.AppSettings)!.Normalized();

    // ---- Settings ----

    // Ported from FloatingLyricsTests.swift: testDefaultsLeaveTheDesktopAsItWas
    [Fact]
    public void DefaultsLeaveTheDesktopAsItWas()
    {
        var options = new AppSettings();
        Assert.Equal(LyricsSurface.Desktop, options.Surface); // off by default: existing users see no change
        Assert.False(options.IsFloating);
        Assert.Equal(44, options.FloatingTextSize); // Medium
        Assert.Equal(FloatingPlacement.Bottom, options.Placement);
        Assert.True(options.FloatingFadeUnderPointer);
        Assert.Equal("", options.FloatingDisplay);
    }

    // Ported from FloatingLyricsTests.swift: testStoredValuesAreReadBackAndClamped
    [Fact]
    public async Task StoredValuesAreReadBackAndClamped()
    {
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "schemaVersion": 1, "lyricsSurface": "floating", "floatingTextSize": 999, "floatingBackdrop": -3,
              "floatingPlacement": "topRight", "floatingDisplay": "nonsense" }
            """);
        await using (var store = AppSettingsStore.Open(temp.Path, Logger.Null))
        {
            var options = store.Current;
            Assert.Equal(LyricsSurface.Floating, options.Surface);
            Assert.Equal(AppSettings.FloatingTextSizeMax, options.FloatingTextSize);
            Assert.Equal(0, options.FloatingBackdrop);
            Assert.Equal(FloatingPlacement.TopRight, options.Placement);
        }
        Assert.Equal(AppSettings.DefaultFloatingTextSize, AppSettings.ClampedFloatingTextSize(double.NaN));
        Assert.Equal(FloatingPlacement.Bottom, (new AppSettings { FloatingPlacement = "sideways" }).Normalized().Placement);
    }

    // Ported from FloatingLyricsTests.swift: testCustomFramesAreKeptPerDisplayAndResetClears
    [Fact]
    public void CustomFramesAreKeptPerDisplayAndResetClears()
    {
        var options = new AppSettings().WithCustomFrame(new FloatingRect(40, 60, 500, 160), "A");
        Assert.Equal(FloatingPlacement.Custom, options.PlacementFor("A")); // dragging the window makes its display Custom
        Assert.Equal(FloatingPlacement.Bottom, options.PlacementFor("B")); // another display keeps its place
        Assert.Equal(FloatingPlacement.Bottom, options.Placement);
        Assert.Equal(new FloatingRect(40, 60, 500, 160), options.CustomFrame("A"));
        Assert.Equal(new Core.Compositions.SizeD(500, 160), options.SavedSize("A"));
        Assert.Null(options.CustomFrame("B")); // another display keeps its own
        options = options.WithFloatingFramesReset();
        Assert.Null(options.CustomFrame("A"));
        Assert.Null(options.SavedSize("A"));
        Assert.Equal(FloatingPlacement.Bottom, options.Placement);
        Assert.Equal(FloatingPlacement.Bottom, options.PlacementFor("A"));
    }

    // Ported from FloatingLyricsTests.swift: testPickingAPlaceAppliesToEveryDisplay
    [Fact]
    public void PickingAPlaceAppliesToEveryDisplay()
    {
        var options = new AppSettings().WithCustomFrame(new FloatingRect(40, 60, 500, 160), "A").WithFloatingPlacement(FloatingPlacement.TopRight);
        Assert.Equal(FloatingPlacement.TopRight, options.PlacementFor("A"));
        Assert.Equal(FloatingPlacement.TopRight, options.PlacementFor("B"));
        Assert.Equal(new FloatingRect(40, 60, 500, 160), options.CustomFrame("A")); // the frame placed by hand is kept for Custom
    }

    // ---- Desktop or floating, never both ----

    // Ported from FloatingLyricsTests.swift: testOnlyOneSurfaceDrawsTheLyrics (the wallpaper's half; the window's is in
    // Lunalyr.Core.Tests)
    [Fact]
    public void OnlyOneSurfaceDrawsTheLyrics()
    {
        Assert.True(LyricsPlacement.DrawsLyrics(settingsPreview: false, LyricsSurface.Desktop));
        Assert.False(LyricsPlacement.DrawsLyrics(settingsPreview: false, LyricsSurface.Floating)); // the wallpaper draws no lyrics while they float
        Assert.True(LyricsPlacement.DrawsLyrics(settingsPreview: true, LyricsSurface.Floating)); // the settings preview shows the style being edited
    }

    // Ported from FloatingLyricsTests.swift: testSwitchingSurfaceIsStoredAndToggles
    [Fact]
    public void SwitchingSurfaceIsStoredAndToggles()
    {
        var s = new AppSettings().ToggleSurface();
        Assert.Equal("floating", s.LyricsSurface);
        Assert.Equal(LyricsSurface.Floating, Relaunched(s).Surface);
        s = s.ToggleSurface();
        Assert.Equal(LyricsSurface.Desktop, s.Surface);
    }

    // ---- Two separate styles ----

    // Ported from FloatingLyricsTests.swift: testFloatingStyleIsSeededOnceFromTheDesktop (the Mac's serif font is
    // Windows' installed family; its Static white, now read as Album drift, is Album drift here)
    [Fact]
    public void FloatingStyleIsSeededOnceFromTheDesktop()
    {
        var s = new AppSettings
        {
            LyricComposition = "echo", LyricScale = 1.3, FontChoice = "installed", FontFamily = "Georgia", LyricHighlightMode = "album",
            LyricColourMode = "sungSweep",
        };
        s = Switch(s, LyricsSurface.Floating);
        Assert.Equal("echo", s.LyricComposition); // Floating starts from the desktop's style
        Assert.Equal("installed", s.FontChoice);
        Assert.Equal("Georgia", s.FontFamily);
        Assert.Equal("album", s.LyricHighlightMode);
        Assert.Equal("sungSweep", s.LyricColourMode);
        Assert.Equal(1, s.LyricScale); // Floating's size is its own text size

        // Change the desktop's style: the floating one, already seeded, keeps its own.
        s = Switch(s, LyricsSurface.Desktop) with { LyricComposition = "portal", LyricHighlightMode = "complement", LyricColourMode = "albumDrift" };
        s = Switch(s, LyricsSurface.Floating);
        Assert.Equal("echo", s.LyricComposition); // seeding happens once
        Assert.Equal("album", s.LyricHighlightMode);
        Assert.Equal("sungSweep", s.LyricColourMode);
    }

    // Ported from FloatingLyricsTests.swift: testChangingFloatingNeverChangesTheDesktop (the backing switch the desktop
    // never set reads as its default, on: Windows writes every setting)
    [Fact]
    public void ChangingFloatingNeverChangesTheDesktop()
    {
        var s = new AppSettings { LyricComposition = "constellation", LyricScale = 1.2, ShowInterludes = true };
        s = Switch(s, LyricsSurface.Floating) with
        {
            LyricComposition = "scattered", LyricScale = 0.8, ShowInterludes = false,
            WordArrivalByStyle = new Dictionary<string, string> { ["scattered"] = "pop" }, LyricHighlightMode = "custom",
            LyricColourMode = "customGradient", BackingVocalsLayer = false,
        };

        s = Switch(s, LyricsSurface.Desktop);
        Assert.Equal("constellation", s.LyricComposition);
        Assert.Equal(1.2, s.LyricScale);
        Assert.True(s.ShowInterludes);
        Assert.Empty(s.WordArrivalByStyle);
        Assert.Equal("complement", s.LyricHighlightMode);
        Assert.Equal("albumDrift", s.LyricColourMode);
        Assert.True(s.BackingVocalsLayer);

        // And the floating style is still there when it comes back.
        s = Switch(s, LyricsSurface.Floating);
        Assert.Equal("scattered", s.LyricComposition);
        Assert.Equal(0.8, s.LyricScale);
        Assert.False(s.ShowInterludes);
        Assert.Equal("pop", s.WordArrivalByStyle["scattered"]);
        Assert.Equal("custom", s.LyricHighlightMode);
        Assert.Equal("customGradient", s.LyricColourMode);
        Assert.False(s.BackingVocalsLayer);
    }

    // Ported from FloatingLyricsTests.swift: testChangingTheDesktopNeverChangesFloating
    [Fact]
    public void ChangingTheDesktopNeverChangesFloating()
    {
        var s = Switch(new AppSettings(), LyricsSurface.Floating) with { LyricComposition = "cascade" };
        s = Switch(s, LyricsSurface.Desktop) with { LyricComposition = "hinge", EchoStrength = 0.4 };
        s = Switch(s, LyricsSurface.Floating);
        Assert.Equal("cascade", s.LyricComposition);
        Assert.Equal(1.0, s.EchoStrength);
        s = Switch(s, LyricsSurface.Desktop);
        Assert.Equal("hinge", s.LyricComposition);
        Assert.Equal(0.4, s.EchoStrength);
    }

    // Ported from FloatingLyricsTests.swift: testStylesSurviveARelaunch
    [Fact]
    public void StylesSurviveARelaunch()
    {
        var s = new AppSettings { LyricComposition = "mosaic" };
        s = Switch(s, LyricsSurface.Floating) with { LyricComposition = "torque" };
        // A relaunch reads the style in use from the ordinary keys.
        var settings = Relaunched(s);
        Assert.Equal("torque", settings.LyricComposition);
        Assert.Equal(LyricsSurface.Floating, settings.Surface);
        Assert.Equal("mosaic", Switch(settings, LyricsSurface.Desktop).LyricComposition);
    }

    // Ported from FloatingLyricsTests.swift: testProfileSwapIsANoOpForTheSameSurface
    [Fact]
    public void ProfileSwapIsANoOpForTheSameSurface()
    {
        var s = new AppSettings { LyricComposition = "echo" };
        var (same, seeded) = LyricStyleProfiles.Switch(s, LyricsSurface.Desktop);
        Assert.False(seeded);
        Assert.Null(LyricStyleProfiles.Saved(same, LyricsSurface.Desktop));
        (s, seeded) = LyricStyleProfiles.Switch(s, LyricsSurface.Floating);
        Assert.True(seeded); // the first switch seeds
        (s, seeded) = LyricStyleProfiles.Switch(s, LyricsSurface.Desktop);
        Assert.False(seeded);
        (_, seeded) = LyricStyleProfiles.Switch(s, LyricsSurface.Floating);
        Assert.False(seeded); // and only the first
    }

    // Ported from FloatingLyricsTests.swift: testFloatingOnlySettingsAreNotPartOfAStyle
    [Fact]
    public void FloatingOnlySettingsAreNotPartOfAStyle()
    {
        foreach (var key in new[] { "lyricsSurface", "floatingTextSize", "floatingPlacement", "backgroundMode", "lyricOffset" })
            Assert.DoesNotContain(key, LyricStyleProfiles.Keys);
        Assert.Equal(LyricStyleProfiles.Keys.Count, LyricStyleProfiles.Keys.Distinct().Count()); // no key twice
    }

    // Ported from OpeningTitleTests.swift: testTheSettingIsPartOfEachSurfacesStyle
    [Fact]
    public void TheSettingIsPartOfEachSurfacesStyle()
    {
        Assert.Contains("openingTitle", LyricStyleProfiles.Keys);
        Assert.Contains("openingTitleCover", LyricStyleProfiles.Keys);
    }

    [Fact]
    public void EveryStyleKeyIsASettingsKey()
    {
        using var json = JsonSerializer.SerializeToDocument(new AppSettings(), AppSettingsJsonContext.Default.AppSettings);
        foreach (var key in LyricStyleProfiles.Keys) Assert.True(json.RootElement.TryGetProperty(key, out _), key);
    }

    [Fact]
    public void TheOpeningTitleFollowsEachSurface()
    {
        var s = Switch(new AppSettings(), LyricsSurface.Floating) with { OpeningTitle = false };
        s = Switch(s, LyricsSurface.Desktop);
        Assert.True(s.OpeningTitle);
        Assert.False(Switch(s, LyricsSurface.Floating).OpeningTitle);
    }

    [Fact]
    public void BrokenFramesAreDroppedOnLoad()
    {
        var s = new AppSettings
        {
            FloatingFrames = new Dictionary<string, IReadOnlyList<double>> { ["A"] = [1, 2, 3, 4], ["B"] = [1, 2, 0, 4], ["C"] = [1, 2] },
        }.Normalized();
        Assert.Equal(["A"], s.FloatingFrames.Keys);
    }

    // Ported from FloatingLyricsSection.swift's LyricSurfaceSection.caption and LyricStyleEditingNote (the Mac has no
    // test of its own for them): the words say which style the page edits.
    [Fact]
    public void TheCaptionsSayWhichStyleIsEdited()
    {
        Assert.StartsWith("Editing the Desktop style.", FloatingCopy.SurfaceCaption(LyricsSurface.Desktop));
        Assert.StartsWith("Editing the Floating style.", FloatingCopy.SurfaceCaption(LyricsSurface.Floating));
        Assert.Null(FloatingCopy.EditingNote(LyricsSurface.Desktop));
        Assert.NotNull(FloatingCopy.EditingNote(LyricsSurface.Floating));
        Assert.Equal("Medium", FloatingCopy.SizeName(44));
        Assert.Equal("Custom", FloatingCopy.SizeName(50));
        Assert.Equal("44 pt", FloatingCopy.Points(44.2));
    }

    // ---- Each layout's own floating size (Windows' own, tester feedback on 0.9.0) ----

    [Fact]
    public void AResizedLayoutKeepsItsOwnSizeAndScaledText()
    {
        var s = new AppSettings().WithLyricsSurface(LyricsSurface.Floating);
        Assert.Equal(s.FloatingTextSize, s.FloatingTextSizeFor("cascade"));
        var resized = s.WithLayoutResized("cascade", new Core.Compositions.SizeD(900, 300), 1.5);
        Assert.Equal(66, resized.FloatingTextSizeFor("cascade"));
        Assert.Equal(new Core.Compositions.SizeD(900, 300), resized.LayoutSize("cascade")!.Value.Size);
        // Another layout keeps the shared size and the Text size setting.
        Assert.Null(resized.LayoutSize("echo"));
        Assert.Equal(44, resized.FloatingTextSizeFor("echo"));
        // Kept across a relaunch.
        Assert.Equal(66, Relaunched(resized).FloatingTextSizeFor("cascade"));
    }

    [Fact]
    public void SettingTheTextSizeByHandForgetsTheLayoutsOwnSizes()
    {
        var resized = new AppSettings().WithLayoutResized("cascade", new Core.Compositions.SizeD(900, 300), 2);
        Assert.Same(resized, resized.WithFloatingTextSize(resized.FloatingTextSize));
        var set = resized.WithFloatingTextSize(30);
        Assert.Equal(30, set.FloatingTextSizeFor("cascade"));
        Assert.Empty(set.FloatingLayoutSizes);
    }

    [Fact]
    public void ALayoutsOwnSizeIsCentredOnTheSharedPlace()
    {
        var own = new FloatingLayoutSize(new Core.Compositions.SizeD(200, 100), 44);
        Assert.Equal(new FloatingRect(150, 50, 200, 100), own.Centred(new FloatingRect(100, 0, 300, 200)));
        Assert.Equal(110, FloatingLayoutSize.ScaledText(44, 100, 400));
        Assert.Equal(22, FloatingLayoutSize.ScaledText(44, 400, 100));
        Assert.Null(FloatingLayoutSize.Decode([1, 2]));
    }
}
