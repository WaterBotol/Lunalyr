// Fable's choreography (the Mac's FableLayout.swift, Build 88: FableStyle, FableLanding, FablePose, fablePlan,
// fablePages, fableHeroRun, fableStrokes, fablePose, fablePageTurn, FableStroke and FableLayout; Lunalyr's own code, no
// line from the Verso import, ported as it is). Pure functions of the line and the time; the drawing is FableLayer's.
//
// A storybook page per line. The page is set in a shape chosen for the line (an arc, a stair, a wave or a plain centred
// block) and each phrase (StagePhrases) is one line of type on it, so the page breaks where the singer breathes. Words
// land playfully, by role: each phrase's focal word makes the big landing (it pops up from its foot with a swing, or
// drops in from above, alternating phrase by phrase), the line's hero word lands biggest of all, content words hop up
// into place, and little words are written in with a flick of the pen; a held word is written slowly over the time it
// is sung, and a quick run hops in as one group. A long line turns the page by phrase: a page holds at most two
// phrases, and the old page folds away about its spine before the next is begun. Nothing moves once landed, and nothing
// decorates a word (no stars, brackets or underlines).

using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>The shape a Fable page's phrases are set in.</summary>
public enum FableShape { Arc, Stair, Wave, Block }

/// <summary>
/// One Fable line's look: the shape its phrases are set in. Chosen by the line, so the same line looks the same on every
/// render; neighbouring lines never share a shape, and the order shifts each lap so the run of shapes does not repeat
/// every four lines.
/// </summary>
public readonly record struct FableStyle(FableShape Shape)
{
    public static FableStyle ForLine(long seed)
    {
        const long n = 4;
        // Floor division, so negative seeds keep the pattern too.
        var lap = seed >= 0 ? seed / n : -((-seed + n - 1) / n);
        var index = ((seed + lap) % n + n) % n;
        return new FableStyle((FableShape)index);
    }
}

/// <summary>
/// How a phrase's focal word lands: popping up from its foot with a swing, or dropping in from above. Phrases alternate,
/// and the line's seed decides which comes first, so neighbouring phrases (and lines) differ.
/// </summary>
public enum FableLanding { Pop, Drop }

/// <summary>A Fable word's landing pose: below (positive) or above (negative) its place as a share of its size, its scale, and its swing about its foot (degrees).</summary>
public readonly record struct FablePose(double Lift, double Scale, double Spin)
{
    /// <summary>Landed.</summary>
    public static FablePose Rest => new(0, 1, 0);
}

/// <summary>A word's part in Fable.</summary>
public enum FableRole
{
    /// <summary>The line's one important word: the biggest word and landing.</summary>
    Hero,
    /// <summary>A phrase's focal word.</summary>
    Focal,
    Content,
    Function,
}

/// <summary>A pen stroke: when it starts (s) and how long it takes.</summary>
public readonly record struct FableStroke(double Start, double Length);

/// <summary>One page: its phrases (indices into the line's phrasing) and runs, one row per phrase.</summary>
/// <param name="Start">Its first onset.</param>
/// <param name="TurnStart">When the page before it starts to turn away.</param>
/// <param name="TurnLength">How long the page before takes to turn away: shorter on a fast line, so it is gone before this page's first word lands.</param>
public sealed record FablePage(int[] Phrases, int[] Runs, int[][] Rows, double Start, double TurnStart, double TurnLength)
{
    /// <summary>The rows as positions in <see cref="Runs"/>, for the layout.</summary>
    public int[][] RowSlots { get; } = [.. Rows.Select(row => row.Select(id => Array.IndexOf(Runs, id)).Where(i => i >= 0).ToArray())];
}

