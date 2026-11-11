namespace Lunalyr.Core.Colour;

/// <summary>
/// A decoded cover: tightly packed 8-bit RGBA, straight (not premultiplied) alpha, sRGB, top row first. The platform
/// decodes artwork into this (small, see <c>ArtworkDecoder</c>); the colour code reads it on any thread.
/// </summary>
public sealed class ArtworkPixels
{
    public ArtworkPixels(int width, int height, byte[] rgba)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length < (long)width * height * 4) throw new ArgumentException("Too few bytes for the size.", nameof(rgba));
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Width × height × 4 bytes: R, G, B, A.</summary>
    public byte[] Rgba { get; }

    public bool IsEmpty => Width == 0 || Height == 0;

    /// <summary>A solid image, for tests and previews.</summary>
    public static ArtworkPixels Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var bytes = new byte[width * height * 4];
        for (var i = 0; i < bytes.Length; i += 4) { bytes[i] = r; bytes[i + 1] = g; bytes[i + 2] = b; bytes[i + 3] = a; }
        return new ArtworkPixels(width, height, bytes);
    }

    /// <summary>
    /// The source pixel a <paramref name="side"/>-wide nearest-neighbour resample takes for destination column (or row)
    /// <paramref name="index"/>: the one under the destination pixel's centre, as CoreGraphics samples with
    /// interpolation off.
    /// </summary>
    internal static int NearestSource(int index, int side, int extent) =>
        Math.Min(extent - 1, (int)Math.Floor((index + 0.5) * extent / side));

    /// <summary>
    /// The image averaged down (or, when smaller, sampled up) to <paramref name="side"/> × <paramref name="side"/>: each
    /// destination pixel is the alpha-weighted mean of the source pixels whose centres fall inside it, like a smooth
    /// downscale. Alpha is the mean coverage.
    /// </summary>
    public ArtworkPixels Averaged(int side)
    {
        var result = new byte[side * side * 4];
        if (IsEmpty || side <= 0) return new ArtworkPixels(Math.Max(0, side), Math.Max(0, side), result);
        for (var y = 0; y < side; y++)
        {
            int y0 = (int)Math.Ceiling((double)y * Height / side - 0.5), y1 = (int)Math.Ceiling((double)(y + 1) * Height / side - 0.5);
            if (y1 <= y0) { y0 = NearestSource(y, side, Height); y1 = y0 + 1; }
            for (var x = 0; x < side; x++)
            {
                int x0 = (int)Math.Ceiling((double)x * Width / side - 0.5), x1 = (int)Math.Ceiling((double)(x + 1) * Width / side - 0.5);
                if (x1 <= x0) { x0 = NearestSource(x, side, Width); x1 = x0 + 1; }
                double r = 0, g = 0, b = 0, a = 0;
                var n = 0;
                for (var sy = Math.Max(0, y0); sy < Math.Min(Height, y1); sy++)
                {
                    var row = sy * Width * 4;
                    for (var sx = Math.Max(0, x0); sx < Math.Min(Width, x1); sx++)
                    {
                        var i = row + sx * 4;
                        double alpha = Rgba[i + 3];
                        r += Rgba[i] * alpha; g += Rgba[i + 1] * alpha; b += Rgba[i + 2] * alpha; a += alpha;
                        n++;
                    }
                }
                var o = (y * side + x) * 4;
                if (n == 0 || a <= 0) continue;
                result[o] = (byte)Math.Round(r / a);
                result[o + 1] = (byte)Math.Round(g / a);
                result[o + 2] = (byte)Math.Round(b / a);
                result[o + 3] = (byte)Math.Round(a / n);
            }
        }
        return new ArtworkPixels(side, side, result);
    }
}
