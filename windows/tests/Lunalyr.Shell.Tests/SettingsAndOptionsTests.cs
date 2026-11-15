using System.Text.Json;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;

namespace Lunalyr.Shell.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void DefaultsMatchTheBrief()
    {
        var settings = new AppSettings();
        Assert.Equal("constellation", settings.LyricComposition);
        Assert.Equal("auto", settings.BackgroundMode);
        Assert.Equal(60, settings.AnimationRate);
        Assert.Equal(0, settings.LyricOffset);
        Assert.True(settings.PauseWhenCovered && settings.PauseOnBatterySaver && settings.PauseWhenFullScreen);
        Assert.Equal("below", settings.RomanizationDisplay);
    }

    [Fact]
    public void TheDefaultSourcesAreTheMacsFreshInstall()
    {
        // The Mac switches on everything but the local folder and Music's own lyrics, in its recommended order.
        var snapshot = new AppSettings().SourceSnapshot();
        Assert.Equal(LyricSourceSnapshot.RecommendedOrder.Where(s => s is not (LyricSourceId.AppleMusicCache or LyricSourceId.Lyrimuse)),
            snapshot.Enabled);
        Assert.Equal(LyricMatchMode.AccuracyFirst, snapshot.Mode);
    }

    [Fact]
    public void TheSourceOrderKeepsTheUsersOrderAndAddsNewSources()
    {
        var settings = new AppSettings { LyricSourceOrder = ["lrclib", "nonsense", "netEase"], LyricSources = ["netEase", "lrclib"] };
        var snapshot = settings.SourceSnapshot();
        Assert.Equal([LyricSourceId.Lrclib, LyricSourceId.NetEase], snapshot.Enabled);
    }

    [Fact]
    public async Task TheFileUsesTheDocumentedKeysAndRoundTrips()
    {
        using var temp = new TempFolder();
        var changed = new AppSettings { LyricComposition = "spoken", WordArrival = "lift", LyricOffset = -0.25, WallpaperPaused = true };
        await using (var store = AppSettingsStore.Open(temp.Path, Logger.Null))
        {
            store.Set(changed);
            Assert.True(await store.FlushAsync());
        }

        var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "settings.json"), TestContext.Current.CancellationToken)).RootElement;
        string[] keys =
        [
            "schemaVersion", "lyricComposition", "wordArrival", "lyricScale", "fontChoice", "fontFamily", "romanizationDisplay",
            "romanizationLanguage", "romanizationTones", "romanizationSaved", "backgroundMode", "wallpaperPaused", "animationRate",
            "pauseWhenCovered", "pauseOnBatterySaver", "pauseWhenFullScreen", "lyricOffset", "repairQQGaps", "songOffsets",
            "lyricSourceOrder", "lyricSources", "lyricMatchMode", "lyrimuseFolder", "artistAliases", "creditsLayer", "openingTitle", "openingTitleCover", "showInterludes",
            "lyricHighlightMode", "lyricHighlightCustom", "rotateLayouts", "rotateLayoutCounts", "rotateLayoutsCursor",
            "constellationSpread", "constellationLinkOpacity", "echoStrength", "wordEntranceMix", "wordArrivalByStyle", "appTheme",
            "showControlsOnHover", "desktopPlayerTimeMode", "player",
            "noLyrics", "showTranslations",
            "albumGradientWallpaper", "shaderStyle", "patternStyle", "wallpaperPath", "wallpaperName", "wallpaperPlacement",
            "effectSpeed", "vividness", "scrimStrength", "patternOpacity", "motionBlur", "textBackingAutomatic", "textBacking",
            "useMotionArtwork", "motionPlacement", "motionExclusions",
            "visualizerStyle", "audioReactiveEnabled", "audioSensitivity", "visualizerPunch", "starTrailsColours",
            // Performance › Low power visuals; Visualiser › Colours for Wavefield and Fosfora Aurora
            "lowPowerVisuals", "wavefieldColours", "wavecoreColours",
            "musicVideoFallback", "musicVideoOnline", "musicVideoSearch", "musicVideoDim", "musicVideoBlur",
            "musicVideoFraming", "libraryVideoFraming",
            "lyricsSurface", "floatingTextSize", "floatingBackdrop", "floatingFadeUnderPointer", "floatingPlacement", "floatingDisplay",
            "floatingFrames", "floatingSizes", "floatingLayoutSizes",
            // (hideLyricsWhileMirroring is written only once chosen; MirroringTests checks it)
            "lyricsHidden",
            // Discord status and the tray lyric (discordSongButton is written only once it is set)
            "discordPresence", "discordStatusText", "discordLyric", "discordCover", "discordButton", "discordProgress", "discordPaused",
            "discordLyricLead", "trayLyric", "trayLyricLength",
            "lyricCardComposition", "lyricCardLinesBefore", "lyricCardLinesAfter", "lyricCardAspect", "lyricCardFormats",
            "lyricCardBackgroundPath", "lyricCardUseSong", "lyricCardTextScale",
            // Updates and the first-run welcome (milestone 8)
            "automaticUpdateChecks", "onboardingDone", "onBattery",
            // Windows' Animation effects: followed or not, and the notice saying why nothing moves
            "motionFollowsWindows", "motionNoticeShown", "reduceFlicker",
            // Word symbols and action words (the Mac's custom.wordSymbols.* and custom.actionWords.*)
            "wordSymbolsEnabled", "wordSymbolsWords", "actionWordsEnabled", "actionWordsWords",
            // The last of the Lyric style page: backing vocals, typography, colour, word reveal and remembered styles
            "backingVocalsLayer", "backingVocalStyle", "editorialTypography", "editorialAmount", "randomizeLyricFonts",
            "lyricColourMode", "lyricColourSpeed", "lyricColourStrength", "lyricColourCustom", "wordRevealByStyle",
            "nextWordPreviewByStyle", "nextWordPreviewLead", "styleMemoryEnabled", "styleMemorySongs", "styleMemoryArtists",
        ];
        Assert.Equal(keys.Order(), json.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());

        await using var reopened = AppSettingsStore.Open(temp.Path, Logger.Null);
        Assert.Equal("spoken", reopened.Current.LyricComposition);
        Assert.Equal("lift", reopened.Current.WordArrival);
        Assert.Equal(-0.25, reopened.Current.LyricOffset);
        Assert.True(reopened.Current.WallpaperPaused);
    }

    // Ported from SpotifyPlayerTests.swift: testThePlayerChoiceIsRememberedUnderACustomKey (the key without "custom.",
    // as every key here; the Mac's publisher assertions are the controller's ApplySettings, tested in AppControllerTests).
    [Fact]
    public async Task ThePlayerChoiceIsRememberedUnderItsKey()
    {
        using var temp = new TempFolder();
        await using (var store = AppSettingsStore.Open(temp.Path, Logger.Null))
        {
            Assert.Equal("automatic", store.Current.Player);
            store.Update(s => s with { Player = "spotify" });
            Assert.True(await store.FlushAsync());
        }

        var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "settings.json"), TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("spotify", json.GetProperty("player").GetString());
        await using (var reopened = AppSettingsStore.Open(temp.Path, Logger.Null))
            Assert.Equal("spotify", reopened.Current.Player);

        await File.WriteAllTextAsync(Path.Combine(temp.Path, "settings.json"), """{ "schemaVersion": 1, "player": "winamp" }""",
            TestContext.Current.CancellationToken);
        await using (var unknown = AppSettingsStore.Open(temp.Path, Logger.Null))
            Assert.Equal("automatic", unknown.Current.Player);
    }

    // Windows only: the desktop player's Spotify search was removed (14 Nov 2026). A settings file from a build that had
    // it still loads, keeps everything else, and drops the old key on the next save.
    [Fact]
    public async Task AnOldSpotifyClientIdIsIgnoredAndDropped()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(file, """{ "schemaVersion": 1, "spotifyClientId": "0123456789abcdef0123456789abcdef", "player": "spotify" }""",
            TestContext.Current.CancellationToken);
        await using (var store = AppSettingsStore.Open(temp.Path, Logger.Null))
        {
            Assert.Equal("spotify", store.Current.Player);
            store.Update(s => s with { WallpaperPaused = true });
            Assert.True(await store.FlushAsync());
        }

        var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).RootElement;
        Assert.False(json.TryGetProperty("spotifyClientId", out _));
        Assert.Equal("spotify", json.GetProperty("player").GetString());
    }

    // Ported from LyricReviewFixesTests.swift: testTranslationsAreOnByDefaultAndPersist (the key without "custom."; the
    // renderer is told through the style). The Mac's last part, that cached lyrics saved before translations still
    // decode, was ported earlier in SavedLyricsTests.
    [Fact]
    public async Task TranslationsAreOnByDefaultAndPersist()
    {
        using var temp = new TempFolder();
        await using (var store = AppSettingsStore.Open(temp.Path, Logger.Null))
        {
            Assert.True(store.Current.ShowTranslations);
            Assert.True(store.Current.Style().Translations);
            store.Update(s => s with { ShowTranslations = false });
            Assert.True(await store.FlushAsync());
        }

        await using var reopened = AppSettingsStore.Open(temp.Path, Logger.Null);
        Assert.False(reopened.Current.ShowTranslations);
        Assert.False(reopened.Current.Style().Translations);
        // The field the parsers fill.
        var line = new LyricLine { Start = 1, End = 2, Text = "Hola", Translation = "Hello" };
        Assert.Equal("Hello", line.Translation);
        Assert.Equal("Hello", Core.Compositions.LineTranslations.Of(line with { Translation = "  Hello " }));
        Assert.Null(Core.Compositions.LineTranslations.Of(line with { Translation = "   " }));
    }

    [Fact]
    public async Task OutOfRangeValuesFromDiskAreRepaired()
    {
        using var temp = new TempFolder();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "settings.json"),
            """{ "schemaVersion": 1, "lyricComposition": "nope", "animationRate": 144, "lyricOffset": 9, "lyricScale": -3, "romanizationDisplay": "sideways" }""",
            TestContext.Current.CancellationToken);
        await using var store = AppSettingsStore.Open(temp.Path, Logger.Null);
        Assert.Equal("constellation", store.Current.LyricComposition);
        Assert.Equal(60, store.Current.AnimationRate);
        Assert.Equal(3, store.Current.LyricOffset);
        Assert.Equal(0.5, store.Current.LyricScale);
        Assert.Equal("below", store.Current.RomanizationDisplay);
    }

    [Fact]
    public void TheRendererGetsTheScenesRevealNames()
    {
        Assert.Equal("softFocus", new AppSettings { WordArrival = "fade" }.Style().Reveal);
        Assert.Equal("floatUp", new AppSettings { WordArrival = "lift" }.Style().Reveal);
        Assert.Equal("pop", new AppSettings { WordArrival = "pop" }.Style().Reveal);
        Assert.Equal(ReadingPlacement.Instead, new AppSettings { RomanizationDisplay = "replace" }.Style().Readings);
        Assert.Equal("Bahnschrift", new AppSettings { FontChoice = "installed", FontFamily = "Bahnschrift" }.Style().FontFamily);
        Assert.Equal(LyricStyle.Default.FontFamily, new AppSettings { FontChoice = "installed" }.Style().FontFamily);
    }
}

