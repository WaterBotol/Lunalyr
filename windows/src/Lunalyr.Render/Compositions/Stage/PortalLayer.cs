// Portal (the Mac's StageLyricView.portal and SketchyOutline in StageLyricStyles.swift; Lunalyr's own code, ported as
// it is). The word being sung sits in a solid pill with a small badge, tilted as it arrives and settling flat; the words
// before it drift away up a trail as smaller, paler pills. Every line leans its own way, and every word comes from its
// own direction with its own tilt, shape and badge. The badges are the Mac's SF Symbols drawn as simple shapes.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Vortice.Direct2D1;

namespace Lunalyr.Render.Compositions.Stage;

internal sealed class PortalLayer(IWordBreaker breaker, Logger? logger) : StageLayer(CompositionKind.Portal, breaker, logger)
{
    private ID2D1PathGeometry? _play;
    private ID2D1PathGeometry? _bolt;

    private sealed class PortalLine(StageSource source) : StageLine(source)
    {
        public double[] Size { get; } = new double[Math.Max(1, source.Runs.Count)];
        public Vector4[] Rest { get; } = new Vector4[Math.Max(1, source.Runs.Count)];
    }

    protected override void CreateShapes(ID2D1DeviceContext dc, ID2D1Factory factory)
    {
        _play = Polygon(factory, [new(-0.55, -0.75), new(0.8, 0), new(-0.55, 0.75)]);
        _bolt = Polygon(factory, [new(0.2, -1), new(-0.6, 0.15), new(-0.05, 0.15), new(-0.25, 1), new(0.6, -0.2), new(0.05, -0.2)]);
    }

    protected override void DisposeOwnShapes()
    {
        _play?.Dispose(); _play = null;
        _bolt?.Dispose(); _bolt = null;
    }

    private static bool Outlined(long seed, int id) => StageShared.Chance(seed, id, 12) < 0.25;

    protected override StageLine Build(StageSource source)
    {
        var line = new PortalLine(source);
        var u = Unit;
        var tile = Tile(source.InkTime);
        var tileText = TileText(source.InkTime);
        var words = new StageWord[source.Runs.Count];
        for (var i = 0; i < words.Length; i++)
        {
            var run = source.Runs[i];
            var sample = Sample(line, run);
            // Set at its resting size; older pills are scaled down, not re-set.
            var size = u * (1.2 + 0.3 * StageShared.Chance(line.Seed, run.Id, 10));
            var outlined = Outlined(line.Seed, run.Id);
            var live = outlined ? tile : tileText;
            var spec = WithReading(new WordSpec(size, [Colour(sample.Rest, 0.85), live]) { MaxWidth = Stage.Width * 0.7, Raster = 1.2f },
                                   source.Readings[i], size, [Colour(sample.Reading), Faded(live, LyricHighlight.ReadingOpacity)],
                                   shadow: false, maxWidth: Stage.Width * 0.7);
            words[i] = Bake(run, run.Text, spec);
            line.Size[i] = size;
            line.Rest[i] = Colour(sample.Rest, 0.16);
        }
        line.Words = words;
        return line;
    }

