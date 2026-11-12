// Fable (the Mac's StageLyricView.fable, fableWord, fableAccent and FableBrackets in StageLyricStyles.swift; Lunalyr's
// own code, ported as it is). Every line is set in its own shape -- a staircase, an arch, a zigzag, a wave or a ramp --
// and marks the word being sung with its own accent: an underline, a ring, a tile, a star or corner brackets.
// Neighbouring lines never share a shape or an accent. The whole line is laid out from its first word, so words land
// in their final places and nothing reflows; each word makes a short entrance of its own and then holds still.

using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Vortice.Direct2D1;

namespace Lunalyr.Render.Compositions.Stage;

internal sealed class FableLayer(IWordBreaker breaker, Logger? logger) : StageLayer(CompositionKind.Fable, breaker, logger)
{
    private readonly (PointD From, PointD To)[] _brackets = new (PointD, PointD)[8];

    private sealed class FableLine(StageSource source) : StageLine(source)
    {
        public FableStyle Style { get; init; }
        public bool RightToLeft { get; init; }
        public double[] Size { get; } = new double[Math.Max(1, source.Runs.Count)];
        public FableLayout.Arrangement? Arrangement { get; set; }
    }

    protected override StageLine Build(StageSource source)
    {
        var line = new FableLine(source)
        {
            Style = FableStyle.ForLine(source.Row),
            RightToLeft = ScriptDirection.LineIsRightToLeft(source.Runs.Select(run => run.Text)),
        };
        var u = Unit;
        var texts = source.Runs.Select(run => run.Text).ToList();
        var hero = FableStyle.HeroIndex(texts);
        var onTile = line.Style.Accent == FableAccent.Tile;
        var tileText = TileText(source.InkTime);
        var words = new StageWord[source.Runs.Count];
        var units = new SizeD[words.Length];
        var shadow = new ShadowSpec(0.3f, (float)Math.Max(3 * Pt, u * 0.08) * 0.5f, Points(2));
        for (var i = 0; i < words.Length; i++)
        {
            var run = source.Runs[i];
            var sample = Sample(line, run);
            // A word's size is fixed for the whole line so nothing reflows: the longest is set larger, the little ones a touch smaller.
            var size = source.Runs.Count <= 2 || hero == i ? u * 1.75
                : BroadsideGeometry.IsLittle(run.Text) ? u * 1.05
                : u * (1.28 + 0.14 * StageShared.Chance(line.Seed, run.Id, 71));
            line.Size[i] = size;
            Vector4[] colours = onTile ? [Colour(sample.Sung), tileText] : [Colour(sample.Sung)];
            var spec = new WordSpec(size, colours) { Shadow = shadow, Raster = 1.15f };
            if (source.Readings[i] is { } reading)
            {
                spec = spec with
                {
                    Reading = reading, ReadingSize = Math.Max(12 * Pt, size * 0.36), ReadingGap = size * 0.06,
                    ReadingColours = [Colour(sample.Rest, 0.9)], ReadingShadow = shadow,
                };
            }
            words[i] = Bake(run, run.Text, spec);
            // Room for the accent, inside the word's own measured box.
            var tight = LyricScript.KeepsSyllables(run.Text);
            units[i] = new SizeD(words[i].Size.Width + 2 * size * (tight ? 0.16 : 0.24), words[i].Size.Height + 2 * size * 0.16);
        }
        line.Words = words;
        // Room kept round the block for shadows, accents and entrances.
        var margin = Math.Max(u * 0.5, Math.Min(Stage.Width, Stage.Height) * 0.05);
        var available = new SizeD(Math.Max(1, Stage.Width - 2 * margin), Math.Max(1, Stage.Height - 2 * margin));
        line.Arrangement = FableLayout.Arrange(units, line.Style.Shape, line.RightToLeft, available, u * 0.08, u * 0.12);
        return line;
    }

