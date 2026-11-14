// Hinge (the Mac's StageLayouts/HingeLayout.swift, Build 88: hinge, hingePage, hingeLine, hingeWord, HingeCap; Lunalyr's
// own code, ported as it is). An open book seen straight on, its spine a vertical axis. Each phrase is one page of heavy
// caps: its focal word set huge and flush to the spine, the words before it a small line along the page's head, the
// words after it a small line at its foot. When the next phrase starts, the page turns over on the spine and lands,
// dimmed, on the other side; at most two pages show. The focal word bursts in oversized and blurred as it swings open on
// the spine; small content words flip open, a quick run as one group; function words just ink. The line leaves by
// closing the book: both pages fold shut onto the spine, accelerating, and the closed book recedes in the reading
// direction; the next line's book arrives still moving that way. The choreography is HingeGeometry's.

using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Vortice.Direct2D1;

namespace Lunalyr.Render.Compositions.Stage;

internal sealed class HingeLayer(IWordBreaker breaker, Logger? logger) : StageLayer(CompositionKind.Hinge, breaker, logger)
{
    private readonly List<HingeInstance> _instances = [];

    private sealed class HingeLine(StageSource source) : StageLine(source)
    {
        public required StagePhrasing Phrasing { get; init; }
        public required List<int[]> Pages { get; init; }
        public double Spine { get; init; }
        public double[] PageStarts { get; init; } = [];
        public double FirstStart { get; init; }
        public double[] Size { get; } = new double[Math.Max(1, source.Runs.Count)];
        /// <summary>Each word's centre (stage pixels) when its page is on its own side (0) or turned over (1).</summary>
        public PointD[][] At { get; } = [new PointD[Math.Max(1, source.Runs.Count)], new PointD[Math.Max(1, source.Runs.Count)]];
        /// <summary>When the quick run each word belongs to swings in as one (its own onset otherwise).</summary>
        public double[] GroupStart { get; } = new double[Math.Max(1, source.Runs.Count)];
        public HingeEntrance[] Entrance { get; } = new HingeEntrance[Math.Max(1, source.Runs.Count)];
        public double?[] Held { get; } = new double?[Math.Max(1, source.Runs.Count)];
        public bool[] Begun { get; set; } = [];
    }

    // Hinge draws Reduce Motion itself: no turns or swings, the pages and words cross-fade.
    protected override bool OwnsReduceMotion => true;

    protected override StageLine Build(StageSource source)
    {
        var runs = source.Runs;
        var phrasing = StagePhrases.Phrasing(runs);
        var pages = HingeGeometry.Pages(phrasing);
        var rtl = phrasing.RightToLeft;
        var width = Stage.Width;
        var spine = HingeGeometry.Spine(width, pages.Count == 1, rtl);
        var line = new HingeLine(source)
        {
            Phrasing = phrasing, Pages = pages, Spine = spine,
            PageStarts = [.. pages.Select(page => page.Min(k => phrasing.Phrases[k].Start))],
            FirstStart = pages.Count == 0 ? 0 : pages.Min(page => page.Min(k => phrasing.Phrases[k].Start)),
        };
        line.Begun = new bool[pages.Count];
        var u = Unit;
        var own = rtl ? HingeSide.Left : HingeSide.Right;
        var room = HingeGeometry.PageWidth(width, spine, own, u);
        var small = HingeGeometry.SmallSize(u);
        var words = new StageWord[runs.Count];
        foreach (var page in pages)
        {
            var slabs = page.Select(k => phrasing.Phrases[k]).ToList();
            var shape = slabs.Select(phrase => new HingeSlab(
                phrase.Focal > phrase.From, phrase.Focal < phrase.To - 1, source.Readings[phrase.Focal] is not null,
                phrase.Runs.Any(id => id != phrase.Focal && source.Readings[id] is not null))).ToList();
            var held = slabs.Count == 1 && phrasing.Words[slabs[0].Focal].IsHeld;
            var focalSize = HingeGeometry.FocalSize(shape, held, u, Stage.Height);
            foreach (var phrase in slabs)
            foreach (var id in phrase.Runs)
            {
                var run = runs[id];
                var info = phrasing.Words[id];
                var focal = info.IsFocal;
                var size = focal ? focalSize : small;
                var sample = Sample(line, run);
                // A word at its own width, never wider than its page: too wide, it shrinks to it.
                var spec = WithReading(new WordSpec(size, [Colour(sample.Sung)])
                {
                    Uppercase = true, MaxWidth = room, MinimumScale = focal ? 0.3 : 0.5,
                    Shadow = Shadow(0.3, Math.Max(2, size * 0.06 / Pt), 2), Raster = focal ? 1.6f : 1.4f,
                }, source.Readings[id], size, [Colour(sample.Reading)], maxWidth: room, alignment: own == HingeSide.Right ? -1 : 1);
                words[id] = Bake(run, run.Text, spec);
                line.Size[id] = size;
                line.Entrance[id] = HingeGeometry.EntranceOf(info);
                line.Held[id] = info.IsHeld ? info.Duration : null;
                var groupStart = run.Start;
                if (info.RunGroup is { } group)
                    foreach (var other in phrasing.Words)
                        if (other.RunGroup == group && other.Run < runs.Count) groupStart = Math.Min(groupStart, runs[other.Run].Start);
                line.GroupStart[id] = groupStart;
            }
        }
        line.Words = words;
        // Each page set on both sides of the spine: on its own side while it is sung, turned over once read.
        foreach (var page in pages)
        {
            PlacePage(line, page, words, own, 0);
            PlacePage(line, page, words, own == HingeSide.Right ? HingeSide.Left : HingeSide.Right, 1);
        }
        return line;
    }

