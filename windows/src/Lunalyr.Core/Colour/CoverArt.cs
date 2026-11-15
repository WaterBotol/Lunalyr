namespace Lunalyr.Core.Colour;

/// <summary>
/// A song's cover as it is drawn (not as its colours are read: that is <see cref="ArtworkPixels"/>): premultiplied
/// BGRA, 8 bits a channel, rows top to bottom with no padding, scaled down smoothly to a display size. Made once per
/// cover off the render thread; the renderer turns it into a bitmap once per cover. Compared by reference.
/// </summary>
public sealed class CoverArt
{
    public CoverArt(int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != width * height * 4) throw new ArgumentException("Expected width × height × 4 bytes.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Premultiplied BGRA, <see cref="Width"/> × 4 bytes a row.</summary>
    public byte[] Pixels { get; }
}
