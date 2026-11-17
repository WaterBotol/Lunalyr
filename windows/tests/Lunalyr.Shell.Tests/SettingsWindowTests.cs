using System.Text.Json;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;

namespace Lunalyr.Shell.Tests;

/// <summary>The settings the window added (milestone 5): the Scattered family's sliders, the custom mix, an entrance per layout and the app's theme.</summary>
public sealed class SettingsWindowSettingsTests
{
    [Fact]
    public void TheNewDefaultsAreTheMacs()
    {
        var settings = new AppSettings();
        Assert.Equal(1.0, settings.ConstellationSpread);
        Assert.Equal(0.20, settings.ConstellationLinkOpacity);
        Assert.Equal(1.0, settings.EchoStrength);
        Assert.Equal(["fade", "lift", "cutoutFold"], settings.WordEntranceMix);
        Assert.Empty(settings.WordArrivalByStyle);
        Assert.Equal("system", settings.AppTheme);

        // The renderer's defaults are the same, so scenes made without the settings draw as before.
        var style = settings.Style();
        Assert.Equal(LyricStyle.Default.ConstellationSpread, style.ConstellationSpread);
        Assert.Equal(LyricStyle.Default.ConstellationLinkOpacity, style.ConstellationLinkOpacity);
        Assert.Equal(LyricStyle.Default.EchoStrength, style.EchoStrength);
        Assert.Empty(style.RevealByLayout);
    }

    [Fact]
    public void TwoStylesFromEqualSettingsAreEqual()
    {
        // The controller and the layers compare styles to decide whether to set the lyrics again: the lists and maps
        // in a style must compare by value, or every settings change would look like a new style.
        var settings = new AppSettings
        {
            WordEntranceMix = ["pop", "orbit"],
            WordArrivalByStyle = new Dictionary<string, string> { ["scattered"] = "orbit", ["echo"] = "mix" },
        };
        var copy = settings with
        {
            WordEntranceMix = [.. settings.WordEntranceMix],
            WordArrivalByStyle = new Dictionary<string, string> { ["echo"] = "mix", ["scattered"] = "orbit" },
        };
        Assert.Equal(settings.Style(), copy.Style());
        Assert.Equal(settings.Style().GetHashCode(), copy.Style().GetHashCode());
        Assert.NotEqual(settings.Style(), (settings with { EchoStrength = 0.5 }).Style());
        Assert.NotEqual(settings.Style(), settings.WithMixEntrance("glide", true).Style());
    }

    [Fact]
    public void OutOfRangeAndUnknownValuesAreRepaired()
    {
        var repaired = new AppSettings
        {
            ConstellationSpread = 9,
            ConstellationLinkOpacity = double.NaN,
            EchoStrength = -1,
            WordEntranceMix = ["splitInk", "nonsense", "automatic", "mix", "fade", "fade"],
            WordArrivalByStyle = new Dictionary<string, string> { ["scattered"] = "orbit", ["nope"] = "pop", ["echo"] = "sideways" },
            AppTheme = "purple",
        }.Normalized();
        Assert.Equal(1.8, repaired.ConstellationSpread);
        Assert.Equal(0.20, repaired.ConstellationLinkOpacity);
        Assert.Equal(0, repaired.EchoStrength);
        Assert.Equal(["fade", "splitInk"], repaired.WordEntranceMix); // known mix entrances, once, in the Mac's order
        Assert.Equal(new Dictionary<string, string> { ["scattered"] = "orbit" }, repaired.WordArrivalByStyle);
        Assert.Equal("system", repaired.AppTheme);
    }

