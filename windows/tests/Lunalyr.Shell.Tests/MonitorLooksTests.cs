using System.Text.Json;
using Lunalyr.Core.Floating;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// Each monitor's look and what each display shows: every display keeps its own wallpaper and Desktop and Floating
/// styles, Settings edits one at a time in the settings' own values, and the others draw from read-only copies. Ported
/// from the Mac's MonitorLooksTests.swift, LyricStylesTests.swift and FloatingLyricsTests.swift (the model parts), on
/// Windows' settings record. Made-up displays only.
/// </summary>
public sealed class MonitorLooksTests
{
    private const string A = "DISPLAY-A", B = "DISPLAY-B", C = "DISPLAY-C";
    private static readonly string[] AB = [A, B];

    private static string? Composition(IReadOnlyDictionary<string, JsonElement>? values) =>
        values?.TryGetValue("lyricComposition", out var value) == true ? value.GetString() : null;

    /// <summary>Settings as an install from before monitors had looks, with <paramref name="connected"/> now known.</summary>
    private static AppSettings Known(AppSettings settings, params string[] connected) => MonitorLooks.DisplaysChanged(settings, connected);

    // ---- Keys ----

    // Ported from MonitorLooksTests.swift: testSlotsAreKeptPerDisplayAndSurface
    [Fact]
    public void SlotsAreKeptPerDisplayAndSurface()
    {
        Assert.Equal("UUID-1.desktop", MonitorLooks.StoreKey(new LookSlot("UUID-1", LyricsSurface.Desktop)));
        Assert.Equal("UUID-1.floating", MonitorLooks.StoreKey(new LookSlot("UUID-1", LyricsSurface.Floating)));
        Assert.Equal(MonitorLookKeys.Style.ToHashSet(), MonitorLookKeys.For(LyricsSurface.Floating).ToHashSet()); // a Floating slot holds a style
        Assert.Equal(MonitorLookKeys.All.ToHashSet(), MonitorLookKeys.For(LyricsSurface.Desktop).ToHashSet()); // a Desktop slot holds the wallpaper too
        Assert.Empty(MonitorLookKeys.Style.Intersect(MonitorLookKeys.Wallpaper));
        Assert.Equal(MonitorLookKeys.All.Count, MonitorLookKeys.All.Distinct().Count()); // no key twice
        Assert.Contains("backgroundMode", MonitorLookKeys.Wallpaper);
        Assert.Contains("lyricComposition", MonitorLookKeys.Style);
        // Every look key is a real setting.
        var written = JsonSerializer.SerializeToNode(new AppSettings(), AppSettingsJsonContext.Default.AppSettings)!.AsObject();
        Assert.All(MonitorLookKeys.All, key => Assert.True(written.ContainsKey(key), key));
    }

    // ---- Migration ----

    // Ported from MonitorLooksTests.swift: testEveryConnectedDisplayStartsWithTodaysLook
    [Fact]
    public void EveryConnectedDisplayStartsWithTodaysLook()
    {
        var today = new AppSettings
        {
            LyricComposition = "echo", FontChoice = "installed", FontFamily = "Georgia", BackgroundMode = "visualizer", Vividness = 0.4,
        };
        // Today's Floating style, kept while the desktop was edited.
        today = today with
        {
            LyricStyleFloating = new Dictionary<string, JsonElement>
            {
                ["lyricComposition"] = JsonSerializer.SerializeToElement("cascade", AppSettingsJsonContext.Default.String),
                ["lyricScale"] = JsonSerializer.SerializeToElement(1.0, AppSettingsJsonContext.Default.Double),
            },
        };
        var s = Known(today, A, B);
        Assert.Equal(A, s.LookMonitor); // Settings edits the main display
        foreach (var display in AB)
        {
            var desktop = s.LookFor(display, LyricsSurface.Desktop, A);
            Assert.Equal("echo", desktop.LyricComposition);
            Assert.Equal("Georgia", desktop.FontFamily);
            var wallpaper = s.WallpaperLookFor(display, A);
            Assert.Equal("visualizer", wallpaper.BackgroundMode);
            Assert.Equal(0.4, wallpaper.Vividness);
            Assert.Equal("cascade", s.LookFor(display, LyricsSurface.Floating, A).LyricComposition);
        }
        var keptB = MonitorLooks.Saved(s, new LookSlot(B, LyricsSurface.Desktop));
        Assert.Equal("echo", Composition(keptB));
        Assert.Equal("visualizer", keptB!["backgroundMode"].GetString());
        Assert.Null(MonitorLooks.Saved(s, new LookSlot(A, LyricsSurface.Desktop))); // the edited slot stays in the settings' own values
        Assert.NotNull(s.LyricStyleFloating); // today's styles stay readable for a release
    }

