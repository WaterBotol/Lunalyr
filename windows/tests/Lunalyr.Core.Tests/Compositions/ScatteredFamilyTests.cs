using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// The Scattered family (Scattered, Constellation, Constellation + Editorial, Echo) and the remaining word entrances
/// (cutouts, highlight, swipes, mix): docs/compositions/scattered.md.
/// </summary>
public class ScatteredFamilyTests
{
    private static readonly CompositionKind[] Kinds = Enum.GetValues<CompositionKind>();

    private static readonly LyricWord[] KeepTheLight =
    [
        new("Keep", 10, 10.3), new("the", 10.3, 10.6), new("light", 10.6, 13), new("♪", 13, 13.1),
    ];

    private static EditorialLine Set(CompositionKind kind, double size = 44, double spread = 1, IReadOnlyList<LyricWord>? words = null)
    {
        words ??= KeepTheLight;
        return EditorialSetting.Layout(words, new bool[words.Count], size, 11, 0, wordTimed: true,
                                       editorial: kind.HasEditorialTypography(), composition: kind, spread: spread);
    }

    // ---- The compositions ----------------------------------------------------------------------------------------

    [Fact]
    public void TheCompositionsAreNamedAndDescribedAsOnTheMac()
    {
        Assert.Equal("scattered", CompositionKind.Scattered.Id());
        Assert.Equal("constellationEditorial", CompositionKind.ConstellationEditorial.Id());
        Assert.Equal("Constellation + Editorial", CompositionKind.ConstellationEditorial.Title());
        Assert.Equal("Words set loose across the line.", CompositionKind.Scattered.Detail());
        Assert.Equal("Fragments linked by hairlines, like a star chart.", CompositionKind.Constellation.Detail());
        Assert.Equal("The phrase repeated at many sizes, some inverted.", CompositionKind.Echo.Detail());
        Assert.True(CompositionKind.Constellation.HasConnections());
        Assert.True(CompositionKind.ConstellationEditorial.HasConnections());
        Assert.False(CompositionKind.Scattered.HasConnections());
        Assert.True(CompositionKind.ConstellationEditorial.HasEditorialTypography());
        Assert.False(CompositionKind.Constellation.HasEditorialTypography());
        Assert.False(CompositionKind.Echo.ArrivalOnly());
    }

    [Fact]
    public void MatchTheStyleGivesEachCompositionItsOwnEntrance()
    {
        Assert.Equal(WordArrival.Pop, WordArrival.Automatic.Resolved(CompositionKind.Scattered));
        Assert.Equal(WordArrival.Orbit, WordArrival.Automatic.Resolved(CompositionKind.Constellation));
        Assert.Equal(WordArrival.Lift, WordArrival.Automatic.Resolved(CompositionKind.ConstellationEditorial));
        Assert.Equal(WordArrival.Lift, WordArrival.Automatic.Resolved(CompositionKind.Echo));
        Assert.Equal(WordArrival.Fade, WordArrival.Automatic.Resolved(CompositionKind.Editorial));
        Assert.Equal(WordArrival.Glide, WordArrival.Automatic.Resolved(CompositionKind.Spoken));
    }

    // Ported from RenderingPerformanceTests.swift: testEveryCompositionKeepsSmallWordsLegibleAtLargeScale (the
    // whole-line compositions; Spoken's sizes are RollingStageTests')
    [Fact]
    public void EveryCompositionKeepsSmallWordsLegibleAtLargeScale()
    {
        foreach (var kind in Kinds.Where(kind => !kind.ArrivalOnly()))
        {
            var sizes = Set(kind, 220).Runs.Select(run => run.Metrics.Size).ToArray();
            Assert.True(sizes.Min() > 220 * 0.55, kind.ToString());
            Assert.True(sizes.Max() / Math.Max(1, sizes.Min()) < 1.9, kind.ToString());
        }
    }

