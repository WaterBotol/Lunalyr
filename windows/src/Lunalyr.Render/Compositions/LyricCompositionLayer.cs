// The lyric stage shared by the compositions: which lines are drawn and how they hand over (the Mac's LyricLayer,
// Lunalyr's own, ported), Editorial-style word-timed lines (ScatteredLyricText: the Verso-derived metrics and rows are
// re-implemented from docs/compositions/editorial.md), line-timed phrases, backing vocals, instrumental breaks, plain
// lyrics and the calm status text.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;
using Lunalyr.Render.Pacing;
using Lunalyr.Render.Scene;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Box = Lunalyr.Core.Motion.Box;
using Double3 = Lunalyr.Core.Colour.Double3;
using Segment = Lunalyr.Core.Motion.Segment;

namespace Lunalyr.Render.Compositions;

/// <summary>A lyric composition drawn on one surface. Pure in song time: the pixels depend only on the scene and the time.</summary>
internal abstract partial class LyricCompositionLayer : IRenderLayer
{
    private const int MaxPlans = 12;
    private readonly CategoryLogger _log;
    private AheadWork? _aheadWork;
    private bool _logged;

    protected LyricCompositionLayer(CompositionKind kind, IWordBreaker breaker, Logger? logger)
    {
        Kind = kind;
        Breaker = breaker;
        _log = (logger ?? Logger.Null).Render;
        Rules = new StageRules(RollsWordTimed: kind.ArrivalOnly());
        if (kind.FillsStage()) Rules = Rules with { FillsStage = true };
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
    /// <summary>The entrance the style chose (one entrance, "Match the style" or a mix).</summary>
    protected EntranceChoice Choice { get; private set; } = new(WordArrival.Fade);
    /// <summary>The layer's own entrance when no word decides it (a mix gives each word its own: see <see cref="Choice"/>).</summary>
    protected WordArrival Arrival { get; private set; } = WordArrival.Fade;
    /// <summary>The palette's still text colour: status text, plain lyrics and anything not set word by word.</summary>
    protected Vector4 TextColour { get; private set; } = Vector4.One;
    protected Vector4 ReadingColour => new(TextColour.X, TextColour.Y, TextColour.Z, TextColour.W * 0.72f);
    protected long Frame { get; private set; }

    // The lyric ink (the Mac's Album drift): settled once per palette, then drifted by song time.
    private Palette? _inkPalette;
    private (string Mode, string Custom) _inkHighlight;
    private LyricInk _ink = LyricInk.White;
    private InkSettling? _settling;

    /// <summary>The scene's settled ink (see <see cref="InkSettling"/>), waited for here the first time it is needed.</summary>
    private LyricInk SettledInk
    {
        get
        {
            if (_settling is { } settling)
            {
                _ink = settling.Ink;
                _settling = null;
            }
            return _ink;
        }
    }

    /// <summary>
    /// The ink for words sung around <paramref name="songSeconds"/>. The Mac drifts its ink through the album's
    /// colours on the wall clock (about 45 s a loop) and redraws every glyph each frame; here the colours are baked
    /// when a line is set, so a line takes the drift at its own start time. Keyed by song time, a frame still depends
    /// only on the scene and the time.
    /// </summary>
    protected LyricInk InkAt(double songSeconds)
    {
        var ink = SettledInk;
        ink.Phase = LyricInks.DriftPhase(songSeconds, ReduceMotion);
        return ink;
    }

    /// <summary>
    /// The scene asks for less motion (<see cref="SceneSnapshot.ReduceMotion"/>, the Mac's <c>lyricReduceMotion</c>):
    /// entrances, swells, drifts, travel and blur become plain fades.
    /// </summary>
    protected bool ReduceMotion => SceneShown?.ReduceMotion == true;

    /// <summary>The style of the scene shown (the default before the first).</summary>
    protected LyricStyle StyleShown => SceneShown?.Style ?? LyricStyle.Default;

    /// <summary>The colours of a word at <paramref name="slot"/> along its line, sung around <paramref name="songSeconds"/>.</summary>
    protected LyricInkSample InkSample(double songSeconds, double slot, ulong seed) => InkAt(songSeconds).Sample(slot, seed);

    private readonly Dictionary<int, LinePlan> _plans = [];
    private readonly Dictionary<int, LinePlan> _phrases = [];
    // Three leaving lines (each drawn twice while a held one cross-fades under Reduce Motion), the line and its preview.
    private readonly DrawnLine[] _drawn = new DrawnLine[9];
    private readonly StackedLine[] _stacked = new StackedLine[9];
    private readonly double[] _offsets = new double[9];
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
    // Entrance extras: the swipe's colours and ribbon, highlight tiles, constellation links.
    private ID2D1SolidColorBrush? _paint;
    private ID2D1StrokeStyle? _roundCaps;
    private ID2D1RectangleGeometry? _unitSquare;
    private ID2D1GradientStopCollection? _ribbonStops;
    private ID2D1LinearGradientBrush? _ribbon;
    private LyricSwipePalette _swipe = LyricSwipePalette.Default;
    private readonly Box[] _linkBoxes = new Box[ConstellationGeometry.MaxWords];
    private readonly Segment[] _linkScratch = new Segment[ConstellationGeometry.MaxWords - 1];

    public bool IsAnimating { get; private set; }

    /// <summary>The render-thread time spent setting lines up (for the line-setup benchmark).</summary>
    internal Diagnostics.SetupMeter Meter { get; } = new();

    // Setting lines up a slice at a time: a line is laid out when it is needed (or 1.5 s ahead) and its text baked
    // within the pass's allowance (AheadWork) over the next frames, or at the latest when it is first drawn.
    private bool _spreads = true;

    /// <summary>A line (or credit) was laid out ahead this frame.</summary>
    private bool _laidOutAhead;
    private bool _aheadOpen;
    private long _aheadStart;
    private long _aheadDeadline;

    /// <summary>
    /// Whether line setup is spread over frames (the default). Off, lines are set exactly as before this was added: each
    /// laid out and baked whole on the frame that first needs it (or 1.5 s ahead, the line after the one being sung),
    /// and every line on the stage measured each frame. What is drawn is the same either way (the frame-identity tests
    /// compare the two). Set it before <see cref="CreateResources"/>.
    /// </summary>
    internal bool SpreadsSetup
    {
        get => _spreads;
        set
        {
            _spreads = value;
            if (Kit is { } kit) kit.Defers = value;
        }
    }

    /// <summary>True while text laid out ahead is still waiting to be baked.</summary>
    internal bool SetupPending => Kit?.HasPending == true;

    public void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        ReleaseResources();
        Kit = new TextKit(dc, dwrite) { Defers = _spreads, Meter = Meter };
        _dotBrush = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _paint = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        using (var factory = dc.Factory)
        {
            _roundCaps = factory.CreateStrokeStyle(new StrokeStyleProperties
            {
                StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round, LineJoin = LineJoin.Round,
            });
            _unitSquare = factory.CreateRectangleGeometry(new Vortice.RawRectF(0, 0, 1, 1));
        }
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
        ReleaseOpening();
        _dotBrush?.Dispose();
        _dotBrush = null;
        _paint?.Dispose();
        _paint = null;
        _roundCaps?.Dispose();
        _roundCaps = null;
        _unitSquare?.Dispose();
        _unitSquare = null;
        DropRibbon();
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
        DropTrackName();
        SceneShown = null;
        Sheet = null;
        InvalidateExtras();
    }