    // Ported from MonitorLooksTests.swift: testMigratingWhileFloatingIsEditedKeepsTheLiveFloatingStyle
    [Fact]
    public void MigratingWhileFloatingIsEditedKeepsTheLiveFloatingStyle()
    {
        // Editing Floating: the desktop's style is the kept one, the live values are Floating's.
        var today = new AppSettings
        {
            LyricsSurface = "floating", LyricComposition = "torque",
            LyricStyleDesktop = new Dictionary<string, JsonElement> { ["lyricComposition"] = JsonSerializer.SerializeToElement("portal", AppSettingsJsonContext.Default.String) },
            LyricStyleFloating = new Dictionary<string, JsonElement> { ["lyricComposition"] = JsonSerializer.SerializeToElement("mosaic", AppSettingsJsonContext.Default.String) },
        };
        var s = Known(today, A, B);
        Assert.Equal("portal", s.LookFor(A, LyricsSurface.Desktop, A).LyricComposition);
        Assert.Equal("torque", s.LookFor(A, LyricsSurface.Floating, A).LyricComposition);
        Assert.Equal("portal", s.LookFor(B, LyricsSurface.Desktop, A).LyricComposition);
        Assert.Equal("torque", s.LookFor(B, LyricsSurface.Floating, A).LyricComposition); // copied from the live Floating style, not the stale kept one
    }

    // Ported from MonitorLooksTests.swift: testADisplayConnectedLaterStartsAsTheMainDisplaysLook
    [Fact]
    public void ADisplayConnectedLaterStartsAsTheMainDisplaysLook()
    {
        var s = Known(new AppSettings(), A) with { LyricComposition = "hinge", BackgroundMode = "pattern" };
        s = Known(s, A, C);
        Assert.Equal("hinge", s.LookFor(C, LyricsSurface.Desktop, A).LyricComposition);
        Assert.Equal("pattern", s.WallpaperLookFor(C, A).BackgroundMode);
        // A copy: changing the main display afterwards leaves it alone.
        s = s with { LyricComposition = "portal" };
        Assert.Equal("hinge", s.LookFor(C, LyricsSurface.Desktop, A).LyricComposition);
    }

    // Ported from MonitorLooksTests.swift: testUnpluggingTheEditedMonitorEditsTheMainOneAndKeepsItsLook
    [Fact]
    public void UnpluggingTheEditedMonitorEditsTheMainOneAndKeepsItsLook()
    {
        var s = MonitorLooks.Edit(Known(new AppSettings(), A, B), B, A) with { LyricComposition = "mosaic" };
        s = Known(s, A);
        Assert.Equal(A, s.LookMonitor);
        Assert.Equal("mosaic", Composition(MonitorLooks.Saved(s, new LookSlot(B, LyricsSurface.Desktop))));
    }

    // ---- Drawing ----

    // Ported from MonitorLooksTests.swift: testTwoDisplaysDrawDifferentLooksAtOnce
    [Fact]
    public void TwoDisplaysDrawDifferentLooksAtOnce()
    {
        var s = Known(new AppSettings(), A, B) with { LyricComposition = "echo", FontFamily = "Georgia", BackgroundMode = "visualizer" };
        s = MonitorLooks.Edit(s, B, A) with { LyricComposition = "cascade", FontFamily = "Consolas", BackgroundMode = "pattern" };
        var onB = s.LookFor(B, LyricsSurface.Desktop, A);
        Assert.Same(s, onB); // the edited monitor draws with the settings' own values
        var onA = s.LookFor(A, LyricsSurface.Desktop, A);
        Assert.Equal("echo", onA.LyricComposition);
        Assert.Equal("Georgia", onA.FontFamily);
        Assert.Equal("visualizer", s.WallpaperLookFor(A, A).BackgroundMode);
        Assert.Equal("cascade", onB.LyricComposition);
        Assert.Equal("pattern", s.WallpaperLookFor(B, A).BackgroundMode);
    }

