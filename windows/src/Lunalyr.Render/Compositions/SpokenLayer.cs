// The rolling stages, drawn on the Mac by SpokenTypographyView: Spoken (each sung word takes focus; recent words
// settle around it), Cascade (a moving column of words, arriving with the voice) and Cutout (uneven type and
// album-colour labels, built word by word). The rolling stage (window, measured stack, cursor, word states, fit),
// Cascade's held-word colour and Cutout's labels are Lunalyr's own and ported (Lunalyr.Core.Compositions.RollingStage,
// RollingWindow, RollingLabel); the Verso-derived parts of SpokenTypography and SpokenTypographyView are re-implemented
// from docs/compositions/spoken.md and cascade-cutout.md.

using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Lunalyr.Render.Scene;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Compositions;

/// <summary>A rolling word with its reading, baked as one unit: exactly what the stack measures and the stage fits.</summary>
internal sealed class SpokenUnit : IDisposable
{
    public required Ink Label { get; init; }
    public Ink? Reading { get; init; }
    public Vector2 LabelOffset { get; init; }
    public Vector2 ReadingOffset { get; init; }
    public SizeD Size { get; init; }
    public long LastUsed { get; set; }
    /// <summary>Cutout's label: its tile, and the word and reading in the colour set on it.</summary>
    public Ink? Tile { get; init; }
    public Ink? LabelOnTile { get; init; }
    public Ink? ReadingOnTile { get; init; }

    public void Dispose()
    {
        Label.Dispose();
        Reading?.Dispose();
        Tile?.Dispose();
        LabelOnTile?.Dispose();
        ReadingOnTile?.Dispose();
    }
}