    // Ported from RenderingPerformanceTests.swift: testResizingAndSpreadInvalidateWithoutChangingWordIdentity (the
    // metrics; there is no memo to count here)
    [Fact]
    public void ResizingAndSpreadChangeTheSettingButNotTheWords()
    {
        var first = Set(CompositionKind.ConstellationEditorial);
        var doubled = Set(CompositionKind.ConstellationEditorial, 88);
        Assert.Equal(first.Runs[0].Metrics.Size * 2, doubled.Runs[0].Metrics.Size, 4);
        var wider = Set(CompositionKind.ConstellationEditorial, 88, 1.5);
        Assert.NotEqual(doubled.Runs[0].Metrics.WaveOffset, wider.Runs[0].Metrics.WaveOffset);
        Assert.Equal(first.Runs.Select(run => run.Seed), wider.Runs.Select(run => run.Seed));
    }

    [Fact]
    public void ConstellationStarsSitAtDeliberateHeightsAroundTheFocalWord()
    {
        LyricWord[] words =
        [
            new("Paper", 10, 10.3), new("boats", 10.3, 10.6), new("sailing", 10.6, 12.2), new("down", 12.2, 12.5),
            new("avenue", 12.5, 12.8), new("tonight", 12.8, 13.1), new("again", 13.1, 13.4),
        ];
        var linked = Set(CompositionKind.Constellation, 100, words: words);
        var focal = linked.Runs.Single(run => run.Metrics.IsFocal);
        Assert.Equal("sailing", focal.Source.Text);
        // The focal word holds its row's line, at the focal tier; every other star climbs or dips by its own height.
        Assert.Equal(0, focal.Metrics.WaveOffset);
        Assert.Equal(104, focal.Metrics.Size, 9);
        foreach (var run in linked.Runs.Where(run => !run.Metrics.IsFocal))
        {
            var star = EditorialSetting.StarHeights[run.Index % EditorialSetting.StarHeights.Count];
            // reach = 0.45 + 0.55 × fullness (7 runs: 1), wobble within ±0.5 × 0.18.
            Assert.InRange(run.Metrics.WaveOffset, (star - 0.09) * 100 - 1e-9, (star + 0.09) * 100 + 1e-9);
            Assert.Equal(0, run.Metrics.Rotation);
        }
        // Neighbours alternate up and down, so no link is level.
        Assert.True(linked.Runs[0].Metrics.WaveOffset < 0 && linked.Runs[1].Metrics.WaveOffset > 0);
        // A short line reaches less, and a wider spread more.
        var shortLine = Set(CompositionKind.Constellation, 100, words: words[..2]);
        var shortWider = Set(CompositionKind.Constellation, 100, 1.8, words[..2]);
        var shortStar = shortLine.Runs.First(run => !run.Metrics.IsFocal);
        Assert.True(Math.Abs(shortWider.Runs[shortStar.Index].Metrics.WaveOffset) > Math.Abs(shortStar.Metrics.WaveOffset));
    }

    [Fact]
    public void ConstellationEditorialSetsEveryOtherRunAndTheFocalStarInTheSecondFace()
    {
        LyricWord[] words = [.. Enumerable.Range(0, 8).Select(i => new LyricWord("word" + i, 10 + i * 0.3, 10.28 + i * 0.3))];
        var line = Set(CompositionKind.ConstellationEditorial, 60, words: words);
        for (var r = 0; r < words.Length; r++)
            Assert.Equal(r % 2 == 0 || line.Runs[r].Metrics.IsFocal, line.Runs[r].Metrics.AlternateFace);
        // The focal star (ties go to the later word) sits on a row of its own, its satellites above and below.
        var focal = line.Runs.Single(run => run.Metrics.IsFocal);
        Assert.Equal(7, focal.Index);
        Assert.True(focal.Metrics.BreakBefore);
        Assert.Equal(0, focal.Metrics.WaveOffset);
        Assert.Equal(EditorialSetting.StarHeights[3] * 0.45 * 60, line.Runs[0].Metrics.WaveOffset, 9);
        Assert.All(Set(CompositionKind.Constellation, 60, words: words).Runs, run => Assert.False(run.Metrics.AlternateFace));
        Assert.All(Set(CompositionKind.Scattered, 60, words: words).Runs, run => Assert.False(run.Metrics.AlternateFace));
    }

