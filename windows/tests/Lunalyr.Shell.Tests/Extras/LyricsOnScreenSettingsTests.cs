using System.Text.Json;
using Lunalyr.Shell.Extras;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests.Extras;

/// <summary>Hide lyrics is kept; Windows has no global keyboard shortcuts, so an older file's shortcut keys are dropped.</summary>
public sealed class LyricsOnScreenSettingsTests
{
    // Ported from LyricExtrasTests.swift: testFeaturePreferencesPersistInIsolatedDefaults (the part Windows has: Hide
    // lyrics is kept), with the Mac's key without "custom.".
    [Fact]
    public void TestFeaturePreferencesPersistInIsolatedDefaults()
    {
        Assert.False(new AppSettings().LyricsHidden);
        var json = JsonSerializer.Serialize(new AppSettings { LyricsHidden = true }, AppSettingsJsonContext.Default.AppSettings);
        Assert.Contains("\"lyricsHidden\": true", json, StringComparison.Ordinal);
        Assert.True(JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!.Normalized().LyricsHidden);
    }

    [Fact]
    public void AnOlderFilesShortcutKeysAreReadPastAndNotWrittenAgain()
    {
        const string older = """{ "lyricsHidden": true, "globalShortcuts": true, "globalShortcutModifiers": "controlShiftCommand", "globalShortcutsOff": ["copyCard"] }""";
        var read = JsonSerializer.Deserialize(older, AppSettingsJsonContext.Default.AppSettings)!.Normalized();
        Assert.True(read.LyricsHidden);
        var written = JsonSerializer.Serialize(read, AppSettingsJsonContext.Default.AppSettings);
        Assert.DoesNotContain("globalShortcut", written, StringComparison.Ordinal);
    }

    [Fact]
    public void TestTheCardShortcutSaysItOpensTheCard() => Assert.Equal("Open lyric card", HotkeyAction.CopyCard.Title());
}
