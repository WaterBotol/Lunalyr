using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Lyrics.Timing;

/// <summary>Duets and ad-libs: lines that are still sung after the next one starts (from LyricLayerFlowTests).</summary>
public class HeldLineTests
{
    private static LyricLine Line(string[] texts, double start, double step = 0.5, double length = 0.4, bool wordTimed = true)
    {
        var words = texts.Select((t, i) => new LyricWord(t, start + i * step, start + i * step + length)).ToArray();
        return new LyricLine
        {
            Start = start, End = words.Length > 0 ? words[^1].End : start, Text = string.Join(' ', texts), Words = words,
            WordTimed = wordTimed,
        };
    }

    // Ported from LyricLayerFlowTests.swift: testAnOverlappingLineHandsOverWhenItsOwnWordsEnd
    [Fact]
    public void AnOverlappingLineHandsOverWhenItsOwnWordsEnd()
    {
        // Line A is sung from 10 to 14; line B starts at 12, under it.
        var a = Line(["I", "will", "wait", "for", "you"], 10, step: 0.8, length: 0.8) with { End = 14 };
        var b = Line(["Oh", "yeah"], 12, step: 1, length: 1);
        var c = Line(["and", "on"], 15);
        LyricLine[] lines = [a, b, c];
        Assert.Equal(14, LyricPhraseTiming.HandOver(0, lines), 1e-9);
        Assert.True(LyricPhraseTiming.IsHeld(0, lines));
        Assert.Equal(0, LyricLineIndex.HeldLine(1, lines, 13));
        // A's words after 12 are still revealed and it leaves after its last word.
        var pose = new LyricPhraseTiming(a, LyricPhraseTiming.HandOver(0, lines), 13.5);
        Assert.Equal(0, pose.Exit);
        Assert.True(pose.ExitStart > 13.5);
        Assert.True(LyricPhraseTiming.StillLeaving(a, 14, 14.2));
        Assert.False(LyricPhraseTiming.StillLeaving(a, 14, 14.8));
        var motion = LyricMotion.ForWord(13.0, a.Words[3].Start, a.Words[3].End, 0.8, pose.ExitStart, 0.75, 7, 0, false);
        Assert.True(motion.Reveal > 0.9);
        Assert.Equal(0, motion.Leave);
        // B is not held: it ends before C.
        Assert.False(LyricPhraseTiming.IsHeld(1, lines));
        Assert.Equal(15, LyricPhraseTiming.HandOver(1, lines));
    }

    // Ported from LyricLayerFlowTests.swift: testOnlyARealOverlapHoldsALine
    [Fact]
    public void OnlyARealOverlapHoldsALine()
    {
        var ringing = Line(["so", "long"], 20, step: 0.6, length: 0.63); // ends 21.23
        var next = Line(["next"], 21.2);
        Assert.False(LyricPhraseTiming.IsHeld(0, [ringing, next]));
        Assert.Equal(21.2, LyricPhraseTiming.HandOver(0, [ringing, next]));
        var tail = Line(["so", "long"], 20, step: 0.6, length: 0.9); // ends 21.5
        Assert.False(LyricPhraseTiming.IsHeld(0, [tail, next]));
        // Nor timings equal but for rounding, as YRC's sums give.
        var rounded = Line(["爱你", "孤身"], 206.9, step: 0.76, length: 0.76);
        rounded = rounded with { Words = [rounded.Words[0], rounded.Words[1] with { End = 208.42000000000002 }] };
        var yrcNext = Line(["爱你"], 208.42);
        Assert.Null(LyricLineIndex.HeldEnd(0, [rounded, yrcNext]));
        Assert.False(LyricPhraseTiming.IsHeld(0, [rounded, yrcNext]));
        // A note held well into the next line stays until it ends...
        var held = Line(["oh", "stay"], 20, step: 0.6, length: 1.4); // ends 22.0
        Assert.Equal(22.0, LyricLineIndex.HeldEnd(0, [held, next])!.Value, 1e-9);
        Assert.True(LyricPhraseTiming.IsHeld(0, [held, next]));
        // ...and so does an answer that only starts as the next line does.
        var answer = Line(["and", "you"], 20, step: 0.6, length: 0.5);
        answer = answer with { Words = [.. answer.Words, new LyricWord("(yeah)", 21.2, 21.45)] };
        Assert.Equal(21.45, LyricLineIndex.HeldEnd(0, [answer, next])!.Value, 1e-9);
        // An answer of a moment is not worth holding the line for.
        answer = answer with { Words = [answer.Words[0], answer.Words[1], answer.Words[2] with { End = 21.3 }] };
        Assert.Null(LyricLineIndex.HeldEnd(0, [answer, next]));
    }

    // Ported from LyricLayerFlowTests.swift: testAHeldLineIsCappedAndOtherLinesHandOverAsBefore
    [Fact]
    public void AHeldLineIsCappedAndOtherLinesHandOverAsBefore()
    {
        var longLine = Line(["oh", "oh", "oh", "oh"], 10, step: 3, length: 3);
        var next = Line(["next"], 12);
        Assert.Equal(12 + LyricPhraseTiming.LongestHold, LyricPhraseTiming.HandOver(0, [longLine, next]));
        // Line-only lyrics never overlap: they hand over as the next starts.
        var plain = longLine with { WordTimed = false };
        Assert.Equal(12, LyricPhraseTiming.HandOver(0, [plain, next]));
        Assert.False(LyricPhraseTiming.IsHeld(0, [plain, next]));
        var early = Line(["done"], 8);
        Assert.Equal(12, LyricPhraseTiming.HandOver(0, [early, next]));
        Assert.Equal(next.End + 0.8, LyricPhraseTiming.HandOver(1, [early, next]), 1e-9);
    }

    [Fact]
    public void TheActiveLineIsTheLastStartAtOrBeforeTheTime()
    {
        LyricLine[] lines = [Line(["a"], 1), Line(["b"], 3), Line(["c"], 3), Line(["d"], 7)];
        var index = new LyricLineIndex(lines);
        Assert.Null(index.LineAt(0.5));
        Assert.Equal(0, index.LineAt(1));
        Assert.Equal(0, index.LineAt(2.9));
        Assert.Equal(2, index.LineAt(3)); // repeated timestamps: the last of them
        Assert.Equal(3, index.LineAt(100));
        Assert.Null(new LyricLineIndex([]).LineAt(6));
        // A malformed timeline keeps the old forward scan, which stops at the first start after the time.
        LyricLine[] unordered = [Line(["a"], 5), Line(["b"], 2), Line(["c"], 8)];
        Assert.Null(new LyricLineIndex(unordered).LineAt(3));
        Assert.Equal(1, new LyricLineIndex(unordered).LineAt(6));
    }
}
