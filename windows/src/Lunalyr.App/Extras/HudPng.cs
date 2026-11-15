using System.Buffers.Binary;
using System.IO.Compression;

namespace Lunalyr.App.Extras;

/// <summary>
/// A small PNG writer for <c>--hud-capture</c>: premultiplied BGRA (as the HUD's bitmap holds it) to straight RGBA, one
/// deflated IDAT. For checks only.
/// </summary>
internal static class HudPng
{
    private static readonly uint[] Crc = MakeCrc();

    public static byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var raw = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 4);
            raw[row] = 0; // no filter
            for (var x = 0; x < width; x++)
            {
                var s = (y * width + x) * 4;
                var d = row + 1 + x * 4;
                var a = bgra[s + 3];
                raw[d] = Unmultiply(bgra[s + 2], a);
                raw[d + 1] = Unmultiply(bgra[s + 1], a);
                raw[d + 2] = Unmultiply(bgra[s], a);
                raw[d + 3] = a;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte Unmultiply(byte value, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Min(255, (value * 255 + alpha / 2) / alpha);

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes) crc = Crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = Crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        Span<byte> check = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(check, crc ^ 0xFFFFFFFFu);
        stream.Write(check);
    }

    private static uint[] MakeCrc()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
