// Torque (the Mac's StageLayouts/TorqueLayout.swift, Build 88: torque, torqueFace, torqueWord, TorqueTurn,
// TorqueFaceLayout; Lunalyr's own code, ported as it is). A square column of heavy, extruded type turning about a
// vertical axis at the stage's centre. Each phrase is one face of the column, tipped ±4°, the other way from its
// neighbours: while it is sung its face looks straight out and its words are wrung into place on it; when the next
// phrase starts, the whole column torques a quarter turn and the next face swings in. The focal word gets the full wring
// (it arrives edge-on and unwinds, its thick edge growing in after it); content words rise a little; function words cut
// in. Every turn is a flat 2D transform (a horizontal squash and a slight shear). The choreography is TorqueGeometry's.

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
        public required TorquePlan Plan { get; init; }
        public double[] Size { get; } = new double[Math.Max(1, source.Runs.Count)];
        /// <summary>Each word's box on its face: the word, room for its thick edge, and its reading under it.</summary>
        public SizeD[] Box { get; } = new SizeD[Math.Max(1, source.Runs.Count)];
        /// <summary>Where the label and the reading sit in that box, from its centre.</summary>
        public Vector2[] LabelAt { get; } = new Vector2[Math.Max(1, source.Runs.Count)];
        public Vector2[] ReadingAt { get; } = new Vector2[Math.Max(1, source.Runs.Count)];
        /// <summary>The thick edge's two shades of the sung colour: nearer and further back.</summary>
        public Ink[][] Edges { get; set; } = [];
        /// <summary>Each face's size and its words' boxes (from the face's top-left), set once.</summary>
        public SizeD[] FaceSize { get; set; } = [];
        public StageRect[][] Frames { get; set; } = [];

        public override void Dispose()
        {
            base.Dispose();
            foreach (var edge in Edges)
            foreach (var ink in edge) ink.Dispose();
        }
    }

    protected override StageLine Build(StageSource source)
    {
        var runs = source.Runs;
        var plan = TorqueGeometry.Plan(runs);
        var line = new TorqueLine(source) { Plan = plan };
        var u = Unit;
        var words = new StageWord[runs.Count];
        var edges = new Ink[runs.Count][];
        var baseline = HeavyBaseline();
        var baselines = new double[runs.Count];
        for (var i = 0; i < words.Length; i++)
        {
            var run = runs[i];
            var role = i < plan.Roles.Length ? plan.Roles[i] : TorqueRole.Content;
            var size = TorqueGeometry.Size(role, u);
            var sample = Sample(line, run);
            var sung = Colour(sample.Sung);
            var spec = WithReading(new WordSpec(size, [Colour(sample.Rest, 0.86), sung])
            {
                Accent = Accent(source, i),
                MaxWidth = Stage.Width * 0.8, MinimumScale = 0.35, Shadow = Shadow(0.28, Math.Max(3, u * 0.1 / Pt), u * 0.05 / Pt), Raster = 1.15f,
            }, source.Readings[i], size, [Colour(sample.Reading), Colour(sample.Sung, LyricHighlight.ReadingOpacity)],
               gap: size * 0.04, maxWidth: Stage.Width * 0.8);
            var word = Bake(run, run.Text, spec);
            words[i] = word;
            // The thick edge: the word's own colour in shade, darker the further back it steps.
            edges[i] = [.. BakeEdge(run, size, Shade(sung, 0.5 - 0.22 * 0.35)), .. BakeEdge(run, size, Shade(sung, 0.5 - 0.22 * 0.85))];
            line.Size[i] = size;
            // Room for the thick edge inside the word's own box (trailing, and below), and its reading under it.
            var depth = TorqueGeometry.Depth(role);
            var padX = size * depth * 0.9;
            var padY = size * depth * 1.4;
            var label = word.Label;
            var readingHeight = word.Size.Height - label.Height;
            double boxW = Math.Max(label.Width + padX, word.HasReading ? word.Size.Width : 0), wordH = label.Height + padY;
            line.Box[i] = new SizeD(boxW, wordH + readingHeight);
            var side = plan.RightToLeft ? 1 : -1;
            line.LabelAt[i] = new Vector2((float)(side * padX / 2), (float)(-line.Box[i].Height / 2 + label.Height / 2));
            line.ReadingAt[i] = new Vector2(0, (float)(line.Box[i].Height / 2 - readingHeight / 2));
            baselines[i] = label.Height * baseline;
        }
        line.Words = words;
        line.Edges = edges;
        // Each face set once: rows sharing a baseline, flush to one side.
        line.FaceSize = new SizeD[plan.Faces.Length];
        line.Frames = new StageRect[plan.Faces.Length][];
        for (var k = 0; k < plan.Faces.Length; k++)
        {
            var face = plan.Faces[k];
            var sizes = face.Runs.Select(id => line.Box[id]).ToArray();
            var lines = face.Runs.Select(id => baselines[id]).ToArray();
            var laid = TorqueGeometry.ArrangeFace(sizes, lines, face.RowSlots, face.FlushTrailing != plan.RightToLeft, plan.RightToLeft,
                                                  u * 0.24, -u * 0.06);
            line.FaceSize[k] = laid.Size;
            line.Frames[k] = laid.Frames;
        }
        return line;
    }

    private Ink[] BakeEdge(Core.Motion.StageRun run, double size, Vector4 colour)
    {
        var word = Bake(run, run.Text, new WordSpec(size, [colour]) { MaxWidth = Stage.Width * 0.8, MinimumScale = 0.35, Raster = 1.0f });
        var inks = word.Labels;
        foreach (var reading in word.Readings) reading.Dispose();
        return inks;
    }

    private static Vector4 Shade(Vector4 colour, double white) =>
        new(colour.X * (float)white, colour.Y * (float)white, colour.Z * (float)white, colour.W);

    protected override void DrawLine(ID2D1DeviceContext dc, StageLine stageLine, double time, double exitStart, in Matrix4x4 stage,
                                     double alpha, float blur, int shown)
    {
        var line = (TorqueLine)stageLine;
        var plan = line.Plan;
        if (plan.Faces.Length == 0) return;
        var reduce = ReduceMotion;
        var rho = TorqueGeometry.Rotation(plan, time, shown, reduce);
        var swing = TorqueGeometry.Swing(plan, time, exitStart, reduce);
        var leaving = TorqueGeometry.Leaving(time, exitStart);
        var whole = (1 - leaving) * alpha;
        if (!(whole > 0.002)) return;
        // Inside the 5 % title-safe margins.
        var available = new SizeD(Stage.Width * 0.9, Stage.Height * 0.9);
        var direction = plan.RightToLeft ? -1.0 : 1.0;
        var flat = Homography.Flat(stage);
        for (var k = 0; k < plan.Faces.Length; k++)
        {
            if (!(Math.Abs(k - rho) < 0.999)) continue;
            var angle = (k - rho) * 90 * direction + swing * direction;
            var cos = Math.Cos(Math.Min(Math.Abs(angle), 90) * Math.PI / 180);
            // A face turned away darkens and fades.
            var faceOpacity = Math.Pow(Math.Max(0, cos), 1.5) * (1 - 0.45 * (1 - cos)) * whole;
            if (!(faceOpacity > 0.002)) continue;
            var size = line.FaceSize[k];
            var turn = TorqueGeometry.Turn(angle, size, available);
            // Each face is set a little tipped, the other way from its neighbours; the turn carries it round the column.
            var tip = reduce ? 0 : (plan.Faces[k].FlushTrailing ? TorqueGeometry.Tip : -TorqueGeometry.Tip);
            var face = Matrix3x2.CreateRotation(Radians(tip), new Vector2((float)(size.Width / 2), (float)(size.Height / 2)))
                * new Matrix3x2((float)turn.A, (float)turn.B, (float)turn.C, (float)turn.D, (float)turn.Tx, (float)turn.Ty)
                * Matrix3x2.CreateTranslation((float)(Stage.Width / 2 - size.Width / 2), (float)(Stage.Height / 2 - size.Height / 2)) * flat;
            var runsOnFace = plan.Faces[k].Runs;
            var frames = line.Frames[k];
            for (var slot = 0; slot < runsOnFace.Length; slot++)
                DrawWord(dc, line, runsOnFace[slot], frames[slot], face, time, faceOpacity, blur);
        }
    }

    private void DrawWord(ID2D1DeviceContext dc, TorqueLine line, int id, StageRect frame, in Matrix3x2 face, double time,
                          double opacity, float blur)
    {
        var plan = line.Plan;
        var run = line.Runs[id];
        var role = plan.Roles[id];
        var size = line.Size[id];
        var reduce = ReduceMotion;
        var pose = TorqueGeometry.Pose(run, role, plan.Clocks[id], plan.Held[id], plan.LineTimed, time, reduce);
        if (!(pose.Opacity * opacity > 0.002)) return;
        var side = plan.RightToLeft ? -1.0 : 1.0;
        // A chosen entrance (not the layout's own) moves the word as everywhere else.
        var entrance = StageShared.ChosenEntrance(ChosenFor(run), StageMotion.Arrival(run.Start, time), size, id % 2 == 0 ? 1 : -1, reduce);
        var native = entrance is null || plan.LineTimed;
        var box = line.Box[id];
        var centre = new Vector2((float)frame.MidX, (float)frame.MidY);
        Matrix4x4 unit;
        if (native)
        {
            // Wrung about its own axis, punched in and risen into place.
            var wring = TorqueGeometry.Turn(pose.Wring * side, box, null);
            var local = Matrix3x2.CreateTranslation((float)(box.Width / 2), (float)(box.Height / 2))
                * new Matrix3x2((float)wring.A, (float)wring.B, (float)wring.C, (float)wring.D, (float)wring.Tx, (float)wring.Ty)
                * Matrix3x2.CreateTranslation(-(float)(box.Width / 2), -(float)(box.Height / 2))
                * Matrix3x2.CreateScale((float)pose.Scale)
                * Matrix3x2.CreateTranslation(centre.X, centre.Y + (float)(size * pose.Rise)) * face;
            unit = Homography.Of(local);
        }
        else
        {
            var e = entrance!.Value;
            unit = Homography.TurnX(e.Fold, Vector2.Zero, box, 0.6)
                * Matrix4x4.CreateRotationZ(Radians(e.Turn))
                * Matrix4x4.CreateScale((float)e.Scale, (float)e.Scale, 1)
                * Matrix4x4.CreateTranslation(centre.X + (float)e.OffsetX, centre.Y + (float)e.OffsetY, 0) * Homography.Of(face);
        }
        var word = line.Words[id];
        var alpha = pose.Opacity * opacity;
        var live = TorqueGeometry.Live(line.Runs, id, time);
        // The thick edge: the word's own colour in shade, stepping back behind it.
        var steps = TorqueGeometry.Steps(role);
        var depth = size * TorqueGeometry.Depth(role) * pose.Depth;
        if (depth > 0.2 * Pt)
        {
            var parts = word.Boundaries is null ? default : line.PartOpacities(run, time);
            // The edge moves with an action word's motion, as the word does.
            var edgeUnit = Acting(word, run, time, unit, line.LabelAt[id] - word.LabelOffset, out var acting);
            var edgeAlpha = alpha * acting.Opacity;
            for (var step = steps; step >= 1; step--)
            {
                var share = (double)step / steps;
                var shade = line.Edges[id][share > 0.6 ? 1 : 0];
                var nudge = word.LabelOffset + new Vector2((float)(side * depth * share * 0.55), (float)(depth * share));
                DrawInk(dc, shade, edgeUnit, line.LabelAt[id] - word.LabelOffset + nudge, edgeAlpha, blur, 0, word.Boundaries, parts, word.RightToLeft);
            }
        }
        var labelNudge = line.LabelAt[id] - word.LabelOffset;
        var tint = SungTint(line, run, time, live);
        if (tint < 0.999) DrawLabel(dc, line, word, run, 0, time, unit, alpha, blur, 1, labelNudge);
        if (tint > 0.001) DrawLabel(dc, line, word, run, 1, time, unit, tint * alpha, blur, tint < 0.999 ? 0 : 1, labelNudge);
        if (word.HasReading)
        {
            // The reading takes the sung colour while its word is being sung (in Sung sweep, from then on).
            var readingUnit = Matrix4x4.CreateTranslation(0, line.ReadingAt[id].Y - word.ReadingOffset.Y, 0) * unit;
            if (tint < 0.999) DrawReading(dc, line, word, run, 0, time, readingUnit, alpha, blur);
            if (tint > 0.001) DrawReading(dc, line, word, run, 1, time, readingUnit, tint * alpha, blur);
        }
    }
}
