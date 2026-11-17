using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Compositions.Stage;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// Show translations under lyrics, drawn offscreen in every layout: a line's translation from the lyric source is drawn
/// under it (over the foot of a rolling stage), only while translations are shown, and frames that draw it allocate
/// nothing once the line is set.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class TranslationRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private const float Scale = 1;
    private readonly Offscreen _stage = new(Width, Height);
    private static readonly Vector4 Background = Fixtures.Night.Base;

    public void Dispose() => _stage.Dispose();

    public static TheoryData<string> Layouts() =>
    [
        "editorial", "spoken", "cascade", "cutout", "scattered", "constellation", "constellationEditorial", "echo",
        .. StageRenderTests.Ids,
    ];

    private IRenderLayer Layer(string id)
    {
        var breaker = IcuWordBreaker.TryCreate();
        IRenderLayerFactory[] factories =
        [
            new EditorialFactory(breaker, Logger.Null), new SpokenFactory(breaker, Logger.Null), new CascadeFactory(breaker, Logger.Null),
            new CutoutFactory(breaker, Logger.Null), new ScatteredFactory(breaker, Logger.Null),
            new ConstellationFactory(breaker, Logger.Null), new ConstellationEditorialFactory(breaker, Logger.Null),
            new EchoFactory(breaker, Logger.Null), .. StageFactories.All(breaker, Logger.Null),
        ];
        var layer = factories.Single(factory => factory.Id == id).Create();
        layer.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        return layer;
    }

    private static readonly string[] English =
        ["In the time passing by", "I remember you", "On the screen we gazed at so wistfully, the two of us", "were laughing"];

    /// <summary>The Japanese fixture with an English translation on each sung line, readings off, translations shown.</summary>
    internal static SceneSnapshot Translated(bool wordTimed = true, bool shown = true)
    {
        var scene = ExtrasFixtures.JapaneseSyllables();
        var synced = Assert.IsType<LyricResult.Synced>(scene.Lyrics);
        var sung = 0;
        var lines = synced.Lines.Select(line => line.IsInstrumental ? line
            : line with { Translation = English[sung++ % English.Length], WordTimed = wordTimed && line.WordTimed }).ToList();
        return scene with
        {
            Lyrics = new LyricResult.Synced(lines, synced.Provenance),
            Style = scene.Style with { Readings = ReadingPlacement.Off, Translations = shown },
        };
    }

    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double time)
    {
        _stage.Render(layer, scene, time, 100, Scale, Background);
        return _stage.Pixels();
    }

    private (byte[] With, byte[] Without) Pair(string id, SceneSnapshot scene, double until)
    {
        using var with = Layer(id);
        using var without = Layer(id);
        var off = scene with { Style = scene.Style with { Translations = false } };
        for (var t = until - 2; t < until; t += 1.0 / 15) { Frame(with, scene, t); Frame(without, off, t); }
        return (Frame(with, scene, until), Frame(without, off, until));
    }

    private static int Differing(byte[] a, byte[] b, int fromRow = 0)
    {
        var differing = 0;
        for (var i = fromRow * Width * 4; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 24) differing++;
        return differing;
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void ATranslationIsDrawnUnderItsLineOnlyWhileShown(string id)
    {
        // "I remember you" under あなたを思い出す, well into the line.
        var (a, b) = Pair(id, Translated(), 21.4);
        var differing = Differing(a, b);
        Assert.True(differing > 60, $"{id}: no translation was drawn ({differing} pixels differ)");
    }

    private static int DifferingRows(byte[] a, byte[] b, int fromRow, int toRow)
    {
        var differing = 0;
        for (var i = fromRow * Width * 4; i < Math.Min(a.Length, toRow * Width * 4); i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 24) differing++;
        return differing;
    }

    private static SceneSnapshot Placed(TranslationPlacement place)
    {
        var scene = Translated();
        return scene with { Style = scene.Style with { TranslationPlacement = place } };
    }

    // The Mac's 5783d638: Translation sits under or over its line, or at the bottom or top of the screen.
    [Theory]
    [InlineData("editorial", TranslationPlacement.Above)]
    [InlineData("editorial", TranslationPlacement.Bottom)]
    [InlineData("editorial", TranslationPlacement.Top)]
    [InlineData("spoken", TranslationPlacement.Above)]
    [InlineData("spoken", TranslationPlacement.Top)]
    [InlineData("hinge", TranslationPlacement.Above)]
    [InlineData("hinge", TranslationPlacement.Top)]
    [InlineData("cutout", TranslationPlacement.Bottom)]
    public void EveryPlaceDrawsTheTranslation(string id, TranslationPlacement place)
    {
        var (a, b) = Pair(id, Placed(place), 21.4);
        var differing = Differing(a, b);
        Assert.True(differing > 60, $"{id} {place}: no translation was drawn ({differing} pixels differ)");
    }

    [Theory]
    [InlineData("editorial")]
    [InlineData("hinge")]
    public void AtTheScreensEdgeTheTranslationSitsThere(string id)
    {
        // At the top, the frames differ in the stage's top fifth; at the bottom, in its bottom fifth.
        var (topWith, topWithout) = Pair(id, Placed(TranslationPlacement.Top), 21.4);
        Assert.True(DifferingRows(topWith, topWithout, 0, Height / 5) > 60, $"{id}: nothing was drawn at the top");
        var (bottomWith, bottomWithout) = Pair(id, Placed(TranslationPlacement.Bottom), 21.4);
        Assert.True(DifferingRows(bottomWith, bottomWithout, Height * 4 / 5, Height) > 60, $"{id}: nothing was drawn at the bottom");
    }

    [Theory]
    [InlineData("editorial")]
    [InlineData("spoken")]
    [InlineData("hinge")]
    public void ALineTimedLineShowsItsTranslationToo(string id)
    {
        var (a, b) = Pair(id, Translated(wordTimed: false), 21.4);
        Assert.True(Differing(a, b) > 60, $"{id}: no translation was drawn under a line-timed line");
    }

    [Theory]
    [InlineData("spoken")]
    [InlineData("cascade")]
    [InlineData("cutout")]
    public void ARollingStageDrawsItsTranslationOverItsFoot(string id)
    {
        // The words keep clear of the band and the translation rests in it: the frames differ in the stage's foot.
        var (a, b) = Pair(id, Translated(), 21.4);
        var band = LineTranslations.RollingBand(ViewportMetrics.For(Width, Height, Scale).BaseSize, 1, true);
        var foot = (int)(Height - (Height - ViewportMetrics.For(Width, Height, Scale).Stage.Height) / 2 - band);
        Assert.True(Differing(a, b, foot) > 60, $"{id}: nothing was drawn in the translation band");
    }

    [Theory]
    [InlineData("editorial")]
    [InlineData("spoken")]
    [InlineData("hinge")]
    public void ASongWithoutTranslationsDrawsAsBefore(string id)
    {
        // Translations shown, but the song has none: nothing moves, no band is kept.
        var plain = ExtrasFixtures.JapaneseSyllables();
        var scene = plain with { Style = plain.Style with { Readings = ReadingPlacement.Off } };
        var shown = scene with { Style = scene.Style with { Translations = true } };
        using var with = Layer(id);
        using var without = Layer(id);
        for (var t = 19.4; t < 21.4; t += 1.0 / 15) { Frame(with, shown, t); Frame(without, scene, t); }
        Assert.Equal(0, Differing(Frame(with, shown, 21.4), Frame(without, scene, 21.4)));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void FramesWithATranslationDoNotAllocate(string id)
    {
        using var layer = Layer(id);
        var scene = Translated();
        for (var t = 19.0; t < 21.0; t += 1.0 / 30) _stage.Render(layer, scene, t, t, Scale, Background);
        // Line setup is spread over frames: let it finish, so what follows only draws.
        _stage.Settle(layer, scene, 19.0, Scale, Background);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var t = 19.0; t < 21.0; t += 1.0 / 60) _stage.Render(layer, scene, t, t, Scale, Background);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Writes frames to look at (not run by default): set LUNALYR_TRANSLATION_SHOTS to a folder.</summary>
    [Fact]
    public void ShotsForAPersonToLookAt()
    {
        if (Environment.GetEnvironmentVariable("LUNALYR_TRANSLATION_SHOTS") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        foreach (var (id, wordTimed) in new[] { ("editorial", true), ("spoken", true), ("hinge", true), ("editorial", false) })
        {
            using var layer = Layer(id);
            var scene = Translated(wordTimed);
            for (var t = 19.4; t <= 21.4; t += 1.0 / 15) Frame(layer, scene, t);
            _stage.SavePng(Path.Combine(folder, $"translation-{id}{(wordTimed ? "" : "-line")}.png"));
        }
    }
}
