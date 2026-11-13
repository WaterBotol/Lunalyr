using Lunalyr.Core.Colour;
using Lunalyr.Core.Visualisers;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;
using Lunalyr.Render.Visualisers;

namespace Lunalyr.Render.Tests.Visualisers;

/// <summary>
/// Visualiser › Colours for Wavefield, Wavefield Sphere and Fosfora Aurora (the Mac's <c>custom.wavefieldColours</c> and
/// <c>custom.wavecoreColours</c>), drawn through the wallpaper's layer on WARP: "Album cover" takes the cover's colours,
/// "Original" keeps the scene's own whatever the cover.
/// </summary>
public sealed class VisualiserColoursTests
{
    private const int Width = 160, Height = 90;

    private sealed class Steady(AudioSnapshot sound) : IVisualiserSound
    {
        public AudioSnapshot Latest() => sound;

        public double Now => 1_000;
    }

    private static Palette Cover(double hue) =>
        PaletteMapper.FromAlbum(new AlbumWallpaperPalette([.. Enumerable.Repeat(new Double3(hue, 1, 0.8), 9)]));

    private static byte[] Draw(VisualiserSettings settings, Palette palette)
    {
        var sound = settings.Style.IsWavefield() ? VisualiserSceneTests.Tone(220) : Loud();
        var host = new VisualiserHost(() => settings, new Steady(sound));
        using var surface = new OffscreenSurface(Width, Height);
        using var layer = new VisualiserLayerFactory(host, new BackgroundOptions { Grain = false, ScrimStrength = 0, ManualBacking = 0 }).Create();
        surface.Prepare(layer);
        var scene = new SceneSnapshot { Palette = palette };
        for (var frame = 0; frame < 3; frame++) surface.Draw(layer, scene, 12 + frame / 60.0);
        return surface.ReadPixels();
    }

    private static AudioSnapshot Loud()
    {
        var sound = new AudioSnapshot { Level = 0.7f, Bass = 0.7f, Mid = 0.5f, Treble = 0.4f };
        for (var i = 0; i < 64; i++) sound.Bands[i] = (float)(0.4 + 0.35 * Math.Sin(i * 0.3));
        for (var i = 0; i < 8; i++) sound.Components[i] = 0.6f;
        return sound;
    }

    private static long Difference(byte[] a, byte[] b)
    {
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum;
    }

    public static TheoryData<VisualizerStyle> Scenes => [VisualizerStyle.Wavefield, VisualizerStyle.WavefieldSphere, VisualizerStyle.FosforaAurora];

    private static VisualiserSettings Original(VisualizerStyle style) => VisualiserSettings.Default with
    {
        Style = style,
        WavefieldAlbumColours = !style.IsWavefield(),
        FosforaAlbumColours = style != VisualizerStyle.FosforaAurora,
    };

    [Theory]
    [MemberData(nameof(Scenes))]
    public void OriginalColoursIgnoreTheCoverAndAlbumColoursFollowIt(VisualizerStyle style)
    {
        var album = VisualiserSettings.Default with { Style = style };
        var red = Draw(album, Cover(0));
        var blue = Draw(album, Cover(2.0 / 3));
        Assert.True(Difference(red, blue) > Width * Height, $"{style.Title()} takes the cover's colours");

        var originalRed = Draw(Original(style), Cover(0));
        var originalBlue = Draw(Original(style), Cover(2.0 / 3));
        Assert.True(Difference(originalRed, originalBlue) < Width * Height / 10, $"{style.Title()}'s own colours whatever the cover");
        Assert.True(Difference(originalRed, red) > Width * Height, $"{style.Title()}'s own colours are not the cover's");
        // The scene's own colours are what it draws with no cover at all.
        Assert.True(Difference(originalRed, Draw(album, Palette.Default)) < Width * Height / 10);
    }

    // Ported from WebVisualizerHostTests.swift: testOriginalColoursDropTheAlbumBlendForThatSceneOnly (the scenePalette half:
    // original draws with no album palette, album with the cover's, a scene without the choice decides elsewhere; the
    // web page's request half has no Windows counterpart)
    [Fact]
    public void EachSceneReadsItsOwnColourChoice()
    {
        var original = VisualiserSettings.Default with { WavefieldAlbumColours = false, FosforaAlbumColours = false };
        Assert.False(original.AlbumColours(VisualizerStyle.Wavefield));
        Assert.False(original.AlbumColours(VisualizerStyle.WavefieldSphere));
        Assert.False(original.AlbumColours(VisualizerStyle.FosforaAurora));
        foreach (var style in VisualizerStyles.All.Where(s => !s.IsWavefield() && s != VisualizerStyle.FosforaAurora))
            Assert.True(original.AlbumColours(style), style.Title()); // Star Trails has its own sky setting
        Assert.All(VisualizerStyles.All, s => Assert.True(VisualiserSettings.Default.AlbumColours(s)));
    }

    [Fact]
    public void TheOtherScenesIgnoreTheChoice()
    {
        var silk = VisualiserSettings.Default with { Style = VisualizerStyle.Silk };
        Assert.True(Difference(Draw(silk, Cover(0)), Draw(Original(VisualizerStyle.Wavefield) with { Style = VisualizerStyle.Silk, FosforaAlbumColours = false }, Cover(0)))
            < Width * Height / 10);
    }

    [Fact]
    public void ChangingTheChoiceRecoloursTheSceneOnScreen()
    {
        // The Colours menu changes the setting under a layer already drawing: the next frames follow it.
        var settings = VisualiserSettings.Default with { Style = VisualizerStyle.Wavefield };
        var host = new VisualiserHost(() => settings, new Steady(VisualiserSceneTests.Tone(220)));
        using var surface = new OffscreenSurface(Width, Height);
        using var layer = new VisualiserLayerFactory(host, new BackgroundOptions { Grain = false, ScrimStrength = 0, ManualBacking = 0 }).Create();
        surface.Prepare(layer);
        var scene = new SceneSnapshot { Palette = Cover(0) };
        for (var frame = 0; frame < 3; frame++) surface.Draw(layer, scene, 12 + frame / 60.0);
        var album = surface.ReadPixels();
        Assert.True(Difference(album, Draw(settings, Cover(0))) < Width * Height / 10);

        settings = Original(VisualizerStyle.Wavefield);
        for (var frame = 3; frame < 6; frame++) surface.Draw(layer, scene, 12 + frame / 60.0);
        var original = surface.ReadPixels();
        Assert.True(Difference(original, album) > Width * Height, "the scene's own colours replace the cover's");
        Assert.True(Difference(original, Draw(settings, Cover(0))) < Width * Height / 10, "as a layer made with the choice draws");
    }
}