    /// <summary>A page's slabs (one phrase each) stacked, flush to the spine, centred down the stage.</summary>
    private void PlacePage(HingeLine line, int[] page, StageWord[] words, HingeSide side, int copy)
    {
        var u = Unit;
        var phrasing = line.Phrasing;
        var rtl = phrasing.RightToLeft;
        var small = HingeGeometry.SmallSize(u);
        var gutter = u * HingeGeometry.Gutter;
        var room = HingeGeometry.PageWidth(Stage.Width, line.Spine, side, u);
        var at = line.At[copy];
        // The rows of the page, top to bottom: a head line, the focal word, a foot line, for each slab.
        var rows = new List<(int[] Ids, double Gap)>();
        foreach (var k in page)
        {
            var phrase = phrasing.Phrases[k];
            var first = rows.Count == 0;
            var slabGap = first ? 0 : u * 0.5;
            if (phrase.Focal > phrase.From)
            {
                rows.Add((Ordered(phrase.From, phrase.Focal, rtl), slabGap));
                slabGap = u * 0.06;
            }
            rows.Add(([phrase.Focal], slabGap));
            if (phrase.Focal < phrase.To - 1) rows.Add((Ordered(phrase.Focal + 1, phrase.To, rtl), u * 0.06));
        }
        double total = 0;
        var heights = new double[rows.Count];
        var scales = new double[rows.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            var (ids, gap) = rows[r];
            double width = 0, height = 0;
            foreach (var id in ids) { width += words[id].Size.Width; height = Math.Max(height, words[id].Size.Height); }
            width += small * 0.34 * (ids.Length - 1);
            // A small line wider than its page is shrunk to it.
            scales[r] = ids.Length > 1 && width > room ? Math.Max(0.5, room / width) : 1;
            heights[r] = height * scales[r];
            total += gap + heights[r];
        }
        var y = Stage.Height / 2 - total / 2;
        for (var r = 0; r < rows.Count; r++)
        {
            var (ids, gap) = rows[r];
            y += gap;
            var scale = scales[r];
            double width = small * 0.34 * (ids.Length - 1);
            foreach (var id in ids) width += words[id].Size.Width;
            width *= scale;
            // Flush to the spine: from its gutter outwards.
            var x = side == HingeSide.Right ? line.Spine + gutter : line.Spine - gutter - width;
            foreach (var id in ids)
            {
                var w = words[id].Size.Width * scale;
                // Top-aligned in its row.
                at[id] = new PointD(x + w / 2, y + words[id].Size.Height * scale / 2);
                x += w + small * 0.34 * scale;
            }
            y += heights[r];
        }
    }

    private static int[] Ordered(int from, int to, bool rightToLeft)
    {
        var ids = Enumerable.Range(from, to - from).ToArray();
        if (rightToLeft) Array.Reverse(ids);
        return ids;
    }

