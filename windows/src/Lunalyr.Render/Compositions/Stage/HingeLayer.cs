// Hinge (the Mac's StageLyricView.hinge in StageLyricStyles.swift; Lunalyr's own code, ported as it is). The corner of
// a cube seen from a slant: the first two words as a tight pair, the third as a big bar on the left face, and from the
// fourth on a tall column down the right face, each word a little bigger than the one above. Both faces meet at the
// vertical edge in the middle and fall away from it in perspective; when the line ends they swing shut edge-on.

using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Vortice.Direct2D1;

namespace Lunalyr.Render.Compositions.Stage;

internal sealed class HingeLayer(IWordBreaker breaker, Logger? logger) : StageLayer(CompositionKind.Hinge, breaker, logger)
{
    private sealed class HingeLine(StageSource source) : StageLine(source)
    {
        /// <summary>The room each word's reading takes under it, at the word's resting size (0 for none).</summary>
        public double[] Slots { get; } = new double[Math.Max(1, source.Runs.Count)];
    }

    // Hinge folds its words shut one after another over most of a second.
    protected override double LeavingSpan => LyricPhraseTiming.HingeExitSpan;

    private static double FontSize(int id, double u) => id < 2 ? u * 0.95 : id == 2 ? u * 1.6 : HingeGeometry.ColumnFont(id - 3, u);

    protected override StageLine Build(StageSource source)
    {
        var line = new HingeLine(source);
        var u = Unit;
        var words = new StageWord[source.Runs.Count];
        for (var i = 0; i < words.Length; i++)
        {
            var run = source.Runs[i];
            var sample = Sample(line, run);
            bool isPair = run.Id < 2, isBar = run.Id == 2;
            var font = FontSize(run.Id, u);
            // At rest the pair is pushed into the corner at a little over half its size, the bar settles at its own.
            var rest = isPair ? 0.55 : isBar ? 1 : 0.85 * 0.86;
            var spec = new WordSpec(font, [isPair ? Colour(sample.Rest, 0.95) : Colour(sample.Sung)])
            {
                Uppercase = true, MaxWidth = Stage.Width * 0.4 / rest, Shadow = Shadow(0.3, 7, 2),
                Raster = isBar ? 2.6f : 2.2f,
            };
            var readingBase = isPair ? HingeGeometry.PairReadingBase(u) : font;
            spec = WithReading(spec, source.Readings[i], readingBase,
                               [isPair ? Colour(sample.Reading, 0.95) : Colour(sample.Reading)],
                               maxWidth: Stage.Width * 0.4 / rest, alignment: isPair ? 0 : run.Id < 3 ? 1 : -1);
            // The pair's readings are sized from a larger base than its own type.
            if (isPair && spec.Reading is not null)
                spec = spec with { ReadingGap = StageShared.ReadingGap(readingBase) };
            words[i] = Bake(run, run.Text, spec);
            line.Slots[i] = source.Readings[i] is null ? 0 : StageShared.ReadingSlot(readingBase, point: Pt);
        }
        line.Words = words;
        return line;
    }

