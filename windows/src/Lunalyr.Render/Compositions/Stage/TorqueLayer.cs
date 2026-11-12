// Torque (the Mac's StageLyricView.torque in StageLyricStyles.swift; Lunalyr's own code, ported as it is). Words are
// wrung into place: each twists in about its own vertical axis and settles with a thick extruded edge, stacking upwards
// with the older words smaller and tipped the other way. The whole stack is turned a little away in depth.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Vortice.Direct2D1;

namespace Lunalyr.Render.Compositions.Stage;

internal sealed class TorqueLayer(IWordBreaker breaker, Logger? logger) : StageLayer(CompositionKind.Torque, breaker, logger)
{
    private sealed class TorqueLine(StageSource source) : StageLine(source)
    {
        public double[] Slots { get; } = new double[Math.Max(1, source.Runs.Count)];
        public double[] Heights { get; } = new double[Math.Max(1, source.Runs.Count)];
    }

    protected override StageLine Build(StageSource source)
    {
        var line = new TorqueLine(source);
        var rest = TorqueGeometry.RestSize(Unit);
        var words = new StageWord[source.Runs.Count];
        for (var i = 0; i < words.Length; i++)
        {
            var run = source.Runs[i];
            var sample = Sample(line, run);
            var sung = Colour(sample.Sung);
            // The thick edge: darker copies of the sung colour.
            var edge = new Vector4(Math.Max(0, sung.X - 0.25f), Math.Max(0, sung.Y - 0.25f), Math.Max(0, sung.Z - 0.25f), sung.W * 0.55f);
            var spec = WithReading(new WordSpec(rest, [Colour(sample.Rest, 0.85), sung, edge])
            {
                MaxWidth = Stage.Width * 0.7, Shadow = Shadow(0.3, 6, 3), Raster = 1.15f,
            }, source.Readings[i], rest, [Colour(sample.Reading), Colour(sample.Sung, LyricHighlight.ReadingOpacity)],
               gap: TorqueGeometry.ReadingGap(rest), maxWidth: Stage.Width * 0.7);
            words[i] = Bake(run, run.Text, spec);
            line.Slots[i] = source.Readings[i] is null ? 0 : TorqueGeometry.ReadingSlot(rest, Pt);
        }
        line.Words = words;
        return line;
    }

