using System.IO.Compression;
using System.Text;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Kugou's KRC lyric file: a four-byte "krc1" header, then zlib data XOR-ed with a fixed 16-byte key. Decoded, its lines
/// read <c>[startMs,durationMs]</c> followed by <c>&lt;offsetMs,durationMs,0&gt;word</c> pieces.
/// </summary>
public static class KugouKrcCipher
{
    private static ReadOnlySpan<byte> Key => [0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69];

    private const int MaxBytes = 2_000_000;

    /// <summary>The KRC text inside an encrypted file, or null when the bytes are not a readable KRC file.</summary>
    public static string? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 6 || data.Length > MaxBytes || !data[..4].SequenceEqual("krc1"u8))
        {
            return null;
        }
        var bytes = data[4..].ToArray();
        var key = Key;
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= key[i % key.Length];
        }
        // zlib: a two-byte header naming deflate, the raw stream, then a checksum.
        if (bytes.Length <= 6 || (bytes[0] & 0x0F) != 8)
        {
            return null;
        }
        try
        {
            using var input = new MemoryStream(bytes, 2, bytes.Length - 2);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = inflater.Read(chunk)) > 0)
            {
                if (output.Length + read > 8 * MaxBytes)
                {
                    return null;
                }
                output.Write(chunk, 0, read);
            }
            return LyricHttpClient.Utf8Text(output.ToArray().AsSpan());
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Encrypts KRC text the way Kugou serves it (for tests and fixtures).</summary>
    public static byte[] Encode(string text)
    {
        using var output = new MemoryStream();
        output.Write("krc1"u8);
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(Encoding.UTF8.GetBytes(text));
        }
        var bytes = packed.ToArray();
        var key = Key;
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= key[i % key.Length];
        }
        output.Write(bytes);
        return output.ToArray();
    }
}
