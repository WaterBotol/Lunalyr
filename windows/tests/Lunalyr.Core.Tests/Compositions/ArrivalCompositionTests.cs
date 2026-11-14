using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>Cascade and Cutout: the rolling stage in its column and collage, and Cutout's labels.</summary>
public class ArrivalCompositionTests
{
    private static readonly WordRevealOptions Options = new();

    private static RollingSong Song(params LyricLine[] lines) => new(lines, SimpleWordBreaker.Instance);

    private static int? NextAfter(IReadOnlyList<LyricWord> words, double time)
    {
        for (var i = 0; i < words.Count; i++) if (words[i].Start > time) return i;
        return null;
    }

    [Fact]
    public void TheArrivalCompositionsRollAndTakeTheirOwnCurvesAndEntrances()
    {
        Assert.True(CompositionKind.Spoken.ArrivalOnly());
        Assert.True(CompositionKind.Cascade.ArrivalOnly());
        Assert.True(CompositionKind.Cutout.ArrivalOnly());
        Assert.False(CompositionKind.Editorial.ArrivalOnly());
        Assert.Equal(RollingStyle.Fan, CompositionKind.Spoken.RollingStyle());
        Assert.Equal(RollingStyle.Column, CompositionKind.Cascade.RollingStyle());
        Assert.Equal(RollingStyle.Collage, CompositionKind.Cutout.RollingStyle());
        // "Match the style": Cascade floats up; Cutout folds in as a cutout (Cutout · Fold), as on the Mac.
        Assert.Equal(WordArrival.Lift, WordArrival.Automatic.Resolved(CompositionKind.Cascade));
        Assert.Equal(WordArrival.CutoutFold, WordArrival.Automatic.Resolved(CompositionKind.Cutout));
        Assert.Equal(WordArrival.Glide, WordArrival.Automatic.Resolved(CompositionKind.Spoken));
        // A chosen entrance is kept.
        Assert.Equal(WordArrival.Pop, WordArrival.Pop.Resolved(CompositionKind.Cutout));
    }

    [Fact]
    public void CascadeAndCutoutSetHeroesLargeFillersSmallAndOnlyHeroesHeavy()
    {
        LyricWord hero = new("dreaming", 10, 10.4), filler = new("the", 11, 11.2), plain = new("rain", 12, 12.3),
            read = new("夢", 13, 13.3);
        var readings = new Dictionary<LyricWord, string> { [read] = "yume" };
        foreach (var composition in new[] { CompositionKind.Cascade, CompositionKind.Cutout })
        {
            var prepared = RollingWords.Prepare([hero, filler, plain, read], 60, readings, composition: composition);
            // Three clear steps (Mac Test Build 88): heroes 0.96, fillers 0.56, the rest 0.70.
            Assert.Equal(60 * 0.96, prepared[0].PointSize, 9);
            Assert.Equal(60 * 0.56, prepared[1].PointSize, 9);
            Assert.Equal(60 * 0.70, prepared[2].PointSize, 9);
            Assert.Equal(60 * 0.70 * 0.88, prepared[3].PointSize, 9);
            Assert.Equal([true, false, false, false], prepared.Select(word => word.Heavy));
        }
        // Spoken: every word large and heavy, never on a label.
        var spoken = RollingWords.Prepare([hero, filler, plain], 60);
        Assert.All(spoken, word => Assert.Equal(66, word.PointSize, 9));
        Assert.All(spoken, word => Assert.True(word.Heavy && !word.Tile));
    }

    [Fact]
    public void CutoutLabelsItsHeroesAndAboutAQuarterOfItsOtherWords()
    {
        var words = Enumerable.Range(0, 400).Select(i => new LyricWord($"w{i}", 10 + i * 0.3, 10 + i * 0.3 + 0.2)).ToArray();
        var cutout = RollingWords.Prepare(words, 50, composition: CompositionKind.Cutout);
        foreach (var word in cutout)
            Assert.Equal(word.Hero || RollingWords.Fnv1a(word.Source.Text) % 4 == 1, word.Tile);
        var share = cutout.Count(word => word.Tile) / (double)cutout.Length;
        Assert.InRange(share, 0.15, 0.35);
        Assert.True(RollingWords.Prepare([new LyricWord("tonight", 1, 1.4)], 50, composition: CompositionKind.Cutout)[0].Tile,
                    "a hero is always on a label");
        Assert.DoesNotContain(RollingWords.Prepare(words, 50, composition: CompositionKind.Cascade), word => word.Tile);
    }

