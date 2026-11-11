using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Tests.Scene;

public class PaletteMapperTests
{
    private static double Luminance(Vector4 c) => Perceptual.Luminance(new Double3(c.X, c.Y, c.Z));

    [Fact]
    public void BaseAndAccentsAreTheCoversColours()
    {
        var cover = new AlbumWallpaperPalette(AlbumColourGrid.Extract(ArtworkPixels.Solid(64, 64, 200, 40, 60)));
        var palette = PaletteMapper.FromAlbum(cover);
        Assert.Equal(5, palette.Accents.Count);
        for (var i = 0; i < 5; i++) Assert.Equal(new Vector4(cover.Stops[i], 1), palette.Accents[i]);
        Assert.Equal(new Vector4(cover.Average * 0.45f, 1), palette.Base);
        Assert.True(palette.Base.X > palette.Base.Z, "a red cover gives a red base");
    }

    [Fact]
    public void TextIsAReadableTintOfTheCoverAndTheHighlightSetsItOff()
    {
        foreach (var (r, g, b) in new[] { (200, 40, 60), (30, 90, 200), (240, 230, 200), (20, 20, 20) })
        {
            var cover = new AlbumWallpaperPalette(AlbumColourGrid.Extract(ArtworkPixels.Solid(64, 64, (byte)r, (byte)g, (byte)b)));
            var palette = PaletteMapper.FromAlbum(cover);
            var backdrop = LyricBackdrop.Luminance(WallpaperBackgroundMode.ColourOnly, false, cover.ArtworkStops, 1, pastelText: true);
            var text = Luminance(palette.Text);
            Assert.True(Perceptual.Contrast(text, backdrop) >= Perceptual.TintedContrast - 0.02 || text > 0.9, $"{(r, g, b)}");
            var lab = Perceptual.ToOkLab(new Double3(palette.Text.X, palette.Text.Y, palette.Text.Z));
            Assert.True(lab.Chroma > 0.04, $"a tint, not white: {(r, g, b)}");
            Assert.NotEqual(palette.Text, palette.Highlight);
            Assert.Equal(1, palette.Text.W);
            Assert.Equal(1, palette.Highlight.W);
        }
    }

    [Fact]
    public void TheCoversNineColoursAndTheTextBackdropGoWithThePaletteForTheLyricInk()
    {
        var cover = new AlbumWallpaperPalette(AlbumColourGrid.Extract(ArtworkPixels.Solid(64, 64, 240, 230, 200)));
        var palette = PaletteMapper.FromAlbum(cover);
        Assert.Equal(cover.ArtworkStops, palette.ArtworkStops);
        Assert.Equal(LyricBackdrop.Luminance(WallpaperBackgroundMode.ColourOnly, false, cover.ArtworkStops, 1, pastelText: true),
                     palette.TextBackdrop, 9);
        // A palette not made from a cover leaves the ink its own pastels.
        Assert.Empty(Palette.Default.ArtworkStops);
    }

    [Fact]
    public void FallbackIsTheMacsFallbackPalette()
    {
        var fallback = PaletteMapper.Fallback;
        Assert.Equal(new Vector4(AlbumWallpaperPalette.Fallback.Stops[0], 1), fallback.Accents[0]);
        Assert.Same(fallback, PaletteMapper.Fallback);
    }
}