    protected override void DrawLine(ID2D1DeviceContext dc, StageLine stageLine, double time, double exitStart, in Matrix4x4 stage,
                                     double alpha, float blur, int shown)
    {
        var line = (FableLine)stageLine;
        var laid = line.Arrangement;
        if (laid is null || laid.Frames.Length == 0) return;
        var runs = line.Runs;
        var leaving = StageMotion.Leave(time, exitStart);
        var whole = (1 - leaving) * alpha;
        if (!(whole > 0.002)) return;
        var speed = line.Speed;
        var reduce = ReduceMotion;
        var seed = line.Seed;
        var style = line.Style;
        var tile = Tile(line.InkTime);
        // The block, fitted inside the stage, readings and all.
        var block = Matrix3x2.CreateTranslation(-(float)(laid.Size.Width / 2), -(float)(laid.Size.Height / 2))
            * Matrix3x2.CreateScale((float)laid.Scale)
            * Matrix3x2.CreateTranslation((float)(Stage.Width / 2), (float)(Stage.Height / 2 - (reduce ? 0 : Stage.Height * 0.04 * leaving)));
        var blockWorld = Homography.Of(block) * stage;
        // The word being sung: from its onset until the next word's.
        var current = -1;
        for (var i = 0; i < runs.Count; i++)
            if (time >= runs[i].Start - 0.04) current = i;
        for (var id = 0; id < runs.Count; id++)
        {
            var run = runs[id];
            var sungAt = run.Start - 0.04;
            if (!(time >= sungAt)) continue;
            var word = line.Words[id];
            var size = line.Size[id];
            // Fable's words never travel under Reduce Motion, so its arrival only fades them in.
            var a = StageMotion.Arrival(run.Start, time, speed);
            // How much of the accent this word wears: it comes with the word and passes to the next one as that is sung.
            var next = id + 1 < runs.Count ? runs[id + 1].Start : double.PositiveInfinity;
            var rise = StageShared.Glide((time - sungAt) / 0.18);
            var fall = double.IsFinite(next) ? 1 - StageShared.Glide((time - (next - 0.04)) / 0.3) : 1;
            var wear = rise * fall;
            var opacity = Math.Min(1, a * 1.8) * (0.8 + 0.2 * (id == current ? 1 : wear)) * whole;
            if (!(opacity > 0.002)) continue;
            var entrance = StageShared.ChosenEntrance(ChosenFor(run), a, size, id % 2 == 0 ? 1 : -1, reduce);
            var kind = (int)(StageShared.Chance(seed, id, 73) * 4) % 4;
            var motion = !reduce && entrance is null
                ? FableStyle.Entrance(kind, a, size, StageShared.Chance(seed, id, 74) < 0.5 ? -1 : 1)
                : (OffsetX: 0, OffsetY: 0, Scale: 1, Spin: 0);
            var e = entrance ?? EntranceTransform.Rest;
            var frame = laid.Frames[id];
            var bottom = new Vector2(0, (float)(frame.Height / 2));
            var unitSize = frame.Size;
            var unit = Matrix4x4.CreateRotationZ(Radians(reduce ? 0 : (StageShared.Chance(seed, id, 72) - 0.5) * style.Shape.Tilt()))
                * Homography.TurnX(e.Fold, Vector2.Zero, unitSize, 0.6)
                * Matrix4x4.CreateRotationZ(Radians(motion.Spin + e.Turn), new Vector3(bottom, 0))
                * Matrix4x4.CreateScale((float)(motion.Scale * e.Scale), (float)(motion.Scale * e.Scale), 1)
                * Matrix4x4.CreateTranslation((float)(motion.OffsetX + e.OffsetX + frame.MidX), (float)(motion.OffsetY + e.OffsetY + frame.MidY), 0)
                * blockWorld;
            // The accents are drawn flat (a chosen Unfold's fold only turns the word).
            var flat = Homography.Flat(unit);
            var label = Matrix3x2.CreateTranslation(word.LabelOffset) * flat;
            var w = word.Label.Width;
            var h = word.Label.Height;
            var stroke = Math.Max(2 * Pt, size * 0.05);
            if (wear > 0.001) DrawAccent(dc, style.Accent, label, w, h, size, wear, line.RightToLeft, stroke, tile, opacity);
            var onTile = style.Accent == FableAccent.Tile;
            DrawLabel(dc, line, word, run, 0, time, unit, onTile ? (1 - wear) * opacity : opacity, blur);
            if (onTile && wear > 0.001) DrawLabel(dc, line, word, run, 1, time, unit, wear * opacity, blur, 0);
            DrawReading(dc, line, word, run, 0, time, unit, opacity, blur);
            if (style.Accent == FableAccent.Star && wear > 0.001)
            {
                var dir = line.RightToLeft ? -1 : 1;
                var star = Matrix3x2.CreateScale((float)wear) * Matrix3x2.CreateRotation(Radians(reduce ? 0 : (1 - wear) * 90))
                    * Matrix3x2.CreateTranslation((float)(dir * (w / 2 - size * 0.21 + size * 0.12)), (float)(-h / 2 + size * 0.21 - size * 0.08))
                    * label;
                FillStar(dc, star, size * 0.42, tile, opacity);
            }
        }
    }

    private void DrawAccent(ID2D1DeviceContext dc, FableAccent accent, in Matrix3x2 label, double w, double h, double size,
                            double wear, bool rightToLeft, double stroke, Vector4 colour, double opacity)
    {
        switch (accent)
        {
            case FableAccent.Underline:
            {
                var thick = stroke * 1.4;
                // Under Reduce Motion the accents only fade: no growing, shrinking or turning.
                var length = ReduceMotion ? w : w * wear;
                var x = rightToLeft ? w / 2 - length / 2 : -w / 2 + length / 2;
                var at = Matrix3x2.CreateTranslation((float)x, (float)(h / 2 - thick / 2 + size * 0.1)) * label;
                FillRounded(dc, at, length, thick, thick / 2, colour, wear * opacity);
                break;
            }
            case FableAccent.Ring:
            {
                double rw = w + size * 0.36, rh = h + size * 0.2;
                var at = Matrix3x2.CreateRotation(Radians(-3)) * Matrix3x2.CreateScale(ReduceMotion ? 1 : (float)(0.9 + 0.1 * wear)) * label;
                StrokeRounded(dc, at, rw, rh, rh / 2, stroke, colour, wear * 0.95 * opacity, inside: false);
                break;
            }
            case FableAccent.Tile:
                FillRounded(dc, label, w + size * 0.28, h + size * 0.08, size * 0.14, colour, wear * opacity);
                break;
            case FableAccent.Brackets:
            {
                double bw = w + size * 0.32, bh = h + size * 0.12;
                FableLayout.Brackets(new StageRect(-bw / 2, -bh / 2, bw, bh), size * 0.22, _brackets);
                dc.Transform = Matrix3x2.CreateScale(ReduceMotion ? 1 : (float)(1.15 - 0.15 * wear)) * label;
                foreach (var (from, to) in _brackets)
                    Line(dc, new Vector2((float)from.X, (float)from.Y), new Vector2((float)to.X, (float)to.Y), stroke, colour, wear * opacity);
                break;
            }
        }
    }
}
