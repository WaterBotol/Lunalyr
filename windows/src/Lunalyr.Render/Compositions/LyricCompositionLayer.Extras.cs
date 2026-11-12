// The wallpaper extras around the lyric, shared by every composition: the song's credits (the Mac's CreditsLayer,
// Lunalyr's own, ported) and the "Cutout · Highlight" tiles pressed behind sung words (LyricHighlightTile.swift,
// Lunalyr's own, ported), in the colour the highlight setting picks.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Render.Scene;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Lunalyr.Render.Compositions;

internal abstract partial class LyricCompositionLayer
{
    private ID2D1SolidColorBrush? _tileBrush;
    private readonly HighlightMark[] _marks = new HighlightMark[32];
    private IReadOnlyList<CreditCue> _cues = [];
    private CreditBand[] _creditBands = [];
    private readonly Dictionary<int, LinePlan> _creditPlans = [];
    private int _creditSeed;

    /// <summary>The scene's reveal presses a tile behind each sung word ("cutoutHighlight").</summary>
    protected bool HighlightsSungWords => HighlightTiming.HighlightsSungWords(SceneShown?.Style.Reveal);

    /// <summary>The bands the credit cues hold, and when: backing vocals keep to the other band meanwhile.</summary>
    protected ReadOnlySpan<CreditBand> CreditBands => _creditBands;

    /// <summary>The credit cues of the scene's song (empty when credits are off).</summary>
    protected IReadOnlyList<CreditCue> Cues => _cues;