    // Ported from MonitorLooksTests.swift: testTheWallpaperFollowsTheEditedMonitorWhicheverStyleIsEdited
    [Fact]
    public void TheWallpaperFollowsTheEditedMonitorWhicheverStyleIsEdited()
    {
        var s = Known(new AppSettings(), A, B).WithLyricsSurface(LyricsSurface.Floating) with { BackgroundMode = "colourOnly" };
        Assert.Same(s, s.WallpaperLookFor(A, A)); // monitor A's wallpaper is being edited
        Assert.Equal("colourOnly", s.WallpaperLookFor(A, A).BackgroundMode);
        Assert.NotEqual("colourOnly", s.WallpaperLookFor(B, A).BackgroundMode);
    }

    // Ported from MonitorLooksTests.swift: testACopyFollowsTheAppsOwnSettings
    [Fact]
    public void ACopyFollowsTheAppsOwnSettings()
    {
        var s = Known(new AppSettings(), A, B) with { LowPowerVisuals = true, AnimationRate = 30 };
        var copy = s.LookFor(B, LyricsSurface.Desktop, A);
        Assert.NotSame(s, copy);
        Assert.True(copy.LowPowerVisuals); // power settings are the app's
        Assert.Equal(30, copy.AnimationRate);
        Assert.False(MonitorLooks.Saved(s, new LookSlot(B, LyricsSurface.Desktop))!.ContainsKey("lowPowerVisuals"));
    }

    // ---- Editing ----

    // Ported from MonitorLooksTests.swift: testSwitchingMonitorsKeepsTheOneLeftAndLoadsThePickedOne
    [Fact]
    public void SwitchingMonitorsKeepsTheOneLeftAndLoadsThePickedOne()
    {
        var s = Known(new AppSettings(), A, B) with
        {
            LyricComposition = "echo", BackgroundMode = "musicVideo", MusicVideoFallback = "pattern", Vividness = 0.6,
            LyricColourMode = "albumDrift", WavefieldColours = "original", TextBacking = 0.8,
        };
        s = MonitorLooks.Edit(s, B, A);
        Assert.Equal(B, s.LookMonitor);
        var keptA = MonitorLooks.Saved(s, new LookSlot(A, LyricsSurface.Desktop));
        Assert.Equal("echo", Composition(keptA));
        Assert.Equal("musicVideo", keptA!["backgroundMode"].GetString());
        // B started as a copy of A's look before it was edited.
        s = s with
        {
            LyricComposition = "torque", BackgroundMode = "shader", Vividness = 1.2, LyricColourMode = "customGradient", WavefieldColours = "album",
            TextBacking = 0.1,
        };
        s = MonitorLooks.Edit(s, A, A);
        Assert.Equal("echo", s.LyricComposition);
        Assert.Equal("musicVideo", s.BackgroundMode);
        Assert.Equal("pattern", s.MusicVideoFallback); // not overwritten by the type it left
        Assert.Equal(0.6, s.Vividness);
        Assert.Equal("albumDrift", s.LyricColourMode);
        Assert.Equal("original", s.WavefieldColours);
        Assert.Equal(0.8, s.TextBacking);
        Assert.Equal("torque", s.LookFor(B, LyricsSurface.Desktop, A).LyricComposition);
        Assert.Equal("shader", s.WallpaperLookFor(B, A).BackgroundMode);
        // What was loaded reads back the same from the settings file.
        var reread = JsonSerializer.Deserialize(JsonSerializer.Serialize(s, AppSettingsJsonContext.Default.AppSettings), AppSettingsJsonContext.Default.AppSettings)!.Normalized();
        Assert.Equal(s.BackgroundMode, reread.BackgroundMode);
        Assert.Equal(s.LyricComposition, reread.LyricComposition);
        Assert.Equal(Composition(MonitorLooks.Saved(s, new LookSlot(B, LyricsSurface.Desktop))), Composition(MonitorLooks.Saved(reread, new LookSlot(B, LyricsSurface.Desktop))));
    }