    public void Render(in FrameContext frame)
    {
        if (Kit is null) return;
        Frame++;
        _aheadWork = frame.AheadWork;
        _aheadOpen = false;
        _laidOutAhead = false;
        var dc = frame.Dc;
        var saved = dc.Transform;
        try
        {
            Sync(frame.Scene);
            IsAnimating = false;
            var opening = PrepareOpening(frame);
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
                // A verdict that the song has no lyrics shows as "When none are found" says; waits always show.
                if (frame.Scene.NoneFound) DrawNoneFound(dc, frame.Scene, frame.WallSeconds);
                else DrawStatus(dc, frame.Scene.Status!, frame.WallSeconds);
            }
            else
            {
                // Nothing to say: a status shown again later eases in again.
                _statusText = null;
            }
            DrawOpening(dc, opening, frame);
            // What is left of the pass's allowance bakes what has been laid out ahead; a frame that laid a line out
            // leaves its baking to the next (a slice begun near the end of the allowance would run past it).
            if (Kit is { HasPending: true } kit && !_laidOutAhead && BeginAhead()) kit.RunPending(_aheadDeadline);
            EndAhead();
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
            && SameWordReadings(old.WordReadings, scene.WordReadings) && old.Style == scene.Style && LyricInks.SameInk(old.Palette, scene.Palette)
            && old.ReduceMotion == scene.ReduceMotion)
        {
            SceneShown = scene;
            return;
        }
        Invalidate();
        SceneShown = scene;
        Kit!.SetFamily(scene.Style.FontFamily);
        Metrics = scene.Style.FloatingTextSize is { } textSize
            ? ViewportMetrics.ForText(SurfaceSize.X, SurfaceSize.Y, Scale, textSize, scene.Style.SizeFraction)
            : ViewportMetrics.For(SurfaceSize.X, SurfaceSize.Y, Scale, scene.Style.SizeFraction);
        // Each word's entrance comes from the choice (a mix picks per word); "Cutout · Highlight" adds a tile and the
        // words still enter as the layout has them enter.
        Choice = scene.Style.EntranceFor(Kind);
        Arrival = Choice.For(Kind, 0).Motion(Kind);
        var highlight = (scene.Style.HighlightMode, scene.Style.HighlightCustom);
        if ((!ReferenceEquals(_inkPalette, scene.Palette) && !(_inkPalette is not null && LyricInks.SameInk(_inkPalette, scene.Palette)))
            || highlight != _inkHighlight)
        {
            // Worked out off the render thread; it is waited for only when first needed.
            _settling = new InkSettling(scene.Palette, scene.Style);
            _inkHighlight = highlight;
            _inkPalette = scene.Palette;
            var stops = new Double3[scene.Palette.ArtworkStops.Count];
            for (var i = 0; i < stops.Length; i++)
            {
                var stop = scene.Palette.ArtworkStops[i];
                stops[i] = new Double3(stop.X, stop.Y, stop.Z);
            }
            _swipe = LyricSwipePalette.ForCover(stops);
            DropRibbon();
        }
        // Status text and plain lyrics keep the palette's still text colour; timed words take the drifting ink.
        TextColour = scene.Palette.Text;
        if (scene.Lyrics is LyricResult.Synced synced)
        {
            Sheet = LyricSheet.Build(synced.Lines, !LyricPauseTiming.IsQQ(synced.Provenance.Source));
            SyncExtras(scene, synced.Lines);
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

    // A new readings table comes in a new snapshot; one carried over by `with` is the same object.
    private static bool SameWordReadings(IReadOnlyDictionary<int, IReadOnlyList<LyricWord>> a,
                                         IReadOnlyDictionary<int, IReadOnlyList<LyricWord>> b) =>
        ReferenceEquals(a, b) || (a.Count == 0 && b.Count == 0);

    /// <summary>The scene's reading placement as the romanizer names it.</summary>
    protected RomanizationDisplay ReadingDisplay => SceneShown?.Style.Readings switch
    {
        ReadingPlacement.Below => RomanizationDisplay.Below,
        ReadingPlacement.Instead => RomanizationDisplay.Replace,
        _ => RomanizationDisplay.Off,
    };

    /// <summary>
    /// How line <paramref name="row"/> is set with its word-by-word readings (<see cref="SceneSnapshot.WordReadings"/>):
    /// the words shown (the readings, with "Instead"), their glue, each run's reading and a reading of the whole line.
    /// Null when the scene has none for the line (then <see cref="ReadingOf"/>'s per-line string is used).
    /// </summary>
    /// <param name="glue">How the line's own words run together, when the layout parts them more finely than the
    /// sheet does (the constellations' fragments); the sheet's glue otherwise.</param>
    protected LineReadingSet? WordReadingsOf(int row, IReadOnlyList<bool>? glue = null)
    {
        if (WordReadingList(row) is not { } readings) return null;
        var sheet = Sheet!;
        return LineReadings.For(sheet.Lines[row], readings, ReadingDisplay, glue ?? sheet.Glue[row]);
    }

    /// <summary>Line <paramref name="row"/>'s word-by-word readings as the scene has them (one per word with "Below",
    /// or the words to show "Instead"), or null when readings are off or the scene has none for the line.</summary>
    protected IReadOnlyList<LyricWord>? WordReadingList(int row)
    {
        if (SceneShown is not { } scene || Sheet is not { } sheet || row < 0 || row >= sheet.Count) return null;
        if (ReadingDisplay == RomanizationDisplay.Off || !scene.WordReadings.TryGetValue(row, out var readings) || readings.Count == 0)
            return null;
        return readings;
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
        var gap = VocalGaps.Find(sheet.VocalWindows, time, SceneShown?.Duration is double length && length > 0 ? length : double.NaN);
        var lyricOpacity = 1 - (gap?.Presence(time) ?? 0);
        var count = 0;
        // Three back: after two very short lines the one before them can still be leaving.
        for (var row = index - 3; row < index; row++)
        {
            if (!LineFlow.IsLeaving(row, index, lines, time, Rules, LeavingSpan)) continue;
            var held = Rolls(row) ? null : LineFlow.HeldShift(row, lines, time);
            if (Rules.FillsStage) held = null;
            var leaving = new DrawnLine
            {
                Row = row, Outgoing = true, Yield = LineFlow.CarriedYield(row, sheet, time, Rules), Opacity = lyricOpacity,
                Held = held, PreviewSince = double.NegativeInfinity,
            };
            if (ReduceMotion && held is { } shift)
            {
                // Reduce Motion: a held line does not travel aside; it fades out where it was as it fades in where it
                // goes (the Mac's earlierLine).
                if (shift > 0 && shift < 1)
                {
                    _drawn[count++] = leaving with { Held = 0, Opacity = lyricOpacity * (1 - shift) };
                    _drawn[count++] = leaving with { Held = 1, Opacity = lyricOpacity * shift };
                    continue;
                }
                leaving.Held = shift >= 1 ? 1 : 0;
            }
            _drawn[count++] = leaving;
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
        // Spreading setup, the lines are measured only then: a line not drawn yet (a new song's first, before it is
        // sung) is set when it is due, not on the song's first frame.
        var anyHeld = false;
        for (var k = 0; k < count; k++) anyHeld |= _drawn[k].Held is > 0;
        if (anyHeld || !_spreads)
            for (var k = 0; k < count; k++) _stacked[k] = Stacked(_drawn[k], time);
        if (anyHeld) LineStack.Offsets(_stacked.AsSpan(0, count), Stage.Height, BaseSize * 0.3, _offsets);
        else Array.Clear(_offsets);
        for (var k = 0; k < count; k++)
        {
            ref var line = ref _drawn[k];
            DrawLine(dc, line, time, _offsets[k]);
        }
        DrawCredits(dc, time);
        if (SceneShown?.Style.Interludes != false && gap is { } found && found.InterludeCue(time, Options.PreviewLead) is { } cue) DrawCue(dc, cue, InterludeYield(time));
        // The next line is set a little ahead of time, not on the frame it first appears: laid out now, within the
        // pass's allowance, and baked a slice at a time over the next frames. The next line is the first one with words
        // after the one being sung (before anything is sung, the song's first). Setting each line whole, as before, it
        // is the line after the one being sung.
        var upcoming = active is null && _spreads ? index : index + 1;
        while (_spreads && upcoming < lines.Count && lines[upcoming].Words.Count == 0 && upcoming < index + 4) upcoming++;
        if (upcoming < lines.Count && lines[upcoming].Words.Count > 0 && !Rolls(upcoming)
            && time > lines[upcoming].Start - 1.5 && !_plans.ContainsKey(upcoming) && !_phrases.ContainsKey(upcoming)
            && !StagePrepared(upcoming) && BeginAhead())
        {
            _laidOutAhead = true;
            if (Rules.FillsStage) { PrepareStage(upcoming); return; }
            _ = lines[upcoming].WordTimed ? Plan(upcoming) : Phrase(upcoming);
        }
    }

    /// <summary>
    /// Opens this frame's work ahead of time when the pass has allowance left (shared by every surface through
    /// <see cref="AheadWork"/>); true while it lasts. Without one (offscreen: previews, lyric cards, tests) work ahead of
    /// time is always allowed, as <see cref="FrameContext.AheadWork"/> says: no clock decides how much a frame does, so
    /// what an offscreen frame leaves for the next never depends on how busy the machine is. (It used to take a frame's
    /// own 2.5 ms of real time, so how much setup was left pending, and with it the state that frames and allocation
    /// counts were compared from, changed with the machine's load.)
    /// </summary>
    private bool BeginAhead()
    {
        if (_aheadWork is null)
        {
            _aheadOpen = true;
            _aheadDeadline = long.MaxValue;
            return true;
        }
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_aheadOpen) return now < _aheadDeadline;
        var remaining = _aheadWork.Open();
        if (remaining <= 0) return false;
        _aheadOpen = true;
        _aheadStart = now;
        _aheadDeadline = now + remaining;
        return true;
    }

    /// <summary>Counts this frame's work ahead of time against the pass.</summary>
    private void EndAhead()
    {
        if (!_aheadOpen) return;
        _aheadOpen = false;
        _aheadWork?.Spend(System.Diagnostics.Stopwatch.GetTimestamp() - _aheadStart);
    }

    private StackedLine Stacked(in DrawnLine line, double time)
    {
        // A stage layout sets every line in the middle of the stage; it never stacks a held line.
        if (Rules.FillsStage) return default;
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
        : new LineFit(Kind.HasConnections() ? ConstellationLayout.FitArea(Stage) : Stage, Metrics.UserScale, 0.75);

    /// <summary>The composition a backing group animates in: the lead's when it sets words loose or links them, else Editorial.</summary>
    internal static CompositionKind BackingKind(CompositionKind lead) => lead is CompositionKind.Scattered or CompositionKind.Constellation
        or CompositionKind.ConstellationEditorial or CompositionKind.Echo or CompositionKind.Editorial ? lead : CompositionKind.Editorial;

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
        if (Rules.FillsStage)
        {
            // A stage layout draws nothing for a preview (the Mac's firstRunOnly is a clear stage).
            if (!drawn.Previewing) DrawStage(dc, row, time, pose, stagePresence, opacity, drawn.Yield);
            return;
        }
        var heldScale = 1 - LineStack.HeldShrink * Math.Min(1, drawn.Held ?? 0);
        if (line.WordTimed)
        {
            var plan = Plan(row);
            if (plan is null) return;
            var exitStart = drawn.Previewing ? double.PositiveInfinity : LineFlow.StageExitStart(line, pose);
            // Reduce Motion: the line fades; it does not slide or swell.
            var still = ReduceMotion;
            var swell = still ? 1 : 0.965 + 0.035 * pose.Arrival - 0.012 * pose.Exit;
            var makesWay = drawn.Outgoing ? LineFlow.IncomingArrival(row, lines, time, Rules) : 0;
            var slide = still ? 0 : ((1 - pose.Arrival) * 0.20 - pose.Exit * 0.10 - 0.30 * makesWay) * BaseSize;
            var content = ContentToDevice(plan, offset, heldScale);
            DrawBacking(dc, plan, time, opacity, row);
            DrawWordTimed(dc, plan, time, exitStart, drawn.PreviewSince, content, plan.Fit * heldScale,
                          stagePresence * opacity, swell, slide, stagePresence * (1 - pose.Exit));
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

    /// <summary>
    /// A stage layout's line <paramref name="row"/> (Hinge, Broadside, Portal, Mosaic, Torque, Fable): the line's
    /// presence, the hand-over opacity and how far it yields to the next line's preview.
    /// </summary>
    protected virtual void DrawStage(ID2D1DeviceContext dc, int row, double time, in LyricPhraseTiming pose, double presence,
                                     double opacity, double yield) { }

    /// <summary>Sets a stage layout's line ahead of time (laid out and baked), not on the frame it first appears.</summary>
    protected virtual void PrepareStage(int row) { }

    /// <summary>True when a stage layout has already set <paramref name="row"/> (its own cache, not the plans).</summary>
    protected virtual bool StagePrepared(int row) => false;

    /// <summary>How long a line takes to leave once its exit has begun (Hinge folds for longer).</summary>
    protected virtual double LeavingSpan => LyricPhraseTiming.ExitSpan;

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
        Meter.Enter();
        try
        {
            var plan = SetPlan(row);
            plan.LastUsed = Frame;
            _plans[row] = plan;
            return plan;
        }
        finally
        {
            Meter.Exit();
        }
    }

    private LinePlan SetPlan(int row)
    {
        var line = Sheet!.Lines[row];
        IReadOnlyList<bool> lineGlue = Sheet.Glue[row];
        var connections = Kind.HasConnections() && line.WordTimed;
        // A constellation is a web of fragments: an unspaced line is parted into its phrases, or it is one fragment
        // with nothing to link. It is parted before its readings are paired with its runs, so each fragment reads its
        // own words.
        if (connections) lineGlue = LyricRuns.SplittingWords(line.Words, lineGlue, Breaker);
        // With word-by-word readings "Instead", the readings are set in the words' place.
        var set = WordReadingsOf(row, lineGlue);
        var glue = set?.Glue ?? lineGlue;
        if (connections && set is { Replaced: true }) glue = LyricRuns.SplittingWords(set.Words, glue, Breaker);
        IReadOnlyList<LyricWord> words = set?.Words ?? line.Words;
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
        var (runReadings, lineReading) = set is not null ? (set.RunReadings(words, mainGlue), set.LineReading) : Readings(row, words, mainGlue);
        // Each run's reading share by share, so it fills in as the run is sung.
        var runShares = set?.RunShares(words, mainGlue);
        // Dense web layouts set long lines smaller.
        var lineBase = connections ? BaseSize * ConstellationLayout.DensityScale(line.Words) : BaseSize;
        // "Cutout · Highlight": each sung word gets a tile, held until the line ends (see Decorate).
        return BuildPlan(words, mainGlue, lineBase, row, line.Start, wordTimed: true, editorial: Kind.HasEditorialTypography(),
                         runReadings, lineReading, FitFor(row), Stage.Width, groups, row, kind: Kind, lineEnd: line.End,
                         runShares: runShares);
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

    /// <param name="inkTime">The song time whose drift the line's colours take (its start).</param>
    /// <param name="kind">The composition the line is set in (the layer's own by default): its typography, entrances, links and echo.</param>
    /// <param name="lineEnd">The line's end, where highlight tiles release.</param>
    protected LinePlan BuildPlan(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue, double baseSize, int lineIndex,
                                 double inkTime, bool wordTimed, bool editorial, string?[]? runReadings, string? lineReading,
                                 LineFit fit, double maxWidth, IReadOnlyList<BackingGroup>? groups = null, int backingSeed = 0,
                                 bool heavy = false, CompositionKind? kind = null, double lineEnd = double.NaN,
                                 IReadOnlyList<ReadingShares?>? runShares = null)
    {
        Meter.Enter();
        try
        {
            return SetLine(words, glue, baseSize, lineIndex, inkTime, wordTimed, editorial, runReadings, lineReading, fit, maxWidth,
                           groups, backingSeed, heavy, kind, lineEnd, runShares);
        }
        finally
        {
            Meter.Exit();
        }
    }

    private LinePlan SetLine(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue, double baseSize, int lineIndex,
                             double inkTime, bool wordTimed, bool editorial, string?[]? runReadings, string? lineReading,
                             LineFit fit, double maxWidth, IReadOnlyList<BackingGroup>? groups, int backingSeed,
                             bool heavy, CompositionKind? kind, double lineEnd, IReadOnlyList<ReadingShares?>? runShares)
    {
        var ink = InkAt(inkTime);
        var planKind = kind ?? Kind;
        var connections = wordTimed && planKind.HasConnections();
        var spec = new PlanSpec(
            BaseSize: baseSize, MinimumSize: 11 * Scale, LineIndex: lineIndex, WordTimed: wordTimed, Editorial: editorial,
            Heavy: heavy, MaxWidth: maxWidth, Spacing: baseSize * ConstellationLayout.Spacing(connections),
            LineSpacing: baseSize * 0.30, Fit: fit,
            RasterBoost: wordTimed ? 1.12 : 1.0,
            Shadow: wordTimed ? new ShadowSpec(0.52f, Points(7) * 0.5f, Points(2)) : new ShadowSpec(0.32f, Points(8) * 0.5f, Points(2)),
            Colour: TextColour, ReadingColour: LyricInks.Vector(ink.Base.Reading), LineReadingSize: baseSize * 0.38,
            LineReadingWidth: Stage.Width * 0.8, LineReadingShadow: new ShadowSpec(0.3f, Points(6) * 0.5f, Points(2)), Ink: ink,
            Connections: connections, Spread: StyleShown.ConstellationSpread,
            EvenRunsAlternate: planKind == CompositionKind.ConstellationEditorial);
        var plan = LinePlans.Build(Kit!, words, glue, spec, runReadings, lineReading, runShares);
        Decorate(plan, planKind, words, glue, lineEnd, lineIndex, baseSize, Stage, ink);
        if (groups is { Count: > 0 })
        {
            var box = BackingVocalLayout.Box(Stage);
            var backingKind = BackingKind(planKind);
            var backingConnections = backingKind.HasConnections();
            var backing = new BackingPlan[groups.Count];
            for (var g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                var sub = LinePlans.Build(Kit!, group.Words, group.Glue, spec with
                {
                    BaseSize = baseSize * 0.5, LineIndex = backingSeed + group.Id,
                    Editorial = backingKind.HasEditorialTypography(), Heavy = false,
                    MaxWidth = box.Width, Spacing = baseSize * 0.5 * ConstellationLayout.Spacing(backingConnections),
                    LineSpacing = baseSize * 0.5 * 0.30, Fit = new LineFit(box, 1, 1), Connections = backingConnections,
                    EvenRunsAlternate = backingKind == CompositionKind.ConstellationEditorial,
                }, null, null);
                Decorate(sub, backingKind, group.Words, group.Glue, double.NaN, backingSeed + group.Id, baseSize * 0.5, box, ink);
                backing[g] = new BackingPlan(group, sub);
            }
            plan.Backing = backing;
        }
        return plan;
    }

    /// <summary>
    /// What a plan needs beyond its words, worked out once: each run's entrance (a mix picks per run), the highlight
    /// tiles' words, the constellation's links and Echo's phrase.
    /// </summary>
    private void Decorate(LinePlan plan, CompositionKind kind, IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue,
                          double lineEnd, int seed, double baseSize, SizeD stage, LyricInk ink)
    {
        var typography = plan.Typography;
        var runs = typography.Runs;
        var n = runs.Count;
        plan.Chosen = new WordArrival[n];
        plan.Motions = new WordArrival[n];
        plan.HeldX = new double[n];
        plan.HeldY = new double[n];
        plan.IdleFloat = kind.IdleFloat();
        var highlights = false;
        for (var r = 0; r < n; r++)
        {
            var chosen = Choice.For(kind, runs[r].Seed);
            plan.Chosen[r] = chosen;
            plan.Motions[r] = chosen.Motion(kind);
            highlights |= chosen.HighlightsSungWords();
            // A held word swells: glyph by glyph where the script allows it (each about its own foot, so the word
            // hardly widens), else the whole shaped word (joined scripts).
            if (ScriptDirection.AllowsGlyphMotion(runs[r].Label))
            {
                plan.HeldY[r] = 0.05;
                plan.HeldX[r] = 0.05 / Math.Max(1, new System.Globalization.StringInfo(runs[r].Label).LengthInTextElements);
            }
            else
            {
                plan.HeldX[r] = plan.HeldY[r] = 0.07;
            }
        }
        if (!typography.WordTimed || n == 0) return;
        var (visible, _) = LyricRuns.Visible(words, glue);
        if (highlights)
        {
            var release = LyricHighlightTiming.LineEnd(double.IsFinite(lineEnd) ? lineEnd : null, visible);
            plan.Release = release;
            plan.Segments = HighlightSegment.Of(typography, words, glue, Breaker, release);
        }
        if (kind.HasConnections() && n >= 2 && n <= ConstellationGeometry.MaxWords)
        {
            // Each run's box is its fixed slot less its inset, where the slot sits on the wave: it never changes as
            // words enter, light and swell.
            for (var r = 0; r < n; r++)
            {
                var unit = plan.Runs[r];
                var metrics = runs[r].Metrics;
                var envelope = ConstellationGeometry.WordEnvelope(unit.Unit.Width, unit.Unit.Height, 1, Math.Abs(metrics.Rotation) + 0.5);
                _linkBoxes[r] = new Box(unit.Centre.X - envelope.Width / 2, unit.Centre.Y + metrics.WaveOffset - envelope.Height / 2,
                                        envelope.Width, envelope.Height);
            }
            var count = ConstellationGeometry.BuildConnections(_linkBoxes.AsSpan(0, n), new Box(0, 0, plan.Block.Width, plan.Block.Height),
                                                               ConstellationLayout.LinkClearance * Scale, _linkScratch);
            plan.Links = _linkScratch.AsSpan(0, count).ToArray();
        }
        if (kind == CompositionKind.Echo)
        {
            var text = LyricRuns.Text(words, glue);
            if (string.IsNullOrWhiteSpace(text)) return;
            var rightToLeft = ScriptDirection.LineIsRightToLeft(visible.Select(word => word.Text));
            var largest = 0.0;
            foreach (var ghost in EchoBackdrop.Repeats) largest = Math.Max(largest, ghost.SizeShare);
            using var layout = Kit!.Layout(text, Face.Regular, (float)(baseSize * largest), TextKit.LocaleFor(text), rightToLeft);
            // The repeats take the ink's colour at the middle of the line.
            var tint = LyricInks.Vector(ink.Sample(0.5, EchoBackdrop.Magnitude(seed)).Rest);
            var raster = (float)Math.Clamp(plan.Fit, 0.2, 2);
            plan.Echo = new EchoPlan(Kit.Bake(layout, tint, raster, ShadowSpec.None), largest, rightToLeft)
            {
                BaseSize = baseSize, Stage = stage, Seed = seed,
            };
        }
    }

    /// <summary>
    /// A word-timed line set as Editorial sets it: each run lit, swelled, floated and revealed by its own timing; the
    /// whole block eased in (swell and slide), placed by <paramref name="content"/>. Echo's repeats and a
    /// constellation's links are drawn behind the words.
    /// </summary>
    /// <param name="echoPresence">How present Echo's repeats are (the line's presence less its exit).</param>
    protected void DrawWordTimed(ID2D1DeviceContext dc, LinePlan plan, double time, double exitStart, double previewSince,
                                 in Matrix3x2 content, double contentScale, double opacity, double swell, double slide,
                                 double echoPresence = 1)
    {
        var typography = plan.Typography;
        var tokens = typography.Tokens;
        if (tokens.Count == 0) return;
        int? next = null;
        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].Source.Start > time) { next = i; break; }
        var energy = OrganicMotion.VocalIntensity(plan.Words, time);
        var highlighted = plan.Segments is not null;
        var reduce = ReduceMotion;
        for (var i = 0; i < tokens.Count; i++)
        {
            var eligible = i > 0 ? tokens[i - 1].Source.Start : previewSince;
            // A word with a highlight tile stays until its tile is released at the line's end.
            var leaves = highlighted && plan.Chosen[tokens[i].Run].HighlightsSungWords() ? Math.Max(exitStart, plan.Release) : exitStart;
            plan.TokenFrames[i] = EditorialTokenFrame.Make(tokens[i], next, time, energy, leaves, reduce, eligible, Options);
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
        if (plan.Echo is { } echo) DrawEcho(dc, plan, echo, time, blockCentre, block, contentScale * swell, opacity * echoPresence);
        if (plan.Links.Length > 0) DrawLinks(dc, plan, Matrix3x2.CreateTranslation((float)origin.X, (float)origin.Y) * block, opacity);
        var idle = OrganicMotion.IdleAmplitude(plan.IdleFloat, 1, reduce);
        foreach (var r in order)
        {
            var run = runs[r];
            var ink = plan.Runs[r];
            ref readonly var frame = ref plan.RunFrames[r];
            var size = run.Metrics.Size;
            var leave = frame.Leave;
            // Reduce Motion: the exit is a plain fade, with no drift, turn or blur (the Mac's WordFloat).
            var travel = reduce ? 0 : leave;
            var presentation = frame.Presentation;
            var visibility = presentation.Opacity * (1 - leave) * opacity;
            if (!(visibility > 0.002)) continue;
            var heat = Math.Min(1, frame.Heat);
            var motion = plan.Motions[r];
            var direction = r % 2 == 0 ? 1.0 : -1.0;
            var transform = EntranceTransform.Make(presentation.Progress, presentation.Arrival, motion, size, direction, reduce);
            var drift = LyricMotion.IdleDrift(run.Seed, Math.Floor(time * 12) / 12, idle);
            var lateral = LyricMotion.Jitter(run.Seed, 12) * 0.10;
            var swellCap = EditorialRunFrame.CappedSwell(
                EditorialRunFrame.Swell(frame.Heat, frame.Glow, frame.DriftScale, reduce), run.Inset.Width, ink.Unit.Width);
            // A held word's letters swell only with motion (the Mac's HeldGlyphRenderer takes no sustain under Reduce Motion).
            var holding = !reduce && run.Sustain > 0.02 ? run.Sustain * Easing.Smoothstep(frame.Fill) : 0;
            var floatScale = drift.Scale * (1 - 0.04 * travel);
            var local =
                Matrix3x2.CreateRotation(Radians(run.Metrics.Rotation))
                * Matrix3x2.CreateScale((float)swellCap)
                * Matrix3x2.CreateRotation(Radians(reduce ? 0 : frame.DriftAngle))
                * Matrix3x2.CreateTranslation((float)(reduce ? 0 : frame.DriftX * size), (float)(reduce ? 0 : frame.DriftY * size))
                * Entrance(transform)
                * Matrix3x2.CreateScale((float)floatScale)
                * Matrix3x2.CreateRotation(Radians(drift.Angle + LyricMotion.Jitter(run.Seed, 13) * 3 * travel))
                * Matrix3x2.CreateTranslation((float)((drift.X + lateral * travel) * size), (float)((drift.Y - 0.26 * travel) * size))
                * Matrix3x2.CreateTranslation((float)(origin.X + ink.Centre.X), (float)(origin.Y + ink.Centre.Y + run.Metrics.WaveOffset))
                * block;
            var heldUp = 1 + plan.HeldY[r] * holding;
            var scale = contentScale * swell * swellCap * heldUp * transform.Scale * floatScale;
            var blur = Combine(Points(motion.Blur(presentation.Progress, reduce)), Points(2.4 * travel)) * (float)scale;
            // "Cutout · Highlight": the tiles behind this run's sung words, and how strong the strongest is.
            var tiled = 0.0;
            var segments = highlighted && plan.Chosen[r].HighlightsSungWords() ? plan.Segments![r] : null;
            if (segments is not null)
                foreach (var segment in segments)
                    tiled = Math.Max(tiled, LyricHighlightTiming.Pose(time, segment.Onset, segment.Release, reduce).Opacity);
            var glowStrength = frame.Glow * (1 - tiled);
            var glow = glowStrength > 0.002
                ? new GlowSpec(new Vector4(ink.Glow.X, ink.Glow.Y, ink.Glow.Z, (float)(0.6 * glowStrength * frame.Fill)),
                               Points(10 + 22 * frame.Glow) * (float)scale)
                : GlowSpec.None;
            var shadow = (float)((0.28 + 0.24 * heat) / 0.52);
            var tokenOpacities = frame.RevealsByToken ? plan.TokenOpacities.AsSpan(run.TokenStart, run.TokenCount) : default;
            // A held word grows about its foot: glyph by glyph it barely widens (the Mac swells each glyph in place).
            var labelHeight = ink.Label.Box.Y;
            var held = holding > 0
                ? Matrix3x2.CreateScale((float)(1 + plan.HeldX[r] * holding), (float)heldUp, new Vector2(0, labelHeight / 2))
                : Matrix3x2.Identity;
            var labelWorld = held * Matrix3x2.CreateTranslation(ink.LabelOffset) * local;
            // A cutout is unmasked from its leading edge while it arrives.
            var clip = InkClip.Leading(motion.PaperShown(transform.Eased), ink.Unit.Width, direction < 0);
            var tiles = segments is not null && tiled > 0.001;
            if (tiles) DrawRunTiles(dc, segments!, ink.Label, labelWorld, size, run.RightToLeft, time, visibility, overlay: false);
            ink.Label.Draw(dc, labelWorld, (float)scale, (float)visibility, blur, glow, shadow,
                           run.Boundaries, tokenOpacities, run.RightToLeft, clip);
            if (tiles) DrawRunTiles(dc, segments!, ink.Label, labelWorld, size, run.RightToLeft, time, visibility, overlay: true);
            if (ink.Reading is { } reading)
            {
                var runTokens = plan.TokenOpacities.AsSpan(run.TokenStart, run.TokenCount);
                var readingOpacity = frame.ReadingOpacity(runTokens);
                // A glued run's reading fills in with it: each token's share from its onset, never ahead of the
                // token itself where the run reveals token by token (the Mac's ScatteredRunFrame.readingReveal).
                var shares = default(ReadOnlySpan<double>);
                if (ink.ReadingSlices is { } slices)
                {
                    var syllables = default(ReadOnlySpan<double>);
                    if (frame.RevealsByToken && runTokens.Length == slices.Owners.Length)
                    {
                        ReadingShares.RelativeToFirst(runTokens, slices.Syllables);
                        syllables = slices.Syllables;
                    }
                    shares = slices.Opacities(time, syllables, reduce);
                }
                reading.Draw(dc, Matrix3x2.CreateTranslation(ink.ReadingOffset) * local, (float)scale,
                             (float)(visibility * readingOpacity), blur, GlowSpec.None, shadow,
                             ink.ReadingSlices?.Boundaries, shares, clip: clip);
            }
            if (LyricSwipe.Shows(motion, presentation.Progress, reduce))
                DrawSwipe(dc, local, ink.Unit, presentation.Progress, direction, motion, opacity);
        }
    }

    /// <summary>
    /// Echo's repeats of the phrase behind the live line: faint, at many sizes, two mirrored, each wandering slowly
    /// and revealed with the line.
    /// </summary>
    private void DrawEcho(ID2D1DeviceContext dc, LinePlan plan, EchoPlan echo, double time, Vector2 blockCentre,
                          in Matrix3x2 block, double blockScale, double presence)
    {
        if (!(presence > 0.002)) return;
        var phrase = echo.Phrase;
        var revealed = Options.RevealsWords ? WordReveal.LineRevealed(plan.Words, time) : 1;
        if (!(revealed > 0)) return;
        for (var slot = 0; slot < EchoBackdrop.Repeats.Count; slot++)
        {
            var pose = EchoBackdrop.Pose(slot, echo.Seed, time, echo.BaseSize, ReduceMotion);
            var ghost = pose.Repeat;
            var alpha = EchoBackdrop.Opacity(ghost, presence, StyleShown.EchoStrength);
            if (!(alpha > 0.002)) continue;
            var relative = ghost.SizeShare / echo.BakedScale;
            var size = new SizeD(phrase.Box.X * relative, phrase.Box.Y * relative);
            var fit = EchoBackdrop.Fit(size, echo.Stage, pose);
            if (!(fit > 0)) continue;
            var k = (float)(relative * fit);
            var world = Matrix3x2.CreateScale(k)
                * Matrix3x2.CreateRotation(Radians(pose.Angle))
                * Matrix3x2.CreateScale(1, ghost.Mirrored ? -1 : 1)
                * Matrix3x2.CreateTranslation((float)pose.X, (float)pose.Y)
                * Matrix3x2.CreateTranslation(blockCentre) * block;
            DrawSwept(dc, phrase, world, (float)(k * blockScale), (float)alpha, revealed, EchoBackdrop.Feather, echo.RightToLeft);
        }
    }

    /// <summary>
    /// Text revealed through a soft sweep in reading order (the Mac's <c>LyricSweepMask</c>): solid up to the sweep, then
    /// a feathered edge drawn as a few strips of falling opacity.
    /// </summary>
    private static void DrawSwept(ID2D1DeviceContext dc, Ink ink, in Matrix3x2 world, float worldScale, float opacity,
                                  double progress, double feather, bool rightToLeft)
    {
        var (solid, clear) = LyricSweep.Edges(progress, feather);
        if (solid >= 1)
        {
            ink.Draw(dc, world, worldScale, opacity, 0, GlowSpec.None, 0);
            return;
        }
        var width = ink.Box.X;
        if (solid > 0) ink.Draw(dc, world, worldScale, opacity, 0, GlowSpec.None, 0, clip: SweepSpan(-1, solid, width, rightToLeft));
        const int strips = 6;
        var step = (clear - solid) / strips;
        if (!(step > 0)) return;
        for (var s = 0; s < strips; s++)
        {
            var from = solid + s * step;
            var alpha = LyricSweep.Alpha(from + step / 2, progress, feather);
            if (alpha > 0.002)
                ink.Draw(dc, world, worldScale, (float)(opacity * alpha), 0, GlowSpec.None, 0,
                         clip: SweepSpan(from, from + step, width, rightToLeft));
        }
    }

    /// <summary>A stretch of a label between two reading-order positions (0…1), as a clip about the label's centre.</summary>
    private static InkClip SweepSpan(double from, double to, float width, bool rightToLeft)
    {
        float X(double along) => (float)(rightToLeft ? width / 2 - along * width : -width / 2 + along * width);
        return rightToLeft ? new InkClip(X(to), X(from)) : new InkClip(X(from), X(to));
    }

    /// <summary>A constellation's hairlines, each with a dot at both ends, lit with the heat of the words they join.</summary>
    private void DrawLinks(ID2D1DeviceContext dc, LinePlan plan, in Matrix3x2 blockToDevice, double opacity)
    {
        var links = StyleShown.ConstellationLinkOpacity;
        if (_paint is null || !(links > 0)) return;
        var link = SettledInk.Link;
        dc.Transform = blockToDevice;
        var width = Points(ConstellationLayout.LinkWidth);
        var dot = Points(ConstellationLayout.DotRadius);
        var tokens = plan.TokenFrames;
        var runs = plan.Typography.Runs;
        foreach (var edge in plan.Links)
        {
            ref readonly var a = ref plan.RunFrames[edge.From];
            ref readonly var b = ref plan.RunFrames[edge.To];
            var shownA = a.Visible * (1 - tokens[runs[edge.From].TokenStart].Leave);
            var shownB = b.Visible * (1 - tokens[runs[edge.To].TokenStart].Leave);
            var alpha = ConstellationLayout.LinkAlpha(links, Math.Max(a.Heat, b.Heat),
                                                      Math.Min(shownA, shownB)) * opacity * link.A;
            if (!(alpha > 0.002)) continue;
            _paint.Color = new Color4((float)link.R, (float)link.G, (float)link.B, (float)alpha);
            var from = new Vector2((float)edge.Ax, (float)edge.Ay);
            var to = new Vector2((float)edge.Bx, (float)edge.By);
            dc.DrawLine(from, to, _paint, width, _roundCaps);
            dc.FillEllipse(new Ellipse(from, dot, dot), _paint);
            dc.FillEllipse(new Ellipse(to, dot, dot), _paint);
        }
    }

    /// <summary>
    /// Pushes a layer for a piece drawn as one (a Cutout label): at <paramref name="opacity"/>, within
    /// <paramref name="bounds"/> (in <paramref name="world"/>'s units about its origin) and, when <paramref name="clip"/>
    /// is set, cut to its stretch across. Pop it with <c>PopLayer</c>.
    /// </summary>
    protected void PushPieceLayer(ID2D1DeviceContext dc, in Matrix3x2 world, Vortice.RawRectF bounds, float opacity, InkClip clip)
    {
        dc.Transform = world;
        var layer = new LayerParameters1
        {
            ContentBounds = bounds,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = opacity,
            LayerOptions = LayerOptions1.None,
        };
        if (clip.IsSet && _unitSquare is not null)
        {
            var left = MathF.Max(clip.Left, bounds.Left);
            var right = MathF.Max(left, MathF.Min(clip.Right, bounds.Right));
            layer.GeometricMask = _unitSquare;
            layer.MaskTransform = Matrix3x2.CreateScale(right - left, bounds.Bottom - bounds.Top)
                * Matrix3x2.CreateTranslation(left, bounds.Top);
        }
        dc.PushLayer(ref layer, null);
    }

    /// <summary>
    /// Satin Ribbon or Split Ink over a word (or a phrase) while it arrives, clipped to its box of size
    /// <paramref name="box"/> centred on the origin of <paramref name="boxWorld"/>.
    /// </summary>
    protected void DrawSwipe(ID2D1DeviceContext dc, in Matrix3x2 boxWorld, SizeD box, double progress, double direction,
                           WordArrival style, double opacity)
    {
        if (_paint is null || _unitSquare is null) return;
        var alpha = (float)(LyricSwipe.Opacity(progress) * opacity);
        if (!(alpha > 0.002f) || !(box.Width > 0) || !(box.Height > 0)) return;
        float w = (float)box.Width, h = (float)box.Height;
        // Box coordinates: top-left origin.
        var toBox = Matrix3x2.CreateTranslation(-w / 2, -h / 2) * boxWorld;
        dc.Transform = toBox;
        var layer = new LayerParameters1
        {
            ContentBounds = new Vortice.RawRectF(0, 0, w, h),
            GeometricMask = _unitSquare,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.CreateScale(w, h),
            Opacity = 1,
            LayerOptions = LayerOptions1.None,
        };
        dc.PushLayer(ref layer, null);
        if (style == WordArrival.SatinRibbon)
        {
            var ribbon = Ribbon();
            var bar = LyricSwipe.Ribbon(progress, direction, w, h);
            float bw = (float)bar.Width, bh = (float)bar.Height;
            dc.Transform = Matrix3x2.CreateRotation(Radians(bar.Turn), new Vector2(bw / 2, bh / 2))
                * Matrix3x2.CreateTranslation((float)bar.X, (float)bar.Y) * toBox;
            if (ribbon is not null)
            {
                ribbon.StartPoint = new Vector2(0, 0);
                ribbon.EndPoint = new Vector2(bw, 0);
                ribbon.Opacity = alpha;
                dc.FillRectangle(new Vortice.RawRectF(0, 0, bw, bh), ribbon);
            }
        }
        else
        {
            var (top, edge, bottom) = LyricSwipe.Split(progress, direction, w, h);
            Bar(dc, top, _swipe.Ink, 0.68f * alpha);
            Bar(dc, edge, _swipe.Highlight, 0.88f * alpha);
            Bar(dc, bottom, _swipe.Ink, 0.52f * alpha);
        }
        dc.PopLayer();
    }

    private void Bar(ID2D1DeviceContext dc, SwipeBar bar, Double3 colour, float alpha)
    {
        _paint!.Color = new Color4((float)colour.X, (float)colour.Y, (float)colour.Z, alpha);
        dc.FillRectangle(new Vortice.RawRectF((float)bar.X, (float)bar.Y, (float)(bar.X + bar.Width), (float)(bar.Y + bar.Height)), _paint);
    }

    /// <summary>Satin Ribbon's band: clear, the ribbon colour at 23%, the highlight at 65%, clear; made when first needed.</summary>
    private ID2D1LinearGradientBrush? Ribbon()
    {
        if (_ribbon is not null || Kit is null) return _ribbon;
        var ribbon = _swipe.Ribbon;
        var highlight = _swipe.Highlight;
        _ribbonStops = Kit.Draw.CreateGradientStopCollection(
        [
            new GradientStop(0, new Color4((float)ribbon.X, (float)ribbon.Y, (float)ribbon.Z, 0)),
            new GradientStop(1f / 3, new Color4((float)ribbon.X, (float)ribbon.Y, (float)ribbon.Z, 0.23f)),
            new GradientStop(2f / 3, new Color4((float)highlight.X, (float)highlight.Y, (float)highlight.Z, 0.65f)),
            new GradientStop(1, new Color4((float)highlight.X, (float)highlight.Y, (float)highlight.Z, 0)),
        ]);
        _ribbon = Kit.Draw.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(0, 0), new Vector2(1, 0)), _ribbonStops);
        return _ribbon;
    }

