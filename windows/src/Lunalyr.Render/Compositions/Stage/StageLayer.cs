// The stage layouts (the Mac's StageLyricView in StageLyricStyles.swift, and its routing in LyricLayerView.swift):
// Lunalyr's own code, no line from the Verso import (git blame -w -M -C), ported as it is. One line at a time is set on
// the whole stage, word by word from the measured size of each word; the line before leaves as this one begins.
// Shared here: which runs a line has, their readings, the line's pace and colours, backing vocals, the line's own
// entrance and exit, and the drawing helpers (baked words drawn flat or in perspective, and the layouts' shapes).

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Lunalyr.Render.Scene;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Lunalyr.Render.Compositions.Stage;

/// <summary>
/// One run baked for a stage layout: its label in one or more colours, its reading under it (in one or more colours),
/// laid out as the Mac's <c>withReading</c> stacks them. Offsets run from the unit's centre to each box's centre.
/// </summary>
internal sealed class StageWord : IDisposable
{
    public required Ink[] Labels { get; init; }
    public Ink[] Readings { get; init; } = [];
    public Vector2 LabelOffset { get; init; }
    public Vector2 ReadingOffset { get; init; }
    /// <summary>The word and its reading (the measured size the layouts place by), layout pixels.</summary>
    public SizeD Size { get; init; }
    /// <summary>The word alone.</summary>
    public SizeD Label { get; init; }
    /// <summary>Where each glued syllable begins and ends along the label (0…1), or null for a run of one part.</summary>
    public double[]? Boundaries { get; init; }
    /// <summary>Where each part's share of the reading falls along it, so the reading fills in as the run is sung (the
    /// Mac's <c>revealedReading</c>); for a word baked from its reading (Mosaic's letter-by-letter kind), along the
    /// label. Null when the reading shows whole.</summary>
    public ReadingSlices? ReadingSlices { get; init; }
    public bool RightToLeft { get; init; }
    public bool HasReading => Readings.Length > 0;

    public void Dispose()
    {
        foreach (var ink in Labels) ink.Dispose();
        foreach (var ink in Readings) ink.Dispose();
    }
}

/// <summary>How a stage word is set.</summary>
internal sealed record WordSpec(double Size, Vector4[] Colours)
{
    public Face Face { get; init; } = Face.Heavy;
    public bool Uppercase { get; init; }
    public double MaxWidth { get; init; } = double.PositiveInfinity;
    public double MinimumScale { get; init; } = 0.35;
    public ShadowSpec Shadow { get; init; }
    public float Raster { get; init; } = 1.1f;
    /// <summary>The reading's own size (null: none drawn).</summary>
    public string? Reading { get; init; }
    public double ReadingSize { get; init; }
    public double ReadingGap { get; init; }
    public double ReadingMaxWidth { get; init; } = double.PositiveInfinity;
    public Vector4[] ReadingColours { get; init; } = [];
    public ShadowSpec ReadingShadow { get; init; }
    /// <summary>The reading's height as the layouts count it (the Mac's <c>readingSize · 1.25</c>); null: as laid out.</summary>
    public double? ReadingRow { get; init; }
    /// <summary>−1 leading, 0 centred, 1 trailing.</summary>
    public int Alignment { get; init; }
    /// <summary>The text baked is the run's reading itself (Mosaic's letter-by-letter kind sets it on its own): its
    /// shares are measured along the label.</summary>
    public bool LabelIsReading { get; init; }
}

/// <summary>What a stage line is built from.</summary>
internal sealed record StageSource(int Row, LyricLine Line, IReadOnlyList<StageRun> Runs, string?[] Readings, double Speed,
                                   double InkTime, ReadingShares?[]? Shares = null);

/// <summary>A line set for a stage layout, with the scratch its frames use (so a frame allocates nothing).</summary>
internal class StageLine : IDisposable
{
    public StageLine(StageSource source)
    {
        Row = source.Row;
        Line = source.Line;
        Runs = source.Runs;
        Readings = source.Readings;
        Speed = source.Speed;
        InkTime = source.InkTime;
        var n = Math.Max(1, Runs.Count);
        A = new double[n]; B = new double[n]; C = new double[n]; D = new double[n]; E = new double[n];
        Sizes = new SizeD[n];
        Places = new PointD[n];
        Parts = new double[16];
        Texts = [.. Runs.Select(run => StageShared.Count(run.Text))];
        ReadingCounts = [.. Readings.Select(reading => reading is null ? 0 : StageShared.Count(reading))];
        Little = BroadsideGeometry.Labels([.. Runs.Select(run => run.Text)]);
    }

    public int Row { get; }
    public long Seed => Row;
    public LyricLine Line { get; }
    public IReadOnlyList<StageRun> Runs { get; }
    public string?[] Readings { get; }
    public double Speed { get; }
    public double InkTime { get; }
    public int[] Texts { get; }
    public int[] ReadingCounts { get; }
    public bool[] Little { get; }
    public bool HasReadings => Array.Exists(Readings, r => r is not null);
    public StageWord[] Words { get; set; } = [];
    public Ink? LineReading { get; set; }
    public BackingPlan[] Backing { get; set; } = [];
    public long LastUsed { get; set; }