    // Ported from MonitorLooksTests.swift: testSwitchingStylesStaysOnTheEditedMonitor
    [Fact]
    public void SwitchingStylesStaysOnTheEditedMonitor()
    {
        var s = MonitorLooks.Edit(Known(new AppSettings(), A, B), B, A).WithLyricsSurface(LyricsSurface.Floating) with { LyricComposition = "portal" };
        Assert.Equal(new LookSlot(B, LyricsSurface.Floating), s.EditingSlot(A));
        s = s.WithLyricsSurface(LyricsSurface.Desktop);
        Assert.Equal("portal", Composition(MonitorLooks.Saved(s, new LookSlot(B, LyricsSurface.Floating))));
        Assert.Null(MonitorLooks.Saved(s, new LookSlot(A, LyricsSurface.Floating))); // A's floating style was never used
        Assert.NotEqual("portal", s.LookFor(A, LyricsSurface.Floating, A).LyricComposition);
    }

    // Ported from MonitorLooksTests.swift: testCopyToAllMonitors
    [Fact]
    public void CopyToAllMonitors()
    {
        var s = Known(new AppSettings(), A, B, C) with { LyricComposition = "mosaic", BackgroundMode = "colourOnly" };
        s = s.WithLyricsSurface(LyricsSurface.Floating) with { LyricComposition = "hinge" };
        s = MonitorLooks.CopyEdited(s, [A, B, C], A);
        foreach (var display in new[] { B, C })
        {
            Assert.Equal("mosaic", s.LookFor(display, LyricsSurface.Desktop, A).LyricComposition);
            Assert.Equal("hinge", s.LookFor(display, LyricsSurface.Floating, A).LyricComposition);
            Assert.Equal("colourOnly", s.WallpaperLookFor(display, A).BackgroundMode);
        }
    }

    // Ported from MonitorLooksTests.swift: testSameOnEveryMonitorDrawsTheEditedLookEverywhere
    [Fact]
    public void SameOnEveryMonitorDrawsTheEditedLookEverywhere()
    {
        var s = MonitorLooks.Edit(Known(new AppSettings(), A, B), B, A) with { LyricComposition = "portal" };
        s = MonitorLooks.Edit(s, A, A) with { LyricComposition = "echo" };
        s = MonitorLooks.WithSameEverywhere(s, true, AB);
        Assert.Same(s, s.LookFor(B, LyricsSurface.Desktop, A)); // one set of values, no copies
        Assert.Same(s, s.WallpaperLookFor(B, A));
        s = MonitorLooks.WithSameEverywhere(s, false, AB);
        Assert.Equal("echo", s.LookFor(B, LyricsSurface.Desktop, A).LyricComposition); // turned off, each monitor keeps the look it showed
    }

    // Ported from MonitorLooksTests.swift: testUnpluggingTheEditedMonitorWhileTheSameEverywhereKeepsThatLook
    [Fact]
    public void UnpluggingTheEditedMonitorWhileTheSameEverywhereKeepsThatLook()
    {
        var s = Known(new AppSettings(), A, B) with { LyricComposition = "torque" };
        s = MonitorLooks.WithSameEverywhere(MonitorLooks.Edit(s, B, A), true, AB) with { LyricComposition = "mosaic", BackgroundMode = "colourOnly" };
        s = Known(s, A);
        Assert.Equal(A, s.LookMonitor);
        Assert.Equal("mosaic", s.LyricComposition); // not the look A kept before every monitor shared one
        Assert.Equal("colourOnly", s.BackgroundMode);
    }

    // Ported from MonitorLooksTests.swift: testAnImportAnotherMonitorShowsIsKept
    [Fact]
    public void AnImportAnotherMonitorShowsIsKept()
    {
        var s = Known(new AppSettings(), A, B) with { WallpaperPath = @"C:\Pictures\one.png" };
        s = MonitorLooks.Edit(s, B, A);
        Assert.True(MonitorLooks.KeepsWallpaperFile(s, @"C:\Pictures\one.png", A)); // monitor A still shows it
        Assert.False(MonitorLooks.KeepsWallpaperFile(s, @"C:\Pictures\other.png", A));
    }

    // ---- The two styles ----

