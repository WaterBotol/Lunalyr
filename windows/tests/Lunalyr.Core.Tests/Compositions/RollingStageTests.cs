using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using static Lunalyr.Core.Tests.Compositions.CompositionTestLines;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>Spoken's rolling stage: the window of words, the cursor, the measured stack.</summary>
public class RollingStageTests
{
    private static readonly WordRevealOptions Options = new();

    private static RollingSong Song(params LyricLine[] lines) => new(lines, SimpleWordBreaker.Instance);

    // Ported from LyricLayerFlowTests.swift: testRollingWordsKeepTheirColourWhenTheWindowMoves
    [Fact]
    public void RollingWordsKeepTheirColourWhenTheWindowMoves()
    {
        var lines = Enumerable.Range(0, 6).Select(row => Line([$"one{row}", $"two{row}", $"three{row}"], 10 + row * 2)).ToArray();
        var song = Song(lines);
        // The window looks back four lines: on line 4 it holds lines 0 to 4; on line 5, lines 1 to 5.
        var early = song.Window(4, 18.5, 1.2);
        var later = song.Window(5, 20.5, 1.2);
        var shared = early.Words.Where(later.Words.Contains).ToList();
        Assert.Equal(12, shared.Count);
        Dictionary<LyricWord, double> Slots(RollingWindow window) =>
            RollingWords.Prepare(window.Words, 50, ordinals: window.Ordinals).ToDictionary(word => word.Source, word => word.Slot);
        var before = Slots(early);
        var after = Slots(later);
        foreach (var word in shared)
        {
            Assert.NotEqual(early.Words.ToList().IndexOf(word), later.Words.ToList().IndexOf(word));
            Assert.Equal(before[word], after[word]);
        }
        // Neighbours still take well-separated places along the colours.
        double first = before[lines[2].Words[0]], second = before[lines[2].Words[1]];
        Assert.True(Math.Min(Math.Abs(first - second), 1 - Math.Abs(first - second)) > 0.3);
    }

    // Ported from LyricMotionPolishTests.swift: testReduceMotionDissolvesTheRollingColumnBetweenSettledPlaces
    [Fact]
    public void ReduceMotionDissolvesTheRollingColumnBetweenSettledPlaces()
    {
        var prepared = RollingWords.Prepare(Words(["one", "two", "three"], 10, step: 1), 40);
        Dictionary<double, double> previous = [];
        for (var time = 9.9; time < 12.5; time += 1.0 / 60)
        {
            var cursor = RollingWords.Cursor(prepared, time, 0, reduceMotion: true);
            var stacks = RollingWords.Stacks(cursor, reduceMotion: true);
            Assert.True(stacks.Count <= 2);
            Assert.Equal(1, stacks.Opacity + stacks.SecondOpacity, 9);
            Dictionary<double, double> now = new() { [stacks.Position] = stacks.Opacity };
            if (stacks.Count == 2) now[stacks.SecondPosition] = stacks.SecondOpacity;
            foreach (var position in now.Keys) Assert.Equal(Math.Round(position), position);
            if (previous.Count > 0)
            {
                foreach (var position in now.Keys.Union(previous.Keys))
                    Assert.True(Math.Abs(now.GetValueOrDefault(position) - previous.GetValueOrDefault(position)) < 0.25,
                                $"the column jumped at {time}");
            }
            previous = now;
        }
        Assert.Equal(1, RollingWords.Cursor(prepared, 10 + WallpaperMotion.ReducedDissolve, 0, true), 9);
        var moving = RollingWords.Stacks(1.37, reduceMotion: false);
        Assert.Equal(1, moving.Count);
        Assert.Equal(1.37, moving.Position);
    }

    // Ported from RollingStackTests.swift: testReduceMotionDoesNotRenderTwoPositionsOfAWord (the places the stage
    // draws: each stack sets every word at one settled rank)
    [Fact]
    public void ReduceMotionDoesNotRenderTwoPositionsOfAWord()
    {
        LyricWord[] words = [new("Hold", 10, 10.5), new("steady", 10.55, 11.2)];
        var prepared = RollingWords.Prepare(words, 52);
        var stacks = RollingWords.Stacks(RollingWords.Cursor(prepared, 10.65, 0, true), true);
        foreach (var position in stacks.Count == 2 ? new[] { stacks.Position, stacks.SecondPosition } : [stacks.Position])
        {
            var ranks = prepared.Select(token => position - 1 - token.Index).ToArray();
            Assert.Equal(words.Length, ranks.Distinct().Count());
            Assert.All(ranks, rank => Assert.Equal(Math.Round(rank), rank));
            var states = prepared.Select(token => RollingTokenState.Make(token, null, position - 1 - token.Index, 10.65, 52,
                600, 0, true, WordArrival.Glide, Options)).ToArray();
            Assert.All(states, state => Assert.Equal(Math.Round(state.Slot.Rank), state.Slot.Rank));
        }
    }

