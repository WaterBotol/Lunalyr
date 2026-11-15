using Lunalyr.Core.Logging;
using Lunalyr.Core.Visualisers;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Visualisers;

namespace Lunalyr.Shell.Tests;

/// <summary>Visualiser › Colours for Wavefield and Fosfora Aurora: the Mac's keys, defaults and their reading.</summary>
public sealed class VisualiserColourSettingsTests
{
    // Ported from VisualizerSafetyTests.swift: testVisualiserPreferencesDefaultToTheShippedLookAndPersist (the colours;
    // punch is SettingsAndOptionsTests' and VisualiserSettingsTests'; heavyScenesOnMainDisplayOnly and Light 3D belong to
    // the web scenes, which Windows doesn't have)
    [Fact]
    public async Task VisualiserColoursDefaultToTheShippedLookAndPersist()
    {
        var defaults = new AppSettings();
        Assert.Equal("album", defaults.WavefieldColours);
        Assert.Equal("album", defaults.WavecoreColours);
        var folder = Path.Combine(Path.GetTempPath(), "lunalyr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using (var store = AppSettingsStore.Open(folder, Logger.Null))
            {
                store.Set(new AppSettings { WavefieldColours = "original" });
                Assert.True(await store.FlushAsync());
            }
            await using (var reloaded = AppSettingsStore.Open(folder, Logger.Null))
            {
                Assert.Equal("original", reloaded.Current.WavefieldColours);
                Assert.Equal("album", reloaded.Current.WavecoreColours);
            }
            var file = Path.Combine(folder, "settings.json");
            var json = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(file, json.Replace("\"wavecoreColours\": \"album\"", "\"wavecoreColours\": \"unknown\"", StringComparison.Ordinal),
                TestContext.Current.CancellationToken);
            await using var unknown = AppSettingsStore.Open(folder, Logger.Null);
            Assert.Equal("album", unknown.Current.WavecoreColours);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void TheChoicesReachTheScenes()
    {
        var album = VisualiserServices.For(new AppSettings());
        Assert.True(album.WavefieldAlbumColours);
        Assert.True(album.FosforaAlbumColours);
        var original = VisualiserServices.For(new AppSettings { WavefieldColours = "original", WavecoreColours = "original" });
        Assert.False(original.AlbumColours(VisualizerStyle.Wavefield));
        Assert.False(original.AlbumColours(VisualizerStyle.WavefieldSphere));
        Assert.False(original.AlbumColours(VisualizerStyle.FosforaAurora));
        Assert.True(original.AlbumColours(VisualizerStyle.Silk));
        // Each scene has its own choice.
        Assert.True(VisualiserServices.For(new AppSettings { WavefieldColours = "original" }).AlbumColours(VisualizerStyle.FosforaAurora));
    }

    [Fact]
    public void TheMacsWebScenesReadAsTheStandInThatTakesTheirColours()
    {
        // Wavefield Original is drawn by Wavefield and Wavecore by Fosfora Aurora: their colour choices go with them.
        Assert.Equal(VisualizerStyle.Wavefield, new AppSettings { VisualizerStyle = "wavefieldOriginal" }.Normalized().Visualizer());
        Assert.Equal(VisualizerStyle.FosforaAurora, new AppSettings { VisualizerStyle = "wavecore" }.Normalized().Visualizer());
    }
}
