// Ported from the Mac app's `parseLyricsFile` (LyricsFetcher.swift, Lunalyr's own code). The Mac decodes the YAML
// with Yams; here MiniYaml reads exactly the subset LRCLIB writes, and the typed fields below mirror the Mac's
// Decodable struct: a missing or mistyped required field makes the whole file unreadable, as a failed decode does.

using System.Text;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>
/// LRCLIB's Lyricsfile: YAML with lines and optional word tokens, e.g.
/// <code>
/// lines:
/// - text: I've been tryna call
///   start_ms: 27160
///   end_ms: 29960
///   words:
///   - text: 'I''ve '
///     start_ms: 27209
/// </code>
/// </summary>
public static class LyricsfileParser
{
    private sealed record Token(string Text, double StartMs, double? EndMs);

    private sealed record Row(string Text, double StartMs, double? EndMs, IReadOnlyList<Token>? Words);

    public static IReadOnlyList<LyricLine> Parse(string yaml, WordDistributor? distribute = null)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > 1_000_000 || !TryDecode(yaml, out var offsetMs, out var decoded)) return [];
        var offset = offsetMs / 1000;
        if (!double.IsFinite(offset)) return [];
        var rows = decoded.Where(row => double.IsFinite(row.StartMs) && row.StartMs >= 0).OrderBy(row => row.StartMs).ToList();
        var lines = new List<LyricLine>(rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var start = Math.Max(0, row.StartMs / 1000 + offset);
            var end = row.EndMs is { } endMs ? endMs / 1000 + offset
                : index + 1 < rows.Count ? rows[index + 1].StartMs / 1000 + offset : start + 4;
            if (!double.IsFinite(end) || !(end > start)) continue;
            var line = LyricLines.MakeLine(row.Text, start, end, distribute);
            if (row.EndMs is null) line = line with { EndFromNextLine = true };
            var tokens = row.Words ?? [];
            var words = tokens.Select((token, i) => new LyricWord(
                token.Text.Trim(),
                token.StartMs / 1000 + offset,
                token.EndMs is { } tokenEnd ? tokenEnd / 1000 + offset : i + 1 < tokens.Count ? tokens[i + 1].StartMs / 1000 + offset : end)).ToList();
            var covered = TextRules.WithoutWhitespace(string.Concat(words.Select(word => word.Text)));
            if (words.Count > 0 && covered == TextRules.WithoutWhitespace(row.Text) && words.Select((word, i) =>
                    double.IsFinite(word.Start) && double.IsFinite(word.End) && word.Start >= start && word.End > word.Start && word.End <= end
                    && (i == 0 || word.Start >= words[i - 1].End)).All(ok => ok))
            {
                line = line with { Words = words, WordTimed = true };
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>The file's rows and offset, or false when it does not decode (see the file header).</summary>
    private static bool TryDecode(string yaml, out double offsetMs, out List<Row> rows)
    {
        offsetMs = 0;
        rows = [];
        if (MiniYaml.Parse(yaml) is not YamlMapping root) return false;
        switch (root["metadata"])
        {
            case null or YamlScalar { IsNull: true }:
                break;
            case YamlMapping metadata:
                if (!OptionalNumber(metadata["offset_ms"], out var offset)) return false;
                offsetMs = offset ?? 0;
                break;
            default:
                return false;
        }
        switch (root["lines"])
        {
            case null or YamlScalar { IsNull: true }:
                return true;
            case YamlSequence sequence:
                foreach (var item in sequence.Items)
                {
                    if (!TryRow(item, out var row)) return false;
                    rows.Add(row);
                }
                return true;
            default:
                return false;
        }
    }

    private static bool TryRow(YamlNode item, out Row row)
    {
        row = null!;
        if (item is not YamlMapping mapping || !TryText(mapping["text"], out var text) || !MiniYaml.TryNumber(mapping["start_ms"], out var start)
            || !OptionalNumber(mapping["end_ms"], out var end)) return false;
        List<Token>? words = null;
        switch (mapping["words"])
        {
            case null or YamlScalar { IsNull: true }:
                break;
            case YamlSequence sequence:
                words = [];
                foreach (var entry in sequence.Items)
                {
                    if (entry is not YamlMapping token || !TryText(token["text"], out var tokenText)
                        || !MiniYaml.TryNumber(token["start_ms"], out var tokenStart) || !OptionalNumber(token["end_ms"], out var tokenEnd)) return false;
                    words.Add(new Token(tokenText, tokenStart, tokenEnd));
                }
                break;
            default:
                return false;
        }
        row = new Row(text, start, end, words);
        return true;
    }

    /// <summary>Any scalar reads as text (an empty plain value as ""); collections do not.</summary>
    private static bool TryText(YamlNode? node, out string text)
    {
        text = (node as YamlScalar)?.Value ?? "";
        return node is YamlScalar;
    }

    /// <summary>A missing or null value is absent; anything else must be a number.</summary>
    private static bool OptionalNumber(YamlNode? node, out double? value)
    {
        value = null;
        if (node is null or YamlScalar { IsNull: true }) return true;
        if (!MiniYaml.TryNumber(node, out var number)) return false;
        value = number;
        return true;
    }
}
