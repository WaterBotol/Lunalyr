// Ported from the Mac app's LyricLayerView.swift (LyricLayerCache.rollingWindow and wordOffsets): Lunalyr's own code.
// The per-line work (glue, merging, readings) is done once per song here rather than at every change of window.

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>The words a rolling stage holds, and the span of time over which that set does not change.</summary>
public sealed record RollingWindow
{
    public required IReadOnlyList<LyricWord> Words { get; init; }
    public required IReadOnlyDictionary<LyricWord, string> Readings { get; init; }
    /// <summary>The window is exactly this set of words while <c>From &lt;= time &lt; Until</c>.</summary>
    public required double From { get; init; }
    public required double Until { get; init; }
    /// <summary>The onset of the first word after the window, if any.</summary>
    public double? Following { get; init; }
    /// <summary>Each word's place in the whole song, which its colour is keyed by.</summary>
    public IReadOnlyDictionary<LyricWord, int> Ordinals { get; init; } = new Dictionary<LyricWord, int>();
    /// <summary>The tokens each glued run of the window is made of.</summary>
    public IReadOnlyDictionary<LyricWord, IReadOnlyList<LyricWord>> Parts { get; init; } = new Dictionary<LyricWord, IReadOnlyList<LyricWord>>();
    public IReadOnlyDictionary<LyricWord, double> LineEnds { get; init; } = new Dictionary<LyricWord, double>();

    public bool Holds(double time) => time >= From && time < Until;
}

/// <summary>
/// A song's lines as a rolling stage sets them: each line's runs joined into words (an unspaced run parted into its
/// words first), their readings, and where each line's words begin among the words of the whole song.
/// </summary>
public sealed class RollingSong
{
    /// <summary>How long a rolling word takes to hand the stage to the next one: the hand-off spring has landed by then.</summary>
    public const double RollingHandover = 3;

    private readonly MergedRuns[] _runs;
    private readonly bool[][] _visible;
    private readonly double[] _ends;
    private readonly IReadOnlyList<Lyrics.Parsing.BackingGroup>[] _backing;

    /// <param name="lines">The synced lines.</param>
    /// <param name="breaker">Parts unspaced runs into words (the system's word breaker on Windows).</param>
    /// <param name="readings">A reading per line index, drawn below the words (null or empty: none).</param>
    /// <param name="replace">Readings replace the words they read, where they can be split by word.</param>
    public RollingSong(IReadOnlyList<LyricLine> lines, IWordBreaker breaker, IReadOnlyDictionary<int, string>? readings = null,
                       bool replace = false)
        : this(lines, breaker, null, replace ? RomanizationDisplay.Replace : RomanizationDisplay.Below, readings)
    {
    }

