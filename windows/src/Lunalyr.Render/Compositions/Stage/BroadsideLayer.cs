// Broadside (the Mac's StageLayouts/BroadsideLayout.swift, Build 88; Lunalyr's own code, ported as it is). One strip of
// type that slides along as words are sung: big, round, hard-shadowed words, with the little ones ("the", "of") as small
// paper labels riding on the shoulder of the word before. Words that slide toward an edge of the stage fade out whole
// rather than being cut off mid-letter. The geometry is BroadsideGeometry's.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Vortice.Direct2D1;

namespace Lunalyr.Render.Compositions.Stage;

internal sealed class BroadsideLayer(IWordBreaker breaker, Logger? logger) : StageLayer(CompositionKind.Broadside, breaker, logger)
{
    private sealed class BroadsideLine(StageSource source) : StageLine(source)
    {
        public double[] Slots { get; } = new double[Math.Max(1, source.Runs.Count)];
        public Vector2[] Padding { get; } = new Vector2[Math.Max(1, source.Runs.Count)];
        /// <summary>How far a label's word reaches left of the label, so the label fades out with the word it rides on.</summary>
        public double[] Rides { get; } = new double[Math.Max(1, source.Runs.Count)];
    }

    protected override StageLine Build(StageSource source)
    {
        var line = new BroadsideLine(source);
        var u = Unit;
        double bigSize = u * 1.7, smallSize = u * 0.52;
        var tile = TileText(source.InkTime);
        var words = new StageWord[source.Runs.Count];
        var anchor = 0;
        for (var i = 0; i < words.Length; i++)
        {
            var run = source.Runs[i];
            var sample = Sample(line, run);
            if (line.Little[i])
            {
                // A label's reading is printed on the label with it.
                var spec = WithReading(new WordSpec(smallSize, [tile]) { MaxWidth = Stage.Width * 0.7 }, source.Readings[i], smallSize,
                                       [Faded(tile, LyricHighlight.ReadingOpacity)], shadow: false, maxWidth: Stage.Width * 0.7);
                words[i] = Bake(run, run.Text, spec);
                line.Padding[i] = new Vector2((float)(smallSize * 0.34), (float)(smallSize * 0.16));
                // About half an em a character of the word it rides on.
                line.Rides[i] = i > 0 ? Math.Max(0, StageShared.Count(source.Runs[anchor].Text) * bigSize * 0.5 - smallSize * 2) : 0;
            }
            else
            {
                anchor = i;
                // The word and its hard shadow, an offset copy in black.
                var spec = WithReading(new WordSpec(bigSize, [Colour(sample.Sung), new Vector4(0, 0, 0, 0.34f)]) { MaxWidth = Stage.Width * 0.7 },
                                       source.Readings[i], bigSize, [Colour(sample.Reading)], maxWidth: Stage.Width * 0.7);
                words[i] = Bake(run, run.Text, spec);
                line.Slots[i] = source.Readings[i] is null ? 0 : StageShared.ReadingSlot(bigSize, point: Pt);
            }
        }
        line.Words = words;
        return line;
    }

    protected override void DrawLine(ID2D1DeviceContext dc, StageLine stageLine, double time, double exitStart, in Matrix4x4 stage,
                                      double alpha, float blur, int shown)
    {
        var line = (BroadsideLine)stageLine;
        var runs = line.Runs;
        var n = shown;
        if (n == 0) return;
        var opacity = (1 - StageMotion.Leave(time, exitStart)) * alpha;
        if (!(opacity > 0.002)) return;
        var u = Unit;
        var speed = line.Speed;
        var reduce = ReduceMotion;
        // Snappy: each word springs in, and the strip glides along with it; the glide hands over without a jolt.
        var arrivals = line.A.AsSpan(0, n);
        var moves = line.B.AsSpan(0, n);
        var keep = line.C.AsSpan(0, n);
        var sizes = line.Sizes.AsSpan(0, n);
        for (var i = 0; i < n; i++)
        {
            arrivals[i] = StageMotion.Arrival(runs[i].Start, time, speed, reduce);
            moves[i] = StageMotion.Handoff(runs[i].Start, time, StageMotion.RunSeed(line.Seed, runs[i].Id), speed, reduce);
            var size = line.Words[i].Size;
            var pad = line.Padding[i];
            sizes[i] = new SizeD(size.Width + 2 * pad.X, size.Height + 2 * pad.Y);
        }
        var little = line.Little.AsSpan(0, n);
        // Words more than a couple of big words behind drop out of the strip.
        BroadsideGeometry.Keep(little, moves, keep);
        var places = line.Places.AsSpan(0, n);
        BroadsideGeometry.Places(sizes, little, line.Slots.AsSpan(0, n), moves, u, Bounds, places);
        var flat = Homography.Flat(stage);
        var tile = Tile(line.InkTime);
        var hard = u * 0.05;
        for (var id = 0; id < n; id++)
        {
            var word = line.Words[id];
            var run = runs[id];
            var a = arrivals[id];
            // A word nearing an edge of the stage fades out whole, never cut through a letter.
            var box = new StageRect(places[id].X - sizes[id].Width / 2, places[id].Y - sizes[id].Height / 2, sizes[id].Width, sizes[id].Height);
            var edge = BroadsideGeometry.EdgeFade(box, Stage.Width, u, little[id] ? line.Rides[id] : 0);
            var alphaWord = Math.Min(1, a * 1.6) * keep[id] * edge * opacity;
            if (!(alphaWord > 0.002)) continue;
            var place = Matrix3x2.CreateTranslation((float)places[id].X, (float)places[id].Y);
            if (little[id])
            {
                var local = Matrix3x2.CreateRotation(Radians(id % 2 == 0 ? -3 : 2.5))
                    * Matrix3x2.CreateScale(reduce ? 1 : (float)(0.8 + 0.2 * a)) * place * flat;
                FillRounded(dc, local, sizes[id].Width, sizes[id].Height, Points(3), tile, alphaWord);
                var unit = Homography.Of(local);
                DrawLabel(dc, line, word, run, 0, time, unit, alphaWord, blur);
                DrawReading(dc, line, word, run, 0, time, unit, alphaWord, blur);
            }
            else
            {
                var local = Matrix3x2.CreateScale(reduce ? 1 : (float)(0.86 + 0.14 * a)) * place * flat;
                var unit = Homography.Of(local);
                DrawLabel(dc, line, word, run, 1, time, unit, alphaWord, blur, 0, new Vector2((float)hard, (float)(hard * 1.15)));
                DrawLabel(dc, line, word, run, 0, time, unit, alphaWord, blur, 0);
                DrawReading(dc, line, word, run, 0, time, unit, alphaWord, blur);
            }
        }
    }
}