    [Fact]
    public async Task TheNewKeysRoundTripThroughTheFile()
    {
        using var temp = new TempFolder();
        var changed = new AppSettings
        {
            ConstellationSpread = 1.4,
            ConstellationLinkOpacity = 0,
            EchoStrength = 0.3,
            WordEntranceMix = ["pop", "satinRibbon"],
            WordArrivalByStyle = new Dictionary<string, string> { ["automatic"] = "glide", ["hinge"] = "pop" },
            AppTheme = "dark",
        };
        await using (var store = AppSettingsStore.Open(temp.Path, Logger.Null))
        {
            store.Set(changed);
            Assert.True(await store.FlushAsync());
        }

        var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "settings.json"), TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal(1.4, json.GetProperty("constellationSpread").GetDouble());
        Assert.Equal("glide", json.GetProperty("wordArrivalByStyle").GetProperty("automatic").GetString());
        Assert.Equal("satinRibbon", json.GetProperty("wordEntranceMix")[1].GetString());

        await using var reopened = AppSettingsStore.Open(temp.Path, Logger.Null);
        var s = reopened.Current;
        Assert.Equal((1.4, 0.0, 0.3, "dark"), (s.ConstellationSpread, s.ConstellationLinkOpacity, s.EchoStrength, s.AppTheme));
        Assert.Equal(["pop", "satinRibbon"], s.WordEntranceMix);
        Assert.Equal("pop", s.WordArrivalByStyle["hinge"]);
    }

    [Fact]
    public void AChosenLayoutUsesItsOwnEntranceElseTheOneForAll()
    {
        var settings = new AppSettings
        {
            LyricComposition = "scattered",
            WordArrival = "lift",
            WordArrivalByStyle = new Dictionary<string, string> { ["scattered"] = "orbit", ["automatic"] = "glide" },
        };
        var style = settings.Style();
        Assert.Equal("orbit", style.RevealFor("scattered"));
        Assert.Equal("floatUp", style.RevealFor("echo")); // no entry: the one entrance for all
        Assert.Equal(WordArrival.Orbit, style.EntranceFor(CompositionKind.Scattered).Selected);
        Assert.Equal("scattered", settings.EntranceKey);
        Assert.Equal("orbit", settings.EntranceForControls());
        Assert.Equal("lift", (settings with { LyricComposition = "echo" }).EntranceForControls());
    }

    [Fact]
    public void MatchTheMusicsOwnEntranceIsForEveryLayoutItPicks()
    {
        var settings = new AppSettings
        {
            LyricComposition = "automatic",
            WordArrivalByStyle = new Dictionary<string, string> { ["automatic"] = "glide", ["scattered"] = "orbit" },
        };
        var style = settings.Style();
        Assert.Equal("glide", style.RevealFor("scattered"));
        Assert.Equal("glide", style.RevealFor("hinge"));

        // Without one of its own, each layout it picks keeps its own entrance.
        var perLayout = (settings with { WordArrivalByStyle = new Dictionary<string, string> { ["scattered"] = "orbit" } }).Style();
        Assert.Equal("orbit", perLayout.RevealFor("scattered"));
        Assert.Equal("automatic", perLayout.RevealFor("hinge"));
        Assert.Equal("automatic", settings.EntranceKey);
        Assert.Equal("glide", settings.EntranceForControls());
        Assert.Equal("pop", settings.WithEntranceForControls("pop").WordArrivalByStyle["automatic"]);
    }

    [Fact]
    public void TheCustomMixReachesTheLayers()
    {
        var settings = new AppSettings { WordArrival = "mix", WordEntranceMix = ["satinRibbon", "pop"] };
        var choice = settings.Style().EntranceFor(CompositionKind.Editorial);
        Assert.Equal(WordArrival.Mix, choice.Selected);
        Assert.Equal([WordArrival.Pop, WordArrival.SatinRibbon], choice.MixEntrances); // the Mac's order

        // An empty mix leaves the layout its own entrance; no list at all is the Mac's default mix.
        var empty = (settings with { WordEntranceMix = [] }).Style().EntranceFor(CompositionKind.Scattered);
        Assert.Equal(WordArrival.Pop, empty.For(CompositionKind.Scattered, 7));
        var none = new LyricStyle { Reveal = "mix" }.EntranceFor(CompositionKind.Editorial);
        Assert.Equal(WordArrivals.DefaultMix, none.MixEntrances);

        // A reveal that names its own list keeps it.
        var named = new LyricStyle { Reveal = "mix:glide", MixEntrances = new ValueList(["pop"]) }.EntranceFor(CompositionKind.Editorial);
        Assert.Equal([WordArrival.Glide], named.MixEntrances);
    }

    [Fact]
    public void MixEntrancesAreKeptInTheMacsOrder()
    {
        var settings = new AppSettings { WordEntranceMix = ["fade"] };
        settings = settings.WithMixEntrance("splitInk", true).WithMixEntrance("pop", true).WithMixEntrance("fade", false);
        Assert.Equal(["pop", "splitInk"], settings.WordEntranceMix);
        Assert.Equal([WordArrival.Pop, WordArrival.SplitInk], settings.MixEntrances());
    }

    [Fact]
    public void TheSlidersReachTheStyle()
    {
        var style = new AppSettings { ConstellationSpread = 0.4, ConstellationLinkOpacity = 0.6, EchoStrength = 1.8 }.Style();
        Assert.Equal((0.4, 0.6, 1.8), (style.ConstellationSpread, style.ConstellationLinkOpacity, style.EchoStrength));
    }
}

