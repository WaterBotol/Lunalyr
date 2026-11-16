using System.Numerics;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// A glued run's parts appear one by one, each through its own slice of the baked label (the Mac's
/// TokenRevealEdgeTests.swift, 55fe0aa and c011a6f). The slices' edges fall where the letters do in the face they are
/// set in, not by character count, so a mixed run ("HAPPYなイメージ") never shows half of a character still to come.
/// Windows' slices are hard-edged and reach the bitmap's edges, so the Mac's feather and overreach have nothing to fix
/// here.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class TokenRevealEdgeTests : IDisposable
{
    private readonly Offscreen _stage = new(64, 64, warp: true);
    private readonly TextKit _kit;

    public TokenRevealEdgeTests()
    {
        _kit = new TextKit(_stage.Dc, _stage.DWrite);
        _kit.SetFamily("Impact");
    }

    public void Dispose()
    {
        _kit.Dispose();
        _stage.Dispose();
    }

    private static LyricWord[] Tokens(params string[] texts) => [.. texts.Select((text, i) => new LyricWord(text, i, i + 1))];

    // Ported from TokenRevealEdgeTests.swift: testAMixedRunsEdgesFallBetweenItsCharacters
    [Fact]
    public void A_mixed_runs_edges_fall_between_its_characters()
    {
        string[] labels = ["HAPPY", "な", "イ", "メ", "ー", "ジ"];
        var counted = LyricRunParts.Boundaries(Tokens(labels));
        var text = string.Concat(labels);
        using var layout = _kit.Layout(text, Face.Heavy, 60, "ja-jp", false);
        var ink = _kit.Bake(layout, Vector4.One, 1, default);
        // The edge before メ is where メ starts.
        layout.HitTestTextPosition((uint)"HAPPYなイ".Length, false, out var x, out _, out _);
        var me = (x - layout.Metrics.Left) / layout.Metrics.Width;
        Assert.Equal(me, ink.Measured(counted[3]), 4);
        Assert.True(Math.Abs(counted[3] - me) > 0.02, $"by count the edge sits inside a character ({counted[3]} vs {me})");
        // Every edge in order, the ends kept.
        var previous = 0.0;
        for (var k = 1; k < counted.Length - 1; k++)
        {
            var edge = ink.Measured(counted[k]);
            Assert.True(edge >= previous, $"edge {k}");
            previous = edge;
        }
        Assert.Equal(0, ink.Measured(0));
        Assert.Equal(1, ink.Measured(1));
        ink.Dispose();
    }

    // Ported from TokenRevealEdgeTests.swift: testRightToLeftAndSingleTokensKeepTheCharacterShare (the right-to-left half
    // is SliceRect's: it leaves those edges alone); and an edge that isn't on a whole letter (a reading's own measured
    // share) is kept as it is.
    [Fact]
    public void Edges_off_a_whole_letter_are_kept()
    {
        using var layout = _kit.Layout("好きだけじゃ", Face.Heavy, 52, "ja-jp", false);
        var ink = _kit.Bake(layout, Vector4.One, 1, default);
        Assert.Equal(0.437, ink.Measured(0.437), 9);
        // 3 of 6, on a whole letter: where だ's successor け starts (the small ゃ makes the shares uneven).
        layout.HitTestTextPosition(3, false, out var x, out _, out _);
        Assert.Equal((x - layout.Metrics.Left) / layout.Metrics.Width, ink.Measured(0.5), 4);
        ink.Dispose();
    }
}