    protected override void DrawLine(ID2D1DeviceContext dc, StageLine stageLine, double time, double exitStart, in Matrix4x4 stage,
                                     double alpha, float blur, int shown)
    {
        var line = (HingeLine)stageLine;
        var phrasing = line.Phrasing;
        var pages = line.Pages;
        if (pages.Count == 0) return;
        var reduce = ReduceMotion;
        var rtl = phrasing.RightToLeft;
        var u = Unit;
        var turns = line.A.AsSpan(0, pages.Count);
        HingeGeometry.Turns(line.PageStarts, phrasing.LineTimed, time, line.Speed, turns);
        var exit = HingeGeometry.Exit(exitStart, time);
        for (var k = 0; k < pages.Count; k++) line.Begun[k] = phrasing.LineTimed || time >= line.PageStarts[k] - StageMotion.Lead;
        HingeGeometry.Instances(turns, line.Begun, exit.Fold, exit.ReadFade, reduce, _instances);
        // The book moves only in the reading direction: in, still moving, as the line arrives; off, accelerating, as it leaves.
        var entry = HingeGeometry.Entry(line.FirstStart, time);
        var drift = reduce ? 0 : (entry - exit.Travel) * Stage.Width * HingeGeometry.ExitTravel * (rtl ? -1 : 1);
        // Reduce Motion: no fold or travel; the line fades as every other does.
        var whole = (reduce ? 1 - StageMotion.Leave(time, exitStart) : exit.Opacity) * alpha;
        if (!(whole > 0.002)) return;
        var book = Matrix4x4.CreateTranslation((float)drift, 0, 0) * stage;
        var own = rtl ? HingeSide.Left : HingeSide.Right;
        var sign = rtl ? -1.0 : 1.0;
        foreach (var instance in _instances)
        {
            var side = instance.Back ? (own == HingeSide.Right ? HingeSide.Left : HingeSide.Right) : own;
            var room = HingeGeometry.PageWidth(Stage.Width, line.Spine, side, u);
            var pageWidth = room + u * HingeGeometry.Gutter;
            // A turn swings the page's free edge out of the stage, over the spine.
            var turn = Homography.TurnY(reduce ? 0 : sign * instance.Angle, new Vector2((float)line.Spine, (float)(Stage.Height / 2)),
                                        new SizeD(pageWidth, Stage.Height), 0.45) * book;
            var opacity = instance.Opacity * whole;
            if (!(opacity > 0.002)) continue;
            foreach (var k in pages[instance.Page])
            for (var id = phrasing.Phrases[k].From; id < phrasing.Phrases[k].To; id++)
                DrawWord(dc, line, id, side, !instance.Back, line.At[instance.Back ? 1 : 0][id], turn, time, opacity, blur);
        }
    }

    /// <summary>One word, in caps, arriving by its role: swinging open on the spine, flipping open, or inking in.</summary>
    private void DrawWord(ID2D1DeviceContext dc, HingeLine line, int id, HingeSide side, bool live, PointD at, in Matrix4x4 page,
                          double time, double opacity, float blur)
    {
        var run = line.Runs[id];
        var word = line.Words[id];
        var size = line.Size[id];
        var held = line.Held[id];
        var (ink, swing, grow, blurAmount) = line.Phrasing.LineTimed || !live
            ? (time >= run.Start - StageMotion.Lead ? 1.0 : 0.0, 0.0, held is null ? 1.0 : 1 + HingeGeometry.HeldGrowth, 0.0)
            : HingeGeometry.Pose(line.Entrance[id], run.Start, line.GroupStart[id], held, time, line.Speed, ReduceMotion);
        var alpha = ink * opacity;
        if (!(alpha > 0.002)) return;
        var right = side == HingeSide.Right;
        var box = word.Size;
        // The burst grows about a point a quarter in from the spine, so a big word does not run far past the page's margin.
        var growAt = new Vector3((float)(-box.Width / 2 + box.Width * (right ? 0.25 : 0.75)), (float)(-box.Height / 2 + box.Height * 0.6), 0);
        var spineAnchor = new Vector2((float)(right ? -box.Width / 2 : box.Width / 2), 0);
        var unit = Matrix4x4.CreateScale((float)grow, (float)grow, 1, growAt)
            * Homography.TurnY((right ? -1 : 1) * swing, spineAnchor, box, 0.5)
            * Matrix4x4.CreateTranslation((float)at.X, (float)at.Y, 0) * page;
        var wordBlur = blur + (float)(blurAmount > 0.01 ? Points(size * 0.07 * blurAmount / Pt) : 0);
        DrawLabel(dc, line, word, run, 0, time, unit, alpha, wordBlur);
        DrawReading(dc, line, word, run, 0, time, unit, alpha, wordBlur);
    }
}
