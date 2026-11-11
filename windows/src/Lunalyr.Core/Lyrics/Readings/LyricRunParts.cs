// Ported from the Mac app's LyricRuns.swift (Lunalyr's own code): the parts the lyric compositions need that the
// readings workstream did not port (merged, boundaries, canPreviewFirstRun).

using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>A line's runs joined into the words a rolling stage sets, with each joined word's readings and tokens.</summary>
/// <param name="Words">One word per run: a single token as it is, a glued run as one word from its first onset to its last end.</param>
/// <param name="Readings">The readings of the words, keyed by the merged word: a glued run's tokens' readings written together.</param>
/// <param name="Tokens">The tokens each glued run was made of, keyed by the merged word.</param>
public sealed record MergedRuns(
    IReadOnlyList<LyricWord> Words,
    IReadOnlyDictionary<LyricWord, string> Readings,
    IReadOnlyDictionary<LyricWord, IReadOnlyList<LyricWord>> Tokens);

public static class LyricRunParts
{
    /// <summary>Glued tokens joined into the word they spell (see <see cref="MergedRuns"/>).</summary>
    public static MergedRuns Merged(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue,
                                    IReadOnlyDictionary<LyricWord, string>? readings = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(glue);
        readings ??= new Dictionary<LyricWord, string>();
        var result = new List<LyricWord>();
        var joined = new Dictionary<LyricWord, string>();
        var parts = new Dictionary<LyricWord, IReadOnlyList<LyricWord>>();
        foreach (var (start, end) in LyricRuns.Ranges(glue, words.Count))
        {
            if (end - start <= 1)
            {
                var single = words[start];
                result.Add(single);
                if (readings.TryGetValue(single, out var own)) joined[single] = own;
                continue;
            }
            var tokens = new LyricWord[end - start];
            for (var i = start; i < end; i++) tokens[i - start] = words[i];
            var text = string.Concat(tokens.Select(token => token.Text)).Trim();
            var last = double.NegativeInfinity;
            foreach (var token in tokens) last = Math.Max(last, token.End);
            var word = new LyricWord(text, tokens[0].Start, last);
            result.Add(word);
            parts[word] = tokens;
            var reading = string.Concat(tokens.Select(token => readings.TryGetValue(token, out var r) ? r : ""));
            if (reading.Length > 0) joined[word] = reading;
        }
        return new MergedRuns(result, joined, parts);
    }

    /// <summary>Where each token begins and ends along the run's text, 0…1 by character, so a token's reveal falls on its own letters.</summary>
    public static double[] Boundaries(IReadOnlyList<LyricWord> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var counts = new double[tokens.Count];
        double total = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            counts[i] = TextElements.Count(tokens[i].Text.Trim());
            total += counts[i];
        }
        total = Math.Max(1, total);
        var result = new double[counts.Length + 1];
        double running = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            running += counts[i];
            result[i + 1] = Math.Min(1, running / total);
        }
        return result;
    }

    /// <summary>
    /// Whether a line-timed line's first run may be previewed: the line has more than one run and its first is a single
    /// word (a phrase sung as one is never previewed).
    /// </summary>
    public static bool CanPreviewFirstRun(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(glue);
        var (visible, visibleGlue) = LyricRuns.Visible(words, glue);
        var ranges = LyricRuns.Ranges(visibleGlue, visible.Count);
        if (ranges.Count <= 1) return false;
        var (start, end) = ranges[0];
        return PreviewWords.IsPreviewWord(string.Concat(visible.Skip(start).Take(end - start).Select(word => word.Text)));
    }
}
