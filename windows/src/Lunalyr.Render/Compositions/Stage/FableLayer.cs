// Fable (the Mac's StageLayouts/FableLayout.swift, Build 88: fable, fablePage, fableWord, fableFace, FableStroke; Lunalyr's
// own code, ported as it is). A storybook page per line, set in a shape chosen for the line (an arc, a stair, a wave or
// a plain centred block), one phrase per row, so the page breaks where the singer breathes. Each phrase's focal word
// makes the big landing (it pops up from its foot with a swing, or drops in from above, alternating phrase by phrase),
// the line's hero lands biggest of all, content words hop up into place, and little and held words are written in with
// a soft-edged pen stroke. A long line turns the page by phrase (at most two phrases a page): the old page folds away
// about its spine before the next is begun. Nothing moves once landed, and nothing decorates a word. The choreography is
// FableGeometry's.

using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Lunalyr.Render.Compositions.Stage;

internal sealed class FableLayer(IWordBreaker breaker, Logger? logger) : StageLayer(CompositionKind.Fable, breaker, logger)
{
    private ID2D1GradientStopCollection? _penStops;
    private ID2D1LinearGradientBrush? _pen;

    private sealed class FableLine(StageSource source) : StageLine(source)
    {
        public required FablePlan Plan { get; init; }
        public FableStyle Style { get; init; }
        public double[] Size { get; } = new double[Math.Max(1, source.Runs.Count)];
        /// <summary>Each word's box (the word, its reading under it, and room for its landing), and where its label sits in it.</summary>
        public SizeD[] Box { get; } = new SizeD[Math.Max(1, source.Runs.Count)];
        public Vector2[] LabelAt { get; } = new Vector2[Math.Max(1, source.Runs.Count)];
        public Vector2[] ReadingAt { get; } = new Vector2[Math.Max(1, source.Runs.Count)];
        /// <summary>Each page's size and its words' boxes (from the page's top-left), set once.</summary>
        public SizeD[] PageSize { get; set; } = [];
        public StageRect[][] Frames { get; set; } = [];
    }

    // Fable draws Reduce Motion itself: its words and pages fade, nothing travels.
    protected override bool OwnsReduceMotion => true;