/// <summary>A line's pages, roles, strokes and hero.</summary>
public sealed class FablePlan
{
    public required FablePage[] Pages { get; init; }
    public required FableRole[] Roles { get; init; }
    public required FableStroke[] Strokes { get; init; }
    /// <summary>The line's hero word.</summary>
    public int? Hero { get; init; }
    /// <summary>The phrase each run belongs to, by run id.</summary>
    public required int[] PhraseOfRun { get; init; }
    /// <summary>Whether each run is written in by the pen (function words, held words, every word of a line-timed line).</summary>
    public required bool[] Writes { get; init; }
    /// <summary>When each run's landing starts: its own onset, or the first onset of the quick run it belongs to.</summary>
    public required double[] Clocks { get; init; }
    public bool LineTimed { get; init; }
    public bool RightToLeft { get; init; }

    public static FablePlan Empty { get; } = new() { Pages = [], Roles = [], Strokes = [], PhraseOfRun = [], Writes = [], Clocks = [] };
}

public static class FableGeometry
{
    /// <summary>Fable's base unit (s).</summary>
    public const double Unit = 0.1;
    /// <summary>A page starts to turn away this long before the next page's first word.</summary>
    public const double TurnLead = 0.3;
    /// <summary>A page turning away.</summary>
    public const double Turn = 0.24;
    /// <summary>Most phrases and runs on one page.</summary>
    public const int PagePhrases = 2, PageRuns = 8;
    /// <summary>The share of a word the pen's soft edge covers.</summary>
    public const double StrokeEdge = 0.3;

    public static FableLanding LandingFor(int phrase, long seed) => ((phrase + seed) % 2 + 2) % 2 == 0 ? FableLanding.Pop : FableLanding.Drop;

    /// <summary>Fable's one out-ease: easeOutBack, overshooting about 10 %.</summary>
    public static double Out(double x)
    {
        var t = double.IsNaN(x) ? 0 : Math.Min(Math.Max(x, 0), 1);
        const double c1 = 1.4, c3 = c1 + 1;
        var u = t - 1;
        return 1 + c3 * u * u * u + c1 * u * u;
    }

    /// <summary>Fable's one in-ease: easeInCubic, for pages turning away.</summary>
    public static double In(double x)
    {
        var t = double.IsNaN(x) ? 0 : Math.Min(Math.Max(x, 0), 1);
        return t * t * t;
    }

    /// <summary>A word's size by its role: the line's hero word largest, little words smallest. A line of one or two words is all set large.</summary>
    public static double Size(FableRole role, int wordCount, double unit)
    {
        if (wordCount <= 2) return unit * 2.0;
        return role switch
        {
            FableRole.Hero => unit * 2.0,
            FableRole.Focal => unit * 1.6,
            FableRole.Content => unit * 1.3,
            _ => unit * 0.95,
        };
    }

    /// <summary>How much of a word its pen stroke has written, 0…1: the pen's own speed (smoothstep), from the stroke's start over its length.</summary>
    public static double Written(FableStroke stroke, double time) =>
        time >= stroke.Start ? Easing.Smoothstep((time - stroke.Start) / Math.Max(0.01, stroke.Length)) : 0;

    /// <summary>
    /// A word's landing at <paramref name="time"/>. The focal words make the big landing (the line's hero biggest,
    /// <paramref name="landing"/> deciding which), content words hop up, function words do not move: the pen writes them in.
    /// </summary>
    public static FablePose Pose(StageRun run, FableRole role, FableLanding landing, bool lineTimed, double? clock, double time)
    {
        var onset = run.Start - StageMotion.Lead;
        if (!(time >= onset) || lineTimed) return FablePose.Rest;
        switch (role)
        {
            case FableRole.Hero:
            case FableRole.Focal:
            {
                var big = role == FableRole.Hero;
                var left = 1 - Out((time - onset) / ((big ? 4.5 : 3.5) * Unit));
                return landing == FableLanding.Pop
                    ? new FablePose(0, 1 - (big ? 0.6 : 0.45) * left, -(big ? 18 : 12) * left)
                    : new FablePose(-(big ? 0.6 : 0.45) * left, 1 + (big ? 0.12 : 0.08) * left, 6 * left);
            }
            case FableRole.Content:
            {
                var p = (time - (Math.Min(clock ?? run.Start, run.Start) - StageMotion.Lead)) / (2.5 * Unit);
                return new FablePose(0.5 * (1 - Out(p)), 1, 0);
            }
            default:
                return FablePose.Rest;
        }
    }

