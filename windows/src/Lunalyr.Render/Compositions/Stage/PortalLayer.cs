// Portal (the Mac's StageLayouts/PortalLayout.swift, Test Build 90: portal, portalPill, portalIcon, portalBackground,
// PortalPillShape, SketchyOutline; Lunalyr's own code, ported as it is). The word being sung sits in a solid pill,
// tilted as it arrives and settling flat, with a soft burst swelling off it as it lands (a quick run shares one burst).
// The words before it climb above it as a trail of smaller, paler type, spaced by their own heights so none overlaps,
// and hold still there to be read; a word the trail would push off the top fades out as it nears it. A word with a
// picture of its own ("love", "home", "call") carries its icon in the pill (a Segoe Fluent Icons glyph standing in for
// the Mac's SF Symbol); some other words carry a small ornament, never one of the three pills before. The pills' shapes
// rotate: capsule, rounded, tag, speech, slant, never the same twice in a row. One pill per word: glued syllables are one
// pill, and an unspaced line is parted into its words. It moves on the original Portal's smooth timing (Build 90): the
// shared landing spring, a 0.6 s settle and 0.36 s lift over the line's pace, shortened only for a word followed within
// 0.35 s, every curve starting at the word's arrival so nothing jumps. The choreography is PortalGeometry's.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Vortice.Direct2D1;

namespace Lunalyr.Render.Compositions.Stage;

internal sealed class PortalLayer(IWordBreaker breaker, Logger? logger) : StageLayer(CompositionKind.Portal, breaker, logger)
{
    private ID2D1PathGeometry? _quarter;

    private sealed class PortalLine(StageSource source) : StageLine(source)
    {
        public PortalMotion Motion { get; } = new(source.Runs.Count);
        public double[] Size { get; } = new double[Math.Max(1, source.Runs.Count)];
        public bool[] Outlined { get; } = new bool[Math.Max(1, source.Runs.Count)];
        public PortalShape[] Shapes { get; set; } = [];
        public bool[] Flip { get; } = new bool[Math.Max(1, source.Runs.Count)];
        public bool[] IconFirst { get; } = new bool[Math.Max(1, source.Runs.Count)];
        public Ink[]?[] Icons { get; } = new Ink[]?[Math.Max(1, source.Runs.Count)];
        /// <summary>The run's icon, for the size estimate (a symbol, an ornament, or none).</summary>
        public bool[] HasIcon { get; } = new bool[Math.Max(1, source.Runs.Count)];
        public double[] Estimated { get; } = new double[Math.Max(1, source.Runs.Count)];
        public double[] EstimatedClimb { get; } = new double[Math.Max(1, source.Runs.Count)];
        public double[] Shown { get; } = new double[Math.Max(1, source.Runs.Count)];
        public double[] Climb { get; } = new double[Math.Max(1, source.Runs.Count)];
        public double[] Column { get; } = new double[Math.Max(1, source.Runs.Count)];
        public double[] Visible { get; } = new double[Math.Max(1, source.Runs.Count)];
        public double[] Tilts { get; } = new double[Math.Max(1, source.Runs.Count)];
        public double[] PillWidth { get; } = new double[Math.Max(1, source.Runs.Count)];
        public double[] PillHeight { get; } = new double[Math.Max(1, source.Runs.Count)];

        public override void Dispose()
        {
            base.Dispose();
            foreach (var inks in Icons)
                if (inks is not null) foreach (var ink in inks) ink.Dispose();
        }
    }

    protected override List<StageRun> MakeRuns(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue) =>
        PortalGeometry.Runs(words, glue, Breaker);

    protected override IReadOnlyList<bool> RunGlue(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue) =>
        PortalGeometry.Glue(words, glue, Breaker);

    protected override void CreateShapes(ID2D1DeviceContext dc, ID2D1Factory factory)
    {
        // A quarter of a unit circle, from (1, 0) to (0, 1): the speech pill's rounded corners, stroked.
        _quarter = factory.CreatePathGeometry();
        using var sink = _quarter.Open();
        sink.BeginFigure(new Vector2(1, 0), FigureBegin.Hollow);
        sink.AddArc(new ArcSegment
        {
            Point = new Vector2(0, 1), Size = new Vortice.Mathematics.Size(1, 1), RotationAngle = 0,
            SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Small,
        });
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
    }

    protected override void DisposeOwnShapes()
    {
        _quarter?.Dispose();
        _quarter = null;
    }