    protected override void CreateShapes(ID2D1DeviceContext dc, ID2D1Factory factory)
    {
        // The pen's soft edge: solid ink, then clear (placed along each word as it is written).
        GradientStop[] stops = [new(0, new Color4(0, 0, 0, 1)), new(1, new Color4(0, 0, 0, 0))];
        _penStops = dc.CreateGradientStopCollection(stops, Gamma.StandardRgb, ExtendMode.Clamp);
        _pen = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, Vector2.UnitX), _penStops);
    }

    protected override void DisposeOwnShapes()
    {
        _pen?.Dispose(); _pen = null;
        _penStops?.Dispose(); _penStops = null;
    }

    protected override StageLine Build(StageSource source)
    {
        var runs = source.Runs;
        var plan = FableGeometry.Plan(runs);
        var line = new FableLine(source) { Plan = plan, Style = FableStyle.ForLine(source.Row) };
        var u = Unit;
        var words = new StageWord[runs.Count];
        var baselines = new double[runs.Count];
        var baseline = HeavyBaseline();
        // One shadow for the page, not one per word: soft and close.
        var shadow = new ShadowSpec(0.3f, (float)Math.Max(3 * Pt, u * 0.08) * 0.5f, Points(2));
        for (var i = 0; i < words.Length; i++)
        {
            var run = runs[i];
            var sample = Sample(line, run);
            var role = i < plan.Roles.Length ? plan.Roles[i] : FableRole.Content;
            var size = FableGeometry.Size(role, runs.Count, u);
            line.Size[i] = size;
            var spec = new WordSpec(size, [Colour(sample.Rest, 0.86), Colour(sample.Sung)]) { Shadow = shadow, Raster = 1.15f };
            if (source.Readings[i] is { } reading)
            {
                spec = spec with
                {
                    Reading = reading, ReadingSize = Math.Max(12 * Pt, size * 0.36), ReadingGap = size * 0.06,
                    ReadingColours = [Colour(sample.Rest, 0.9)], ReadingShadow = shadow,
                };
            }
            var word = Bake(run, run.Text, spec);
            words[i] = word;
            // Room for the landings, inside the word's own measured box.
            var tight = LyricScript.KeepsSyllables(run.Text);
            double padX = size * (tight ? 0.12 : 0.18), padY = size * 0.14;
            line.Box[i] = new SizeD(word.Size.Width + 2 * padX, word.Size.Height + 2 * padY);
            line.LabelAt[i] = word.LabelOffset;
            line.ReadingAt[i] = word.ReadingOffset;
            // The baseline from the box's top: the row's words sit on it.
            baselines[i] = padY + word.Label.Height * baseline;
        }
        line.Words = words;
        line.PageSize = new SizeD[plan.Pages.Length];
        line.Frames = new StageRect[plan.Pages.Length][];
        for (var k = 0; k < plan.Pages.Length; k++)
        {
            var page = plan.Pages[k];
            var sizes = page.Runs.Select(id => line.Box[id]).ToArray();
            var lines = page.Runs.Select(id => baselines[id]).ToArray();
            var laid = FableGeometry.Arrange(sizes, page.RowSlots, line.Style.Shape, plan.RightToLeft, u * 0.1, u * 0.1, lines);
            line.PageSize[k] = laid.Size;
            line.Frames[k] = laid.Frames;
        }
        return line;
    }

    protected override void DrawLine(ID2D1DeviceContext dc, StageLine stageLine, double time, double exitStart, in Matrix4x4 stage,
                                     double alpha, float blur, int shown)
    {
        var line = (FableLine)stageLine;
        var plan = line.Plan;
        if (plan.Pages.Length == 0 || !(alpha > 0.002)) return;
        var reduce = ReduceMotion;
        var u = Unit;
        // At least the 5 % title-safe margin on every side.
        var marginX = Math.Max(u * 0.5, Stage.Width * 0.05);
        var marginY = Math.Max(u * 0.5, Stage.Height * 0.05);
        var available = new SizeD(Math.Max(1, Stage.Width - 2 * marginX), Math.Max(1, Stage.Height - 2 * marginY));
        var flat = Homography.Flat(stage);
        for (var k = 0; k < plan.Pages.Length; k++)
        {
            if (!FableGeometry.PageShown(k, plan, time, exitStart)) continue;
            var turn = FableGeometry.PageTurn(k, plan, time, exitStart);
            var size = line.PageSize[k];
            // Long pages and big readings are shrunk to fit, never cut off.
            var fit = LyricStageFit.Scale(size, available);
            // The page folds away about its spine (the edge reading starts from), darkening.
            var spine = (float)(plan.RightToLeft ? Stage.Width / 2 + size.Width / 2 : Stage.Width / 2 - size.Width / 2);
            var page = Matrix3x2.CreateTranslation(-(float)(size.Width / 2), -(float)(size.Height / 2))
                * Matrix3x2.CreateScale((float)fit)
                * Matrix3x2.CreateTranslation((float)(Stage.Width / 2), (float)(Stage.Height / 2))
                * Matrix3x2.CreateScale(reduce ? 1 : (float)Math.Max(0.001, 1 - turn * 0.85), 1, new Vector2(spine, 0)) * flat;
            var opacity = (1 - turn) * (reduce ? 1 : 1 - 0.3 * turn) * alpha;
            if (!(opacity > 0.002)) continue;
            var runsOnPage = plan.Pages[k].Runs;
            var frames = line.Frames[k];
            for (var slot = 0; slot < runsOnPage.Length; slot++)
                DrawWord(dc, line, runsOnPage[slot], frames[slot], page, time, opacity, blur);
        }
    }

    private void DrawWord(ID2D1DeviceContext dc, FableLine line, int id, StageRect frame, in Matrix3x2 page, double time, double opacity,
                          float blur)
    {
        var plan = line.Plan;
        var run = line.Runs[id];
        if (!(time >= run.Start - StageMotion.Lead)) return;
        var reduce = ReduceMotion;
        var size = line.Size[id];
        var role = plan.Roles[id];
        var entrance = StageShared.ChosenEntrance(ChosenFor(run), StageMotion.Arrival(run.Start, time), size, id % 2 == 0 ? 1 : -1, reduce);
        var native = entrance is null && !reduce;
        // Reduce Motion, or another entrance chosen: a fade on the word's beat instead of the stroke.
        var fade = Easing.Smoothstep((time - (run.Start - StageMotion.Lead)) / WallpaperMotion.ReducedDissolve);
        var landing = FableGeometry.LandingFor(plan.PhraseOfRun[id], line.Seed);
        var pose = native ? FableGeometry.Pose(run, role, landing, plan.LineTimed, plan.Clocks[id], time) : FablePose.Rest;
        var side = plan.RightToLeft ? -1.0 : 1.0;
        var alpha = (native ? 1 : fade) * opacity;
        if (!(alpha > 0.002)) return;
        var box = line.Box[id];
        // About the word's foot: its landing, then any chosen entrance.
        var foot = new Vector2(0, (float)(box.Height / 2));
        var landed = Matrix3x2.CreateScale((float)pose.Scale, foot)
            * Matrix3x2.CreateRotation(Radians(pose.Spin * side), foot)
            * Matrix3x2.CreateTranslation(0, (float)(size * pose.Lift));
        Matrix4x4 unit;
        var centre = new Vector2((float)frame.MidX, (float)frame.MidY);
        if (entrance is { } e)
        {
            unit = Homography.Of(landed) * Homography.TurnX(e.Fold, Vector2.Zero, box, 0.6)
                * Matrix4x4.CreateRotationZ(Radians(e.Turn), new Vector3(foot, 0))
                * Matrix4x4.CreateScale((float)e.Scale, (float)e.Scale, 1)
                * Matrix4x4.CreateTranslation(centre.X + (float)e.OffsetX, centre.Y + (float)e.OffsetY, 0) * Homography.Of(page);
        }
        else
        {
            unit = Homography.Of(landed * Matrix3x2.CreateTranslation(centre) * page);
        }
        var word = line.Words[id];
        var live = TorqueGeometry.Live(line.Runs, id, time);
        // Written in by the pen: a soft-edged, slightly slanted wipe in the reading direction.
        var written = native && plan.Writes[id] ? FableGeometry.Written(plan.Strokes[id], time) : 1;
        if (!(written > 0)) return;
        var masked = written < 1 && _pen is not null && Homography.IsAffine(unit);
        if (masked)
        {
            var (solid, clear) = FableGeometry.StrokeStops(written);
            if (clear - solid < 1e-4) clear = solid + 1e-4;
            double left = -box.Width / 2, top = -box.Height / 2;
            Vector2 Along(double t)
            {
                var x = plan.RightToLeft ? 1 - t : t;
                return new Vector2((float)(left + x * box.Width), (float)(top + (0.35 + 0.3 * t) * box.Height));
            }
            dc.Transform = Homography.Flat(unit);
            _pen!.StartPoint = Along(solid);
            _pen.EndPoint = Along(clear);
            dc.PushLayer(new LayerParameters1
            {
                ContentBounds = new RawRectF((float)(left - box.Width), (float)(top - box.Height), (float)(box.Width * 1.5), (float)(box.Height * 1.5)),
                MaskTransform = Matrix3x2.Identity, MaskAntialiasMode = AntialiasMode.PerPrimitive, Opacity = 1,
                OpacityBrush = _pen, LayerOptions = LayerOptions1.None,
            }, null!);
        }
        try
        {
            if (live < 0.999) DrawLabel(dc, line, word, run, 0, time, unit, alpha, blur);
            if (live > 0.001) DrawLabel(dc, line, word, run, 1, time, unit, live * alpha, blur, live < 0.999 ? 0 : 1);
            DrawReading(dc, line, word, run, 0, time, unit, alpha, blur);
        }
        finally
        {
            if (masked)
            {
                dc.Transform = Homography.Flat(unit);
                dc.PopLayer();
            }
        }
    }
}