    [Fact]
    public void DenseConstellationLinesAreSetSmaller()
    {
        Assert.Equal(1, ConstellationLayout.DensityScale(KeepTheLight));
        var dense = Enumerable.Range(0, 30).Select(i => new LyricWord("syllables", i, i + 0.5)).ToArray();
        // 30 × (9 + 3) = 360: sqrt(110 / 360).
        Assert.Equal(Math.Sqrt(110.0 / 360), ConstellationLayout.DensityScale(dense), 9);
        var huge = Enumerable.Range(0, 300).Select(i => new LyricWord("syllables", i, i + 0.5)).ToArray();
        Assert.Equal(0.52, ConstellationLayout.DensityScale(huge));
        Assert.Equal(new SizeD(920, 860), ConstellationLayout.FitArea(new SizeD(1000, 1000)));
    }

    [Fact]
    public void LinksBrightenWithTheWordsTheyJoinAndNeverPassThreeQuarters()
    {
        Assert.Equal(0.20, ConstellationLayout.LinkAlpha(0.20, 0, 1), 9);
        Assert.Equal(0.20 * 1.45, ConstellationLayout.LinkAlpha(0.20, 1, 1), 9);
        Assert.Equal(0.75, ConstellationLayout.LinkAlpha(0.6, 1, 1), 9);
        Assert.Equal(0.10, ConstellationLayout.LinkAlpha(0.20, 0, 0.5), 9);
    }

    // Ported from RenderingPerformanceTests.swift: testConstellationLightingIsContinuousAtWordBoundaries
    [Fact]
    public void ConstellationLightingIsContinuousAtWordBoundaries()
    {
        var token = Set(CompositionKind.ConstellationEditorial).Tokens[2];
        double Heat(double time) => EditorialTokenFrame.Make(token, null, time, 0, double.PositiveInfinity, false,
                                                             double.NegativeInfinity, new WordRevealOptions()).Heat;
        foreach (var boundary in new[] { token.Source.Start, token.Source.End, token.Source.End + 0.3 })
            Assert.Equal(Heat(boundary - 0.0001), Heat(boundary + 0.0001), 3);
        Assert.True(Heat(token.Source.End + 0.1) > Heat(token.Source.End + 0.2));
    }

    // Ported from RenderingPerformanceTests.swift: testStableInkFramesStillChangeAtArrivalAndOnBackwardsSeek
    [Fact]
    public void StableInkFramesStillChangeAtArrivalAndOnBackwardsSeek()
    {
        var token = Set(CompositionKind.Scattered).Tokens[0];
        EditorialTokenFrame Frame(double time) => EditorialTokenFrame.Make(token, 0, time, 0, double.PositiveInfinity, false,
                                                                           double.NegativeInfinity, new WordRevealOptions());
        Assert.Equal(Frame(0), Frame(8));
        Assert.Equal(Frame(20), Frame(30));
        Assert.NotEqual(Frame(10.01), Frame(10.08));
        Assert.NotEqual(Frame(20), Frame(8));
    }

    // ---- Entrances -----------------------------------------------------------------------------------------------

    // Ported from LyricHighlightTests.swift: testTheNewEntranceIsOfferedEverywhere
    [Fact]
    public void TheNewEntranceIsOfferedEverywhere()
    {
        Assert.Contains(WordArrival.CutoutHighlight, Enum.GetValues<WordArrival>());
        Assert.Contains(WordArrival.CutoutHighlight, WordArrivals.MixChoices);
        Assert.Equal("Cutout · Highlight", WordArrival.CutoutHighlight.Title());
        Assert.False(WordArrival.CutoutHighlight.RequiresReveal());
        Assert.False(WordArrival.CutoutHighlight.IsCutout());
        foreach (var composition in Kinds)
        {
            // Words enter as the layout has them enter; the tile is added.
            Assert.Equal(WordArrival.Automatic.Resolved(composition), WordArrival.CutoutHighlight.Motion(composition));
            Assert.Equal(WordArrival.CutoutHighlight, WordArrival.CutoutHighlight.Resolved(composition));
            Assert.True(WordArrival.CutoutHighlight.HighlightsSungWords());
        }
        foreach (var arrival in Enum.GetValues<WordArrival>().Where(arrival => arrival != WordArrival.CutoutHighlight))
        {
            Assert.False(arrival.HighlightsSungWords());
            Assert.Equal(arrival, arrival.Motion(CompositionKind.Scattered));
        }
    }