/// <param name="kind">Spoken, Cascade or Cutout.</param>
internal sealed class SpokenLayer(IWordBreaker breaker, Logger? logger, CompositionKind kind = CompositionKind.Spoken)
    : LyricCompositionLayer(kind, breaker, logger)
{
    private RollingSong? _song;
    private RollingWindow? _window;
    private int _windowIndex = -1;
    private RollingWord[] _prepared = [];
    private SpokenUnit?[] _preparedUnits = [];
    private readonly Dictionary<LyricWord, SpokenUnit> _units = [];
    private readonly Dictionary<int, BackingPlan[]> _backing = [];
    private int _trackSeed;

    // Per-frame scratch, grown with the window.
    private SizeD[] _sizes = [];
    private RollingStack.Slot[] _slots = [];
    private PointD[] _centres = [];
    private int[] _order = [];
    private double[] _stack = [];
    private int[] _members = [];
    private RollingTokenState[] _states = [];
    private double[] _reveal = new double[16];

    protected override void Invalidate()
    {
        base.Invalidate();
        _song = null;
        _window = null;
        _windowIndex = -1;
        _prepared = [];
        _preparedUnits = [];
        foreach (var unit in _units.Values) unit.Dispose();
        _units.Clear();
        foreach (var plans in _backing.Values)
            foreach (var plan in plans) plan.Dispose();
        _backing.Clear();
    }

    protected override void OnLyrics(IReadOnlyList<LyricLine> lines, SceneSnapshot scene)
    {
        var readings = scene.Style.Readings == ReadingPlacement.Off ? null : scene.Readings;
        _song = new RollingSong(lines, Breaker, readings, scene.Style.Readings == ReadingPlacement.Instead);
        _trackSeed = (int)(RollingWords.Fnv1a((scene.Title ?? "") + "\u001f" + (scene.Artist ?? "")) & 0x7FFF_FFFF);
    }

    private double LineBase => BaseSize * 0.75 * Metrics.UserScale;

    protected override void DrawRolling(ID2D1DeviceContext dc, int row, double time, double presence)
    {
        if (_song is null) return;
        EnsureWindow(row, time);
        var prepared = _prepared;
        var n = prepared.Length;
        if (n == 0) return;
        var lineBase = LineBase;
        DrawRollingBacking(dc, row, time, presence);
        int? next = null;
        for (var i = 0; i < n; i++)
            if (prepared[i].Source.Start > time) { next = i; break; }
        var intensity = OrganicMotion.VocalIntensity(_window!.Words, time);
        var cursor = RollingWords.Cursor(prepared, time, intensity, false);
        var stacks = RollingWords.Stacks(cursor, false);
        DrawStack(dc, stacks.Position, stacks.Opacity * presence, time, next, intensity, lineBase);
        if (stacks.Count > 1) DrawStack(dc, stacks.SecondPosition, stacks.SecondOpacity * presence, time, next, intensity, lineBase);
    }

    private void DrawStack(ID2D1DeviceContext dc, double position, double alpha, double time, int? next, double intensity,
                           double lineBase)
    {
        var prepared = _prepared;
        var stage = Stage;
        var count = 0;
        for (var k = 0; k < prepared.Length; k++)
        {
            var rank = position - 1 - k;
            if (rank < -2 || rank > 5.2) continue;
            var token = prepared[k];
            var unit = _preparedUnits[k] ??= Unit(token);
            unit.LastUsed = Frame;
            var eligible = k > 0 ? prepared[k - 1].Source.Start : double.NegativeInfinity;
            var state = RollingTokenState.Make(token, next, rank, time, lineBase, stage.Width, intensity, false, Arrival,
                                               Options, eligible, composition: Kind);
            _members[count] = k;
            _sizes[count] = unit.Size;
            _slots[count] = state.Slot;
            _states[count] = state;
            count++;
        }
        if (count == 0) return;
        // Rank 0, the word in focus, sits here; every other word is stacked above or below it by its measured size.
        var anchor = new PointD(stage.Width * 0.5, stage.Height * 0.54 + RollingMotion.Baseline(Kind.RollingStyle(), 0) * lineBase);
        RollingStack.Centres(_sizes.AsSpan(0, count), _slots.AsSpan(0, count), anchor, lineBase * 0.16,
                             _centres.AsSpan(0, count), _order.AsSpan(0, count), _stack.AsSpan(0, count));
        // The word in focus is drawn last (on top).
        var order = _order.AsSpan(0, count);
        for (var i = 0; i < count; i++) order[i] = i;
        for (var i = 1; i < count; i++)
        {
            var member = order[i];
            var j = i - 1;
            while (j >= 0 && _states[order[j]].Focus > _states[member].Focus) { order[j + 1] = order[j]; j--; }
            order[j + 1] = member;
        }
        var origin = StageOrigin;
        var margin = lineBase * 0.22;
        foreach (var m in order)
        {
            ref readonly var state = ref _states[m];
            var opacity = state.Shown * alpha;
            if (!(opacity > 0.002)) continue;
            var slot = _slots[m];
            var centre = _centres[m];
            var fit = RollingStack.Fit(_sizes[m], slot, centre, stage, margin);
            if (!(fit > 0)) continue;
            var token = prepared[_members[m]];
            var unit = _preparedUnits[_members[m]]!;
            var entrance = Matrix3x2.CreateScale((float)slot.EntranceScale) * Matrix3x2.CreateRotation(Radians(slot.Turn));
            if (Kind != CompositionKind.Spoken && Arrival == WordArrival.Unfold)
            {
                // Cascade and Cutout fold an Unfold entrance in about the unit's bottom edge (foreshortened).
                var fold = EntranceTransform.Make(state.Presentation.Progress, state.Presentation.Arrival, Arrival, token.PointSize,
                                                  token.Direction, false, token.TurnLimit).Fold;
                if (fold != 0) entrance *= Fold(fold, unit.Size.Height);
            }
            var world =
                entrance
                * Matrix3x2.CreateTranslation((float)slot.EntranceX, (float)slot.EntranceY)
                * Matrix3x2.CreateScale((float)(fit * state.Scale))
                * Matrix3x2.CreateRotation(Radians(state.Angle))
                * Matrix3x2.CreateTranslation(origin.X + (float)centre.X, origin.Y + (float)centre.Y);
            var scale = (float)(slot.EntranceScale * fit * state.Scale);
            var blur = Combine(Points(Arrival.Blur(state.Presentation.Progress)), Points(state.Pose.Blur)) * scale;
            var reveal = Span<double>.Empty;
            if (token.Parts.Count > 1)
            {
                if (_reveal.Length < token.Parts.Count) _reveal = new double[token.Parts.Count * 2];
                if (RollingTokenState.PartReveal(token, time, state.Presentation.Opacity, intensity, false, Options, _reveal))
                    reveal = _reveal.AsSpan(0, token.Parts.Count);
            }
            if (unit.Tile is not null)
            {
                DrawLabelled(dc, token, unit, state, world, scale, (float)opacity, blur, reveal);
                continue;
            }
            unit.Label.Draw(dc, Matrix3x2.CreateTranslation(unit.LabelOffset) * world, scale, (float)opacity, blur,
                            GlowSpec.None, 1, reveal.IsEmpty ? default : AsSpan(token.Boundaries), reveal, token.RightToLeft);
            if (unit.Reading is { } reading && !state.Presentation.Preview)
                reading.Draw(dc, Matrix3x2.CreateTranslation(unit.ReadingOffset) * world, scale, (float)opacity, blur, GlowSpec.None, 1);
        }
    }

    /// <summary>
    /// A word on Cutout's label. The tile is pressed on from the word's leading edge by its spring (a sliver under the
    /// faint preview), tilted a little; where it has pressed in, the word and its reading take the colour set on it.
    /// The unit's shadow lies under the tile, as the Mac shadows the label and its word as one.
    /// </summary>
    private void DrawLabelled(ID2D1DeviceContext dc, RollingWord token, SpokenUnit unit, in RollingTokenState state,
                              in Matrix3x2 world, float scale, float opacity, float blur, ReadOnlySpan<double> reveal)
    {
        var boundaries = reveal.IsEmpty ? default : AsSpan(token.Boundaries);
        var rightToLeft = token.RightToLeft;
        var labelWorld = Matrix3x2.CreateTranslation(unit.LabelOffset) * world;
        var readingWorld = Matrix3x2.CreateTranslation(unit.ReadingOffset) * world;
        var reading = state.Presentation.Preview ? null : unit.Reading;
        // The label fades as one piece (its word stays dark on a fading tile, as on the Mac), so a label that is not
        // fully there is drawn in a layer at its opacity.
        var layered = opacity < 0.995f;
        if (layered)
        {
            var margin = (float)(unit.Size.Height + unit.Size.Width * 0.1);
            dc.Transform = world;
            dc.PushLayer(new LayerParameters1
            {
                ContentBounds = new Vortice.RawRectF(-(float)unit.Size.Width / 2 - margin, -(float)unit.Size.Height / 2 - margin,
                                                     (float)unit.Size.Width / 2 + margin, (float)unit.Size.Height / 2 + margin),
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = opacity,
                LayerOptions = LayerOptions1.None,
            }, null!);
            opacity = 1;
        }
        try
        {
            DrawLabelParts(dc, token, unit, state, world, labelWorld, readingWorld, reading, scale, opacity, blur, boundaries, reveal);
        }
        finally
        {
            if (layered) dc.PopLayer();
        }
    }

    private static void DrawLabelParts(ID2D1DeviceContext dc, RollingWord token, SpokenUnit unit, in RollingTokenState state,
                                       in Matrix3x2 world, in Matrix3x2 labelWorld, in Matrix3x2 readingWorld, Ink? reading,
                                       float scale, float opacity, float blur, ReadOnlySpan<double> boundaries,
                                       ReadOnlySpan<double> reveal)
    {
        var rightToLeft = token.RightToLeft;
        unit.Label.DrawShadow(dc, labelWorld, opacity, 1, boundaries, reveal, rightToLeft);
        reading?.DrawShadow(dc, readingWorld, opacity, 1);

        var width = (float)unit.Size.Width;
        var pressed = (float)RollingLabel.TileWidth(state.Presentation.Arrival, false);
        var edge = rightToLeft ? width / 2 : -width / 2;
        var tileWorld =
            Matrix3x2.CreateTranslation(-edge, 0) * Matrix3x2.CreateScale(pressed, 1) * Matrix3x2.CreateTranslation(edge, 0)
            * Matrix3x2.CreateRotation(Radians(RollingLabel.DrawnTilt)) * world;
        unit.Tile!.Draw(dc, tileWorld, scale, opacity, blur, GlowSpec.None, 1);

        // Where the tile has pressed in (from the leading edge; the word and reading are centred on the unit) they take
        // the colour set on it, and their own colour beyond. Each colour is drawn only on its side of the edge, so no
        // glyph is drawn twice and the tile's text has no fringe of the other colour.
        var seam = rightToLeft ? width / 2 - width * pressed : -width / 2 + width * pressed;
        float onFrom = rightToLeft ? seam : float.NegativeInfinity, onTo = rightToLeft ? float.PositiveInfinity : seam;
        float offFrom = rightToLeft ? float.NegativeInfinity : seam, offTo = rightToLeft ? seam : float.PositiveInfinity;
        unit.Label.DrawBetween(dc, labelWorld, scale, opacity, blur, offFrom, offTo, boundaries, reveal, rightToLeft);
        unit.LabelOnTile!.DrawBetween(dc, labelWorld, scale, opacity, blur, onFrom, onTo, boundaries, reveal, rightToLeft);
        if (reading is not null)
        {
            reading.DrawBetween(dc, readingWorld, scale, opacity, blur, offFrom, offTo);
            unit.ReadingOnTile?.DrawBetween(dc, readingWorld, scale, opacity, blur, onFrom, onTo);
        }
    }

    /// <summary>A fold about the bottom edge of a unit <paramref name="height"/> tall, drawn as a foreshortening.</summary>
    private static Matrix3x2 Fold(double degrees, double height)
    {
        var bottom = (float)(height / 2);
        return Matrix3x2.CreateTranslation(0, -bottom) * Matrix3x2.CreateScale(1, (float)Math.Cos(degrees * Math.PI / 180))
            * Matrix3x2.CreateTranslation(0, bottom);
    }

    private static ReadOnlySpan<double> AsSpan(IReadOnlyList<double> values) =>
        values is double[] array ? array : values.ToArray();

    /// <summary>The words of the rolling stage: rebuilt only when a word joins or leaves the window.</summary>
    private void EnsureWindow(int index, double time)
    {
        if (_window is not null && _windowIndex == index && _window.Holds(time)) return;
        var lead = Math.Max(1.2, Options.PreviewLead);
        _window = _song!.Window(index, time, lead);
        _windowIndex = index;
        _prepared = RollingWords.Prepare(_window.Words, LineBase, _window.Readings, _window.Following, _window.Ordinals, _window.Parts,
                                         Kind);
        _preparedUnits = new SpokenUnit?[_prepared.Length];
        for (var k = 0; k < _prepared.Length; k++)
            if (_units.TryGetValue(_prepared[k].Source, out var unit) && unit.Size.Width > 0) _preparedUnits[k] = unit;
        var n = Math.Max(1, _prepared.Length);
        if (_sizes.Length < n)
        {
            _sizes = new SizeD[n * 2];
            _slots = new RollingStack.Slot[n * 2];
            _centres = new PointD[n * 2];
            _order = new int[n * 2];
            _stack = new double[n * 2];
            _members = new int[n * 2];
            _states = new RollingTokenState[n * 2];
        }
        // Units of words that have left the window go.
        if (_units.Count > _prepared.Length + 24)
        {
            var keep = new HashSet<LyricWord>(_prepared.Select(word => word.Source));
            foreach (var key in _units.Keys.Where(key => !keep.Contains(key)).ToList())
            {
                _units[key].Dispose();
                _units.Remove(key);
            }
        }
    }

    /// <summary>
    /// A rolling word and its reading, set at the word's size (in the heavy cut in Spoken, for heroes otherwise) and
    /// baked once; on Cutout's label, also the tile and the word and reading in the colour set on it.
    /// </summary>
    private SpokenUnit Unit(RollingWord token)
    {
        if (_units.TryGetValue(token.Source, out var found)) return found;
        var kit = Kit!;
        var (label, reading, width, height) = Layout(kit, token, Stage.Width, Scale);
        using var _ = label;
        using var __ = reading;
        var labelBox = label.Metrics;
        var shadow = new ShadowSpec(0.25f, Points(7) * 0.5f, Points(2));
        // Rasterised at the focus size (and its breath), and scaled down for the words around it.
        const float raster = 1.16f;
        // The word's own colour from the ink: its slot along the song's colours, at the drift of its onset.
        var colours = InkSample(token.Source.Start, token.Slot, token.Seed);
        var rest = colours.Rest;
        var ink = InkAt(token.Source.Start);
        // Cascade's held words take the highlight's hue as type of their own.
        if (Kind == CompositionKind.Cascade && token.Hero && !colours.Sweeps && ink.Accent is { } accent) rest = accent;
        var labelInk = kit.Bake(label, LyricInks.Vector(rest), raster, shadow);
        var readingInk = reading is null ? null : kit.Bake(reading, LyricInks.Vector(colours.Reading), raster, shadow);
        Ink? tile = null, labelOnTile = null, readingOnTile = null;
        if (token.Tile)
        {
            var onTile = LyricInks.Vector(ink.TileText);
            tile = kit.BakeTile(new Vector2((float)width, (float)height), Points(RollingLabel.CornerRadius),
                                LyricInks.Vector(ink.TileColour), raster, shadow);
            labelOnTile = kit.Bake(label, onTile, raster, ShadowSpec.None);
            readingOnTile = reading is null ? null
                : kit.Bake(reading, onTile with { W = onTile.W * (float)Core.Colour.LyricHighlight.ReadingOpacity }, raster, ShadowSpec.None);
        }
        // The word and its reading sit in the middle of the unit's padding.
        var content = height - 2 * Padding(token, Scale).Y;
        var unit = new SpokenUnit
        {
            Label = labelInk,
            Reading = readingInk,
            LabelOffset = new Vector2(0, (float)(-content / 2 + labelBox.Height / 2)),
            ReadingOffset = readingInk is null ? default : new Vector2(0, (float)(content / 2 - readingInk.Box.Y / 2)),
            Size = new SizeD(width, height),
            LastUsed = Frame,
            Tile = tile,
            LabelOnTile = labelOnTile,
            ReadingOnTile = readingOnTile,
        };
        _units[token.Source] = unit;
        return unit;
    }

    /// <summary>
    /// A rolling word and its reading laid out as the stage measures them: the word in the heavy cut (or, for a
    /// Cascade or Cutout word that is not a hero, the regular one), its reading under it (wrapping at 80% of the
    /// stage), 2 points of padding at each side, or 11 by 3 round a Cutout label. The caller disposes the layouts.
    /// </summary>
    internal static (IDWriteTextLayout Label, IDWriteTextLayout? Reading, double Width, double Height) Layout(
        TextKit kit, RollingWord token, double stageWidth, float scale)
    {
        var size = (float)token.PointSize;
        var label = kit.Layout(token.Source.Text, token.Heavy ? Face.Heavy : Face.Regular, size,
                               TextKit.LocaleFor(token.Source.Text), token.RightToLeft);
        var reading = token.Reading is { } text
            ? kit.Layout(text, Face.Quiet, size * 0.56f, TextKit.LocaleFor(text), false, (float)(stageWidth * 0.8))
            : null;
        double width = label.Metrics.Width, height = label.Metrics.Height;
        if (reading is not null)
        {
            width = Math.Max(width, reading.Metrics.Width);
            height += token.PointSize * 0.02 + reading.Metrics.Height;
        }
        if (!token.Tile) return (label, reading, width + 2 * 2 * scale, height);
        var padding = Padding(token, scale);
        return (label, reading, width + 2 * padding.X, height + 2 * padding.Y);
    }

    /// <summary>The padding round a unit, in pixels: 2 points at each side, or 11 by 3 round a Cutout label.</summary>
    internal static (double X, double Y) Padding(RollingWord token, float scale) => token.Tile
        ? (RollingLabel.PaddingX * scale, RollingLabel.PaddingY * scale)
        : (RollingLabel.PlainPaddingX * scale, 0);

    /// <summary>Backing vocals of the lines on the rolling stage, set in Editorial's typography (Spoken itself would take over the stage).</summary>
    private void DrawRollingBacking(ID2D1DeviceContext dc, int index, double time, double presence)
    {
        var song = _song!;
        var (first, last) = LyricPhraseTiming.RollingRange(index, song.Lines);
        for (var row = Math.Max(first, index - 1); row <= Math.Min(last, index); row++)
        {
            var groups = song.Backing(row);
            if (groups.Count == 0) continue;
            if (!_backing.TryGetValue(row, out var plans))
            {
                var box = BackingVocalLayout.Box(Stage);
                plans = new BackingPlan[groups.Count];
                for (var g = 0; g < groups.Count; g++)
                {
                    var group = groups[g];
                    var plan = BuildPlan(group.Words, group.Glue, LineBase * 0.5, _trackSeed + group.Id,
                                         group.Words.Count > 0 ? group.Words[0].Start : 0, wordTimed: true,
                                         editorial: true, null, null, new LineFit(box, 1, 1), box.Width);
                    plans[g] = new BackingPlan(group, plan);
                }
                _backing[row] = plans;
            }
            foreach (var plan in plans) DrawBackingGroup(dc, plan, time, presence, _trackSeed);
        }
    }
}

/// <summary>Editorial: two typefaces in one line, set like a spread.</summary>
internal sealed class EditorialLayer(IWordBreaker breaker, Logger? logger)
    : LyricCompositionLayer(CompositionKind.Editorial, breaker, logger);
