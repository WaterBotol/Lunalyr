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

    // The visualiser's Direct3D pass shares the device context with Direct2D: a lyric composition drawn after it, frame
    // after frame, still shows (the settings preview, on the GPU when there is one).
    [Fact]
    public void LyricsShowOverTheVisualiserFrameAfterFrame()
    {
        var song = Lunalyr.Shell.Demo.DemoSongs.Songs[0];
        var scene = new Lunalyr.Render.Scene.SceneSnapshot
        {
            Title = song.Title, Artist = song.Artist, Lyrics = song.Lyrics, Style = new AppSettings().Style(), TrackKey = "demo",
        };
        using var renderer = new SettingsPreviewRenderer(320, 180,
            WallpaperLayers.Factories(Lunalyr.Core.Lyrics.Readings.ReadingServices.Basic.WordBreaker), hardware: true);
        using var bare = new SettingsPreviewRenderer(320, 180,
            WallpaperLayers.Factories(Lunalyr.Core.Lyrics.Readings.ReadingServices.Basic.WordBreaker), hardware: true);
        var with = new byte[320 * 180 * 4];
        var without = new byte[320 * 180 * 4];
        for (var frame = 0; frame < 5; frame++)
        {
            var wall = 1 + frame / 15.0;
            renderer.Render(scene, "visualizer", "editorial", 15.5, true, wall, with);
            bare.Render(scene with { Lyrics = null }, "visualizer", "editorial", 15.5, true, wall, without);
            var changed = 0;
            for (var i = 0; i < with.Length; i += 4)
                if (Math.Abs(with[i] - without[i]) + Math.Abs(with[i + 1] - without[i + 1]) + Math.Abs(with[i + 2] - without[i + 2]) > 30) changed++;
            Assert.True(changed > 200, $"frame {frame}: {changed} pixels differ; the lyrics should show over the visualiser");
        }
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