    [Fact]
    public void EveryRevealIdIsReadAndTheMixHoldsEveryEntranceButTwo()
    {
        foreach (var arrival in Enum.GetValues<WordArrival>())
        {
            Assert.Equal(arrival, WordArrivals.Parse(arrival.Id()));
            Assert.Equal(arrival, WordArrivals.Parse(arrival.Id().ToUpperInvariant()));
        }
        Assert.Equal(["cutoutFold", "cutoutSlide", "cutoutHighlight", "satinRibbon", "splitInk", "mix"],
                     new[] { WordArrival.CutoutFold, WordArrival.CutoutSlide, WordArrival.CutoutHighlight, WordArrival.SatinRibbon,
                             WordArrival.SplitInk, WordArrival.Mix }.Select(arrival => arrival.Id()));
        Assert.Equal(WordArrival.Fade, WordArrivals.Parse("softFocus"));
        Assert.Equal(WordArrival.Fade, WordArrivals.Parse("nonsense"));
        Assert.Equal(WordArrival.Automatic, WordArrivals.Parse(null));
        Assert.Equal(11, WordArrivals.MixChoices.Count);
        Assert.DoesNotContain(WordArrival.Automatic, WordArrivals.MixChoices);
        Assert.DoesNotContain(WordArrival.Mix, WordArrivals.MixChoices);
        Assert.Equal("Satin Ribbon", WordArrival.SatinRibbon.Title());
        Assert.Equal("Mixed entrances", WordArrival.Mix.Title());
        Assert.True(WordArrival.SplitInk.RequiresReveal());
        Assert.True(WordArrival.CutoutSlide.RequiresReveal());
        Assert.False(WordArrival.Pop.RequiresReveal());
        Assert.Equal(12, WordArrival.CutoutSlide.TravelRotation());
        Assert.Equal(12, WordArrival.Orbit.TravelRotation());
        Assert.Equal(0, WordArrival.CutoutFold.TravelRotation());
    }

    [Fact]
    public void ACustomMixGivesEachWordOneOfItsEntrancesBySeed()
    {
        var plain = EntranceChoice.Parse("pop");
        Assert.Equal(WordArrival.Pop, plain.For(CompositionKind.Echo, 12345));
        Assert.Equal(WordArrival.Lift, EntranceChoice.Parse("automatic").For(CompositionKind.Echo, 1));
        // The Mac's default mix: Soft focus, Float up, Cutout · Fold.
        var mix = EntranceChoice.Parse("mix");
        Assert.Equal([WordArrival.Fade, WordArrival.Lift, WordArrival.CutoutFold], mix.MixEntrances);
        Assert.Equal(WordArrival.Fade, mix.For(CompositionKind.Scattered, 3));
        Assert.Equal(WordArrival.Lift, mix.For(CompositionKind.Scattered, 4));
        Assert.Equal(WordArrival.CutoutFold, mix.For(CompositionKind.Scattered, 5));
        // Named entrances are kept in the mix choices' order, whatever order they are given in.
        var named = EntranceChoice.Parse("mix:splitInk, pop,nonsense,automatic,cutoutHighlight");
        Assert.Equal([WordArrival.Pop, WordArrival.CutoutHighlight, WordArrival.SplitInk], named.MixEntrances);
        Assert.Equal(WordArrival.CutoutHighlight, named.For(CompositionKind.Spoken, 1));
        // An empty mix is the layout's own.
        Assert.Equal(WordArrival.Orbit, EntranceChoice.Parse("mix:").For(CompositionKind.Constellation, 7));
        // Words with different seeds take different entrances.
        var seeds = Enumerable.Range(0, 30).Select(i => TimedWordKey.SeedOf("word", 10 + i * 0.37));
        Assert.Equal(3, seeds.Select(seed => mix.For(CompositionKind.Scattered, seed)).Distinct().Count());
    }

