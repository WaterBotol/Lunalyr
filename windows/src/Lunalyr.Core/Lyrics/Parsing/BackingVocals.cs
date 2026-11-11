// Ported from the Mac app's BackingVocals.split (Sources/UI/BackingVocals.swift, Lunalyr's own code). Only the text
// split lives here; the backing layer's look and motion belong to the display workstream.

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>A run of backing words sung together ("oh oh", "ooh"), without its brackets.</summary>
public sealed record BackingGroup(int Id, string Text, double Start, double End, IReadOnlyList<LyricWord> Words, IReadOnlyList<bool> Glue);

/// <summary>A word-timed line's lead words (with their glue to the word before) and its backing groups.</summary>
public sealed record BackingSplit(IReadOnlyList<LyricWord> Main, IReadOnlyList<bool> MainGlue, IReadOnlyList<BackingGroup> Backing);

/// <summary>
/// Sources write backing vocals as bracketed words inside a line: "Hold me (oh oh) tonight". They are lifted out of
/// the line so they can be drawn apart. Only word-timed lines are split.
/// </summary>
public static class BackingVocals
{
    private static bool Opens(char c) => c is '(' or '（';
    private static bool Closes(char c) => c is ')' or '）';

    /// <summary>
    /// Separates bracketed backing words from the lead words. <paramref name="glue"/> runs parallel to
    /// <paramref name="words"/> (true: the word continues the one before without a space). A line made only of
    /// backing words keeps them all as backing, so the lead layer is simply empty for it.
    /// </summary>
    public static BackingSplit Split(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue)
    {
        var main = new List<LyricWord>();
        var mainGlue = new List<bool>();
        var groups = new List<BackingGroup>();
        var current = new List<LyricWord>();
        var currentGlue = new List<bool>();
        var inside = false;
        var removedBefore = false;

        void Flush()
        {
            if (current.Count == 0) return;
            var shown = new List<LyricWord>();
            var shownGlue = new List<bool>();
            for (var offset = 0; offset < current.Count; offset++)
            {
                var text = new string(current[offset].Text.Where(c => !Opens(c) && !Closes(c)).ToArray());
                if (TextRules.TrimSpaces(text).Length == 0) continue;
                shown.Add(current[offset] with { Text = text });
                shownGlue.Add(shown.Count > 1 && currentGlue[offset]);
            }
            if (shown.Count > 0)
            {
                groups.Add(new BackingGroup(groups.Count, RunsText(shown, shownGlue).Trim(), shown[0].Start, shown[^1].End, shown, shownGlue));
            }
            current = [];
            currentGlue = [];
        }

        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            var joins = index < glue.Count && glue[index];
            var opening = word.Text.Any(Opens);
            var closing = word.Text.Any(Closes);
            if (opening) inside = true;
            if (inside)
            {
                current.Add(word);
                currentGlue.Add(current.Count > 1 && joins);
                removedBefore = true;
            }
            else
            {
                main.Add(word);
                // The word after a lifted group no longer follows what it was glued to.
                mainGlue.Add(!removedBefore && joins);
                removedBefore = false;
            }
            if (closing && inside)
            {
                inside = false;
                Flush();
            }
        }
        Flush();
        return new BackingSplit(main, mainGlue, groups);
    }

    /// <summary>The words as one string: glued runs written together, runs apart (the Mac's <c>LyricRuns.text</c>).</summary>
    private static string RunsText(List<LyricWord> words, List<bool> glue)
    {
        var labels = new List<string>();
        var start = 0;
        for (var index = 1; index <= words.Count; index++)
        {
            if (index < words.Count && glue[index]) continue;
            var label = string.Concat(words.Skip(start).Take(index - start).Select(word => word.Text)).Trim();
            if (label.Length > 0) labels.Add(label);
            start = index;
        }
        return string.Join(' ', labels);
    }
}
