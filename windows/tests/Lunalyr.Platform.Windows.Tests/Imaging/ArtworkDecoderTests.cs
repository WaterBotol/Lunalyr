using Lunalyr.Core.Colour;
using Lunalyr.Platform.Imaging;

namespace Lunalyr.Platform.Windows.Tests.Imaging;

/// <summary>
/// The artwork decoder on generated covers (Fixtures/Artwork, drawn by make-fixtures.ps1 with GDI+, so WIC reads files
/// another encoder wrote): sizes, colours, transparency, refusals, and the whole way to a palette.
/// </summary>
public class ArtworkDecoderTests
{
    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Artwork", name));

    private static (byte R, byte G, byte B, byte A) Pixel(ArtworkPixels image, int x, int y)
    {
        var i = (y * image.Width + x) * 4;
        return (image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2], image.Rgba[i + 3]);
    }

    [Fact]
    public void DecodesAJpegSmall()
    {
        var image = ArtworkDecoder.Decode(Fixture("warm.jpg"));
        Assert.NotNull(image);
        Assert.Equal(ArtworkDecoder.DefaultMaxSide, image.Width);
        Assert.Equal(ArtworkDecoder.DefaultMaxSide, image.Height);
        // The bottom band is a deep maroon; the top-left a deep red.
        var band = Pixel(image, 72, 135);
        Assert.InRange(band.R, 45, 80);
        Assert.InRange(band.G, 0, 30);
        Assert.Equal(255, band.A);
        var corner = Pixel(image, 2, 2);
        Assert.True(corner.R > 120 && corner.G < 60 && corner.B < 60, $"{corner}");
    }

    [Fact]
    public void DecodesAPngAndKeepsTheAspect()
    {
        var cool = ArtworkDecoder.Decode(Fixture("cool.png"));
        Assert.NotNull(cool);
        Assert.Equal((144, 144), (cool.Width, cool.Height));
        var wide = ArtworkDecoder.Decode(Fixture("wide.jpg"));
        Assert.NotNull(wide);
        Assert.Equal((144, 48), (wide.Width, wide.Height));
        var left = Pixel(wide, 20, 24);
        var right = Pixel(wide, 124, 24);
        Assert.True(left.B > 150 && left.R < 60, $"{left}");
        Assert.True(right.R > 200 && right.B < 60, $"{right}");
    }

    [Fact]
    public void NeverEnlargesAndKeepsTransparency()
    {
        var disc = ArtworkDecoder.Decode(Fixture("disc.png"));
        Assert.NotNull(disc);
        Assert.Equal((64, 64), (disc.Width, disc.Height));
        Assert.Equal(0, Pixel(disc, 0, 0).A);
        var centre = Pixel(disc, 32, 32);
        Assert.Equal(255, centre.A);
        Assert.Equal((30, 180, 90), (centre.R, centre.G, centre.B));
        // Scaled down, the straight colour at the soft rim stays the disc's green rather than darkening towards black.
        var small = ArtworkDecoder.Decode(Fixture("disc.png"), 16);
        Assert.NotNull(small);
        for (var x = 0; x < 16; x++)
        {
            var rim = Pixel(small, x, 2);
            if (rim.A > 40) Assert.True(rim.G > 150, $"{rim} at {x}");
        }
    }

    [Theory]
    [InlineData("broken.jpg")]
    public void RefusesWhatIsNotAnImage(string name)
    {
        Assert.Null(ArtworkDecoder.Decode(Fixture(name)));
        Assert.Null(ArtworkDecoder.Decode([]));
        Assert.Null(ArtworkDecoder.Decode("not an image at all, just words"u8.ToArray()));
    }

    [Fact]
    public async Task DecodesAStreamOffTheCallingThread()
    {
        await using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Artwork", "contrast.png"));
        var image = await ArtworkDecoder.DecodeAsync(file, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        Assert.Equal((144, 144), (image.Width, image.Height));
        await using var junk = new MemoryStream([1, 2, 3]);
        Assert.Null(await ArtworkDecoder.DecodeAsync(junk, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ACoverBecomesItsPalette()
    {
        // Saturated red and cyan stripes on black: both colours reach the wallpaper stops.
        var contrast = new AlbumWallpaperPalette(AlbumColourGrid.Extract(ArtworkDecoder.Decode(Fixture("contrast.png"))));
        Assert.Contains(contrast.ArtworkStops, s => s.X > 0.6 && s.Y < 0.3 && s.Z < 0.3);
        Assert.Contains(contrast.ArtworkStops, s => s.X < 0.3 && s.Y > 0.55 && s.Z > 0.55);
        // A grey cover gives grey cells and the player's neutral card.
        var mono = ArtworkDecoder.Decode(Fixture("mono.jpg"));
        Assert.All(AlbumColourGrid.Extract(mono), cell => Assert.True(cell.Y < 0.12, $"{cell}"));
        Assert.Equal(ArtworkPalette.Neutral, ArtworkPalette.Of(mono));
        // A warm cover: warm cells, and a warm player accent.
        var warm = ArtworkDecoder.Decode(Fixture("warm.jpg"));
        var grid = AlbumColourGrid.Extract(warm);
        Assert.All(grid, cell => Assert.True(cell.X < 0.15 || cell.X > 0.85, $"warm hue {cell}"));
        var sleeve = ArtworkPalette.Of(warm);
        Assert.True(sleeve.FromArtwork);
        Assert.True(sleeve.Accent.Red > sleeve.Accent.Blue);
    }
}
