using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;

namespace Lunalyr.Core.Compositions;

/// <summary>
/// Which lines of a song move gently (the Mac's <c>WordAccentPlan.gentleLines</c>, 16 Nov 2026): each line read the one
/// way the Mac reads it for every layout (its rolling glue, the lifted backing vocals left out), so every layout and
/// every seek agrees. Kept for the last few songs.
/// </summary>
public static class ActionRepeats
{
    private static readonly Lock Gate = new();
    private static readonly List<(IReadOnlyList<LyricLine> Lines, WordAccentLists Lists, bool Lifts, HashSet<int> Gentle)> Memo = [];

    /// <summary>The indices of <paramref name="lines"/> whose action word is a gentle repeat.</summary>
    public static IReadOnlySet<int> Of(IReadOnlyList<LyricLine> lines, WordAccentLists lists, IWordBreaker breaker, bool liftsBacking)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(lists);
        lock (Gate)
        {
            foreach (var entry in Memo)
                if (ReferenceEquals(entry.Lines, lines) && entry.Lists.Equals(lists) && entry.Lifts == liftsBacking) return entry.Gentle;
        }
        var runs = new IReadOnlyList<string>?[lines.Count];
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Words.Count == 0) continue;
            IReadOnlyList<LyricWord> words = line.Words;
            IReadOnlyList<bool> glue = LyricRuns.RollingGlue(line, words, breaker);
            if (liftsBacking)
            {
                var split = BackingVocals.Split(words, glue);
                words = split.Main;
                glue = split.MainGlue;
            }
            runs[index] = LyricRuns.Labels(words, glue);
        }
        var gentle = WordAccents.GentleLines(runs, lists);
        lock (Gate)
        {
            if (Memo.Count > 8) Memo.RemoveAt(0);
            Memo.Add((lines, lists, liftsBacking, gentle));
        }
        return gentle;
    }
}