    // Per-frame scratch, one value per run.
    public double[] A { get; }
    public double[] B { get; }
    public double[] C { get; }
    public double[] D { get; }
    public double[] E { get; }
    public SizeD[] Sizes { get; }
    public PointD[] Places { get; }
    public double[] Parts { get; private set; }

    /// <summary>A run's glued syllables' opacities at <paramref name="time"/>, or empty for a run of one part.</summary>
    public ReadOnlySpan<double> PartOpacities(StageRun run, double time)
    {
        if (run.Parts.Count <= 1) return default;
        if (Parts.Length < run.Parts.Count) Parts = new double[run.Parts.Count * 2];
        for (var i = 0; i < run.Parts.Count; i++) Parts[i] = StageRun.PartOpacity(i, run.Parts, time);
        return Parts.AsSpan(0, run.Parts.Count);
    }

    /// <summary>
    /// How far each part's share of <paramref name="word"/>'s reading shows at <paramref name="time"/>: with its part,
    /// as the word's own syllables do (the Mac's <c>revealedReading</c>). Empty while the reading shows whole.
    /// </summary>
    public ReadOnlySpan<double> ReadingOpacities(StageWord word, StageRun run, double time) =>
        word.ReadingSlices is { } slices ? slices.FromParts(PartOpacities(run, time)) : default;

    public virtual void Dispose()
    {
        foreach (var word in Words) word.Dispose();
        LineReading?.Dispose();
        foreach (var plan in Backing) plan.Dispose();
    }
}

