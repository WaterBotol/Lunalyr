// Ported from the Mac app's WordTimedLyrics.swift (QQMusicQRC, KuwoLrcx; 7ddb08a, Lunalyr's own code). Contract
// references, as on the Mac: Yudaotor/lyrimuse, lyrimuse-engine/qq.go, qqfallback.go and kuwolrcx.go.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>
/// QQ Music's QRC word-timed lyrics, from <c>lyric_download.fcg</c>. The <c>&lt;content&gt;</c> field is hex text,
/// triple-DES encrypted (<see cref="QQMusicTripleDes"/>) over zlib data. Inside is XML whose <c>LyricContent</c>
/// attribute holds lines <c>[startMs,lengthMs]word(startMs,lengthMs)…</c>: each word comes before its stamp, and the
/// stamps are absolute.
/// </summary>
public static partial class QQMusicQrc
{
    /// <summary>
    /// What one <c>lyric_download.fcg</c> reply holds: word-timed lyrics converted to enhanced LRC, and/or plain line LRC
    /// (a song with no QRC is sent as line LRC through the same field).
    /// </summary>
    public sealed record Download(string? Enhanced, string? Line);

    public static string? Decrypt(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var digits = hex.Trim();
        if (digits.Length == 0 || digits.Length > 4_000_000 || digits.Length % 16 != 0) return null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(digits);
        }
        catch (FormatException)
        {
            return null;
        }
        return QQMusicTripleDes.Decrypt(bytes) is { } plain ? WordTimedLrc.Utf8(WordTimedLrc.Inflate(plain)) : null;
    }

    [GeneratedRegex("\"\\s*/>", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeEnd();

    /// <summary>
    /// The first track's <c>LyricContent</c>. QQ leaves literal quotes inside it unescaped, so this cuts at the next
    /// <c>&lt;Lyric_</c> and then matches up to the last <c>"/&gt;</c> rather than using an XML parser.
    /// </summary>
    public static string? LyricContent(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var start = xml.IndexOf("LyricContent=\"", StringComparison.Ordinal);
        if (start < 0) return null;
        var segment = xml[(start + "LyricContent=\"".Length)..];
        var next = segment.IndexOf("<Lyric_", StringComparison.Ordinal);
        if (next >= 0) segment = segment[..next];
        var closes = AttributeEnd().Matches(segment);
        if (closes.Count == 0) return null;
        return LrcText.DecodeEntities(segment[..closes[^1].Index]);
    }

    [GeneratedRegex(@"^\[(\d+),(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex QrcHead();

    [GeneratedRegex(@"\((\d+),(\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex QrcStamp();

    public static string? EnhancedLrcFromQrc(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var output = new List<string>();
        foreach (var raw in LrcParser.SplitNewlines(text))
        {
            var row = raw.Trim(' ', '\t');
            if (row.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(row);
                continue;
            }
            var head = QrcHead().Match(row);
            if (!head.Success || !double.TryParse(head.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
                || !double.TryParse(head.Groups[2].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var length)) continue;
            var body = row[(head.Index + head.Length)..];
            var marks = QrcStamp().Matches(body);
            if (marks.Count == 0)
            {
                var plain = body.Trim(' ', '\t');
                if (plain.Length > 0) output.Add("[" + KugouKrc.Clock(start / 1000) + "]" + plain);
                continue;
            }
            var words = new List<WordTimedLrc.Word>();
            var textStart = 0;
            foreach (Match mark in marks)
            {
                words.Add(new WordTimedLrc.Word(body[textStart..mark.Index], Number(mark.Groups[1].ValueSpan) ?? start, Number(mark.Groups[2].ValueSpan) ?? 0));
                textStart = mark.Index + mark.Length;
            }
            // Text after the last stamp is not in well-formed QRC; keep it with the last word.
            if (textStart < body.Length && words.Count > 0) words[^1] = words[^1] with { Text = words[^1].Text + body[textStart..] };
            if (WordTimedLrc.Line(start, length, words) is { } converted) output.Add(converted);
        }
        return output.Any(line => !line.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase)) ? string.Join('\n', output) : null;
    }

    [GeneratedRegex(@"<content(?:\s[^>]*)?><!\[CDATA\[(.*?)\]\]></content>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ContentField();

    /// <summary>Reads a <c>lyric_download.fcg</c> reply (XML-like text wrapped in a comment).</summary>
    public static Download? DownloadOf(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var match = ContentField().Match(body);
        if (!match.Success || Decrypt(match.Groups[1].Value) is not { } decrypted) return null;
        if (LyricContent(decrypted) is { } content) return new Download(EnhancedLrcFromQrc(content), null);
        // No QRC for this song: the field is line LRC.
        return decrypted.Contains('[', StringComparison.Ordinal) ? new Download(null, decrypted) : null;
    }

    private static double? Number(ReadOnlySpan<char> text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>
/// Kuwo's word-timed "lrcx" lyrics, from <c>mlyric.kuwo.cn/mobi.s?…&amp;lrcx=1</c>. The reply is a <c>tp=content</c>
/// header, a blank line, then zlib data; inflated, it is base64 text that, decoded and XOR-ed with "yeelion", gives
/// lines <c>[mm:ss.xxx]&lt;a,b&gt;word…</c>. Words come after their stamps. The stamps are obfuscated:
/// <c>[kuwo:NNN]</c> read as octal gives v, k1 = v / 10, k2 = v % 10, and a word starts |a+b| / (2·k1) ms and lasts
/// |a−b| / (2·k2) ms after the line. Translation and blank lines carry only <c>&lt;0,0&gt;</c> stamps and are dropped.
/// </summary>
public static partial class KuwoLrcx
{
    private static ReadOnlySpan<byte> Key => "yeelion"u8;

    public static string? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 10 || data.Length > 2_000_000 || !Encoding.UTF8.GetString(data[..10]).Equals("tp=content", StringComparison.OrdinalIgnoreCase))
            return null;
        var separator = data.IndexOf("\r\n\r\n"u8);
        if (separator < 0 || WordTimedLrc.Utf8(WordTimedLrc.Inflate(data[(separator + 4)..])) is not { } base64) return null;
        byte[] bytes;
        try
        {
            // Unknown characters (line breaks) are left out, as the Mac's base64 options say.
            bytes = Convert.FromBase64String(new string([.. base64.Where(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')]));
        }
        catch (FormatException)
        {
            return null;
        }
        for (var index = 0; index < bytes.Length; index++) bytes[index] ^= Key[index % Key.Length];
        return WordTimedLrc.Utf8(bytes);
    }

    [GeneratedRegex(@"^\s*\[kuwo:(\d+)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex Header();

    public static (int K1, int K2)? Coefficients(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = Header().Match(text);
        if (!match.Success) return null;
        int value;
        try
        {
            value = Convert.ToInt32(match.Groups[1].Value, 8);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
        int k1 = value / 10, k2 = value % 10;
        return k1 != 0 && k2 != 0 ? (k1, k2) : null;
    }

    [GeneratedRegex(@"^\[(\d{1,3}):(\d{2})\.(\d{1,3})\]", RegexOptions.CultureInvariant)]
    private static partial Regex LineHead();

    [GeneratedRegex(@"<(-?\d+),(-?\d+)(?:,-?\d+)?>", RegexOptions.CultureInvariant)]
    private static partial Regex Stamp();

    public static string? EnhancedLrcFromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Coefficients(text) is not var (k1, k2)) return null;
        var output = new List<string>();
        foreach (var raw in LrcParser.SplitNewlines(text))
        {
            var row = raw.Trim(' ', '\t');
            if (row.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(row);
                continue;
            }
            var head = LineHead().Match(row);
            if (!head.Success) continue;
            var fraction = head.Groups[3].Value;
            var start = Whole(head.Groups[1].Value) * 60_000 + Whole(head.Groups[2].Value) * 1000
                        + Whole(fraction) * Math.Pow(10, 3 - fraction.Length);
            var body = row[(head.Index + head.Length)..];
            var marks = Stamp().Matches(body);
            if (marks.Count == 0) continue;
            var words = new List<WordTimedLrc.Word>();
            var allZero = true;
            for (var index = 0; index < marks.Count; index++)
            {
                var mark = marks[index];
                var a = (int)Whole(mark.Groups[1].Value);
                var b = (int)Whole(mark.Groups[2].Value);
                if (a != 0 || b != 0) allZero = false;
                var textStart = mark.Index + mark.Length;
                var textEnd = index + 1 < marks.Count ? marks[index + 1].Index : body.Length;
                // Whole milliseconds, as the Mac's integer division gives them.
                var offset = Math.Abs(a + b) / (2 * k1);
                var length = Math.Abs(a - b) / (2 * k2);
                words.Add(new WordTimedLrc.Word(body[textStart..textEnd], start + offset, length));
            }
            if (allZero) continue;
            if (WordTimedLrc.Line(start, null, words) is { } converted) output.Add(converted);
        }
        return output.Any(line => !line.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase)) ? string.Join('\n', output) : null;
    }

    public static string? ToEnhancedLrc(ReadOnlySpan<byte> data) => Decode(data) is { } text ? EnhancedLrcFromText(text) : null;

    private static double Whole(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
