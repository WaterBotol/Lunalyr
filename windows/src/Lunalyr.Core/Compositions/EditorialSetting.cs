// Editorial's typography. The line settings, word metrics and row layout are re-implemented from the behaviour
// description in docs/compositions/editorial.md (the Mac's ScatteredTypography, LyricLineStyle and FlowLayout trace back
// to the squashed Verso import). The run structure, token frames and run frames are Lunalyr's own (the Mac's
// ScatteredTypography.layout, ScatteredWordFrame.state, ScatteredRunFrame.make) and ported as they are.

using System.Globalization;
using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

public enum RowAlignment { Leading, Center, Trailing }

/// <summary>How one line is set: rows, an undulating baseline, turns, size contrast and capital heroes.</summary>
public readonly record struct LineSetting(RowAlignment Rows, double Wave, double RotationSpread, double SizeContrast,
                                          bool CapitalHeroes)
{
    private static readonly LineSetting[] Cycle =
    [
        new(RowAlignment.Center, 0.00, 1.3, 0.90, true),
        new(RowAlignment.Leading, 0.09, 0.5, 0.45, false),
        new(RowAlignment.Center, 0.15, 2.1, 1.00, true),
        new(RowAlignment.Trailing, -0.10, 0.9, 0.60, false),
        new(RowAlignment.Center, 0.06, 0.0, 0.25, true),
    ];

    /// <summary>The setting for line <paramref name="index"/>: five settings in turn, so neighbouring lines sit differently.</summary>
    public static LineSetting ForLine(int index) => Cycle[(int)(Math.Abs((long)index) % Cycle.Length)];
}

/// <summary>How one run is set.</summary>
/// <param name="Size">Type size, in the caller's units.</param>
/// <param name="IsHero">A word the singer leans on: heavier and larger.</param>
/// <param name="Rotation">Degrees.</param>
/// <param name="WaveOffset">Vertical offset on the line's wave, in the caller's units (positive is down).</param>
/// <param name="Uppercased">Set in capitals.</param>
/// <param name="AlternateFace">Set in the contrasting typeface.</param>
public readonly record struct RunMetrics(double Size, bool IsHero, double Rotation, double WaveOffset, bool Uppercased,
                                         bool AlternateFace);

/// <summary>One drawn token of a line (a word, or a syllable or character of a glued run).</summary>
public sealed record EditorialToken(int Index, LyricWord Source, int Run, ulong Seed, double Cadence, double Order, double Sustain);

/// <summary>Glued tokens as the line places them: one shaped label, one slot.</summary>
public sealed record EditorialRun
{
    public required int Index { get; init; }
    /// <summary>Its tokens: <c>[TokenStart, TokenEnd)</c> in <see cref="EditorialLine.Tokens"/>.</summary>
    public required int TokenStart { get; init; }
    public required int TokenEnd { get; init; }
    public required string Label { get; init; }
    public required RunMetrics Metrics { get; init; }
    /// <summary>The run as one timed word: first onset to last end.</summary>
    public required LyricWord Source { get; init; }
    public required ulong Seed { get; init; }
    /// <summary>Reading position in the line, 0…1: staggers exits.</summary>
    public required double Order { get; init; }
    /// <summary>How held a single-token run is (0 for a glued run, which lights through the shared sweep).</summary>
    public required double Sustain { get; init; }
    public required bool RightToLeft { get; init; }
    /// <summary>Where each token begins and ends along the label, 0…1 by character.</summary>
    public required double[] Boundaries { get; init; }
    public string? Reading { get; init; }
    public int TokenCount => TokenEnd - TokenStart;
    public bool IsGlued => TokenCount > 1;
    /// <summary>Fixed clearance around the ink: the slot never grows with the word.</summary>
    public SizeD Inset => new(Metrics.Size * 0.12, Metrics.Size * 0.05);
}

/// <summary>A line's runs and tokens, worked out once per line.</summary>
public sealed record EditorialLine(IReadOnlyList<EditorialToken> Tokens, IReadOnlyList<EditorialRun> Runs, bool RightToLeft,
                                   LineSetting Setting, bool WordTimed)
{
    /// <summary>The run count as a share of a full line (seven runs), for spacing that grows with the line.</summary>
    public double Fullness => Math.Min(Runs.Count / 7.0, 1);
}

