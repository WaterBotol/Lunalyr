// Phrases and word roles for the stage layouts (the Mac's StagePhrases.swift, Build 88; Lunalyr's own code, no line
// from the Verso import, ported as it is).
//
// Kinetic type choreographs phrases, not words. This turns a line's runs (StageRun: glued syllables and CJK characters
// already joined, each run kept whole) into phrases -- the unit a layout composes and moves -- and gives every run a
// role within its phrase:
//
// - focal: the phrase's longest-held content word; it carries the layout's signature move. Exactly one per phrase.
// - function: a little English word ("the", "to", "and", "you"...); it rides with a neighbour and never makes an
//   entrance of its own.
// - held: sung for at least Rules.Held seconds; it may stretch or hold a pose.
// - run: one of several words sung in quick succession; they arrive as one group.
//
// Phrases break at a timing gap, after punctuation, and before they grow longer than Rules.MaxRuns; an overlong
// stretch is cut into near-equal phrases at the best nearby place (not after a function word, not inside a quick run,
// at the biggest gap). A line-timed line (every word at one time) is phrased by punctuation and length alone.
//
// Pure values: nothing here draws or moves. Phrasing caches the analysis per line, so a layout may ask on every frame.

using System.Globalization;
using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>What a run does in its phrase; the one that leads, in the order focal, run, held, function, content.</summary>
public enum StageWordRole { Focal, Run, Held, Function, Content }

/// <summary>Why a phrase starts where it does.</summary>
public enum StagePhraseBreak
{
    /// <summary>The line's first phrase.</summary>
    LineStart,
    /// <summary>A pause of at least <see cref="StagePhraseRules.Gap"/> before it.</summary>
    Gap,
    /// <summary>The phrase before ended with punctuation.</summary>
    Punctuation,
    /// <summary>The stretch was too long for one phrase and was cut here.</summary>
    Length,
}

/// <summary>One run's place in the line's phrasing.</summary>
/// <param name="Run">The run (<see cref="StageRun.Id"/>, its index in the line).</param>
/// <param name="Phrase">The phrase it belongs to (index into <see cref="StagePhrasing.Phrases"/>).</param>
/// <param name="RunGroup">The quick run it belongs to (an index shared by its members), or null.</param>
/// <param name="Duration">How long it is sung: to its end, never past the next word's onset; 0 for a line-timed line.</param>
/// <param name="GapBefore">The pause before it (0 for the first word, overlaps and line-timed lines).</param>
public readonly record struct StageWordInfo(int Run, int Phrase, StageWordRole Role, bool IsFocal, bool IsFunction, bool IsHeld,
                                            int? RunGroup, double Duration, double GapBefore);

/// <summary>A phrase: the runs <see cref="From"/> up to (not including) <see cref="To"/>, consecutive and in reading order.</summary>
/// <param name="Focal">The focal run (<see cref="StageRun.Id"/>).</param>
/// <param name="Start">The earliest onset of its runs.</param>
/// <param name="End">The latest end of its runs (so out-of-order word times still give a span that holds them all).</param>
public readonly record struct StagePhrase(int Index, int From, int To, int Focal, double Start, double End, StagePhraseBreak BreakBefore)
{
    public int Count => To - From;

    public bool Contains(int run) => run >= From && run < To;

    /// <summary>Its run ids, in reading order.</summary>
    public IEnumerable<int> Runs => Enumerable.Range(From, Count);
}

/// <summary>A line's phrases and the role of each of its runs. Equal when every phrase and word is.</summary>
public sealed class StagePhrasing : IEquatable<StagePhrasing>
{
    public StagePhrasing(IReadOnlyList<StagePhrase> phrases, IReadOnlyList<StageWordInfo> words, bool lineTimed, bool rightToLeft)
    {
        Phrases = phrases;
        Words = words;
        LineTimed = lineTimed;
        RightToLeft = rightToLeft;
    }

