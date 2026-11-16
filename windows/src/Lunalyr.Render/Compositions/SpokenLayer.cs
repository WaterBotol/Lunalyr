// The rolling stages, drawn on the Mac by SpokenTypographyView: Spoken (each sung word takes focus; recent words
// settle around it), Cascade (a moving column of words, arriving with the voice) and Cutout (uneven type and
// album-colour labels, built word by word). The rolling stage (window, measured stack, cursor, word states, fit),
// Cascade's held-word colour and Cutout's labels are Lunalyr's own and ported (Lunalyr.Core.Compositions.RollingStage,
// RollingWindow, RollingLabel); the Verso-derived parts of SpokenTypography and SpokenTypographyView are re-implemented
// from docs/compositions/spoken.md and cascade-cutout.md.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Lunalyr.Render.Scene;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

using Lunalyr.Render.Compositions.Stage;

namespace Lunalyr.Render.Compositions;

/// <summary>A rolling word with its reading, baked as one unit: exactly what the stack measures and the stage fits.</summary>
internal sealed class SpokenUnit : IDisposable
{
    public required Ink Label { get; init; }
    /// <summary>The word in the colour "Sung sweep" fills it with as it is sung; null when the ink doesn't sweep.</summary>
    public Ink? Sung { get; init; }
    public Ink? Reading { get; init; }
    /// <summary>Where each token's share of a glued word's reading falls along it, so it fills in as the word is
    /// sung; null when the reading shows whole.</summary>
    public ReadingSlices? ReadingSlices { get; init; }
    public Vector2 LabelOffset { get; init; }
    public Vector2 ReadingOffset { get; init; }
    public SizeD Size { get; init; }
    public long LastUsed { get; set; }
    /// <summary>Cutout's label: its tile, and the word and reading in the colour set on it.</summary>
    public Ink? Tile { get; init; }
    public Ink? LabelOnTile { get; init; }
    public Ink? ReadingOnTile { get; init; }
    /// <summary>The word's symbol (the Mac's word symbols) in its rest colour, and in its sung colour where it sweeps.</summary>
    public Ink? Symbol { get; init; }
    public Ink? SymbolSung { get; init; }
    public Vector2 SymbolOffset { get; init; }
    /// <summary>The word's action motion (the Mac's action words), if it takes one.</summary>
    public WordAction? Action { get; init; }
    /// <summary>A chorus repeat of an action word already heard: it moves gently (the Mac's <c>WordAccent.gentle</c>).</summary>
    public bool Gentle { get; init; }
    public Vector4 RestColour { get; init; } = Vector4.One;
    public Vector4 SungColour { get; init; } = Vector4.One;

    public void Dispose()
    {
        Label.Dispose();
        Sung?.Dispose();
        Reading?.Dispose();
        Tile?.Dispose();
        LabelOnTile?.Dispose();
        ReadingOnTile?.Dispose();
        Symbol?.Dispose();
        SymbolSung?.Dispose();
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
    /// <summary>Each rolling run's word symbol and action word, planned per whole line and keyed by the run.</summary>
    private readonly Dictionary<LyricWord, RunAccent> _accents = [];
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
    private HighlightPose[] _tiles = [];
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
        // Word-by-word readings where the scene has them, each line's reading string otherwise.
        _song = new RollingSong(lines, Breaker, scene.WordReadings, ReadingDisplay, scene.Readings, scene.Style.BackingVocals);
        PlanAccents(lines, scene);
        _trackSeed = (int)(RollingWords.Fnv1a((scene.Title ?? "") + "\u001f" + (scene.Artist ?? "")) & 0x7FFF_FFFF);
    }

    private double LineBase => BaseSize * 0.75 * Metrics.UserScale;