public static class EditorialSetting
{
    /// <summary>Words too slight to be leaned on, however long they are held.</summary>
    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "so", "if", "as", "of", "to", "in", "on", "at", "by", "for", "with",
        "from", "up", "out", "is", "am", "are", "was", "were", "be", "been", "do", "did", "it", "its", "i", "im", "you",
        "youre", "me", "my", "we", "he", "she", "they", "them", "this", "that", "there", "here", "just", "got", "gonna",
        "wanna",
    };

    /// <summary>
    /// The runs of a line and how each is set.
    /// </summary>
    /// <param name="words">The line's words (or a line-timed line's estimated words).</param>
    /// <param name="glue">Per word: continues the previous word's run.</param>
    /// <param name="baseSize">The base type size, in the caller's units.</param>
    /// <param name="minimumSize">The smallest type allowed (11 points), in the caller's units.</param>
    /// <param name="lineIndex">Picks the line's setting.</param>
    /// <param name="wordTimed">Word timing: line-timed lines are read as one level phrase.</param>
    /// <param name="editorial">The editorial range and second face (Editorial itself; not a line-timed phrase).</param>
    /// <param name="readings">A reading per run, by run index; null for none.</param>
    /// <param name="moodIntensity">The track mood's intensity (1 for the neutral mood).</param>
    /// <param name="connections">The constellations: words scattered to different heights, heroes larger, the rest smaller.</param>
    /// <param name="spread">The constellations' Spread setting (1 by default).</param>
    /// <param name="evenRunsAlternate">Constellation + Editorial: the second face goes to every other run rather than every third by hash.</param>
    /// <param name="editorialAmount">How much of the editorial range and second face ("Editorial amount", 0–1), with <paramref name="editorial"/>.</param>
    public static EditorialLine Layout(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue, double baseSize,
                                       double minimumSize, int lineIndex, bool wordTimed, bool editorial,
                                       IReadOnlyList<string?>? readings = null, double moodIntensity = 1,
                                       bool connections = false, double spread = 1, bool evenRunsAlternate = false,
                                       double editorialAmount = 1)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(glue);
        var setting = LineSetting.ForLine(lineIndex);
        var (visible, visibleGlue) = LyricRuns.Visible(words, glue);
        var ranges = LyricRuns.Ranges(visibleGlue, visible.Count);
        var sources = new LyricWord[ranges.Count];
        for (var r = 0; r < ranges.Count; r++)
        {
            var (start, end) = ranges[r];
            if (end - start == 1) { sources[r] = visible[start]; continue; }
            var text = new StringBuilder();
            var last = double.NegativeInfinity;
            for (var i = start; i < end; i++) { text.Append(visible[i].Text); last = Math.Max(last, visible[i].End); }
            sources[r] = new LyricWord(text.ToString().Trim(), visible[start].Start, last);
        }
        double durationSum = 0;
        var durationCount = 0;
        foreach (var source in sources)
        {
            var duration = source.End - source.Start;
            if (duration > 0.01) { durationSum += duration; durationCount++; }
        }
        var average = durationCount == 0 ? 0.3 : durationSum / durationCount;
        var lineRightToLeft = ScriptDirection.LineIsRightToLeft(visible.Select(word => word.Text));
        var span = (double)Math.Max(ranges.Count - 1, 1);
        var fullness = Math.Min(ranges.Count / 7.0, 1);
        var tokens = new List<EditorialToken>(visible.Count);
        var runs = new List<EditorialRun>(ranges.Count);
        for (var r = 0; r < ranges.Count; r++)
        {
            var (start, end) = ranges[r];
            var source = sources[r];
            var metrics = Metrics(r, source, average, setting, baseSize, minimumSize, wordTimed, editorial, moodIntensity,
                                  connections, spread, fullness, evenRunsAlternate, editorialAmount);
            var order = ranges.Count > 1 ? r / span : 0.5;
            var runCadence = r + 1 < ranges.Count ? Math.Max(0.05, sources[r + 1].Start - source.Start)
                : Math.Max(source.End - source.Start, 0.12);
            var labels = new string[end - start];
            for (var i = start; i < end; i++)
            {
                var word = visible[i];
                labels[i - start] = metrics.Uppercased ? word.Text.ToUpperInvariant() : word.Text;
                tokens.Add(new EditorialToken(i, word, r, TimedWordKey.SeedOf(word.Text, word.Start),
                    // A run enters at the pace of the run, not of its first syllable.
                    i == start ? runCadence : OrganicMotion.Cadence(visible, i), order,
                    Easing.Smoothstep((word.End - word.Start - 0.45) / 0.85)));
            }
            var label = labels.Length > 1 ? string.Concat(labels).Trim() : labels[0];
            double total = Math.Max(1, labels.Sum(text => TextElements.Count(text)));
            var boundaries = new double[labels.Length + 1];
            double running = 0;
            for (var k = 0; k < labels.Length; k++)
            {
                running += TextElements.Count(labels[k]);
                boundaries[k + 1] = Math.Min(1, running / total);
            }
            runs.Add(new EditorialRun
            {
                Index = r,
                TokenStart = start,
                TokenEnd = end,
                Label = label,
                Metrics = metrics,
                Source = source,
                Seed = TimedWordKey.SeedOf(source.Text, source.Start),
                Order = order,
                Sustain = end - start == 1 ? tokens[start].Sustain : 0,
                RightToLeft = ScriptDirection.IsRightToLeft(label),
                Boundaries = boundaries,
                Reading = readings is not null && r < readings.Count ? readings[r] : null,
            });
        }
        return new EditorialLine(tokens, runs, lineRightToLeft, setting, wordTimed);
    }

    /// <summary>The run's hash: FNV-1a of its text, mixed with its place in the line.</summary>
    public static ulong RunHash(string text, int index) =>
        RollingWords.Fnv1a(text) ^ unchecked((ulong)((long)index * 2654435761L));

    /// <summary>How held a run is against the line's average (0 for short and function words).</summary>
    public static double Held(LyricWord word, double average)
    {
        if (!(average > 0.01)) return 0;
        var cleaned = TrimPunctuationAndSpaces(word.Text.ToLowerInvariant());
        if (TextElements.Count(cleaned) <= 2 || FunctionWords.Contains(cleaned)) return 0;
        return Easing.Smoothstep(((word.End - word.Start) / average - 1.15) / 0.85);
    }

    private static string TrimPunctuationAndSpaces(string text)
    {
        static bool Trimmed(char c) => char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
        int start = 0, end = text.Length;
        while (start < end && Trimmed(text[start])) start++;
        while (end > start && Trimmed(text[end - 1])) end--;
        return text[start..end];
    }

    /// <summary>
    /// How run <paramref name="index"/> of a line is set (see docs/compositions/editorial.md; the constellations'
    /// differences in scattered.md).
    /// </summary>
    /// <param name="fullness">The line's run count over seven, at most 1: short constellation lines scatter less.</param>
    public static RunMetrics Metrics(int index, LyricWord word, double averageDuration, LineSetting setting, double baseSize,
                                     double minimumSize, bool wordTimed, bool editorial, double moodIntensity = 1,
                                     bool connections = false, double spread = 1, double fullness = 1,
                                     bool evenRunsAlternate = false, double editorialAmount = 1)
    {
        var hash = RunHash(word.Text, index);
        var held = wordTimed ? Held(word, averageDuration) : 0;
        var hero = held > 0.25;
        var jitter = (double)((hash / 100) % 5) / 5;
        var size = hero ? baseSize * (0.58 + 0.30 * setting.SizeContrast * held + 0.05 * jitter)
            : baseSize * (0.64 + 0.06 * jitter);
        var alternate = false;
        // "Editorial amount" (Lunalyr's own, ported from the Mac's ScatteredTypography.metrics): the range widens and the
        // second face spreads with it; at 1 every eligible run takes it, as Editorial always has.
        var amount = double.IsFinite(editorialAmount) ? Math.Clamp(editorialAmount, 0, 1) : 1;
        if (editorial && amount > 0)
        {
            size *= 1 + 0.10 * amount * (hero ? 1 : jitter);
            var eligible = evenRunsAlternate ? index % 2 == 0 : (hash / 10_000) % 3 == 0;
            alternate = !hero && eligible && (double)((hash / 700_000) % 100) < amount * 100;
        }
        if (!wordTimed)
            return new RunMetrics(Math.Max(baseSize * 0.68, minimumSize), false, 0, 0, false, alternate);
        var wave = Math.Sin(index * 0.85) * setting.Wave * baseSize * moodIntensity;
        if (connections)
        {
            // The constellation offset (docs/compositions/scattered.md): a hashed share of 2.1 base sizes plus a sine
            // term, scaled by the line's fullness and the Spread setting.
            var scatter = (double)((hash / 7) % 100) / 100 - 0.5;
            var reach = (0.30 + 0.70 * fullness) * spread;
            wave += (scatter * baseSize * 2.1 + Math.Sin(index * 2.3) * baseSize * 0.42) * reach;
            // Heroes 6% larger, other runs 4% smaller (Lunalyr's factors).
            size *= hero ? 1.06 : 0.96;
        }
        var rotation = ((long)((hash / 1000) % 9) - 4) * 0.35 * setting.RotationSpread * moodIntensity;
        return new RunMetrics(Math.Max(size, minimumSize), hero, rotation, wave, hero && setting.CapitalHeroes, alternate);
    }
}