public sealed class ShellOptionsTests
{
    [Fact]
    public void ReadsEveryFlag()
    {
        var options = ShellOptions.Parse(["--no-playback", "--data", "C:\\temp\\x", "--seconds", "20", "--log-level", "debug", "--settings", "--no-network", "lunalyr://open"]);
        Assert.True(options.NoPlayback);
        Assert.Equal(Path.GetFullPath("C:\\temp\\x"), options.DataRoot);
        Assert.Equal(20, options.QuitAfterSeconds);
        Assert.Equal(LogLevel.Debug, options.LogLevel);
        Assert.True(options.OpenSettings);
        Assert.True(options.NoNetwork);
        Assert.Equal(["lunalyr://open"], options.Links);
        Assert.Empty(options.Problems);
    }

    [Fact]
    public void NoArgumentsMeansTheDefaults()
    {
        var options = ShellOptions.Parse([]);
        Assert.False(options.NoPlayback);
        Assert.Null(options.DataRoot);
        Assert.Null(options.QuitAfterSeconds);
        Assert.Equal(LogLevel.Info, options.LogLevel);
    }

    [Fact]
    public void BadArgumentsAreReportedNotFatal()
    {
        var options = ShellOptions.Parse(["--seconds", "-4", "--log-level", "loud", "--frobnicate", "--data"]);
        Assert.Null(options.QuitAfterSeconds);
        Assert.Equal(LogLevel.Info, options.LogLevel);
        Assert.Equal(4, options.Problems.Count);
    }