    /// <summary>
    /// Word symbols and action words (the Mac's ad5a6a6 and cba108f): each word-timed line planned whole, with its own
    /// caps, from the runs it rolls as (lifted backing vocals already out), never from the window on screen; the
    /// window's runs look theirs up. None where readings replace the words.
    /// </summary>
    private void PlanAccents(IReadOnlyList<LyricLine> lines, SceneSnapshot scene)
    {
        var style = scene.Style;
        _accents.Clear();
        if (!(style.WordSymbols || style.ActionWords) || _song is null) return;
        var replacing = style.Readings == ReadingPlacement.Instead;
        for (var row = 0; row < lines.Count; row++)
        {
            if (!lines[row].WordTimed) continue;
            // A line whose romanisation rolls in its words' place takes none.
            if (replacing && (scene.WordReadings.TryGetValue(row, out var read) && read.Count > 0 || scene.Readings.ContainsKey(row))) continue;
            var words = _song.WordsOf(row);
            var texts = new string[words.Count];
            for (var i = 0; i < texts.Length; i++) texts[i] = words[i].Text;
            var gentle = style.ActionWords && ActionRepeats.Of(lines, style.WordLists, Breaker, style.BackingVocals).Contains(row);
            var plan = WordAccents.Plan(texts, style.WordSymbols, style.ActionWords, style.WordLists, gentle);
            for (var i = 0; i < plan.Length; i++)
                if (!plan[i].IsEmpty) _accents[words[i]] = plan[i];
        }
    }

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
        var cursor = RollingWords.Cursor(prepared, time, intensity, ReduceMotion, Kind);
        var stacks = RollingWords.Stacks(cursor, ReduceMotion);
        // A word missing from the window's line ends releases its tile with the line on stage (the Mac's defaultEnd).
        var lineEnd = LyricHighlightTiming.LineEnd(Sheet!.Lines[row].End, _window.Words);
        DrawStack(dc, stacks.Position, stacks.Opacity * presence, time, next, intensity, lineBase, lineEnd);
        if (stacks.Count > 1) DrawStack(dc, stacks.SecondPosition, stacks.SecondOpacity * presence, time, next, intensity, lineBase, lineEnd);
    }

    private void DrawStack(ID2D1DeviceContext dc, double position, double alpha, double time, int? next, double intensity,
                           double lineBase, double defaultEnd)
    {
        var prepared = _prepared;
        // With translations, the words keep clear of the band at the stage's foot where they sit.
        var stage = FreeStage(Stage, TranslationBand);
        var reduce = ReduceMotion;
        var count = 0;
        for (var k = 0; k < prepared.Length; k++)
        {
            var rank = position - 1 - k;
            if (rank < -2 || rank > 5.2) continue;
            var token = prepared[k];
            var unit = _preparedUnits[k] ??= Unit(token);
            unit.LastUsed = Frame;
            var eligible = k > 0 ? prepared[k - 1].Source.Start : double.NegativeInfinity;
            // Each word its own entrance (a mix picks per word; "Cutout · Highlight" moves as the layout's own).
            var chosen = Choice.For(Kind, token.Seed);
            var motion = chosen.Motion(Kind);
            // "Cutout · Highlight": the word's tile, held until its own line ends, keeps it on the stage meanwhile.
            // Cutout's labels are tiles already and take none.
            var tile = chosen.HighlightsSungWords() && !token.Tile
                ? LyricHighlightTiming.Pose(time, token.Source.Start,
                                            _window!.LineEnds.TryGetValue(token.Source, out var lineEnd) ? lineEnd : defaultEnd, reduce)
                : HighlightPose.Hidden;
            _tiles[count] = tile;
            var state = RollingTokenState.Make(token, next, rank, time, lineBase, stage.Width, intensity, reduce, motion,
                                               Options, eligible, tile.Opacity, Kind);
            _members[count] = k;
            _sizes[count] = unit.Size;
            _slots[count] = state.Slot;
            _states[count] = state;
            count++;
        }
        if (count == 0) return;
        // Rank 0, the word in focus, sits here; every other word is stacked above or below it by its measured size.
        // Cutout's focus sits a little lower, so its tall collage keeps off the top edge.
        var anchor = new PointD(stage.Width * 0.5,
                                stage.Height * RollingStack.FocusHeight(Kind) + RollingMotion.Baseline(Kind.RollingStyle(), 0) * lineBase);
        RollingStack.Centres(_sizes.AsSpan(0, count), _slots.AsSpan(0, count), anchor, lineBase * RollingStack.Gap(Kind),
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
        // Title-safe: kept about 5% in from every edge of the stage.
        var (margin, marginY) = RollingStack.Margins(stage, lineBase);
        foreach (var m in order)
        {
            ref readonly var state = ref _states[m];
            var opacity = state.Shown * alpha;
            if (!(opacity > 0.002)) continue;
            var slot = _slots[m];
            var centre = _centres[m];
            // Word presence (Mac queue item 5): the entrance travels further (a soft one rises and grows), the word in
            // focus swells more, and receding words blur more. Drawn only; the stack is measured as before.
            var presence = WordPresence.At(StyleShown.WordPresence);
            var stateScale = state.Scale;
            var stateAngle = state.Angle;
            if (!presence.IsToday && !reduce)
            {
                var soft = slot.EntranceX == 0 && slot.EntranceY == 0 && !state.Presentation.Preview;
                var coming = 1 - state.Presentation.Progress;
                var extra = presence.In - 1;
                slot = slot with
                {
                    EntranceX = slot.EntranceX * presence.In,
                    EntranceY = slot.EntranceY * presence.In + (soft ? Math.Clamp(coming, 0, 1) * prepared[_members[m]].PointSize * 0.32 * extra : 0),
                    Turn = slot.Turn * presence.In,
                    EntranceScale = (1 - (1 - slot.EntranceScale) * presence.In) * (soft ? 1 - 0.12 * extra * Math.Clamp(coming, 0, 1) : 1),
                };
                stateScale = 1 + (state.Scale - 1) * presence.Hold;
                stateAngle = state.Angle * presence.Hold;
            }
            var fit = RollingStack.Fit(_sizes[m], slot, centre, stage, margin, marginY);
            if (!(fit > 0)) continue;
            var token = prepared[_members[m]];
            var unit = _preparedUnits[_members[m]]!;
            var motion = Choice.For(Kind, token.Seed).Motion(Kind);
            var entrance = Matrix3x2.CreateScale((float)slot.EntranceScale) * Matrix3x2.CreateRotation(Radians(slot.Turn));
            if (Kind != CompositionKind.Spoken && motion is WordArrival.Unfold or WordArrival.CutoutFold)
            {
                // Cascade and Cutout fold an Unfold or Cutout · Fold entrance in about the unit's bottom edge
                // (foreshortened). Spoken draws them without the fold, as it always has.
                var fold = EntranceTransform.Make(state.Presentation.Progress, state.Presentation.Arrival, motion, token.PointSize,
                                                  token.Direction, reduce, token.TurnLimit).Fold;
                if (fold != 0) entrance *= Fold(fold, unit.Size.Height);
            }
            var world =
                entrance
                * Matrix3x2.CreateTranslation((float)slot.EntranceX, (float)slot.EntranceY)
                * Matrix3x2.CreateScale((float)(fit * stateScale))
                * Matrix3x2.CreateRotation(Radians(stateAngle))
                * Matrix3x2.CreateTranslation(origin.X + (float)centre.X, origin.Y + (float)centre.Y);
            var scale = (float)(slot.EntranceScale * fit * stateScale);
            // An action word's motion (the Mac's lively pose, 16 Nov 2026), in the unit's own space about its centre,
            // travel in ems of the word; a spin's turn in perspective draws through Direct2D's perspective transform.
            var action = unit.Action is { } act
                ? ActionPose(new RunAccent(null, act, unit.Gentle), token.Source.Start, token.Source.End, time)
                : LivelyPose.Identity;
            var restWorld = world;
            var unitBox = new Vector2((float)unit.Size.Width, (float)unit.Size.Height);
            Matrix4x4? turned = null;
            if (!action.IsIdentity)
            {
                var lively = LivelyWord.Transform(action, token.PointSize, Vector2.Zero, unit.Size) * Homography.Of(world);
                if (Homography.IsAffine(lively)) world = Homography.Flat(lively);
                else turned = lively;
                scale *= (float)Math.Sqrt(Math.Abs(action.Sx * action.Sy));
                opacity *= action.Opacity;
            }
            var progress = state.Presentation.Progress;
            // Presence sharpens the entrance's blur; a receding word keeps its own (stronger, it kept settling for a
            // second on a paused clock).
            var blur = Combine(Points(motion.Blur(progress, reduce) * presence.In), Points(state.Pose.Blur)) * scale;
            // A cutout is unmasked from its leading edge while it arrives (cutouts follow the reveal itself).
            var clip = InkClip.Leading(motion.PaperShown(reduce ? 1 : progress), unit.Size.Width, token.Direction < 0);
            var reveal = Span<double>.Empty;
            if (token.Parts.Count > 1)
            {
                if (_reveal.Length < token.Parts.Count) _reveal = new double[token.Parts.Count * 2];
                if (RollingTokenState.PartReveal(token, time, state.Presentation.Opacity, intensity, reduce, Options, _reveal))
                    reveal = _reveal.AsSpan(0, token.Parts.Count);
            }
            // A glued word's reading fills in with it: each token's share from its onset, never ahead of the token
            // itself (the Mac's LyricReadingInk.shareOpacities with RollingTokenState.partReveal).
            var readingShares = unit.ReadingSlices is { } slices ? slices.Opacities(time, reveal, reduce) : default;
            var readingBoundaries = readingShares.IsEmpty ? default : unit.ReadingSlices!.Boundaries.AsSpan();
            var actionElapsed = time - token.Source.Start;
            DrawLivelyAround(dc, restWorld, Vector2.Zero, unitBox, action, actionElapsed, token.PointSize, unit.RestColour, (float)opacity, front: false);
            if (turned is { } depth)
            {
                // Mid-spin: the word, its symbol and its reading turn as one (no tile, sweep or blur while it turns).
                StageLayer.DrawInk(dc, unit.Label, depth, unit.LabelOffset, opacity, 0, 1,
                                   reveal.IsEmpty ? default : AsSpan(token.Boundaries), reveal, token.RightToLeft);
                if (!state.Presentation.Preview)
                {
                    if (unit.Symbol is { } turning)
                        StageLayer.DrawInk(dc, turning, depth, unit.SymbolOffset, opacity * WordActionMotion.SymbolOpacity(time - token.Source.Start));
                    if (unit.Reading is { } spun)
                        StageLayer.DrawInk(dc, spun, depth, unit.ReadingOffset, opacity, 0, 1, readingBoundaries, readingShares);
                }
                DrawLivelyAround(dc, restWorld, Vector2.Zero, unitBox, action, actionElapsed, token.PointSize, unit.RestColour, (float)opacity, front: true);
                continue;
            }
            var labelWorld = Matrix3x2.CreateTranslation(unit.LabelOffset) * world;
            if (unit.Tile is not null)
            {
                // The label lifts away no faster than the exit, from the next word's onset (the Mac's 436adda).
                var member = _members[m];
                double? nextOnset = member + 1 < prepared.Length ? prepared[member + 1].Source.Start : null;
                DrawLabelled(dc, token, unit, state, world, scale, (float)opacity, blur, reveal, clip, readingBoundaries, readingShares,
                             time, nextOnset);
            }
            else
            {
                // "Cutout · Highlight": the tile lies behind the whole unit, word and reading, reaching a label's padding
                // past it; where it has pressed in, both take the colour set on it.
                var tile = _tiles[m];
                var tileRect = tile.IsVisible ? RollingLabel.HighlightRect(unit.Size.Width, unit.Size.Height, Scale) : default;
                if (tile.IsVisible) DrawTileRect(dc, world, tileRect, tile, token.RightToLeft, opacity);
                var actionGlow = LivelyWord.Glow(action, unit.RestColour, token.PointSize);
                var glowShown = actionGlow.Visible ? actionGlow with { Radius = actionGlow.Radius * scale } : GlowSpec.None;
                // An action word's letters move on their own while its action runs (the Mac's per-glyph pass).
                var lettersDrawn = false;
                if (unit.Action is { } livelyAction && !tile.IsVisible && !clip.IsSet && Revealed(reveal) && !state.Presentation.Preview)
                {
                    var basis = unit.Sung is not null
                        ? Vector4.Lerp(unit.RestColour, unit.SungColour, (float)Math.Clamp(SungSweep.Progress(token, time), 0, 1))
                        : unit.RestColour;
                    var cue = unit.Sung is not null || action.Tints ? LivelyWord.Tinted(action, basis with { W = 1 }, 1) : default;
                    lettersDrawn = DrawLively(dc, unit.Label, labelWorld, scale, new RunAccent(null, livelyAction, unit.Gentle),
                                              token.Source.Start, token.Source.End, time, token.PointSize, basis, unit.RestColour,
                                              action, (float)opacity, 1, glowShown, cue);
                }
                if (!lettersDrawn)
                    unit.Label.Draw(dc, labelWorld, scale, (float)opacity, blur, glowShown,
                                    1, reveal.IsEmpty ? default : AsSpan(token.Boundaries), reveal, token.RightToLeft, clip);
                // "Sung sweep": the sung colour fills the word from its leading edge as it is sung (the Mac's RollingUnit tint).
                if (unit.Sung is { } sung && !tile.IsVisible && !clip.IsSet && !lettersDrawn && SungSweep.Progress(token, time) is var tint and > 0)
                {
                    var (from, to) = SungSweep.Span(unit.Label.Box.X, tint, token.RightToLeft);
                    sung.DrawBetween(dc, labelWorld, scale, (float)opacity, blur, (float)from, (float)to,
                                     reveal.IsEmpty ? default : AsSpan(token.Boundaries), reveal, token.RightToLeft);
                }
                if (unit.Symbol is not null || action.Tints)
                    DrawAccent(dc, token, unit, labelWorld, world, scale, opacity, blur, action, time, state.Presentation.Preview, clip,
                               lettersDrawn, reduce);
                var readingWorld = Matrix3x2.CreateTranslation(unit.ReadingOffset) * world;
                var reading = state.Presentation.Preview ? null : unit.Reading;
                reading?.Draw(dc, readingWorld, scale, (float)opacity, blur, GlowSpec.None, 1, readingBoundaries, readingShares, clip: clip);
                if (tile.IsVisible)
                {
                    var pressed = RollingLabel.Pressed(tileRect, tile.Width, token.RightToLeft);
                    var on = new InkClip((float)pressed.X, (float)(pressed.X + pressed.Width));
                    if (clip.IsSet) on = new InkClip(MathF.Max(on.Left, clip.Left), MathF.Min(on.Right, clip.Right));
                    unit.Label.DrawTinted(dc, labelWorld, TileTextColour(tile, opacity), on);
                    reading?.DrawTinted(dc, readingWorld, TileTextColour(tile, opacity, Core.Colour.LyricHighlight.ReadingOpacity), on,
                                         readingBoundaries, readingShares);
                }
            }
            DrawLivelyAround(dc, restWorld, Vector2.Zero, unitBox, action, actionElapsed, token.PointSize, unit.RestColour, (float)opacity, front: true);
            if (LyricSwipe.Shows(motion, progress, reduce)) DrawSwipe(dc, world, unit.Size, progress, token.Direction, motion, alpha);
        }
    }

    /// <summary>
    /// A word on Cutout's label. The tile is pressed on from the word's leading edge by its spring (a sliver under the
    /// faint preview), tilted a little; where it has pressed in, the word and its reading take the colour set on it.
    /// The unit's shadow lies under the tile, as the Mac shadows the label and its word as one. A cutout entrance
    /// unmasks the whole label (tile, word, reading and shadow) from its leading edge (<paramref name="clip"/>).
    /// </summary>
    private void DrawLabelled(ID2D1DeviceContext dc, RollingWord token, SpokenUnit unit, in RollingTokenState state,
                              in Matrix3x2 world, float scale, float opacity, float blur, ReadOnlySpan<double> reveal,
                              InkClip clip, ReadOnlySpan<double> readingBoundaries, ReadOnlySpan<double> readingShares,
                              double time = double.NaN, double? nextOnset = null)
    {
        var boundaries = reveal.IsEmpty ? default : AsSpan(token.Boundaries);
        var rightToLeft = token.RightToLeft;
        var labelWorld = Matrix3x2.CreateTranslation(unit.LabelOffset) * world;
        var readingWorld = Matrix3x2.CreateTranslation(unit.ReadingOffset) * world;
        var reading = state.Presentation.Preview ? null : unit.Reading;
        // Used with restraint: only the word in focus wears its label; as it steps back the label lifts away and it is
        // plain type again (the Mac's RollingUnit.labelHold).
        var hold = RollingLabel.Hold(state.Slot.Rank);
        if (nextOnset is { } after && double.IsFinite(time))
            hold = Math.Max(hold, 1 - LyricHighlightTiming.ExitProgress(time - after));
        // The label fades as one piece (its word stays dark on a fading tile, as on the Mac), so a label that is not
        // fully there is drawn in a layer at its opacity; a cutout's label is drawn in a layer cut to its unmasked part.
        if (clip.IsSet && !(clip.Right > clip.Left)) return;
        // Once its label has lifted away the word is plain type, drawn at its own opacity without a layer.
        var layered = (opacity < 0.995f && hold > 0.001) || clip.IsSet;
        if (layered)
        {
            var margin = (float)(unit.Size.Height + unit.Size.Width * 0.1);
            var bounds = new Vortice.RawRectF(-(float)unit.Size.Width / 2 - margin, -(float)unit.Size.Height / 2 - margin,
                                              (float)unit.Size.Width / 2 + margin, (float)unit.Size.Height / 2 + margin);
            PushPieceLayer(dc, world, bounds, opacity, clip);
            opacity = 1;
        }
        try
        {
            DrawLabelParts(dc, token, unit, state, world, labelWorld, readingWorld, reading, scale, opacity, blur, boundaries, reveal,
                           readingBoundaries, readingShares, ReduceMotion, hold);
        }
        finally
        {
            if (layered) dc.PopLayer();
        }
    }

    private static void DrawLabelParts(ID2D1DeviceContext dc, RollingWord token, SpokenUnit unit, in RollingTokenState state,
                                       in Matrix3x2 world, in Matrix3x2 labelWorld, in Matrix3x2 readingWorld, Ink? reading,
                                       float scale, float opacity, float blur, ReadOnlySpan<double> boundaries,
                                       ReadOnlySpan<double> reveal, ReadOnlySpan<double> readingBoundaries,
                                       ReadOnlySpan<double> readingShares, bool reduce, double hold = 1)
    {
        // Under Reduce Motion the label is simply there (no press, no tilt) and fades in with its word.
        var rightToLeft = token.RightToLeft;
        unit.Label.DrawShadow(dc, labelWorld, opacity, 1, boundaries, reveal, rightToLeft);
        reading?.DrawShadow(dc, readingWorld, opacity, 1, readingBoundaries, readingShares);
        if (!(hold > 0.001))
        {
            // The label has lifted away: plain type again.
            unit.Label.Draw(dc, labelWorld, scale, opacity, blur, GlowSpec.None, 0, boundaries, reveal, rightToLeft);
            reading?.Draw(dc, readingWorld, scale, opacity, blur, GlowSpec.None, 0, readingBoundaries, readingShares);
            return;
        }

        var width = (float)unit.Size.Width;
        var pressed = (float)RollingLabel.TileWidth(state.Presentation.Arrival, reduce);
        var edge = rightToLeft ? width / 2 : -width / 2;
        var tileWorld =
            Matrix3x2.CreateTranslation(-edge, 0) * Matrix3x2.CreateScale(pressed, 1) * Matrix3x2.CreateTranslation(edge, 0)
            * Matrix3x2.CreateRotation(Radians(reduce ? 0 : RollingLabel.DrawnTilt)) * world;
        unit.Tile!.Draw(dc, tileWorld, scale, opacity * (float)hold, blur, GlowSpec.None, 1);

        // Where the tile has pressed in (from the leading edge; the word and reading are centred on the unit) they take
        // the colour set on it, and their own colour beyond. Each colour is drawn only on its side of the edge, so no
        // glyph is drawn twice and the tile's text has no fringe of the other colour.
        var seam = rightToLeft ? width / 2 - width * pressed : -width / 2 + width * pressed;
        float onFrom = rightToLeft ? seam : float.NegativeInfinity, onTo = rightToLeft ? float.PositiveInfinity : seam;
        float offFrom = rightToLeft ? float.NegativeInfinity : seam, offTo = rightToLeft ? seam : float.PositiveInfinity;
        // As the label lifts away, the word on it changes back to its own colour (the Mac's tile-text switch, at 0.4).
        var on = (float)LyricHighlightTiming.TextOpacity(hold, 0.4, hold < 0.999 ? LyricHighlightTiming.ExitTextBand : LyricHighlightTiming.SwitchWidth);
        unit.Label.DrawBetween(dc, labelWorld, scale, opacity, blur, offFrom, offTo, boundaries, reveal, rightToLeft);
        // Mid-switch the word is whole in its own colour under the tile's text colour, as the Mac draws it.
        if (on < 0.999f) unit.Label.DrawBetween(dc, labelWorld, scale, opacity, blur, onFrom, onTo, boundaries, reveal, rightToLeft);
        unit.LabelOnTile!.DrawBetween(dc, labelWorld, scale, opacity * on, blur, onFrom, onTo, boundaries, reveal, rightToLeft);
        if (reading is not null)
        {
            reading.DrawBetween(dc, readingWorld, scale, opacity, blur, offFrom, offTo, readingBoundaries, readingShares);
            if (on < 0.999f) reading.DrawBetween(dc, readingWorld, scale, opacity, blur, onFrom, onTo, readingBoundaries, readingShares);
            unit.ReadingOnTile?.DrawBetween(dc, readingWorld, scale, opacity * on, blur, onFrom, onTo, readingBoundaries, readingShares);
        }
    }

    /// <summary>
    /// A rolling word's symbol, fading in over 0.3 s from its onset (none while the word is a faint preview) and
    /// crossing to the sung colour as a sweep fills the word, and its action's colour cue laid over word and symbol.
    /// </summary>
    private static void DrawAccent(ID2D1DeviceContext dc, RollingWord token, SpokenUnit unit, in Matrix3x2 labelWorld,
                                   in Matrix3x2 world, float scale, double opacity, float blur, in LivelyPose action,
                                   double time, bool preview, InkClip clip, bool lettersDrawn = false, bool reduce = false)
    {
        var sweep = unit.SymbolSung is not null || action.Tints ? Math.Clamp(SungSweep.Progress(token, time), 0, 1) : 0;
        var basis = Vector4.Lerp(unit.RestColour, unit.SungColour, (float)sweep);
        var cue = LivelyWord.Tinted(action, basis with { W = 1 }, 1);
        if (action.Tints && !lettersDrawn) unit.Label.DrawTinted(dc, labelWorld, cue with { W = (float)opacity }, clip);
        if (unit.Symbol is not { } symbol || preview) return;
        var shown = opacity * WordActionMotion.SymbolOpacity(time - token.Source.Start);
        if (!(shown > 0.002)) return;
        // It pops in, echoing the word's action (the Mac's symbol entrance); under Reduce Motion it only fades.
        var entrance = LivelyWord.SymbolEntrance(unit.Action, time - token.Source.Start, reduce, token.PointSize, symbol.Box, out var cool);
        var symbolWorld = entrance * Matrix3x2.CreateTranslation(unit.SymbolOffset) * world;
        var glow = LivelyWord.Glow(action, unit.RestColour, token.PointSize);
        if (glow.Visible) glow = glow with { Radius = glow.Radius * scale };
        symbol.Draw(dc, symbolWorld, scale, (float)shown, blur, glow, 1, clip: clip);
        if (unit.SymbolSung is { } sung && sweep > 0.002) sung.Draw(dc, symbolWorld, scale, (float)(shown * sweep), blur, GlowSpec.None, 0, clip: clip);
        if (action.Tints) symbol.DrawTinted(dc, symbolWorld, cue with { W = (float)shown }, clip);
        if (cool > 0.005)
            symbol.DrawTinted(dc, symbolWorld, LivelyWord.Tinted(LivelyPose.Identity with { Cool = cool }, basis with { W = 1 }, shown), clip);
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
            _tiles = new HighlightPose[n * 2];
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
        Meter.Enter();
        try
        {
            var unit = SetUnit(token);
            _units[token.Source] = unit;
            return unit;
        }
        finally
        {
            Meter.Exit();
        }
    }

    private SpokenUnit SetUnit(RollingWord token)
    {
        var kit = Kit!;
        // "Add Editorial typography": about a third of the words, by the amount set, take the contrasting face (the Mac's
        // SpokenTypography); "Randomise lyric fonts" gives each word one of the mix's faces by its seed.
        var style = StyleShown;
        var amount = double.IsFinite(style.EditorialAmount) ? Math.Clamp(style.EditorialAmount, 0, 1) : 1;
        var alternate = style.EditorialTypography && token.Hash % 3 == 1 && (double)((token.Hash / 300) % 100) < amount * 100;
        var mix = style.RandomFonts ? (int)(token.Seed % TextKit.MixFaces) : -1;
        var (label, reading, width, height) = Layout(kit, token, Stage.Width, Scale, alternate, mix);
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
        // "Sung sweep" (not on Cascade's held words, which keep the highlight's hue, nor on Cutout's labels).
        var sungInk = colours.Sweeps && !token.Tile && !(Kind == CompositionKind.Cascade && token.Hero)
            ? kit.Bake(label, LyricInks.Vector(colours.Sung), raster, ShadowSpec.None) : null;
        var readingInk = reading is null ? null : kit.Bake(reading, LyricInks.Vector(colours.Reading), raster, shadow);
        var slices = reading is null ? null : ReadingSlices.Measure(reading, token.Reading, token.Shares);
        Ink? tile = null, labelOnTile = null, readingOnTile = null;
        if (token.Tile)
        {
            var onTile = LyricInks.Vector(ink.TileText);
            tile = kit.BakeTile(new Vector2((float)width, (float)height), Points(RollingLabel.CornerRadius),
                                LyricInks.Vector(LabelColour(ink)), raster, shadow);
            labelOnTile = kit.Bake(label, onTile, raster, ShadowSpec.None);
            readingOnTile = reading is null ? null
                : kit.Bake(reading, onTile with { W = onTile.W * (float)Core.Colour.LyricHighlight.ReadingOpacity }, raster, ShadowSpec.None);
        }
        // The word and its reading sit in the middle of the unit's padding.
        var content = height - 2 * Padding(token, Scale).Y;
        var labelY = (float)(-content / 2 + labelBox.Height / 2);
        // A word symbol: 0.16 em from the word, centred on its box, measured with the unit (not on Cutout's labels).
        _accents.TryGetValue(token.Source, out var wordAccent);
        Ink? symbolInk = null, symbolSung = null;
        var labelX = 0f;
        var symbolOffset = Vector2.Zero;
        var sungColour = LyricInks.Vector(colours.Sweeps ? colours.Sung : rest);
        if (!token.Tile && WordSymbolGlyphs.Glyph(wordAccent.Symbol) is { } glyph)
        {
            var symbolSize = (float)Math.Max(Points(6), token.PointSize * 0.5);
            using var symbol = glyph == WordSymbolGlyphs.Dollar
                ? kit.Layout(glyph, Face.Heavy, symbolSize, "en-us", false)
                : kit.SymbolLayout(glyph, symbolSize);
            var gap = token.PointSize * 0.16;
            var symbolWidth = symbol.Metrics.Width;
            var side = token.RightToLeft ? -1f : 1f;
            width += gap + symbolWidth;
            height = Math.Max(height, symbol.Metrics.Height + 2 * Padding(token, Scale).Y);
            labelX = -side * (float)((gap + symbolWidth) / 2);
            symbolOffset = new Vector2(labelX + side * (float)(labelBox.Width / 2 + gap + symbolWidth / 2), labelY);
            symbolInk = kit.Bake(symbol, LyricInks.Vector(rest), raster, shadow);
            symbolSung = sungInk is not null ? kit.Bake(symbol, sungColour, raster, ShadowSpec.None) : null;
        }
        return new SpokenUnit
        {
            Label = labelInk,
            Sung = sungInk,
            Reading = readingInk,
            ReadingSlices = slices,
            LabelOffset = new Vector2(labelX, labelY),
            ReadingOffset = readingInk is null ? default : new Vector2(labelX, (float)(content / 2 - readingInk.Box.Y / 2)),
            Size = new SizeD(width, height),
            LastUsed = Frame,
            Tile = tile,
            LabelOnTile = labelOnTile,
            ReadingOnTile = readingOnTile,
            Symbol = symbolInk,
            SymbolSung = symbolSung,
            SymbolOffset = symbolOffset,
            Action = wordAccent.Action, Gentle = wordAccent.Gentle,
            RestColour = LyricInks.Vector(rest),
            SungColour = sungColour,
        };
    }

    /// <summary>
    /// Cutout's label colour (the Mac's <c>labelColour</c>): the highlight's tile with its chroma eased back
    /// (<see cref="RollingLabel.Calm"/>); the fallback tile likewise when the ink has none; a tile set without its tone
    /// as given.
    /// </summary>
    internal static SrgbColour LabelColour(in LyricInk ink)
    {
        if (ink.Highlight is { } tone) return Perceptual.ToColour(RollingLabel.Calm(tone.Tile));
        return ink.Tile is null ? Perceptual.ToColour(RollingLabel.Calm(Core.Colour.LyricHighlight.Fallback.Tile)) : ink.TileColour;
    }

    /// <summary>
    /// A rolling word and its reading laid out as the stage measures them: the word in the heavy cut (or, for a
    /// Cascade or Cutout word that is not a hero, the regular one), its reading under it (wrapping at 80% of the
    /// stage), 2 points of padding at each side, or a Cutout label's padding (<see cref="RollingLabel.TilePadding"/>).
    /// The caller disposes the layouts.
    /// </summary>
    /// <param name="alternate">Set in Editorial's contrasting face ("Add Editorial typography").</param>
    /// <param name="mix">One of "Randomise lyric fonts" faces, or −1.</param>
    internal static (IDWriteTextLayout Label, IDWriteTextLayout? Reading, double Width, double Height) Layout(
        TextKit kit, RollingWord token, double stageWidth, float scale, bool alternate = false, int mix = -1)
    {
        var size = (float)token.PointSize;
        var face = mix < 0 && alternate ? Face.Contrast : token.Heavy ? Face.Heavy : Face.Regular;
        var label = kit.Layout(token.Source.Text, face, size, TextKit.LocaleFor(token.Source.Text), token.RightToLeft, mix: mix);
        var reading = token.Reading is { } text
            ? kit.Layout(text, mix < 0 && alternate ? Face.Contrast : Face.Quiet, size * 0.56f, TextKit.LocaleFor(text), false,
                         (float)(stageWidth * 0.8), mix)
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

    /// <summary>
    /// The part of the stage the rolling words are placed and fitted in: all of it, less a translation band at its foot
    /// (the Mac's <c>bottomInset</c>), whose top is then the stage's bottom edge for the words.
    /// </summary>
    internal static SizeD FreeStage(SizeD stage, double band) => new(stage.Width, Math.Max(0, stage.Height - band));

    /// <summary>
    /// The padding round a unit, in pixels: 2 points at each side, or round a Cutout label in proportion to its type
    /// (at least 11 by 3 points).
    /// </summary>
    internal static (double X, double Y) Padding(RollingWord token, float scale) => token.Tile
        ? RollingLabel.TilePadding(token.PointSize, scale)
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
                                         editorial: BackingFor(Kind).HasEditorialTypography(), null, null, new LineFit(box, 1, 1), box.Width,
                                         kind: BackingFor(Kind));
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