/// <summary>
/// Editorial's rows: slots fill rows greedily in reading order, each row aligned within the line's width, a slot too
/// wide for the stage kept on a row of its own (see docs/compositions/editorial.md).
/// </summary>
public static class EditorialFlow
{
    /// <summary>A run's fixed slot: its ink turned by its rotation plus half a degree, plus its inset at each side.</summary>
    public static SizeD Slot(SizeD ink, double rotation, SizeD inset, double waveOffset = 0)
    {
        var box = ConstellationGeometry.WordEnvelope(ink.Width, ink.Height, 1, Math.Abs(rotation) + 0.5);
        return new SizeD(box.Width + inset.Width * 2, box.Height + inset.Height * 2 + Math.Abs(waveOffset) * 2);
    }

    /// <summary>
    /// Places <paramref name="slots"/> into rows no wider than <paramref name="maxWidth"/> and writes each slot's centre
    /// (relative to the top-left of the block) into <paramref name="centres"/>; returns the block's size.
    /// </summary>
    public static SizeD Place(ReadOnlySpan<SizeD> slots, double maxWidth, double spacing, double lineSpacing,
                              RowAlignment alignment, bool rightToLeft, Span<PointD> centres)
    {
        // First pass: the block's size (widest row, rows' heights and gaps).
        double width = 0, height = 0;
        var rows = 0;
        for (var start = 0; start < slots.Length;)
        {
            var end = RowEnd(slots, start, maxWidth, spacing, out var rowWidth, out var rowHeight);
            width = Math.Max(width, rowWidth);
            height += rowHeight + (rows > 0 ? lineSpacing : 0);
            rows++;
            start = end;
        }
        // A right-to-left line's leading edge is the right one.
        var aligned = !rightToLeft ? alignment
            : alignment == RowAlignment.Leading ? RowAlignment.Trailing
            : alignment == RowAlignment.Trailing ? RowAlignment.Leading : RowAlignment.Center;
        double y = 0;
        for (var start = 0; start < slots.Length;)
        {
            var end = RowEnd(slots, start, maxWidth, spacing, out var rowWidth, out var rowHeight);
            var x = aligned switch
            {
                RowAlignment.Leading => 0,
                RowAlignment.Trailing => width - rowWidth,
                _ => (width - rowWidth) / 2,
            };
            for (var k = 0; k < end - start; k++)
            {
                var item = rightToLeft ? end - 1 - k : start + k;
                centres[item] = new PointD(x + slots[item].Width / 2, y + rowHeight / 2);
                x += slots[item].Width + spacing;
            }
            y += rowHeight + lineSpacing;
            start = end;
        }
        return new SizeD(width, height);
    }