/// <summary>The window's choices, captions and source list.</summary>
public sealed class SettingsCopyTests
{
    // Ported from DesignSystemTests.swift: testTimingNudgeWordsTheOffsetBothWays
    [Fact]
    public void TestTimingNudgeWordsTheOffsetBothWays()
    {
        Assert.Equal("0.00 s", SettingsCopy.FormatOffset(0));
        Assert.Equal("+0.25 s", SettingsCopy.FormatOffset(0.25));
        Assert.Equal("−0.30 s", SettingsCopy.FormatOffset(-0.3));
        Assert.Equal("No offset", SettingsCopy.SpokenOffset(0));
        Assert.Equal("Lyrics 0.25 seconds earlier", SettingsCopy.SpokenOffset(0.25));
        Assert.Equal("Lyrics 0.30 seconds later", SettingsCopy.SpokenOffset(-0.3));
    }

    // Ported from SettingsPolishTests.swift: testTheSongOffsetSliderWidensForLargeOffsets
    [Fact]
    public void TestTheSongOffsetSliderWidensForLargeOffsets()
    {
        Assert.Equal((-10.0, 10.0), SettingsCopy.SongOffsetRange(2));
        Assert.Equal((-10.0, 10.0), SettingsCopy.SongOffsetRange(-10));
        Assert.Equal((-30.0, 30.0), SettingsCopy.SongOffsetRange(18.5));
        Assert.Equal((-30.0, 30.0), SettingsCopy.SongOffsetRange(-24));
        var (min, max) = SettingsCopy.SongOffsetRange(30);
        Assert.InRange(30, min, max);
    }

    [Fact]
    public void EveryLayoutIsOfferedOnceWithMatchTheMusicFirst()
    {
        Assert.Equal(AppSettings.Compositions, SettingsCopy.Layouts.Select(c => c.Value));
        Assert.Equal("Custom Mix", SettingsCopy.Layouts[0].Title);
        Assert.All(SettingsCopy.Layouts.Skip(1), c => Assert.NotNull(SettingsCopy.Kind(c.Value)));
        Assert.All(SettingsCopy.Layouts, c => Assert.False(string.IsNullOrEmpty(c.Detail)));
        Assert.Null(SettingsCopy.Kind("automatic"));
    }

