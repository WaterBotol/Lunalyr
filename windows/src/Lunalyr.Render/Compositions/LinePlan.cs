using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Compositions;

/// <summary>One run's baked ink: its label, the reading under it, and where it sits in the line.</summary>
internal sealed class RunInk : IDisposable
{
    public required Ink Label { get; init; }
    /// <summary>The label in the colour a sweep fills it with as it is sung ("Sung sweep"); null when the ink doesn't sweep.</summary>
    public Ink? Sung { get; init; }
    public Ink? Reading { get; init; }
    /// <summary>Where each token's share of the reading falls along it, so it fills in as the run is sung; null when
    /// the reading shows whole.</summary>
    public ReadingSlices? ReadingSlices { get; init; }
    /// <summary>Label and reading centres relative to the unit's centre, layout pixels.</summary>
    public Vector2 LabelOffset { get; init; }
    public Vector2 ReadingOffset { get; init; }
    public SizeD Unit { get; init; }
    public SizeD Slot { get; init; }
    /// <summary>The glow of the run while it is held: its ink's glow colour.</summary>
    public Vector4 Glow { get; init; } = Vector4.One;
    /// <summary>The slot's centre in the line's block (top-left origin), layout pixels.</summary>
    public PointD Centre { get; set; }
    /// <summary>The word's symbol (the Mac's word symbols), in the rest colour, and in the sung colour where the word sweeps.</summary>
    public Ink? Symbol { get; init; }
    public Ink? SymbolSung { get; init; }
    /// <summary>The symbol's centre relative to the unit's centre, layout pixels.</summary>
    public Vector2 SymbolOffset { get; init; }
    /// <summary>The run's action word motion, if it takes one.</summary>
    public WordAction? Action { get; init; }
    /// <summary>A chorus repeat of an action word already heard: it moves gently (the Mac's <c>WordAccent.gentle</c>).</summary>
    public bool Gentle { get; init; }
    /// <summary>The colours the label was baked in (the action's colour cue multiplies them).</summary>
    public Vector4 RestColour { get; init; } = Vector4.One;
    public Vector4 SungColour { get; init; } = Vector4.One;

    public bool HasAccent => Symbol is not null || Action is not null;

    public void Dispose()
    {
        Label.Dispose();
        Sung?.Dispose();
        Reading?.Dispose();
        Symbol?.Dispose();
        SymbolSung?.Dispose();
    }
}

/// <summary>A backing-vocal group of a line, set as a small line of its own.</summary>
internal sealed class BackingPlan(BackingGroup group, LinePlan plan) : IDisposable
{
    public BackingGroup Group { get; } = group;
    public LinePlan Plan { get; } = plan;
    public void Dispose() => Plan.Dispose();
}

/// <summary>
/// One line set for drawing (Editorial's typography, or a line-timed phrase): its runs' baked ink, the row layout, the
/// line's reading, its fit to the stage, and scratch space for the per-frame states. Built when the line is first
/// needed and kept until the lyrics, size, scale or style change.
/// </summary>
internal sealed class LinePlan : IDisposable
{
    public required EditorialLine Typography { get; init; }
    public required RunInk[] Runs { get; init; }
    /// <summary>The words the plan was set from (the lead's, with backing vocals lifted out).</summary>
    public required IReadOnlyList<LyricWord> Words { get; init; }
    public required SizeD Block { get; init; }
    public Ink? LineReading { get; init; }
    public SizeD Content { get; init; }
    /// <summary>The line reading's centre in content coordinates.</summary>
    public PointD ReadingCentre { get; init; }
    /// <summary>The line's translation (Show translations under lyrics), under the line and its reading; null when none.</summary>
    public Ink? Translation { get; init; }
    /// <summary>The translation's centre in content coordinates.</summary>
    public PointD TranslationCentre { get; init; }
    public double Fit { get; init; }
    public BackingPlan[] Backing { get; set; } = [];

    // Per-frame scratch, sized once.
    public EditorialTokenFrame[] TokenFrames { get; init; } = [];
    public EditorialRunFrame[] RunFrames { get; init; } = [];
    public double[] TokenOpacities { get; init; } = [];
    public int[] Order { get; init; } = [];
    public long LastUsed { get; set; }