    private static int RowEnd(ReadOnlySpan<SizeD> slots, int start, double maxWidth, double spacing, out double width,
                              out double height)
    {
        width = slots[start].Width;
        height = slots[start].Height;
        var end = start + 1;
        while (end < slots.Length && width + spacing + slots[end].Width <= maxWidth)
        {
            width += spacing + slots[end].Width;
            height = Math.Max(height, slots[end].Height);
            end++;
        }
        return end;
    }
}

/// <summary>What one token shows on one frame, with its share of the line's exit.</summary>
public readonly record struct EditorialTokenFrame(WordPresentation Presentation, double Heat, double Fill, double Glow,
                                                  double DriftX, double DriftY, double DriftScale, double DriftAngle,
                                                  double Leave)
{
    /// <summary>
    /// A token at <paramref name="time"/>: its motion (heat, gesture, exit), its presentation (reveal and the
    /// next-word preview), the karaoke fill (1 when words are revealed one by one) and the glow of a held word.
    /// </summary>
    public static EditorialTokenFrame Make(EditorialToken token, int? nextIndex, double time, double energy,
                                           double exitStart, bool reduceMotion, double eligibleSince,
                                           WordRevealOptions options)
    {
        var word = token.Source;
        var motion = LyricMotion.ForWord(time, word.Start, word.End, token.Cadence, exitStart, token.Order, token.Seed,
                                         energy, reduceMotion);
        var presentation = WordPresentation.Make(word, token.Index, nextIndex, time, options, motion, eligibleSince);
        var progress = Math.Min(Math.Max((time - word.Start) / Math.Max(word.End - word.Start, 0.001), 0), 1);
        var fill = options.RevealsWords ? 1 : progress;
        var heat = motion.Heat;
        var glow = token.Sustain > 0.01 && heat > 0.01
            ? Math.Min(1, heat) * token.Sustain * Easing.Smoothstep(progress / 0.45) : 0;
        return new EditorialTokenFrame(presentation, heat, fill, glow, motion.X, motion.Y, motion.Scale, motion.Angle,
                                       motion.Leave);
    }
}

