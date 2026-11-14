// Editorial's typography. The line settings, word metrics and row layout are re-implemented from the behaviour
// description in docs/compositions/editorial.md (the Mac's ScatteredTypography, LyricLineStyle and FlowLayout trace back
// to the squashed Verso import). The run structure, token frames and run frames are Lunalyr's own (the Mac's
// ScatteredTypography.layout, ScatteredWordFrame.state, ScatteredRunFrame.make) and ported as they are, as are Test
// Build 88's focal word, size tiers and composition (ScatteredTypography.focalRun and the metrics' tiers, FlowPlan, and
// FlowLayout's forced breaks, balanced rows, row shifts and field width), which are Lunalyr's own lines.

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
/// <param name="IsFocal">The line's focal word: set largest and heaviest, so each line has one place for the eye to land.</param>
/// <param name="BreakBefore">Starts a new row (Editorial and Constellation + Editorial give the focal word a row of its own).</param>
public readonly record struct RunMetrics(double Size, bool IsHero, double Rotation, double WaveOffset, bool Uppercased,
                                         bool AlternateFace, bool IsFocal = false, bool BreakBefore = false);

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
    /// <param name="composition">The layout the line is set in: its size tiers, heights, turns and second face.</param>
    /// <param name="spread">The constellations' Spread setting (1 by default).</param>
    /// <param name="editorialAmount">How much of the second face ("Editorial amount", 0–1), with <paramref name="editorial"/>.</param>
    public static EditorialLine Layout(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue, double baseSize,
                                       double minimumSize, int lineIndex, bool wordTimed, bool editorial,
                                       IReadOnlyList<string?>? readings = null, double moodIntensity = 1,
                                       CompositionKind composition = CompositionKind.Editorial, double spread = 1,
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
        var focal = wordTimed ? FocalRun(sources, average) : null;
        var tokens = new List<EditorialToken>(visible.Count);
        var runs = new List<EditorialRun>(ranges.Count);
        for (var r = 0; r < ranges.Count; r++)
        {
            var (start, end) = ranges[r];
            var source = sources[r];
            var metrics = Metrics(r, source, average, setting, baseSize, minimumSize, wordTimed, editorial, moodIntensity,
                                  composition, spread, fullness, editorialAmount, focal, ranges.Count);
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
    /// The run a line is built around (the Mac's <c>ScatteredTypography.focalRun</c>): the word the singer leans on
    /// longest, else the longest word that carries meaning (counted up to ten characters). Function words lead only when
    /// nothing else is there, and ties go to the later word, so a line resolves forwards. Null for a line of no words.
    /// </summary>
    public static int? FocalRun(IReadOnlyList<LyricWord> sources, double average)
    {
        ArgumentNullException.ThrowIfNull(sources);
        int? best = null;
        var bestScore = double.NegativeInfinity;
        for (var index = 0; index < sources.Count; index++)
        {
            var cleaned = TrimPunctuationAndSpaces(sources[index].Text.ToLowerInvariant());
            if (cleaned.Length == 0) continue;
            var score = Held(sources[index], average) * 6 + Math.Min(TextElements.Count(cleaned), 10);
            if (FunctionWords.Contains(cleaned)) score -= 20;
            if (score >= bestScore) { bestScore = score; best = index; }
        }
        return best;
    }

    /// <summary>A function word ("the", "and"), lower-cased and trimmed of punctuation: the small tier.</summary>
    public static bool IsFunctionWord(string text) =>
        FunctionWords.Contains(TrimPunctuationAndSpaces((text ?? "").ToLowerInvariant()));

    /// <summary>
    /// Deliberate heights for the constellations' stars, in ems: up, down, small, large, so the links between
    /// neighbours always run on a slant and the figure never collapses into a ruled line.
    /// </summary>
    public static IReadOnlyList<double> StarHeights { get; } = [-0.55, 0.40, -0.20, 0.62, -0.42, 0.18, 0.52, -0.30];

    /// <summary>
    /// A layout's three size tiers, in ems of the base size: the focal word, ordinary words and small function words.
    /// They stay within a ratio of about 1.8, so the smallest word is still easily read.
    /// </summary>
    public static (double Focal, double Normal, double Minor) Tiers(CompositionKind composition) => composition switch
    {
        CompositionKind.Editorial => (1.08, 0.74, 0.60),
        CompositionKind.Constellation => (1.04, 0.70, 0.60),
        CompositionKind.ConstellationEditorial => (1.06, 0.70, 0.60),
        CompositionKind.Echo => (0.98, 0.80, 0.66),
        _ => (1.04, 0.72, 0.60),
    };

    /// <summary>
    /// How run <paramref name="index"/> of a line is set (see docs/compositions/editorial.md and scattered.md). A
    /// word-timed line has three size tiers: the focal word (<paramref name="focal"/>), ordinary words and function
    /// words. Editorial sets the words before the focal one small (its kicker) and the focal word on a row of its own,
    /// level and upright; the constellations lift their stars to deliberate heights around the focal word; Echo is one
    /// calm level line; Scattered tilts and lifts each word a little by its hash and keeps the focal word level.
    /// </summary>
    /// <param name="fullness">The line's run count over seven, at most 1: short constellation lines reach less.</param>
    /// <param name="count">How many runs the line has (a one-run line never breaks).</param>
    public static RunMetrics Metrics(int index, LyricWord word, double averageDuration, LineSetting setting, double baseSize,
                                     double minimumSize, bool wordTimed, bool editorial, double moodIntensity = 1,
                                     CompositionKind composition = CompositionKind.Editorial, double spread = 1,
                                     double fullness = 1, double editorialAmount = 1, int? focal = null, int count = 1)
    {
        var hash = RunHash(word.Text, index);
        var held = wordTimed ? Held(word, averageDuration) : 0;
        var hero = held > 0.25;
        var jitter = (double)((hash / 100) % 5) / 5;
        // "Editorial amount" (Lunalyr's own): the second face spreads with it; at 1 every eligible run takes it.
        var amount = !editorial ? 0 : double.IsFinite(editorialAmount) ? Math.Clamp(editorialAmount, 0, 1) : 1;
        // Whether the hash lets this run take the second face at the current amount.
        var takesAlternate = (double)((hash / 700_000) % 100) < amount * 100;
        var everyThird = (hash / 10_000) % 3 == 0;
        // A line without word timing is read as one phrase: one size, level and upright.
        if (!wordTimed)
            return new RunMetrics(Math.Max(baseSize * 0.68, minimumSize), false, 0, 0, false,
                                  amount > 0 && !hero && everyThird && takesAlternate);
        var isFocal = index == focal;
        var isMinor = !isFocal && IsFunctionWord(word.Text);
        var tiers = Tiers(composition);
        // A held word that is not the focus still sits a step up.
        var size = isFocal ? tiers.Focal : isMinor ? tiers.Minor
            : tiers.Normal + 0.03 * jitter + 0.14 * setting.SizeContrast * held;
        // Editorial's kicker (the words before the focal one) is set small: kicker, headline, then text.
        var kicker = focal is { } before && index < before;
        if (composition == CompositionKind.Editorial && !isFocal && kicker) size = tiers.Minor;
        size = Math.Min(size, tiers.Focal) * baseSize;

        var alternate = false;
        var uppercased = hero && setting.CapitalHeroes;
        double rotation = 0, offset = 0;
        var breakBefore = false;
        switch (composition)
        {
            case CompositionKind.Editorial:
                // A spread: a small kicker, the focal word alone on its own row in the second face, then the rest.
                if (focal is { } e) breakBefore = e > 0 ? index == e || index == e + 1 : index == e + 1;
                alternate = amount > 0 && takesAlternate && (isFocal || (!kicker && everyThird));
                break;
            case CompositionKind.ConstellationEditorial:
                // The focal word is the bright star on a row of its own; the others spread above and below it as
                // satellites, every other one in the second face.
                if (focal is { } c) breakBefore = (index == c && c > 0) || index == c + 1;
                alternate = amount > 0 && takesAlternate && (isFocal || index % 2 == 0);
                if (!isFocal) offset = StarHeights[(index + 3) % StarHeights.Count] * 0.45 * baseSize * spread;
                break;
            case CompositionKind.Constellation:
                // A field of stars at deliberate heights; the focal word holds its row's line.
                if (!isFocal)
                {
                    var reach = (0.45 + 0.55 * fullness) * spread;
                    var wobble = (double)((hash / 7) % 100) / 100 - 0.5;
                    offset = (StarHeights[index % StarHeights.Count] + 0.18 * wobble) * baseSize * reach;
                }
                alternate = amount > 0 && !isFocal && everyThird && takesAlternate;
                break;
            case CompositionKind.Echo:
                // One calm level line: the repeats behind it carry the idea.
                alternate = amount > 0 && !isFocal && everyThird && takesAlternate;
                break;
            default:
                // Scattered: words set loose, each tilted and lifted a little off the baseline by its own hash. The
                // focal word stays level, the anchor the loose words gather round.
                if (!isFocal)
                {
                    var lift = (double)((hash / 13) % 100) / 100 - 0.5;
                    offset = lift * 0.52 * baseSize * (0.5 + 0.5 * moodIntensity) + Math.Sin(index * 0.85) * setting.Wave * baseSize;
                    rotation = ((long)((hash / 1000) % 9) - 4) * 0.7 * Math.Max(0.6, setting.RotationSpread) * moodIntensity;
                }
                uppercased = (hero || isFocal) && setting.CapitalHeroes;
                alternate = amount > 0 && !isFocal && everyThird && takesAlternate;
                break;
        }
        return new RunMetrics(Math.Max(size, minimumSize), hero, rotation, offset, uppercased, alternate, isFocal,
                              breakBefore && count > 1);
    }
}

/// <summary>
/// How a flow layout uses the stage (the Mac's <c>FlowPlan</c>, Lunalyr's own): the measure it wraps at, its gaps, and
/// how its rows sit. Lengths are in ems of the line's base size, so a line composes the same way on a small window and
/// a wide display.
/// </summary>
/// <param name="Measure">Wrap width; null wraps only at the stage's edge.</param>
/// <param name="Balanced">A line that needs two rows is parted near its middle, not one word left alone on a row.</param>
/// <param name="RowShifts">Per row, cycling, a sideways shift from the row's aligned place (only with two rows or more).</param>
/// <param name="Field">The narrowest the block may be: a flush-left layout keeps one left edge from line to line.</param>
public readonly record struct FlowPlan(double Spacing = 0.02, double LineSpacing = 0.30,
                                       RowAlignment Alignment = RowAlignment.Center, double? Measure = null,
                                       bool Balanced = false, double[]? RowShifts = null, double? Field = null)
{
    /// <summary>The spacing unit: 8 points at the gallery's 46-point base size.</summary>
    public const double Unit = 8.0 / 46.0;

    /// <summary>The plan for a line in <paramref name="composition"/> whose setting aligns its rows <paramref name="rows"/>.</summary>
    public static FlowPlan Make(CompositionKind composition, RowAlignment rows, bool wordTimed, double fullness)
    {
        // A line sung as one phrase is set as one level sentence.
        if (!wordTimed) return new FlowPlan(Spacing: composition.HasConnections() ? 0.06 : 0.02);
        switch (composition)
        {
            case CompositionKind.Editorial:
                // A flush-left column of fixed measure, centred on the stage: every line hangs from the same edge.
                return new FlowPlan(0.02, 0, RowAlignment.Leading, 10, true, null, 10);
            case CompositionKind.Constellation:
            {
                // A wide field, rows stepping against each other, with real gaps so the hairlines can be seen.
                var step = 6 * Unit;
                double[] shifts = rows == RowAlignment.Trailing ? [step, -step] : [-step, step];
                return new FlowPlan(4 * Unit, (fullness < 0.6 ? 2 : 1) * Unit, RowAlignment.Center, 12, true, shifts);
            }
            case CompositionKind.ConstellationEditorial:
                // Satellites spread wide above and below the focal star.
                return new FlowPlan(5 * Unit, Unit, RowAlignment.Center, 13, true);
            case CompositionKind.Echo:
                return new FlowPlan(0.02, 0, RowAlignment.Center, 13, true);
            default:
            {
                // Scattered: a compact block whose rows step left and right of each other, so the line breaks loose
                // of a single baseline.
                double[] shifts = rows switch
                {
                    RowAlignment.Leading => [-9 * Unit, 7 * Unit, -2 * Unit],
                    RowAlignment.Trailing => [9 * Unit, -7 * Unit, 2 * Unit],
                    _ => [-5 * Unit, 9 * Unit, -9 * Unit],
                };
                return new FlowPlan(0.02, 0, RowAlignment.Center, 9, true, shifts);
            }
        }
    }
}

/// <summary>
/// Constellation's fill (the Mac's next/tester-fixes, 15 Nov 2026; testers saw word-timed lines bunched in the middle,
/// most on a 21:9 display): the gaps between words in a row, and between rows, grow toward a target block of 75% of the
/// stage each way, up to 2.5 ems between words and 1.2 ems between rows. Gaps only grow, so words never overlap.
/// </summary>
public readonly record struct FlowFill(double TargetWidth, double MaxWordGap, double TargetHeight, double MaxRowGap)
{
    /// <summary>The share of the stage a filled line aims for, each way.</summary>
    public const double TargetShare = 0.75;

    /// <summary>
    /// A gap for <paramref name="items"/> laid out at <paramref name="natural"/> (gaps included): clamp(gap + (target −
    /// natural) / (items − 1), gap, max(gap, most)); <paramref name="gap"/> itself for fewer than two.
    /// </summary>
    public static double Gap(double gap, double target, double natural, int items, double most) =>
        items < 2 || !double.IsFinite(target) || !double.IsFinite(natural)
            ? gap
            : Math.Clamp(gap + (target - natural) / (items - 1), gap, Math.Max(gap, most));

    /// <summary>Constellation's fill and measure on a stage of <paramref name="stage"/> with base size <paramref name="em"/>.</summary>
    public static (FlowFill Fill, double Measure) Constellation(SizeD stage, double em)
    {
        double targetWidth = TargetShare * stage.Width, targetHeight = TargetShare * stage.Height;
        var measure = Math.Min(targetWidth, Math.Max(12 * em, 0.7 * targetWidth));
        return (new FlowFill(targetWidth, 2.5 * em, targetHeight, 1.2 * em), measure);
    }
}

/// <summary>
/// The flow layouts' rows: slots fill rows greedily in reading order, each row aligned within the line's width, a slot
/// too wide for the stage kept on a row of its own (see docs/compositions/editorial.md); a slot may start a row of its
/// own, and the rows may be balanced and stepped (Lunalyr's, Test Build 88).
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
    /// <param name="breaks">Per slot: starts a new row (the focal word's own row); empty for none.</param>
    /// <param name="balanced">Evens out the rows: the narrowest measure that still takes no more rows (Lunalyr's).</param>
    /// <param name="rowShifts">Per row, cycling, a sideways shift from its aligned place, used only when there are two
    /// rows or more (mirrored for a right-to-left line); the block is widened by twice the largest so rows stay inside.</param>
    /// <param name="field">The narrowest the block may be (never past <paramref name="maxWidth"/>): rows align inside it.</param>
    /// <param name="fill">Widens the gaps toward a target block (Constellation, <see cref="FlowFill"/>); null keeps them.</param>
    public static SizeD Place(ReadOnlySpan<SizeD> slots, double maxWidth, double spacing, double lineSpacing,
                              RowAlignment alignment, bool rightToLeft, Span<PointD> centres,
                              ReadOnlySpan<bool> breaks = default, bool balanced = false,
                              ReadOnlySpan<double> rowShifts = default, double? field = null, FlowFill? fill = null)
    {
        var measure = maxWidth;
        if (balanced && double.IsFinite(maxWidth) && slots.Length > 1)
        {
            var count = RowCount(slots, breaks, maxWidth, spacing);
            if (count > 1)
            {
                // The narrowest measure that still takes no more rows.
                double low = 0, high = maxWidth;
                foreach (var slot in slots) low = Math.Max(low, slot.Width);
                for (var step = 0; step < 14 && high - low > 1; step++)
                {
                    var middle = (low + high) / 2;
                    if (RowCount(slots, breaks, middle, spacing) > count) low = middle;
                    else high = middle;
                }
                measure = high;
            }
        }
        // First pass: the block's size (widest row and its shift, rows' heights and gaps).
        double width = 0, height = 0;
        var rows = 0;
        for (var start = 0; start < slots.Length;)
        {
            var end = RowEnd(slots, breaks, start, measure, spacing, out var rowWidth, out var rowHeight);
            height += rowHeight + (rows > 0 ? lineSpacing : 0);
            rows++;
            start = end;
        }
        // Filling: the rows' gap grows toward the target height (gaps only grow, so nothing overlaps).
        if (fill is { } grow && rows >= 2)
        {
            lineSpacing = FlowFill.Gap(lineSpacing, grow.TargetHeight, height, rows, grow.MaxRowGap);
            height = 0;
            for (int start = 0, row = 0; start < slots.Length; row++)
            {
                var end = RowEnd(slots, breaks, start, measure, spacing, out _, out var rowHeight);
                height += rowHeight + (row > 0 ? lineSpacing : 0);
                start = end;
            }
        }
        for (int start = 0, row = 0; start < slots.Length; row++)
        {
            var end = RowEnd(slots, breaks, start, measure, spacing, out var rowWidth, out _);
            rowWidth = Widened(rowWidth, end - start, spacing, fill);
            width = Math.Max(width, rowWidth + 2 * Math.Abs(Shift(rowShifts, row, rows, rightToLeft)));
            start = end;
        }
        if (field is { } least && double.IsFinite(least)) width = Math.Max(width, Math.Min(least, maxWidth));
        // A right-to-left line's leading edge is the right one.
        var aligned = !rightToLeft ? alignment
            : alignment == RowAlignment.Leading ? RowAlignment.Trailing
            : alignment == RowAlignment.Trailing ? RowAlignment.Leading : RowAlignment.Center;
        double y = 0;
        for (int start = 0, row = 0; start < slots.Length; row++)
        {
            var end = RowEnd(slots, breaks, start, measure, spacing, out var rowWidth, out var rowHeight);
            // A filled row's own word gap, the row centred at its new width.
            var gap = fill is { } rowFill && end - start >= 2
                ? FlowFill.Gap(spacing, rowFill.TargetWidth, rowWidth, end - start, rowFill.MaxWordGap) : spacing;
            rowWidth = Widened(rowWidth, end - start, spacing, fill);
            var x = aligned switch
            {
                RowAlignment.Leading => 0,
                RowAlignment.Trailing => width - rowWidth,
                _ => (width - rowWidth) / 2,
            } + Shift(rowShifts, row, rows, rightToLeft);
            for (var k = 0; k < end - start; k++)
            {
                var item = rightToLeft ? end - 1 - k : start + k;
                centres[item] = new PointD(x + slots[item].Width / 2, y + rowHeight / 2);
                x += slots[item].Width + gap;
            }
            y += rowHeight + lineSpacing;
            start = end;
        }
        return new SizeD(width, height);
    }

    /// <summary>A row of <paramref name="items"/> as wide as it is set with its filled word gap.</summary>
    private static double Widened(double rowWidth, int items, double spacing, FlowFill? fill) =>
        fill is { } grow && items >= 2 ? rowWidth + (FlowFill.Gap(spacing, grow.TargetWidth, rowWidth, items, grow.MaxWordGap) - spacing) * (items - 1) : rowWidth;

    private static double Shift(ReadOnlySpan<double> shifts, int row, int rows, bool rightToLeft)
    {
        if (rows <= 1 || shifts.IsEmpty) return 0;
        var value = shifts[row % shifts.Length];
        return rightToLeft ? -value : value;
    }

    private static int RowCount(ReadOnlySpan<SizeD> slots, ReadOnlySpan<bool> breaks, double maxWidth, double spacing)
    {
        var rows = 0;
        for (var start = 0; start < slots.Length; rows++) start = RowEnd(slots, breaks, start, maxWidth, spacing, out _, out _);
        return rows;
    }

    private static int RowEnd(ReadOnlySpan<SizeD> slots, ReadOnlySpan<bool> breaks, int start, double maxWidth, double spacing,
                              out double width, out double height)
    {
        width = slots[start].Width;
        height = slots[start].Height;
        var end = start + 1;
        while (end < slots.Length && !(end < breaks.Length && breaks[end]) && width + spacing + slots[end].Width <= maxWidth)
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