    [Fact]
    public void CutoutsAndSwipesFollowTheRevealAndCutoutsGrowFromThreeQuarters()
    {
        // The spring is ignored: half revealed is half way, whatever the spring says.
        var fold = EntranceTransform.Make(0.5, 0.9, WordArrival.CutoutFold, 40, 1, false);
        Assert.Equal(0.5, fold.Eased);
        Assert.Equal(0.76 + 0.24 * 0.5, fold.Scale, 9);
        Assert.Equal(-30 * 0.5, fold.Fold, 9);
        Assert.Equal(0, fold.OffsetX);
        var slide = EntranceTransform.Make(0.25, 0.9, WordArrival.CutoutSlide, 40, -1, false);
        Assert.Equal(-0.75 * 40 * 0.72, slide.OffsetX, 9);
        Assert.Equal(-0.75 * 12, slide.Turn, 9);
        Assert.Equal(0, slide.OffsetY);
        Assert.Equal(0.76 + 0.24 * 0.25, slide.Scale, 9);
        var ribbon = EntranceTransform.Make(0.3, 0.9, WordArrival.SatinRibbon, 40, 1, false);
        Assert.Equal(new EntranceTransform(0, 0, 0, 1, 0, 0.3), ribbon);
        // Unfold keeps its own −48° and follows the spring.
        Assert.Equal(-48 * 0.1, EntranceTransform.Make(0.5, 0.9, WordArrival.Unfold, 40, 1, false).Fold, 9);
        Assert.Equal(EntranceTransform.Rest, EntranceTransform.Make(0.1, 0.1, WordArrival.CutoutSlide, 40, 1, true));
        Assert.Equal(new SizeD(0.72 * 40 * 2, 0), WordArrival.CutoutSlide.TravelBounds(40));
        // Unmasked from the leading edge: 88% shows at the start, all of it once arrived.
        Assert.Equal(0.88, WordArrival.CutoutFold.PaperShown(0), 9);
        Assert.Equal(0.94, WordArrival.CutoutSlide.PaperShown(0.5), 9);
        Assert.Equal(1, WordArrival.CutoutFold.PaperShown(1));
        Assert.Equal(1, WordArrival.Pop.PaperShown(0));
    }

    [Fact]
    public void SwipeInkCrossesTheWordWhileItArrives()
    {
        Assert.False(LyricSwipe.Shows(WordArrival.SatinRibbon, 0, false));
        Assert.True(LyricSwipe.Shows(WordArrival.SatinRibbon, 0.4, false));
        Assert.False(LyricSwipe.Shows(WordArrival.SplitInk, 0.4, true));
        Assert.False(LyricSwipe.Shows(WordArrival.SplitInk, 1, false));
        Assert.False(LyricSwipe.Shows(WordArrival.Pop, 0.4, false));
        Assert.Equal(1, LyricSwipe.Opacity(0.5), 9);
        // The ribbon starts left of the box and leaves on the right (the other way for direction −1).
        var start = LyricSwipe.Ribbon(0.01, 1, 200, 50);
        var end = LyricSwipe.Ribbon(0.99, 1, 200, 50);
        Assert.True(start.X + start.Width / 2 < 0 && end.X + end.Width / 2 > 200);
        Assert.Equal(200 * 0.42, start.Width, 9);
        Assert.Equal(50 * 1.35, start.Height, 9);
        Assert.Equal(-13, start.Turn);
        var back = LyricSwipe.Ribbon(0.01, -1, 200, 50);
        Assert.True(back.X + back.Width / 2 > 200);
        // Split Ink's bars slide past each other.
        var (top, edge, bottom) = LyricSwipe.Split(0.2, 1, 200, 50);
        Assert.Equal(200 * 0.53, top.Width + edge.Width, 9);
        Assert.Equal(200 * 0.06, edge.Width, 9);
        Assert.Equal(top.X + top.Width, edge.X, 9);
        Assert.True(top.X + (top.Width + edge.Width) / 2 < 100 && bottom.X + bottom.Width / 2 > 100);
        Assert.Equal(50 * 0.48, bottom.Height, 9);
        // Colours from the album's accent.
        var palette = LyricSwipePalette.From(0.0, 0.5);
        Assert.NotEqual(palette.Ribbon, palette.Ink);
        Assert.Equal(LyricSwipePalette.From(0.64, 0.55), LyricSwipePalette.Default);
        Assert.Equal(LyricSwipePalette.From(0.3, 0.72), LyricSwipePalette.From(0.3, 5));
        var red = LyricSwipePalette.ForCover([.. Enumerable.Repeat(new Double3(0.9, 0.1, 0.1), 9)]);
        Assert.Equal(LyricSwipePalette.From(0, AlbumColourGrid.Hsb(new Double3(0.9, 0.1, 0.1)).Y), red);
        Assert.Equal(LyricSwipePalette.From(AlbumColourGrid.FallbackHsb[2].X, AlbumColourGrid.FallbackHsb[2].Y),
                     LyricSwipePalette.ForCover([]));
    }

