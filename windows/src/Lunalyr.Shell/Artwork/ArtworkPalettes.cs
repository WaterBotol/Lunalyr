using Lunalyr.Core.Colour;
using Lunalyr.Platform.Imaging;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell.Artwork;

/// <summary>A track's artwork to the scene's colours: decode, the Mac's 3×3 colour grid, the scene palette.</summary>
public static class ArtworkPalettes
{
    /// <summary>
    /// Decodes <paramref name="stream"/> (any image WIC reads) and maps its colours, all on the thread pool. Null when
    /// it is not an image.
    /// </summary>
    public static async Task<Palette?> FromStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var pixels = await ArtworkDecoder.DecodeAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (pixels is null) return null;
        return await Task.Run(() => PaletteMapper.FromAlbum(new AlbumWallpaperPalette(AlbumColourGrid.Extract(pixels))),
            cancellationToken).ConfigureAwait(false);
    }
}
