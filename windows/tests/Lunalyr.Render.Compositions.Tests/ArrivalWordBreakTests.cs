using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Lunalyr.Platform.Readings;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>Cutout builds an unspaced line word by word, as Spoken does, its labels included.</summary>
public class ArrivalWordBreakTests
{
    private static readonly IWordBreaker Breaker = IcuWordBreaker.TryCreate() ?? (IWordBreaker)SimpleWordBreaker.Instance;
    private static readonly WordRevealOptions Options = new();

    private const string UnspacedTtml = """<tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" itunes:timing="Word" xml:lang="ja"><body><div><p begin="10.000" end="13.000"><span begin="10.000" end="10.600">夕焼け</span><span begin="10.600" end="10.800">の</span><span begin="10.900" end="11.500">帰り道</span><span begin="11.500" end="11.700">を</span><span begin="11.800" end="12.400">歩い</span><span begin="12.400" end="13.000">た</span></p></div></body></tt>""";

    // Ported from LyricLayerFlowTests.swift: testARollingStageBuildsAnUnspacedLineWordByWord (the part prepared as
    // Cutout: each word arrives on its own onset)
    [Fact]
    public void ACutoutLineIsBuiltWordByWord()
    {
        var lines = TtmlParser.Parse(UnspacedTtml, 60);
        var window = new RollingSong(lines, Breaker).Window(0, 10.1, 1.2);
        Assert.Equal(["夕焼けの", "帰り道を", "歩いた"], window.Words.Select(word => word.Text));
        var prepared = RollingWords.Prepare(window.Words, 60, window.Readings, parts: window.Parts, composition: CompositionKind.Cutout);
        Assert.Equal(["帰り道", "を"], prepared[1].Parts.Select(word => word.Text));
        Assert.Equal(prepared[1].Parts.Count + 1, prepared[1].Boundaries.Count);
        double Opacity(int index, double time)
        {
            int? next = null;
            for (var i = 0; i < prepared.Length; i++) if (prepared[i].Source.Start > time) { next = i; break; }
            return WordPresentation.Make(prepared[index].Source, index, next, time, Options,
                eligibleSince: index > 0 ? prepared[index - 1].Source.Start : double.NegativeInfinity).Opacity;
        }
        Assert.True(Opacity(0, 10.4) > 0.5);
        Assert.True(Opacity(1, 10.4) <= 0.25, "at most the next word's faint preview");
        Assert.Equal(0, Opacity(2, 10.4));
        Assert.True(Opacity(1, 11.3) > 0.5);
        Assert.Equal(0, Opacity(2, 11.3), 0.25);
        Assert.True(Opacity(2, 12.3) > 0.5);
        // A labelled glued run still arrives part by part.
        var run = prepared[1];
        var state = RollingTokenState.Make(run, 2, 0, 11.0, 60, 1000, 0, false, WordArrival.Unfold, Options,
                                           composition: CompositionKind.Cutout);
        var reveal = new double[run.Parts.Count];
        Assert.True(RollingTokenState.PartReveal(run, 11.0, state.Presentation.Opacity, 0, false, Options, reveal));
        Assert.Equal(1, reveal[0]);
        Assert.True(reveal[1] < 0.5, "を is not there yet");
    }
}
