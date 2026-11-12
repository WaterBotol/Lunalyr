using Lunalyr.Core.Logging;
using Lunalyr.Core.Visualisers;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Shell.Rendering;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Visualisers;
using Lunalyr.Shell.Wallpaper;

namespace Lunalyr.Shell.Tests;

/// <summary>The visualisers' settings and their wallpaper-type hooks.</summary>
public sealed class VisualiserSettingsTests
{
    [Fact]
    public void TheVisualisersTypeHasItsLayer()
    {
        Assert.True(WallpaperCatalog.HasVisualisers);
        var settings = new AppSettings { BackgroundMode = "visualizer" };
        Assert.Equal("visualizer", WallpaperCatalog.VisualiserLayerFor(settings));
        Assert.Contains(WallpaperLayers.Backgrounds(BackgroundOptions.Default, Logger.Null), f => f.Id == "visualizer");
    }

    [Fact]
    public void DefaultsAreTheMacs()
    {
        var s = new AppSettings();
        Assert.Equal("silk", s.VisualizerStyle);
        Assert.False(s.AudioReactiveEnabled); // System audio is off until the user turns it on
        Assert.Equal(1, s.AudioSensitivity);
        Assert.Equal(1, s.VisualizerPunch);
        Assert.Equal("album", s.StarTrailsColours);
        var visualiser = VisualiserServices.For(s);
        Assert.Equal(VisualizerStyle.Silk, visualiser.Style);
        Assert.True(visualiser.StarTrailsAlbumColours);
    }

    [Fact]
    public void StoredValuesComeBackInRange()
    {
        var s = new AppSettings
        {
            VisualizerStyle = "light3D", AudioSensitivity = 9, VisualizerPunch = -1, StarTrailsColours = "neon",
        }.Normalized();
        Assert.Equal("starTrails", s.VisualizerStyle); // the Mac's web scene reads as its native stand-in
        Assert.Equal(AppSettings.SensitivityMax, s.AudioSensitivity);
        Assert.Equal(0, s.VisualizerPunch);
        Assert.Equal("album", s.StarTrailsColours);
        Assert.Equal("silk", (new AppSettings { VisualizerStyle = "nonsense", AudioSensitivity = double.NaN }.Normalized()).VisualizerStyle);
        Assert.Equal(1, new AppSettings { AudioSensitivity = double.NaN }.Normalized().AudioSensitivity);
        Assert.Equal("original", new AppSettings { StarTrailsColours = "original" }.Normalized().StarTrailsColours);
    }
}