    private void CreateExtras(ID2D1DeviceContext dc) => _tileBrush = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));

    private void ReleaseExtras()
    {
        _tileBrush?.Dispose();
        _tileBrush = null;
    }

    private void InvalidateExtras()
    {
        foreach (var plan in _creditPlans.Values) plan.Dispose();
        _creditPlans.Clear();
        _cues = [];
        _creditBands = [];
    }

    /// <summary>The song's credit cues, worked out once per scene (they depend on its lines, title and artist).</summary>
    private void SyncExtras(SceneSnapshot scene, IReadOnlyList<LyricLine>? lines)
    {
        if (lines is null || !scene.Style.Credits) return;
        _cues = CreditCues.Cues(lines, scene.Title, scene.Artist, EstimatedWords.Distributor(Breaker));
        _creditBands = CreditCues.Bands(_cues);
        _creditSeed = (int)(RollingWords.Fnv1a((scene.Title ?? "") + "\u001f" + (scene.Artist ?? "")) & 0x3FFF_FFFF);
    }

    // ---- Highlight tiles ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Marks <paramref name="plan"/>'s runs for highlight tiles: every run sung from <paramref name="from"/> on gets a
    /// tile behind each word a reader takes in, pressed in as it is sung and held until <paramref name="release"/>.
    /// </summary>
    protected void MarkHighlights(LinePlan plan, double from, double release)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var runs = plan.Typography.Runs;
        var tokens = plan.Typography.Tokens;
        var segments = new HighlightSegment[runs.Count][];
        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            var words = new LyricWord[run.TokenCount];
            for (var i = 0; i < words.Length; i++) words[i] = tokens[run.TokenStart + i].Source;
            segments[r] = HighlightSegments.For(words, run.Boundaries, Breaker);
        }
        plan.HighlightSegments = segments;
        plan.HighlightFrom = from;
        plan.HighlightRelease = release;
    }

    /// <summary>The tiles of run <paramref name="r"/> of <paramref name="plan"/>: behind its label, or (with
    /// <paramref name="overlay"/>) the label's ink in the tile's text colour where the tiles have pressed in.</summary>
    private void DrawRunTiles(ID2D1DeviceContext dc, LinePlan plan, int r, Ink label, in Matrix3x2 labelWorld,
                              float visibility, double time, bool overlay)
    {
        if (plan.HighlightSegments is not { } all || r >= all.Length) return;
        var run = plan.Typography.Runs[r];
        if (run.Source.Start < plan.HighlightFrom) return;
        var count = HighlightSegments.Marks(all[r], time, plan.HighlightRelease, false, _marks);
        for (var k = 0; k < count; k++)
            DrawTile(dc, label, labelWorld, run.Metrics.Size, run.RightToLeft, _marks[k], visibility, overlay);
    }

    /// <summary>
    /// One highlight tile on <paramref name="label"/> (drawn with <paramref name="labelWorld"/>, origin at the label's
    /// centre): flat paper with slightly rounded corners and a small tilt, pressed in from the word's leading edge
    /// (<paramref name="overlay"/> false, drawn before the label); or the label's ink in the colour set on the tile,
    /// where the press has reached, while the tile is strong enough to carry it (drawn after the label).
    /// </summary>
    protected void DrawTile(ID2D1DeviceContext dc, Ink label, in Matrix3x2 labelWorld, double pointSize, bool rightToLeft,
                            HighlightMark mark, float visibility, bool overlay)
    {
        if (_tileBrush is null || !(visibility > 0.002f)) return;
        var ink = _ink;
        double width = label.Box.X, height = label.Box.Y;
        var rect = mark.Pressed(width, height, pointSize, rightToLeft);
        if (!(rect.Width > 0.01)) return;
        var toCentre = Matrix3x2.CreateTranslation(-(float)(width / 2), -(float)(height / 2));
        if (!overlay)
        {
            var tile = LyricInks.Vector(ink.TileColour);
            var centre = new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2));
            dc.Transform = Matrix3x2.CreateTranslation(-centre) * Matrix3x2.CreateRotation(Radians(HighlightTiming.Tilt))
                * Matrix3x2.CreateTranslation(centre) * toCentre * labelWorld;
            _tileBrush.Color = new Color4(tile.X, tile.Y, tile.Z, (float)(mark.Pose.Opacity * visibility));
            var radius = Points(HighlightTiming.CornerRadius);
            dc.FillRoundedRectangle(new RoundedRectangle(
                new RawRectF((float)rect.X, (float)rect.Y, (float)(rect.X + rect.Width), (float)(rect.Y + rect.Height)), radius, radius),
                _tileBrush);
            return;
        }
        var alpha = HighlightTiming.TextOpacity(mark.Pose.Opacity, ink.TileTextSwitch) * visibility;
        if (!(alpha > 0.002)) return;
        var text = LyricInks.Vector(ink.TileText);
        _tileBrush.Color = new Color4(text.X, text.Y, text.Z, (float)alpha);
        // In the label bitmap's pixels: its ink as a mask, filled with the tile's text colour.
        var raster = label.Raster;
        var left = Math.Clamp(label.Centre.X + (float)(rect.X - width / 2) * raster, 0, label.PixelWidth);
        var right = Math.Clamp(label.Centre.X + (float)(rect.X + rect.Width - width / 2) * raster, 0, label.PixelWidth);
        if (!(right > left)) return;
        var source = new RawRectF(left, 0, right, label.PixelHeight);
        dc.Transform = Matrix3x2.CreateTranslation(-label.Centre) * Matrix3x2.CreateScale(1 / raster) * labelWorld;
        var antialias = dc.AntialiasMode;
        dc.AntialiasMode = AntialiasMode.Aliased;
        dc.FillOpacityMask(label.Text, _tileBrush, source, source);
        dc.AntialiasMode = antialias;
    }

    // ---- Credits ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The song's credits, over the lyric: each cue spelled out word by word in Editorial's typography at most of the
    /// lead's size and its full brightness, in a thin band along the top or foot of the stage, clear of the middle
    /// where the lyric sits; a production credit lifts away toward its edge, the opening title blooms.
    /// </summary>
    private void DrawCredits(ID2D1DeviceContext dc, double time)
    {
        if (_cues.Count == 0) return;
        var box = new SizeD(Stage.Width * 0.7, Stage.Height * 0.16);
        var origin = StageOrigin;
        foreach (var cue in _cues)
        {
            var level = CreditCues.Level(cue, time);
            if (!(level > 0.004)) continue;
            var plan = CreditPlan(cue, box);
            var exit = LayerExit.Pose(cue.Title is not null ? LayerExitKind.Title : LayerExitKind.Credit,
                                      CreditCues.ExitProgress(cue, time), box.Height, cue.Top);
            var scale = plan.Fit * exit.Scale;
            var centre = new Vector2(origin.X + (float)(Stage.Width * 0.5),
                                     origin.Y + (float)(Stage.Height * (cue.Top ? 0.1 : 0.9) + exit.Offset));
            var content = Matrix3x2.CreateTranslation(-(float)(plan.Content.Width / 2), -(float)(plan.Content.Height / 2))
                * Matrix3x2.CreateScale((float)scale) * Matrix3x2.CreateTranslation(centre);
            DrawWordTimed(dc, plan, time, cue.End, double.NegativeInfinity, content, scale, level, 1, 0);
        }
    }

    private LinePlan CreditPlan(CreditCue cue, SizeD box)
    {
        if (_creditPlans.TryGetValue(cue.Id, out var found)) return found;
        // Credits are not sung: "Song (Remix)" keeps its brackets in the line rather than losing them to backing vocals.
        var glue = new bool[cue.Words.Count];
        var plan = BuildPlan(cue.Words, glue, BaseSize * 0.72, _creditSeed + cue.Id, cue.Start, wordTimed: true,
                             editorial: true, null, null, new LineFit(box, 1, 1), box.Width);
        if (cue.HighlightFrom is { } from) MarkHighlights(plan, from, cue.End);
        _creditPlans[cue.Id] = plan;
        return plan;
    }
}