    [Fact]
    public void EveryStoredChoiceHasATitle()
    {
        Assert.Equal(AppSettings.WordArrivals, SettingsCopy.Entrances("editorial").Select(c => c.Value));
        Assert.Equal(["off", "below", "replace"], SettingsCopy.ReadingDisplays.Select(c => c.Value));
        Assert.Equal(["automatic", "japanese", "mandarin", "cantonese", "korean", "other"], SettingsCopy.ReadingLanguages.Select(c => c.Value));
        Assert.Equal(AppSettings.AnimationRates.Select(r => r.ToString(System.Globalization.CultureInfo.InvariantCulture)).Order(),
            SettingsCopy.FrameRates.Select(c => c.Value).Order());
        Assert.Equal(AppSettings.AppThemes, SettingsCopy.AppThemes.Select(c => c.Value));
        Assert.Equal(["smart", "priority"], SettingsCopy.MatchModes.Select(c => c.Value));
        Assert.Equal(["complement", "album", "custom"], SettingsCopy.HighlightModes.Select(c => c.Value));
    }

    [Fact]
    public void EveryMacWallpaperTypeIsOffered()
    {
        // The Mac's grid order: the five Lunalyr draws, then the three that start from something of yours.
        Assert.Equal(["auto", "visualizer", "shader", "pattern", "colourOnly", "desktop", "custom", "musicVideo"],
            SettingsCopy.WallpaperTypes.Select(t => t.Value));
        Assert.Equal(AppSettings.BackgroundModes.Order(), SettingsCopy.WallpaperTypes.Select(t => t.Value).Order());
        // Music video arrived last (milestone 6, "Music video"): every type is available now.
        Assert.All(SettingsCopy.WallpaperTypes, t => Assert.True(t.Available, t.Value));
        Assert.All(SettingsCopy.WallpaperTypes, t => Assert.EndsWith(".", t.Summary));
        Assert.Equal("colourOnly", SettingsCopy.WallpaperTypeFor("colour"));
        Assert.Equal("colourOnly", SettingsCopy.WallpaperTypeFor("gradient"));
        Assert.Equal("auto", SettingsCopy.WallpaperTypeFor("auto"));
        Assert.Equal("shader", SettingsCopy.WallpaperTypeFor("shader"));
        Assert.Equal("auto", SettingsCopy.WallpaperTypeFor("somethingNew"));
    }

    [Fact]
    public void RotationCountsReadAsTheMacsDo()
    {
        Assert.Equal("Never", SettingsCopy.RotationCount(0));
        Assert.Equal("1 song", SettingsCopy.RotationCount(1));
        Assert.Equal("3 songs in a row", SettingsCopy.RotationCount(3));
    }

    [Fact]
    public void ThePauseCaptionSaysWhenTheDotsAreOff()
    {
        Assert.DoesNotContain("The orb is off", SettingsCopy.PauseCaption(showInterludes: true));
        Assert.Contains("The orb is off", SettingsCopy.PauseCaption(showInterludes: false));
    }

    [Fact]
    public void SourcesMoveSwitchAndRestore()
    {
        var settings = new AppSettings();
        var rows = SettingsCopy.Sources(settings);
        Assert.Equal(LyricSourceInfo.All.Count, rows.Count);
        Assert.True(rows[0].IsFirst);
        Assert.True(rows[^1].IsLast);

        // Moving swaps with the neighbour, never past either end.
        var first = rows[0].Value;
        var second = rows[1].Value;
        var moved = SettingsCopy.MoveSource(settings, first, 1);
        Assert.Equal([second, first], SettingsCopy.Sources(moved).Take(2).Select(r => r.Value));
        Assert.Same(settings, SettingsCopy.MoveSource(settings, first, -1));
        Assert.Same(settings, SettingsCopy.MoveSource(settings, rows[^1].Value, 1));

        // Switching one off leaves it in the list, unticked, and out of the lookup.
        var off = SettingsCopy.SetSourceEnabled(settings, "lrclib", false);
        Assert.False(SettingsCopy.Sources(off).Single(r => r.Value == "lrclib").Enabled);
        Assert.DoesNotContain(LyricSourceId.Lrclib, off.SourceSnapshot().Enabled);
        Assert.Contains(LyricSourceId.Lrclib, SettingsCopy.SetSourceEnabled(off, "lrclib", true).SourceSnapshot().Enabled);

        // Restore puts back the recommended order and Accuracy first, keeping which are on.
        var restored = SettingsCopy.RestoreRecommendedOrder(moved with { LyricMatchMode = "priority", LyricSources = off.LyricSources });
        Assert.Equal(LyricSourceSnapshot.RecommendedOrder.Select(s => s.RawValue()), restored.LyricSourceOrder);
        Assert.Equal("smart", restored.LyricMatchMode);
        Assert.Equal(off.LyricSources, restored.LyricSources);
    }
}

