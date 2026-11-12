// Broadside (the Mac's StageLyricView.broadside in StageLyricStyles.swift; Lunalyr's own code, ported as it is). One
// strip of type that slides along as words are sung: big, round, hard-shadowed words, with the little ones ("the",
// "of") as paper labels laid across the word before. The strip runs off both edges of the stage, faded by a mask.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Lunalyr.Render.Compositions.Stage;

internal sealed class BroadsideLayer(IWordBreaker breaker, Logger? logger) : StageLayer(CompositionKind.Broadside, breaker, logger)
{
    private ID2D1GradientStopCollection? _maskStops;
    private ID2D1LinearGradientBrush? _mask;

    private sealed class BroadsideLine(StageSource source) : StageLine(source)
    {
        public double[] Slots { get; } = new double[Math.Max(1, source.Runs.Count)];
        public Vector2[] Padding { get; } = new Vector2[Math.Max(1, source.Runs.Count)];
    }

    protected override void CreateShapes(ID2D1DeviceContext dc, ID2D1Factory factory)
    {
        GradientStop[] stops =
        [
            new(0, new Color4(0, 0, 0, 0)), new(0.1f, new Color4(0, 0, 0, 1)), new(0.9f, new Color4(0, 0, 0, 1)),
            new(1, new Color4(0, 0, 0, 0)),
        ];
        _maskStops = dc.CreateGradientStopCollection(stops, Gamma.StandardRgb, ExtendMode.Clamp);
        _mask = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, Vector2.UnitX), _maskStops);
    }

    protected override void DisposeOwnShapes()
    {
        _mask?.Dispose(); _mask = null;
        _maskStops?.Dispose(); _maskStops = null;
    }

    protected override StageLine Build(StageSource source)
    {
        var line = new BroadsideLine(source);
        var u = Unit;
        double bigSize = u * 1.7, smallSize = u * 0.52;
        var tile = TileText(source.InkTime);
        var words = new StageWord[source.Runs.Count];
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
            }
            else
            {
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
                                      double alpha, float blur)
    {
        var line = (BroadsideLine)stageLine;
        var runs = line.Runs;
        var n = StageMotion.ShownCount(runs, time);
        if (n == 0) return;
        var opacity = (1 - StageMotion.Leave(time, exitStart)) * alpha;
        if (!(opacity > 0.002)) return;
        var u = Unit;
        var speed = line.Speed;
        // Snappy: each word springs in, and the strip glides along with it; the glide hands over without a jolt.
        var arrivals = line.A.AsSpan(0, n);
        var moves = line.B.AsSpan(0, n);
        var keep = line.C.AsSpan(0, n);
        var sizes = line.Sizes.AsSpan(0, n);
        for (var i = 0; i < n; i++)
        {
            arrivals[i] = StageMotion.Arrival(runs[i].Start, time, speed);
            moves[i] = StageMotion.Handoff(runs[i].Start, time, StageMotion.RunSeed(line.Seed, runs[i].Id), speed);
            var size = line.Words[i].Size;
            var pad = line.Padding[i];
            sizes[i] = new SizeD(size.Width + 2 * pad.X, size.Height + 2 * pad.Y);
        }
        var little = line.Little.AsSpan(0, n);
        // Words more than a couple of big words behind drop out of the strip.
        BroadsideGeometry.Keep(little, moves, keep);
        var places = line.Places.AsSpan(0, n);
        BroadsideGeometry.Places(sizes, little, line.Slots.AsSpan(0, n), moves, u, Bounds, places);

        // The strip fades out towards both edges of the stage.
        var flat = Homography.Flat(stage);
        dc.Transform = flat;
        _mask!.StartPoint = Vector2.Zero;
        _mask.EndPoint = new Vector2((float)Stage.Width, 0);
        dc.PushLayer(new LayerParameters1
        {
            ContentBounds = new RawRectF(-(float)Stage.Width, -(float)Stage.Height, 2 * (float)Stage.Width, 2 * (float)Stage.Height),
            MaskTransform = Matrix3x2.Identity, MaskAntialiasMode = AntialiasMode.PerPrimitive, Opacity = 1,
            OpacityBrush = _mask, LayerOptions = LayerOptions1.None,
        }, null!);
        try
        {
            var tile = Tile(line.InkTime);
            var hard = u * 0.05;
            for (var id = 0; id < n; id++)
            {
                var word = line.Words[id];
                var run = runs[id];
                var a = arrivals[id];
                var alphaWord = Math.Min(1, a * 1.6) * keep[id] * opacity;
                if (!(alphaWord > 0.002)) continue;
                var place = Matrix3x2.CreateTranslation((float)places[id].X, (float)places[id].Y);
                if (little[id])
                {
                    var local = Matrix3x2.CreateScale((float)(0.8 + 0.2 * a))
                        * Matrix3x2.CreateRotation(Radians(id % 2 == 0 ? -3 : 2.5)) * place * flat;
                    FillRounded(dc, local, sizes[id].Width, sizes[id].Height, Points(3), tile, alphaWord);
                    var unit = Homography.Of(local);
                    DrawLabel(dc, line, word, run, 0, time, unit, alphaWord, blur);
                    DrawReading(dc, word, 0, unit, alphaWord, blur);
                }
                else
                {
                    var local = Matrix3x2.CreateScale((float)(0.86 + 0.14 * a)) * place * flat;
                    var unit = Homography.Of(local);
                    DrawLabel(dc, line, word, run, 1, time, unit, alphaWord, blur, 0, new Vector2((float)hard, (float)(hard * 1.15)));
                    DrawLabel(dc, line, word, run, 0, time, unit, alphaWord, blur, 0);
                    DrawReading(dc, word, 0, unit, alphaWord, blur);
                }
            }
        }
        finally
        {
            dc.Transform = flat;
            dc.PopLayer();
        }
    }
}
