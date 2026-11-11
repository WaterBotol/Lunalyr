using Lunalyr.Core.Colour;
using Vortice.WIC;

namespace Lunalyr.Platform.Imaging;

/// <summary>
/// Decodes a track's artwork (the GSMTC thumbnail: JPEG, PNG, or anything else WIC reads) into a small straight-alpha
/// RGBA buffer for the colour code. Decoding happens on the thread pool, never on the caller's (UI) thread; the image is
/// scaled down inside WIC (JPEGs decode straight to a reduced size), so a 3000-pixel cover costs about what a small one
/// does.
/// </summary>
public static class ArtworkDecoder
{
    /// <summary>
    /// The longest side the colour code reads: twice the 72-pixel grid the palette samples, so the nearest-neighbour
    /// read still lands on cover detail rather than on the averaging.
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
    public static ArtworkPixels? Decode(byte[] data, int maxSide = DefaultMaxSide)
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

            // Scale premultiplied, so transparent pixels' colour never bleeds into the edges, then unpremultiply.
            using var premultiplied = factory.CreateFormatConverter();
            premultiplied.Initialize(frame, PixelFormat.Format32bppPRGBA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
            using var scaler = factory.CreateBitmapScaler();
            scaler.Initialize(premultiplied, (uint)width, (uint)height, BitmapInterpolationMode.Fant);
            using var straight = factory.CreateFormatConverter();
            straight.Initialize(scaler, PixelFormat.Format32bppRGBA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
            var pixels = new byte[width * height * 4];
            straight.CopyPixels((uint)(width * 4), pixels);
            return new ArtworkPixels(width, height, pixels);
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
