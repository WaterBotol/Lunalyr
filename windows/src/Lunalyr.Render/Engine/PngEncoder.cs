using System.Buffers.Binary;
using System.IO.Compression;

namespace Lunalyr.Render.Engine;

/// <summary>Encodes a BGRA back-buffer copy as an RGB PNG (alpha forced opaque: the wallpaper ignores alpha).</summary>
internal static class PngEncoder
{
    public static byte[] EncodeBgra(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        int rowBytes = width * 3 + 1;
        var raw = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            var src = bgra.Slice(y * stride, width * 4);
            int o = y * rowBytes;
            raw[o++] = 0; // filter: none
            for (int x = 0; x < width; x++, o += 3)
            {
                raw[o] = src[x * 4 + 2];
                raw[o + 1] = src[x * 4 + 1];
                raw[o + 2] = src[x * 4];
            }
        }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // colour type: RGB
        ihdr[10] = ihdr[11] = ihdr[12] = 0;
        Chunk(png, "IHDR"u8, ihdr);
        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
            Chunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        s.Write(word);
        s.Write(type);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        s.Write(word);
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc(uint c, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c;
    }
}