    // The entrances and their extras (set once, after the plan is built).
    /// <summary>Per run: the entrance chosen for it (a mix picks per run; "Cutout · Highlight" kept as such).</summary>
    public WordArrival[] Chosen { get; set; } = [];
    /// <summary>Per run: the entrance it moves with (<see cref="WordArrivals.Motion"/>).</summary>
    public WordArrival[] Motions { get; set; } = [];
    /// <summary>Per run: its sung words, for the highlight tiles (null when no run is highlighted).</summary>
    public HighlightSegment[][]? Segments { get; set; }
    /// <summary>When the line's highlight tiles release (its end).</summary>
    public double Release { get; set; } = double.PositiveInfinity;
    /// <summary>The constellation's links, in block coordinates.</summary>
    public Lunalyr.Core.Motion.Segment[] Links { get; set; } = [];
    /// <summary>Echo's repeated phrase.</summary>
    public EchoPlan? Echo { get; set; }
    /// <summary>Per run: how much a held word grows across and up at full hold (see <see cref="EditorialRun.Sustain"/>).</summary>
    public double[] HeldX { get; set; } = [];
    public double[] HeldY { get; set; } = [];
    /// <summary>
    /// Words land without overshoot: their spring is held at its rest instead of passing it. Every flow layout but Echo,
    /// which keeps its small overshoot (the Mac's <c>ScatteredRunInk.settles</c>).
    /// </summary>
    public bool Settles { get; set; } = true;

    public PointD BlockOrigin => new((Content.Width - Block.Width) / 2, 0);

    public void Dispose()
    {
        foreach (var run in Runs) run.Dispose();
        LineReading?.Dispose();
        Translation?.Dispose();
        foreach (var backing in Backing) backing.Dispose();
        Echo?.Dispose();
    }
}

/// <summary>Everything a plan is set with.</summary>
/// <param name="Kind">The flow layout the line is set in (its tiers, heights and rows, <see cref="FlowPlan"/>).</param>
internal readonly record struct PlanSpec(
    double BaseSize, double MinimumSize, int LineIndex, bool WordTimed, bool Editorial, bool Heavy, double MaxWidth,
    LineFit Fit, double RasterBoost, ShadowSpec Shadow, Vector4 Colour,
    Vector4 ReadingColour, double LineReadingSize, double LineReadingWidth, ShadowSpec LineReadingShadow,
    LyricInk? Ink = null, CompositionKind Kind = CompositionKind.Editorial, double Spread = 1,
    double TranslationSize = 0, double EditorialAmount = 1, bool RandomFonts = false, SizeD? FillStage = null,
    bool Symbols = false, bool Actions = false, WordAccentLists? WordLists = null, bool GentleActions = false,
    bool TranslationAbove = false);

