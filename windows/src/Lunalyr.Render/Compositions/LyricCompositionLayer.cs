// The lyric stage shared by the compositions: which lines are drawn and how they hand over (the Mac's LyricLayer,
// Lunalyr's own, ported), Editorial-style word-timed lines (ScatteredLyricText: the Verso-derived metrics and rows are
// re-implemented from docs/compositions/editorial.md), line-timed phrases, backing vocals, instrumental breaks, plain
// lyrics and the calm status text.

using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;
using Lunalyr.Render.Scene;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Lunalyr.Render.Compositions;

/// <summary>A lyric composition drawn on one surface. Pure in song time: the pixels depend only on the scene and the time.</summary>
internal abstract class LyricCompositionLayer : IRenderLayer
{
    private const int MaxPlans = 12;
    private readonly CategoryLogger _log;
    private bool _logged;

    protected LyricCompositionLayer(CompositionKind kind, IWordBreaker breaker, Logger? logger)
    {
        Kind = kind;
        Breaker = breaker;
        _log = (logger ?? Logger.Null).Render;
        Rules = new StageRules(RollsWordTimed: kind == CompositionKind.Spoken);
    }

    protected CompositionKind Kind { get; }
    protected IWordBreaker Breaker { get; }
    protected StageRules Rules { get; }
    protected WordRevealOptions Options { get; } = new();
    protected TextKit? Kit { get; private set; }
    protected Vector2 SurfaceSize { get; private set; }
    protected float Scale { get; private set; } = 1;
    protected ViewportMetrics Metrics { get; private set; }
    protected LyricSheet? Sheet { get; private set; }
    protected SceneSnapshot? SceneShown { get; private set; }
    protected WordArrival Arrival { get; private set; } = WordArrival.Fade;
    protected Vector4 TextColour { get; private set; } = Vector4.One;
    /// <summary>The glow of a held word: the palette's highlight (the Mac's ink glow).</summary>
    protected Vector4 GlowColour => SceneShown?.Palette.Highlight ?? TextColour;
    protected Vector4 ReadingColour => new(TextColour.X, TextColour.Y, TextColour.Z, TextColour.W * 0.72f);
    protected long Frame { get; private set; }

    private readonly Dictionary<int, LinePlan> _plans = [];
    private readonly Dictionary<int, LinePlan> _phrases = [];
    private readonly DrawnLine[] _drawn = new DrawnLine[6];
    private readonly StackedLine[] _stacked = new StackedLine[6];
    private readonly double[] _offsets = new double[6];
    private IReadOnlyList<string>? _plainLines;
    private Ink?[] _plain = [];
    private double _plainFit = 1;
    private double _plainHeight;
    private double[] _plainY = [];
    private Ink? _status;
    private string? _statusText;
    private double _statusSince = double.NaN;
    private ID2D1SolidColorBrush? _dotBrush;
    private string[]? _readingParts;

    public bool IsAnimating { get; private set; }