    protected override void DrawLine(ID2D1DeviceContext dc, StageLine stageLine, double time, double exitStart, in Matrix4x4 stage,
                                     double alpha, float blur)
    {
        var line = (TorqueLine)stageLine;
        var runs = line.Runs;
        var n = StageMotion.ShownCount(runs, time);
        if (n == 0) return;
        var leaving = StageMotion.Leave(time, exitStart);
        var whole = (1 - leaving) * alpha;
        if (!(whole > 0.002)) return;
        var speed = line.Speed;
        var seed = line.Seed;
        var u = Unit;
        var rest = TorqueGeometry.RestSize(u);
        var arrivals = line.A.AsSpan(0, n);
        var ranks = line.B.AsSpan(0, n);
        var scales = line.C.AsSpan(0, n);
        var weights = line.D.AsSpan(0, n);
        var sizes = line.Sizes.AsSpan(0, n);
        for (var i = 0; i < n; i++) arrivals[i] = StageMotion.Arrival(runs[i].Start, time, speed);
        StageMotion.Ranks(runs, n, time, speed, seed, false, ranks);
        for (var i = 0; i < n; i++)
        {
            scales[i] = TorqueGeometry.ScaleFor(ranks[i]);
            weights[i] = TorqueGeometry.WeightFor(arrivals[i], ranks[i]);
            sizes[i] = line.Words[i].Size;
        }
        var heights = line.Heights.AsSpan(0, n);
        TorqueGeometry.Heights(sizes, scales, weights, line.Slots.AsSpan(0, n), heights);
        // A stack taller than the stage is given the room it needs, then shrunk to fit the stage.
        var need = TorqueGeometry.Height(heights, rest);
        var height = Math.Max(Stage.Height, need);
        var bounds = new StageRect(0, Stage.Height / 2 - height / 2, Stage.Width, height);
        var places = line.Places.AsSpan(0, n);
        TorqueGeometry.Stack(heights, ranks, seed, bounds, places);
        var fit = LyricStageFit.Scale(new SizeD(Stage.Width, height), Stage);
        var middle = new Vector2((float)(Stage.Width / 2), (float)(Stage.Height / 2));
        var all = Matrix4x4.CreateScale((float)fit, (float)fit, 1, new Vector3(middle, 0))
            * Homography.TurnY(-10, middle, Stage, 0.5)
            * Matrix4x4.CreateTranslation(0, (float)(Stage.Height * 0.12 * leaving), 0) * stage;
        for (var id = 0; id < n; id++)
        {
            var word = line.Words[id];
            var run = runs[id];
            var a = arrivals[id];
            var rank = ranks[id];
            var opacity = Math.Min(1, a * 1.6) * StageMotion.TorqueVisibility(rank) * whole;
            if (!(opacity > 0.002)) continue;
            // 1 while this is the word being sung, easing to 0 as the next takes over.
            var live = StageMotion.Liveness(rank);
            var scale = scales[id];
            var even = id % 2 == 0;
            // The word's own wringing, unless another word entrance has been chosen.
            var entrance = StageShared.ChosenEntrance(Arrival, a, rest * scale, even ? 1 : -1, false);
            var twist = entrance is null ? (1 - Math.Min(a, 1)) * 110 * (even ? 1 : -1) : 0;
            // Wrung and loosened again, all the while it is held.
            var breathe = Math.Sin(time * 2.1 + id * 1.7) * (5 + 6 * live);
            var wander = Math.Sin(time * 1.3 + id * 2.3) * u * (0.08 + 0.06 * live);
            var lean = Math.Min(rank, 2) * 3 * (even ? 1 : -1);
            var e = entrance ?? EntranceTransform.Rest;
            var unit = Matrix4x4.CreateScale((float)scale, (float)scale, 1)
                * Matrix4x4.CreateRotationZ(Radians(-4 * live + lean * (1 - live)))
                * Homography.TurnY(twist + breathe, Vector2.Zero, word.Size, 0.6)
                * Homography.TurnX(breathe * 0.5, Vector2.Zero, word.Size, 0.6)
                * Homography.TurnX(e.Fold, Vector2.Zero, word.Size, 0.6)
                * Matrix4x4.CreateRotationZ(Radians(e.Turn))
                * Matrix4x4.CreateScale((float)e.Scale, (float)e.Scale, 1)
                * Matrix4x4.CreateTranslation((float)(wander + e.OffsetX + places[id].X), (float)(e.OffsetY + places[id].Y), 0)
                * all;
            var shadow = (float)((0.15 + 0.15 * live) / 0.3);
            var wordBlur = blur + (float)(rest * scale * 0.07 * (1 - Math.Min(a, 1)));
            if (live > 0.001)
            {
                // The thick edge: darker copies stepping back, thinning away as the word hands over.
                for (var s = 1; s < 7; s++)
                    DrawLabel(dc, line, word, run, 2, time, unit, live * opacity, wordBlur, 0,
                              new Vector2((float)(s * rest * 0.012), (float)(s * rest * 0.022)));
            }
            if (live < 0.999) DrawLabel(dc, line, word, run, 0, time, unit, opacity, wordBlur, shadow);
            if (live > 0.001) DrawLabel(dc, line, word, run, 1, time, unit, live * opacity, wordBlur, live < 0.999 ? 0 : shadow);
            // The reading takes the sung colour while its word is being sung, then settles back to the reading colour.
            if (live < 0.999) DrawReading(dc, word, 0, unit, opacity, wordBlur, shadow);
            if (live > 0.001) DrawReading(dc, word, 1, unit, live * opacity, wordBlur, live < 0.999 ? 0 : shadow);
        }
    }
}