    protected override void DrawLine(ID2D1DeviceContext dc, StageLine stageLine, double time, double exitStart, in Matrix4x4 stage,
                                      double alpha, float blur, int shown)
    {
        var line = (PortalLine)stageLine;
        var runs = line.Runs;
        var n = shown;
        if (n == 0) return;
        var leaving = StageMotion.Leave(time, exitStart);
        var whole = (1 - leaving) * alpha;
        if (!(whole > 0.002)) return;
        var speed = line.Speed;
        var reduce = ReduceMotion;
        var seed = line.Seed;
        // Smootherstep starts and ends at rest, so the trail's sum of them never jolts.
        var settled = line.A.AsSpan(0, n);
        var ranks = line.B.AsSpan(0, n);
        var arrivals = line.C.AsSpan(0, n);
        for (var i = 0; i < n; i++)
        {
            // Under Reduce Motion each step is a dissolve instead (StageLayer.DrawStage).
            settled[i] = reduce ? 1 : StageShared.Glide((time - (runs[i].Start - 0.05)) / (0.6 / speed));
            arrivals[i] = StageMotion.Arrival(runs[i].Start, time, speed, reduce);
        }
        double after = 0;
        for (var i = n - 1; i >= 0; i--) { ranks[i] = after; after += settled[i]; }
        var trail = PortalGeometry.TrailFor(seed, Stage, speed);
        // Quick songs get less float and a shorter run-in, so the eye can follow.
        var drift = reduce ? 0 : 1 / speed;
        var bounds = Bounds;
        var u = Unit;
        var tile = Tile(line.InkTime);
        var tileText = TileText(line.InkTime);
        var flat = Matrix3x2.CreateTranslation(0, reduce ? 0 : (float)(-Stage.Height * 0.1 * leaving)) * Homography.Flat(stage);
        var boil = reduce ? 0 : Math.Floor(time * 8) * 0.9;
        for (var id = 0; id < n; id++)
        {
            var word = line.Words[id];
            var run = runs[id];
            var size = line.Size[id];
            var rank = ranks[id];
            var eased = settled[id];
            var a = arrivals[id];
            // 1 while this is the word being sung, easing to 0 as the next takes over: the pill restyles over that hand-over.
            var live = StageMotion.Liveness(rank);
            var scale = PortalGeometry.Scale(rank);
            var outlined = Outlined(seed, id);
            var corner = size * (0.24 + 0.26 * StageShared.Chance(seed, id, 11));
            var badgeSide = StageShared.Chance(seed, id, 13);
            var tiltAmount = 4 + 10 * StageShared.Chance(seed, id, 14);
            var tiltSign = StageShared.Chance(seed, id, 15) < 0.5 ? 1.0 : -1.0;
            var tilt = reduce ? 0 : ((1 - eased) * tiltAmount * tiltSign + Math.Sin(time * 1.1 + id) * 1.6) / speed;
            var hasBadge = badgeSide < 0.85;
            var badgeWidth = size * 0.62 * live;
            var contentWidth = word.Size.Width + (hasBadge ? size * 0.3 + badgeWidth : 0);
            var contentHeight = Math.Max(word.Size.Height, hasBadge ? size * 0.62 : 0);
            double pillWidth = contentWidth + size, pillHeight = contentHeight + size * 0.52;

            // Where it sits on the trail, bobbing and swaying, coming in from its own direction.
            var p = trail.Along(rank);
            var bob = Math.Sin(time * (1.2 + 0.6 * StageShared.Chance(seed, id, 7)) + id * 1.3) * u * 0.07 * drift;
            var sway = Math.Cos(time * (0.9 + 0.5 * StageShared.Chance(seed, id, 8)) + id * 0.9) * u * 0.05 * drift;
            var heading = StageShared.Chance(seed, id, 9) * 2 * Math.PI;
            var rise = 1 - eased;
            var comeX = rise * Math.Cos(heading) * bounds.Width * 0.07 * drift;
            var comeY = rise * Math.Sin(heading) * bounds.Height * 0.1 * drift;
            var centre = PortalGeometry.KeepOnStage(
                new PointD(bounds.MidX + trail.ShiftX + p.X + sway + comeX, bounds.MidY + trail.Lift + p.Y + bob + comeY),
                new SizeD(pillWidth, pillHeight), scale, bounds);

            var opacity = eased * Math.Max(0, 1 - Math.Max(0, rank - (speed > 1.6 ? 1.1 : 1.6)) * 0.7) * (0.85 + 0.15 * live) * whole;
            if (!(opacity > 0.002)) continue;
            var landing = reduce ? 0 : 1 - Math.Min(a, 1);
            // It squashes as it lands, then settles.
            var pill = Matrix3x2.CreateScale((float)scale)
                * Matrix3x2.CreateScale((float)(1 + 0.07 * landing), (float)(1 - 0.07 * landing))
                * Matrix3x2.CreateScale(reduce ? 1 : (float)(0.9 + 0.1 * Math.Min(a, 1.05)))
                * Matrix3x2.CreateRotation(Radians(tilt * (0.5 + 0.5 * live)))
                * Matrix3x2.CreateTranslation((float)centre.X, (float)centre.Y) * flat;

            if (!outlined && live > 0.001)
                SoftRounded(dc, Matrix3x2.CreateTranslation(0, Points(4)) * pill, pillWidth, pillHeight, corner, Points(9),
                            new Vector4(0, 0, 0, 1), 0.2 * live * opacity);
            if (live > 0.001)
            {
                // A soft ripple swells off the pill as it lands (not under Reduce Motion).
                if (!reduce)
                    SoftRounded(dc, Matrix3x2.CreateScale((float)(1 + 0.3 * eased)) * pill, pillWidth, pillHeight, corner, size * 0.32,
                                tile, 0.5 * (1 - eased) * live * opacity / 0.73);
                // Two loose, hand-drawn outlines round the pill, redrawn a few times a second so the lines seem to boil.
                for (var pass = 0; pass < 2; pass++)
                {
                    var outline = Matrix3x2.CreateScale((float)(1.08 + 0.05 * pass))
                        * Matrix3x2.CreateRotation(Radians(reduce ? 0 : pass == 0 ? -1.6 : 1.4)) * pill;
                    StrokeSketchy(dc, outline, pillWidth, pillHeight, corner * 1.15, size * 0.035, boil + pass * 2.3, id * 3 + pass,
                                  Math.Max(1.2 * Pt, size * 0.026), tile, (pass == 0 ? 0.75 : 0.4) * eased * live * opacity);
                }
            }
            // The resting pill, and the live one fading in over it.
            if (live < 0.999)
                FillRounded(dc, pill, pillWidth, pillHeight, corner, line.Rest[id], (outlined ? 1 - live : 1) * opacity);
            if (live > 0.001)
            {
                if (outlined) StrokeRounded(dc, pill, pillWidth, pillHeight, corner, Math.Max(2 * Pt, size * 0.07), tile, live * opacity);
                else FillRounded(dc, pill, pillWidth, pillHeight, corner, tile, live * opacity);
            }

            // The content: the badge before or after the word (it gives its room back as the word hands over).
            var left = -contentWidth / 2;
            double wordX, badgeX = 0;
            if (hasBadge && badgeSide < 0.6) { badgeX = left + badgeWidth / 2; wordX = left + badgeWidth + size * 0.3 + word.Size.Width / 2; }
            else { wordX = left + word.Size.Width / 2; badgeX = left + word.Size.Width + size * 0.3 + badgeWidth / 2; }
            if (hasBadge && live > 0.001)
            {
                var badge = Matrix3x2.CreateTranslation((float)badgeX, 0) * pill;
                FillRounded(dc, badge, size * 0.62, size * 0.62, size * 0.31, tile, (outlined ? 0.2 : 0.35 + 0.65 * live) * live * opacity);
                var icon = Matrix3x2.CreateRotation(Radians(reduce ? 0 : (1 - eased) * -90 * tiltSign)) * badge;
                DrawBadge(dc, trail.Badge, icon, size * 0.34, outlined ? tile : tileText, live * opacity);
            }
            var unit = Homography.Of(Matrix3x2.CreateTranslation((float)wordX, 0) * pill);
            if (live < 0.999)
            {
                DrawLabel(dc, line, word, run, 0, time, unit, opacity, blur);
                DrawReading(dc, word, 0, unit, opacity, blur);
            }
            if (live > 0.001)
            {
                DrawLabel(dc, line, word, run, 1, time, unit, live * opacity, blur);
                DrawReading(dc, word, 1, unit, live * opacity, blur);
            }
        }
    }