    /// <summary>How far page <paramref name="index"/> has turned away, 0…1: when the next page is begun, or when the line leaves.</summary>
    public static double PageTurn(int index, FablePlan plan, double time, double exitStart)
    {
        var start = double.PositiveInfinity;
        var length = Turn;
        if (index + 1 < plan.Pages.Length)
        {
            start = plan.Pages[index + 1].TurnStart;
            length = plan.Pages[index + 1].TurnLength;
        }
        if (double.IsFinite(exitStart) && exitStart < start)
        {
            start = exitStart;
            length = Turn;
        }
        return double.IsFinite(start) ? In((time - start) / length) : 0;
    }

    /// <summary>Whether page <paramref name="index"/> is drawn at <paramref name="time"/>: from its first word's beat until it has turned away.</summary>
    public static bool PageShown(int index, FablePlan plan, double time, double exitStart)
    {
        if (index < 0 || index >= plan.Pages.Length) return false;
        if (!(time >= plan.Pages[index].Start - StageMotion.Lead)) return false;
        return PageTurn(index, plan, time, exitStart) < 1;
    }

    /// <summary>The line's plan, from its (cached) phrasing. A layer makes it once per line.</summary>
    public static FablePlan Plan(IReadOnlyList<StageRun> runs) => MakePlan(runs, StagePhrases.Phrasing(runs));

    public static FablePlan MakePlan(IReadOnlyList<StageRun> runs, StagePhrasing phrasing)
    {
        if (runs.Count == 0 || phrasing.Words.Count != runs.Count) return FablePlan.Empty;
        var infos = phrasing.Words;
        var hero = HeroRun(runs, phrasing);
        var roles = infos.Select(info => info.Run == hero ? FableRole.Hero : info.IsFocal ? FableRole.Focal
            : info.IsFunction ? FableRole.Function : FableRole.Content).ToArray();
        var strokes = Strokes(runs, phrasing, roles);
        List<int[]> groups = phrasing.LineTimed ? [[.. Enumerable.Range(0, phrasing.Phrases.Count)]] : Pages(phrasing.Phrases);
        var pages = new List<FablePage>();
        foreach (var group in groups)
        {
            var rows = group.Select(k => phrasing.Phrases[k].Runs.ToArray()).ToArray();
            var ids = rows.SelectMany(row => row).ToArray();
            var start = ids.Length == 0 ? 0 : ids.Min(id => runs[id].Start);
            if (pages.Count > 0)
            {
                var lastLanded = pages[^1].Runs.Max(id => runs[id].Start);
                var early = start - StageMotion.Lead - TurnLead;
                // Never before the old page's last word has landed, and gone before this page's first word lands.
                var landed = lastLanded + Unit;
                var turnStart = Math.Min(start - StageMotion.Lead - Unit, Math.Max(early, landed));
                var room = start - StageMotion.Lead - turnStart;
                var length = Math.Min(Turn, Math.Max(Unit, room));
                pages.Add(new FablePage(group, ids, rows, start, turnStart, length));
                continue;
            }
            pages.Add(new FablePage(group, ids, rows, start, double.NegativeInfinity, Turn));
        }
        var clocks = runs.Select(run => run.Start).ToArray();
        for (var index = 0; index < infos.Count; index++)
        {
            if (infos[index].RunGroup is not { } group) continue;
            var earliest = double.PositiveInfinity;
            for (var k = 0; k < infos.Count; k++)
                if (infos[k].RunGroup == group && infos[k].Phrase == infos[index].Phrase) earliest = Math.Min(earliest, runs[k].Start);
            clocks[index] = double.IsFinite(earliest) ? earliest : runs[index].Start;
        }
        return new FablePlan
        {
            Pages = [.. pages], Roles = roles, Strokes = strokes, Hero = hero, PhraseOfRun = [.. infos.Select(info => info.Phrase)],
            Writes = [.. infos.Select(info => phrasing.LineTimed || info.IsFunction || info.IsHeld)], Clocks = clocks,
            LineTimed = phrasing.LineTimed, RightToLeft = phrasing.RightToLeft,
        };
    }

