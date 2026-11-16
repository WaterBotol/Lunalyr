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
    /// <summary>The word's symbol (the Mac's word symbols) in each of the label's colours, on its baseline beside it.</summary>
    public Ink[] Symbols { get; init; } = [];
    public Vector2 SymbolOffset { get; init; }
    /// <summary>The word's action motion (the Mac's action words), if it takes one.</summary>
    public WordAction? Action { get; init; }
    /// <summary>A chorus repeat of an action word already heard: it moves gently (the Mac's <c>WordAccent.gentle</c>).</summary>
    public bool Gentle { get; init; }
    public Vector4[] Colours { get; init; } = [];
    /// <summary>The centre of the word and its symbol together: an action turns and scales about it.</summary>
    public Vector2 Pivot { get; init; }
    /// <summary>The size the label was set at (after any shrinking), layout pixels.</summary>
    public double FontSize { get; init; }

    public void Dispose()
    {
        foreach (var ink in Labels) ink.Dispose();
        foreach (var ink in Readings) ink.Dispose();
        foreach (var ink in Symbols) ink.Dispose();
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
    /// <summary>The run's word symbol and action word (planned for its whole line, <see cref="StageLayer.Accent"/>).</summary>
    public RunAccent Accent { get; init; }
}

/// <summary>What a stage line is built from.</summary>
internal sealed record StageSource(int Row, LyricLine Line, IReadOnlyList<StageRun> Runs, string?[] Readings, double Speed,
                                   double InkTime, ReadingShares?[]? Shares = null)
{
    /// <summary>The runs are the line's romanisation, shown instead of its words (they take no word accents).</summary>
    public bool Romanised { get; init; }
}

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
    /// <summary>The line's translation (Show translations under lyrics), under the stage and its reading.</summary>
    public Ink? Translation { get; set; }
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
        Translation?.Dispose();
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
    /// <summary>The seed of the line being built (its row, as <see cref="StageLine.Seed"/>): picks its words' faces.</summary>
    private long _bakingSeed;
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
        Meter.Enter();
        try
        {
            var line = SetLine(row);
            line.LastUsed = Frame;
            _lines[row] = line;
            Trim();
            return line;
        }
        finally
        {
            Meter.Exit();
        }
    }

    private StageLine SetLine(int row)
    {
        var sheet = Sheet!;
        var source = sheet.Lines[row];
        IReadOnlyList<LyricWord> words = source.Words;
        IReadOnlyList<bool> glue = sheet.Glue[row];
        // Word-by-word readings where the scene has them (the Mac's romanized): "Instead" sets them in the words' place,
        // grouped as the line's words were.
        var set = WordReadingsOf(row);
        var romanised = set is { Replaced: true };
        if (romanised) { words = set!.Words; glue = set.Glue; }
        IReadOnlyList<BackingGroup> groups = [];
        if (source.WordTimed && StyleShown.BackingVocals)
        {
            var split = BackingVocals.Split(words, glue);
            if (split.Backing.Count > 0) { words = split.Main; glue = split.MainGlue; groups = split.Backing; }
        }
        if (set is null && Replaced(row, words) is { } swapped)
        {
            romanised = true;
            // "Instead", with one part per sung word: the Mac's replace hands the layouts the romanised words, each a
            // word of its own (a Latin reading of a Japanese character stands apart), at the words' own times.
            words = swapped;
            glue = new bool[swapped.Count];
        }
        var runs = MakeRuns(words, glue);
        var (readings, lineReading, shares) = set is not null ? PairWordReadings(row, set, runs) : PairReadings(row, words, runs);
        var starts = new double[words.Count];
        for (var i = 0; i < words.Count; i++) starts[i] = words[i].Start;
        StageLine line;
        _baking = shares;
        _bakingSeed = row;
        // Dark lyrics bake their shadows as a light halo.
        Kit!.Dark = (float)InkAt(source.Start).Dark;
        try
        {
            line = Build(new StageSource(row, source, runs, readings, StageShared.LineSpeed(starts), source.Start, shares) { Romanised = romanised });
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
        if (TranslationOf(row) is { } translation)
        {
            using var layout = Kit!.Layout(translation, Face.Quiet, (float)(BaseSize * LineTranslations.LineSize * Metrics.UserScale),
                                           TextKit.LocaleFor(translation), ScriptDirection.IsRightToLeft(translation),
                                           (float)(Stage.Width * LineTranslations.Width));
            line.Translation = Kit.Bake(layout, LyricInks.Vector(InkAt(source.Start).Base.Reading), 1,
                                        new ShadowSpec(0.3f, Points(6) * 0.5f, Points(2)));
        }
        if (groups.Count > 0)
        {
            var box = BackingVocalLayout.Box(Stage);
            // In Editorial's typography, or the layout chosen for backing vocals.
            CompositionKind? chosen = StyleShown.BackingStyle is null ? null : BackingFor(Kind);
            var plans = new BackingPlan[groups.Count];
            for (var g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                var plan = BuildPlan(group.Words, group.Glue, Unit * 0.5, row + group.Id,
                                     group.Words.Count > 0 ? group.Words[0].Start : source.Start, wordTimed: true,
                                     editorial: chosen?.HasEditorialTypography() ?? true, null, null, new LineFit(box, 1, 1), box.Width,
                                     kind: chosen);
                plans[g] = new BackingPlan(group, plan);
            }
            line.Backing = plans;
        }
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
        var pairs = LyricReadingPairs.Parts(read, line.Words, RunGlue(line.Words, Sheet.Glue[row]));
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

    /// <summary>The runs a line is set in: the shared runs (<see cref="StageRun.Make"/>), or a layout's own (Portal's whole words).</summary>
    protected virtual List<StageRun> MakeRuns(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue) => StageRun.Make(words, glue);

    /// <summary>The glue of the runs <see cref="MakeRuns"/> makes, so word-by-word readings pair with the runs drawn.</summary>
    protected virtual IReadOnlyList<bool> RunGlue(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue) => StageRun.Glue(words, glue);

    /// <summary>
    /// Whether the layout draws Reduce Motion itself (the Mac's <c>ownsReduceMotion</c>: Hinge cross-fades its pages and
    /// words, Fable fades its words), rather than the shared dissolve between steps.
    /// </summary>
    protected virtual bool OwnsReduceMotion => false;

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
        _lineLeave = StageMotion.Leave(time, exitStart);
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
        // A reading that cannot be split by run sits once under the stage, and the line's translation under that; they are
        // fitted to the stage together.
        var fit = 1.0;
        var lift = 0.0;
        var gap = BaseSize * 0.2;
        var below = (line.LineReading is { } reading ? gap + reading.Box.Y : 0) + (line.Translation is { } translated ? gap + translated.Box.Y : 0);
        if (below > 0)
        {
            var total = Stage.Height + below;
            fit = Math.Min(1, Stage.Height / total);
            lift = -below / 2 * fit;
        }
        var origin = StageOrigin;
        var toDevice = local
            * Matrix3x2.CreateScale((float)fit, centre)
            * Matrix3x2.CreateTranslation(origin.X, origin.Y + (float)lift);
        var stage = new Matrix4x4(toDevice);
        var shown = StageMotion.ShownCount(line.Runs, time);
        // Reduce Motion: every layout step is a short dissolve between where the words were and where they now are, not a
        // journey; the curves keep the sum of the two near 1 where nothing moved. Hinge and Fable draw it themselves.
        if (StageMotion.ReducedDissolve(line.Runs, shown, !OwnsReduceMotion, time, reduce) is { } step)
        {
            DrawLine(dc, line, time, exitStart, stage, alpha * (1 - step * step), blur, shown - 1);
            DrawLine(dc, line, time, exitStart, stage, alpha * (1 - (1 - step) * (1 - step)), blur, shown);
        }
        else
        {
            DrawLine(dc, line, time, exitStart, stage, alpha, blur, shown);
        }
        // Below the stage, in the stack they are fitted as (the Mac's VStack of the stage, its reading and translation).
        var top = Stage.Height;
        var underOpacity = (float)(pose.Progress * (1 - pose.Exit) * opacity);
        if (line.LineReading is { } under)
        {
            var world = Matrix3x2.CreateTranslation(centre.X, (float)(top + gap + under.Box.Y / 2)) * toDevice;
            under.Draw(dc, world, (float)fit, underOpacity, 0, GlowSpec.None, 1);
            top += gap + under.Box.Y;
        }
        if (line.Translation is { } translation)
        {
            var world = Matrix3x2.CreateTranslation(centre.X, (float)(top + gap + translation.Box.Y / 2)) * toDevice;
            translation.Draw(dc, world, (float)fit, underOpacity, 0, GlowSpec.None, 1);
        }
    }

    // ---- Baking --------------------------------------------------------------------------------------------------

    /// <summary>The colours of <paramref name="run"/> in its line (the Mac's <c>sample(run, of:)</c>).</summary>
    protected LyricInkSample Sample(StageLine line, StageRun run) => Sample(line.InkTime, line.Seed, run, line.Runs.Count);

    protected LyricInkSample Sample(double inkTime, long seed, StageRun run, int count) =>
        InkSample(inkTime, count > 1 ? (double)run.Id / (count - 1) : 0.5, (ulong)Math.Abs(seed + run.Id));

    /// <summary>
    /// How much of the sung colour a word shows: <paramref name="live"/> while it is the word being sung, and in Sung sweep
    /// all of it once reached, so a sung word keeps its colour when the next begins (the Mac's next/tester-fixes, 15 Nov
    /// 2026: Torque, Fable and Portal turned it back to white).
    /// </summary>
    protected double SungTint(StageLine line, StageRun run, double time, double live) =>
        InkAt(line.InkTime).Mode == LyricColourMode.SungSweep && time >= run.Start ? 1 : live;

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
    /// The faces a run's word and reading are set in: with "Add Editorial typography", about a third of the words (by the
    /// amount set) take the contrasting face, the same words every time for a line; with "Randomise lyric fonts", each
    /// word one of the mix's faces by its seed (the Mac's StageLyricStyles font and readingFont, Lunalyr's own).
    /// </summary>
    private (Face Word, Face Reading, int Mix) FacesFor(StageRun? run, WordSpec spec)
    {
        if (run is null || spec.LabelIsReading || spec.Face == Face.Quiet) return (spec.Face, Face.Quiet, -1);
        var style = StyleShown;
        var seed = _bakingSeed;
        if (style.RandomFonts) return (spec.Face, Face.Quiet, (int)((ulong)Math.Abs(seed + run.Id) % TextKit.MixFaces));
        var amount = double.IsFinite(style.EditorialAmount) ? Math.Clamp(style.EditorialAmount, 0, 1) : 1;
        var alternate = style.EditorialTypography && StageShared.Chance(seed, run.Id, 60) < 0.34
            && StageShared.Chance(seed, run.Id, 61) < amount;
        return alternate ? (Face.Contrast, Face.Contrast, -1) : (spec.Face, Face.Quiet, -1);
    }

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
        var (face, readingFace, mix) = FacesFor(run, spec);
        var label = kit.Layout(text, face, (float)size, locale, rightToLeft, mix: mix);
        try
        {
            var width = label.Metrics.Width;
            // A word with a symbol fits with the symbol's room beside it (its gap and about half the size).
            var reserve = spec.Colours.Length > 0 && WordSymbolGlyphs.Glyph(spec.Accent.Symbol) is not null ? size * 0.85 : 0;
            if (width + reserve > spec.MaxWidth && width > 0)
            {
                size *= Math.Max(spec.MinimumScale, spec.MaxWidth / (width + reserve));
                label.Dispose();
                label = kit.Layout(text, face, (float)size, locale, rightToLeft, mix: mix);
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
                var layout = kit.Layout(reading, readingFace, (float)readingSize, readingLocale, false, mix: mix);
                if (layout.Metrics.Width > spec.ReadingMaxWidth && layout.Metrics.Width > 0)
                {
                    var shrunk = readingSize * Math.Max(0.5, spec.ReadingMaxWidth / layout.Metrics.Width);
                    layout.Dispose();
                    layout = kit.Layout(reading, readingFace, (float)shrunk, readingLocale, false, mix: mix);
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
            // A word symbol: a space of 0.352 of the word's size, then the symbol at half its size on the word's
            // baseline, measured with the word so the stage never reflows when it shows.
            var symbols = Array.Empty<Ink>();
            double symbolWidth = 0, symbolGap = 0, symbolDy = 0;
            if (spec.Colours.Length > 0 && WordSymbolGlyphs.Glyph(spec.Accent.Symbol) is { } glyph)
            {
                var symbolSize = (float)Math.Max(6 * Pt, size * 0.5);
                using var symbol = glyph == WordSymbolGlyphs.Dollar
                    ? kit.Layout(glyph, face, symbolSize, "en-us", false)
                    : kit.SymbolLayout(glyph, symbolSize);
                symbols = new Ink[spec.Colours.Length];
                for (var i = 0; i < symbols.Length; i++) symbols[i] = kit.Bake(symbol, spec.Colours[i], spec.Raster, spec.Shadow);
                symbolWidth = symbol.Metrics.Width;
                symbolGap = size * 0.352;
                symbolDy = Baseline(label) - Baseline(symbol) + symbol.Metrics.Height / 2 - h / 2;
            }
            var wordWidth = w + (symbols.Length > 0 ? symbolGap + symbolWidth : 0);
            var unitWidth = Math.Max(wordWidth, readingWidth);
            var unitHeight = h + (hasReading ? spec.ReadingGap + readingRow : 0);
            float Aligned(double inner) => (float)(spec.Alignment * (unitWidth - inner) / 2);
            // With a symbol the word moves aside by half of it (the symbol follows it in its reading direction).
            var side = rightToLeft ? -1f : 1f;
            var wordShift = symbols.Length > 0 ? -side * (float)((symbolGap + symbolWidth) / 2) : 0f;
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
                LabelOffset = new Vector2(Aligned(wordWidth) + wordShift, (float)(-unitHeight / 2 + h / 2)),
                ReadingOffset = new Vector2(Aligned(readingWidth), (float)(unitHeight / 2 - readingRow / 2)),
                Size = new SizeD(unitWidth, unitHeight),
                Label = new SizeD(w, h),
                Boundaries = boundaries,
                ReadingSlices = readingSlices,
                RightToLeft = rightToLeft,
                Symbols = symbols,
                SymbolOffset = symbols.Length == 0 ? default
                    : new Vector2(Aligned(wordWidth) + wordShift + side * (float)(w / 2 + symbolGap + symbolWidth / 2),
                                  (float)(-unitHeight / 2 + h / 2 + symbolDy)),
                Action = spec.Colours.Length > 0 ? spec.Accent.Action : null, Gentle = spec.Accent.Gentle,
                Pivot = new Vector2(Aligned(wordWidth), (float)(-unitHeight / 2 + h / 2)),
                FontSize = size,
                Colours = spec.Colours,
            };
        }
        finally
        {
            label.Dispose();
        }
    }

    /// <summary>
    /// Bakes the Segoe Fluent Icons <paramref name="glyph"/> at <paramref name="size"/> in each of <paramref name="colours"/>
    /// (the stage's word icons); null for no glyph.
    /// </summary>
    protected Ink[]? BakeSymbol(string? glyph, double size, params Vector4[] colours)
    {
        if (string.IsNullOrEmpty(glyph)) return null;
        var kit = Kit!;
        // A glyph the icon font lacks (the dollar sign) is set from the lyric face, not drawn as the font's empty box.
        using var layout = kit.SymbolFontHas(glyph) ? kit.SymbolLayout(glyph, (float)size)
            : kit.Layout(glyph, Face.Heavy, (float)(size * 0.8), "en-us", false);
        var inks = new Ink[colours.Length];
        for (var i = 0; i < inks.Length; i++) inks[i] = kit.Bake(layout, colours[i], 1.2f, ShadowSpec.None);
        return inks;
    }

    /// <summary>A laid-out text's first baseline from its top, layout pixels.</summary>
    private static double Baseline(Vortice.DirectWrite.IDWriteTextLayout layout)
    {
        var metrics = new Vortice.DirectWrite.LineMetrics[1];
        layout.GetLineMetrics(metrics, out var count);
        return count > 0 ? metrics[0].Baseline : layout.Metrics.Height * 0.8;
    }

    private StageSource? _accentSource;
    private RunAccent[] _accents = [];

    /// <summary>
    /// The word symbol and action word of <paramref name="source"/>'s run <paramref name="index"/> (the Mac's ad5a6a6
    /// and cba108f), planned once for the whole line: English words of word-timed lines only, never a reading shown
    /// instead of the words.
    /// </summary>
    protected RunAccent Accent(StageSource source, int index)
    {
        if (!ReferenceEquals(source, _accentSource))
        {
            _accentSource = source;
            var style = StyleShown;
            var shown = source.Line.WordTimed && !source.Romanised;
            var symbolsOn = shown && style.WordSymbols;
            var actionsOn = shown && style.ActionWords;
            var texts = new string[source.Runs.Count];
            for (var i = 0; i < texts.Length; i++) texts[i] = source.Runs[i].Text;
            _accents = symbolsOn || actionsOn ? WordAccents.Plan(texts, symbolsOn, actionsOn, style.WordLists, actionsOn && GentleRow(source.Row)) : [];
        }
        return index >= 0 && index < _accents.Length ? _accents[index] : default;
    }

    /// <summary>The heavy face's baseline as a share of a line's height (rows of words share their baselines).</summary>
    protected double HeavyBaseline()
    {
        using var layout = Kit!.Layout("Ag", Face.Heavy, 100, "en-us", false);
        var metrics = new Vortice.DirectWrite.LineMetrics[1];
        layout.GetLineMetrics(metrics, out var count);
        return count > 0 && layout.Metrics.Height > 0 ? metrics[0].Baseline / layout.Metrics.Height : 0.8;
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
    /// <param name="glow">A glow behind the ink (an action word's), its radius in unit layout pixels; flat transforms only.</param>
    internal static void DrawInk(ID2D1DeviceContext dc, Ink ink, in Matrix4x4 unit, Vector2 offset, double opacity, float blur = 0,
                                 float shadow = 1, ReadOnlySpan<double> boundaries = default,
                                 ReadOnlySpan<double> opacities = default, bool rightToLeft = false, GlowSpec glow = default)
    {
        if (!(opacity > 0.002)) return;
        var world = Matrix4x4.CreateTranslation(offset.X, offset.Y, 0) * unit;
        if (Homography.IsAffine(world))
        {
            var flat = Homography.Flat(world);
            var scale = MathF.Sqrt(MathF.Abs(flat.GetDeterminant()));
            ink.Draw(dc, flat, scale, (float)opacity, blur, glow.Visible ? glow with { Radius = glow.Radius * scale } : GlowSpec.None,
                     shadow, boundaries, opacities, rightToLeft);
            return;
        }
        // Baked now if its turn has not come yet (it is about to be drawn).
        if (!ink.Bake()) return;
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
    /// <param name="poseInUnit">The layout moved the word's whole piece by its action already (Portal's pill): the
    /// letters and their particles still move, the word is not moved again.</param>
    /// <param name="particles">Whether this layer draws the action's particles and whirlwind (one layer of a word drawn
    /// in several); by default every colour but the live overlay (1).</param>
    protected void DrawLabel(ID2D1DeviceContext dc, StageLine line, StageWord word, StageRun run, int colour, double time,
                             in Matrix4x4 unit, double opacity, float blur = 0, float shadow = 1, Vector2 nudge = default,
                             bool poseInUnit = false, bool? particles = null)
    {
        var particleLayer = particles ?? colour != 1;
        var parts = word.Boundaries is null ? default : line.PartOpacities(run, time);
        if (word.Action is null && word.Symbols.Length == 0 && (ReduceMotion || !(StyleShown.WordPresence > 1)))
        {
            DrawInk(dc, word.Labels[colour], unit, word.LabelOffset + nudge, opacity, blur, shadow, word.Boundaries, parts,
                    word.RightToLeft);
            return;
        }
        // An action word's motion about the word's centre (scale with the spin's turn, then the offset in ems), and
        // its colour cue laid over it; the symbol beside it moves with it and fades in from the word's onset.
        var moved = Acting(word, run, time, unit, nudge, out var pose, out var rest, poseInUnit);
        var shown = opacity * pose.Opacity;
        if (!ReduceMotion && StyleShown.WordPresence > 1) blur = Math.Max(blur, Points(PresenceOf(run, time).Blur));
        // An action's glow (and Reduce Motion's cue): a shadow in the word's own colour.
        var tone = word.Colours[Math.Min(colour, word.Colours.Length - 1)];
        var glow = LivelyWord.Glow(pose, tone, word.FontSize);
        // An action word's letters move on their own while its action runs (the Mac's per-glyph pass), once every part of
        // it is there and while it is drawn flat; the particles come with its first colour only.
        var lettersDrawn = false;
        var around = Homography.IsAffine(rest) && particleLayer;
        var wordCentre = (word.Symbols.Length > 0 ? word.Pivot : word.LabelOffset) + nudge;
        var wordBox = word.Labels[colour].Box;
        if (around) DrawLivelyAround(dc, Homography.Flat(rest), wordCentre, wordBox, pose, time - run.Start, word.FontSize, tone, (float)shown, front: false);
        var labelWorld = Matrix4x4.CreateTranslation(word.LabelOffset.X + nudge.X, word.LabelOffset.Y + nudge.Y, 0) * moved;
        if (word.Action is { } livelyAction && Homography.IsAffine(labelWorld) && Revealed(parts))
        {
            var flat = Homography.Flat(labelWorld);
            var worldScale = MathF.Sqrt(MathF.Abs(flat.GetDeterminant()));
            var cue = pose.Tints ? LivelyWord.Tinted(pose, tone with { W = 1 }, 1) : default;
            lettersDrawn = DrawLively(dc, word.Labels[colour], flat, worldScale, new RunAccent(null, livelyAction, word.Gentle),
                                      run.Start, run.End, time, word.FontSize, tone, tone, pose, (float)shown, shadow,
                                      glow.Visible ? glow with { Radius = glow.Radius * worldScale } : GlowSpec.None, cue, particleLayer);
        }
        if (!lettersDrawn)
        {
            DrawInk(dc, word.Labels[colour], moved, word.LabelOffset + nudge, shown, blur, shadow, word.Boundaries, parts,
                    word.RightToLeft, glow);
            if (pose.Tints) DrawCue(dc, word.Labels[colour], word.Colours[Math.Min(colour, word.Colours.Length - 1)], pose, moved, word.LabelOffset + nudge, shown);
        }
        if (word.Symbols.Length > colour)
        {
            var symbol = shown * WordActionMotion.SymbolOpacity(time - run.Start);
            // It pops in, echoing the word's action (the Mac's symbol entrance); under Reduce Motion it only fades.
            var entrance = LivelyWord.SymbolEntrance(word.Action, time - run.Start, ReduceMotion, word.FontSize,
                                                     word.Symbols[colour].Box, out var cool);
            var symbolUnit = Homography.Of(entrance) * Matrix4x4.CreateTranslation(word.SymbolOffset.X + nudge.X, word.SymbolOffset.Y + nudge.Y, 0) * moved;
            DrawInk(dc, word.Symbols[colour], symbolUnit, Vector2.Zero, symbol, blur, shadow, glow: glow);
            if (pose.Tints) DrawCue(dc, word.Symbols[colour], word.Colours[Math.Min(colour, word.Colours.Length - 1)], pose, symbolUnit, Vector2.Zero, symbol);
            if (cool > 0.005)
                DrawCue(dc, word.Symbols[colour], word.Colours[Math.Min(colour, word.Colours.Length - 1)],
                        LivelyPose.Identity with { Cool = cool }, symbolUnit, Vector2.Zero, symbol);
        }
        if (around) DrawLivelyAround(dc, Homography.Flat(rest), wordCentre, wordBox, pose, time - run.Start, word.FontSize, tone, (float)shown, front: true);
    }

    /// <summary>
    /// <paramref name="unit"/> with <paramref name="word"/>'s action motion layered on it (the Mac's lively pose, 16 Nov
    /// 2026), about the word's centre (its label at <paramref name="nudge"/>): the scale about its anchor, a spin's turn
    /// in perspective, the tilt, then the travel in ems of its size; word presence about the same centre after it.
    /// Unchanged when the word takes no action or its motion is over.
    /// </summary>
    protected Matrix4x4 Acting(StageWord word, StageRun run, double time, in Matrix4x4 unit, Vector2 nudge, out LivelyPose pose) =>
        Acting(word, run, time, unit, nudge, out pose, out _);

    /// <summary>As <see cref="Acting(StageWord, StageRun, double, in Matrix4x4, Vector2, out LivelyPose)"/>, with
    /// <paramref name="rest"/> the word's frame before its action (word presence and the unit).</summary>
    private Matrix4x4 Acting(StageWord word, StageRun run, double time, in Matrix4x4 unit, Vector2 nudge, out LivelyPose pose,
                             out Matrix4x4 rest, bool poseInUnit = false)
    {
        pose = word.Action is { } action ? ActionPose(new RunAccent(null, action, word.Gentle), run.Start, run.End, time) : LivelyPose.Identity;
        rest = unit;
        // A layout that moves the word's whole piece itself (Portal's pill) has the pose in the unit already.
        if (poseInUnit) return unit;
        // Word presence (Mac queue item 5): a rise and grow as the word arrives, a swell while it is sung, a drift up
        // as the line leaves. None under Reduce Motion.
        var (presenceDy, presenceScale, _) = ReduceMotion ? (0.0, 1.0, 0.0) : PresenceOf(run, time);
        if (pose.IsIdentity && presenceDy == 0 && presenceScale == 1) return unit;
        var size = word.FontSize;
        var centre = (word.Symbols.Length > 0 ? word.Pivot : word.LabelOffset) + nudge;
        var box = word.Labels.Length > 0 ? word.Labels[0].Box : new Vector2((float)size, (float)size);
        var lively = LivelyWord.Transform(pose, size, centre, new SizeD(box.X, box.Y));
        var presence = presenceDy == 0 && presenceScale == 1 ? Matrix4x4.Identity
            : Matrix4x4.CreateTranslation(-centre.X, -centre.Y, 0) * Matrix4x4.CreateScale((float)presenceScale, (float)presenceScale, 1)
              * Matrix4x4.CreateTranslation(centre.X, centre.Y + (float)(presenceDy * size), 0);
        rest = presence * unit;
        return lively * rest;
    }

    /// <summary>The line being drawn: how far it has left (0 while it stays).</summary>
    private double _lineLeave;

    /// <summary>A stage word's presence at <paramref name="time"/>: its arrival spring, how much it is being sung, and the line's leave.</summary>
    private (double Dy, double Scale, double Blur) PresenceOf(StageRun run, double time)
    {
        var strength = StyleShown.WordPresence;
        if (!(strength > 1)) return (0, 1, 0);
        var arrival = StageMotion.Arrival(run.Start, time);
        var sung = Easing.Smoothstep((time - run.Start) / 0.12) * (1 - Easing.Smoothstep((time - Math.Max(run.End, run.Start + 0.15)) / 0.35));
        return WordPresence.Stage(strength, arrival, sung, _lineLeave);
    }

    /// <summary>An action's warm or cool colour cue: the ink's colour multiplied, laid over it (never lighter).</summary>
    private static void DrawCue(ID2D1DeviceContext dc, Ink ink, Vector4 colour, in LivelyPose pose, in Matrix4x4 unit,
                                Vector2 offset, double opacity)
    {
        if (!(opacity > 0.002)) return;
        var world = Matrix4x4.CreateTranslation(offset.X, offset.Y, 0) * unit;
        if (!Homography.IsAffine(world)) return;
        ink.DrawTinted(dc, Homography.Flat(world), LivelyWord.Tinted(pose, colour, opacity), InkClip.None);
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
