using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// Cascade and Cutout with readings: the word being sung, the next-word preview and the words fading out never run
/// into each other, labels included. Units are measured with DirectWrite exactly as the stage measures them.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class ArrivalStackTests : IDisposable
{
    private readonly Offscreen _stage = new(64, 64);
    private readonly TextKit _kit;
    private static readonly WordRevealOptions Options = new();
    private static readonly CompositionKind[] Compositions = [CompositionKind.Cascade, CompositionKind.Cutout];

    public ArrivalStackTests()
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

    private static readonly Lyric Phrases = new("Phrases", [("夢見る少女じゃいられない", "yumemiru shoujo ja irarenai"),
        ("抱きしめて永遠に輝いて", "dakishimete eien ni kagayaite"), ("涙がこぼれる前に", "namida ga koboreru mae ni")]);

    private static readonly Lyric Runs = new("Replace runs", [("dakishimeteeiennikagayaite", null), ("namidagakoborerumaeni", null),
        ("yumemirushoujojairarenai", null)]);

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

    private SizeD[] Measure(RollingWord[] prepared, double stageWidth) =>
    [
        .. prepared.Select(token =>
        {
            var (label, reading, width, height) = SpokenLayer.Layout(_kit, token, stageWidth, 1);
            label.Dispose();
            reading?.Dispose();
            return new SizeD(width, height);
        }),
    ];

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

    private static double Legibility(SizeD size, RollingStack.Slot slot, PointD centre, SizeD stage, double margin)
    {
        var fit = RollingStack.Fit(size, slot, centre, stage, margin);
        var wide = RollingStack.Fit(size, slot, new PointD(centre.X, 1e6), new SizeD(stage.Width, 2e6), margin);
        return wide > 0 ? fit / wide : 0;
    }

    /// <summary>The stage as SpokenLayer places it for <paramref name="composition"/>.</summary>
    private static List<Placed> Stage(RollingWord[] prepared, SizeD[] sizes, double time, double baseSize, SizeD stage,
                                      WordArrival arrival, bool reduce, CompositionKind composition)
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
                token.Index > 0 ? prepared[token.Index - 1].Source.Start : double.NegativeInfinity, composition: composition);
            units.Add((sizes[token.Index], state.Slot));
        }
        var anchor = new PointD(stage.Width / 2, stage.Height * 0.54 + RollingMotion.Baseline(composition.RollingStyle(), 0) * baseSize);
        var centres = RollingStack.Centres([.. units.Select(unit => unit.Size)], [.. units.Select(unit => unit.Slot)],
                                           anchor, baseSize * 0.16);
        return [.. units.Select((unit, k) => new Placed(unit.Size, unit.Slot, centres[k],
            RollingStack.Fit(unit.Size, unit.Slot, centres[k], stage, baseSize * 0.22)))];
    }

    // Ported from RollingStackTests.swift: testPreviewCurrentAndFadingWordsNeverOverlapOnTheRealStage and
    // testEveryEntranceStyleStaysClearAsDrawn (Cascade and Cutout, with the entrances built on Windows)
    [Fact]
    public void PreviewCurrentAndFadingWordsNeverOverlapInAnyEntrance()
    {
        var stage = new SizeD(1210, 707);
        var crowded = 0;
        WordArrival[] styles = [WordArrival.Fade, WordArrival.Lift, WordArrival.Pop, WordArrival.Orbit, WordArrival.Glide, WordArrival.Unfold];
        foreach (var composition in Compositions)
        foreach (var style in styles)
        foreach (var userScale in new[] { 0.7, 1.0, 1.5 })
        foreach (var lyric in Lyrics.Append(Phrases).Append(Runs))
        {
            var (words, readings) = Timed(lyric);
            var baseSize = 70 * 0.75 * userScale;
            var prepared = RollingWords.Prepare(words, baseSize, readings, composition: composition);
            var sizes = Measure(prepared, stage.Width);
            for (var step = 0; step <= 44; step++)
            {
                var time = 9.4 + step * 0.11;
                var placed = Stage(prepared, sizes, time, baseSize, stage, style, false, composition);
                var context = $"{composition} {style} {lyric.Name} x{userScale} at {time}";
                AssertSeparated(placed, context);
                if (placed.Count(unit => unit.Slot.Shown > 0.05) >= 3) crowded++;
                foreach (var unit in placed)
                {
                    var share = Legibility(unit.Size, unit.Slot, unit.Centre, stage, baseSize * 0.22);
                    // Kept clear by room, not by shrinking: the word in focus is never shrunk, and at ordinary sizes
                    // neither is the next word.
                    if (unit.Slot.Shown > 0.5 && Math.Abs(unit.Slot.Rank) <= 0.25)
                        Assert.True(share >= 0.95, $"{context}: the word in focus shrunk");
                    if (userScale <= 1 && unit.Slot.Shown > 0.02 && unit.Slot.Rank > -1.3 && unit.Slot.Rank < -0.25)
                        Assert.True(share >= 0.95, $"{context}: the next word shrunk at rank {unit.Slot.Rank}");
                }
            }
        }
        Assert.True(crowded > 1000, "three or more words were on stage together");
    }

    // Ported from RollingStackTests.swift: testEveryEntranceStyleStaysClearAsDrawn, with the Mac's own sampling
    // (Cascade and Cutout; the entrances built on Windows)
    [Fact]
    public void EveryEntranceStyleStaysClearAsDrawn()
    {
        var stage = new SizeD(1210, 707);
        var crowded = 0;
        WordArrival[] styles = [WordArrival.Fade, WordArrival.Lift, WordArrival.Pop, WordArrival.Orbit, WordArrival.Glide, WordArrival.Unfold];
        foreach (var style in styles)
        foreach (var composition in Compositions)
        foreach (var userScale in new[] { 0.7, 1.5 })
        foreach (var lyric in new[] { Phrases, Runs, Lyrics[0] })
        {
            var (words, readings) = Timed(lyric);
            var baseSize = 70 * 0.75 * userScale;
            var prepared = RollingWords.Prepare(words, baseSize, readings, composition: composition);
            var sizes = Measure(prepared, stage.Width);
            for (var step = 0; step <= 50; step++)
            {
                var time = 9.4 + step * 0.07;
                var placed = Stage(prepared, sizes, time, baseSize, stage, style, false, composition);
                var context = $"{style} {composition} {lyric.Name} x{userScale} at {time}";
                if (placed.Count(unit => unit.Slot.Shown > 0.05) >= 3) crowded++;
                AssertSeparated(placed, context);
                foreach (var unit in placed)
                {
                    var share = Legibility(unit.Size, unit.Slot, unit.Centre, stage, baseSize * 0.22);
                    if (unit.Slot.Shown > 0.5 && Math.Abs(unit.Slot.Rank) <= 0.25)
                        Assert.True(share >= 0.95, $"{context}: the word in focus shrunk");
                    if (userScale <= 1 && unit.Slot.Shown > 0.02 && unit.Slot.Rank > -1.3 && unit.Slot.Rank < -0.25)
                        Assert.True(share >= 0.95, $"{context}: the next word shrunk at rank {unit.Slot.Rank}");
                }
            }
        }
        Assert.True(crowded > 400, "three or more words were on stage together");
    }

    // Ported from RollingStackTests.swift: testStackingHoldsInEveryFaceAndAtEverySliderValue (Windows faces)
    [Fact]
    public void StackingHoldsInEveryFaceAndAtEverySliderValue()
    {
        foreach (var face in new[] { "Segoe UI", "Yu Mincho", "Malgun Gothic", "Georgia" })
        {
            _kit.SetFamily(face);
            foreach (var composition in Compositions)
            foreach (var userScale in new[] { 0.7, 1.0, 1.5 })
            foreach (var lyric in Lyrics)
            {
                var (words, readings) = Timed(lyric);
                var baseSize = 70 * 0.75 * userScale;
                var prepared = RollingWords.Prepare(words, baseSize, readings, composition: composition);
                var sizes = Measure(prepared, 1210);
                var arrival = WordArrival.Automatic.Resolved(composition);
                for (var step = 0; step <= 60; step++)
                foreach (var reduce in new[] { false, true })
                {
                    var time = 9.4 + step * 0.08;
                    var placed = Stage(prepared, sizes, time, baseSize, new SizeD(1210, 760), arrival, reduce, composition)
                        .Select(unit => unit with { Fit = 1 });
                    AssertSeparated(placed, $"{face} {composition} {lyric.Name} x{userScale}{(reduce ? " reduced" : "")} at {time}");
                }
            }
        }
    }

    // Ported from RollingStackTests.swift: testRoomForAnEntranceShrinksSmoothly
    [Fact]
    public void RoomForAnEntranceShrinksSmoothly()
    {
        LyricWord[] words = [new("hold", 9, 9.6), new("me", 9.6, 10), new("forever", 10, 11.8), new("now", 11.8, 12.2)];
        var prepared = RollingWords.Prepare(words, 60, composition: CompositionKind.Cascade);
        var sizes = Measure(prepared, 1200);
        PointD[]? previous = null;
        for (var step = 0; step <= 1200; step++)
        {
            var time = 10 + step / 1000.0;
            var position = RollingWords.Cursor(prepared, time, 0, false);
            int? next = null;
            for (var i = 0; i < words.Length; i++) if (words[i].Start > time) { next = i; break; }
            var slots = prepared.Select(token => RollingTokenState.Make(token, next, position - 1 - token.Index, time, 60, 1200, 0,
                false, WordArrival.Lift, Options, composition: CompositionKind.Cascade).Slot).ToArray();
            var centres = RollingStack.Centres(sizes, slots, new PointD(600, 380), 9.6);
            if (previous is not null)
                for (var k = 0; k < centres.Length; k++)
                    Assert.True(Math.Abs(previous[k].Y - centres[k].Y) < 1.2, $"jump at {time}");
            previous = centres;
        }
    }

    // Ported from RollingStackTests.swift: testWideLabelsTurnLess (the measured label)
    [Fact]
    public void AWideLabelSwingsByLessThanItsOwnHeight()
    {
        foreach (var (text, reading) in new[] { ("夢見る少女じゃいられない", "yumemiru shoujo ja irarenai"), ("dakishimeteeiennikagayaite", (string?)null) })
        {
            var word = new LyricWord(text, 10, 10.5);
            var readings = reading is null ? null : new Dictionary<LyricWord, string> { [word] = reading };
            var phrase = RollingWords.Prepare([word], 66, readings, composition: CompositionKind.Cutout)[0];
            Assert.True(phrase.Tile);
            Assert.True(phrase.TurnLimit < 8, text);
            var size = Measure([phrase], 4000)[0];
            var radians = phrase.TurnLimit * Math.PI / 180;
            var turned = size.Height * Math.Cos(radians) + size.Width * Math.Sin(radians);
            Assert.True(turned < size.Height * 1.8, $"{text} swings by more than its own height");
        }
    }

    [Fact]
    public void ALabelIsPaddedElevenByThreePoints()
    {
        var word = new LyricWord("tonight", 10, 10.5);
        var label = RollingWords.Prepare([word], 60, composition: CompositionKind.Cutout)[0];
        var plain = label with { Tile = false };
        foreach (var scale in new[] { 1f, 1.5f })
        {
            var (a, _, labelWidth, labelHeight) = SpokenLayer.Layout(_kit, label, 1200, scale);
            var (b, _, plainWidth, plainHeight) = SpokenLayer.Layout(_kit, plain, 1200, scale);
            a.Dispose();
            b.Dispose();
            Assert.Equal(plainWidth - 4 * scale + 22 * scale, labelWidth, 3);
            Assert.Equal(plainHeight + 6 * scale, labelHeight, 3);
        }
    }

    // Ported from RollingStackTests.swift: testATallNextWordIsStillShrunkAtLargeSizes. The Mac pins a known limit
    // with an expected failure: at the largest sizes a tall next word is still shrunk. The second case's translation
    // band is not in the snapshot, so it is taken as given (the free height is the stage less the band).
    [Fact]
    public void ATallNextWordIsStillShrunkAtLargeSizes()
    {
        var worst = 1.0;
        (SizeD Stage, double Band, double Base, CompositionKind Composition, WordArrival Style)[] cases =
        [
            (new SizeD(1210, 707), 0, 70 * 1.5, CompositionKind.Cutout, WordArrival.Orbit),
            (new SizeD(1536, 777.6), 88.3 * 1.25 * 0.30 * 2.7 + 88.3 * 0.3, 88.3 * 1.25, CompositionKind.Cascade, WordArrival.Lift),
        ];
        foreach (var item in cases)
        {
            var (words, readings) = Timed(Phrases);
            var baseSize = item.Base * 0.75;
            var prepared = RollingWords.Prepare(words, baseSize, readings, composition: item.Composition);
            var sizes = Measure(prepared, item.Stage.Width);
            var free = new SizeD(item.Stage.Width, item.Stage.Height - item.Band);
            for (var step = 0; step <= 300; step++)
            {
                var time = 9.2 + step / 100.0;
                foreach (var unit in Stage(prepared, sizes, time, baseSize, free, item.Style, false, item.Composition))
                {
                    if (unit.Slot.Shown > 0.02 && unit.Slot.Rank > -1.3 && unit.Slot.Rank < -0.25)
                        worst = Math.Min(worst, Legibility(unit.Size, unit.Slot, unit.Centre, free, baseSize * 0.22));
                }
            }
        }
        // Known limit, handed over: when it is fixed this fails, and the test should say the next word keeps its size.
        Assert.True(worst < 0.9, $"the tall next word is no longer shrunk ({worst})");
    }
}