    /// <summary>
    /// Phrases gathered into pages: at most two phrases and eight runs a page. Where a pause is followed by a phrase that
    /// could go with the one after it, the page is turned at the pause instead.
    /// </summary>
    public static List<int[]> Pages(IReadOnlyList<StagePhrase> phrases)
    {
        var pages = new List<int[]>();
        var page = new List<int>();
        var count = 0;
        for (var index = 0; index < phrases.Count; index++)
        {
            var phrase = phrases[index];
            var fits = page.Count < PagePhrases && count + phrase.Count <= PageRuns;
            if (fits && page.Count == 1 && phrase.BreakBefore == StagePhraseBreak.Gap && index + 1 < phrases.Count)
            {
                var next = phrases[index + 1];
                // Turn at the pause when the next two make a page of their own.
                if (next.BreakBefore != StagePhraseBreak.Gap && phrase.Count + next.Count <= PageRuns) fits = false;
            }
            if (!fits && page.Count > 0)
            {
                pages.Add([.. page]);
                page.Clear();
                count = 0;
            }
            page.Add(index);
            count += phrase.Count;
        }
        if (page.Count > 0) pages.Add([.. page]);
        return pages;
    }

    /// <summary>The line's hero word: the phrase focal held longest (ties: the later one); on a line-timed line, the longest focal word.</summary>
    public static int? HeroRun(IReadOnlyList<StageRun> runs, StagePhrasing phrasing)
    {
        int? best = null;
        foreach (var phrase in phrasing.Phrases)
        {
            var id = phrase.Focal;
            if (id < 0 || id >= runs.Count || id >= phrasing.Words.Count) continue;
            if (best is not { } current) { best = id; continue; }
            if (phrasing.LineTimed)
            {
                if (TextElements.Count(runs[id].Text) > TextElements.Count(runs[current].Text)) best = id;
            }
            else if (phrasing.Words[id].Duration >= phrasing.Words[current].Duration - 0.05)
            {
                best = id;
            }
        }
        return best;
    }

    /// <summary>Each word's pen stroke. A quick run is one stroke across the group: each member is written from its own beat until the next member's.</summary>
    public static FableStroke[] Strokes(IReadOnlyList<StageRun> runs, StagePhrasing phrasing, IReadOnlyList<FableRole> roles)
    {
        var strokes = new FableStroke[runs.Count];
        for (var index = 0; index < runs.Count; index++)
        {
            var run = runs[index];
            var info = phrasing.Words[index];
            var start = run.Start - StageMotion.Lead;
            if (phrasing.LineTimed)
            {
                // Written together, phrase by phrase, a little apart.
                strokes[index] = new FableStroke(start + info.Phrase * 0.8 * Unit, 3 * Unit);
                continue;
            }
            var letters = TextElements.Count(run.Text);
            var length = roles[index] switch
            {
                FableRole.Function => Unit,
                FableRole.Content => Math.Min(4 * Unit, Math.Max(1.5 * Unit, letters * 0.3 * Unit)),
                _ => Math.Min(4 * Unit, Math.Max(2.5 * Unit, letters * 0.4 * Unit)),
            };
            if (info.IsHeld) length = Math.Max(length, Math.Min(1.2, info.Duration * 0.85));
            if (info.RunGroup is { } group && index + 1 < runs.Count && phrasing.Words[index + 1].RunGroup == group)
                length = Math.Max(0.05, runs[index + 1].Start - run.Start);
            strokes[index] = new FableStroke(start, length);
        }
        return strokes;
    }

