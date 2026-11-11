using System.Text;
using System.Text.RegularExpressions;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Reads lyric files in the encodings players and editors commonly save: UTF-8 and UTF-16 (with or without a byte-order
/// mark), Shift_JIS, GB18030, Big5 and EUC-KR. Of the decodings that succeed without loss, the first to yield usable
/// LRC or TTML timing wins.
/// </summary>
/// <remarks>
/// Ported from the Mac's <c>LyricTextDecoder</c> (renamed so it cannot clash with a port of the import path). Foundation's language-aware detection, which the Mac uses to order the
/// legacy encodings, has no Windows equivalent: here the legacy decodings are ordered by how plausible their text looks
/// (fewest half-width katakana, private-use and rare CJK characters, the usual signs of a wrong code page), then in the
/// order above.
/// </remarks>
public static partial class LyricFileDecoder
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly int[] LegacyCodePages = [932, 54936, 950, 51949];

    static LyricFileDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The most plausible text, preferring a decoding with lyric timing; null when nothing decodes.</summary>
    public static string? Decode(ReadOnlySpan<byte> data)
    {
        var texts = Decodings(data);
        if (texts.Count <= 1)
        {
            return texts.FirstOrDefault();
        }
        return texts.FirstOrDefault(HasTiming) ?? texts[0];
    }

    /// <summary>Lossless decodings, most plausible first, each without a byte-order mark.</summary>
    public static IReadOnlyList<string> Decodings(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return [];
        }
        // A byte-order mark is unambiguous.
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Strict(StrictUtf8, data[3..]) is { } utf8 ? [Prepared(utf8)] : [];
        }
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Strict(Strict(Encoding.Unicode), data[2..]) is { } little ? [Prepared(little)] : [];
        }
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Strict(Strict(Encoding.BigEndianUnicode), data[2..]) is { } big ? [Prepared(big)] : [];
        }
        // UTF-16 without a mark can also be valid UTF-8 full of NULs, so the zero-byte pattern is checked first.
        var utf16 = Utf16Order(data);
        if (utf16 is null && Strict(StrictUtf8, data) is { } text)
        {
            return [Prepared(text)];
        }
        var found = new List<string>();
        void Add(string? value)
        {
            if (value is not null && Prepared(value) is var prepared && !found.Contains(prepared))
            {
                found.Add(prepared);
            }
        }
        if (utf16 is not null)
        {
            Add(Strict(Strict(utf16), data));
        }
        Add(Strict(StrictUtf8, data));
        var legacy = new List<(string Text, int Penalty, int Order)>();
        for (var i = 0; i < LegacyCodePages.Length; i++)
        {
            if (Strict(Encoding.GetEncoding(LegacyCodePages[i], EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), data) is { } decoded)
            {
                legacy.Add((decoded, Penalty(decoded), i));
            }
        }
        foreach (var (decoded, _, _) in legacy.OrderBy(x => x.Penalty).ThenBy(x => x.Order))
        {
            Add(decoded);
        }
        return found;
    }

    /// <summary>Whether the text has LRC or TTML lyric timing.</summary>
    public static bool HasTiming(string text) =>
        text.TrimStart().StartsWith('<') ? LrcText.TtmlHasTimedLine(text) : LrcText.HasTimedLine(text);

    private static Encoding Strict(Encoding encoding) =>
        Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private static string? Strict(Encoding encoding, ReadOnlySpan<byte> data)
    {
        try
        {
            return encoding.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// UTF-16 without a mark leaves a zero in every other byte of the ASCII timestamps; which half holds them gives the
    /// byte order.
    /// </summary>
    private static Encoding? Utf16Order(ReadOnlySpan<byte> data)
    {
        var sample = data[..Math.Min(512, data.Length)];
        if (sample.Length < 8)
        {
            return null;
        }
        int even = 0, odd = 0;
        for (var i = 0; i < sample.Length; i++)
        {
            if (sample[i] == 0)
            {
                if (i % 2 == 0)
                {
                    even++;
                }
                else
                {
                    odd++;
                }
            }
        }
        var half = sample.Length / 2;
        if (odd * 3 > half && even * 10 < half)
        {
            return Encoding.Unicode;
        }
        if (even * 3 > half && odd * 10 < half)
        {
            return Encoding.BigEndianUnicode;
        }
        return null;
    }

    /// <summary>How many characters look like the product of a wrong code page.</summary>
    private static int Penalty(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is (>= 0xFF61 and <= 0xFF9F) or (>= 0xE000 and <= 0xF8FF) or (>= 0xF900 and <= 0xFAFF)
                or (>= 0x3400 and <= 0x4DBF) or (>= 0x20000 and <= 0x2FA1F) || Rune.IsControl(rune) && rune.Value is not ('\r' or '\n' or '\t'))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Unicode text with no byte-order mark, and an XML declaration that no longer names the original encoding.</summary>
    private static string Prepared(string text)
    {
        var value = text.StartsWith('﻿') ? text[1..] : text;
        return value.StartsWith("<?xml", StringComparison.Ordinal) ? XmlEncoding().Replace(value, "$1$2UTF-8$3", 1) : value;
    }

    [GeneratedRegex("""^(<\?xml[^>]*?encoding\s*=\s*)(["'])[^"']*(["'])""")]
    private static partial Regex XmlEncoding();
}