    // Ported from LyricStylesTests.swift: testTheFloatingStyleStartsFromTheDesktopsLessItsSize
    [Fact]
    public void TheFloatingStyleStartsFromTheDesktopsLessItsSize()
    {
        var s = Known(new AppSettings { LyricComposition = "echo", LyricScale = 1.4 }, A);
        var floating = s.LookFor(null, LyricsSurface.Floating, A);
        Assert.NotSame(s, floating);
        Assert.Same(s, s.LookFor(null, LyricsSurface.Desktop, A)); // the desktop draws with the settings' own values
        Assert.Equal("echo", floating.LyricComposition);
        Assert.Equal(1, floating.LyricScale);
        Assert.Null(MonitorLooks.Saved(s, new LookSlot(A, LyricsSurface.Floating))); // not kept until floating lyrics are used
    }

    // Ported from LyricStylesTests.swift: testTheFloatingStyleIsKeptWhenFloatingLyricsAreFirstUsed
    [Fact]
    public void TheFloatingStyleIsKeptWhenFloatingLyricsAreFirstUsed()
    {
        var s = Known(new AppSettings { LyricComposition = "echo" }, A);
        // Months later, the desktop has moved on before anything floats.
        s = s with { LyricComposition = "torque" };
        s = s.WithFloatingToggled([A], A);
        Assert.Equal("torque", Composition(MonitorLooks.Saved(s, new LookSlot(A, LyricsSurface.Floating)))); // the desktop's look of that day
        Assert.Equal("torque", s.LookFor(null, LyricsSurface.Floating, A).LyricComposition);
        // From then on the two are apart.
        s = s with { LyricComposition = "hinge" };
        s = MonitorLooks.KeepFloatingStyle(s, [A], A);
        Assert.Equal("torque", s.LookFor(null, LyricsSurface.Floating, A).LyricComposition);
    }

    // Ported from LyricStylesTests.swift: testBothSurfacesReadTheirOwnStyleAtOnce
    [Fact]
    public void BothSurfacesReadTheirOwnStyleAtOnce()
    {
        var s = Known(new AppSettings(), A) with { LyricComposition = "portal", FontFamily = "Georgia", LyricColourMode = "albumDrift" };
        s = s.WithLyricsSurface(LyricsSurface.Floating) with
        {
            LyricComposition = "cascade", FontFamily = "Consolas", LyricColourMode = "customGradient", BackingVocalsLayer = false,
        };
        // Editing Floating: the floating lyrics read the live values, the desktop the kept ones.
        var desktop = s.LookFor(null, LyricsSurface.Desktop, A);
        var floating = s.LookFor(null, LyricsSurface.Floating, A);
        Assert.Equal("portal", desktop.LyricComposition);
        Assert.Equal("Georgia", desktop.FontFamily);
        Assert.Equal("albumDrift", desktop.LyricColourMode);
        Assert.True(desktop.BackingVocalsLayer);
        Assert.Equal("cascade", floating.LyricComposition);
        Assert.Equal("Consolas", floating.FontFamily);
        Assert.False(floating.BackingVocalsLayer);
        // Back to editing the desktop: the same two looks, the values swapped.
        s = s.WithLyricsSurface(LyricsSurface.Desktop);
        Assert.Equal("portal", s.LookFor(null, LyricsSurface.Desktop, A).LyricComposition);
        Assert.Equal("cascade", s.LookFor(null, LyricsSurface.Floating, A).LyricComposition);
        Assert.Equal("customGradient", s.LookFor(null, LyricsSurface.Floating, A).LyricColourMode);
        Assert.False(s.LookFor(null, LyricsSurface.Floating, A).BackingVocalsLayer);
        Assert.True(s.LookFor(null, LyricsSurface.Desktop, A).BackingVocalsLayer);
    }

    // Ported from LyricStylesTests.swift: testEditingTheDesktopLeavesTheFloatingLyricsAlone
    [Fact]
    public void EditingTheDesktopLeavesTheFloatingLyricsAlone()
    {
        var s = Known(new AppSettings(), A).WithLyricsSurface(LyricsSurface.Floating) with { LyricComposition = "torque" };
        s = s.WithLyricsSurface(LyricsSurface.Desktop) with { LyricComposition = "hinge", EchoStrength = 0.3 };
        var floating = s.LookFor(null, LyricsSurface.Floating, A);
        Assert.Equal("torque", floating.LyricComposition);
        Assert.Equal(new AppSettings().EchoStrength, floating.EchoStrength);
        Assert.Equal("hinge", s.LookFor(null, LyricsSurface.Desktop, A).LyricComposition);
    }

