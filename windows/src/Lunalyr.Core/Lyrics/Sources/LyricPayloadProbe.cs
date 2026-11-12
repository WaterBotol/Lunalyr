using System.Net;
using System.Text.RegularExpressions;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Questions about an unparsed payload that some sources need answered to choose among their own hits: does it hold
/// lyrics, and is its timing sound. The Mac answers both by parsing, and so does <see cref="ParsedLyricPayloadProbe"/>;
/// tests may plug in their own.
/// </summary>
public interface ILyricPayloadProbe
{
    /// <summary>
    /// Whether the payload holds at least one lyric line in its format: a timed line for the timed formats, any text for
    /// <see cref="LyricFormat.Plain"/>. The Mac's equivalent is "parses to at least one line".
    /// </summary>
    bool IsUsable(LyricFormat format, string payload);

    /// <summary>
    /// Whether timed lyrics have sound timing (no run of lines bunched faster than they could be sung, no words crammed
    /// into a split second: <see cref="LyricTimingRules.TimingFault(IReadOnlyList{LyricLine})"/>). LRCLIB uses it to
    /// decide whether an exact hit ends the search, and to rank an unsound upload below sound ones.
    /// </summary>
    bool HasSoundTiming(LyricFormat format, string payload);
}

/// <summary>
/// The probe backed by the real parsers and the timing-fault rules, as the Mac's providers parse a payload to judge it.
/// Payloads are parsed without a track length (it only moves the last line's end, which no answer here depends on).
/// </summary>
public sealed class ParsedLyricPayloadProbe : ILyricPayloadProbe
{
    public static ParsedLyricPayloadProbe Instance { get; } = new();

    public bool IsUsable(LyricFormat format, string payload) =>
        format == LyricFormat.Plain ? !string.IsNullOrWhiteSpace(payload) : Lines(format, payload).Count > 0;

    public bool HasSoundTiming(LyricFormat format, string payload)
    {
        if (format == LyricFormat.Plain)
        {
            return false;
        }
        var lines = Lines(format, payload);
        return lines.Count > 0 && LyricTimingRules.TimingFault(lines) is null;
    }

    /// <summary>The payload's lines, as <see cref="LyricParser"/> reads it; empty when nothing timed is in it.</summary>
    public static IReadOnlyList<LyricLine> Lines(LyricFormat format, string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }
        IReadOnlyList<LyricLine> lines = format switch
        {
            LyricFormat.Lrc => LrcParser.Parse(payload, 0),
            LyricFormat.Lyricsfile => LyricsfileParser.Parse(payload),
            LyricFormat.Ttml => TtmlParser.Parse(payload, 0),
            LyricFormat.Yrc => YrcParser.Parse(payload),
            LyricFormat.Krc => KugouKrc.EnhancedLrcFromText(payload) is { } lrc ? LrcParser.Parse(lrc, 0) : [],
            LyricFormat.KPoeJson => KPoeJsonParser.Parse(payload)?.Lines ?? [],
            _ => [],
        };
        // A file of instrumental breaks alone still parses to lines, as on the Mac: a synced result with no sung line,
        // which ranks with untimed text. LRCLIB's exact match of that kind ends its search (spec §4.5).
        return lines;
    }
}

/// <summary>Small text helpers for lyric payloads that sources need without parsing them into lines.</summary>
internal static partial class LrcText
{
    /// <summary>Whether LRC text has a line that starts with a time stamp and carries text after it.</summary>
    public static bool HasTimedLine(string lrc)
    {
        foreach (var line in Lines(lrc))
        {
            var match = LeadingStamps().Match(line);
            if (match.Success && line.AsSpan(match.Length).Trim().Length > 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether TTML text times spans inside its lines (word or syllable timing).</summary>
    public static bool TtmlHasTimedSpans(string ttml) => TtmlTimedSpan().IsMatch(ttml);

    /// <summary>
    /// The value of an ID tag (<c>[ti:Song]</c>, <c>[ar:…]</c>, <c>[length:03:41]</c>) in the first 12,000 characters,
    /// trimmed; null when missing or empty. The key is matched without regard to case.
    /// </summary>
    public static string? Tag(string key, string text)
    {
        var prefix = "[" + key + ":";
        var head = text.Length > 12000 ? text[..12000] : text;
        foreach (var raw in Lines(head))
        {
            var line = raw.Trim();
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var close = line.IndexOf(']', StringComparison.Ordinal);
            if (close < prefix.Length)
            {
                continue;
            }
            var value = line[prefix.Length..close].Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    /// <summary>Text with HTML character references decoded (QQ and Kuwo send "&amp;apos;" and friends).</summary>
    public static string DecodeEntities(string text) =>
        text.Contains('&', StringComparison.Ordinal) ? WebUtility.HtmlDecode(text) : text;

    /// <summary>Lines split on CR, LF or CRLF.</summary>
    public static IEnumerable<string> Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.None);

    [GeneratedRegex(@"^\s*(?:\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\]\s*)+")]
    private static partial Regex LeadingStamps();

    [GeneratedRegex(@"<(?:\w+:)?span\b[^>]*\bbegin\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex TtmlTimedSpan();
}