/// <summary>Which entrances a layout shows.</summary>
public sealed class EntranceSupportTests
{
    // Ported from SettingsFollowupTests.swift: testStageLayoutsThatIgnoreEveryChosenMotion
    [Fact]
    public void TestStageLayoutsThatIgnoreEveryChosenMotion()
    {
        foreach (var layout in new[] { CompositionKind.Hinge, CompositionKind.Broadside, CompositionKind.Portal, CompositionKind.Mosaic })
        {
            foreach (var arrival in Enum.GetValues<WordArrival>())
            {
                var shown = arrival is WordArrival.Automatic or WordArrival.Mix or WordArrival.CutoutHighlight;
                Assert.Equal(shown, EntranceSupport.AppliesToSungWords(arrival, layout));
            }
        }
    }

    // Ported from SettingsFollowupTests.swift: testTorqueAndFableMatchTheRenderer (the parts that don't ask the Mac's
    // StageLyricView renderer; Windows' stage layers are checked against the same rule in their own render tests).
    [Fact]
    public void TestTorqueAndFableMatchTheRenderer()
    {
        foreach (var layout in new[] { CompositionKind.Torque, CompositionKind.Fable })
        {
            Assert.False(EntranceSupport.AppliesToSungWords(WordArrival.Fade, layout));
            Assert.False(EntranceSupport.AppliesToSungWords(WordArrival.SatinRibbon, layout));
            Assert.False(EntranceSupport.AppliesToSungWords(WordArrival.SplitInk, layout));
            Assert.True(EntranceSupport.AppliesToSungWords(WordArrival.Pop, layout));
            Assert.True(EntranceSupport.AppliesToSungWords(WordArrival.CutoutHighlight, layout));
        }
    }

    // Ported from SettingsFollowupTests.swift: testOtherLayoutsShowEveryEntrance
    [Fact]
    public void TestOtherLayoutsShowEveryEntrance()
    {
        var stage = new[] { CompositionKind.Hinge, CompositionKind.Broadside, CompositionKind.Portal, CompositionKind.Mosaic, CompositionKind.Torque, CompositionKind.Fable };
        foreach (var layout in Enum.GetValues<CompositionKind>().Except(stage))
        {
            foreach (var arrival in Enum.GetValues<WordArrival>())
            {
                Assert.True(EntranceSupport.AppliesToSungWords(arrival, layout), $"{layout} {arrival}");
                Assert.Equal(arrival.Title(), EntranceSupport.MenuTitle(arrival, layout));
            }
        }
    }