    [Fact]
    public void TheColumnSitsOnTheLeftThirdAndLabelsKeepRoomForTheirTilt()
    {
        var word = new LyricWord("tonight", 10, 10.6);
        RollingTokenState State(CompositionKind composition, bool reduce = false) =>
            RollingTokenState.Make(RollingWords.Prepare([word], 50, composition: composition)[0], null, 0, 10.8, 50, 1200, 0,
                                   reduce, WordArrival.Fade, Options, composition: composition);
        var spoken = State(CompositionKind.Spoken);
        var cascade = State(CompositionKind.Cascade);
        var cutout = State(CompositionKind.Cutout);
        // The pose's own sideways place is within half a type size. Cascade is a flush column 19% of the stage to the
        // left, its edge on the axis (a hero's; the rest tucked in 0.34 of a type size); Cutout's collage 16% left.
        Assert.InRange(spoken.Slot.Dx, -50, 50);
        Assert.Equal(-0.19 * 1200, cascade.Slot.Dx, 9);
        Assert.Equal(1, cascade.Slot.Align);
        Assert.InRange(cutout.Slot.Dx, -0.16 * 1200 - 0.54 * 50, -0.16 * 1200 + 0.54 * 50);
        Assert.Equal(0, cutout.Slot.Align);
        Assert.Equal(0, spoken.Slot.Tilt);
        Assert.Equal(0, cascade.Slot.Tilt);
        Assert.Equal(RollingLabel.Tilt, cutout.Slot.Tilt);
        Assert.Equal(0, State(CompositionKind.Cutout, reduce: true).Slot.Tilt);
        // Each composition's pose comes from its own curves.
        var seed = RollingWords.Prepare([word], 50)[0].Seed;
        Assert.Equal(RollingMotion.Pose(RollingStyle.Column, 0, 10.8 - 10, 10.6 - 10, seed, 0, false), cascade.Pose);
        Assert.Equal(RollingMotion.Pose(RollingStyle.Collage, 0, 10.8 - 10, 10.6 - 10, seed, 0, false), cutout.Pose);
        Assert.Equal(RollingMotion.Pose(RollingStyle.Fan, 0, 10.8 - 10, 10.6 - 10, seed, 0, false), spoken.Pose);
    }

    [Fact]
    public void AWordKeepsItsSeedSizeAndLabelWhenTheWindowMoves()
    {
        // Behaviour test (the Mac's testRollingStylesPreserveTimedWordIdentityAcrossWindows is Verso-attributed and
        // is not translated): a timed word is set the same however many words come before it in the window.
        var words = new[] { new LyricWord("Hold", 10, 10.4), new LyricWord("me", 10.4, 10.8), new LyricWord("tonight", 10.8, 11.6) };
        foreach (var composition in new[] { CompositionKind.Spoken, CompositionKind.Cascade, CompositionKind.Cutout })
        {
            var first = RollingWords.Prepare(words, 44, composition: composition);
            var shifted = RollingWords.Prepare(words[1..], 44, composition: composition);
            Assert.Equal(first[1].Source, shifted[0].Source);
            Assert.Equal(first[1].Seed, shifted[0].Seed);
            Assert.Equal(first[1].Hash, shifted[0].Hash);
            Assert.Equal(first[1].PointSize, shifted[0].PointSize);
            Assert.Equal(first[1].Heavy, shifted[0].Heavy);
            Assert.Equal(first[1].Tile, shifted[0].Tile);
        }
    }