    public IReadOnlyList<StagePhrase> Phrases { get; }
    /// <summary>One per run, indexed by <see cref="StageRun.Id"/>.</summary>
    public IReadOnlyList<StageWordInfo> Words { get; }
    /// <summary>No word timing: every run starts at once.</summary>
    public bool LineTimed { get; }
    public bool RightToLeft { get; }

    public static StagePhrasing Empty { get; } = new([], [], false, false);

    /// <summary>The phrase run <paramref name="run"/> belongs to.</summary>
    public StagePhrase? PhraseOf(int run) => run >= 0 && run < Words.Count ? Phrases[Words[run].Phrase] : null;

    /// <summary>The phrase being sung at <paramref name="time"/>: the last whose start (less <paramref name="lead"/>) has passed.</summary>
    public int? PhraseIndex(double time, double lead = StageMotion.Lead)
    {
        for (var i = Phrases.Count - 1; i >= 0; i--)
            if (time >= Phrases[i].Start - lead) return i;
        return null;
    }

    public bool Equals(StagePhrasing? other) =>
        other is not null && LineTimed == other.LineTimed && RightToLeft == other.RightToLeft
        && Phrases.SequenceEqual(other.Phrases) && Words.SequenceEqual(other.Words);

    public override bool Equals(object? obj) => Equals(obj as StagePhrasing);

    public override int GetHashCode() => HashCode.Combine(Phrases.Count, Words.Count, LineTimed, RightToLeft);
}

/// <summary>The thresholds the analysis uses (the defaults are the Mac's).</summary>
public sealed record StagePhraseRules
{
    /// <summary>A pause at least this long (s) between two words starts a phrase.</summary>
    public double Gap { get; init; } = 0.35;
    /// <summary>The most runs in a phrase.</summary>
    public int MaxRuns { get; init; } = 4;
    /// <summary>Sung at least this long (s), a word is held.</summary>
    public double Held { get; init; } = 0.8;
    /// <summary>Onsets closer than this (s) chain into a quick run…</summary>
    public double RunSpacing { get; init; } = 0.25;
    /// <summary>…of at least this many words.</summary>
    public int RunLength { get; init; } = 3;

    public static StagePhraseRules Default { get; } = new();
}

public static class StagePhrases
{
    /// <summary>Small English words that ride with a neighbour: articles, pronouns, short prepositions, conjunctions, auxiliaries.</summary>
    public static readonly HashSet<string> FunctionWords =
    [
        "a", "an", "the", "this", "that", "these", "those",
        "i", "me", "my", "you", "your", "we", "us", "our", "he", "him", "his", "she", "her", "it", "its",
        "they", "them", "their", "i'm", "i'll", "i've", "i'd", "you're", "you'll", "we're", "it's", "im", "ill",
        "to", "of", "in", "on", "at", "by", "for", "with", "from", "into", "onto", "up", "out", "off",
        "under", "over", "through", "till", "until", "as", "than",
        "and", "or", "but", "so", "if", "nor", "yet", "cause", "'cause",
        "is", "am", "are", "was", "were", "be", "been", "do", "does", "did", "have", "has", "had",
        "can", "will", "would", "could", "should", "just", "not", "no",
    ];

    /// <summary>Whether <paramref name="text"/> is a function word, ignoring case and punctuation round it (and a curly apostrophe inside it).</summary>
    public static bool IsFunctionWord(string text)
    {
        var plain = text.ToLowerInvariant().Replace('\u2019', '\'');
        plain = TrimScalars(plain, rune => Rune.GetUnicodeCategory(rune) is >= UnicodeCategory.ConnectorPunctuation
                                                and <= UnicodeCategory.OtherPunctuation && rune.Value != '\'');
        plain = TrimScalars(plain, Rune.IsWhiteSpace);
        return FunctionWords.Contains(plain);
    }