    [Fact]
    public void AValueCannotBeTheNextFlag()
    {
        var options = ShellOptions.Parse(["--data", "--no-playback"]);
        Assert.Null(options.DataRoot);
        Assert.True(options.NoPlayback);
    }
}

public sealed class CrashReportTests
{
    [Fact]
    public void WritesACrashFileIntoTheLogsFolder()
    {
        using var temp = new TempFolder();
        var logs = Path.Combine(temp.Path, "Logs");
        Exception thrown;
        try
        {
            throw new InvalidOperationException("Something broke");
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        var time = new DateTimeOffset(2026, 11, 12, 9, 30, 15, 123, TimeSpan.FromHours(11));
        var first = CrashReport.Write(logs, thrown, "AppDomain.UnhandledException", "0.1.0", time);
        var second = CrashReport.Write(logs, thrown, "AppDomain.UnhandledException", "0.1.0", time);

        Assert.Equal(Path.Combine(logs, "crash-2026-11-12T09-30-15-123.txt"), first);
        Assert.NotEqual(first, second);
        var text = File.ReadAllText(first!);
        Assert.Contains("Something broke", text);
        Assert.Contains("AppDomain.UnhandledException", text);
        Assert.Contains("Version: 0.1.0", text);
    }

    [Fact]
    public void NeverThrows()
    {
        Assert.Null(CrashReport.Write("\0bad", new Exception("x"), "test", "0"));
    }
}