    // Ported from LyricReviewFixesTests.swift: testReduceMotionKeepsCutoutLabelsAndSungWordsStill (the label width and
    // the swell; unsynced lyrics never scroll on Windows, so the PlainLyricsScroll part has nothing to test)
    [Fact]
    public void ReduceMotionKeepsCutoutLabelsStill()
    {
        for (var arrival = 0.0; arrival <= 1.1; arrival += 0.05)
            Assert.Equal(1, RollingLabel.TileWidth(arrival, reduceMotion: true));
        Assert.Equal(0.05, RollingLabel.TileWidth(0, reduceMotion: false));
        Assert.Equal(0.5, RollingLabel.TileWidth(0.5, reduceMotion: false), 9);
        // The word in focus does not zoom; emphasis stays in light and glow.
        Assert.Equal(1, EditorialRunFrame.Swell(heat: 1, glow: 1, gesture: 1.12, reduceMotion: true));
        Assert.True(EditorialRunFrame.Swell(heat: 1, glow: 1, gesture: 1.12, reduceMotion: false) > 1.1);
        // Pressed on with a little give, never further than 6% past the word.
        Assert.Equal(1.06, RollingLabel.TileWidth(1.3, reduceMotion: false), 9);
    }

    // Ported from RollingStackTests.swift: testTheStackDoesNotLurchWhenTheNextLineJoins
    [Fact]
    public void TheStackDoesNotLurchWhenTheNextLineJoinsInCascade()
    {
        var first = new LyricLine { Start = 10, End = 11, Text = "hold on", WordTimed = true,
            Words = [new("hold", 10, 10.6), new("on", 10.6, 10.9)] };
        var second = new LyricLine { Start = 12.1, End = 13, Text = "tonight", WordTimed = true, Words = [new("tonight", 12.1, 12.9)] };
        var song = Song(first, second);
        double Cursor(double time)
        {
            var window = song.Window(0, time, 1.2);
            var prepared = RollingWords.Prepare(window.Words, 50, following: window.Following, composition: CompositionKind.Cascade);
            return RollingWords.Cursor(prepared, time, 0, false);
        }
        // "tonight" joins the window at 12.1 - 1.2 = 10.9, while "on" is still handing over.
        var previous = Cursor(10.7);
        for (var step = 1; step <= 600; step++)
        {
            var now = Cursor(10.7 + step / 1000.0);
            Assert.True(Math.Abs(now - previous) < 0.01, $"the stage jumped at {10.7 + step / 1000.0}");
            previous = now;
        }
    }

    // Ported from RollingStackTests.swift: testDroppingOldWordsNeverMovesTheWordsOnStage (Cascade, Float up)
    [Fact]
    public void DroppingOldWordsNeverMovesTheWordsOnStageInCascade()
    {
        var first = new LyricLine { Start = 10, End = 11.6, Text = "hold on to me", WordTimed = true,
            Words = [new("hold", 10, 10.4), new("on", 10.4, 10.8), new("to", 10.8, 11.1), new("me", 11.1, 11.6)] };
        var second = new LyricLine { Start = 27.6, End = 29.4, Text = "tonight forever", WordTimed = true,
            Words = [new("tonight", 27.6, 28.4), new("forever", 28.4, 29.4)] };
        var song = Song(first, second);
        var anchor = new PointD(600, 700 * 0.54 + RollingMotion.Baseline(RollingStyle.Column, 0) * 50);
        // "me" carries a reading, so it is twice as tall as the rest.
        static SizeD Size(LyricWord word) => new(30 * word.Text.Length + 20, word.Text == "me" ? 140 : 70);
        Dictionary<LyricWord, (PointD Centre, double Shown, double Rank)> Stage(IReadOnlyList<LyricWord> words, double? following,
                                                                              double time)
        {
            var prepared = RollingWords.Prepare(words, 50, following: following, composition: CompositionKind.Cascade);
            var position = RollingWords.Cursor(prepared, time, 0, false);
            var next = NextAfter(words, time);
            var sizes = new List<SizeD>();
            var states = new List<RollingTokenState>();
            var keys = new List<LyricWord>();
            foreach (var token in prepared)
            {
                var rank = position - 1 - token.Index;
                if (rank < -2 || rank > 5.2) continue;
                states.Add(RollingTokenState.Make(token, next, rank, time, 50, 1200, 0, false, WordArrival.Lift, Options,
                    token.Index > 0 ? prepared[token.Index - 1].Source.Start : double.NegativeInfinity,
                    composition: CompositionKind.Cascade));
                sizes.Add(Size(token.Source));
                keys.Add(token.Source);
            }
            var centres = RollingStack.Centres(sizes, states.Select(state => state.Slot).ToList(), anchor, 8);
            return keys.Select((key, k) => (key, (centres[k], states[k].Shown, states[k].Slot.Rank)))
                .ToDictionary(pair => pair.key, pair => pair.Item2);
        }
        var compared = 0;
        LyricWord[] everything = [.. first.Words, .. second.Words];
        for (var step = 0; step <= 10 * 60; step++)
        {
            var time = 21.5 + step / 60.0;
            var index = time < second.Start ? 0 : 1;
            var window = song.Window(index, time, 1.2);
            var drawn = Stage(window.Words, window.Following, time);
            var ideal = Stage(everything, null, time);
            foreach (var (key, unit) in drawn)
            {
                Assert.True(ideal.TryGetValue(key, out var reference), $"an unknown word at {time}");
                Assert.True(Math.Abs(unit.Centre.Y - reference.Centre.Y) < 0.5, $"a word moved at {time}");
                Assert.Equal(reference.Shown, unit.Shown, 9);
                if (unit.Rank > -1.3 && unit.Rank < 0.3) compared++;
            }
            foreach (var (key, unit) in ideal)
                if (unit.Shown > 0.01) Assert.True(drawn.ContainsKey(key), $"a word on stage was dropped at {time}");
        }
        Assert.True(compared > 300, "the next word and the new line must be on stage");
    }