    // ---- "Cutout · Highlight" -------------------------------------------------------------------------------------

    // Ported from LyricHighlightTests.swift: testTheTilePressesInAtTheOnsetAndFadesAtTheLineEnd
    [Fact]
    public void TheTilePressesInAtTheOnsetAndFadesAtTheLineEnd()
    {
        const double onset = 10.0, release = 13.6;
        HighlightPose Pose(double time, bool reduce = false) => LyricHighlightTiming.Pose(time, onset, release, reduce);
        Assert.False(Pose(onset - 0.001).IsVisible);
        Assert.False(Pose(onset).IsVisible);
        var width = 0.0;
        for (var step = 1; step <= 24; step++)
        {
            var current = Pose(onset + LyricHighlightTiming.Press * step / 24);
            Assert.True(current.IsVisible);
            Assert.True(current.Width >= width);
            Assert.True(current.Width <= 1);
            width = current.Width;
        }
        Assert.True(Pose(onset + 0.02).Width < 0.6, "it presses in rather than appearing whole");
        Assert.True(Pose(onset + 0.06).Width > 0.7, "most of the way in half way through");
        Assert.Equal(1, Pose(onset + LyricHighlightTiming.Press).Width, 9);
        Assert.Equal(1, Pose(onset + LyricHighlightTiming.Press).Opacity, 9);
        for (var time = onset + 0.13; time < release; time += 0.05)
            Assert.Equal(new HighlightPose(1, 1), Pose(time));
        var opacity = 1.0;
        for (var step = 1; step <= 30; step++)
        {
            var current = Pose(release + LyricHighlightTiming.Fade * step / 30);
            Assert.True(current.Opacity < opacity || current.Opacity == 0); // the curve's tail is hidden a frame early
            if (current.IsVisible) Assert.Equal(1, current.Width);
            opacity = current.Opacity;
        }
        Assert.False(Pose(release + LyricHighlightTiming.Fade).IsVisible);
        Assert.Equal(0.42, LyricHighlightTiming.Fade, 0.01); // the Mac's 436adda (was 0.30)
        Assert.Equal(0.12, LyricHighlightTiming.Press, 0.03);
    }

    // Ported from LyricHighlightTests.swift: testReduceMotionOnlyFadesTheTile
    [Fact]
    public void ReduceMotionOnlyFadesTheTile()
    {
        const double release = 5.6;
        var opacity = 0.0;
        for (var step = 1; step <= 12; step++)
        {
            var pose = LyricHighlightTiming.Pose(5 + LyricHighlightTiming.Press * step / 12, 5, release, true);
            Assert.Equal(1, pose.Width);
            Assert.True(pose.Opacity >= opacity);
            opacity = pose.Opacity;
        }
        Assert.Equal(1, opacity, 9);
        Assert.Equal(1, LyricHighlightTiming.Pose(5.7, 5, release, true).Width);
        Assert.True(LyricHighlightTiming.Pose(5.7, 5, release, true).Opacity < 1);
    }

