// Lyrics+ / KPoe node replies (JSON with per-syllable timing).
//
// Provenance: in the Mac's KPoeLyricsProvider.swift the payload types, the entry conversion and parts of the syllable
// loop trace to the Verso import, so they are re-implemented here from this description:
//   * The reply has "lyrics": [{time, duration, text, syllabus: [{time, duration, text}]}] in milliseconds, and
//     "metadata" naming the upstream ("Apple (via Lyrics+)") and, on some nodes, the matched title and artist (as a
//     string or a list of strings, under "title"/"name" and "artist"/"artists"/"artistName").
//   * A line runs from time to time + max(duration, 1) ms. Lines with no text after trimming, or no syllables, are
//     dropped; a reply with no lines left has nothing to offer. A malformed entry makes the reply unreadable.
//   * A syllable holds from its time to time + max(duration, 1) ms, and at least 0.06 s unless that passes the end
//     of its line.
// Lunalyr's own rule (cache policy v8) is ported as written: syllables written together join into the word they
// spell, except Han, kana, Hangul and Thai syllables, which stay timed tokens of their own (LyricScript.Words).

using System.Text.Json;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>What a Lyrics+ node returned: word-timed lines, the upstream it pulled them from, and the recording it matched.</summary>
public sealed record KPoeHit(IReadOnlyList<LyricLine> Lines, string Source, string? Title, string? Artist);

public static class KPoeJsonParser
{
    /// <summary>The node's word-timed lines, or null when the reply is unreadable or has none.</summary>
    public static KPoeHit? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var lines = new List<LyricLine>();
            if (root.TryGetProperty("lyrics", out var lyrics) && lyrics.ValueKind != JsonValueKind.Null)
            {
                foreach (var entry in lyrics.EnumerateArray())
                {
                    if (!TryEntry(entry, out var line)) return null;
                    if (line is not null) lines.Add(line);
                }
            }
            if (lines.Count == 0) return null;
            string? source = null, title = null, artist = null;
            if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                source = Text(metadata, "source");
                title = Text(metadata, "title") ?? Text(metadata, "name");
                artist = Text(metadata, "artist") ?? Text(metadata, "artists") ?? Text(metadata, "artistName");
            }
            // The node reports which upstream it pulled from, e.g. "Apple" or "Lyrics+ (via Apple with QQ)"; keep the leading name.
            var raw = source ?? "Lyrics+";
            var cut = raw.IndexOf(" (", StringComparison.Ordinal);
            return new KPoeHit(lines, cut < 0 ? raw : raw[..cut], title, artist);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>False when the entry is malformed; <paramref name="line"/> is null when it is well formed but has nothing to show.</summary>
    private static bool TryEntry(JsonElement entry, out LyricLine? line)
    {
        line = null;
        if (entry.ValueKind != JsonValueKind.Object || !Number(entry, "time", out var time) || !Number(entry, "duration", out var duration)
            || !entry.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String) return false;
        var syllables = new List<(double Time, double Duration, string Text)>();
        if (entry.TryGetProperty("syllabus", out var syllabus) && syllabus.ValueKind != JsonValueKind.Null)
        {
            foreach (var syllable in syllabus.EnumerateArray())
            {
                if (syllable.ValueKind != JsonValueKind.Object || !Number(syllable, "time", out var at) || !Number(syllable, "duration", out var length)
                    || !syllable.TryGetProperty("text", out var piece) || piece.ValueKind != JsonValueKind.String) return false;
                syllables.Add((at, length, piece.GetString()!));
            }
        }
        var start = time / 1000;
        var end = (time + Math.Max(duration, 1)) / 1000;
        var text = TextRules.TrimSpaces(textElement.GetString()!);
        if (text.Length == 0 || syllables.Count == 0) return true;
        var words = Words(syllables, end);
        if (words.Count == 0) return true;
        line = new LyricLine { Start = start, End = end, Text = text, Words = words, WordTimed = true };
        return true;
    }

    /// <summary>
    /// A syllable is not always a word: "con", "trol" arrive as two entries written together and are joined back into
    /// the word they spell, from the first syllable's start to the last one's end. Han, kana, Hangul and Thai
    /// syllables stay timed tokens of their own, as the TTML, YRC and LRC parsers keep them.
    /// </summary>
    private static List<LyricWord> Words(List<(double Time, double Duration, string Text)> syllables, double lineEnd)
    {
        var tokens = new List<TimedToken>(syllables.Count);
        var spaced = true;
        foreach (var syllable in syllables)
        {
            var raw = syllable.Text;
            var glued = !spaced && !(raw.Length == 0 || char.IsWhiteSpace(raw[0]));
            if (raw.Length > 0) spaced = char.IsWhiteSpace(raw[^1]);
            var text = raw.Trim();
            if (text.Length == 0) continue;
            var start = syllable.Time / 1000;
            var end = (syllable.Time + Math.Max(syllable.Duration, 1)) / 1000;
            tokens.Add(new TimedToken(text, start, Math.Max(end, Math.Min(start + 0.06, lineEnd)), glued));
        }
        return LyricScript.Words(tokens);
    }

    private static bool Number(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value);
    }

    /// <summary>A trimmed, non-empty string, or a list of names joined by ", ".</summary>
    private static string? Text(JsonElement metadata, string name)
    {
        if (!metadata.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var trimmed = value.GetString()!.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)) return null;
            var names = value.EnumerateArray().Select(item => item.GetString()!.Trim()).Where(item => item.Length > 0).ToList();
            return names.Count == 0 ? null : string.Join(", ", names);
        }
        return null;
    }
}