    // ---- What each display shows ----

    // Ported from FloatingLyricsTests.swift: testEachDisplayDrawsWhatItsChoiceSays (the choice's half)
    [Fact]
    public void EachChoiceSaysWhereTheLyricsGo()
    {
        Assert.Equal([DisplayLyrics.Desktop, DisplayLyrics.Both], DisplayLyricsChoices.All.Where(c => c.OnDesktop()));
        Assert.Equal([DisplayLyrics.Floating, DisplayLyrics.Both], DisplayLyricsChoices.All.Where(c => c.Floats()));
        Assert.All(DisplayLyricsChoices.All, c => Assert.Equal(c, DisplayLyricsChoices.Parse(c.Raw())));
    }

    // Ported from FloatingLyricsTests.swift: testChoicesAreStoredPerDisplayAndNewDisplaysGetDesktopLyrics
    [Fact]
    public void ChoicesAreStoredPerDisplayAndNewDisplaysGetDesktopLyrics()
    {
        var s = new AppSettings();
        Assert.Equal(DisplayLyrics.Desktop, s.DisplayChoice("A")); // a display never seen shows the lyrics on its desktop
        s = s.WithDisplayChoice("A", DisplayLyrics.Both).WithDisplayChoice("B", DisplayLyrics.None);
        Assert.Equal(DisplayLyrics.Both, s.DisplayChoice("A"));
        Assert.Equal(DisplayLyrics.None, s.DisplayChoice("B"));
        Assert.Equal(DisplayLyrics.Desktop, s.DisplayChoice("C"));
        Assert.Equal(new Dictionary<string, string> { ["A"] = "both", ["B"] = "none" }, s.DisplayLyrics);
        var reread = JsonSerializer.Deserialize(JsonSerializer.Serialize(s, AppSettingsJsonContext.Default.AppSettings), AppSettingsJsonContext.Default.AppSettings)!
            .Normalized().WithDisplaysMigrated(["A", "B", "C"]);
        Assert.Equal(DisplayLyrics.Both, reread.DisplayChoice("A"));
        Assert.Equal(DisplayLyrics.None, reread.DisplayChoice("B"));
        Assert.Equal(DisplayLyrics.Desktop, reread.DisplayChoice("C")); // a stored choice is never migrated again
    }

    // Ported from FloatingLyricsTests.swift: testWhichDisplaysGetAFloatingWindow
    [Fact]
    public void WhichDisplaysGetAFloatingWindow()
    {
        string[] connected = ["A", "B", "C", "D"];
        var s = new AppSettings();
        Assert.Empty(s.FloatingDisplays(connected)); // nothing floats by default
        s = s.WithDisplayChoice("A", DisplayLyrics.Floating).WithDisplayChoice("C", DisplayLyrics.Both)
            .WithDisplayChoice("D", DisplayLyrics.None).WithDisplayChoice("gone", DisplayLyrics.Floating);
        Assert.Equal(["A", "C"], s.FloatingDisplays(connected)); // one window per floating display
        Assert.True(s.FloatsOn(connected));
        Assert.False(s.FloatsOn(["B", "D"]));
    }

    // Ported from FloatingLyricsTests.swift: testMigrationFromTheDesktopKeepsEveryDisplaysLyrics
    [Fact]
    public void MigrationFromTheDesktopKeepsEveryDisplaysLyrics()
    {
        var s = new AppSettings { LyricsSurface = "desktop" }.WithDisplaysMigrated(["main", "side"]);
        Assert.Equal(DisplayLyrics.Desktop, s.DisplayChoice("main"));
        Assert.Equal(DisplayLyrics.Desktop, s.DisplayChoice("side"));
        Assert.Empty(s.FloatingDisplays(["main", "side"]));
    }