    /// <summary>
    /// A song with its readings as <see cref="LyricRomanizer.Readings"/> works them out, one reading per word (the
    /// Mac's <c>romanized</c>): with "Below lyrics" each rolling word carries the reading of its run, and a reading
    /// that cannot be split by word hangs under the line's first word; with "Replace lyrics" the readings roll in the
    /// words' place. A line with no entry in <paramref name="wordReadings"/> falls back to its reading in
    /// <paramref name="lineReadings"/>, given as one string.
    /// </summary>
    public RollingSong(IReadOnlyList<LyricLine> lines, IWordBreaker breaker,
                       IReadOnlyDictionary<int, IReadOnlyList<LyricWord>>? wordReadings, RomanizationDisplay display,
                       IReadOnlyDictionary<int, string>? lineReadings = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(breaker);
        Lines = lines;
        _runs = new MergedRuns[lines.Count];
        _visible = new bool[lines.Count][];
        _ends = new double[lines.Count];
        _backing = new IReadOnlyList<Lyrics.Parsing.BackingGroup>[lines.Count];
        var offsets = new int[lines.Count + 1];
        var total = 0;
        for (var row = 0; row < lines.Count; row++)
        {
            var line = lines[row];
            IReadOnlyList<LyricWord>? read = null;
            var byWord = display != RomanizationDisplay.Off && wordReadings is not null
                && wordReadings.TryGetValue(row, out read) && read is { Count: > 0 };
            // The Mac's rollingWindow: the words shown (the readings, when they replace them), parted as a rolling
            // stage parts them, and with "Below lyrics" each run's reading.
            var source = byWord && display == RomanizationDisplay.Replace ? read! : line.Words;
            var glue = LyricRuns.RollingGlue(line, source, breaker);
            var pairs = byWord && display == RomanizationDisplay.Below ? LyricReadingPairs.Rolling(read, line, glue) : null;
            var merged = LyricRunParts.Merged(source, glue, pairs);
            // Bracketed backing vocals are lifted out of the rolling words and drawn apart.
            _backing[row] = [];
            if (line.WordTimed)
            {
                var split = Lyrics.Parsing.BackingVocals.Split(merged.Words, new bool[merged.Words.Count]);
                _backing[row] = split.Backing;
                if (split.Backing.Count > 0) merged = merged with { Words = split.Main };
            }
            if (!byWord && display != RomanizationDisplay.Off)
            {
                string? reading = null;
                lineReadings?.TryGetValue(row, out reading);
                merged = WithReading(merged, reading, display == RomanizationDisplay.Replace);
            }
            _runs[row] = merged;
            var shown = new bool[merged.Words.Count];
            for (var i = 0; i < shown.Length; i++) shown[i] = LyricRuns.IsVisible(merged.Words[i].Text);
            _visible[row] = shown;
            offsets[row] = total;
            total += shown.Count(visible => visible);
            _ends[row] = LineFlow.HighlightLineEnd(line.End, line.Words);
        }
        offsets[lines.Count] = total;
        WordOffsets = offsets;
    }

    public IReadOnlyList<LyricLine> Lines { get; }

    /// <summary>Where each line's words begin among the drawn words of the whole song; one entry per line, then the total.</summary>
    public IReadOnlyList<int> WordOffsets { get; }

    /// <summary>Line <paramref name="row"/>'s bracketed backing vocals, lifted out of its rolling words.</summary>
    public IReadOnlyList<Lyrics.Parsing.BackingGroup> Backing(int row) => _backing[row];

    /// <summary>The words line <paramref name="row"/> rolls as.</summary>
    public IReadOnlyList<LyricWord> WordsOf(int row) => _runs[row].Words;