    public void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        ReleaseResources();
        Kit = new TextKit(dc, dwrite);
        _dotBrush = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        Resize(size, scale);
    }

    public void Resize(Vector2 size, float scale)
    {
        SurfaceSize = size;
        Scale = scale > 0 && float.IsFinite(scale) ? scale : 1;
        Invalidate();
    }

    public void ReleaseResources()
    {
        Invalidate();
        _dotBrush?.Dispose();
        _dotBrush = null;
        Kit?.Dispose();
        Kit = null;
    }

    public void Dispose() => ReleaseResources();

    /// <summary>Drops everything set for the current scene; it is set again when next needed.</summary>
    protected virtual void Invalidate()
    {
        foreach (var plan in _plans.Values) plan.Dispose();
        _plans.Clear();
        foreach (var plan in _phrases.Values) plan.Dispose();
        _phrases.Clear();
        foreach (var ink in _plain) ink?.Dispose();
        _plain = [];
        _plainLines = null;
        _status?.Dispose();
        _status = null;
        SceneShown = null;
        Sheet = null;
    }

    public void Render(in FrameContext frame)
    {
        if (Kit is null) return;
        Frame++;
        var dc = frame.Dc;
        var saved = dc.Transform;
        try
        {
            Sync(frame.Scene);
            IsAnimating = false;
            var time = frame.SongSeconds;
            if (Sheet is { Count: > 0 } && double.IsFinite(time))
            {
                DrawSynced(dc, time, frame.WallSeconds);
                IsAnimating = frame.IsPlaying;
            }
            else if (frame.Scene.Lyrics is LyricResult.Plain { Lines.Count: > 0 } plain)
            {
                DrawPlain(dc, plain.Lines);
            }
            else if (!string.IsNullOrWhiteSpace(frame.Scene.Status))
            {
                DrawStatus(dc, frame.Scene.Status!, frame.WallSeconds);
            }
            else
            {
                // Nothing to say: a status shown again later eases in again.
                _statusText = null;
            }
            Trim();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!_logged) _log.Error($"The {Kind} lyric layer could not draw a frame", exception);
            _logged = true;
        }
        finally
        {
            dc.Transform = saved;
        }
    }

    /// <summary>Notices a new scene: new lyrics, readings, style or colours set everything again.</summary>
    private void Sync(SceneSnapshot scene)
    {
        var old = SceneShown;
        if (old is not null && ReferenceEquals(old.Lyrics, scene.Lyrics) && SameReadings(old.Readings, scene.Readings)
            && old.Style == scene.Style && old.Palette.Text == scene.Palette.Text)
        {
            SceneShown = scene;
            return;
        }
        Invalidate();
        SceneShown = scene;
        Kit!.SetFamily(scene.Style.FontFamily);
        Metrics = ViewportMetrics.For(SurfaceSize.X, SurfaceSize.Y, Scale, scene.Style.SizeFraction);
        Arrival = WordArrivals.Parse(scene.Style.Reveal).Resolved(Kind);
        TextColour = scene.Palette.Text;
        if (scene.Lyrics is LyricResult.Synced synced)
        {
            Sheet = LyricSheet.Build(synced.Lines, !LyricPauseTiming.IsQQ(synced.Provenance.Source));
            OnLyrics(synced.Lines, scene);
        }
    }

    private static bool SameReadings(IReadOnlyDictionary<int, string> a, IReadOnlyDictionary<int, string> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        foreach (var (row, reading) in a)
            if (!b.TryGetValue(row, out var other) || other != reading) return false;
        return true;
    }

    /// <summary>New synced lyrics (or readings, size, style): a composition prepares what it sets per song.</summary>
    protected virtual void OnLyrics(IReadOnlyList<LyricLine> lines, SceneSnapshot scene) { }

    // ---- Geometry ------------------------------------------------------------------------------------------------

    protected double BaseSize => Metrics.BaseSize;
    protected SizeD Stage => Metrics.Stage;
    protected Vector2 StageCentre => SurfaceSize / 2;
    protected Vector2 StageOrigin => new((float)((SurfaceSize.X - Stage.Width) / 2), (float)((SurfaceSize.Y - Stage.Height) / 2));
    protected float Points(double points) => (float)(points * Scale);
    protected bool ReadingsShown => SceneShown?.Style.Readings == ReadingPlacement.Below || SceneShown?.Style.Readings == ReadingPlacement.Instead;

    /// <summary>The line's reading, when readings are shown.</summary>
    protected string? ReadingOf(int row)
    {
        if (SceneShown is not { } scene || scene.Style.Readings == ReadingPlacement.Off) return null;
        return scene.Readings.TryGetValue(row, out var reading) && !string.IsNullOrWhiteSpace(reading) ? reading.Trim() : null;
    }

    // ---- The synced stage ------------------------------------------------------------------------------------------

    private struct DrawnLine
    {
        public int Row;
        public bool Outgoing;
        public bool Previewing;
        public double Yield;
        public double PreviewSince;
        public double Opacity;
        public double? Held;
        public bool HeldCross;
    }

    private void DrawSynced(ID2D1DeviceContext dc, double time, double wall)
    {
        var sheet = Sheet!;
        var lines = sheet.Lines;
        var active = sheet.Index.LineAt(time);
        var index = Math.Clamp(active ?? 0, 0, lines.Count - 1);
        var gap = VocalGaps.Find(sheet.VocalWindows, time, double.NaN);
        var lyricOpacity = 1 - (gap?.Presence(time) ?? 0);
        var count = 0;
        // Three back: after two very short lines the one before them can still be leaving.
        for (var row = index - 3; row < index; row++)
        {
            if (!LineFlow.IsLeaving(row, index, lines, time, Rules)) continue;
            var held = Rolls(row) ? null : LineFlow.HeldShift(row, lines, time);
            _drawn[count++] = new DrawnLine
            {
                Row = row, Outgoing = true, Yield = LineFlow.CarriedYield(row, sheet, time, Rules), Opacity = lyricOpacity,
                Held = held, PreviewSince = double.NegativeInfinity,
            };
        }
        int? next = active is not null ? LineFlow.NextLinePreview(index, sheet, time, Rules) : null;
        var since = sheet.PreviewSince[index];
        _drawn[count++] = new DrawnLine
        {
            Row = index, Yield = next is { } n ? LineFlow.PreviewLevel(lines[n], time, since, Options.PreviewLead) : 0,
            Opacity = lyricOpacity, PreviewSince = double.NegativeInfinity,
        };
        if (next is { } preview)
            _drawn[count++] = new DrawnLine { Row = preview, Previewing = true, PreviewSince = since, Opacity = 1 };

        // Held duet lines rise clear of the lines in the middle, by their measured, fitted size.
        var anyHeld = false;
        for (var k = 0; k < count; k++)
        {
            ref var line = ref _drawn[k];
            _stacked[k] = Stacked(line, time);
            anyHeld |= line.Held is > 0;
        }
        if (anyHeld) LineStack.Offsets(_stacked.AsSpan(0, count), Stage.Height, BaseSize * 0.3, _offsets);
        else Array.Clear(_offsets);
        for (var k = 0; k < count; k++)
        {
            ref var line = ref _drawn[k];
            DrawLine(dc, line, time, _offsets[k]);
        }
        if (gap is { } found && found.InterludeCue(time, Options.PreviewLead) is { } cue) DrawCue(dc, cue);
        // The next line is set (laid out and baked) a little ahead of time, not on the frame it first appears.
        var upcoming = index + 1;
        if (upcoming < lines.Count && lines[upcoming].Words.Count > 0 && !Rolls(upcoming)
            && time > lines[upcoming].Start - 1.5 && !_plans.ContainsKey(upcoming) && !_phrases.ContainsKey(upcoming))
        {
            _ = lines[upcoming].WordTimed ? Plan(upcoming) : Phrase(upcoming);
        }
    }

    private StackedLine Stacked(in DrawnLine line, double time)
    {
        var lines = Sheet!.Lines;
        var row = line.Row;
        if (row < 0 || row >= lines.Count || lines[row].Words.Count == 0 || Rolls(row)) return default;
        var plan = lines[row].WordTimed ? Plan(row) : Phrase(row);
        if (plan is null) return default;
        var pose = new LyricPhraseTiming(lines[row], LineFlow.HandOver(row, lines, Rules), time);
        return new StackedLine(plan.Content, true, FitFor(row), line.Previewing ? null : pose.Progress * (1 - pose.Exit), line.Held);
    }

    protected bool Rolls(int row) => Sheet is { } sheet && Rules.Rolls(sheet.Lines, row);

    private LineFit FitFor(int row) => Rolls(row)
        ? new LineFit(Stage, 1, 1)
        : new LineFit(Stage, Metrics.UserScale, 0.75);

    private void DrawLine(ID2D1DeviceContext dc, in DrawnLine drawn, double time, double offset)
    {
        var lines = Sheet!.Lines;
        var row = drawn.Row;
        if (row < 0 || row >= lines.Count) return;
        var line = lines[row];
        if (line.Words.Count == 0 || !(drawn.Previewing || time >= line.Start)) return;
        var pose = new LyricPhraseTiming(line, LineFlow.HandOver(row, lines, Rules), time);
        var rolls = Rolls(row);
        var handsOffPreview = line.WordTimed && !drawn.Outgoing && Options.RevealsWords && Options.PreviewsNextWord;
        var stagePresence = drawn.Previewing ? 1
            : rolls ? LineFlow.RollingPresence(Rolls(row + 1), drawn.Outgoing, pose.Exit)
            : line.WordTimed ? (handsOffPreview ? 1 : pose.Progress)
            : pose.Presence;
        if (!(stagePresence > 0.001)) return;
        var handoff = LineFlow.TimingHandoffPresence(row, lines, time);
        var opacity = drawn.Opacity * handoff;
        if (!(opacity > 0.001)) return;
        if (rolls)
        {
            DrawRolling(dc, row, time, stagePresence * opacity);
            return;
        }
        var heldScale = 1 - LineStack.HeldShrink * Math.Min(1, drawn.Held ?? 0);
        if (line.WordTimed)
        {
            var plan = Plan(row);
            if (plan is null) return;
            var exitStart = drawn.Previewing ? double.PositiveInfinity : LineFlow.StageExitStart(line, pose);
            var swell = 0.965 + 0.035 * pose.Arrival - 0.012 * pose.Exit;
            var makesWay = drawn.Outgoing ? LineFlow.IncomingArrival(row, lines, time, Rules) : 0;
            var slide = ((1 - pose.Arrival) * 0.20 - pose.Exit * 0.10 - 0.30 * makesWay) * BaseSize;
            var content = ContentToDevice(plan, offset, heldScale);
            DrawBacking(dc, plan, time, opacity, row);
            DrawWordTimed(dc, plan, time, exitStart, drawn.PreviewSince, content, plan.Fit * heldScale,
                          stagePresence * opacity, swell, slide);
            DrawLineReading(dc, plan, content, plan.Fit * heldScale, pose.Progress * (1 - pose.Exit) * opacity);
        }
        else
        {
            var plan = Phrase(row);
            if (plan is null) return;
            var heldPreview = !drawn.Previewing && row > 0 && time - line.Start < 1
                && LineFlow.NextLinePreview(row - 1, Sheet!, Math.BitDecrement(line.Start), Rules) == row;
            DrawPhrase(dc, plan, line, row, time, pose, drawn, heldPreview, offset, heldScale, opacity);
        }
    }

    /// <summary>The rolling stage for line <paramref name="row"/> (Spoken).</summary>
    protected virtual void DrawRolling(ID2D1DeviceContext dc, int row, double time, double presence) { }

    /// <summary>Maps a plan's content (top-left origin, layout pixels) onto the stage, fitted and placed.</summary>
    private Matrix3x2 ContentToDevice(LinePlan plan, double offset, double heldScale) =>
        Matrix3x2.CreateTranslation(-(float)(plan.Content.Width / 2), -(float)(plan.Content.Height / 2))
        * Matrix3x2.CreateScale((float)(plan.Fit * heldScale))
        * Matrix3x2.CreateTranslation(StageCentre.X, StageCentre.Y + (float)offset);

    // ---- Word-timed lines (Editorial's typography) ------------------------------------------------------------------

    /// <summary>The plan for a word-timed line, set when first needed.</summary>
    protected LinePlan? Plan(int row)
    {
        if (_plans.TryGetValue(row, out var found)) { found.LastUsed = Frame; return found; }
        var line = Sheet!.Lines[row];
        var glue = Sheet.Glue[row];
        IReadOnlyList<LyricWord> words = line.Words;
        IReadOnlyList<bool> mainGlue = glue;
        var groups = Array.Empty<BackingGroup>() as IReadOnlyList<BackingGroup>;
        if (line.WordTimed)
        {
            var split = BackingVocals.Split(words, glue);
            if (split.Backing.Count > 0)
            {
                words = split.Main;
                mainGlue = split.MainGlue;
                groups = split.Backing;
            }
        }
        var (runReadings, lineReading) = Readings(row, words, mainGlue);
        var plan = BuildPlan(words, mainGlue, BaseSize, row, wordTimed: true, editorial: true, runReadings, lineReading,
                             FitFor(row), Stage.Width, groups, row);
        plan.LastUsed = Frame;
        _plans[row] = plan;
        return plan;
    }

    /// <summary>A line's reading, split over its runs when it has one part per run, otherwise for the whole line.</summary>
    private (string?[]? Runs, string? Line) Readings(int row, IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue)
    {
        if (ReadingOf(row) is not { } reading) return (null, null);
        // A reading that reads just like the lyric is not drawn twice.
        if (!LyricReadingPairs.IsDistinct(reading, string.Concat(words.Select(word => word.Text)))) return (null, null);
        var (visible, visibleGlue) = LyricRuns.Visible(words, glue);
        var ranges = LyricRuns.Ranges(visibleGlue, visible.Count);
        _readingParts = reading.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (ranges.Count > 1 && _readingParts.Length == ranges.Count)
        {
            var result = new string?[ranges.Count];
            for (var r = 0; r < ranges.Count; r++)
            {
                var (start, end) = ranges[r];
                var label = string.Concat(visible.Skip(start).Take(end - start).Select(word => word.Text));
                result[r] = LyricReadingPairs.IsDistinct(_readingParts[r], label) ? _readingParts[r] : null;
            }
            return (result, null);
        }
        if (ranges.Count == 1) return (new string?[] { reading }, null);
        return (null, reading);
    }

    protected LinePlan BuildPlan(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue, double baseSize, int lineIndex,
                                 bool wordTimed, bool editorial, string?[]? runReadings, string? lineReading, LineFit fit,
                                 double maxWidth, IReadOnlyList<BackingGroup>? groups = null, int backingSeed = 0,
                                 bool heavy = false)
    {
        var spec = new PlanSpec(
            BaseSize: baseSize, MinimumSize: 11 * Scale, LineIndex: lineIndex, WordTimed: wordTimed, Editorial: editorial,
            Heavy: heavy, MaxWidth: maxWidth, Spacing: baseSize * 0.02, LineSpacing: baseSize * 0.30, Fit: fit,
            RasterBoost: wordTimed ? 1.12 : 1.0,
            Shadow: wordTimed ? new ShadowSpec(0.52f, Points(7) * 0.5f, Points(2)) : new ShadowSpec(0.32f, Points(8) * 0.5f, Points(2)),
            Colour: TextColour, ReadingColour: ReadingColour, LineReadingSize: BaseSize * 0.38,
            LineReadingWidth: Stage.Width * 0.8, LineReadingShadow: new ShadowSpec(0.3f, Points(6) * 0.5f, Points(2)));
        var plan = LinePlans.Build(Kit!, words, glue, spec, runReadings, lineReading);
        if (groups is { Count: > 0 })
        {
            var box = BackingVocalLayout.Box(Stage);
            var backing = new BackingPlan[groups.Count];
            for (var g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                var sub = LinePlans.Build(Kit!, group.Words, group.Glue, spec with
                {
                    BaseSize = baseSize * 0.5, LineIndex = backingSeed + group.Id, Editorial = true, Heavy = false,
                    MaxWidth = box.Width, Spacing = baseSize * 0.5 * 0.02, LineSpacing = baseSize * 0.5 * 0.30,
                    Fit = new LineFit(box, 1, 1),
                }, null, null);
                backing[g] = new BackingPlan(group, sub);
            }
            return new LinePlan
            {
                Typography = plan.Typography, Runs = plan.Runs, Words = plan.Words, Block = plan.Block,
                LineReading = plan.LineReading, Content = plan.Content, ReadingCentre = plan.ReadingCentre, Fit = plan.Fit,
                TokenFrames = plan.TokenFrames, RunFrames = plan.RunFrames, TokenOpacities = plan.TokenOpacities,
                Order = plan.Order, Backing = backing,
            };
        }
        return plan;
    }

    /// <summary>
    /// A word-timed line set as Editorial sets it: each run lit, swelled, floated and revealed by its own timing; the
    /// whole block eased in (swell and slide), placed by <paramref name="content"/>.
    /// </summary>
    protected void DrawWordTimed(ID2D1DeviceContext dc, LinePlan plan, double time, double exitStart, double previewSince,
                                 in Matrix3x2 content, double contentScale, double opacity, double swell, double slide)
    {
        var typography = plan.Typography;
        var tokens = typography.Tokens;
        if (tokens.Count == 0) return;
        int? next = null;
        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].Source.Start > time) { next = i; break; }
        var energy = OrganicMotion.VocalIntensity(plan.Words, time);
        for (var i = 0; i < tokens.Count; i++)
        {
            var eligible = i > 0 ? tokens[i - 1].Source.Start : previewSince;
            plan.TokenFrames[i] = EditorialTokenFrame.Make(tokens[i], next, time, energy, exitStart, false, eligible, Options);
        }
        var runs = typography.Runs;
        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            plan.RunFrames[r] = EditorialRunFrame.Make(run, plan.TokenFrames, Options.RevealsWords,
                                                       plan.TokenOpacities.AsSpan(run.TokenStart, run.TokenCount));
            plan.Order[r] = r;
        }
        // A word being sung sits above its neighbours.
        var order = plan.Order.AsSpan(0, runs.Count);
        var hot = 0;
        for (var r = 0; r < order.Length; r++)
            if (plan.RunFrames[r].Heat <= 0.05) order[hot++] = r;
        for (var r = 0; r < runs.Count; r++)
            if (plan.RunFrames[r].Heat > 0.05) order[hot++] = r;

        var origin = plan.BlockOrigin;
        var blockCentre = new Vector2((float)(origin.X + plan.Block.Width / 2), (float)(plan.Block.Height / 2));
        var block = Matrix3x2.CreateTranslation(-blockCentre) * Matrix3x2.CreateScale((float)swell)
            * Matrix3x2.CreateTranslation(blockCentre.X, blockCentre.Y + (float)slide) * content;
        foreach (var r in order)
        {
            var run = runs[r];
            var ink = plan.Runs[r];
            ref readonly var frame = ref plan.RunFrames[r];
            var size = run.Metrics.Size;
            var leave = frame.Leave;
            var presentation = frame.Presentation;
            var visibility = presentation.Opacity * (1 - leave) * opacity;
            if (!(visibility > 0.002)) continue;
            var heat = Math.Min(1, frame.Heat);
            var transform = EntranceTransform.Make(presentation.Progress, presentation.Arrival, Arrival, size,
                                                   r % 2 == 0 ? 1 : -1, false);
            var drift = LyricMotion.IdleDrift(run.Seed, Math.Floor(time * 12) / 12, OrganicMotion.IdleAmplitude(OrganicMotion.DefaultLayoutFloat, 1, false));
            var lateral = LyricMotion.Jitter(run.Seed, 12) * 0.10;
            var swellCap = EditorialRunFrame.CappedSwell(
                EditorialRunFrame.Swell(frame.Heat, frame.Glow, frame.DriftScale, false), run.Inset.Width, ink.Unit.Width);
            var held = run.Sustain > 0.02 ? 1 + 0.05 * run.Sustain * Easing.Smoothstep(frame.Fill) : 1;
            var floatScale = drift.Scale * (1 - 0.04 * leave);
            var local =
                Matrix3x2.CreateRotation(Radians(run.Metrics.Rotation))
                * Matrix3x2.CreateScale((float)(swellCap * held))
                * Matrix3x2.CreateRotation(Radians(frame.DriftAngle))
                * Matrix3x2.CreateTranslation((float)(frame.DriftX * size), (float)(frame.DriftY * size))
                * Entrance(transform)
                * Matrix3x2.CreateScale((float)floatScale)
                * Matrix3x2.CreateRotation(Radians(drift.Angle + LyricMotion.Jitter(run.Seed, 13) * 3 * leave))
                * Matrix3x2.CreateTranslation((float)((drift.X + lateral * leave) * size), (float)((drift.Y - 0.26 * leave) * size))
                * Matrix3x2.CreateTranslation((float)(origin.X + ink.Centre.X), (float)(origin.Y + ink.Centre.Y + run.Metrics.WaveOffset))
                * block;
            var scale = contentScale * swell * swellCap * held * transform.Scale * floatScale;
            var blur = Combine(Points(Arrival.Blur(presentation.Progress)), Points(2.4 * leave)) * (float)scale;
            var glow = frame.Glow > 0.002
                ? new GlowSpec(new Vector4(GlowColour.X, GlowColour.Y, GlowColour.Z, (float)(0.6 * frame.Glow * frame.Fill)),
                               Points(10 + 22 * frame.Glow) * (float)scale)
                : GlowSpec.None;
            var shadow = (float)((0.28 + 0.24 * heat) / 0.52);
            var tokenOpacities = frame.RevealsByToken ? plan.TokenOpacities.AsSpan(run.TokenStart, run.TokenCount) : default;
            var labelWorld = Matrix3x2.CreateTranslation(ink.LabelOffset) * local;
            ink.Label.Draw(dc, labelWorld, (float)scale, (float)visibility, blur, glow, shadow,
                           run.Boundaries, tokenOpacities, run.RightToLeft);
            if (ink.Reading is { } reading)
            {
                var readingOpacity = frame.ReadingOpacity(plan.TokenOpacities.AsSpan(run.TokenStart, run.TokenCount));
                reading.Draw(dc, Matrix3x2.CreateTranslation(ink.ReadingOffset) * local, (float)scale,
                             (float)(visibility * readingOpacity), blur, GlowSpec.None, shadow);
            }
        }
    }

    private void DrawLineReading(ID2D1DeviceContext dc, LinePlan plan, in Matrix3x2 content, double scale, double opacity)
    {
        if (plan.LineReading is not { } reading || !(opacity > 0.002)) return;
        var world = Matrix3x2.CreateTranslation((float)plan.ReadingCentre.X, (float)plan.ReadingCentre.Y) * content;
        reading.Draw(dc, world, (float)scale, (float)opacity, 0, GlowSpec.None, 1);
    }

    private void DrawBacking(ID2D1DeviceContext dc, LinePlan plan, double time, double opacity, int seed)
    {
        foreach (var backing in plan.Backing) DrawBackingGroup(dc, backing, time, opacity, seed);
    }

    /// <summary>A backing group: small, dim and behind the lead, in a band near the top or foot of the stage.</summary>
    protected void DrawBackingGroup(ID2D1DeviceContext dc, BackingPlan backing, double time, double opacity, int seed)
    {
        var group = backing.Group;
        var level = BackingVocalLayout.Level(group, time);
        if (!(level > 0.004)) return;
        var box = BackingVocalLayout.Box(Stage);
        var (sink, shrink) = BackingVocalLayout.Exit(BackingVocalLayout.ExitProgress(group, time), box.Height);
        var position = BackingVocalLayout.Position(group, seed, Stage, BaseSize, time, false);
        var hold = BackingVocalLayout.Hold(group);
        var plan = backing.Plan;
        var scale = plan.Fit * shrink;
        var content = Matrix3x2.CreateTranslation(-(float)(plan.Content.Width / 2), -(float)(plan.Content.Height / 2))
            * Matrix3x2.CreateScale((float)scale)
            * Matrix3x2.CreateTranslation(StageOrigin.X + (float)position.X, StageOrigin.Y + (float)(position.Y + sink));
        DrawWordTimed(dc, plan, time, hold, double.NegativeInfinity, content, scale, 0.62 * level * opacity, 1, 0);
    }

    // ---- Line-timed phrases ----------------------------------------------------------------------------------------

    /// <summary>The plan for a line-timed line: one level phrase, every word one size.</summary>
    private LinePlan? Phrase(int row)
    {
        if (_phrases.TryGetValue(row, out var found)) { found.LastUsed = Frame; return found; }
        var line = Sheet!.Lines[row];
        var glue = Sheet.Glue[row];
        var (runReadings, lineReading) = Readings(row, line.Words, glue);
        var phraseBase = BaseSize * (Kind == CompositionKind.Spoken ? 1.26 : 1.06);
        var plan = BuildPlan(line.Words, glue, phraseBase, row, wordTimed: false, editorial: false, runReadings, lineReading,
                             new LineFit(Stage, Metrics.UserScale, 0.75), Stage.Width);
        plan.LastUsed = Frame;
        _phrases[row] = plan;
        return plan;
    }

    private void DrawPhrase(ID2D1DeviceContext dc, LinePlan plan, LyricLine line, int row, double time, LyricPhraseTiming pose,
                            in DrawnLine drawn, bool heldPreview, double offset, double heldScale, double opacity)
    {
        var previewing = drawn.Previewing;
        var pointSize = plan.Runs.Length > 0 ? plan.Typography.Runs[0].Metrics.Size : BaseSize;
        var previewOpacity = WordReveal.Visibility(time, line.Start, line.End, true, true, Options.PreviewLead)
            * Easing.Smoothstep((time - drawn.PreviewSince) / LineFlow.PreviewRamp);
        var visibility = pose.Presence * (1 - 0.5 * drawn.Yield);
        var holdsFirstRun = !previewing && heldPreview && (pose.Progress < 1 || pose.Arrival != 1) && pose.Exit <= 0;
        var content = ContentToDevice(plan, offset, heldScale);
        var scale = plan.Fit * heldScale;
        var progress = previewing ? 1 : pose.Progress;
        var transform = EntranceTransform.Make(progress, previewing ? 1 : pose.Arrival, Arrival, pointSize, row % 2 == 0 ? 1 : -1, false);
        var phrase = Matrix3x2.CreateTranslation(-(float)(plan.Content.Width / 2), -(float)(plan.Content.Height / 2))
            * Entrance(transform)
            * Matrix3x2.CreateTranslation((float)(plan.Content.Width / 2), (float)(plan.Content.Height / 2)) * content;
        var entranceScale = scale * transform.Scale;
        var blur = Points(Arrival.Blur(progress)) * (float)entranceScale;
        var alpha = previewing ? 1 : visibility;
        // The line itself (in preview: only its first word, where it will be).
        DrawPhraseRuns(dc, plan, phrase, entranceScale, alpha * opacity, blur, firstRunOnly: previewing ? previewOpacity : null,
                       hidesFirstRun: holdsFirstRun, readings: !previewing);
        if (holdsFirstRun)
        {
            // The previewed first word stays where the preview showed it while the rest makes its entrance.
            DrawPhraseRuns(dc, plan, content, scale, opacity, 0,
                           firstRunOnly: LineFlow.HeldFirstRunOpacity(pose.Presence) * (1 - 0.5 * drawn.Yield),
                           hidesFirstRun: false, readings: false);
        }
        if (!previewing) DrawLineReading(dc, plan, content, scale, pose.Progress * (1 - pose.Exit) * opacity);
    }

    private static void DrawPhraseRuns(ID2D1DeviceContext dc, LinePlan plan, in Matrix3x2 content, double scale, double opacity,
                                       float blur, double? firstRunOnly, bool hidesFirstRun, bool readings)
    {
        var origin = plan.BlockOrigin;
        for (var r = 0; r < plan.Runs.Length; r++)
        {
            var alpha = firstRunOnly is { } only ? (r == 0 ? only : 0) : hidesFirstRun && r == 0 ? 0 : 1;
            alpha *= opacity;
            if (!(alpha > 0.002)) continue;
            var ink = plan.Runs[r];
            var local = Matrix3x2.CreateTranslation((float)(origin.X + ink.Centre.X), (float)(origin.Y + ink.Centre.Y)) * content;
            ink.Label.Draw(dc, Matrix3x2.CreateTranslation(ink.LabelOffset) * local, (float)scale, (float)alpha, blur, GlowSpec.None, 1);
            if (readings && firstRunOnly is null && ink.Reading is { } reading)
                reading.Draw(dc, Matrix3x2.CreateTranslation(ink.ReadingOffset) * local, (float)scale, (float)alpha, blur, GlowSpec.None, 1);
        }
    }

    // ---- Instrumental breaks, plain lyrics, status -----------------------------------------------------------------

    /// <summary>The three interlude dots of an instrumental break, in the album's accent.</summary>
    private void DrawCue(ID2D1DeviceContext dc, InstrumentalCue cue)
    {
        if (_dotBrush is null || SceneShown is null) return;
        var accents = SceneShown.Palette.Accents;
        var accent = accents.Count > 0 ? accents[0] : TextColour;
        var diameter = (float)(Math.Min(40, Math.Max(15, BaseSize / Scale * 0.3)) * Scale);
        var spacing = diameter * 0.65f;
        var width = diameter * 3 + spacing * 2;
        dc.Transform = Matrix3x2.Identity;
        for (var i = 0; i < 3; i++)
        {
            var highlight = cue.Highlight(i);
            var radius = diameter / 2 * (float)(1 + cue.Opacity * cue.Breath(i) * 0.09);
            var x = StageCentre.X - width / 2 + diameter / 2 + i * (diameter + spacing);
            var y = StageCentre.Y + (float)(cue.Opacity * cue.Bob(i)) * diameter * 0.42f;
            _dotBrush.Color = new Color4(accent.X, accent.Y, accent.Z, (float)(cue.Opacity * (0.32 + highlight * 0.62)));
            dc.FillEllipse(new Ellipse(new Vector2(x, y), radius, radius), _dotBrush);
        }
    }

    /// <summary>Unsynced lyrics: the whole block, fitted to the stage and still.</summary>
    private void DrawPlain(ID2D1DeviceContext dc, IReadOnlyList<string> lines)
    {
        if (!ReferenceEquals(lines, _plainLines))
        {
            foreach (var ink in _plain) ink?.Dispose();
            _plainLines = lines;
            var size = BaseSize * Metrics.UserScale * 0.3;
            var spacing = size * 0.42;
            var layouts = new IDWriteTextLayout?[lines.Count];
            var heights = new double[lines.Count];
            double total = 0;
            try
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    var text = lines[i].Trim();
                    if (text.Length > 0)
                    {
                        layouts[i] = Kit!.Layout(text, Face.Regular, (float)size, TextKit.LocaleFor(text),
                                                 ScriptDirection.IsRightToLeft(text), (float)Stage.Width);
                        heights[i] = layouts[i]!.Metrics.Height;
                    }
                    else
                    {
                        heights[i] = size * 1.3;
                    }
                    total += heights[i] + (i > 0 ? spacing : 0);
                }
                _plainFit = LyricStageFit.PresentationScale(new SizeD(Stage.Width, total), Stage, 1, 1);
                _plainHeight = total;
                _plain = new Ink?[lines.Count];
                _plainY = new double[lines.Count];
                double y = 0;
                var colour = new Vector4(TextColour.X, TextColour.Y, TextColour.Z, TextColour.W * 0.78f);
                for (var i = 0; i < lines.Count; i++)
                {
                    _plainY[i] = y + heights[i] / 2;
                    y += heights[i] + spacing;
                    if (layouts[i] is { } layout)
                        _plain[i] = Kit!.Bake(layout, colour, (float)Math.Clamp(_plainFit, 0.1, 2), new ShadowSpec(0.4f, Points(8) * 0.5f, Points(2)));
                }
            }
            finally
            {
                foreach (var layout in layouts) layout?.Dispose();
            }
        }
        for (var i = 0; i < _plain.Length; i++)
        {
            if (_plain[i] is not { } ink) continue;
            var world = Matrix3x2.CreateTranslation(0, (float)(_plainY[i] - _plainHeight / 2)) * Matrix3x2.CreateScale((float)_plainFit)
                * Matrix3x2.CreateTranslation(StageCentre);
            ink.Draw(dc, world, (float)_plainFit, 1, 0, GlowSpec.None, 1);
        }
    }

    /// <summary>The calm status text ("No lyrics found"), easing in when it changes.</summary>
    private void DrawStatus(ID2D1DeviceContext dc, string status, double wall)
    {
        if (status != _statusText || _status is null)
        {
            _status?.Dispose();
            // A new snapshot (new colours, a new size) sets the same words again without easing them in again.
            if (status != _statusText || !double.IsFinite(_statusSince)) _statusSince = wall;
            _statusText = status;
            using var layout = Kit!.Layout(status, Face.Quiet, (float)(BaseSize * 0.34), TextKit.LocaleFor(status),
                                           ScriptDirection.IsRightToLeft(status), (float)Stage.Width);
            _status = Kit.Bake(layout, new Vector4(TextColour.X, TextColour.Y, TextColour.Z, TextColour.W * 0.5f), 1, ShadowSpec.None);
        }
        var presence = double.IsFinite(_statusSince) ? LyricMotion.Settle(wall - _statusSince, 0.6) : 1;
        IsAnimating = presence < 1;
        _status!.Draw(dc, Matrix3x2.CreateTranslation(StageCentre), 1, (float)presence, 0, GlowSpec.None, 0);
    }

    /// <summary>Keeps only the most recently used line plans.</summary>
    private void Trim()
    {
        Trim(_plans);
        Trim(_phrases);
    }

    private void Trim(Dictionary<int, LinePlan> plans)
    {
        while (plans.Count > MaxPlans)
        {
            var oldest = -1;
            var stamp = long.MaxValue;
            foreach (var (row, plan) in plans)
                if (plan.LastUsed < stamp) { stamp = plan.LastUsed; oldest = row; }
            if (oldest < 0) break;
            plans[oldest].Dispose();
            plans.Remove(oldest);
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    protected static float Radians(double degrees) => (float)(degrees * Math.PI / 180);

    protected static float Combine(float a, float b) => MathF.Sqrt(a * a + b * b);

    /// <summary>An entrance's scale, turn and remaining travel, about the word's centre (fold drawn as a foreshortening).</summary>
    protected static Matrix3x2 Entrance(in EntranceTransform transform)
    {
        var fold = (float)Math.Cos(transform.Fold * Math.PI / 180);
        return Matrix3x2.CreateScale((float)transform.Scale, (float)transform.Scale * fold)
            * Matrix3x2.CreateRotation(Radians(transform.Turn))
            * Matrix3x2.CreateTranslation((float)transform.OffsetX, (float)transform.OffsetY);
    }
}