    // Ported from SettingsFollowupTests.swift: testTheMenuMarksAndExplainsALeftOutEntrance
    [Fact]
    public void TestTheMenuMarksAndExplainsALeftOutEntrance()
    {
        Assert.Equal("Satin Ribbon (whole lines only)", EntranceSupport.MenuTitle(WordArrival.SatinRibbon, CompositionKind.Hinge));
        Assert.Equal("Cutout · Highlight", EntranceSupport.MenuTitle(WordArrival.CutoutHighlight, CompositionKind.Hinge));
        // Custom Mix marks nothing.
        Assert.Equal("Satin Ribbon", EntranceSupport.MenuTitle(WordArrival.SatinRibbon, null));
        Assert.Null(EntranceSupport.Note(WordArrival.SatinRibbon, null, []));

        Assert.Equal("Torque moves each sung word its own way, so Split Ink plays only on lyrics timed by the line.",
            EntranceSupport.Note(WordArrival.SplitInk, CompositionKind.Torque, []));
        Assert.Null(EntranceSupport.Note(WordArrival.Pop, CompositionKind.Torque, [WordArrival.Fade]));
        Assert.Equal("Fable moves each sung word its own way, so Soft focus and Split Ink play only on lyrics timed by the line.",
            EntranceSupport.Note(WordArrival.Mix, CompositionKind.Fable, [WordArrival.Fade, WordArrival.Pop, WordArrival.SplitInk]));
        Assert.Null(EntranceSupport.Note(WordArrival.Mix, CompositionKind.Hinge, [WordArrival.CutoutHighlight]));
        Assert.Equal("A, B and C", EntranceSupport.List(["A", "B", "C"]));
    }

    [Fact]
    public void TheMenuMarksTheLayoutsItLeavesOut()
    {
        var hinge = SettingsCopy.Entrances("hinge");
        Assert.Equal("Soft focus (whole lines only)", hinge.Single(c => c.Value == "fade").Title);
        Assert.Equal("Soft focus", SettingsCopy.Entrances("automatic").Single(c => c.Value == "fade").Title);
    }
}

/// <summary>The window's command-line flags and the settings log line.</summary>
public sealed class SettingsWindowOptionsTests
{
    [Fact]
    public void ASettingsPageOpensSettingsAndTheThemeIsForThisRunOnly()
    {
        var options = Lunalyr.Shell.ShellOptions.Parse(["--settings-page", "timing", "--theme", "Dark"]);
        Assert.True(options.OpenSettings);
        Assert.Equal("timing", options.SettingsPage);
        Assert.Equal("dark", options.Theme);
        Assert.Empty(options.Problems);

        var bad = Lunalyr.Shell.ShellOptions.Parse(["--theme", "purple", "--settings-page"]);
        Assert.Null(bad.Theme);
        Assert.Null(bad.SettingsPage);
        Assert.Equal(2, bad.Problems.Count);
    }

    [Fact]
    public void TheLogNamesTheKeysThatChanged()
    {
        var before = new AppSettings();
        Assert.Empty(AppSettings.ChangedKeys(before, before with { }));
        Assert.Equal(["lyricComposition", "echoStrength"],
            AppSettings.ChangedKeys(before, before with { LyricComposition = "echo", EchoStrength = 0.5 }));
        Assert.Equal(["wordArrivalByStyle"], AppSettings.ChangedKeys(before, before.WithEntranceForControls("pop")));
    }
}

/// <summary>The settings window's live preview, drawn offscreen by the wallpaper's own layers (WARP: no GPU needed).</summary>
public sealed class SettingsPreviewRendererTests
{
    [Fact]
    public void ThePreviewDrawsTheSceneWithTheWallpapersLayers()
    {
        var song = Lunalyr.Shell.Demo.DemoSongs.Songs[0];
        var scene = new SceneSnapshot
        {
            Title = song.Title, Artist = song.Artist, Lyrics = song.Lyrics, Style = new AppSettings().Style(), TrackKey = "demo",
        };
        var withLyrics = Draw(scene, "gradient", "editorial", 15.5);
        var without = Draw(scene with { Lyrics = null }, "gradient", "editorial", 15.5);
        Assert.True(Coloured(without) > 320 * 180 / 2, "the album colour field should fill the frame");
        var changed = Differing(withLyrics, without);
        Assert.True(changed > 200, $"{changed} pixels differ: the lyrics should show");

        // Unknown ids fall back as the desktop does: the album colours and Editorial.
        Assert.Equal(withLyrics, Draw(scene, "visualizer.nope", "nope", 15.5));

        // Nothing playing: the background alone.
        Assert.True(Coloured(Draw(new SceneSnapshot(), "colourOnly", "echo", double.NaN, playing: false)) > 320 * 180 / 2);
    }