    [Fact]
    public void ATileReachesPastItsWordAndPressesInFromTheLeadingEdge()
    {
        // A tile on one frame is a mark: a segment's stretch of the label with its pose.
        var full = new HighlightPose(1, 1);
        var whole = new HighlightMark(0, 1, full);
        var rect = whole.Rect(200, 50, 40, false);
        Assert.Equal(new RectD(-4, -2, 208, 54), rect);
        var half = whole with { Pose = new HighlightPose(1, 0.5) };
        Assert.Equal(new RectD(-4, -2, 104, 54), half.Pressed(200, 50, 40, false));
        Assert.Equal(new RectD(100, -2, 104, 54), half.Pressed(200, 50, 40, true));
        // Two words of one run: their tiles just overlap at the seam.
        var first = new HighlightMark(0, 0.4, full).Rect(200, 50, 40, false);
        var second = new HighlightMark(0.4, 1, full).Rect(200, 50, 40, false);
        Assert.Equal(80 + 1.6, first.X + first.Width, 9);
        Assert.Equal(80 - 1.6, second.X, 9);
        // Right to left: the first word is on the right.
        var rtl = new HighlightMark(0, 0.4, full).Rect(200, 50, 40, true);
        Assert.Equal(200 + 4, rtl.X + rtl.Width, 9);
        Assert.Equal(1, LyricHighlightTiming.TextOpacity(1, 0.9));
        Assert.Equal(0, LyricHighlightTiming.TextOpacity(0, 0.1));
    }

    [Fact]
    public void EachWordOfARunGetsItsOwnTileReleasedAtTheLineEnd()
    {
        // A Latin word sung in syllables is one word; separate words are separate runs.
        LyricWord[] words = [new("beau", 1, 1.2), new("ti", 1.2, 1.4), new("ful", 1.4, 1.8), new("night", 1.9, 2.5)];
        bool[] glue = [false, true, true, false];
        var line = EditorialSetting.Layout(words, glue, 44, 11, 0, true, false);
        var segments = HighlightSegment.Of(line, words, glue, SimpleWordBreaker.Instance, 3);
        Assert.Equal(2, segments.Length);
        Assert.Equal([new HighlightSegment(0, 1, 1, 1.8, 3)], segments[0]);
        Assert.Equal([new HighlightSegment(0, 1, 1.9, 2.5, 3)], segments[1]);
        Assert.Equal(2.5, LyricHighlightTiming.LineEnd(2.2, words));
        Assert.Equal(2.7, LyricHighlightTiming.LineEnd(2.7, words));
    }

    // ---- Echo ----------------------------------------------------------------------------------------------------

    // Ported from CreditsLayerTests.swift: testEchoTakesAnySeedWithoutTrapping (the seeds; drawing every seed is in
    // ScatteredFamilyRenderTests)
    [Fact]
    public void EchoTakesAnySeedWithoutTrapping()
    {
        Assert.Equal((ulong)(41_234 * 31 + 3), EchoBackdrop.WanderSeed(41_234, 3));
        foreach (var seed in new[] { long.MaxValue, long.MinValue, -7L, 0L, 7_381_234_567_890_123_456L, -5_102_938_475_610_293_847L })
        {
            for (var slot = 0; slot < 5; slot++)
            {
                _ = EchoBackdrop.WanderSeed(seed, slot);
                var pose = EchoBackdrop.Pose(slot, seed, 3, 40, false, 60);
                Assert.True(double.IsFinite(pose.X) && double.IsFinite(pose.Y) && double.IsFinite(pose.Angle));
            }
            _ = EchoBackdrop.Side(seed);
        }
        Assert.Equal(EchoBackdrop.WanderSeed(-7, 2), EchoBackdrop.WanderSeed(7, 2));
    }

