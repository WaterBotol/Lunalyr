using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Lunalyr.App.Ticker;

/// <summary>A plain RGBA PNG of the strip's frame, for <c>--ticker-capture</c> (checks only).</summary>
internal static class TickerPng
{
    public static byte[] FromBgra(ReadOnlySpan<byte> bgra, int width, int height)
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
                raw[d] = bgra[s + 2];
                raw[d + 1] = bgra[s + 1];
                raw[d + 2] = bgra[s];
                raw[d + 3] = bgra[s + 3];
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

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        png.Write(four);
        var typed = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, typed);
        data.CopyTo(typed, 4);
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(four, Crc(typed));
        png.Write(four);
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