    // Ported from OpeningTitlePreviewTests.swift (b11a1ab3): the placed-row shots became card shots, stacked and one row.
    [Fact]
    public void TheFloatingPictureDrawsTheCardInItsBox()
    {
        var song = Lunalyr.Shell.Demo.DemoSongs.Songs[0];
        var style = new AppSettings { OpeningTitleAlways = true } .Style();
        var scene = new SceneSnapshot { Title = song.Title, Artist = song.Artist, Lyrics = song.Lyrics, Style = style, TrackKey = "demo" };
        const int width = 640, height = 360;
        // The picture stands for a 1512 × 982 point display: a 316 × 70 point card is stacked; at 31 points tall, one row.
        const float scale = height / 982f;
        var stackedBox = new SizeD(316, 70);
        var rowBox = new SizeD(316, 31);
        Assert.True(Lunalyr.Core.Floating.FloatingTitleFrame.Layout(stackedBox).Stacked);
        Assert.False(Lunalyr.Core.Floating.FloatingTitleFrame.Layout(rowBox).Stacked);
        using var renderer = new Lunalyr.Shell.Rendering.SettingsPreviewRenderer(width, height,
            Lunalyr.Shell.Rendering.WallpaperLayers.Factories(Lunalyr.Core.Lyrics.Readings.ReadingServices.Basic.WordBreaker), hardware: false);
        byte[] Picture(bool floating, Lunalyr.Shell.Rendering.SettingsPreviewRenderer.PictureCard? card)
        {
            var pixels = new byte[width * height * 4];
            renderer.Render(scene, "colourOnly", "editorial", 15.5, true, wallSeconds: 1, pixels, floating, card);
            return pixels;
        }
        Lunalyr.Shell.Rendering.SettingsPreviewRenderer.PictureCard Card(SizeD points) =>
            new(200, 230, (int)Math.Round(points.Width * scale), (int)Math.Round(points.Height * scale), scale, 44);

        var desktop = Picture(floating: false, null);
        var bare = Picture(floating: true, null);
        // Floating: the desktop's own title card is not drawn.
        Assert.True(Differing(desktop, bare) > 200, $"{Differing(desktop, bare)}");
        foreach (var points in new[] { stackedBox, rowBox })
        {
            var card = Card(points);
            var drawn = Picture(floating: true, card);
            // The card, inside its box and nowhere else.
            Assert.True(Differing(drawn, bare, card) > 20, $"{points}: {Differing(drawn, bare, card)}");
            Assert.Equal(Differing(drawn, bare), Differing(drawn, bare, card));
        }
    }

    /// <summary>Pixels that differ within a card's box on a 640-wide picture.</summary>
    private static int Differing(byte[] a, byte[] b, Lunalyr.Shell.Rendering.SettingsPreviewRenderer.PictureCard box)
    {
        var count = 0;
        for (var y = box.Y; y < box.Y + box.Height; y++)
        for (var x = box.X; x < box.X + box.Width; x++)
        {
            var i = (y * 640 + x) * 4;
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 30) count++;
        }
        return count;
    }

    private static byte[] Draw(SceneSnapshot scene, string background, string composition, double time, bool playing = true)
    {
        using var renderer = new Lunalyr.Shell.Rendering.SettingsPreviewRenderer(320, 180,
            Lunalyr.Shell.Rendering.WallpaperLayers.Factories(Lunalyr.Core.Lyrics.Readings.ReadingServices.Basic.WordBreaker), hardware: false);
        var pixels = new byte[320 * 180 * 4];
        renderer.Render(scene, background, composition, time, playing, wallSeconds: 1, pixels);
        return pixels;
    }

    private static int Differing(byte[] a, byte[] b)
    {
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 30) count++;
        return count;
    }

    private static int Coloured(byte[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 12) count++;
        return count;
    }
}