    protected override StageLine Build(StageSource source)
    {
        var line = new PortalLine(source);
        var seed = line.Seed;
        var u = Unit;
        var tile = Tile(source.InkTime);
        var tileText = TileText(source.InkTime);
        var runs = source.Runs;
        var icons = PortalGeometry.Icons([.. runs.Select(run => run.Text)], i => StageShared.Chance(seed, i < 0 ? null : i, 6));
        line.Shapes = PortalGeometry.Shapes(runs.Count, i => StageShared.Chance(seed, i, 16));
        var words = new StageWord[runs.Count];
        for (var i = 0; i < words.Length; i++)
        {
            var run = runs[i];
            var sample = Sample(line, run);
            // Set at its resting size; older pills are scaled down, not re-set.
            var size = u * (1.2 + 0.3 * StageShared.Chance(seed, run.Id, 10));
            // Some pills are only outlined.
            var outlined = StageShared.Chance(seed, run.Id, 12) < 0.25;
            var live = outlined ? tile : tileText;
            var spec = WithReading(new WordSpec(size, [Colour(sample.Rest, 0.85), live]) { MaxWidth = Stage.Width * 0.7, Raster = 1.2f },
                                   source.Readings[i], size, [Colour(sample.Reading), Faded(live, LyricHighlight.ReadingOpacity)],
                                   shadow: false, maxWidth: Stage.Width * 0.7);
            words[i] = Bake(run, run.Text, spec);
            line.Size[i] = size;
            line.Outlined[i] = outlined;
            line.Flip[i] = StageShared.Chance(seed, run.Id, 17) < 0.5;
            line.IconFirst[i] = StageShared.Chance(seed, run.Id, 13) < 0.6;
            line.HasIcon[i] = icons[i] is not null;
            // The Mac sets its symbol at 0.34 of the word's size; a Segoe Fluent Icons glyph inks about 0.55 of its em,
            // so it is set larger to fill the same share of its disc.
            line.Icons[i] = BakeSymbol(SegoeIcons.For(icons[i]), size * 0.55, outlined ? tile : tileText);
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
        var u = Unit;
        var bounds = Bounds;
        // Worked out over the whole line, so a word's entrance knows when the next comes.
        var motion = line.Motion;
        PortalGeometry.Motion(runs, time, speed, reduce, motion);
        // Ranks count only the words shown, so the newest is always the live one.
        var ranks = line.A.AsSpan(0, n);
        var lives = line.B.AsSpan(0, n);
        var scales = line.C.AsSpan(0, n);
        double after = 0;
        for (var i = n - 1; i >= 0; i--) { ranks[i] = after; after += motion.Settle[i]; }
        for (var i = 0; i < n; i++)
        {
            lives[i] = StageMotion.Liveness(ranks[i]);
            scales[i] = PortalGeometry.Scale(ranks[i]);
            // Each pill's tilt, in degrees: it leans in as it arrives and rocks a little while it is sung.
            var tiltAmount = 4 + 10 * StageShared.Chance(seed, i, 14);
            var tiltSign = StageShared.Chance(seed, i, 15) < 0.5 ? 1.0 : -1.0;
            var rock = Math.Sin(time * 1.1 + i) * 1.6 * lives[i];
            var entrance = (1 - motion.Settle[i]) * tiltAmount * tiltSign;
            line.Tilts[i] = reduce ? 0 : (entrance + rock) / speed * (0.5 + 0.5 * lives[i]);
        }
        // Which way this line's trail leans, and where the pills sit. The trail always climbs above the live pill.
        var flipX = StageShared.Chance(seed, null, 1) < 0.5 ? 1.0 : -1.0;
        var reach = (0.8 + 0.5 * StageShared.Chance(seed, null, 3)) / Math.Sqrt(speed);
        var shiftX = (StageShared.Chance(seed, null, 4) - 0.5) * 0.24 * Stage.Width;
        var lift = Stage.Height * (0.1 + 0.08 * StageShared.Chance(seed, null, 5));
        // Pills further back than this fade out: the live one and two before it.
        var fadeFrom = speed > 1.6 ? 1.0 : 1.6;
        var gap = u * 0.16;
        // A pill the trail would push past the top of the stage fades out as it nears it (judged from the type size, before
        // measuring), rather than being held at the top, where the pills below would pile onto it.
        var roomAbove = Stage.Height * 0.48 + lift;
        for (var i = 0; i < n; i++)
        {
            var size = line.Size[i];
            var reading = line.Words[i].HasReading ? StageShared.ReadingSlot(size, point: Pt) : 0;
            line.Estimated[i] = scales[i] * (size * 1.5 + reading) * (1.04 + 0.1 * lives[i]);
        }
        PortalGeometry.Climb(line.Estimated.AsSpan(0, n), motion.Lift.AsSpan(0, n), gap, line.EstimatedClimb);
        for (var i = 0; i < n; i++)
        {
            var aged = 1 - Easing.Smoothstep((ranks[i] - fadeFrom) / 0.9);
            var top = line.EstimatedClimb[i] + line.Estimated[i] / 2;
            var crowded = Easing.Smoothstep((top - roomAbove) / Math.Max(1, line.Estimated[i] * 0.6) + 1);
            // The live pill never fades for room; the one it took over from starts to as it hands over.
            line.Visible[i] = aged * (1 - crowded * (1 - lives[i]));
        }
        // Measured: each pill's own box (its icon gives its room back as the word hands over); its outline reaches a
        // little beyond it while it is live.
        for (var i = 0; i < n; i++)
        {
            var size = line.Size[i];
            var word = line.Words[i].Size;
            var icon = line.Icons[i] is not null;
            line.PillWidth[i] = word.Width + (icon ? size * 0.3 + size * 0.62 * lives[i] : 0) + size;
            line.PillHeight[i] = Math.Max(word.Height, icon ? size * 0.62 : 0) + size * 0.52;
            line.Shown[i] = line.PillHeight[i] * scales[i] * (1.04 + 0.1 * lives[i]);
        }
        var baseY = bounds.MidY + lift;
        PortalGeometry.Column(line.Shown.AsSpan(0, n), motion.Lift.AsSpan(0, n), line.Visible.AsSpan(0, n), motion.Appear.AsSpan(0, n),
                              gap, baseY - bounds.MinY, bounds.MaxY - baseY, line.Climb, line.Column);
        // Quick songs get less float, so the eye can follow.
        var floatAmount = reduce ? 0 : 1 / speed;
        var group = Matrix3x2.CreateTranslation(0, reduce ? 0 : (float)(-Stage.Height * 0.1 * leaving)) * Homography.Flat(stage);
        var tile = Tile(line.InkTime);
        var calm = Desaturated(tile, 0.78);
        var boil = reduce ? 0 : Math.Floor(time * 8) * 0.9;
        for (var id = 0; id < n; id++)
        {
            var opacity = motion.Appear[id] * line.Visible[id] * (0.85 + 0.15 * lives[id]) * whole;
            if (!(opacity > 0.002)) continue;
            var rank = ranks[id];
            var live = lives[id];
            // Where it sits on the trail, which zig-zags a little either side.
            var x0 = AlongX(rank, flipX, reach);
            // Only the live pill floats; the trail holds still to be read.
            var floating = live * floatAmount;
            var bob = Math.Sin(time * (1.2 + 0.6 * StageShared.Chance(seed, id, 7)) + id * 1.3) * u * 0.07 * floating;
            var sway = Math.Cos(time * (0.9 + 0.5 * StageShared.Chance(seed, id, 8)) + id * 0.9) * u * 0.05 * floating;
            // Each pill comes in from its own direction, between the right and below, never down through the trail.
            var heading = StageShared.Chance(seed, id, 9) * 0.5 * Math.PI;
            var travel = (1 - motion.Settle[id]) * floatAmount;
            var comeX = travel * Math.Cos(heading) * bounds.Width * 0.07;
            var comeY = travel * Math.Sin(heading) * bounds.Height * 0.1;
            // A pill with its reading is taller; none is pushed off the stage.
            var centre = PortalGeometry.KeepOnStage(
                new PointD(bounds.MidX + shiftX + x0 + sway + comeX, baseY + line.Column[id] + bob + comeY),
                new SizeD(line.PillWidth[id], line.PillHeight[id]), scales[id], bounds);
            DrawPill(dc, line, id, time, centre, scales[id], live, motion, group, tile, calm, boil, opacity, blur);
        }
    }

    /// <summary>Where a pill of <paramref name="rank"/> sits across the stage: the trail zig-zags a little either side.</summary>
    private double AlongX(double rank, double flipX, double reach)
    {
        static double Zig(int k) => k switch { 0 => 0, 1 => -0.07, 2 => 0.05, _ => -0.02 };
        var capped = Math.Min(Math.Max(rank, 0), 3);
        var low = (int)Math.Floor(capped);
        var high = Math.Min(low + 1, 3);
        var f = StageShared.Glide(capped - low);
        var scale = Stage.Width * flipX * reach;
        return (Zig(low) + (Zig(high) - Zig(low)) * f) * scale;
    }

    /// <summary>One word: its pill while it is sung, pale type once it is not.</summary>
    private void DrawPill(ID2D1DeviceContext dc, PortalLine line, int id, double time, PointD centre, double scale, double live,
                          PortalMotion motion, in Matrix3x2 group, Vector4 tile, Vector4 calm, double boil, double opacity, float blur)
    {
        var reduce = ReduceMotion;
        var run = line.Runs[id];
        var word = line.Words[id];
        var size = line.Size[id];
        var eased = motion.Settle[id];
        var burst = motion.Burst[id];
        var a = StageMotion.Arrival(run.Start, time, line.Speed, reduce);
        var outlined = line.Outlined[id];
        var shape = line.Shapes[id];
        var flip = line.Flip[id];
        double w = line.PillWidth[id], h = line.PillHeight[id];
        var landing = reduce ? 0 : 1 - Math.Min(a, 1);
        // It squashes as it lands, then settles.
        var pill = Matrix3x2.CreateScale((float)scale)
            * Matrix3x2.CreateScale((float)(1 + 0.07 * landing), (float)(1 - 0.07 * landing))
            * Matrix3x2.CreateScale(reduce ? 1 : (float)(0.9 + 0.1 * Math.Min(a, 1.05)))
            * Matrix3x2.CreateRotation(Radians(line.Tilts[id]))
            * Matrix3x2.CreateTranslation((float)centre.X, (float)centre.Y) * group;
        // The slant: a capsule leaned by a sixth of its height.
        var body = shape == PortalShape.Slant ? Matrix3x2.CreateSkew(-MathF.Atan(flip ? -0.18f : 0.18f), 0) * pill : pill;
        var radius = Math.Max(0, Math.Min(PortalGeometry.Radius(shape, size), Math.Min(h / 2, w / 2)));
        if (live > 0.001)
        {
            if (!outlined)
                SoftRounded(dc, Matrix3x2.CreateTranslation(0, Points(4)) * body, w, h, radius, Points(9), new Vector4(0, 0, 0, 1),
                            0.2 * live * opacity);
            // The burst: a soft swell off the pill as it lands (a quick run's words share one, passed from pill to pill).
            if (!reduce && burst < 1)
                SoftRounded(dc, Matrix3x2.CreateScale((float)(1 + 0.3 * burst)) * body, w, h, radius, size * 0.32, tile,
                            0.5 * (1 - burst) * live * opacity / 0.73);
            // One loose, hand-drawn outline round the pill, redrawn a few times a second so the line seems to boil (not
            // round the speech and slanted shapes, which a rounded loop does not fit).
            if (shape is not (PortalShape.Speech or PortalShape.Slant))
                StrokeSketchy(dc, Matrix3x2.CreateScale(1.08f) * Matrix3x2.CreateRotation(Radians(reduce ? 0 : -1.6)) * pill, w, h,
                              radius * 1.15, size * 0.035, boil, id * 3, Math.Max(1.2 * Pt, size * 0.026), tile, 0.6 * eased * live * opacity);
            // Only the word being sung sits in a pill: the trail is plain pale type. A little less saturated than the
            // ink's tile colour, so the large live pill is calmer on a dark stage.
            if (outlined) StrokePill(dc, body, w, h, radius, shape, flip, size, Math.Max(2 * Pt, size * 0.07), tile, live * opacity);
            else FillPill(dc, body, w, h, radius, shape, flip, size, calm, live * opacity);
        }
        // The content: the icon before or after the word, giving its room back as the word hands over.
        var iconWidth = line.Icons[id] is not null ? size * 0.62 * live : 0;
        var contentWidth = word.Size.Width + (line.Icons[id] is not null ? size * 0.3 + iconWidth : 0);
        var left = -contentWidth / 2;
        double wordX, iconX;
        if (line.Icons[id] is not null && line.IconFirst[id])
        {
            iconX = left + iconWidth / 2;
            wordX = left + iconWidth + size * 0.3 + word.Size.Width / 2;
        }
        else
        {
            wordX = left + word.Size.Width / 2;
            iconX = left + word.Size.Width + size * 0.3 + iconWidth / 2;
        }
        if (line.Icons[id] is { } icon && live > 0.001)
        {
            // It spins in with the pill's entrance and goes with the pill when the next word takes over.
            var tiltSign = StageShared.Chance(line.Seed, id, 15) < 0.5 ? 1.0 : -1.0;
            var disc = Matrix3x2.CreateTranslation((float)iconX, 0) * pill;
            FillRounded(dc, disc, size * 0.62, size * 0.62, size * 0.31, tile, (outlined ? 0.2 : 0.35 + 0.65 * live) * live * opacity);
            var glyph = Matrix3x2.CreateScale(reduce ? 1 : (float)(0.6 + 0.4 * eased))
                * Matrix3x2.CreateRotation(Radians(reduce ? 0 : (1 - eased) * -90 * tiltSign)) * disc;
            DrawInk(dc, icon[0], Homography.Of(glyph), Vector2.Zero, live * opacity);
        }
        var unit = Homography.Of(Matrix3x2.CreateTranslation((float)wordX, 0) * pill);
        if (live < 0.999)
        {
            DrawLabel(dc, line, word, run, 0, time, unit, opacity, blur);
            DrawReading(dc, line, word, run, 0, time, unit, opacity, blur);
        }
        if (live > 0.001)
        {
            DrawLabel(dc, line, word, run, 1, time, unit, live * opacity, blur);
            DrawReading(dc, line, word, run, 1, time, unit, live * opacity, blur);
        }
    }

    /// <summary>A pill's fill: a rounded rectangle, the speech shape's one sharp corner squared off.</summary>
    private void FillPill(ID2D1DeviceContext dc, in Matrix3x2 body, double w, double h, double radius, PortalShape shape, bool flip,
                          double size, Vector4 colour, double opacity)
    {
        FillRounded(dc, body, w, h, radius, colour, opacity);
        if (shape != PortalShape.Speech || !(radius > 0)) return;
        // The sharp corner: the bottom leading one (trailing when flipped), nearly square.
        var sharp = Math.Min(radius, size * 0.05);
        var cx = flip ? w / 2 - radius / 2 : -w / 2 + radius / 2;
        FillRounded(dc, Matrix3x2.CreateTranslation((float)cx, (float)(h / 2 - radius / 2)) * body, radius, radius, sharp, colour, opacity);
    }

    /// <summary>A pill's outline, inside its edge: the speech shape's rounded corners as arcs and its sharp corner square.</summary>
    private void StrokePill(ID2D1DeviceContext dc, in Matrix3x2 body, double w, double h, double radius, PortalShape shape, bool flip,
                            double size, double stroke, Vector4 colour, double opacity)
    {
        if (shape != PortalShape.Speech)
        {
            StrokeRounded(dc, body, w, h, radius, stroke, colour, opacity);
            return;
        }
        if (!(opacity > 0.002)) return;
        double hw = w / 2 - stroke / 2, hh = h / 2 - stroke / 2;
        var r = Math.Max(0, Math.Min(radius - stroke / 2, Math.Min(hw, hh)));
        var sharpLeft = !flip;
        var brush = Brush(colour, opacity);
        var frame = body;
        dc.Transform = frame;
        float X(double v) => (float)v;
        // Top, right and left edges and the bottom, leaving room for each rounded corner.
        dc.DrawLine(new Vector2(X(-hw + r), X(-hh)), new Vector2(X(hw - r), X(-hh)), brush, (float)stroke, Round);
        dc.DrawLine(new Vector2(X(hw), X(-hh + r)), new Vector2(X(hw), X(sharpLeft ? hh - r : hh)), brush, (float)stroke, Round);
        dc.DrawLine(new Vector2(X(-hw), X(-hh + r)), new Vector2(X(-hw), X(sharpLeft ? hh : hh - r)), brush, (float)stroke, Round);
        dc.DrawLine(new Vector2(X(sharpLeft ? -hw : -hw + r), X(hh)), new Vector2(X(sharpLeft ? hw - r : hw), X(hh)), brush, (float)stroke, Round);
        if (!(r > 0.5)) return;
        // The rounded corners: a quarter circle each (the unit arc runs from the +x axis to the +y axis).
        void Corner(double cx, double cy, double turn)
        {
            dc.Transform = Matrix3x2.CreateScale((float)r) * Matrix3x2.CreateRotation((float)turn)
                * Matrix3x2.CreateTranslation(X(cx), X(cy)) * frame;
            dc.DrawGeometry(_quarter!, brush, (float)(stroke / r), Round);
        }
        // (The sharp corner has none: its two edges meet square.)
        if (sharpLeft) Corner(hw - r, hh - r, 0);       // bottom right
        else Corner(-hw + r, hh - r, MathF.PI / 2);     // bottom left
        Corner(-hw + r, -hh + r, MathF.PI);             // top left
        Corner(hw - r, -hh + r, -MathF.PI / 2);         // top right
    }

    /// <summary><paramref name="colour"/> with its saturation scaled (SwiftUI's saturation(), on luminance).</summary>
    private static Vector4 Desaturated(Vector4 colour, double amount)
    {
        var luma = 0.2126f * colour.X + 0.7152f * colour.Y + 0.0722f * colour.Z;
        var k = (float)amount;
        return new Vector4(luma + (colour.X - luma) * k, luma + (colour.Y - luma) * k, luma + (colour.Z - luma) * k, colour.W);
    }
}