    protected override void DrawLine(ID2D1DeviceContext dc, StageLine stageLine, double time, double exitStart, in Matrix4x4 stage,
                                      double alpha, float blur)
    {
        var line = (HingeLine)stageLine;
        var runs = line.Runs;
        var n = StageMotion.ShownCount(runs, time);
        if (n == 0) return;
        var total = runs.Count;
        var u = Unit;
        var speed = line.Speed;
        var arrivals = line.A.AsSpan(0, n);
        var weights = line.B.AsSpan(0, n);
        for (var i = 0; i < n; i++)
        {
            arrivals[i] = StageMotion.Arrival(runs[i].Start, time, speed);
            weights[i] = StageMotion.Weight(arrivals[i]);
        }
        var hasBar = n > 2;
        var barStart = hasBar ? runs[2].Start : double.PositiveInfinity;
        // How far the bar has settled from huge to its place, and how far the pair has been pushed to the corner.
        var settle = hasBar ? Easing.Smoothstep((time - barStart - 0.25) / 0.5) : 0;
        var pushed = hasBar ? Easing.Smoothstep((time - barStart + 0.05) / 0.4) : 0;
        var columnCount = Math.Max(0, n - 3);
        var (fit, shrink) = HingeGeometry.Fit(line.Texts.AsSpan(0, n), weights, settle, u, Stage, line.ReadingCounts.AsSpan(0, n), Pt);
        var slots = line.Slots;
        const double room = HingeGeometry.ReadingRoom;
        double ColumnScale(int j, ReadOnlySpan<double> a) => shrink * (1 + 0.3 * (1 - a[j + 3]));
        var pairWeight = n > 1 ? weights[1] : 0;
        var pairScale = (1 + (1.3 / 0.95 - 1) * (1 - pairWeight)) * (1 - 0.45 * pushed);
        var barScale = (3.4 - 1.8 * settle) / 1.6;
        var firstColumn = columnCount > 0 ? weights[3] : 0;
        // The faces swing shut as the words fold away, all within the line's exit.
        var close = LyricMotion.Settle(time - exitStart, StageMotion.HingeClose);
        var faceAngle = 52 + (88 - 52) * close;
        var words = line.Words;
        var bounds = Bounds;
        double edge = bounds.MidX, gap = u * 0.12;
        var centres = line.Places;

        // The left face: the pair and the bar.
        var leftCount = Math.Min(3, n);
        var barSize = leftCount > 2 ? new SizeD(words[2].Size.Width * barScale, words[2].Size.Height * barScale) : SizeD.Zero;
        double totalColumn = 0;
        for (var j = 0; j < columnCount; j++)
            totalColumn += (HingeGeometry.ColumnFont(j, u) * shrink * 0.95 + slots[j + 3] * room * shrink) * weights[j + 3];
        // The bar moves up beside the column as its first word arrives.
        var top = bounds.MidY - (u + (totalColumn / 2 - u) * firstColumn);
        for (var i = 0; i < leftCount; i++) centres[i] = new PointD(edge / 2, bounds.MidY);
        var barSlot = leftCount > 2 ? slots[2] * barScale : 0;
        var barWord = barSize.Height - barSlot;
        if (leftCount > 2)
        {
            var settledX = edge - gap - barSize.Width / 2;
            var settledY = bounds.MidY + (top + barWord * 0.45 - bounds.MidY) * firstColumn;
            centres[2] = new PointD(edge / 2 + (settledX - edge / 2) * settle, bounds.MidY + (settledY - bounds.MidY) * settle);
        }
        // The pair's words overlap a little; their readings do not.
        double PairRoom(int index, StageWord[] w, int count) =>
            index < count ? (w[index].Size.Height - slots[index]) * 0.78 + slots[index] * room : PairRoom(0, w, count);
        var pairRead = slots[0] > 0 || (n > 1 && slots[1] > 0);
        var pairHeight = (pairRead ? (PairRoom(0, words, leftCount) + PairRoom(1, words, leftCount)) / 2 : PairRoom(0, words, leftCount)) * pairScale;
        var home = new PointD(edge / 2, bounds.MidY);
        // A reading under the pair's lower word lifts the pair clear of the bar.
        var lowerSlot = leftCount > 1 ? slots[1] * room * pairScale : 0;
        var corner = leftCount > 2 ? new PointD(centres[2].X + barSize.Width * 0.28, centres[2].Y - barWord * 0.62 - lowerSlot) : home;
        var spot = new PointD(home.X + (corner.X - home.X) * pushed, home.Y + (corner.Y - home.Y) * pushed);
        var split = pairHeight * 0.5 * pairWeight;
        centres[0] = new PointD(spot.X, spot.Y - split);
        if (leftCount > 1) centres[1] = new PointD(spot.X, spot.Y + split * (1 - 0.25 * pushed));
        if (leftCount > 2) centres[2] = centres[2] with { Y = centres[2].Y + barSlot / 2 };

        // The right face: the column, each word taking its room as it arrives.
        var heights = line.C.AsSpan(0, Math.Max(1, columnCount));
        double sum = 0;
        for (var k = 0; k < columnCount; k++)
        {
            heights[k] = ((words[k + 3].Size.Height - slots[k + 3]) * 0.9 + slots[k + 3] * room) * ColumnScale(k, arrivals) * weights[k + 3];
            sum += heights[k];
        }
        var cursor = bounds.MidY - sum / 2;
        for (var k = 0; k < columnCount; k++)
        {
            centres[k + 3] = new PointD(edge + gap + words[k + 3].Size.Width * ColumnScale(k, arrivals) / 2, cursor + heights[k] / 2);
            cursor += heights[k];
        }

        // Looked at from a little above, the whole corner fitted to the stage.
        var middle = new Vector2((float)bounds.MidX, (float)bounds.MidY);
        var whole = Homography.TurnX(9, middle, Stage, 0.5)
            * Matrix4x4.CreateScale((float)fit, (float)fit, 1, new Vector3(middle, 0)) * stage;
        var leftFace = Homography.TurnY(faceAngle, middle, Stage, 0.8) * whole;
        var rightFace = Homography.TurnY(-faceAngle, middle, Stage, 0.8) * whole;
        for (var id = 0; id < n; id++)
        {
            var isLeft = id < 3;
            bool isPair = id < 2, isBar = id == 2;
            var word = words[id];
            var a = arrivals[id];
            var scale = isPair ? pairScale : isBar ? barScale : ColumnScale(id - 3, arrivals);
            // Words fold away oldest first.
            var fold = StageMotion.HingeFold((double)id / Math.Max(total - 1, 1), time, exitStart);
            var opacity = Math.Min(1, a * 1.6) * (1 - fold) * alpha;
            if (!(opacity > 0.002)) continue;
            var sx = (float)(scale * (isBar || isPair ? 1 : 0.86));
            var sy = (float)(scale * (isBar || isPair ? 1 : 1.1));
            // Each word unfolds from the edge like a panel on a hinge, with a little spring; on the way out they fold
            // shut one after another.
            var angle = (isLeft ? 1 : -1) * (84 * (1 - a) - 80 * fold);
            var anchor = new Vector2((float)((isLeft ? 1 : -1) * word.Size.Width / 2), 0);
            var unit = Matrix4x4.CreateScale(sx, sy, 1)
                * Homography.TurnY(angle, anchor, word.Size, 0.6)
                * Matrix4x4.CreateTranslation((float)centres[id].X, (float)(centres[id].Y + u * 0.25 * fold), 0)
                * (isLeft ? leftFace : rightFace);
            var run = runs[id];
            // (Drawn in perspective, so without its arrival blur: Direct2D turns bitmaps, not effects, in depth.)
            var wordBlur = blur + (float)(FontSize(id, u) * scale * fit * 0.08 * (1 - Math.Min(a, 1)));
            DrawLabel(dc, line, word, run, 0, time, unit, opacity, wordBlur);
            // The bar arrives huge and settles into place; its reading comes once it is nearly there.
            var shown = isBar ? Easing.Smoothstep((settle - 0.5) / 0.5) : 1;
            DrawReading(dc, word, 0, unit, opacity * shown, wordBlur);
        }
    }
}
