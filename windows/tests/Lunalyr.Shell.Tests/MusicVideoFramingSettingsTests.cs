using System.Text.Json;
using Lunalyr.Core.Logging;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;

namespace Lunalyr.Shell.Tests;

// Ported from MusicVideoFramingTests.swift (Test Build 88), the setting's half: "Fill the screen" or "Show the whole video",
// for YouTube videos (musicVideoFraming) and video files (libraryVideoFraming, the Mac's library videos). A temp folder
// stands in for the Mac's UUID defaults suite; nothing loads.
public sealed class MusicVideoFramingSettingsTests
{
    // Ported from MusicVideoFramingTests.swift: testDefaultsToFillSoNothingChanges (Windows writes every key, so the
    // default is checked in the file rather than as an absent key)
    [Fact]
    public async Task DefaultsToFillSoNothingChanges()
    {
        var settings = new AppSettings();
        Assert.Equal(MusicVideoFraming.Fill, settings.YouTubeFraming);
        Assert.Equal(MusicVideoFraming.Fill, settings.FileFraming);
        using var temp = new TempFolder();
        await using (var store = AppSettingsStore.Open(temp.Path, Logger.Null))
        {
            store.Set(new AppSettings { LyricOffset = 0.5 });
            Assert.True(await store.FlushAsync());
        }
        var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "settings.json"), TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("fill", json.GetProperty(MusicVideoFramings.YouTubeKey).GetString());
        Assert.Equal("fill", json.GetProperty(MusicVideoFramings.LibraryKey).GetString());
        Assert.False(json.TryGetProperty("youTubeFraming", out _)); // the computed framings are not written
        Assert.False(json.TryGetProperty("fileFraming", out _));
    }

    // Ported from MusicVideoFramingTests.swift: testEachChoiceIsRememberedOnItsOwn
    [Fact]
    public async Task EachChoiceIsRememberedOnItsOwn()
    {
        using var temp = new TempFolder();
        await using (var store = AppSettingsStore.Open(temp.Path, Logger.Null))
        {
            store.Set(store.Current with { MusicVideoFraming = "whole" });
            Assert.True(await store.FlushAsync());
        }
        await using (var reloaded = AppSettingsStore.Open(temp.Path, Logger.Null))
        {
            Assert.Equal(MusicVideoFraming.Whole, reloaded.Current.YouTubeFraming);
            Assert.Equal(MusicVideoFraming.Fill, reloaded.Current.FileFraming); // video files keep their own choice
            reloaded.Set(reloaded.Current with { LibraryVideoFraming = "whole", MusicVideoFraming = "fill" });
            Assert.True(await reloaded.FlushAsync());
        }
        await using var again = AppSettingsStore.Open(temp.Path, Logger.Null);
        Assert.Equal(MusicVideoFraming.Fill, again.Current.YouTubeFraming);
        Assert.Equal(MusicVideoFraming.Whole, again.Current.FileFraming);
    }

    // Ported from MusicVideoFramingTests.swift: testUnknownStoredValuesFallBackToFill (the file's half; the stored-value
    // half is in Lunalyr.Core.Tests' MusicVideoFramingTests)
    [Fact]
    public async Task UnknownStoredValuesFallBackToFill()
    {
        using var temp = new TempFolder();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "settings.json"),
            "{\"schemaVersion\":1,\"musicVideoFraming\":\"bogus\",\"libraryVideoFraming\":\"whole\"}", TestContext.Current.CancellationToken);
        await using var store = AppSettingsStore.Open(temp.Path, Logger.Null);
        Assert.Equal("fill", store.Current.MusicVideoFraming);
        Assert.Equal(MusicVideoFraming.Fill, store.Current.YouTubeFraming);
        Assert.Equal(MusicVideoFraming.Whole, store.Current.FileFraming);
        Assert.Equal("fill", (new AppSettings { LibraryVideoFraming = "Whole" }).Normalized().LibraryVideoFraming);
    }
}