    private void DropRibbon()
    {
        _ribbon?.Dispose();
        _ribbon = null;
        _ribbonStops?.Dispose();
        _ribbonStops = null;
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
        // Reduce Motion: backing vocals neither drift nor sink as they leave; they only fade (the Mac's layerExit).
        var (sink, shrink) = ReduceMotion ? (0.0, 1.0) : BackingVocalLayout.Exit(BackingVocalLayout.ExitProgress(group, time), box.Height);
        var position = BackingVocalLayout.Position(group, seed, Stage, BaseSize, time, ReduceMotion, CreditBands);
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
        Meter.Enter();
        try
        {
            var line = Sheet!.Lines[row];
            var set = WordReadingsOf(row);
            var glue = set?.Glue ?? Sheet.Glue[row];
            var words = set?.Words ?? line.Words;
            var (runReadings, lineReading) = set is not null ? (set.RunReadings(words, glue), set.LineReading) : Readings(row, words, glue);
            var phraseBase = BaseSize * (Kind == CompositionKind.Spoken ? 1.26 : 1.06);
            var plan = BuildPlan(words, glue, phraseBase, row, line.Start, wordTimed: false, editorial: false, runReadings, lineReading,
                                 FitFor(row), Stage.Width);
            plan.LastUsed = Frame;
            _phrases[row] = plan;
            return plan;
        }
        finally
        {
            Meter.Exit();
        }
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
        // A line sung as one has no words to highlight one by one: it enters as its layout has it enter.
        var motion = Choice.For(Kind, TimedWordKey.SeedOf(line.Text, line.Start)).Motion(Kind);
        var direction = row % 2 == 0 ? 1.0 : -1.0;
        var transform = EntranceTransform.Make(progress, previewing ? 1 : pose.Arrival, motion, pointSize, direction, ReduceMotion);
        var phrase = Matrix3x2.CreateTranslation(-(float)(plan.Content.Width / 2), -(float)(plan.Content.Height / 2))
            * Entrance(transform)
            * Matrix3x2.CreateTranslation((float)(plan.Content.Width / 2), (float)(plan.Content.Height / 2)) * content;
        var entranceScale = scale * transform.Scale;
        var blur = Points(motion.Blur(progress, ReduceMotion)) * (float)entranceScale;
        // A cutout phrase is unmasked from its leading edge (in the block's own width).
        var shown = motion.PaperShown(transform.Eased);
        var width = plan.Block.Width;
        var (cropLeft, cropRight) = shown >= 1 ? (double.NegativeInfinity, double.PositiveInfinity)
            : direction >= 0 ? (double.NegativeInfinity, shown * width) : (width - shown * width, double.PositiveInfinity);
        var alpha = previewing ? 1 : visibility;
        // The line itself (in preview: only its first word, where it will be).
        DrawPhraseRuns(dc, plan, phrase, entranceScale, alpha * opacity, blur, firstRunOnly: previewing ? previewOpacity : null,
                       hidesFirstRun: holdsFirstRun, readings: !previewing, cropLeft, cropRight);
        if (!previewing && LyricSwipe.Shows(motion, progress, ReduceMotion))
        {
            var origin = plan.BlockOrigin;
            DrawSwipe(dc, Matrix3x2.CreateTranslation((float)(origin.X + width / 2), (float)(origin.Y + plan.Block.Height / 2)) * phrase,
                      plan.Block, progress, direction, motion, opacity);
        }
        if (holdsFirstRun)
        {
            // The previewed first word stays where the preview showed it while the rest makes its entrance.
            DrawPhraseRuns(dc, plan, content, scale, opacity, 0,
                           firstRunOnly: LineFlow.HeldFirstRunOpacity(pose.Presence) * (1 - 0.5 * drawn.Yield),
                           hidesFirstRun: false, readings: false, double.NegativeInfinity, double.PositiveInfinity);
        }
        if (!previewing) DrawLineReading(dc, plan, content, scale, pose.Progress * (1 - pose.Exit) * opacity);
    }