    // Ported from RollingStackTests.swift: testTheStackRollsContinuously
    [Fact]
    public void TheStackRollsContinuously()
    {
        SizeD[] sizes = [new(300, 140), new(180, 90), new(420, 160), new(220, 120)];
        PointD[] Centres(double position)
        {
            var slots = Enumerable.Range(0, 4).Select(index =>
            {
                var rank = position - 1 - index;
                return new RollingStack.Slot { Rank = rank, Scale = 0.7 + 0.34 * Math.Exp(-rank * rank * 2.4) };
            }).ToArray();
            return RollingStack.Centres(sizes, slots, new PointD(500, 400), 12);
        }
        var previous = Centres(0);
        for (var step = 1; step <= 3000; step++)
        {
            var now = Centres(step / 1000.0);
            for (var k = 0; k < now.Length; k++)
                Assert.True(Math.Abs(previous[k].Y - now[k].Y) < 1.5, $"a word jumped at cursor {step / 1000.0}");
            previous = now;
        }
        // The word in focus sits on the anchor.
        Assert.Equal(400, Centres(2)[1].Y, 3);
    }

    // Ported from RollingStackTests.swift: testTheStackDoesNotLurchWhenTheNextLineJoins
    [Fact]
    public void TheStackDoesNotLurchWhenTheNextLineJoins()
    {
        var first = new LyricLine { Start = 10, End = 11, Text = "hold on", WordTimed = true,
            Words = [new("hold", 10, 10.6), new("on", 10.6, 10.9)] };
        var second = new LyricLine { Start = 12.1, End = 13, Text = "tonight", WordTimed = true, Words = [new("tonight", 12.1, 12.9)] };
        var song = Song(first, second);
        double Cursor(double time)
        {
            var window = song.Window(0, time, 1.2);
            return RollingWords.Cursor(RollingWords.Prepare(window.Words, 50, following: window.Following), time, 0, false);
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

    // Ported from RollingStackTests.swift: testDroppingOldWordsNeverMovesTheWordsOnStage. The Mac checks this in
    // Cascade with Float up; here in Spoken (the rolling layout built on Windows) with the same entrance.
    [Fact]
    public void DroppingOldWordsNeverMovesTheWordsOnStage()
    {
        var first = new LyricLine { Start = 10, End = 11.6, Text = "hold on to me", WordTimed = true,
            Words = [new("hold", 10, 10.4), new("on", 10.4, 10.8), new("to", 10.8, 11.1), new("me", 11.1, 11.6)] };
        var second = new LyricLine { Start = 27.6, End = 29.4, Text = "tonight forever", WordTimed = true,
            Words = [new("tonight", 27.6, 28.4), new("forever", 28.4, 29.4)] };
        var song = Song(first, second);
        var anchor = new PointD(600, 700 * 0.54 + RollingMotion.Baseline(RollingStyle.Fan, 0) * 50);
        // "me" carries a reading, so it is twice as tall as the rest.
        static SizeD Size(LyricWord word) => new(30 * word.Text.Length + 20, word.Text == "me" ? 140 : 70);
        Dictionary<LyricWord, (PointD Centre, double Shown, double Rank)> Stage(IReadOnlyList<LyricWord> words, double? following,
                                                                              double time)
        {
            var prepared = RollingWords.Prepare(words, 50, following: following);
            var position = RollingWords.Cursor(prepared, time, 0, false);
            int? next = null;
            for (var i = 0; i < words.Count; i++) if (words[i].Start > time) { next = i; break; }
            var sizes = new List<SizeD>();
            var states = new List<RollingTokenState>();
            var keys = new List<LyricWord>();
            foreach (var token in prepared)
            {
                var rank = position - 1 - token.Index;
                if (rank < -2 || rank > 5.2) continue;
                states.Add(RollingTokenState.Make(token, next, rank, time, 50, 1200, 0, false, WordArrival.Lift, Options,
                    token.Index > 0 ? prepared[token.Index - 1].Source.Start : double.NegativeInfinity));
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

    // Ported from RollingStackTests.swift: testWordsStillOnStageStayInTheWindowAcrossShortLines (in Spoken rather
    // than Cascade, with the same entrance)
    [Fact]
    public void WordsStillOnStageStayInTheWindowAcrossShortLines()
    {
        var lines = Enumerable.Range(0, 8).Select(k =>
        {
            var start = 10 + k * 0.3;
            return new LyricLine { Start = start, End = start + 0.28, Text = $"w{k}", WordTimed = true,
                Words = [new($"w{k}", start, start + 0.25)] };
        }).ToArray();
        var song = Song(lines);
        var everything = lines.SelectMany(line => line.Words).ToArray();
        var prepared = RollingWords.Prepare(everything, 50);
        var index = new Lunalyr.Core.Lyrics.Timing.LyricLineIndex(lines);
        var checkedCount = 0;
        for (var step = 0; step <= 260; step++)
        {
            var time = 10 + step / 100.0;
            var row = index.LineAt(time) ?? 0;
            var window = song.Window(row, time, 1.2).Words.ToHashSet();
            var position = RollingWords.Cursor(prepared, time, 0, false);
            int? next = null;
            for (var i = 0; i < everything.Length; i++) if (everything[i].Start > time) { next = i; break; }
            foreach (var token in prepared.Where(token => token.Source.Start <= time))
            {
                var state = RollingTokenState.Make(token, next, position - 1 - token.Index, time, 50, 1200, 0, false,
                                                   WordArrival.Lift, Options);
                if (state.Shown <= 0.02) continue;
                Assert.True(window.Contains(token.Source), $"{token.Source.Text} vanished at {time}");
                checkedCount++;
            }
        }
        Assert.True(checkedCount > 500);
    }

    // Ported from RollingStackTests.swift: testTheFirstWordWaitsOnePlainStepBelowTheFocus
    [Fact]
    public void TheFirstWordWaitsOnePlainStepBelowTheFocus()
    {
        var slot = new RollingStack.Slot { Rank = -1, EntranceY = 30, Turn = 12, EntranceScale = 1.1 };
        SizeD[] size = [new(400, 80)];
        var anchor = new PointD(600, 300);
        Assert.Equal(anchor.Y + 80 + 10, RollingStack.Centres(size, [slot], anchor, 10)[0].Y, 3);
        // Halfway there, halfway down; at the focus, on it.
        Assert.Equal(anchor.Y + 45, RollingStack.Centres(size, [slot with { Rank = -0.5 }], anchor, 10)[0].Y, 3);
        Assert.Equal(anchor.Y, RollingStack.Centres(size, [slot with { Rank = 0 }], anchor, 10)[0].Y, 3);
    }

    // Ported from RollingStackTests.swift: testWideLabelsTurnLess (the turn limits, with the sizes and labels Cutout
    // gives these words; the rendered label is not checked)
    [Fact]
    public void WideLabelsTurnLess()
    {
        static double Limit(string text, string? reading = null)
        {
            var word = new LyricWord(text, 10, 10.5);
            var readings = reading is null ? null : new Dictionary<LyricWord, string> { [word] = reading };
            return RollingWords.Prepare([word], 66, readings, composition: CompositionKind.Cutout)[0].TurnLimit;
        }
        Assert.Equal(12, Limit("love"));
        Assert.Equal(12, Limit("夢見る", "yumemiru"));
        Assert.Equal(10, Limit("tonight"), 1.5);
        Assert.True(Limit("夢見る少女じゃいられない", "yumemiru shoujo ja irarenai") < 8);
        Assert.True(Limit("dakishimeteeiennikagayaite") < 8);
    }

    [Fact]
    public void SpokenSetsEveryWordLargeAndSmallerUnderAReading()
    {
        LyricWord plain = new("tonight", 10, 10.5), read = new("夢", 11, 11.5);
        var prepared = RollingWords.Prepare([plain, read], 60, new Dictionary<LyricWord, string> { [read] = "yume" });
        Assert.Equal(66, prepared[0].PointSize, 9);
        Assert.Equal(66 * 0.88, prepared[1].PointSize, 9);
        Assert.Equal("yume", prepared[1].Reading);
        Assert.True(prepared[0].Hero);
        Assert.False(RollingWords.IsHero(new LyricWord("you", 0, 2)), "a filler is never a hero");
        Assert.True(RollingWords.IsHero(new LyricWord("oh", 0, 0.9)), "a held word is");
        Assert.True(RollingWords.IsFiller("The"));
        Assert.False(RollingWords.IsFiller("then"));
    }

    [Fact]
    public void ALineReadingIsSplitOverItsWordsOrHungUnderTheFirst()
    {
        var spaced = Line(["kimi", "no", "na"], 10);
        var song = new RollingSong([spaced, Line(["one", "two"], 12)], SimpleWordBreaker.Instance,
                                   new Dictionary<int, string> { [0] = "KIMI NO NA wa", [1] = "uno dos" });
        var window = song.Window(1, 12.2, 1.2);
        // Four parts for three words: the whole reading under the first word.
        Assert.Equal("KIMI NO NA wa", window.Readings[spaced.Words[0]]);
        Assert.False(window.Readings.ContainsKey(spaced.Words[1]));
        // One part per word: each word its own.
        Assert.Equal("uno", window.Readings[song.WordsOf(1)[0]]);
        Assert.Equal("dos", window.Readings[song.WordsOf(1)[1]]);
    }

    [Fact]
    public void BackingVocalsAreLiftedOutOfTheRollingWords()
    {
        var line = Line(["Hold", "me", "(oh", "oh)", "tonight"], 10);
        var song = Song(line);
        Assert.Equal(["Hold", "me", "tonight"], song.WordsOf(0).Select(word => word.Text));
        var group = Assert.Single(song.Backing(0));
        Assert.Equal("oh oh", group.Text);
        Assert.Equal([0, 3], song.WordOffsets);
    }
}
