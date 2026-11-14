using System.Text.Json;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// The last of the Lyric style page's settings: the text colour, word reveal and preview, Editorial typography, backing
/// vocals and remembered styles. Ported from the Mac's LyricColourTests, TypographySettingsTests,
/// SettingsFollowupTests, NextWordPreviewTests, WorkspaceCopyTests, LyricExtrasTests and SettingsPolishTests (their
/// settings halves). Settings live in memory only.
/// </summary>
public class LyricStyleExtrasTests
{
    private static AppSettings Relaunched(AppSettings s) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(s, AppSettingsJsonContext.Default.AppSettings),
            AppSettingsJsonContext.Default.AppSettings)!.Normalized();

    private static AppSettings FromJson(string json) =>
        JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!.Normalized();

    private static readonly LyricQuery Song = new("Midnight Rain", "Taylor Swift", "Midnights", 174);
    private static readonly LyricQuery SameArtist = new("Anti-Hero", "Taylor Swift", "Midnights", 200);
    private static readonly LyricQuery Other = new("Hello", "Adele", "25", 295);

    private static (RememberedStyle Style, StyleScope Scope)? Remembered(AppSettings s, LyricQuery song) =>
        StyleMemory.Remembered(s, song, song.Artist);

    // ---- Colour ----

    // Ported from LyricColourTests.swift: testColourSettingsPersistInIsolatedDefaults (the settings half; the rules are
    // in Lunalyr.Core.Tests' LyricColourTests)
    [Fact]
    public void ColourSettingsPersistInIsolatedDefaults()
    {
        var options = new AppSettings();
        Assert.Equal(LyricColourMode.AlbumDrift, options.ColourMode);
        Assert.DoesNotContain(LyricColourMode.Standard, LyricColourModes.Choices);
        Assert.Equal("albumDrift", FromJson("""{ "lyricColourMode": "static" }""").LyricColourMode);
        Assert.Equal(LyricColourMode.AlbumDrift, new RememberedStyle(null, "static").ColourMode);
        options = options with { LyricColourMode = "customGradient" };
        Assert.Equal(1, options.LyricColourStrength);
        options = options with { LyricColourStrength = 1.6, LyricColourSpeed = 1.5 };
        options = options.WithCustomColour(0, "#FF0000").WithCustomColourAdded().WithCustomColourAdded().WithCustomColourAdded();
        Assert.Equal(4, options.LyricColourCustom.Count); // at most four colours
        var reloaded = Relaunched(options);
        Assert.Equal(LyricColourMode.CustomGradient, reloaded.ColourMode);
        Assert.Equal(1.5, reloaded.LyricColourSpeed);
        Assert.Equal(1.6, reloaded.LyricColourStrength);
        Assert.Equal("#FF0000", reloaded.LyricColourCustom[0]);
        Assert.Equal(4, reloaded.Style().ColourCustom.Count);
        reloaded = reloaded.WithCustomColourRemoved(0).WithCustomColourRemoved(0).WithCustomColourRemoved(0);
        Assert.Equal(2, reloaded.LyricColourCustom.Count); // at least two colours
        var repaired = (FromJson("""{ "lyricColourCustom": ["nope", "#12345"], "lyricColourStrength": -1 }""") with { LyricColourSpeed = double.NaN }).Normalized();
        Assert.Equal(2, repaired.LyricColourCustom.Count);
        Assert.Equal(1, repaired.LyricColourSpeed);
        Assert.Equal(0, repaired.LyricColourStrength);
    }

    [Fact]
    public void TheColourReachesTheRenderersStyle()
    {
        var style = new AppSettings { LyricColourMode = "sungSweep", LyricColourSpeed = 0.5, LyricColourStrength = 1.4 }.Style();
        Assert.Equal(LyricColourMode.SungSweep, style.ColourMode);
        Assert.Equal(0.5, style.ColourSpeed);
        Assert.Equal(1.4, style.ColourStrength);
        // The defaults draw what this build drew before: Album drift at normal speed and strength, every word revealed and
        // previewed, backing vocals behind the lead, one typeface.
        var defaults = new AppSettings().Style();
        Assert.Equal(LyricColourMode.AlbumDrift, defaults.ColourMode);
        Assert.Equal((1.0, 1.0, 0.45), (defaults.ColourSpeed, defaults.ColourStrength, defaults.PreviewLead));
        Assert.Empty(defaults.NoWordReveal);
        Assert.Empty(defaults.NoNextWordPreview);
        Assert.True(defaults.BackingVocals);
        Assert.False(defaults.EditorialTypography || defaults.RandomFonts);
        Assert.Equal(Lunalyr.Render.Scene.LyricStyle.Default.ColourCustom, defaults.ColourCustom);
    }

    // ---- Word reveal and preview ----

    // Ported from TypographySettingsTests.swift: testRevealLockMatchesWhatTheRendererRequires
    [Fact]
    public void RevealLockMatchesWhatTheRendererRequires()
    {
        foreach (var id in AppSettings.Compositions.Where(id => id != AppSettings.AllLayoutsKey))
        {
            var style = SettingsCopy.Kind(id)!.Value;
            foreach (var arrival in AppSettings.WordArrivals)
            {
                foreach (var mix in new[] { new[] { "fade" }, new[] { "cutoutFold", "pop" } })
                {
                    var settings = new AppSettings
                    {
                        LyricComposition = id, WordArrivalByStyle = new Dictionary<string, string> { [id] = arrival }, WordEntranceMix = mix,
                    };
                    var selected = settings.ArrivalIn(style);
                    // The rule as it stood before the lock was given reasons, less the entrances a chosen stage layout
                    // leaves out.
                    bool Unveils(WordArrival a) => a.RequiresReveal() && EntranceSupport.AppliesToSungWords(a, style);
                    var required = style.ArrivalOnly() || Unveils(selected)
                        || (selected == WordArrival.Mix && mix.Any(m => Unveils(WordArrivals.Parse(m))));
                    Assert.True(settings.RequiresWordEntrances(style) == required, $"{id} {arrival} {string.Join(",", mix)}");
                    Assert.Equal(required, settings.RevealLock(style) is not null);
                }
            }
        }
        var scattered = new AppSettings
        {
            LyricComposition = "scattered", WordArrivalByStyle = new Dictionary<string, string> { ["scattered"] = "mix" },
            WordEntranceMix = ["splitInk"],
        };
        Assert.Equal(WordRevealLock.Mix, scattered.RevealLock(CompositionKind.Scattered));
        scattered = scattered with { WordArrivalByStyle = new Dictionary<string, string> { ["scattered"] = "satinRibbon" } };
        Assert.Equal(WordRevealLock.ForEntrance(WordArrival.SatinRibbon), scattered.RevealLock(CompositionKind.Scattered));
        Assert.Equal(WordRevealLock.ForLayout(CompositionKind.Cutout), scattered.RevealLock(CompositionKind.Cutout));
    }

    // Ported from SettingsFollowupTests.swift: testALeftOutEntranceDoesNotLockReveal
    [Fact]
    public void ALeftOutEntranceDoesNotLockReveal()
    {
        static AppSettings With(string layout, string arrival, params string[] mix) => new()
        {
            LyricComposition = layout, WordArrivalByStyle = new Dictionary<string, string> { [layout] = arrival },
            WordEntranceMix = mix.Length > 0 ? mix : new AppSettings().WordEntranceMix,
        };
        Assert.Null(With("hinge", "satinRibbon").RevealLock(CompositionKind.Hinge));
        Assert.Null(With("hinge", "mix", "cutoutFold", "splitInk").RevealLock(CompositionKind.Hinge));

        Assert.Null(With("torque", "splitInk").RevealLock(CompositionKind.Torque));
        Assert.Equal(WordRevealLock.ForEntrance(WordArrival.CutoutFold), With("torque", "cutoutFold").RevealLock(CompositionKind.Torque)); // Torque does fold its words
        Assert.Equal(WordRevealLock.Mix, With("torque", "mix", "cutoutFold", "splitInk").RevealLock(CompositionKind.Torque));

        Assert.Equal(WordRevealLock.ForEntrance(WordArrival.SatinRibbon), With("scattered", "satinRibbon").RevealLock(CompositionKind.Scattered));

        // Custom Mix's entrance is for every layout it picks: it locks in a stage layout too, as before.
        var automatic = With(AppSettings.AllLayoutsKey, "satinRibbon");
        Assert.Equal(WordRevealLock.ForEntrance(WordArrival.SatinRibbon), automatic.RevealLock(CompositionKind.Hinge));
    }

    // Ported from NextWordPreviewTests.swift: testAutomaticCutoutAppliesToAllResolvedLayouts (the settings half: the
    // entrance and the lock reach every layout Custom Mix picks; WordPresentation's half is in Core's tests)
    [Fact]
    public void AutomaticCutoutAppliesToAllResolvedLayouts()
    {
        var settings = new AppSettings { LyricComposition = AppSettings.AllLayoutsKey }
            .WithEntranceForControls("cutoutSlide").WithRevealsWordsForControls(false);
        var style = settings.Style();
        foreach (var id in AppSettings.Compositions.Where(id => id != AppSettings.AllLayoutsKey))
        {
            var kind = SettingsCopy.Kind(id)!.Value;
            Assert.Equal(WordArrival.CutoutSlide, settings.ArrivalIn(kind));
            Assert.Equal("cutoutSlide", style.RevealFor(id));
            // Cutout · Slide unveils each word as it is sung, so the switch can't take the reveal away.
            Assert.True(style.RevealOptionsFor(kind).RevealsWords, id);
        }
    }

    [Fact]
    public void RevealAndPreviewFollowTheLayoutTheyAreSetFor()
    {
        var constellation = new AppSettings { LyricComposition = "constellation" }.WithRevealsWordsForControls(false).WithPreviewsNextWordForControls(false);
        Assert.False(constellation.RevealsWords(CompositionKind.Constellation));
        Assert.True(constellation.RevealsWords(CompositionKind.Scattered)); // a layout without an entry reveals its words
        var style = constellation.Style();
        Assert.False(style.RevealOptionsFor(CompositionKind.Constellation).RevealsWords);
        Assert.False(style.RevealOptionsFor(CompositionKind.Constellation).PreviewsNextWord);
        Assert.True(style.RevealOptionsFor(CompositionKind.Echo).PreviewsNextWord);
        // Spoken builds its lines from the sung words: always revealed.
        var spoken = new AppSettings { LyricComposition = "spoken" }.WithRevealsWordsForControls(false);
        Assert.True(spoken.RevealsWords(CompositionKind.Spoken));
        Assert.Equal(WordRevealLock.ForLayout(CompositionKind.Spoken), spoken.RevealLockForControls());
        // Custom Mix's own entry is for every layout it picks.
        var all = new AppSettings { LyricComposition = AppSettings.AllLayoutsKey }.WithPreviewsNextWordForControls(false);
        Assert.All(AppSettings.Compositions.Where(id => id != AppSettings.AllLayoutsKey),
            id => Assert.False(all.PreviewsNextWord(SettingsCopy.Kind(id)!.Value)));
        // The lead is one for all, held to 0.15–1.25 s.
        Assert.Equal(1.25, FromJson("""{ "nextWordPreviewLead": 9 }""").NextWordPreviewLead);
        Assert.Equal(0.6, new AppSettings { NextWordPreviewLead = 0.6 }.Style().PreviewLead);
    }

    // Ported from WorkspaceCopyTests.swift: testEveryRevealLockIsExplained
    [Fact]
    public void EveryRevealLockIsExplained()
    {
        var locks = Enum.GetValues<CompositionKind>().Select(WordRevealLock.ForLayout)
            .Concat(Enum.GetValues<WordArrival>().Select(WordRevealLock.ForEntrance)).Append(WordRevealLock.Mix);
        foreach (var revealLock in locks)
            Assert.NotNull(LyricStyleCopy.RevealLockReason(revealLock));
        Assert.Contains(CompositionKind.Cascade.Title(), LyricStyleCopy.RevealLockReason(WordRevealLock.ForLayout(CompositionKind.Cascade)));
        Assert.Contains(WordArrival.SplitInk.Title(), LyricStyleCopy.RevealLockReason(WordRevealLock.ForEntrance(WordArrival.SplitInk)));
        Assert.Null(LyricStyleCopy.RevealLockReason(null));
    }

    // ---- Typography ----

    // Ported from WorkspaceCopyTests.swift: testEditorialSwitchIsExplainedWhereTheLayoutAlreadyMixesFaces
    [Fact]
    public void EditorialSwitchIsExplainedWhereTheLayoutAlreadyMixesFaces()
    {
        // Editorial typography is built into these, so the note explains the switch rather than leaving it to do nothing visible.
        foreach (var layout in Enum.GetValues<CompositionKind>())
            Assert.True((LyricStyleCopy.EditorialNote(layout) is not null) == layout.HasEditorialTypography(), layout.Title());
        Assert.NotNull(LyricStyleCopy.EditorialNote(null)); // Custom Mix can pick Editorial
    }

    [Fact]
    public void TypographyAndBackingReachTheRenderersStyle()
    {
        var style = new AppSettings
        {
            EditorialTypography = true, EditorialAmount = 0.4, RandomizeLyricFonts = true, BackingVocalsLayer = false, BackingVocalStyle = "echo",
        }.Style();
        Assert.True(style.EditorialTypography);
        Assert.Equal(0.4, style.EditorialAmount);
        Assert.True(style.RandomFonts);
        Assert.False(style.BackingVocals);
        Assert.Equal("echo", style.BackingStyle);
        // Only the layouts that set words loose or link them can carry backing words.
        Assert.Equal("", FromJson("""{ "backingVocalStyle": "spoken" }""").BackingVocalStyle);
        Assert.Null(new AppSettings().Style().BackingStyle);
        Assert.Equal(1, FromJson("""{ "editorialAmount": 4 }""").EditorialAmount);
    }

    // ---- Remembered styles ----

    // Ported from LyricExtrasTests.swift: testStyleMemoryPrefersTheSongThenItsArtistAndPersists
    [Fact]
    public void StyleMemoryPrefersTheSongThenItsArtistAndPersists()
    {
        var memory = new AppSettings();
        Assert.Null(Remembered(memory, Song));
        memory = StyleMemory.Remember(memory, new RememberedStyle("echo", "albumDrift"), Song, StyleScope.Artist);
        Assert.Equal(CompositionKind.Echo, Remembered(memory, SameArtist)?.Style.Layout); // the artist's style applies to all their songs
        Assert.Equal(StyleScope.Artist, Remembered(memory, SameArtist)?.Scope);
        memory = StyleMemory.Remember(memory, new RememberedStyle("cutout", "sungSweep"), Song, StyleScope.Song);
        Assert.Equal(CompositionKind.Cutout, Remembered(memory, Song)?.Style.Layout); // a song's own style wins
        Assert.Equal(LyricColourMode.SungSweep, Remembered(memory, Song)?.Style.ColourMode);
        Assert.Equal(CompositionKind.Echo, Remembered(memory, SameArtist)?.Style.Layout);
        Assert.Null(Remembered(memory, Other));
        var reloaded = Relaunched(memory);
        Assert.Equal(CompositionKind.Cutout, Remembered(reloaded, Song)?.Style.Layout);
        Assert.Equal(LyricColourMode.AlbumDrift, Remembered(reloaded, SameArtist)?.Style.ColourMode);
        // Names are not stored in the clear.
        var stored = JsonSerializer.Serialize(reloaded, AppSettingsJsonContext.Default.AppSettings);
        Assert.DoesNotContain("Taylor", stored);
        Assert.DoesNotContain("Midnight", stored);
    }

    // Ported from LyricExtrasTests.swift: testStyleMemoryCanBeSwitchedOffAndForgotten
    [Fact]
    public void StyleMemoryCanBeSwitchedOffAndForgotten()
    {
        var memory = StyleMemory.Remember(new AppSettings(), new RememberedStyle("automatic", null), Song, StyleScope.Song);
        Assert.NotNull(Remembered(memory, Song));
        memory = memory with { StyleMemoryEnabled = false };
        Assert.Null(Remembered(memory, Song));
        memory = memory with { StyleMemoryEnabled = true };
        memory = StyleMemory.Forget(memory, Song, StyleScope.Song);
        Assert.Null(Remembered(memory, Song));
        memory = StyleMemory.Remember(memory, new RememberedStyle("spoken", null), Other, StyleScope.Artist);
        memory = StyleMemory.ForgetAll(memory);
        Assert.Null(Remembered(Relaunched(memory), Other));
        Assert.Equal("Custom Mix · Album drift", StyleMemory.Describe(new RememberedStyle("automatic", "albumDrift")));
    }

    // Ported from LyricExtrasTests.swift: testARememberedStyleIsNamedUnderThePickerItOverrides
    [Fact]
    public void ARememberedStyleIsNamedUnderThePickerItOverrides()
    {
        var both = new RememberedStyle("echo", "albumDrift");
        Assert.Equal("Echo", StyleMemory.RememberedName(StyleAspect.Layout, both));
        Assert.Equal("Album drift", StyleMemory.RememberedName(StyleAspect.Colour, both));
        var layoutOnly = new RememberedStyle("automatic", null);
        Assert.Equal("Custom Mix", StyleMemory.RememberedName(StyleAspect.Layout, layoutOnly));
        Assert.Null(StyleMemory.RememberedName(StyleAspect.Colour, layoutOnly)); // the colour picker is not overridden
    }

    // Ported from SettingsPolishTests.swift: testForgetAllCanBeUndone (the snapshot half: everything forgotten comes back
    // as it was and is saved; Windows' settings window has no Edit › Undo, so the undo manager's half is not ported)
    [Fact]
    public void ForgetAllCanBeUndone()
    {
        var memory = StyleMemory.Remember(new AppSettings(), new RememberedStyle("echo", null), Other, StyleScope.Song);
        memory = StyleMemory.Remember(memory, new RememberedStyle(null, "pastel"), Other, StyleScope.Artist);
        var before = (memory.StyleMemorySongs, memory.StyleMemoryArtists);
        var forgotten = StyleMemory.ForgetAll(memory);
        Assert.Empty(forgotten.StyleMemorySongs);
        Assert.Empty(forgotten.StyleMemoryArtists);
        var restored = Relaunched(forgotten with { StyleMemorySongs = before.StyleMemorySongs, StyleMemoryArtists = before.StyleMemoryArtists });
        Assert.Single(restored.StyleMemorySongs);
        Assert.Single(restored.StyleMemoryArtists);
    }

    [Fact]
    public void ForgettingOnePartKeepsTheOther()
    {
        var memory = StyleMemory.Remember(new AppSettings(), new RememberedStyle("echo", "sungSweep"), Song, StyleScope.Song);
        var active = Remembered(memory, Song)!.Value;
        memory = StyleMemory.ForgetPart(memory, Song, StyleScope.Song, active.Style, StyleAspect.Layout);
        Assert.Equal(new RememberedStyle(null, "sungSweep"), Remembered(memory, Song)?.Style);
        memory = StyleMemory.ForgetPart(memory, Song, StyleScope.Song, Remembered(memory, Song)!.Value.Style, StyleAspect.Colour);
        Assert.Null(Remembered(memory, Song));
        Assert.Equal("1 song · 2 artists remembered", StyleMemory.RememberedCount(1, 2));
    }

    /// <summary>
    /// Windows' own: what the music playing sees. A remembered layout and colour replace the chosen ones (a remembered
    /// "automatic" means Custom Mix over a chosen layout), a part left to the settings keeps the chosen one, and
    /// nothing remembered changes nothing.
    /// </summary>
    [Fact]
    public void ARememberedStyleReplacesTheChosenLayoutAndColour()
    {
        var chosen = new AppSettings { LyricComposition = "spoken", LyricColourMode = "sungSweep", EditorialTypography = true };
        Assert.Same(chosen, StyleMemory.Applied(chosen, null));
        var both = StyleMemory.Applied(chosen, new RememberedStyle("automatic", "customGradient"));
        Assert.Equal(("automatic", LyricColourMode.CustomGradient), (both.LyricComposition, both.ColourMode));
        Assert.True(both.EditorialTypography);
        var colourOnly = StyleMemory.Applied(chosen, new RememberedStyle(null, "musicReactive"));
        Assert.Equal(("spoken", LyricColourMode.MusicReactive), (colourOnly.LyricComposition, colourOnly.ColourMode));
        var unknown = StyleMemory.Applied(chosen, new RememberedStyle("nonsense", "nonsense"));
        Assert.Equal(("spoken", LyricColourMode.SungSweep), (unknown.LyricComposition, unknown.ColourMode));
        Assert.Equal(LyricColourMode.CustomGradient, both.Style().ColourMode);
    }

    /// <summary>
    /// A layout taken out of Custom Mix stays out even for music it was remembered for; the colour still applies (the
    /// Mac's rule, next/tester-fixes, 14 Nov 2026).
    /// </summary>
    [Fact]
    public void ALayoutTakenOutOfCustomMixIsNotBroughtBackByMemory()
    {
        var mix = new AppSettings
        {
            LyricComposition = "automatic", RotateLayouts = true,
            RotateLayoutCounts = new Dictionary<string, int> { ["echo"] = 0, ["cascade"] = 2 },
        };
        var applied = StyleMemory.Applied(mix, new RememberedStyle("echo", "customGradient"));
        Assert.Equal("automatic", applied.LyricComposition);
        Assert.Equal("customGradient", applied.LyricColourMode);
        Assert.Equal("cascade", StyleMemory.Applied(mix, new RememberedStyle("cascade", null)).LyricComposition);
        // With the rotation off, or a layout chosen, the memory wins as before.
        Assert.Equal("echo", StyleMemory.Applied(mix with { RotateLayouts = false }, new RememberedStyle("echo", null)).LyricComposition);
        Assert.Equal("echo", StyleMemory.Applied(mix with { LyricComposition = "spoken" }, new RememberedStyle("echo", null)).LyricComposition);
    }

    /// <summary>Editorial and Constellation + Editorial were cut (the Mac's next/tester-fixes): saved values move on.</summary>
    [Fact]
    public void TheCutLayoutsMoveOnWhereverTheyAreSaved()
    {
        var saved = new AppSettings
        {
            LyricComposition = "constellationEditorial", BackingVocalStyle = "editorial", LyricCardComposition = "editorial",
            RotateLayoutCounts = new Dictionary<string, int> { ["editorial"] = 3, ["echo"] = 2 },
        }.Normalized();
        Assert.Equal("constellation", saved.LyricComposition);
        Assert.Equal("scattered", saved.BackingVocalStyle);
        Assert.Equal("scattered", saved.CardComposition);
        Assert.Equal(["echo"], saved.RotateLayoutCounts.Keys);
        Assert.Equal("constellation", new AppSettings().LyricComposition);
        Assert.Equal("hinge", new AppSettings { LyricComposition = "broadside" }.Normalized().LyricComposition);
        var remembered = RememberedStyle.FromStored(new Dictionary<string, string> { ["composition"] = "editorial" })!;
        Assert.Equal("scattered", remembered.Composition);
        // The typeface is remembered too; older styles without it leave the chosen one.
        var chosen = new AppSettings { FontChoice = "installed", FontFamily = "Georgia" };
        var kept = RememberedStyle.FromStored(StyleMemory.Chosen(chosen).Stored())!;
        Assert.Equal(("installed", "Georgia"), (kept.Font, kept.FontFamily));
        Assert.Equal("Georgia", StyleMemory.Applied(new AppSettings(), kept).FontFamily);
        Assert.Equal("Georgia", StyleMemory.Applied(chosen, remembered).FontFamily);
    }
}