/// <summary>The shared stage of Hinge, Broadside, Portal, Mosaic, Torque and Fable.</summary>
internal abstract class StageLayer(CompositionKind kind, IWordBreaker breaker, Logger? logger)
    : LyricCompositionLayer(kind, breaker, logger)
{
    private const int MaxLines = 6;
    private readonly Dictionary<int, StageLine> _lines = [];
    /// <summary>The reading shares of the line being built, by run id: a run's reading is measured share by share as it
    /// is baked.</summary>
    private ReadingShares?[]? _baking;
    private ID2D1SolidColorBrush? _brush;
    private ID2D1StrokeStyle? _round;
    private ID2D1PathGeometry? _star;
    private ID2D1DeviceContext? _shapesOn;
    private readonly PointD[] _points = new PointD[StageShapes.SketchyPoints];
    private readonly Vector2[] _vectors = new Vector2[StageShapes.SketchyPoints];

    /// <summary>The stage layouts' unit: the Mac's <c>max(16, baseSize)</c>, the layer handing them 0.8 of its base, times the size slider.</summary>
    protected double Unit => Math.Max(16 * Scale, BaseSize * 0.8 * Metrics.UserScale);

    /// <summary>Pixels per point.</summary>
    protected double Pt => Scale;

    protected StageRect Bounds => new(0, 0, Stage.Width, Stage.Height);

    /// <summary>
    /// The entrance chosen for <paramref name="run"/> (the Mac's <c>settings.arrival(in:for:)</c> on the run as a word):
    /// one entrance for every run, or a mix's pick by the run's visual seed.
    /// </summary>
    protected WordArrival ChosenFor(StageRun run) => Choice.For(Kind, TimedWordKey.SeedOf(run.Text, run.Start));

    protected override void Invalidate()
    {
        base.Invalidate();
        foreach (var line in _lines.Values) line.Dispose();
        _lines.Clear();
        DisposeShapes();
    }

    private void DisposeShapes()
    {
        _brush?.Dispose(); _brush = null;
        _round?.Dispose(); _round = null;
        _star?.Dispose(); _star = null;
        DisposeOwnShapes();
        _shapesOn = null;
    }

    /// <summary>Releases a layout's own device shapes (made in <see cref="CreateShapes"/>).</summary>
    protected virtual void DisposeOwnShapes() { }

    /// <summary>Makes a layout's own device shapes, once per device context.</summary>
    protected virtual void CreateShapes(ID2D1DeviceContext dc, ID2D1Factory factory) { }

    protected override void PrepareStage(int row) => Line(row);

    protected override bool StagePrepared(int row) => _lines.ContainsKey(row);

    /// <summary>The stage line for <paramref name="row"/>, set (laid out and baked) when first needed.</summary>
    protected StageLine? Line(int row)
    {
        if (_lines.TryGetValue(row, out var found)) { found.LastUsed = Frame; return found; }
        var sheet = Sheet!;
        if (row < 0 || row >= sheet.Lines.Count || sheet.Lines[row].Words.Count == 0) return null;
        var source = sheet.Lines[row];
        IReadOnlyList<LyricWord> words = source.Words;
        IReadOnlyList<bool> glue = sheet.Glue[row];
        // Word-by-word readings where the scene has them (the Mac's romanized): "Instead" sets them in the words' place,
        // grouped as the line's words were.
        var set = WordReadingsOf(row);
        if (set is { Replaced: true }) { words = set.Words; glue = set.Glue; }
        IReadOnlyList<BackingGroup> groups = [];
        if (source.WordTimed)
        {
            var split = BackingVocals.Split(words, glue);
            if (split.Backing.Count > 0) { words = split.Main; glue = split.MainGlue; groups = split.Backing; }
        }
        if (set is null && Replaced(row, words) is { } romanised)
        {
            // "Instead", with one part per sung word: the Mac's replace hands the layouts the romanised words, each a
            // word of its own (a Latin reading of a Japanese character stands apart), at the words' own times.
            words = romanised;
            glue = new bool[romanised.Count];
        }
        var runs = StageRun.Make(words, glue);
        var (readings, lineReading, shares) = set is not null ? PairWordReadings(row, set, runs) : PairReadings(row, words, runs);
        var starts = new double[words.Count];
        for (var i = 0; i < words.Count; i++) starts[i] = words[i].Start;
        StageLine line;
        _baking = shares;
        try
        {
            line = Build(new StageSource(row, source, runs, readings, StageShared.LineSpeed(starts), source.Start, shares));
        }
        finally
        {
            _baking = null;
        }
        if (lineReading is not null)
        {
            using var layout = Kit!.Layout(lineReading, Face.Quiet, (float)(BaseSize * 0.38), TextKit.LocaleFor(lineReading),
                                           ScriptDirection.IsRightToLeft(lineReading), (float)(Stage.Width * 0.8));
            line.LineReading = Kit.Bake(layout, LyricInks.Vector(InkAt(source.Start).Base.Reading), 1,
                                        new ShadowSpec(0.3f, Points(6) * 0.5f, Points(2)));
        }
        if (groups.Count > 0)
        {
            var box = BackingVocalLayout.Box(Stage);
            var plans = new BackingPlan[groups.Count];
            for (var g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                var plan = BuildPlan(group.Words, group.Glue, Unit * 0.5, row + group.Id,
                                     group.Words.Count > 0 ? group.Words[0].Start : source.Start, wordTimed: true,
                                     editorial: true, null, null, new LineFit(box, 1, 1), box.Width);
                plans[g] = new BackingPlan(group, plan);
            }
            line.Backing = plans;
        }
        line.LastUsed = Frame;
        _lines[row] = line;
        Trim();
        return line;
    }

    private void Trim()
    {
        while (_lines.Count > MaxLines)
        {
            var oldest = -1;
            var stamp = long.MaxValue;
            foreach (var (row, line) in _lines)
                if (line.LastUsed < stamp) { stamp = line.LastUsed; oldest = row; }
            if (oldest < 0) break;
            _lines[oldest].Dispose();
            _lines.Remove(oldest);
        }
    }

    /// <summary>
    /// The line's reading paired with its stage runs: one space-separated part per run (or the whole reading on a line
    /// of one run) goes under its own run; otherwise the reading goes once under the whole stage. "Instead" sets a
    /// reading that pairs this way in place of the runs' words.
    /// </summary>
    private (string?[] Runs, string? Line, ReadingShares?[]? Shares) PairReadings(int row, IReadOnlyList<LyricWord> words,
                                                                                List<StageRun> runs)
    {
        var none = new string?[runs.Count];
        if (ReadingOf(row) is not { } reading) return (none, null, null);
        if (!LyricReadingPairs.IsDistinct(reading, string.Concat(words.Select(word => word.Text)))) return (none, null, null);
        var parts = reading.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string?[]? paired = null;
        if (runs.Count == 1) paired = [reading];
        else if (runs.Count > 1 && parts.Length == runs.Count) paired = parts;
        if (paired is null) return (none, reading, null);
        if (SceneShown?.Style.Readings == ReadingPlacement.Instead)
        {
            for (var i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                runs[i] = run with { Text = paired[i]!, Parts = [new StageRun.Part(paired[i]!, run.Start, run.Parts[0].Source)] };
            }
            return (none, null, null);
        }
        for (var i = 0; i < runs.Count; i++)
            if (!LyricReadingPairs.IsDistinct(paired[i]!, runs[i].Text)) paired[i] = null;
        return (paired, null, null);
    }

    /// <summary>
    /// The line's word-by-word readings paired with its stage runs (the Mac's <c>readingPairs(byPart:)</c>): with "Below
    /// lyrics" each token keeps its own share of its stage run's reading (<see cref="LyricReadingPairs.Parts"/>, by the
    /// runs the stage draws, <see cref="StageRun.Glue"/>), so each run carries only its own reading and fills it in
    /// part by part; a reading that cannot be split by word goes once under the whole stage. "Instead" has set the
    /// readings in the words' place already.
    /// </summary>
    private (string?[] Runs, string? Line, ReadingShares?[]? Shares) PairWordReadings(int row, LineReadingSet set,
                                                                                    List<StageRun> runs)
    {
        var none = new string?[runs.Count];
        if (set.Replaced || ReadingDisplay != RomanizationDisplay.Below || WordReadingList(row) is not { } read)
            return (none, null, null);
        var line = Sheet!.Lines[row];
        var pairs = LyricReadingPairs.Parts(read, line.Words, StageRun.Glue(line.Words, Sheet.Glue[row]));
        var readings = new string?[runs.Count];
        var shares = new ReadingShares?[runs.Count];
        for (var i = 0; i < runs.Count; i++)
        {
            readings[i] = StageRun.Reading(runs[i], pairs);
            if (readings[i] is not null) shares[i] = ReadingShares.Of(runs[i], pairs);
        }
        return (readings, set.LineReading, shares);
    }

    /// <summary>The line's words with the reading in their place, when it is set "Instead" and has one part per word.</summary>
    private List<LyricWord>? Replaced(int row, IReadOnlyList<LyricWord> words)
    {
        if (SceneShown?.Style.Readings != ReadingPlacement.Instead || ReadingOf(row) is not { } reading) return null;
        var parts = reading.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var visible = words.Where(word => word.Text.Trim().Length > 0).ToList();
        if (visible.Count < 2 || parts.Length != visible.Count) return null;
        if (!LyricReadingPairs.IsDistinct(reading, string.Concat(words.Select(word => word.Text)))) return null;
        return [.. visible.Select((word, i) => word with { Text = parts[i] })];
    }

    /// <summary>Bakes a stage line's words for this layout.</summary>
    protected abstract StageLine Build(StageSource source);

    /// <summary>
    /// Draws the line's words. <paramref name="stage"/> maps stage pixels (top-left origin, <see cref="Stage"/> in
    /// size) to the device; <paramref name="alpha"/> is the line's opacity and <paramref name="blur"/> a line-timed
    /// line's entrance blur (device pixels). <paramref name="shown"/> is how many of the line's runs are laid out (the
    /// ones that have arrived, or one fewer for the step a Reduce Motion dissolve starts from); Fable lays out its
    /// whole line and ignores it.
    /// </summary>
    protected abstract void DrawLine(ID2D1DeviceContext dc, StageLine line, double time, double exitStart, in Matrix4x4 stage,
                                     double alpha, float blur, int shown);

    protected override void DrawStage(ID2D1DeviceContext dc, int row, double time, in LyricPhraseTiming pose, double presence,
                                      double opacity, double yield)
    {
        var line = Line(row);
        if (line is null) return;
        EnsureShapes(dc);
        var source = line.Line;
        var exitStart = LineFlow.StageExitStart(source, pose);
        foreach (var backing in line.Backing) DrawBackingGroup(dc, backing, time, opacity, row);
        // The line's own entrance: a word-timed line swells and slides in; a line-timed one makes the chosen entrance.
        var centre = new Vector2((float)(Stage.Width / 2), (float)(Stage.Height / 2));
        Matrix3x2 local;
        double alpha;
        float blur = 0;
        var reduce = ReduceMotion;
        if (source.WordTimed)
        {
            alpha = presence * opacity;
            // Reduce Motion: the line fades; it does not slide or swell.
            var swell = reduce ? 1 : (float)(0.965 + 0.035 * pose.Arrival - 0.012 * pose.Exit);
            var slide = reduce ? 0 : (float)(((1 - pose.Arrival) * 0.20 - pose.Exit * 0.10) * BaseSize);
            local = Matrix3x2.CreateScale(swell, centre) * Matrix3x2.CreateTranslation(0, slide);
        }
        else
        {
            alpha = pose.Presence * (1 - 0.5 * yield) * opacity;
            var pointSize = BaseSize * 1.06 * 0.68;
            // A line sung as one enters as its layout has it enter: the choice for the phrase (a mix picks by its seed).
            var motion = Choice.For(Kind, TimedWordKey.SeedOf(source.Text, source.Start)).Motion(Kind);
            var entrance = EntranceTransform.Make(pose.Progress, pose.Arrival, motion, pointSize, row % 2 == 0 ? 1 : -1, reduce);
            local = Matrix3x2.CreateTranslation(-centre) * Entrance(entrance) * Matrix3x2.CreateTranslation(centre);
            blur = Points(motion.Blur(pose.Progress, reduce));
        }
        if (!(alpha > 0.002)) return;
        // A reading that cannot be split by run sits once under the stage; the two are fitted to the stage together.
        var fit = 1.0;
        var lift = 0.0;
        if (line.LineReading is { } reading)
        {
            var gap = BaseSize * 0.2;
            var total = Stage.Height + gap + reading.Box.Y;
            fit = Math.Min(1, Stage.Height / total);
            lift = -(gap + reading.Box.Y) / 2 * fit;
        }
        var origin = StageOrigin;
        var toDevice = local
            * Matrix3x2.CreateScale((float)fit, centre)
            * Matrix3x2.CreateTranslation(origin.X, origin.Y + (float)lift);
        var stage = new Matrix4x4(toDevice);
        var shown = StageMotion.ShownCount(line.Runs, time);
        // Reduce Motion: every layout step is a short dissolve between where the words were and where they now are, not a
        // journey; the curves keep the sum of the two near 1 where nothing moved. Fable never moves its words.
        if (StageMotion.ReducedDissolve(line.Runs, shown, Kind != CompositionKind.Fable, time, reduce) is { } step)
        {
            DrawLine(dc, line, time, exitStart, stage, alpha * (1 - step * step), blur, shown - 1);
            DrawLine(dc, line, time, exitStart, stage, alpha * (1 - (1 - step) * (1 - step)), blur, shown);
        }
        else
        {
            DrawLine(dc, line, time, exitStart, stage, alpha, blur, shown);
        }
        if (line.LineReading is { } under)
        {
            // Below the stage, in the stack the two are fitted as (the Mac's VStack of the stage and its reading).
            var y = Stage.Height + BaseSize * 0.2 + under.Box.Y / 2;
            var world = Matrix3x2.CreateTranslation(centre.X, (float)y) * toDevice;
            under.Draw(dc, world, (float)fit, (float)(pose.Progress * (1 - pose.Exit) * opacity), 0, GlowSpec.None, 1);
        }
    }

    // ---- Baking --------------------------------------------------------------------------------------------------

    /// <summary>The colours of <paramref name="run"/> in its line (the Mac's <c>sample(run, of:)</c>).</summary>
    protected LyricInkSample Sample(StageLine line, StageRun run) => Sample(line.InkTime, line.Seed, run, line.Runs.Count);

    protected LyricInkSample Sample(double inkTime, long seed, StageRun run, int count) =>
        InkSample(inkTime, count > 1 ? (double)run.Id / (count - 1) : 0.5, (ulong)Math.Abs(seed + run.Id));

    protected Vector4 Tile(double inkTime) => LyricInks.Vector(InkAt(inkTime).TileColour);
    protected Vector4 TileText(double inkTime) => LyricInks.Vector(InkAt(inkTime).TileText);

    protected static Vector4 Colour(SrgbColour colour, double opacity = 1)
    {
        var v = LyricInks.Vector(colour);
        return v with { W = v.W * (float)opacity };
    }

    protected static Vector4 Faded(Vector4 colour, double opacity) => colour with { W = colour.W * (float)opacity };

    /// <summary>A soft drop shadow given in points (opacity, radius, drop).</summary>
    protected ShadowSpec Shadow(double alpha, double radius, double drop) => new((float)alpha, Points(radius) * 0.5f, Points(drop));

    /// <summary>
    /// Bakes <paramref name="text"/> (a run, or any text with <paramref name="run"/>'s parts) as <paramref name="spec"/>
    /// sets it: the word shrunk to its maximum width (never below its minimum scale), its reading under it.
    /// </summary>
    protected StageWord Bake(StageRun? run, string text, WordSpec spec)
    {
        var kit = Kit!;
        if (spec.Uppercase) text = text.ToUpperInvariant();
        var rightToLeft = ScriptDirection.IsRightToLeft(text);
        var locale = TextKit.LocaleFor(text);
        var size = spec.Size;
        var label = kit.Layout(text, spec.Face, (float)size, locale, rightToLeft);
        try
        {
            var width = label.Metrics.Width;
            if (width > spec.MaxWidth && width > 0)
            {
                size *= Math.Max(spec.MinimumScale, spec.MaxWidth / width);
                label.Dispose();
                label = kit.Layout(text, spec.Face, (float)size, locale, rightToLeft);
            }
            var labels = new Ink[spec.Colours.Length];
            for (var i = 0; i < labels.Length; i++) labels[i] = kit.Bake(label, spec.Colours[i], spec.Raster, spec.Shadow);
            double w = label.Metrics.Width, h = label.Metrics.Height;
            var readings = Array.Empty<Ink>();
            // A word baked from its run's reading fills in along its label; a reading under a word, along the reading.
            var readingSlices = spec.LabelIsReading ? ReadingSlices.Measure(label, text, SharesOf(run)) : null;
            double readingWidth = 0, readingRow = 0;
            if (spec.Reading is { } reading && spec.ReadingColours.Length > 0)
            {
                var readingSize = spec.ReadingSize;
                var readingLocale = TextKit.LocaleFor(reading);
                var layout = kit.Layout(reading, Face.Quiet, (float)readingSize, readingLocale, false);
                if (layout.Metrics.Width > spec.ReadingMaxWidth && layout.Metrics.Width > 0)
                {
                    var shrunk = readingSize * Math.Max(0.5, spec.ReadingMaxWidth / layout.Metrics.Width);
                    layout.Dispose();
                    layout = kit.Layout(reading, Face.Quiet, (float)shrunk, readingLocale, false);
                }
                using (layout)
                {
                    readings = new Ink[spec.ReadingColours.Length];
                    readingSlices = ReadingSlices.Measure(layout, reading, SharesOf(run));
                    for (var i = 0; i < readings.Length; i++) readings[i] = kit.Bake(layout, spec.ReadingColours[i], spec.Raster, spec.ReadingShadow);
                    readingWidth = layout.Metrics.Width;
                    readingRow = spec.ReadingRow ?? layout.Metrics.Height;
                }
            }
            var hasReading = readings.Length > 0;
            var unitWidth = Math.Max(w, readingWidth);
            var unitHeight = h + (hasReading ? spec.ReadingGap + readingRow : 0);
            float Aligned(double inner) => (float)(spec.Alignment * (unitWidth - inner) / 2);
            double[]? boundaries = null;
            if (run is { Parts.Count: > 1 })
            {
                boundaries = new double[run.Parts.Count + 1];
                double total = 0;
                foreach (var part in run.Parts) total += StageShared.Count(part.Text);
                double running = 0;
                for (var i = 0; i < run.Parts.Count; i++)
                {
                    running += StageShared.Count(run.Parts[i].Text);
                    boundaries[i + 1] = Math.Min(1, running / Math.Max(1, total));
                }
            }
            return new StageWord
            {
                Labels = labels,
                Readings = readings,
                LabelOffset = new Vector2(Aligned(w), (float)(-unitHeight / 2 + h / 2)),
                ReadingOffset = new Vector2(Aligned(readingWidth), (float)(unitHeight / 2 - readingRow / 2)),
                Size = new SizeD(unitWidth, unitHeight),
                Label = new SizeD(w, h),
                Boundaries = boundaries,
                ReadingSlices = readingSlices,
                RightToLeft = rightToLeft,
            };
        }
        finally
        {
            label.Dispose();
        }
    }

    /// <summary>The shares of <paramref name="run"/>'s reading on the line being built, if it fills in by part.</summary>
    private ReadingShares? SharesOf(StageRun? run) =>
        run is not null && _baking is { } shares && run.Id >= 0 && run.Id < shares.Length ? shares[run.Id] : null;

    /// <summary>The reading spec of <paramref name="text"/> under a word set at <paramref name="size"/> (the Mac's <c>withReading</c>).</summary>
    protected WordSpec WithReading(WordSpec spec, string? text, double size, Vector4[] colours, double? gap = null,
                                   bool shadow = true, double maxWidth = double.PositiveInfinity, int alignment = 0)
    {
        if (text is null) return spec;
        var readingSize = StageShared.ReadingSize(size, Pt);
        return spec with
        {
            Reading = text, ReadingSize = readingSize, ReadingGap = gap ?? StageShared.ReadingGap(size),
            ReadingColours = colours, ReadingMaxWidth = maxWidth, ReadingRow = readingSize * 1.25, Alignment = alignment,
            ReadingShadow = shadow ? new ShadowSpec(0.35f, (float)Math.Max(2 * Pt, readingSize * 0.18) * 0.5f, Points(1)) : ShadowSpec.None,
        };
    }

    // ---- Drawing words ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Draws <paramref name="ink"/> at <paramref name="offset"/> from a unit placed by <paramref name="unit"/> (unit
    /// layout pixels → device). Flat transforms take the ink's own path (blur, syllables, shadow); a turn in depth draws
    /// the bitmaps through Direct2D's perspective transform (without blur).
    /// </summary>
    protected static void DrawInk(ID2D1DeviceContext dc, Ink ink, in Matrix4x4 unit, Vector2 offset, double opacity, float blur = 0,
                                  float shadow = 1, ReadOnlySpan<double> boundaries = default,
                                  ReadOnlySpan<double> opacities = default, bool rightToLeft = false)
    {
        if (!(opacity > 0.002)) return;
        var world = Matrix4x4.CreateTranslation(offset.X, offset.Y, 0) * unit;
        if (Homography.IsAffine(world))
        {
            var flat = Homography.Flat(world);
            var scale = MathF.Sqrt(MathF.Abs(flat.GetDeterminant()));
            ink.Draw(dc, flat, scale, (float)opacity, blur, GlowSpec.None, shadow, boundaries, opacities, rightToLeft);
            return;
        }
        dc.Transform = Matrix3x2.Identity;
        var sliced = opacities.Length > 1 && boundaries.Length == opacities.Length + 1;
        if (ink.Soft is { } soft && shadow * opacity > 0.004)
        {
            var softWorld = Matrix4x4.CreateTranslation(-ink.SoftCentre.X, -ink.SoftCentre.Y, 0)
                * Matrix4x4.CreateScale(1 / ink.SoftRaster) * world;
            DrawSlices(dc, soft, ink, softWorld, (float)(opacity * shadow), ink.SoftCentre, ink.SoftRaster, ink.SoftWidth,
                       ink.SoftHeight, sliced, boundaries, opacities, rightToLeft);
        }
        var textWorld = Matrix4x4.CreateTranslation(-ink.Centre.X, -ink.Centre.Y, 0) * Matrix4x4.CreateScale(1 / ink.Raster) * world;
        DrawSlices(dc, ink.Text, ink, textWorld, (float)opacity, ink.Centre, ink.Raster, ink.PixelWidth, ink.PixelHeight, sliced,
                   boundaries, opacities, rightToLeft);
    }

    private static void DrawSlices(ID2D1DeviceContext dc, ID2D1Bitmap1 bitmap, Ink ink, Matrix4x4 world, float opacity,
                                   Vector2 centre, float raster, int width, int height, bool sliced,
                                   ReadOnlySpan<double> boundaries, ReadOnlySpan<double> opacities, bool rightToLeft)
    {
        if (!sliced)
        {
            var whole = new RawRectF(0, 0, width, height);
            dc.DrawBitmap(bitmap, whole, opacity, InterpolationMode.Linear, whole, world);
            return;
        }
        var count = opacities.Length;
        var w = ink.Box.X;
        for (var k = 0; k < count; k++)
        {
            var alpha = (float)opacities[k] * opacity;
            if (alpha <= 0.002f) continue;
            float a, b;
            if (!rightToLeft)
            {
                a = k == 0 ? float.NegativeInfinity : -w / 2 + (float)boundaries[k] * w;
                b = k == count - 1 ? float.PositiveInfinity : -w / 2 + (float)boundaries[k + 1] * w;
            }
            else
            {
                a = k == count - 1 ? float.NegativeInfinity : w / 2 - (float)boundaries[k + 1] * w;
                b = k == 0 ? float.PositiveInfinity : w / 2 - (float)boundaries[k] * w;
            }
            var left = Math.Clamp(centre.X + a * raster, 0, width);
            var right = Math.Clamp(centre.X + b * raster, 0, width);
            var slice = new RawRectF(left, 0, MathF.Max(left, right), height);
            dc.DrawBitmap(bitmap, slice, alpha, InterpolationMode.Linear, slice, world);
        }
    }

    /// <summary>Draws a stage word's label (colour <paramref name="colour"/>) with its syllables arriving as sung.</summary>
    protected static void DrawLabel(ID2D1DeviceContext dc, StageLine line, StageWord word, StageRun run, int colour, double time,
                                    in Matrix4x4 unit, double opacity, float blur = 0, float shadow = 1, Vector2 nudge = default)
    {
        var parts = word.Boundaries is null ? default : line.PartOpacities(run, time);
        DrawInk(dc, word.Labels[colour], unit, word.LabelOffset + nudge, opacity, blur, shadow, word.Boundaries, parts,
                word.RightToLeft);
    }

    /// <summary>
    /// Draws a stage word's reading (colour <paramref name="colour"/>), each part's share showing as that part is sung
    /// (the Mac's <c>revealedReading</c>): a share not yet sung is not drawn but keeps its room.
    /// </summary>
    protected static void DrawReading(ID2D1DeviceContext dc, StageLine line, StageWord word, StageRun run, int colour, double time,
                                      in Matrix4x4 unit, double opacity, float blur = 0, float shadow = 1)
    {
        if (word.Readings.Length == 0) return;
        var shares = line.ReadingOpacities(word, run, time);
        DrawInk(dc, word.Readings[Math.Min(colour, word.Readings.Length - 1)], unit, word.ReadingOffset, opacity, blur, shadow,
                shares.IsEmpty ? default : word.ReadingSlices!.Boundaries, shares);
    }

    // ---- Shapes ----------------------------------------------------------------------------------------------------

    private void EnsureShapes(ID2D1DeviceContext dc)
    {
        if (ReferenceEquals(_shapesOn, dc) && _brush is not null) return;
        DisposeShapes();
        _shapesOn = dc;
        _brush = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        using var factory = dc.Factory;
        _round = factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round, LineJoin = LineJoin.Round,
        });
        Span<PointD> star = stackalloc PointD[8];
        StageShapes.Star4(new StageRect(-1, -1, 2, 2), star);
        _star = Polygon(factory, star);
        CreateShapes(dc, factory);
    }

    /// <summary>A closed, filled polygon through <paramref name="points"/>.</summary>
    protected static ID2D1PathGeometry Polygon(ID2D1Factory factory, ReadOnlySpan<PointD> points)
    {
        var geometry = factory.CreatePathGeometry();
        using var sink = geometry.Open();
        sink.BeginFigure(new Vector2((float)points[0].X, (float)points[0].Y), FigureBegin.Filled);
        for (var i = 1; i < points.Length; i++) sink.AddLine(new Vector2((float)points[i].X, (float)points[i].Y));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geometry;
    }

    protected ID2D1SolidColorBrush Brush(Vector4 colour, double opacity)
    {
        _brush!.Color = new Color4(colour.X, colour.Y, colour.Z, Math.Clamp(colour.W * (float)opacity, 0, 1));
        return _brush;
    }

    protected ID2D1StrokeStyle Round => _round!;

    /// <summary>Fills a rounded rectangle centred on the transform's origin.</summary>
    protected void FillRounded(ID2D1DeviceContext dc, in Matrix3x2 transform, double width, double height, double radius,
                               Vector4 colour, double opacity)
    {
        if (!(opacity > 0.002) || !(width > 0) || !(height > 0)) return;
        dc.Transform = transform;
        var r = (float)Math.Min(radius, Math.Min(width, height) / 2);
        dc.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(-(float)width / 2, -(float)height / 2, (float)width, (float)height), r, r),
                                Brush(colour, opacity));
    }

    /// <summary>Strokes a rounded rectangle centred on the transform's origin, the stroke inside its edge when <paramref name="inside"/>.</summary>
    protected void StrokeRounded(ID2D1DeviceContext dc, in Matrix3x2 transform, double width, double height, double radius,
                                 double stroke, Vector4 colour, double opacity, bool inside = true)
    {
        if (!(opacity > 0.002) || !(width > 0) || !(height > 0)) return;
        var inset = inside ? stroke / 2 : 0;
        double w = width - 2 * inset, h = height - 2 * inset;
        if (!(w > 0) || !(h > 0)) return;
        dc.Transform = transform;
        var r = (float)Math.Max(0, Math.Min(radius - inset, Math.Min(w, h) / 2));
        dc.DrawRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(-(float)w / 2, -(float)h / 2, (float)w, (float)h), r, r),
                                Brush(colour, opacity), (float)stroke, _round!);
    }

    /// <summary>A soft shadow under a rounded shape: a few widening, fainter copies.</summary>
    protected void SoftRounded(ID2D1DeviceContext dc, in Matrix3x2 transform, double width, double height, double radius,
                               double blur, Vector4 colour, double opacity)
    {
        if (!(opacity > 0.002)) return;
        for (var k = 1; k <= 4; k++)
        {
            var grow = blur * k / 4;
            FillRounded(dc, transform, width + grow, height + grow, radius + grow / 2, colour, opacity * 0.35 / k);
        }
    }

    /// <summary>A four-pointed star of <paramref name="size"/> centred on the transform's origin.</summary>
    protected void FillStar(ID2D1DeviceContext dc, in Matrix3x2 transform, double size, Vector4 colour, double opacity)
    {
        if (!(opacity > 0.002) || !(size > 0)) return;
        dc.Transform = Matrix3x2.CreateScale((float)(size / 2)) * transform;
        dc.FillGeometry(_star!, Brush(colour, opacity), null);
    }

    /// <summary>A hand-drawn rounded rectangle centred on the transform's origin (the Mac's SketchyOutline), stroked.</summary>
    protected void StrokeSketchy(ID2D1DeviceContext dc, in Matrix3x2 transform, double width, double height, double corner,
                                 double amount, double phase, double salt, double stroke, Vector4 colour, double opacity)
    {
        if (!(opacity > 0.002)) return;
        if (!StageShapes.Sketchy(new StageRect(-width / 2, -height / 2, width, height), corner, amount, phase, salt, _points)) return;
        const int count = StageShapes.SketchyPoints;
        for (var i = 0; i < count; i++) _vectors[i] = new Vector2((float)_points[i].X, (float)_points[i].Y);
        dc.Transform = transform;
        var brush = Brush(colour, opacity);
        // The Mac joins the points' midpoints with quadratic curves through them; flattened here into short lines.
        var from = (_vectors[count - 1] + _vectors[0]) / 2;
        for (var i = 0; i < count; i++)
        {
            var control = _vectors[i];
            var to = (_vectors[i] + _vectors[(i + 1) % count]) / 2;
            var previous = from;
            for (var s = 1; s <= 2; s++)
            {
                var t = s / 2f;
                var point = (1 - t) * (1 - t) * from + 2 * (1 - t) * t * control + t * t * to;
                dc.DrawLine(previous, point, brush, (float)stroke, _round);
                previous = point;
            }
            from = to;
        }
    }

    protected void Line(ID2D1DeviceContext dc, Vector2 a, Vector2 b, double stroke, Vector4 colour, double opacity)
    {
        if (!(opacity > 0.002)) return;
        dc.DrawLine(a, b, Brush(colour, opacity), (float)stroke, _round);
    }
}

