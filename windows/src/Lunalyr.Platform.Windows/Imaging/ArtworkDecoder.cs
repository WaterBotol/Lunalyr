using Lunalyr.Core.Colour;
using Vortice.WIC;

namespace Lunalyr.Platform.Imaging;

/// <summary>
/// Decodes a track's artwork (the GSMTC thumbnail: JPEG, PNG, or anything else WIC reads) into a small straight-alpha
/// RGBA buffer for the colour code. Decoding happens on the thread pool, never on the caller's (UI) thread; the image is
/// scaled down inside WIC, so a 3000-pixel cover costs about what a small one does. Scaling is nearest-neighbour, as the
/// Mac reads a cover for its palette: contrasting colour fields stay apart instead of averaging into a third colour (the
/// player's palette averages the result itself).
/// </summary>
public static class ArtworkDecoder
{
    /// <summary>
    /// The longest side the colour code reads: twice the 72-pixel grid the palette samples.
    /// </summary>
    public const int DefaultMaxSide = 144;

    /// <summary>Covers larger than this on either side are refused (a corrupt header or a decompression bomb).</summary>
    public const int MaxSourceSide = 16384;

    /// <summary>
    /// Reads <paramref name="stream"/> (from its current position) and decodes it off the calling thread. Null when the
    /// bytes are not an image WIC can read. The caller keeps ownership of the stream.
    /// </summary>
    public static async Task<ArtworkPixels?> DecodeAsync(Stream stream, int maxSide = DefaultMaxSide,
                                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] bytes;
        if (stream is MemoryStream memory && memory.TryGetBuffer(out var buffer) && memory.Position == 0)
        {
            bytes = buffer.AsSpan().ToArray();
        }
        else
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            bytes = copy.ToArray();
        }
        return await Task.Run(() => Decode(bytes, maxSide), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Decodes <paramref name="data"/> on the calling thread: the first frame, scaled (never enlarged) so its longer side
    /// is at most <paramref name="maxSide"/>, as straight-alpha RGBA. Null when it cannot be read.
    /// </summary>
    public static ArtworkPixels? Decode(byte[] data, int maxSide = DefaultMaxSide) =>
        Read(data, maxSide, BitmapInterpolationMode.NearestNeighbor, PixelFormat.Format32bppRGBA,
             (width, height, pixels) => new ArtworkPixels(width, height, pixels));

    /// <summary>The longest side of a cover as the opening title draws it: about its size on a 4K screen.</summary>
    public const int CoverMaxSide = 384;

    /// <summary>
    /// Decodes <paramref name="data"/> on the calling thread for drawing (the opening title's cover): the first frame,
    /// scaled down smoothly (never enlarged) so its longer side is at most <paramref name="maxSide"/>, as premultiplied
    /// BGRA, ready for a Direct2D bitmap. Null when it cannot be read.
    /// </summary>
    public static CoverArt? DecodeCover(byte[] data, int maxSide = CoverMaxSide) =>
        Read(data, maxSide, BitmapInterpolationMode.Fant, PixelFormat.Format32bppPBGRA,
             (width, height, pixels) => new CoverArt(width, height, pixels));

    private static T? Read<T>(byte[] data, int maxSide, BitmapInterpolationMode interpolation, Guid format,
                              Func<int, int, byte[], T> make) where T : class
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0 || maxSide <= 0) return null;
        try
        {
            using var factory = new IWICImagingFactory();
            using var wicStream = factory.CreateStream();
            wicStream.Initialize(data);
            using var decoder = factory.CreateDecoderFromStream(wicStream, DecodeOptions.CacheOnDemand);
            if (decoder.FrameCount == 0) return null;
            using var frame = decoder.GetFrame(0);
            var size = frame.Size;
            if (size.Width <= 0 || size.Height <= 0 || size.Width > MaxSourceSide || size.Height > MaxSourceSide) return null;
            var scale = Math.Min(1.0, (double)maxSide / Math.Max(size.Width, size.Height));
            var width = Math.Max(1, (int)Math.Round(size.Width * scale));
            var height = Math.Max(1, (int)Math.Round(size.Height * scale));

            using var scaler = factory.CreateBitmapScaler();
            scaler.Initialize(frame, (uint)width, (uint)height, interpolation);
            using var converted = factory.CreateFormatConverter();
            converted.Initialize(scaler, format, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
            var pixels = new byte[width * height * 4];
            converted.CopyPixels((uint)(width * 4), pixels);
            return make(width, height, pixels);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