    [Fact]
    public void EchoRepeatsInStepsOffTheLinesEdgesWithTwoStrengths()
    {
        Assert.Equal(5, EchoBackdrop.Repeats.Count);
        // Two strengths only: the repeat next to the line, and those further off.
        Assert.Equal([0.08, 0.18], EchoBackdrop.Repeats.Select(ghost => ghost.Strength).Distinct().Order());
        // Three rise above the line, smaller and fainter with each step; two are mirrored below it as a reflection.
        var above = EchoBackdrop.Repeats.Where(ghost => ghost.Down < 0).ToArray();
        var below = EchoBackdrop.Repeats.Where(ghost => ghost.Down > 0).ToArray();
        Assert.Equal(3, above.Length);
        Assert.All(above, ghost => Assert.False(ghost.Mirrored));
        Assert.All(below, ghost => Assert.True(ghost.Mirrored));
        for (var k = 1; k < above.Length; k++)
        {
            Assert.True(above[k].SizeShare < above[k - 1].SizeShare);
            Assert.True(above[k].Down < above[k - 1].Down);
        }
        Assert.All(EchoBackdrop.Repeats, ghost => Assert.Equal(0, ghost.Tilt));
        // The seed picks the side the echo drifts to: lines alternate.
        Assert.Equal(1, EchoBackdrop.Side(4));
        Assert.Equal(-1, EchoBackdrop.Side(3));
        Assert.Equal(-1, EchoBackdrop.Side(-3));
        // At rest under Reduce Motion, stepped off the nearer edge of a 60-pixel line; wandering a little otherwise.
        var still = EchoBackdrop.Pose(0, 4, 30, 50, true, 60);
        Assert.Equal(0.30 * 50, still.X, 9);
        Assert.Equal(-30 - 0.48 * 50, still.Y, 9);
        Assert.Equal(0, still.Angle);
        var mirrored = EchoBackdrop.Pose(3, 3, 30, 50, true, 60);
        Assert.Equal(30 + 0.58 * 50, mirrored.Y, 9);
        // Mirrored by the side: the last repeat leans the other way on an odd line.
        Assert.Equal(0.30 * 50, EchoBackdrop.Pose(4, 3, 30, 50, true, 60).X, 9);
        var moving = EchoBackdrop.Pose(1, 4, 30, 50, false, 60);
        Assert.InRange(Math.Abs(moving.X - moving.RestX), 0, 50 * 0.08 + 1e-9);
        Assert.InRange(Math.Abs(moving.Y - moving.RestY), 0, 50 * 0.03 + 1e-9);
        Assert.InRange(Math.Abs(moving.Angle), 0, 0.4 + 1e-9);
        Assert.Equal(0.1 * 0.5 * 1.5, EchoBackdrop.Opacity(new EchoRepeat(1, 0, 0, 0, 0.1, false), 0.5, 1.5), 9);
        // A ghost is shrunk to keep within the title-safe stage (90%), never grown.
        Assert.Equal(1, EchoBackdrop.Fit(new SizeD(10, 5), new SizeD(1200, 600), still));
        var wide = EchoBackdrop.Fit(new SizeD(4000, 50), new SizeD(1200, 600), still);
        Assert.Equal(((1080 / 2.0 - 0.30 * 50) - 4) * 2 / (4000 * 1.02), wide, 9);
    }

    [Fact]
    public void TheSweepRevealsInReadingOrderWithASoftEdge()
    {
        Assert.Equal(0, LyricSweep.Alpha(0, 0, 0.2));
        Assert.Equal(1, LyricSweep.Alpha(1, 1, 0.2));
        Assert.Equal(1, LyricSweep.Alpha(0.1, 0.5, 0.2));
        Assert.Equal(0, LyricSweep.Alpha(0.7, 0.5, 0.2));
        var (solid, clear) = LyricSweep.Edges(0.5, 0.2);
        Assert.Equal(0.4, solid, 9);
        Assert.Equal(0.6, clear, 9);
        Assert.Equal(0.5, LyricSweep.Alpha(0.5, 0.5, 0.2), 9);
    }
}