internal static class LinePlans
{
    /// <summary>
    /// Sets a line: runs and metrics, laid-out labels and readings, rows, the line reading, the translation, the fit;
    /// then bakes. The translation (set at <see cref="PlanSpec.TranslationSize"/>, as wide as the line reading may be)
    /// goes under the line and its reading, and is fitted to the stage with them (the Mac's VStack).
    /// </summary>
    public static LinePlan Build(TextKit kit, IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue, PlanSpec spec,
                                 IReadOnlyList<string?>? runReadings, string? lineReading,
                                 IReadOnlyList<ReadingShares?>? runShares = null, string? translation = null)
    {
        var typography = EditorialSetting.Layout(words, glue, spec.BaseSize, spec.MinimumSize, spec.LineIndex, spec.WordTimed,
                                                 spec.Editorial, runReadings, 1, spec.Kind, spec.Spread, spec.EditorialAmount);
        var locale = TextKit.LocaleFor(string.Concat(words.Select(word => word.Text)));
        var count = typography.Runs.Count;
        // Word symbols and action words: English words of a word-timed line, planned once for the whole line.
        var accents = Accents(typography, spec);
        var symbols = accents is null ? null : new IDWriteTextLayout?[count];
        var symbolGaps = accents is null ? null : new double[count];
        var labels = new IDWriteTextLayout[count];
        var readings = new IDWriteTextLayout?[count];
        var slices = new ReadingSlices?[count];
        var units = new SizeD[count];
        var slots = new SizeD[count];
        try
        {
            for (var r = 0; r < count; r++)
            {
                var run = typography.Runs[r];
                var metrics = run.Metrics;
                // "Randomise lyric fonts": each run takes one of the mix's faces by its seed (its weight as it would have it),
                // ahead of the second face; its reading takes the same one.
                var mix = spec.RandomFonts ? (int)(run.Seed % TextKit.MixFaces) : -1;
                // The focal word is set in the heavy cut too (the Mac's hero font).
                var heavy = metrics.IsHero || metrics.IsFocal || spec.Heavy;
                var face = mix >= 0 ? heavy ? Face.Heavy : Face.Regular
                    : metrics.AlternateFace ? Face.Contrast : heavy ? Face.Heavy : Face.Regular;
                labels[r] = kit.Layout(run.Label, face, (float)metrics.Size, locale, run.RightToLeft, mix: mix);
                var label = labels[r].Metrics;
                double width = label.Width, height = label.Height;
                if (!string.IsNullOrWhiteSpace(run.Reading))
                {
                    readings[r] = kit.Layout(run.Reading!, mix < 0 && metrics.AlternateFace ? Face.Contrast : Face.Quiet,
                                             (float)(metrics.Size * 0.56), TextKit.LocaleFor(run.Reading!), false, mix: mix,
                                             faceText: run.Label);
                    slices[r] = ReadingSlices.Measure(readings[r]!, run.Reading, runShares is not null && r < runShares.Count ? runShares[r] : null);
                    var reading = readings[r]!.Metrics;
                    width = Math.Max(width, reading.Width);
                    height += metrics.Size * 0.04 + reading.Height;
                }
                // The symbol sits beside the word from the line's first frame (transparent until its word is sung), so
                // it is measured with the word and the line never reflows.
                if (accents is not null && WordSymbolGlyphs.Glyph(accents[r].Symbol) is { } glyph)
                {
                    var symbolSize = (float)Math.Max(spec.MinimumSize * 6 / 11, metrics.Size * 0.5);
                    var symbol = glyph == WordSymbolGlyphs.Dollar
                        ? kit.Layout(glyph, Face.Heavy, symbolSize, "en-us", false)
                        : kit.SymbolLayout(glyph, symbolSize);
                    symbols![r] = symbol;
                    symbolGaps![r] = metrics.Size * 0.16;
                    width = Math.Max(width, label.Width) + symbolGaps[r] + symbol.Metrics.Width;
                    height = Math.Max(height, symbol.Metrics.Height);
                }
                units[r] = new SizeD(width, height);
                slots[r] = EditorialFlow.Slot(units[r], metrics.Rotation, run.Inset, metrics.WaveOffset);
            }
            var centres = new PointD[count];
            // How the layout uses the stage (the Mac's FlowPlan): its measure, gaps, rows, steps and field, in ems.
            var flow = FlowPlan.Make(spec.Kind, typography.Setting.Rows, spec.WordTimed, typography.Fullness);
            var em = spec.BaseSize;
            var breaks = new bool[count];
            for (var r = 0; r < count; r++) breaks[r] = typography.Runs[r].Metrics.BreakBefore;
            var shifts = flow.RowShifts is { } steps ? steps.Select(step => step * em).ToArray() : [];
            var measure = flow.Measure is { } wrap ? Math.Min(spec.MaxWidth, wrap * em) : spec.MaxWidth;
            // A word-timed Constellation line fills its stage (the Mac's next/tester-fixes): its own measure, and gaps
            // that grow toward three quarters of the stage each way.
            FlowFill? fill = null;
            if (spec.Kind == CompositionKind.Constellation && spec.WordTimed && spec.FillStage is { } stage)
            {
                var (constellation, wrapAt) = FlowFill.Constellation(stage, em);
                fill = constellation;
                measure = Math.Min(spec.MaxWidth, wrapAt);
            }
            var block = EditorialFlow.Place(slots, measure, em * flow.Spacing, em * flow.LineSpacing, flow.Alignment,
                                            typography.RightToLeft, centres, breaks, flow.Balanced, shifts,
                                            flow.Field * em, fill);

            IDWriteTextLayout? lineLayout = null;
            var content = block;
            var readingCentre = new PointD(block.Width / 2, block.Height);
            if (!string.IsNullOrWhiteSpace(lineReading))
            {
                lineLayout = kit.Layout(lineReading!, Face.Quiet, (float)spec.LineReadingSize, TextKit.LocaleFor(lineReading!),
                                        false, (float)spec.LineReadingWidth);
                var m = lineLayout.Metrics;
                var gap = spec.BaseSize * 0.2;
                content = new SizeD(Math.Max(block.Width, m.Width), block.Height + gap + m.Height);
                readingCentre = new PointD(content.Width / 2, block.Height + gap + m.Height / 2);
            }
            IDWriteTextLayout? translationLayout = null;
            var translationCentre = default(PointD);
            if (!string.IsNullOrWhiteSpace(translation) && spec.TranslationSize > 0)
            {
                var text = translation.Trim();
                translationLayout = kit.Layout(text, Face.Quiet, (float)spec.TranslationSize, TextKit.LocaleFor(text),
                                               ScriptDirection.IsRightToLeft(text), (float)spec.LineReadingWidth);
                var m = translationLayout.Metrics;
                var gap = spec.BaseSize * LineTranslations.Gap;
                if (spec.TranslationAbove)
                {
                    // Over the line (Translation sits › Above the line): the line keeps its place and the translation
                    // rests above it.
                    content = new SizeD(Math.Max(content.Width, m.Width), content.Height);
                    translationCentre = new PointD(content.Width / 2, -(gap + m.Height / 2));
                }
                else
                {
                    var top = content.Height + gap;
                    content = new SizeD(Math.Max(content.Width, m.Width), top + m.Height);
                    translationCentre = new PointD(content.Width / 2, top + m.Height / 2);
                }
                // The line and its reading stay centred over the wider stack.
                readingCentre = readingCentre with { X = content.Width / 2 };
            }
            var fit = spec.Fit.ScaleFor(content);
            var raster = (float)Math.Clamp(fit * spec.RasterBoost, 0.2, 2.5);

            var runs = new RunInk[count];
            for (var r = 0; r < count; r++)
            {
                var run = typography.Runs[r];
                // Each run takes its own colour from the ink, by its place along the line (the Mac's ink.sample).
                var sample = spec.Ink?.Sample(run.Order, run.Seed);
                var colour = sample is { } rest ? LyricInks.Vector(rest.Rest) : spec.Colour;
                var readingColour = sample is { } read ? LyricInks.Vector(read.Reading) : spec.ReadingColour;
                var label = kit.Bake(labels[r], colour, raster, spec.Shadow);
                // "Sung sweep": the same label in the colour the word fills with as it is sung.
                Ink? sung = sample is { Sweeps: true } sweep ? kit.Bake(labels[r], LyricInks.Vector(sweep.Sung), raster, ShadowSpec.None) : null;
                Ink? reading = readings[r] is { } layout ? kit.Bake(layout, readingColour, raster, spec.Shadow) : null;
                var unit = units[r];
                var labelHeight = labels[r].Metrics.Height;
                var labelY = (float)(-unit.Height / 2 + labelHeight / 2);
                // With a symbol, the word moves aside by half the symbol and its gap (the symbol follows the word in
                // its reading direction), and its reading stays under it.
                var labelX = 0f;
                Ink? symbolInk = null, symbolSung = null;
                var symbolOffset = Vector2.Zero;
                var sungColour = sample is { Sweeps: true } swept ? LyricInks.Vector(swept.Sung) : colour;
                if (symbols?[r] is { } symbolLayout)
                {
                    var symbolWidth = symbolLayout.Metrics.Width;
                    var side = typography.Runs[r].RightToLeft ? -1f : 1f;
                    var labelWidth = labels[r].Metrics.Width;
                    labelX = -side * (float)((symbolGaps![r] + symbolWidth) / 2);
                    symbolOffset = new Vector2(labelX + side * (float)(labelWidth / 2 + symbolGaps[r] + symbolWidth / 2), labelY);
                    symbolInk = kit.Bake(symbolLayout, colour, raster, spec.Shadow);
                    symbolSung = sample is { Sweeps: true } ? kit.Bake(symbolLayout, sungColour, raster, ShadowSpec.None) : null;
                }
                runs[r] = new RunInk
                {
                    Label = label,
                    Sung = sung,
                    Reading = reading,
                    ReadingSlices = reading is null ? null : slices[r],
                    LabelOffset = new Vector2(labelX, labelY),
                    ReadingOffset = reading is null ? default : new Vector2(labelX, (float)(unit.Height / 2 - reading.Box.Y / 2)),
                    Unit = unit,
                    Slot = slots[r],
                    Centre = centres[r],
                    Glow = sample is { } glow ? LyricInks.Vector(glow.Glow) : colour,
                    Symbol = symbolInk,
                    SymbolSung = symbolSung,
                    SymbolOffset = symbolOffset,
                    Action = accents?[r].Action, Gentle = accents?[r].Gentle == true,
                    RestColour = colour,
                    SungColour = sungColour,
                };
            }
            var lineInk = lineLayout is null ? null : kit.Bake(lineLayout, spec.ReadingColour, raster, spec.LineReadingShadow);
            lineLayout?.Dispose();
            var translationInk = translationLayout is null ? null
                : kit.Bake(translationLayout, spec.ReadingColour, raster, spec.LineReadingShadow);
            translationLayout?.Dispose();
            var tokenCount = typography.Tokens.Count;
            return new LinePlan
            {
                Typography = typography,
                Runs = runs,
                Words = words,
                Block = block,
                LineReading = lineInk,
                Content = content,
                ReadingCentre = readingCentre,
                Translation = translationInk,
                TranslationCentre = translationCentre,
                Fit = fit,
                TokenFrames = new EditorialTokenFrame[tokenCount],
                RunFrames = new EditorialRunFrame[count],
                TokenOpacities = new double[tokenCount],
                Order = new int[count],
            };
        }
        finally
        {
            foreach (var layout in labels) layout?.Dispose();
            foreach (var layout in readings) layout?.Dispose();
            if (symbols is not null) foreach (var layout in symbols) layout?.Dispose();
        }
    }

