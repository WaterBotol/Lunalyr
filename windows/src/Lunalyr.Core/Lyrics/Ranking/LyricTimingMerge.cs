using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>
/// The best text with the best timing. When the lyrics that lead on their source (Apple Music's own, Apple Music's via
/// Lyrics+, Musixmatch) are timed by the line only, and another match for the same recording, timed alike, has genuine
/// word timing, the leading text takes those word times: words are paired line by line (case, punctuation, small
/// spelling differences and ad-libs tolerated) and a line takes them only when nearly all its words pair and their
/// times fit the line. Other lines stay line-timed, so the result is word-by-word or mixed timing, and it ranks as the
/// text's source at that timing. The provenance names both ("Musixmatch text · QQ Music word timing"). Lyrics in another
/// script or language, a translation, another version, a shifted or faulty timeline and word stamps spread by a tool
/// never lend timing. Ported from the Mac's LyricTimingMerge.swift (63662fe; Lunalyr's own code).
/// </summary>
public static class LyricTimingMerge
{
    public const string MergedTextMarker = " text · ";
    public const string MergedTimingSuffix = " word timing";

    /// <summary>How far the two matches' line starts may differ at the median.</summary>
    public const double LineStartTolerance = 0.3;

    /// <summary>How far a line's word times may run past the line before it keeps its line timing.</summary>
    public const double LineSlack = 0.3;

    /// <summary>The share of a line's words that must pair, and how many may not.</summary>
    public const double WordShare = 0.85;

    public const int UnpairedWords = 2;

    /// <summary>The share of sung lines that must take word timing for a merge to be kept.</summary>
    public const double LineShare = 0.5;

    public static string MergedProvenanceSource(string text, string timing) => text + MergedTextMarker + timing + MergedTimingSuffix;

