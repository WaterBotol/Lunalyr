// Ported from the Mac app's WordTimedLyrics.swift (EnhancedLRC, 7ddb08a; Lunalyr's own code).

using System.IO.Compression;
using System.Text;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>
/// Writes word-timed lines as enhanced LRC (<c>[mm:ss.xxx]&lt;mm:ss.xxx&gt;word…&lt;mm:ss.xxx&gt;</c>), the form
/// <see cref="LrcParser"/> reads as word-timed. Kugou KRC, QQ QRC and Kuwo lrcx all come through here.
/// </summary>
public static class WordTimedLrc
{
    /// <summary>One word: its text, and its absolute start and length in milliseconds.</summary>
    public readonly record struct Word(string Text, double Start, double Length);

    /// <summary>
    /// One line. The parser drops a line's word timing when text precedes the first word stamp, a word starts before
    /// the line, or starts run backwards; starts are clamped so none of that happens. A line whose words are all blank
    /// is dropped (null).
    /// </summary>
    public static string? Line(double start, double? length, IReadOnlyList<Word> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (!words.Any(word => word.Text.Trim().Length > 0)) return null;
        var lineStart = Math.Max(0, start);
        var result = new StringBuilder("[").Append(KugouKrc.Clock(lineStart / 1000)).Append(']');
        double previous = lineStart, lastEnd = lineStart;
        foreach (var word in words)
        {
            var at = Math.Max(previous, double.IsFinite(word.Start) ? word.Start : previous);
            result.Append('<').Append(KugouKrc.Clock(at / 1000)).Append('>').Append(word.Text);
            previous = at;
            lastEnd = Math.Max(lastEnd, at + Math.Max(0, double.IsFinite(word.Length) ? word.Length : 0));
        }
        if (length is { } span && double.IsFinite(span)) lastEnd = Math.Max(lastEnd, lineStart + span);
        return result.Append('<').Append(KugouKrc.Clock(lastEnd / 1000)).Append('>').ToString();
    }

    /// <summary>A zlib stream (two-byte header, raw deflate, checksum) inflated; null when it isn't one or runs past 4 MB.</summary>
    public static byte[]? Inflate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= 6 || (bytes[0] & 0x0F) != 8) return null;
        try
        {
            using var input = new MemoryStream(bytes[2..].ToArray());
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > 4_000_000) return null;
            }
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>UTF-8 text, or null when it isn't valid UTF-8.</summary>
    public static string? Utf8(byte[]? bytes)
    {
        if (bytes is null) return null;
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>zlib-compressed bytes (for tests and fixtures, as the services send them).</summary>
    public static byte[] Deflate(ReadOnlySpan<byte> bytes)
    {
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(bytes);
        return packed.ToArray();
    }
}