/// <summary>
/// Planar projective transforms as Direct2D takes them (row vectors, a 4×4 with the z column left alone): the Mac's
/// <c>rotation3DEffect</c> turns, each flattened back onto the plane as SwiftUI's projection does.
/// </summary>
internal static class Homography
{
    public static bool IsAffine(in Matrix4x4 m) => MathF.Abs(m.M14) < 1e-7f && MathF.Abs(m.M24) < 1e-7f && MathF.Abs(m.M44 - 1) < 1e-6f;

    public static Matrix3x2 Flat(in Matrix4x4 m) => new(m.M11, m.M12, m.M21, m.M22, m.M41, m.M42);

    public static Matrix4x4 Of(in Matrix3x2 m) => new(m);

    /// <summary>
    /// A turn of <paramref name="degrees"/> about a vertical axis through <paramref name="anchor"/>, seen in perspective:
    /// SwiftUI's <c>rotation3DEffect(axis: (0, 1, 0), perspective:)</c> for a view of <paramref name="viewSize"/> (the
    /// eye <c>max(width, height) / perspective</c> away). A positive turn sends the side right of the axis away (checked
    /// against the Mac's gallery frames of Hinge and Torque).
    /// </summary>
    public static Matrix4x4 TurnY(double degrees, Vector2 anchor, SizeD viewSize, double perspective)
    {
        if (degrees == 0) return Matrix4x4.Identity;
        var radians = degrees * Math.PI / 180;
        var distance = Math.Max(1, Math.Max(viewSize.Width, viewSize.Height)) / Math.Max(0.01, perspective);
        var turn = Matrix4x4.Identity;
        turn.M11 = (float)Math.Cos(radians);
        turn.M14 = (float)(Math.Sin(radians) / distance);
        return Matrix4x4.CreateTranslation(-anchor.X, -anchor.Y, 0) * turn * Matrix4x4.CreateTranslation(anchor.X, anchor.Y, 0);
    }

    /// <summary>A turn about a horizontal axis through <paramref name="anchor"/>: a positive turn sends the top away.</summary>
    public static Matrix4x4 TurnX(double degrees, Vector2 anchor, SizeD viewSize, double perspective)
    {
        if (degrees == 0) return Matrix4x4.Identity;
        var radians = degrees * Math.PI / 180;
        var distance = Math.Max(1, Math.Max(viewSize.Width, viewSize.Height)) / Math.Max(0.01, perspective);
        var turn = Matrix4x4.Identity;
        turn.M22 = (float)Math.Cos(radians);
        turn.M24 = (float)(-Math.Sin(radians) / distance);
        return Matrix4x4.CreateTranslation(-anchor.X, -anchor.Y, 0) * turn * Matrix4x4.CreateTranslation(anchor.X, anchor.Y, 0);
    }
}