    /// <summary>A badge (the Mac's arrow.up.right, arrow.right, sparkle, play.fill, plus, bolt.fill) of <paramref name="size"/>.</summary>
    private void DrawBadge(ID2D1DeviceContext dc, PortalGeometry.Badge badge, in Matrix3x2 transform, double size, Vector4 colour,
                           double opacity)
    {
        if (!(opacity > 0.002)) return;
        // The symbol fills its type size (the Mac sets it as a bold glyph of that size).
        var h = (float)(size * 0.62);
        var stroke = size * 0.2;
        dc.Transform = transform;
        switch (badge)
        {
            case PortalGeometry.Badge.ArrowUpRight:
                Line(dc, new Vector2(-h * 0.7f, h * 0.7f), new Vector2(h * 0.7f, -h * 0.7f), stroke, colour, opacity);
                Line(dc, new Vector2(-h * 0.15f, -h * 0.7f), new Vector2(h * 0.7f, -h * 0.7f), stroke, colour, opacity);
                Line(dc, new Vector2(h * 0.7f, -h * 0.7f), new Vector2(h * 0.7f, h * 0.15f), stroke, colour, opacity);
                break;
            case PortalGeometry.Badge.ArrowRight:
                Line(dc, new Vector2(-h * 0.85f, 0), new Vector2(h * 0.85f, 0), stroke, colour, opacity);
                Line(dc, new Vector2(h * 0.2f, -h * 0.6f), new Vector2(h * 0.85f, 0), stroke, colour, opacity);
                Line(dc, new Vector2(h * 0.2f, h * 0.6f), new Vector2(h * 0.85f, 0), stroke, colour, opacity);
                break;
            case PortalGeometry.Badge.Sparkle:
                FillStar(dc, transform, size * 1.15, colour, opacity);
                break;
            case PortalGeometry.Badge.Play:
                dc.Transform = Matrix3x2.CreateScale(h) * transform;
                dc.FillGeometry(_play!, Brush(colour, opacity), null);
                break;
            case PortalGeometry.Badge.Plus:
                Line(dc, new Vector2(-h * 0.8f, 0), new Vector2(h * 0.8f, 0), stroke, colour, opacity);
                Line(dc, new Vector2(0, -h * 0.8f), new Vector2(0, h * 0.8f), stroke, colour, opacity);
                break;
            default:
                dc.Transform = Matrix3x2.CreateScale(h) * transform;
                dc.FillGeometry(_bolt!, Brush(colour, opacity), null);
                break;
        }
    }
}
