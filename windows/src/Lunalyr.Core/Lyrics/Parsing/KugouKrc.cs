// Ported from the Mac app's KugouKRC.swift (Lunalyr's own code; one Verso-attributed line, the row loop, is ordinary
// line splitting written afresh here).

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>
/// Kugou's KRC lyric file: a four-byte "krc1" header, then zlib data XOR-ed with a fixed 16-byte key. Its lines read
/// <c>[startMs,durationMs]</c> followed by <c>&lt;offsetMs,durationMs,0&gt;word</c> pieces. This turns one into enhanced
/// LRC (<c>[mm:ss.xxx]&lt;mm:ss.xxx&gt;word…&lt;mm:ss.xxx&gt;</c>), which <see cref="LrcParser"/> reads as word-timed lines.
/// </summary>
public static partial class KugouKrc
{
    private static readonly byte[] Key = [0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69];

    /// <summary>The KRC file's text, or null when the data is not a KRC file.</summary>
    public static string? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 6 || data.Length > 2_000_000 || !data[..4].SequenceEqual("krc1"u8)) return null;
        var bytes = data[4..].ToArray();
        for (var index = 0; index < bytes.Length; index++) bytes[index] ^= Key[index % Key.Length];
        // zlib = a two-byte header, raw deflate, then a checksum. Only the deflate stream is read, as on the Mac.
        if (bytes.Length <= 6 || (bytes[0] & 0x0F) != 8) return null;
        try
        {
            using var input = new MemoryStream(bytes, 2, bytes.Length - 2);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > 20_000_000) return null;
            }
            return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Encrypts KRC text the way Kugou serves it (for tests and fixtures).</summary>
    public static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(Encoding.UTF8.GetBytes(text));
        }
        var bytes = packed.ToArray();
        for (var index = 0; index < bytes.Length; index++) bytes[index] ^= Key[index % Key.Length];
        return [.. "krc1"u8, .. bytes];
    }

    public static string? EnhancedLrc(ReadOnlySpan<byte> data) => Decode(data) is { } text ? EnhancedLrcFromText(text) : null;

    [GeneratedRegex(@"^\[([0-9]+),([0-9]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex LineHead();

    [GeneratedRegex(@"<([0-9]+),([0-9]+),[0-9]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Piece();

    public static string? EnhancedLrcFromText(string text)
    {
        var output = new List<string>();
        foreach (var raw in LrcParser.SplitNewlines(text))
        {
            var head = LineHead().Match(raw);
            if (!head.Success || !TextRules.TryParseDouble(head.Groups[1].ValueSpan, out var start)
                || !TextRules.TryParseDouble(head.Groups[2].ValueSpan, out var length)) continue;
            var body = raw[(head.Index + head.Length)..];
            var marks = Piece().Matches(body);
            var result = new StringBuilder(Stamp('[', start / 1000, ']'));
            if (marks.Count == 0)
            {
                var words = TextRules.TrimSpaces(body);
                if (words.Length == 0) continue;
                output.Add(result.Append(words).ToString());
                continue;
            }
            var lastEnd = start / 1000;
            for (var index = 0; index < marks.Count; index++)
            {
                var mark = marks[index];
                var offset = TextRules.TryParseDouble(mark.Groups[1].ValueSpan, out var o) ? o : 0;
                var span = TextRules.TryParseDouble(mark.Groups[2].ValueSpan, out var s) ? s : 0;
                var textStart = mark.Index + mark.Length;
                var textEnd = index + 1 < marks.Count ? marks[index + 1].Index : body.Length;
                var word = body[textStart..Math.Max(textStart, textEnd)];
                result.Append(Stamp('<', (start + offset) / 1000, '>')).Append(word);
                lastEnd = Math.Max(lastEnd, (start + offset + span) / 1000);
            }
            result.Append(Stamp('<', Math.Max(lastEnd, (start + length) / 1000), '>'));
            output.Add(result.ToString());
        }
        return output.Count == 0 ? null : string.Join('\n', output);
    }

    private static string Stamp(char open, double seconds, char close) => open + Clock(seconds) + close;

    /// <summary>
    /// An LRC time, "mm:ss.xxx", from whole milliseconds. Rounding the seconds on their own wrote 59.996 s as
    /// "00:60.00", which no LRC reader accepts.
    /// </summary>
    public static string Clock(double seconds)
    {
        // At most 999 minutes, the longest an LRC stamp can say.
        var clamped = Math.Min(Math.Max(0, double.IsFinite(seconds) ? seconds : 0), 59_999);
        var ms = (long)Math.Round(clamped * 1000, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 60_000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");
    }
}