    private static void DrawPhraseRuns(ID2D1DeviceContext dc, LinePlan plan, in Matrix3x2 content, double scale, double opacity,
                                       float blur, double? firstRunOnly, bool hidesFirstRun, bool readings,
                                       double cropLeft, double cropRight)
    {
        var origin = plan.BlockOrigin;
        for (var r = 0; r < plan.Runs.Length; r++)
        {
            var alpha = firstRunOnly is { } only ? (r == 0 ? only : 0) : hidesFirstRun && r == 0 ? 0 : 1;
            alpha *= opacity;
            if (!(alpha > 0.002)) continue;
            var ink = plan.Runs[r];
            // The crop is in block coordinates; each label is centred on its slot.
            var clip = double.IsNegativeInfinity(cropLeft) && double.IsPositiveInfinity(cropRight) ? InkClip.None
                : new InkClip((float)(cropLeft - ink.Centre.X), (float)(cropRight - ink.Centre.X));
            var local = Matrix3x2.CreateTranslation((float)(origin.X + ink.Centre.X), (float)(origin.Y + ink.Centre.Y)) * content;
            ink.Label.Draw(dc, Matrix3x2.CreateTranslation(ink.LabelOffset) * local, (float)scale, (float)alpha, blur, GlowSpec.None, 1, clip: clip);
            if (readings && firstRunOnly is null && ink.Reading is { } reading)
                reading.Draw(dc, Matrix3x2.CreateTranslation(ink.ReadingOffset) * local, (float)scale, (float)alpha, blur, GlowSpec.None, 1, clip: clip);
        }
    }