    /// <summary>Where the pen's solid ink ends and the clear part starts, 0…1 across the word (a soft-edged wipe).</summary>
    public static (double Solid, double Clear) StrokeStops(double progress)
    {
        if (progress >= 1) return (1, 1);
        var head = Math.Max(0, progress) * (1 + StrokeEdge);
        var clear = Math.Min(1, head);
        var solid = Math.Min(Math.Max(head - StrokeEdge, 0), clear);
        return (solid, Math.Max(clear, solid));
    }

    /// <summary>
    /// Lays a page out: one row per phrase, words on a shared baseline that the shape bends, every word in a box of its
    /// own that nothing else enters; each row starts below the lowest point of the one before. A word in no row gets a
    /// zero box at the origin. Right to left, the page is mirrored. <paramref name="baselines"/> (from each box's top)
    /// default to half the box's height.
    /// </summary>
    public static (SizeD Size, StageRect[] Frames) Arrange(IReadOnlyList<SizeD> sizes, IReadOnlyList<IReadOnlyList<int>> rows,
                                                           FableShape shape, bool rightToLeft, double gap, double rowGap,
                                                           IReadOnlyList<double>? baselines = null)
    {
        var frames = new StageRect[sizes.Count];
        var valid = rows.Select(row => row.Where(i => i >= 0 && i < sizes.Count).ToArray()).Where(row => row.Length > 0).ToArray();
        if (valid.Length == 0) return (SizeD.Zero, frames);
        double Baseline(int index) =>
            baselines is null || index >= baselines.Count ? sizes[index].Height / 2 : Math.Min(sizes[index].Height, Math.Max(0, baselines[index]));
        var rowWidths = valid.Select(row => row.Sum(i => sizes[i].Width) + gap * (row.Length - 1)).ToArray();
        var widest = rowWidths.Max();
        var placedAll = new List<int>();
        double top = 0;
        var order = 0;
        var placed = new List<(int Index, StageRect Rect)>();
        for (var r = 0; r < valid.Length; r++)
        {
            var row = valid[r];
            var rowHeight = row.Max(i => sizes[i].Height);
            var start = shape == FableShape.Stair ? r * rowHeight * 0.6 : (widest - rowWidths[r]) / 2;
            var middle = start + rowWidths[r] / 2;
            var half = Math.Max(rowWidths[r] / 2, 1);
            var x = start;
            placed.Clear();
            for (var j = 0; j < row.Length; j++)
            {
                var index = row[j];
                var size = sizes[index];
                var u = Math.Min(1, Math.Max(-1, (x + size.Width / 2 - middle) / half));
                var dy = shape switch
                {
                    FableShape.Arc => -rowHeight * 0.5 * (1 - u * u),
                    FableShape.Stair => j * rowHeight * 0.16,
                    FableShape.Wave => Math.Sin(order * 1.4 + r * 0.9) * rowHeight * 0.2,
                    _ => 0,
                };
                placed.Add((index, new StageRect(x, dy - Baseline(index), size.Width, size.Height)));
                x += size.Width + gap;
                order++;
            }
            var rowTop = placed.Min(p => p.Rect.MinY);
            var shift = top - rowTop;
            foreach (var (index, rect) in placed)
            {
                frames[index] = rect.Offset(0, shift);
                placedAll.Add(index);
            }
            top = placed.Max(p => p.Rect.MaxY) + shift + rowGap;
        }
        var box = frames[placedAll[0]];
        foreach (var index in placedAll.Skip(1)) box = box.Union(frames[index]);
        foreach (var index in placedAll) frames[index] = frames[index].Offset(-box.MinX, -box.MinY);
        if (rightToLeft)
            foreach (var index in placedAll)
            {
                var f = frames[index];
                frames[index] = f with { X = box.Width - f.MaxX };
            }
        return (box.Size, frames);
    }
}