    /// <summary>The source of a provenance's text: itself, or the text's in a merge.</summary>
    public static string TextSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.EndsWith(MergedTimingSuffix, StringComparison.Ordinal)) return source;
        var marker = source.LastIndexOf(MergedTextMarker, StringComparison.Ordinal);
        return marker < 0 ? source : source[..marker];
    }

    /// <summary>Which source lent a merge its word timing, or null for lyrics that are not a merge.</summary>
    public static string? TimingDonor(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.EndsWith(MergedTimingSuffix, StringComparison.Ordinal)) return null;
        var marker = source.LastIndexOf(MergedTextMarker, StringComparison.Ordinal);
        return marker < 0 ? null : source[(marker + MergedTextMarker.Length)..^MergedTimingSuffix.Length];
    }

    public static string? TimingDonor(LyricCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.Result is LyricResult.Synced synced ? TimingDonor(synced.Provenance.Source) : null;
    }

    // ---- Choosing what to merge ----

    /// <summary>
    /// <paramref name="accepted"/> with the best line-timed match of a leading source given word timing from another
    /// match, when the merge would outrank every word-timed match in hand. The merge replaces that match.
    /// </summary>
    public static List<LyricCandidate> Merging(IReadOnlyList<LyricCandidate> accepted, LyricQuery query, LyricSourceSnapshot options)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        var donors = accepted.Where(c => IsTimingDonor(c, query)).ToList();
        if (LyricRanking.Choose(donors, query, options) is not { } bestDonor) return [.. accepted];
        // A donor ranked higher is tried first, so the choice does not depend on which answered first.
        var comparer = Comparer<LyricCandidate>.Create((a, b) =>
            LyricRanking.Outranks(a, b, query, options) ? -1 : LyricRanking.Outranks(b, a, query, options) ? 1 : 0);
        var rankedDonors = donors.Order(comparer).ToList();
        var recipients = accepted.Where(c => IsMergeRecipient(c, query)).Order(comparer).ToList();
        foreach (var recipient in recipients)
        {
            if (recipient.Result is not LyricResult.Synced synced) continue;
            (IReadOnlyList<LyricLine> Lines, LyricCandidate Donor, int Count)? best = null;
            foreach (var donor in rankedDonors)
            {
                if (donor.Source == recipient.Source || !SameScript(recipient, donor) || donor.Result is not LyricResult.Synced donorSynced) continue;
                if (LyricAgreementRules.TimelineOffset(LyricAgreementRules.Timing(recipient), LyricAgreementRules.Timing(donor)) is not { } offset
                    || Math.Abs(offset.Median) > LineStartTolerance
                    || MergeWordTiming(synced.Lines, donorSynced.Lines) is not { } merged)
                {
                    continue;
                }
                var count = merged.Count(line => line.WordTimed);
                if (count > (best?.Count ?? 0)) best = (merged, donor, count);
            }
            if (best is not { } chosen) continue;
            var mergedCandidate = recipient with
            {
                Result = new LyricResult.Synced(chosen.Lines, new LyricProvenance(LyricTimingRules.Precision(chosen.Lines),
                    MergedProvenanceSource(synced.Provenance.Source, chosen.Donor.Result.Provenance.Source))),
            };
            if (!LyricRanking.Outranks(mergedCandidate, bestDonor, query, options)) continue;
            return [.. accepted.Select(c => c.Source == recipient.Source ? mergedCandidate : c)];
        }
        return [.. accepted];
    }

    /// <summary>Genuine word timing for the playing version, sound and in step with the others.</summary>
    public static bool IsTimingDonor(LyricCandidate candidate, LyricQuery query)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Result is not LyricResult.Synced || TimingDonor(candidate) is not null || !LyricRanking.FitsVersion(candidate, query)
            || candidate.Agreement.Shift is not null || (candidate.Source == LyricSourceId.Lrclib && candidate.Agreement.TimingOutlier))
        {
            return false;
        }
        return LyricTimingRules.RankedTiming(candidate.Result) == 2 && !LyricTimingRules.HasTimingFault(candidate.Result);
    }

    /// <summary>Line-timed lyrics from a source that leads line timing (Apple Music's own or relayed, Musixmatch), for the playing version.</summary>
    public static bool IsMergeRecipient(LyricCandidate candidate, LyricQuery query)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Result is not LyricResult.Synced || TimingDonor(candidate) is not null || !LyricRanking.FitsVersion(candidate, query)
            || candidate.Agreement.Shift is not null || LyricTimingRules.RankedTiming(candidate.Result) != 1
            || LyricTimingRules.HasTimingFault(candidate.Result))
        {
            return false;
        }
        return LyricRanking.Lead(candidate, 1) < 4;
    }

    private static bool SameScript(LyricCandidate a, LyricCandidate b)
    {
        if (a.LanguageMismatch || b.LanguageMismatch) return false;
        var first = LyricLanguage.ScriptOf(a);
        return first is not null && first == LyricLanguage.ScriptOf(b);
    }

    // ---- Pairing words ----

    /// <summary>
    /// A piece of a line that takes one time: a word in spaced script, a character in unspaced script.
    /// <paramref name="Text"/> keeps the punctuation around it (brackets included, for backing vocals), so the pieces
    /// cover the line; <paramref name="Key"/> is its lower-cased letters and numbers, without accents.
    /// </summary>
    public readonly record struct MergeUnit(string Text, string Key);

    private static readonly HashSet<string> OpeningPunctuation =
        new(["(", "[", "{", "（", "［", "「", "『", "“", "‘", "¿", "¡", "<", "《", "〈"], StringComparer.Ordinal);

    /// <summary>The text elements (user-perceived characters) of <paramref name="text"/>.</summary>
    private static List<string> Characters(string text)
    {
        var characters = new List<string>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) characters.Add(elements.GetTextElement());
        return characters;
    }

    private static bool IsLetterOrNumber(string character) =>
        character.Length > 0 && Rune.DecodeFromUtf16(character, out var rune, out _) == System.Buffers.OperationStatus.Done
        && (Rune.IsLetter(rune) || Rune.IsNumber(rune));

    private static bool IsUnspaced(string character) => character.EnumerateRunes().Any(rune => rune.Value is
        >= 0x0E00 and <= 0x0EFF or >= 0x1000 and <= 0x109F or >= 0x1780 and <= 0x17FF or >= 0x3040 and <= 0x30FF
        or >= 0x31F0 and <= 0x31FF or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
        or >= 0x20000 and <= 0x2FA1F);

    public static List<MergeUnit> MergeUnits(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var units = new List<MergeUnit>();
        var prefix = "";
        foreach (var chunk in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var current = "";
            var afterUnspaced = false;
            void Flush()
            {
                var key = MergeKey(current);
                if (key.Length == 0) return;
                units.Add(new MergeUnit(prefix + current, key));
                prefix = "";
                current = "";
            }
            foreach (var character in Characters(chunk))
            {
                if (IsUnspaced(character))
                {
                    Flush();
                    current = character;
                    Flush();
                    afterUnspaced = true;
                }
                else if (IsLetterOrNumber(character))
                {
                    current += character;
                    afterUnspaced = false;
                }
                else if (OpeningPunctuation.Contains(character))
                {
                    // An opening bracket or quote leads the word after it.
                    Flush();
                    prefix += current + character;
                    current = "";
                }
                else if (MergeKey(current).Length == 0 && afterUnspaced && units.Count > 0)
                {
                    // Punctuation after a character in unspaced script stays with it.
                    units[^1] = units[^1] with { Text = units[^1].Text + character };
                }
                else if (MergeKey(current).Length == 0)
                {
                    prefix += current + character;
                    current = "";
                }
                else
                {
                    current += character;
                }
            }
            Flush();
            prefix += current;
        }
        // Punctuation with no word after it joins the last word.
        if (prefix.Length > 0 && units.Count > 0) units[^1] = units[^1] with { Text = units[^1].Text + prefix };
        return units;
    }

    /// <summary>Lower-cased letters and numbers, without accents, at ordinary width.</summary>
    public static string MergeKey(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Concat(Characters(LyricTextFolding.FoldWidth(text)).Where(IsLetterOrNumber));
    }

    private static int Length(string key) => new StringInfo(key).LengthInTextElements;

    /// <summary>
    /// Whether two pieces are the same word: equal, or long enough and spelt nearly alike: five letters or more, one in
    /// five different at most ("colour"/"color", "thinkin"/"thinking").
    /// </summary>
    public static bool SameMergeWord(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a == b) return true;
        var x = Characters(a);
        var y = Characters(b);
        if (Math.Min(x.Count, y.Count) < 5) return false;
        var row = Enumerable.Range(0, y.Count + 1).ToArray();
        for (var i = 1; i <= x.Count; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= y.Count; j++)
            {
                var saved = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (x[i - 1] == y[j - 1] ? 0 : 1));
                previous = saved;
            }
        }
        return row[y.Count] <= 0.2 * Math.Max(x.Count, y.Count);
    }

    /// <summary>A donor's piece with the times of the words that sing it.</summary>
    private record struct TimedUnit(string Key, double Start, double End);

    private static List<TimedUnit> TimedUnits(LyricLine line)
    {
        // Each letter of the line belongs to one of the source's timed words.
        var owner = new List<int>();
        for (var index = 0; index < line.Words.Count; index++)
        {
            owner.AddRange(Enumerable.Repeat(index, Length(MergeKey(line.Words[index].Text))));
        }
        var units = new List<TimedUnit>();
        var spans = new List<(int First, int Last)>();
        var cursor = 0;
        foreach (var unit in MergeUnits(line.Text))
        {
            var count = Length(unit.Key);
            if (cursor + count > owner.Count) return [];
            spans.Add((owner[cursor], owner[cursor + count - 1]));
            units.Add(new TimedUnit(unit.Key, line.Words[owner[cursor]].Start, line.Words[owner[cursor + count - 1]].End));
            cursor += count;
        }
        if (cursor != owner.Count) return [];
        // Pieces sung as one timed word share its time in equal parts.
        var at = 0;
        while (at < units.Count)
        {
            var last = at;
            while (last + 1 < units.Count && spans[last + 1] == spans[at]) last++;
            if (last > at)
            {
                var start = units[at].Start;
                var step = (units[last].End - start) / (last - at + 1);
                for (var k = at; k <= last; k++)
                {
                    units[k] = units[k] with { Start = start + (step * (k - at)), End = start + (step * (k - at + 1)) };
                }
            }
            at = last + 1;
        }
        return units;
    }

    /// <summary>
    /// <paramref name="recipient"/> with word timing from <paramref name="donor"/> on each line whose words nearly all pair
    /// with words the donor sings around the same time, or null when too few lines do (<see cref="LineShare"/>).
    /// </summary>
    public static IReadOnlyList<LyricLine>? MergeWordTiming(IReadOnlyList<LyricLine> recipient, IReadOnlyList<LyricLine> donor)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(donor);
        var pool = donor.Where(line => !line.IsInstrumental && LyricTimingRules.ValidWordTiming(line)).SelectMany(TimedUnits).ToList();
        if (pool.Count == 0) return null;
        var merged = recipient.ToList();
        var transferred = 0;
        var sung = Enumerable.Range(0, recipient.Count).Where(i => !recipient[i].IsInstrumental && recipient[i].Credit != true).ToList();
        foreach (var index in sung)
        {
            if (Transfer(recipient[index], pool) is not { } words) continue;
            var timed = recipient[index] with { Words = words, WordTimed = true };
            if (LyricTimingRules.ValidWordTiming(timed))
            {
                merged[index] = timed;
                transferred++;
            }
        }
        if (sung.Count == 0 || transferred < LineShare * sung.Count
            || !LyricTimingRules.HasRealWordEvidence(merged.Where(line => !line.IsInstrumental)))
        {
            return null;
        }
        return merged;
    }

    /// <summary>Word times for one line from the donor's pieces sung within it.</summary>
    private static List<LyricWord>? Transfer(LyricLine line, List<TimedUnit> pool)
    {
        var units = MergeUnits(line.Text);
        if (units.Count == 0 || !(line.End > line.Start)) return null;
        var window = pool.Where(u => u.Start >= line.Start - LineSlack && u.Start < line.End).ToList();
        if (window.Count == 0) return null;
        // Longest common subsequence of the pieces, in order.
        int n = units.Count, m = window.Count;
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                table[i, j] = SameMergeWord(units[i].Key, window[j].Key) ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }
        var pairs = new Dictionary<int, int>();
        {
            int i = 0, j = 0;
            while (i < n && j < m)
            {
                if (SameMergeWord(units[i].Key, window[j].Key) && table[i, j] == table[i + 1, j + 1] + 1)
                {
                    pairs[i] = j;
                    i++;
                    j++;
                }
                else if (table[i + 1, j] >= table[i, j + 1]) i++;
                else j++;
            }
        }
        var unpaired = n - pairs.Count;
        if (pairs.Count < WordShare * n || unpaired > UnpairedWords || !pairs.ContainsKey(0) || !pairs.ContainsKey(n - 1)) return null;
        var starts = new double[n];
        var ends = new double[n];
        foreach (var (unit, match) in pairs)
        {
            starts[unit] = window[match].Start;
            ends[unit] = window[match].End;
        }
        // An unpaired word (an ad-lib one source leaves out) shares the time from the paired word before it to the next one.
        var k = 0;
        while (k < n)
        {
            if (pairs.ContainsKey(k))
            {
                k++;
                continue;
            }
            var last = k;
            while (!pairs.ContainsKey(last + 1)) last++;
            double from = starts[k - 1], to = starts[last + 1], step = (to - from) / (last - k + 2);
            for (var index = k - 1; index <= last; index++)
            {
                var offset = index - (k - 1);
                starts[index] = from + (step * offset);
                ends[index] = from + (step * (offset + 1));
            }
            k = last + 1;
        }
        if (starts[0] < line.Start - LineSlack || ends[n - 1] > line.End + LineSlack) return null;
        var words = new List<LyricWord>(n);
        for (var index = 0; index < n; index++)
        {
            var start = Math.Min(Math.Max(starts[index], line.Start), line.End);
            var end = Math.Min(Math.Max(ends[index], start), line.End);
            if (!(end > start) || (index > 0 && start < words[index - 1].Start)) return null;
            words.Add(new LyricWord(units[index].Text, start, end));
        }
        return words;
    }
}