    // Ported from RollingStackTests.swift: testWordsStillOnStageStayInTheWindowAcrossShortLines (Cascade, Float up)
    [Fact]
    public void WordsStillOnStageStayInTheWindowAcrossShortLinesInCascade()
    {
        var lines = Enumerable.Range(0, 8).Select(k =>
        {
            var start = 10 + k * 0.3;
            return new LyricLine { Start = start, End = start + 0.28, Text = $"w{k}", WordTimed = true,
                Words = [new($"w{k}", start, start + 0.25)] };
        }).ToArray();
        var song = Song(lines);
        var everything = lines.SelectMany(line => line.Words).ToArray();
        var prepared = RollingWords.Prepare(everything, 50, composition: CompositionKind.Cascade);
        var index = new Lunalyr.Core.Lyrics.Timing.LyricLineIndex(lines);
        var checkedCount = 0;
        for (var step = 0; step <= 260; step++)
        {
            var time = 10 + step / 100.0;
            var row = index.LineAt(time) ?? 0;
            var window = song.Window(row, time, 1.2).Words.ToHashSet();
            var position = RollingWords.Cursor(prepared, time, 0, false);
            var next = NextAfter(everything, time);
            foreach (var token in prepared.Where(token => token.Source.Start <= time))
            {
                var state = RollingTokenState.Make(token, next, position - 1 - token.Index, time, 50, 1200, 0, false,
                                                   WordArrival.Lift, Options, composition: CompositionKind.Cascade);
                if (state.Shown <= 0.02) continue;
                Assert.True(window.Contains(token.Source), $"{token.Source.Text} vanished at {time}");
                checkedCount++;
            }
        }
        Assert.True(checkedCount > 500);
    }

    // Ported from RollingStackTests.swift: testWideLabelsTurnLess (the turn limits, through Cutout's real preparation;
    // the measured label is checked in the render tests)
    [Fact]
    public void WideLabelsTurnLess()
    {
        static RollingWord Token(string text, string? reading = null)
        {
            var word = new LyricWord(text, 10, 10.5);
            var readings = reading is null ? null : new Dictionary<LyricWord, string> { [word] = reading };
            return RollingWords.Prepare([word], 66, readings, composition: CompositionKind.Cutout)[0];
        }
        Assert.Equal(12, Token("love").TurnLimit);
        Assert.Equal(12, Token("夢見る", "yumemiru").TurnLimit);
        Assert.Equal(10, Token("tonight").TurnLimit, 1.5);
        Assert.True(Token("夢見る少女じゃいられない", "yumemiru shoujo ja irarenai").TurnLimit < 8);
        Assert.True(Token("dakishimeteeiennikagayaite").TurnLimit < 8);
    }
}
