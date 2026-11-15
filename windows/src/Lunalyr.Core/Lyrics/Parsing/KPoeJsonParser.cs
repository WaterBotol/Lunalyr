// Lyrics+ / KPoe node replies (JSON with per-syllable timing).
//
// Provenance: in the Mac's KPoeLyricsProvider.swift the payload types, the entry conversion and parts of the syllable
// loop trace to the Verso import, so they are re-implemented here from this description:
//   * The reply has "lyrics": [{time, duration, text, syllabus: [{time, duration, text}]}] in milliseconds, and
//     "metadata" naming the upstream ("Apple (via Lyrics+)") and, on some nodes, the matched title and artist (as a
//     string or a list of strings, under "title"/"name" and "artist"/"artists"/"artistName").
//   * A line runs from time to time + max(duration, 1) ms. Lines with no text after trimming are dropped; a reply with
//     no lines left has nothing to offer. A malformed entry makes the reply unreadable.
//   * A syllable holds from its time to time + max(duration, 1) ms, and at least 0.06 s unless that passes the end
//     of its line.
// Lunalyr's own rule (cache policy v8) is ported as written: syllables written together join into the word they
// spell, except Han, kana, Hangul and Thai syllables, which stay timed tokens of their own (LyricScript.Words).
//
// Line replies (the Mac's next/integration 1cdde21, cache policy v16 there, v19 here; ported from the Mac session's
// description): a "type":"Line" reply carries entries with time, duration and text and no syllables. They are kept as
// line timing, with estimated words, instead of being dropped. An entry without a duration ends where the next entry
// starts; the last one ends with the track, or 4 s after it starts (the LRC parser's rule; the description doesn't say).
// A word reply with a line that has no syllables keeps that line at line timing, so the reply ranks as mixed timing.

using System.Text.Json;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>What a Lyrics+ node returned: its lines (word- or line-timed), the upstream it pulled them from, and the recording it matched.</summary>
public sealed record KPoeHit(IReadOnlyList<LyricLine> Lines, string Source, string? Title, string? Artist);

public static class KPoeJsonParser
{
    private sealed record Entry(double Time, double? Duration, string Text, List<(double Time, double Duration, string Text)> Syllables);

    /// <summary>The node's lines, or null when the reply is unreadable or has none.</summary>
    /// <param name="json">The reply body.</param>
    /// <param name="trackDuration">The playing track's length in seconds, which ends a last line that has no duration; 0 when unknown.</param>
    /// <param name="distribute">Spreads estimated words over a line without syllables; the built-in splitting when null.</param>
    public static KPoeHit? Parse(string json, double trackDuration = 0, WordDistributor? distribute = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var entries = new List<Entry>();
            if (root.TryGetProperty("lyrics", out var lyrics) && lyrics.ValueKind != JsonValueKind.Null)
            {
                foreach (var item in lyrics.EnumerateArray())
                {
                    if (!TryEntry(item, out var entry)) return null;
                    entries.Add(entry!);
                }
            }
            var lines = new List<LyricLine>();
            for (var index = 0; index < entries.Count; index++)
            {
                if (Line(entries, index, trackDuration, distribute) is { } line) lines.Add(line);
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
            var name = cut < 0 ? raw : raw[..cut];
            // One node says "qApple" for Apple Music's lyrics: name them "Apple" whichever node answered.
            name = Ranking.LyricSourceRanking.RelayLabel(name);
            return new KPoeHit(lines, name, title, artist);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>False when the entry is malformed: no numeric time, no text, a duration that is not a number, or a malformed syllable.</summary>
    private static bool TryEntry(JsonElement item, out Entry? entry)
    {
        entry = null;
        if (item.ValueKind != JsonValueKind.Object || !Number(item, "time", out var time)
            || !item.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String) return false;
        double? duration = null;
        if (item.TryGetProperty("duration", out var durationElement) && durationElement.ValueKind != JsonValueKind.Null)
        {
            if (!Number(item, "duration", out var value)) return false;
            duration = value;
        }
        var syllables = new List<(double Time, double Duration, string Text)>();
        if (item.TryGetProperty("syllabus", out var syllabus) && syllabus.ValueKind != JsonValueKind.Null)
        {
            foreach (var syllable in syllabus.EnumerateArray())
            {
                if (syllable.ValueKind != JsonValueKind.Object || !Number(syllable, "time", out var at) || !Number(syllable, "duration", out var length)
                    || !syllable.TryGetProperty("text", out var piece) || piece.ValueKind != JsonValueKind.String) return false;
                syllables.Add((at, length, piece.GetString()!));
            }
        }
        entry = new Entry(time, duration, textElement.GetString()!, syllables);
        return true;
    }

    /// <summary>
    /// Entry <paramref name="index"/> as a line: word-timed from its syllables, else timed by its window with estimated
    /// words. Null when it has no text.
    /// </summary>
    private static LyricLine? Line(List<Entry> entries, int index, double trackDuration, WordDistributor? distribute)
    {
        var entry = entries[index];
        var text = TextRules.TrimSpaces(entry.Text);
        if (text.Length == 0) return null;
        var start = entry.Time / 1000;
        double end;
        if (entry.Duration is { } duration)
        {
            end = (entry.Time + Math.Max(duration, 1)) / 1000;
        }
        else
        {
            // No duration: the line lasts until the next entry starts.
            var next = entries.Skip(index + 1).FirstOrDefault(later => later.Time > entry.Time);
            end = next is not null ? next.Time / 1000 : Math.Max(trackDuration, start + 4);
        }
        if (entry.Syllables.Count > 0 && Words(entry.Syllables, end) is { Count: > 0 } words)
        {
            return new LyricLine { Start = start, End = end, Text = text, Words = words, WordTimed = true };
        }
        var line = LyricLines.MakeLine(text, start, end, distribute);
        return entry.Duration is null ? line with { EndFromNextLine = true } : line;
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
