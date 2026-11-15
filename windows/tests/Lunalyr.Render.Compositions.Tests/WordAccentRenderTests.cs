using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Compositions.Stage;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// Word symbols and action words (the Mac's ad5a6a6 and cba108f) in every layout that draws them: the symbol shows
/// once its word is sung, an action moves its word only while it runs, and both switched off change nothing.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class WordAccentRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private readonly Offscreen _stage = new(Width, Height);
    private static readonly Vector4 Background = Fixtures.Night.Base;
    private static readonly IWordBreaker Breaker = IcuWordBreaker.TryCreate() ?? (IWordBreaker)SimpleWordBreaker.Instance;

    public void Dispose() => _stage.Dispose();

    public static readonly TheoryData<string> Layouts = new() { "scattered", "constellation", "echo", "spoken", "cascade", "hinge", "torque", "fable" };

    private static IRenderLayerFactory Factory(string id) => id switch
    {
        "scattered" => new ScatteredFactory(Breaker),
        "constellation" => new ConstellationFactory(Breaker),
        "echo" => new EchoFactory(Breaker),
        "spoken" => new SpokenFactory(Breaker),
        "cascade" => new CascadeFactory(Breaker),
        "hinge" => new HingeFactory(Breaker),
        "torque" => new TorqueFactory(Breaker),
        _ => new FableFactory(Breaker),
    };

    private byte[] Frame(string id, bool symbols, bool actions, double time)
    {
        using var layer = Factory(id).Create();
        layer.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), 1);
        var scene = Fixtures.Accents(LyricStyle.Default with { WordSymbols = symbols, ActionWords = actions });
        // Walk up to the moment, so the line is set and its entrances have landed.
        for (var t = Math.Max(0, time - 1.5); t < time; t += 0.25) _stage.Render(layer, scene, t, 100 + t, 1, Background);
        _stage.Render(layer, scene, time, 100 + time, 1, Background);
        return _stage.Pixels();
    }

    private static int Lit(byte[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 90) count++;
        return count;
    }

    private static int Differing(byte[] a, byte[] b)
    {
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 24) count++;
        return count;
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void A_symbol_shows_beside_its_word_once_sung(string id)
    {
        // "Frozen hearts under the sun": "sun" is sung at 20.0 s and its symbol is in by 20.3 s. What changes over that
        // time, with symbols and without: the sun symbol is the difference (the room for it is kept either way).
        var on = Differing(Frame(id, symbols: true, actions: false, 20.35), Frame(id, symbols: true, actions: false, 19.95));
        var off = Differing(Frame(id, symbols: false, actions: false, 20.35), Frame(id, symbols: false, actions: false, 19.95));
        Assert.True(on > off + 8, $"{id}: {on} pixels changed with symbols, {off} without");
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void An_action_moves_its_word_only_while_it_runs(string id)
    {
        // "spin" is sung at 12.25 s and turns for 0.8 s.
        var turning = Differing(Frame(id, false, true, 12.6), Frame(id, false, false, 12.6));
        Assert.True(turning > 100, $"{id}: the spin changed {turning} pixels");
        var done = Differing(Frame(id, false, true, 13.6), Frame(id, false, false, 13.6));
        Assert.True(done < 20, $"{id}: {done} pixels still differ after the spin");
    }
}