    /// <summary>
    /// A line reading given as one string: split over the line's drawn words when it has exactly one space-separated
    /// part for each, else hung whole under the first drawn word (the Mac's rule for a reading of the whole line).
    /// With <paramref name="replace"/> and a split, each word takes its part instead.
    /// </summary>
    private static MergedRuns WithReading(MergedRuns merged, string? reading, bool replace)
    {
        reading = reading?.Trim();
        if (string.IsNullOrEmpty(reading)) return merged;
        var drawn = new List<int>();
        for (var i = 0; i < merged.Words.Count; i++)
            if (LyricRuns.IsVisible(merged.Words[i].Text)) drawn.Add(i);
        if (drawn.Count == 0) return merged;
        var parts = reading.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == drawn.Count && drawn.Count > 1)
        {
            if (replace)
            {
                var words = merged.Words.ToArray();
                var tokens = new Dictionary<LyricWord, IReadOnlyList<LyricWord>>();
                for (var k = 0; k < drawn.Count; k++)
                {
                    var old = words[drawn[k]];
                    words[drawn[k]] = old with { Text = parts[k] };
                }
                return new MergedRuns(words, new Dictionary<LyricWord, string>(), tokens);
            }
            var paired = new Dictionary<LyricWord, string>();
            for (var k = 0; k < drawn.Count; k++)
            {
                var word = merged.Words[drawn[k]];
                if (LyricReadingPairs.IsDistinct(parts[k], word.Text)) paired[word] = parts[k];
            }
            return merged with { Readings = paired };
        }
        var first = merged.Words[drawn[0]];
        var whole = string.Join(' ', merged.Words.Select(word => word.Text));
        if (!LyricReadingPairs.IsDistinct(reading, whole)) return merged;
        return merged with { Readings = new Dictionary<LyricWord, string> { [first] = reading } };
    }

    /// <summary>
    /// The words of a rolling stage around line <paramref name="index"/> at <paramref name="time"/>: the lines of its
    /// run of word timing (<see cref="LyricPhraseTiming.RollingRange"/>), old words until they have long gone, the
    /// next line's words a little before they are sung (<paramref name="lead"/>).
    /// </summary>
    public RollingWindow Window(int index, double time, double lead)
    {
        var words = new List<LyricWord>();
        var seen = new HashSet<LyricWord>();
        var readings = new Dictionary<LyricWord, string>();
        var ordinals = new Dictionary<LyricWord, int>();
        var parts = new Dictionary<LyricWord, IReadOnlyList<LyricWord>>();
        var lineEnds = new Dictionary<LyricWord, double>();
        double from = double.NegativeInfinity, until = double.PositiveInfinity;
        if (index < 0 || index >= Lines.Count)
            return new RollingWindow { Words = words, Readings = readings, From = from, Until = until };
        var (first, last) = LyricPhraseTiming.RollingRange(index, Lines);
        var candidates = new List<(int Row, LyricWord Word)>();
        for (var row = first; row <= last; row++)
        {
            var runs = _runs[row];
            foreach (var (key, value) in runs.Readings) readings.TryAdd(key, value);
            foreach (var (key, value) in runs.Tokens) parts.TryAdd(key, value);
            var place = WordOffsets[row];
            for (var i = 0; i < runs.Words.Count; i++)
            {
                if (!_visible[row][i]) continue;
                var word = runs.Words[i];
                ordinals.TryAdd(word, place);
                place++;
                candidates.Add((row, word));
            }
        }
        var onsets = candidates.Select(candidate => candidate.Word.Start).Order().ToArray();
        foreach (var (row, word) in candidates)
        {
            // Old words leave twelve seconds after they end, and never before the word after them has taken their
            // place: the stack is anchored on the last word sung.
            int low = 0, high = onsets.Length;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (onsets[middle] <= word.Start) low = middle + 1; else high = middle;
            }
            var successor = low < onsets.Length ? onsets[low] : double.PositiveInfinity;
            var lineEnd = Math.Max(_ends[row], word.End);
            var leaves = Math.Max(Math.Max(word.End + 12, successor + RollingHandover), lineEnd + LineFlow.HighlightFade);
            if (time > leaves) { from = Math.Max(from, leaves); continue; }
            until = Math.Min(until, leaves);
            // Words of the following line join a little before they are sung.
            if (row > index)
            {
                var joins = word.Start - lead;
                if (time < joins) { until = Math.Min(until, joins); continue; }
                from = Math.Max(from, joins);
            }
            if (seen.Add(word))
            {
                words.Add(word);
                lineEnds[word] = lineEnd;
            }
        }
        var sorted = words.OrderBy(word => word.Start).ToList();
        double? following = null;
        if (sorted.Count > 0)
        {
            var lastStart = sorted[^1].Start;
            for (var row = first; row < Math.Min(Lines.Count, last + 3) && following is null; row++)
            {
                var runs = _runs[row];
                for (var i = 0; i < runs.Words.Count; i++)
                {
                    var word = runs.Words[i];
                    if (word.Start > lastStart && _visible[row][i] && !seen.Contains(word)) { following = word.Start; break; }
                }
            }
        }
        return new RollingWindow
        {
            Words = sorted,
            Readings = readings,
            From = from,
            Until = until,
            Following = following,
            Ordinals = ordinals.Where(pair => seen.Contains(pair.Key)).ToDictionary(),
            Parts = parts.Where(pair => seen.Contains(pair.Key)).ToDictionary(),
            LineEnds = lineEnds,
        };
    }
}
