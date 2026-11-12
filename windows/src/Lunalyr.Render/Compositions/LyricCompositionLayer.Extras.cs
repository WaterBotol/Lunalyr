// The wallpaper extras around the lyric, shared by every composition: the song's credits (the Mac's CreditsLayer,
// Lunalyr's own, ported) and the "Cutout · Highlight" tiles pressed behind sung words (LyricHighlightTile.swift,
// Lunalyr's own, ported), in the colour the highlight setting picks.

using System.Numerics;
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
    private readonly HighlightMark[] _marks = new HighlightMark[32];
    private IReadOnlyList<CreditCue> _cues = [];
    private CreditBand[] _creditBands = [];
    private readonly Dictionary<int, LinePlan> _creditPlans = [];
    private int _creditSeed;

    /// <summary>The bands the credit cues hold, and when: backing vocals keep to the other band meanwhile.</summary>
    protected ReadOnlySpan<CreditBand> CreditBands => _creditBands;

    /// <summary>The credit cues of the scene's song (empty when credits are off).</summary>
    protected IReadOnlyList<CreditCue> Cues => _cues;

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
    /// The tiles of one run's sung words (<paramref name="segments"/>) on its <paramref name="label"/>: behind it, or
    /// (with <paramref name="overlay"/>) the label's ink in the tile's text colour where the tiles have pressed in.
    /// </summary>
    private void DrawRunTiles(ID2D1DeviceContext dc, ReadOnlySpan<HighlightSegment> segments, Ink label, in Matrix3x2 labelWorld,
                              double pointSize, bool rightToLeft, double time, double visibility, bool overlay)
    {
        var count = HighlightSegment.Marks(segments, time, false, _marks);
        for (var k = 0; k < count; k++) DrawTile(dc, label, labelWorld, pointSize, rightToLeft, _marks[k], visibility, overlay);
    }

    /// <summary>
    /// One highlight tile on <paramref name="label"/> (drawn with <paramref name="labelWorld"/>, origin at the label's
    /// centre), the one way every layer draws them (the Mac's <c>CutoutTile</c> and <c>LyricHighlightMask</c>). Without
    /// <paramref name="overlay"/>, drawn before the label: flat paper with slightly rounded corners, pressed in from the
    /// word's leading edge and then given Cutout's small tilt about its centre. With it, drawn after the label: the
    /// word in the colour set on the tile where the press has reached, while the tile is strong enough to carry it.
    /// </summary>
    protected void DrawTile(ID2D1DeviceContext dc, Ink label, in Matrix3x2 labelWorld, double pointSize, bool rightToLeft,
                            HighlightMark mark, double visibility, bool overlay)
    {
        if (_paint is null || !mark.Pose.IsVisible || !(visibility > 0.002)) return;
        double width = label.Box.X, height = label.Box.Y;
        if (!overlay)
        {
            var rect = mark.Rect(width, height, pointSize, rightToLeft);
            var tile = _ink.TileColour;
            _paint.Color = new Color4((float)tile.R, (float)tile.G, (float)tile.B, (float)(tile.A * mark.Pose.Opacity * visibility));
            float w = (float)rect.Width, h = (float)rect.Height;
            dc.Transform = Matrix3x2.CreateScale((float)mark.Pose.Width, 1, new Vector2(rightToLeft ? w : 0, 0))
                * Matrix3x2.CreateRotation(Radians(CutoutTile.Tilt), new Vector2(w / 2, h / 2))
                * Matrix3x2.CreateTranslation((float)(rect.X - width / 2), (float)(rect.Y - height / 2))
                * labelWorld;
            var radius = Points(CutoutTile.CornerRadius);
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(0, 0, w, h), radius, radius), _paint);
            return;
        }
        var shown = LyricHighlightTiming.TextOpacity(mark.Pose.Opacity, _ink.TileTextSwitch) * visibility;
        if (!(shown > 0.002)) return;
        var pressed = mark.Pressed(width, height, pointSize, rightToLeft);
        var colour = _ink.TileText;
        label.DrawTinted(dc, labelWorld, new Vector4((float)colour.R, (float)colour.G, (float)colour.B, (float)(colour.A * shown)),
                         new InkClip((float)(pressed.X - width / 2), (float)(pressed.X + pressed.Width - width / 2)));
    }

    // ---- Credits ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The song's credits, over the lyric: each cue spelled out word by word in the backing vocals' typography
    /// (<see cref="BackingKind"/>: Editorial's, or the Scattered family's own) at most of the lead's size and its full brightness, in a thin band along the top or foot of the stage, clear of the middle
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
        // They are set as backing vocals are (the lead's composition when it sets words loose or links them, else
        // Editorial), and the artist's words, from HighlightFrom on, are pressed onto highlight tiles.
        var glue = new bool[cue.Words.Count];
        var kind = BackingKind(Kind);
        var plan = BuildPlan(cue.Words, glue, BaseSize * 0.72, _creditSeed + cue.Id, cue.Start, wordTimed: true,
                             editorial: kind.HasEditorialTypography(), null, null, new LineFit(box, 1, 1), box.Width,
                             kind: kind, lineEnd: cue.End, highlightFrom: cue.HighlightFrom);
        _creditPlans[cue.Id] = plan;
        return plan;
    }
}