    // Ported from FloatingLyricsTests.swift: testMigrationFromFloatingOnAChosenDisplay
    [Fact]
    public void MigrationFromFloatingOnAChosenDisplay()
    {
        var s = new AppSettings { LyricsSurface = "floating", FloatingDisplay = "side" }.WithDisplaysMigrated(["main", "side", "third"]);
        Assert.Equal(DisplayLyrics.Floating, s.DisplayChoice("side")); // the window stays on the display it was on
        Assert.Equal(DisplayLyrics.None, s.DisplayChoice("main")); // Floating left every wallpaper without lyrics
        Assert.Equal(DisplayLyrics.None, s.DisplayChoice("third"));
        Assert.Equal(DisplayLyrics.Desktop, s.DisplayChoice("plugged-in-later"));
        Assert.Equal(LyricsSurface.Floating, s.Surface); // and Settings still edits the Floating style
    }

    // Ported from FloatingLyricsTests.swift: testMigrationFromFloatingWithNoDisplayChosen
    [Theory]
    [InlineData("")]
    [InlineData("disconnected")]
    public void MigrationFromFloatingWithNoDisplayChosen(string chosen)
    {
        var s = new AppSettings { LyricsSurface = "floating", FloatingDisplay = chosen }.WithDisplaysMigrated(["main", "side"]);
        Assert.Equal(DisplayLyrics.Floating, s.DisplayChoice("main")); // the main display, where the window was
        Assert.Equal(DisplayLyrics.None, s.DisplayChoice("side"));
    }

    // Ported from FloatingLyricsTests.swift: testMigrationWaitsForADisplay
    [Fact]
    public void MigrationWaitsForADisplay()
    {
        var s = new AppSettings { LyricsSurface = "floating" }.WithDisplaysMigrated([]);
        Assert.Null(s.DisplayLyrics); // nothing to migrate yet
        s = s.WithDisplaysMigrated(["main"]);
        Assert.Equal(DisplayLyrics.Floating, s.DisplayChoice("main"));
        s = s.WithDisplayChoice("main", DisplayLyrics.Desktop).WithDisplaysMigrated(["main"]);
        Assert.Equal(DisplayLyrics.Desktop, s.DisplayChoice("main")); // only once
    }

    // Ported from FloatingLyricsTests.swift: testFloatingQuickToggle (Windows' target: the primary display)
    [Fact]
    public void FloatingQuickToggle()
    {
        string[] connected = ["main", "side"];
        var s = new AppSettings().WithDisplayChoice("side", DisplayLyrics.None).WithFloatingToggled(connected, "side");
        Assert.Equal(DisplayLyrics.Floating, s.DisplayChoice("side")); // on for the target display
        Assert.Equal(DisplayLyrics.Desktop, s.DisplayChoice("main")); // the others keep their choice
        s = s.WithDisplayChoice("main", DisplayLyrics.Both).WithFloatingToggled(connected, "side");
        Assert.Equal(DisplayLyrics.Desktop, s.DisplayChoice("main")); // off everywhere: back on the desktop
        Assert.Equal(DisplayLyrics.Desktop, s.DisplayChoice("side"));
        s = s.WithFloatingToggled(connected, null);
        Assert.Equal(DisplayLyrics.Floating, s.DisplayChoice("main")); // no target: the main display
    }

    [Fact]
    public void ASettingsFileFromBeforeLooksChangesNothingOnScreen()
    {
        // A 2.1.1 settings file floating on the second display, editing Floating, with both styles kept.
        const string Old = """
            { "schemaVersion": 1, "lyricsSurface": "floating", "floatingDisplay": "side", "lyricComposition": "hinge",
              "lyricStyleDesktop": { "lyricComposition": "echo" }, "lyricStyleFloating": { "lyricComposition": "hinge" },
              "backgroundMode": "pattern" }
            """;
        var s = JsonSerializer.Deserialize(Old, AppSettingsJsonContext.Default.AppSettings)!.Normalized();
        s = Known(s, "main", "side");
        Assert.Equal(DisplayLyrics.None, s.DisplayChoice("main"));
        Assert.Equal(DisplayLyrics.Floating, s.DisplayChoice("side"));
        Assert.Equal("hinge", s.LookFor("side", LyricsSurface.Floating, "main").LyricComposition);
        Assert.Equal("echo", s.LookFor("main", LyricsSurface.Desktop, "main").LyricComposition);
        Assert.Equal("pattern", s.WallpaperLookFor("side", "main").BackgroundMode);
        Assert.Equal("pattern", s.WallpaperLookFor("main", "main").BackgroundMode);
    }
}