    /// <summary>Punctuation that ends a phrase when a run ends with it.</summary>
    public static readonly HashSet<char> PhraseEnders =
    [
        ',', '.', ';', ':', '!', '?', '\u2014', '\u2026',
        '\u3001', '\u3002', '\uFF0C', '\uFF01', '\uFF1F', '\uFF1B', '\uFF1A',
        '\u060C', '\u061F', '\u061B',
    ];

    public static bool EndsPhrase(string text)
    {
        var trimmed = text.Trim();
        // A closing quote or bracket after the punctuation still ends it.
        while (trimmed.Length > 0 && "\"'\u201D\u2019)]\u300D\u300F".Contains(trimmed[^1])) trimmed = trimmed[..^1];
        return trimmed.Length > 0 && PhraseEnders.Contains(trimmed[^1]);
    }

    // ---- Cache ----------------------------------------------------------------------------------------------------

    private sealed class Key : IEquatable<Key>
    {
        private readonly string[] _texts;
        private readonly double[] _starts;
        private readonly double[] _ends;
        private readonly bool? _lineTimed;
        private readonly StagePhraseRules _rules;
        private readonly int _hash;

        public Key(IReadOnlyList<StageRun> runs, bool? lineTimed, StagePhraseRules rules)
        {
            _texts = [.. runs.Select(run => run.Text)];
            _starts = [.. runs.Select(run => run.Start)];
            _ends = [.. runs.Select(run => run.End)];
            _lineTimed = lineTimed;
            _rules = rules;
            var hash = new HashCode();
            foreach (var text in _texts) hash.Add(text);
            foreach (var start in _starts) hash.Add(start);
            hash.Add(lineTimed);
            hash.Add(rules);
            _hash = hash.ToHashCode();
        }

        public bool Equals(Key? other) =>
            other is not null && _hash == other._hash && _lineTimed == other._lineTimed && _rules == other._rules
            && _texts.AsSpan().SequenceEqual(other._texts) && _starts.AsSpan().SequenceEqual(other._starts)
            && _ends.AsSpan().SequenceEqual(other._ends);

        public override bool Equals(object? obj) => Equals(obj as Key);

        public override int GetHashCode() => _hash;
    }

    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<Key, StagePhrasing> Cache = [];
    private const int CacheLimit = 64;

