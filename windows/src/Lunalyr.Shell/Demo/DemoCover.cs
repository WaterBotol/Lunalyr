namespace Lunalyr.Shell.Demo;

/// <summary>
/// Paints an invented album cover in code: four corner colours blended across the square with a soft glowing disc,
/// returned as an uncompressed 24-bit BMP (which WIC reads like any thumbnail). Nothing is read from disk.
/// </summary>
public static class DemoCover
{
    /// <summary>An sRGB colour, 0…255 per channel.</summary>
    public readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>The cover as BMP bytes, <paramref name="side"/> pixels square.</summary>
    public static byte[] Paint(Rgb topLeft, Rgb topRight, Rgb bottomLeft, Rgb bottomRight, Rgb glow, int side = 240)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(side, 8);
        var stride = (side * 3 + 3) & ~3;
        const int headers = 14 + 40;
        var bytes = new byte[headers + stride * side];
        // BITMAPFILEHEADER
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.TryWriteBytes(bytes.AsSpan(2), bytes.Length);
        BitConverter.TryWriteBytes(bytes.AsSpan(10), headers);
        // BITMAPINFOHEADER: bottom-up rows, 24 bits, no compression.
        BitConverter.TryWriteBytes(bytes.AsSpan(14), 40);
        BitConverter.TryWriteBytes(bytes.AsSpan(18), side);
        BitConverter.TryWriteBytes(bytes.AsSpan(22), side);
        BitConverter.TryWriteBytes(bytes.AsSpan(26), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), (short)24);
        BitConverter.TryWriteBytes(bytes.AsSpan(34), stride * side);
        BitConverter.TryWriteBytes(bytes.AsSpan(38), 2835);
        BitConverter.TryWriteBytes(bytes.AsSpan(42), 2835);

        for (var row = 0; row < side; row++)
        {
            var y = (row + 0.5) / side;
            var offset = headers + (side - 1 - row) * stride;
            for (var column = 0; column < side; column++)
            {
                var x = (column + 0.5) / side;
                // Corner colours, blended with a smoothstep so the quarters keep their own colour.
                var sx = x * x * (3 - 2 * x);
                var sy = y * y * (3 - 2 * y);
                double Channel(byte tl, byte tr, byte bl, byte br) =>
                    (tl * (1 - sx) + tr * sx) * (1 - sy) + (bl * (1 - sx) + br * sx) * sy;
                var r = Channel(topLeft.R, topRight.R, bottomLeft.R, bottomRight.R);
                var g = Channel(topLeft.G, topRight.G, bottomLeft.G, bottomRight.G);
                var b = Channel(topLeft.B, topRight.B, bottomLeft.B, bottomRight.B);
                // A soft disc a little above and right of centre, like a sun or a lamp.
                var dx = x - 0.58;
                var dy = y - 0.42;
                var disc = Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy) / 0.26, 0, 1);
                disc = disc * disc * (3 - 2 * disc);
                r += (glow.R - r) * disc;
                g += (glow.G - g) * disc;
                b += (glow.B - b) * disc;
                // Faint diagonal stripes, so the cover has some texture.
                var stripe = 0.94 + 0.06 * Math.Sin((x + y) * Math.PI * 18);
                var pixel = offset + column * 3;
                bytes[pixel] = (byte)Math.Clamp(Math.Round(b * stripe), 0, 255);
                bytes[pixel + 1] = (byte)Math.Clamp(Math.Round(g * stripe), 0, 255);
                bytes[pixel + 2] = (byte)Math.Clamp(Math.Round(r * stripe), 0, 255);
            }
        }
        return bytes;
    }
}