/// <summary>
/// What one run shows on one frame, gathered from its tokens: a single word passes straight through; a glued run
/// lights by character share and swells with whichever token is being sung, its tokens appearing one by one.
/// </summary>
public readonly record struct EditorialRunFrame(WordPresentation Presentation, double Heat, double Fill, double Glow,
                                                double DriftX, double DriftY, double DriftScale, double DriftAngle,
                                                double Visible, bool RevealsByToken, double Leave)
{
    /// <summary>The reveal mask does not cover a glued run's reading, which comes with its first token.</summary>
    public double ReadingOpacity(ReadOnlySpan<double> tokenOpacities) =>
        Presentation.Preview ? 0 : RevealsByToken && tokenOpacities.Length > 0 ? tokenOpacities[0] : 1;

    /// <summary>
    /// The run's frame; per-token opacities go into <paramref name="tokenOpacities"/> (one per token) when the run
    /// reveals by token.
    /// </summary>
    public static EditorialRunFrame Make(EditorialRun run, ReadOnlySpan<EditorialTokenFrame> tokens, bool reveals,
                                         Span<double> tokenOpacities)
    {
        var first = tokens[run.TokenStart];
        if (!run.IsGlued)
        {
            return new EditorialRunFrame(first.Presentation, first.Heat, first.Fill, first.Glow, first.DriftX, first.DriftY,
                first.DriftScale, first.DriftAngle, first.Presentation.Opacity, false, first.Leave);
        }
        double heat = 0, glow = 0, fill = 0, visible = 0, x = 0, y = 0, scale = 1, angle = 0;
        for (var offset = 0; offset < run.TokenCount; offset++)
        {
            var frame = tokens[run.TokenStart + offset];
            var share = run.Boundaries[offset + 1] - run.Boundaries[offset];
            heat = Math.Max(heat, frame.Heat);
            glow = Math.Max(glow, frame.Glow);
            fill += share * frame.Fill;
            x += frame.DriftX; y += frame.DriftY; scale += frame.DriftScale - 1; angle += frame.DriftAngle;
            if (offset < tokenOpacities.Length) tokenOpacities[offset] = frame.Presentation.Opacity;
            visible = Math.Max(visible, frame.Presentation.Opacity);
        }
        // The run enters with its first token; its tokens then appear through the reveal mask.
        var presentation = reveals ? first.Presentation with { Opacity = 1 } : first.Presentation;
        return new EditorialRunFrame(presentation, heat, fill, glow, x, y, scale, angle, visible, reveals, first.Leave);
    }

    /// <summary>How much the word in focus grows; under Reduce Motion nothing zooms.</summary>
    public static double Swell(double heat, double glow, double gesture, bool reduceMotion) =>
        reduceMotion ? 1 : (1 + 0.07 * heat + 0.03 * glow) * gesture;

    /// <summary>A swell held to the slot's inset, so emphasis never pushes a word into its neighbours.</summary>
    public static double CappedSwell(double swell, double inset, double width) =>
        Math.Min(swell, 1 + Math.Max(0.06, 1.8 * inset / Math.Max(1, width)));
}
