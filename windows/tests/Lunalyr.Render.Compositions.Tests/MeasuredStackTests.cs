using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// Spoken with readings: the word being sung, the next-word preview and the words fading out must never run into each
/// other, whatever the script, face or size. Units are measured with DirectWrite exactly as the stage measures them.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class MeasuredStackTests : IDisposable
{
    private readonly Offscreen _stage = new(64, 64);
    private readonly TextKit _kit;
    private static readonly WordRevealOptions Options = new();

    public MeasuredStackTests()
    {
        _kit = new TextKit(_stage.Dc, _stage.DWrite);
        _kit.SetFamily("Segoe UI Variable Display");
    }

    public void Dispose()
    {
        _kit.Dispose();
        _stage.Dispose();
    }

    private sealed record Lyric(string Name, (string Text, string? Reading)[] Words);

    private static readonly Lyric[] Lyrics =
    [
        new("Japanese", [("夢見る", "yumemiru"), ("少女じゃ", "shoujo ja"), ("いられない", "irarenai"), ("抱きしめて", "dakishimete"),
                         ("永遠に", "eien ni"), ("輝いて", "kagayaite")]),
        new("Korean", [("사랑했지만", "saranghaetjiman"), ("그대를", "geudaereul"), ("기억하며", "gieokhamyeo"),
                       ("잊을게요", "ijeulgeyo"), ("영원히", "yeongwonhi")]),
        new("Latin", [("extraordinarily", null), ("unforgettable", null), ("everlasting", null), ("love", null), ("tonight", null)]),
        new("Replace", [("yumemiru", null), ("shoujoja", null), ("irarenai", null), ("dakishimete", null)]),
    ];

    /// <summary>Whole glued phrases with their readings: the tallest, widest units.</summary>
    private static readonly Lyric Phrases = new("Phrases", [("夢見る少女じゃいられない", "yumemiru shoujo ja irarenai"),
        ("抱きしめて永遠に輝いて", "dakishimete eien ni kagayaite"), ("涙がこぼれる前に", "namida ga koboreru mae ni")]);

    private static (LyricWord[] Words, Dictionary<LyricWord, string> Readings) Timed(Lyric lyric)
    {
        var words = new List<LyricWord>();
        var readings = new Dictionary<LyricWord, string>();
        for (var index = 0; index < lyric.Words.Length; index++)
        {
            var start = 10 + index * 0.75;
            var word = new LyricWord(lyric.Words[index].Text, start, start + 0.65);
            words.Add(word);
            if (lyric.Words[index].Reading is { } reading) readings[word] = reading;
        }
        return ([.. words], readings);
    }

    private SizeD[] Measure(RollingWord[] prepared, double stageWidth)
    {
        return [.. prepared.Select(token =>
        {
            var (label, reading, width, height) = SpokenLayer.Layout(_kit, token, stageWidth, 1);
            label.Dispose();
            reading?.Dispose();
            return new SizeD(width, height);
        })];
    }

    private readonly record struct Placed(SizeD Size, RollingStack.Slot Slot, PointD Centre, double Fit = 1)
    {
        public Box Box => RollingStack.DrawnBox(Size, Slot, Centre, Fit);
    }

    private static void AssertSeparated(IEnumerable<Placed> placed, string context)
    {
        var visible = placed.Where(unit => unit.Slot.Shown > 0.05).ToArray();
        for (var i = 0; i < visible.Length; i++)
        for (var j = i + 1; j < visible.Length; j++)
        {
            Box a = Inset(visible[i].Box), b = Inset(visible[j].Box);
            var overlaps = a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
            Assert.False(overlaps, $"{context}: ranks {visible[i].Slot.Rank} and {visible[j].Slot.Rank} overlap");
        }
        static Box Inset(Box box) => new(box.X + 0.5, box.Y + 0.5, box.Width - 1, box.Height - 1);
    }

    /// <summary>The stage fit for a unit as a share of what its width alone allows: below 1, it is being shrunk to fit vertically.</summary>
    private static double Legibility(SizeD size, RollingStack.Slot slot, PointD centre, SizeD stage, double margin)
    {
        var fit = RollingStack.Fit(size, slot, centre, stage, margin);
        var wide = RollingStack.Fit(size, slot, new PointD(centre.X, 1e6), new SizeD(stage.Width, 2e6), margin);
        return wide > 0 ? fit / wide : 0;
    }

    /// <summary>Spoken's stage for <paramref name="lyric"/> at <paramref name="time"/>, as SpokenLayer places it.</summary>
    private List<Placed> Stage(RollingWord[] prepared, SizeD[] sizes, double time, double baseSize, SizeD stage,
                               WordArrival arrival, bool reduce)
    {
        var cursor = RollingWords.Cursor(prepared, time, 0, reduce);
        var position = reduce ? Math.Round(cursor) : cursor;
        int? next = null;
        for (var i = 0; i < prepared.Length; i++) if (prepared[i].Source.Start > time) { next = i; break; }
        var units = new List<(SizeD Size, RollingStack.Slot Slot)>();
        foreach (var token in prepared)
        {
            var rank = position - 1 - token.Index;
            if (rank < -2 || rank > 5.2) continue;
            var state = RollingTokenState.Make(token, next, rank, time, baseSize, stage.Width, 0, reduce, arrival, Options,
                token.Index > 0 ? prepared[token.Index - 1].Source.Start : double.NegativeInfinity);
            units.Add((sizes[token.Index], state.Slot));
        }
        var anchor = new PointD(stage.Width / 2, stage.Height * 0.54 + RollingMotion.Baseline(RollingStyle.Fan, 0) * baseSize);
        var centres = RollingStack.Centres([.. units.Select(unit => unit.Size)], [.. units.Select(unit => unit.Slot)],
                                           anchor, baseSize * 0.16);
        return [.. units.Select((unit, k) => new Placed(unit.Size, unit.Slot, centres[k],
            RollingStack.Fit(unit.Size, unit.Slot, centres[k], stage, baseSize * 0.22)))];
    }

    // Ported from RollingStackTests.swift: testPreviewCurrentAndFadingWordsNeverOverlapOnTheRealStage and
    // testEveryEntranceStyleStaysClearAsDrawn (Spoken, with the entrances built on Windows)
    [Fact]
    public void PreviewCurrentAndFadingWordsNeverOverlapInAnyEntrance()
    {
        var stage = new SizeD(1210, 707);
        var crowded = 0;
        WordArrival[] styles = [WordArrival.Fade, WordArrival.Lift, WordArrival.Pop, WordArrival.Orbit, WordArrival.Glide, WordArrival.Unfold];
        foreach (var style in styles)
        foreach (var userScale in new[] { 0.7, 1.0, 1.5 })
        foreach (var lyric in Lyrics.Append(Phrases))
        {
            var (words, readings) = Timed(lyric);
            var baseSize = 70 * 0.75 * userScale;
            var prepared = RollingWords.Prepare(words, baseSize, readings);
            var sizes = Measure(prepared, stage.Width);
            for (var step = 0; step <= 44; step++)
            {
                var time = 9.4 + step * 0.11;
                var placed = Stage(prepared, sizes, time, baseSize, stage, style, false);
                var context = $"{style} {lyric.Name} x{userScale} at {time}";
                AssertSeparated(placed, context);
                if (placed.Count(unit => unit.Slot.Shown > 0.05) >= 3) crowded++;
                // Kept clear by room, not by shrinking: the word in focus is never shrunk.
                foreach (var unit in placed.Where(unit => unit.Slot.Shown > 0.5 && Math.Abs(unit.Slot.Rank) <= 0.25))
                    Assert.True(Legibility(unit.Size, unit.Slot, unit.Centre, stage, baseSize * 0.22) >= 0.95,
                                $"{context}: the word in focus shrunk");
            }
        }
        Assert.True(crowded > 200, "the preview, current and fading words were on stage together");
    }

    // Ported from RollingStackTests.swift: testStackingHoldsInEveryFaceAndAtEverySliderValue (Windows faces)
    [Fact]
    public void StackingHoldsInEveryFaceAndAtEverySliderValue()
    {
        foreach (var face in new[] { "Segoe UI", "Yu Mincho", "Malgun Gothic", "Georgia" })
        {
            _kit.SetFamily(face);
            foreach (var userScale in new[] { 0.7, 1.0, 1.5 })
            foreach (var lyric in Lyrics)
            {
                var (words, readings) = Timed(lyric);
                var baseSize = 70 * 0.75 * userScale;
                var prepared = RollingWords.Prepare(words, baseSize, readings);
                var sizes = Measure(prepared, 1210);
                for (var step = 0; step <= 60; step++)
                foreach (var reduce in new[] { false, true })
                {
                    var time = 9.4 + step * 0.08;
                    var placed = Stage(prepared, sizes, time, baseSize, new SizeD(1210, 760), WordArrival.Glide, reduce)
                        .Select(unit => unit with { Fit = 1 });
                    AssertSeparated(placed, $"{face} {lyric.Name} x{userScale}{(reduce ? " reduced" : "")} at {time}");
                }
            }
        }
    }

    // Ported from RollingStackTests.swift: testTheOldFixedSpacingDidOverlapWithReadings
    [Fact]
    public void TheOldFixedSpacingDidOverlapWithReadings()
    {
        var (words, readings) = Timed(Lyrics[0]);
        const double baseSize = 70 * 0.75;
        var prepared = RollingWords.Prepare(words, baseSize, readings);
        var sizes = Measure(prepared, 1210);
        var overlapped = false;
        for (var step = 0; step <= 40; step++)
        {
            var time = 10.3 + step * 0.1;
            var position = RollingWords.Cursor(prepared, time, 0, false);
            var boxes = new List<Box>();
            foreach (var token in prepared)
            {
                var rank = position - 1 - token.Index;
                if (rank < -1.2 || rank > 1.5) continue;
                var pose = RollingMotion.Pose(RollingStyle.Fan, rank, time - token.Source.Start, 0.65, token.Seed, 0, true);
                boxes.Add(RollingStack.DrawnBox(sizes[token.Index], new RollingStack.Slot { Rank = rank, Scale = pose.Scale },
                                                new PointD(0, pose.Y * baseSize)));
            }
            for (var i = 0; i < boxes.Count; i++)
            for (var j = i + 1; j < boxes.Count; j++)
            {
                Box a = boxes[i], b = new(boxes[j].X + 1, boxes[j].Y + 1, boxes[j].Width - 2, boxes[j].Height - 2);
                if (a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height) overlapped = true;
            }
        }
        Assert.True(overlapped, "the regression tests must be able to see this bug");
    }
}

/// <summary>Tests that use a Direct3D device run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpuCollection
{
    public const string Name = "GPU";
}
