using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Cheap questions about an unparsed payload that some sources need answered to choose among their own hits: is
/// anything timed in it, and is its timing sound. The Mac answers both by parsing; sources here ask a probe instead, so
/// the lookup can plug in the real parsers (and the timing-fault check) and tests can use the basic one.
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
    /// into a split second). LRCLIB uses it to decide whether an exact hit ends the search.
    /// </summary>
    bool HasSoundTiming(LyricFormat format, string payload);
}

/// <summary>
/// The probe used when no parser is plugged in: it only looks for timestamps, so every timed payload counts as sound.
/// </summary>
public sealed class BasicLyricPayloadProbe : ILyricPayloadProbe
{
    public static BasicLyricPayloadProbe Instance { get; } = new();

    public bool IsUsable(LyricFormat format, string payload) => format switch
    {
        LyricFormat.Lrc => LrcText.HasTimedLine(payload),
        LyricFormat.Lyricsfile => payload.Contains("start_ms:", StringComparison.Ordinal),
        LyricFormat.Ttml => LrcText.TtmlHasTimedLine(payload),
        LyricFormat.Yrc or LyricFormat.Krc => LrcText.HasBracketedMillisecondLine(payload),
        LyricFormat.KPoeJson => payload.Contains("\"lyrics\"", StringComparison.Ordinal),
        LyricFormat.Plain => !string.IsNullOrWhiteSpace(payload),
        _ => false,
    };

    public bool HasSoundTiming(LyricFormat format, string payload) => format != LyricFormat.Plain && IsUsable(format, payload);
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

    /// <summary>The latest line stamp in LRC text, in seconds (an <c>[offset:]</c> is not applied); null without one.</summary>
    public static double? LastLineStart(string lrc)
    {
        double? last = null;
        foreach (var line in Lines(lrc))
        {
            var leading = LeadingStamps().Match(line);
            if (!leading.Success)
            {
                continue;
            }
            foreach (Match stamp in Stamp().Matches(leading.Value))
            {
                var seconds = Seconds(stamp);
                if (last is null || seconds > last)
                {
                    last = seconds;
                }
            }
        }
        return last;
    }

    /// <summary>Whether LRC text carries inline word stamps (enhanced LRC: <c>&lt;00:12.34&gt;word</c>).</summary>
    public static bool HasWordStamps(string lrc) => WordStamp().IsMatch(lrc);

    /// <summary>Whether YRC or KRC text has a line headed <c>[startMs,durationMs]</c>.</summary>
    public static bool HasBracketedMillisecondLine(string text) => MillisecondLine().IsMatch(text);

    /// <summary>Whether YRC text has word timing tuples <c>(startMs,durationMs,0)</c>.</summary>
    public static bool HasYrcWords(string text) => YrcWord().IsMatch(text);

    /// <summary>Whether TTML text has a timed paragraph.</summary>
    public static bool TtmlHasTimedLine(string ttml) => TtmlTimedParagraph().IsMatch(ttml);

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

    /// <summary>
    /// An LRC time, "mm:ss.xxx", from whole milliseconds, so 59.9996 s reads "01:00.000" and never "00:60.00". Clamped
    /// to 0 … 999 minutes, the longest an LRC stamp can say.
    /// </summary>
    public static string Clock(double seconds)
    {
        var clamped = Math.Min(Math.Max(0, double.IsFinite(seconds) ? seconds : 0), 59_999);
        var ms = (long)Math.Round(clamped * 1000, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 60_000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");
    }

    /// <summary>Text with HTML character references decoded (QQ and Kuwo send "&amp;apos;" and friends).</summary>
    public static string DecodeEntities(string text) =>
        text.Contains('&', StringComparison.Ordinal) ? WebUtility.HtmlDecode(text) : text;

    /// <summary>Lines split on CR, LF or CRLF.</summary>
    public static IEnumerable<string> Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.None);

    private static double Seconds(Match stamp)
    {
        var minutes = double.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
        var fraction = stamp.Groups[3].Success ? stamp.Groups[3].Value : "";
        var part = fraction.Length == 0 ? 0 : double.Parse(fraction, CultureInfo.InvariantCulture) / Math.Pow(10, fraction.Length);
        return (minutes * 60) + seconds + part;
    }

    [GeneratedRegex(@"^\s*(?:\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\]\s*)+")]
    private static partial Regex LeadingStamps();

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordStamp();

    [GeneratedRegex(@"^\s*\[\d+,\d+\]", RegexOptions.Multiline)]
    private static partial Regex MillisecondLine();

    [GeneratedRegex(@"\(\d+,\d+,-?\d+\)")]
    private static partial Regex YrcWord();

    [GeneratedRegex(@"<(?:\w+:)?p\b[^>]*\bbegin\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex TtmlTimedParagraph();

    [GeneratedRegex(@"<(?:\w+:)?span\b[^>]*\bbegin\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex TtmlTimedSpan();
}