    /// <summary>The phrasing of a line's runs, cached by the line (its texts and times), so asking again costs a lookup.</summary>
    public static StagePhrasing Phrasing(IReadOnlyList<StageRun> runs, bool? lineTimed = null, StagePhraseRules? rules = null)
    {
        rules ??= StagePhraseRules.Default;
        var key = new Key(runs, lineTimed, rules);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
        }
        var result = Analyse(runs, lineTimed, rules);
        lock (CacheLock)
        {
            if (Cache.Count >= CacheLimit) Cache.Clear();
            Cache[key] = result;
        }
        return result;
    }

    /// <summary>The phrasing of a line given as words and glue, as the stage gets it.</summary>
    public static StagePhrasing Phrasing(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue, bool? lineTimed = null,
                                         StagePhraseRules? rules = null) =>
        Phrasing(StageRun.Make(words, glue), lineTimed, rules);

    // ---- Analysis -------------------------------------------------------------------------------------------------

    /// <summary>Whether <paramref name="runs"/> carry no word timing: more than one run, all starting together.</summary>
    public static bool LooksLineTimed(IReadOnlyList<StageRun> runs)
    {
        if (runs.Count <= 1) return false;
        var first = runs[0].Start;
        return runs.All(run => Math.Abs(run.Start - first) < 0.01);
    }

    /// <summary>The analysis itself, uncached. <paramref name="lineTimed"/> null decides from the times.</summary>
    public static StagePhrasing Analyse(IReadOnlyList<StageRun> runs, bool? lineTimed = null, StagePhraseRules? rules = null)
    {
        rules ??= StagePhraseRules.Default;
        if (runs.Count == 0) return StagePhrasing.Empty;
        var count = runs.Count;
        var untimed = lineTimed ?? LooksLineTimed(runs);
        var rightToLeft = ScriptDirection.LineIsRightToLeft(runs.Select(run => run.Text));
        var function = runs.Select(run => IsFunctionWord(run.Text)).ToArray();

        // How long each run is sung, and the pause before it.
        var durations = new double[count];
        var gaps = new double[count];
        if (!untimed)
        {
            var hasEnds = runs.Any(run => run.End > run.Start + 0.05);
            var spacings = new List<double>();
            for (var i = 1; i < count; i++)
            {
                var spacing = runs[i].Start - runs[i - 1].Start;
                if (spacing > 0.02) spacings.Add(spacing);
            }
            spacings.Sort();
            var typical = spacings.Count == 0 ? 0 : spacings[spacings.Count / 2];
            for (var index = 0; index < count; index++)
            {
                var run = runs[index];
                double? next = index + 1 < count ? runs[index + 1].Start : null;
                var sung = Math.Max(0, run.End - run.Start);
                if (next is { } n && n > run.Start) sung = Math.Min(sung, n - run.Start);
                durations[index] = sung;
                if (index == 0) continue;
                var previous = runs[index - 1];
                var onsetGap = run.Start - previous.Start;
                if (hasEnds)
                {
                    // A word without an end of its own runs on to the next.
                    var end = previous.End > previous.Start + 0.05 ? previous.End : run.Start;
                    gaps[index] = Math.Max(0, run.Start - end);
                }
                else
                {
                    // No ends at all: a pause is an onset gap well past the line's pace.
                    gaps[index] = Math.Max(0, onsetGap - typical);
                }
            }
        }
        var held = durations.Select(duration => !untimed && duration >= rules.Held).ToArray();

        // Quick runs: chains of onsets closer than RunSpacing.
        var runGroups = new int?[count];
        if (!untimed)
        {
            var chainStart = 0;
            var group = 0;
            void Close(int end)
            {
                if (end - chainStart < rules.RunLength) return;
                for (var index = chainStart; index < end; index++) runGroups[index] = group;
                group++;
            }
            for (var index = 1; index < Math.Max(count, 1); index++)
            {
                var spacing = runs[index].Start - runs[index - 1].Start;
                if (!(spacing >= 0 && spacing < rules.RunSpacing))
                {
                    Close(index);
                    chainStart = index;
                }
            }
            Close(count);
        }

        // Hard breaks: a pause, or punctuation ending the run before.
        var breaks = new StagePhraseBreak?[count];
        breaks[0] = StagePhraseBreak.LineStart;
        for (var index = 1; index < count; index++)
        {
            if (EndsPhrase(runs[index - 1].Text)) breaks[index] = StagePhraseBreak.Punctuation;
            else if (!untimed && gaps[index] >= rules.Gap) breaks[index] = StagePhraseBreak.Gap;
        }
        // Each stretch between hard breaks, cut to length.
        var starts = new List<int>();
        var at = 0;
        while (at < count)
        {
            var end = at + 1;
            while (end < count && breaks[end] is null) end++;
            starts.Add(at);
            foreach (var cut in LengthCuts(at, end, function, runGroups, gaps, rules.MaxRuns))
            {
                starts.Add(cut);
                breaks[cut] = StagePhraseBreak.Length;
            }
            at = end;
        }

        var phrases = new List<StagePhrase>(starts.Count);
        var words = new List<StageWordInfo>(count);
        for (var number = 0; number < starts.Count; number++)
        {
            var first = starts[number];
            var last = number + 1 < starts.Count ? starts[number + 1] : count;
            var focal = FocalRun(first, last, runs, durations, function, untimed);
            double start = double.PositiveInfinity, finish = double.NegativeInfinity;
            for (var id = first; id < last; id++)
            {
                start = Math.Min(start, runs[id].Start);
                finish = Math.Max(finish, Math.Max(runs[id].End, runs[id].Start));
            }
            phrases.Add(new StagePhrase(number, first, last, focal, start, finish, breaks[first] ?? StagePhraseBreak.Length));
            for (var id = first; id < last; id++)
            {
                var role = id == focal ? StageWordRole.Focal
                    : runGroups[id] is not null ? StageWordRole.Run
                    : held[id] ? StageWordRole.Held
                    : function[id] ? StageWordRole.Function : StageWordRole.Content;
                words.Add(new StageWordInfo(id, number, role, id == focal, function[id], held[id], runGroups[id], durations[id], gaps[id]));
            }
        }
        return new StagePhrasing(phrases, words, untimed, rightToLeft);
    }

    /// <summary>
    /// Where to cut a stretch (runs <paramref name="from"/> up to <paramref name="to"/>) longer than
    /// <paramref name="maxRuns"/>: into the fewest near-equal phrases, each cut moved by one if that keeps a function
    /// word with the word after it, keeps a quick run together, or lands on a bigger pause.
    /// </summary>
    public static List<int> LengthCuts(int from, int to, IReadOnlyList<bool> function, IReadOnlyList<int?> groups,
                                       IReadOnlyList<double> gaps, int maxRuns)
    {
        var limit = Math.Max(1, maxRuns);
        var length = to - from;
        var cuts = new List<int>();
        if (length <= limit) return cuts;
        var pieces = (length + limit - 1) / limit;
        var previous = from;
        for (var piece = 1; piece < pieces; piece++)
        {
            // Swift's rounded(): half away from zero.
            var ideal = from + (int)Math.Round((double)length * piece / pieces, MidpointRounding.AwayFromZero);
            var remaining = pieces - piece;
            var best = ideal;
            var bestScore = double.NegativeInfinity;
            foreach (var candidate in (ReadOnlySpan<int>)[ideal, ideal - 1, ideal + 1])
            {
                // Both sides must still fit: this phrase, and what is left for the rest.
                if (!(candidate > previous && candidate - previous <= limit
                      && to - candidate <= remaining * limit && to - candidate >= remaining)) continue;
                var score = -Math.Abs(candidate - ideal) * 0.6;
                if (function[candidate - 1]) score -= 1;
                if (groups[candidate - 1] is { } group && groups[candidate] == group) score -= 2;
                score += Math.Min(gaps[candidate], 1) * 2;
                if (score > bestScore + 1e-9) { bestScore = score; best = candidate; }
            }
            cuts.Add(best);
            previous = best;
        }
        return cuts;
    }

    /// <summary>
    /// The phrase's focal run: its longest-held content word (a function word only when the phrase has nothing else).
    /// Ties within 0.05 s go to the longer word, then the later one, where English puts the stress. A line-timed phrase
    /// has no durations, so its longest word leads.
    /// </summary>
    public static int FocalRun(int from, int to, IReadOnlyList<StageRun> runs, IReadOnlyList<double> durations,
                               IReadOnlyList<bool> function, bool untimed)
    {
        var candidates = new List<int>();
        for (var id = from; id < to; id++) if (!function[id]) candidates.Add(id);
        if (candidates.Count == 0) for (var id = from; id < to; id++) candidates.Add(id);
        var best = candidates[0];
        foreach (var id in candidates.Skip(1))
        {
            var longer = untimed ? 0 : durations[id] - durations[best];
            if (longer > 0.05) { best = id; continue; }
            if (longer < -0.05) continue;
            var letters = TextElements.Count(runs[id].Text) - TextElements.Count(runs[best].Text);
            if (letters >= 0) best = id;
        }
        return best;
    }

    private static string TrimScalars(string text, Func<Rune, bool> trims)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && Rune.DecodeFromUtf16(text.AsSpan(start, end - start), out var rune, out var used) == System.Buffers.OperationStatus.Done
               && trims(rune)) start += used;
        while (end > start && Rune.DecodeLastFromUtf16(text.AsSpan(start, end - start), out var rune, out var used) == System.Buffers.OperationStatus.Done
               && trims(rune)) end -= used;
        return text[start..end];
    }
}