    /// <summary>
    /// The line's word symbols and action word (the Mac's ad5a6a6 and cba108f): only for a word-timed line with either
    /// switched on; each run matched by its text (a glued run's tokens joined). Null when there are none.
    /// </summary>
    private static RunAccent[]? Accents(EditorialLine typography, in PlanSpec spec)
    {
        if (!spec.WordTimed || !(spec.Symbols || spec.Actions) || spec.WordLists is not { } lists) return null;
        var texts = new string[typography.Runs.Count];
        for (var r = 0; r < texts.Length; r++) texts[r] = typography.Runs[r].Label;
        var plan = WordAccents.Plan(texts, spec.Symbols, spec.Actions, lists, spec.GentleActions);
        foreach (var accent in plan)
            if (!accent.IsEmpty) return plan;
        return null;
    }
}

/// <summary>Echo's phrase baked once (at the largest ghost's size), and how wide it reads.</summary>
internal sealed class EchoPlan(Ink phrase, double bakedScale, bool rightToLeft) : IDisposable
{
    public Ink Phrase { get; } = phrase;
    /// <summary>The ghost scale (share of the base size) the phrase was baked at.</summary>
    public double BakedScale { get; } = bakedScale;
    public bool RightToLeft { get; } = rightToLeft;
    /// <summary>The base size, stage and seed of the line the ghosts belong to (layout pixels).</summary>
    public double BaseSize { get; init; }
    public SizeD Stage { get; init; }
    public int Seed { get; init; }
    public void Dispose() => Phrase.Dispose();
}