    // ---- Instrumental breaks, plain lyrics, status -----------------------------------------------------------------

    /// <summary>The three interlude dots of an instrumental break, in the album's accent.</summary>
    private void DrawCue(ID2D1DeviceContext dc, InstrumentalCue cue, double yield = 1)
    {
        // Dots not there yet draw nothing (and need no ink: a song's opening frames do not wait for it).
        if (_dotBrush is null || SceneShown is null || yield <= 0.002 || !(cue.Opacity > 0)) return;
        // The ink's dot colour (made readable against the backdrop), as the Mac colours its interlude dots.
        var accent = SettledInk.Dot is { } dot ? LyricInks.Vector(dot) : TextColour;
        var diameter = (float)(Math.Min(40, Math.Max(15, BaseSize / Scale * 0.3)) * Scale);
        var spacing = diameter * 0.65f;
        var width = diameter * 3 + spacing * 2;
        // The wander grows in with the cue, so the dots arrive still; under Reduce Motion they never wander.
        var wander = ReduceMotion ? 0 : cue.Opacity;
        dc.Transform = Matrix3x2.Identity;
        for (var i = 0; i < 3; i++)
        {
            var highlight = cue.Highlight(i);
            var radius = diameter / 2 * (float)(1 + wander * cue.Breath(i) * 0.09);
            var x = StageCentre.X - width / 2 + diameter / 2 + i * (diameter + spacing);
            var y = StageCentre.Y + (float)(wander * cue.Bob(i)) * diameter * 0.42f;
            _dotBrush.Color = new Color4(accent.X, accent.Y, accent.Z, (float)(yield * cue.Opacity * (0.32 + highlight * 0.62)));
            dc.FillEllipse(new Ellipse(new Vector2(x, y), radius, radius), _dotBrush);
            // A lit dot carries a faint white ring (the Mac's strokeBorder, 1 point inside the edge).
            if (highlight > 0.01)
            {
                var stroke = Points(1);
                _dotBrush.Color = new Color4(1, 1, 1, (float)(yield * cue.Opacity * highlight * 0.22));
                dc.DrawEllipse(new Ellipse(new Vector2(x, y), radius - stroke / 2, radius - stroke / 2), _dotBrush, stroke);
            }
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

    /// <summary>
    /// The calm status text ("No lyrics found"), easing in when it changes; with <paramref name="brief"/>, held for a
    /// few seconds and then faded away (<see cref="LyricStagePresence.Notice"/>).
    /// </summary>
    private void DrawStatus(ID2D1DeviceContext dc, string status, double wall, bool brief = false)
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
        if (brief && double.IsFinite(_statusSince))
        {
            // The clock keeps running through the hold, or a still background would never draw the fade.
            presence *= LyricStagePresence.Notice(_statusSince, wall, WallpaperMotion.NoticeHold, WallpaperMotion.NoticeFade);
            IsAnimating |= wall < _statusSince + WallpaperMotion.NoticeHold + WallpaperMotion.NoticeFade + 0.05;
        }
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
