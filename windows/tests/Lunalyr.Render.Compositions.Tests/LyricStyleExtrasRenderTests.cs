using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Compositions.Stage;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// The last of the Lyric style page reaching the compositions: backing vocals on and off, Editorial typography, mixed
/// fonts, the text colour's modes, speed and strength, and the sung sweep. Drawn offscreen.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class LyricStyleExtrasRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private const float Scale = 1;
    private readonly Offscreen _stage = new(Width, Height);
    private static readonly Vector4 Background = Fixtures.Night.Base;
    private static readonly IWordBreaker Breaker = IcuWordBreaker.TryCreate() ?? (IWordBreaker)SimpleWordBreaker.Instance;

    public void Dispose() => _stage.Dispose();

    private IRenderLayer Layer(string id)
    {
        IRenderLayerFactory factory = id switch
        {
            "scattered" => new ScatteredFactory(Breaker, Logger.Null),
            "spoken" => new SpokenFactory(Breaker, Logger.Null),
            "editorial" => new EditorialFactory(Breaker, Logger.Null),
            _ => StageFactories.All(Breaker, Logger.Null).Single(f => f.Id == id),
        };
        var layer = factory.Create();
        layer.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        return layer;
    }

    /// <summary>A frame after a few leading up to it, as the app draws them.</summary>
    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double time)
    {
        for (var t = time - 0.2; t < time; t += 1.0 / 60) _stage.Render(layer, scene, t, 100 + t, Scale, Background);
        _stage.Render(layer, scene, time, 100 + time, Scale, Background);
        return _stage.Pixels();
    }

    private byte[] Draw(string id, SceneSnapshot scene, double time)
    {
        using var layer = Layer(id);
        return Frame(layer, scene, time);
    }

    /// <summary>Drawn on the night background: soft backing words included.</summary>
    private static bool IsLit(byte[] pixels, int i) => pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 60;

    /// <summary>Lit pixels in a band of the stage (fractions of its height, and of its width from <paramref name="left"/>).</summary>
    private static int LitBetween(byte[] pixels, double top, double bottom, double left = 0)
    {
        var count = 0;
        for (var y = (int)(Height * top); y < (int)(Height * bottom); y++)
            for (var x = (int)(Width * left); x < Width; x++)
                if (IsLit(pixels, (y * Width + x) * 4)) count++;
        return count;
    }

    private static SceneSnapshot With(SceneSnapshot scene, Func<LyricStyle, LyricStyle> change) => scene with { Style = change(scene.Style) };

    /// <summary>
    /// Windows' own: switched off, bracketed backing words stay in the lead line instead of the band below it (to the
    /// right of the rolling column in Spoken).
    /// </summary>
    [Theory]
    [InlineData("editorial", 0.0)]
    [InlineData("spoken", 0.58)]
    public void BackingVocalsCanStayInTheLine(string id, double bandLeft)
    {
        var scene = Fixtures.English();
        var on = Draw(id, scene, 14.9);
        var off = Draw(id, With(scene, style => style with { BackingVocals = false }), 14.9);
        Assert.NotEqual(on, off);
        // "(oh oh)" is set small and soft in the lower band with the layer on, and nowhere there with it off.
        Assert.True(LitBetween(on, 0.66, 0.9, bandLeft) > 40, $"backing band drawn: {LitBetween(on, 0.66, 0.9, bandLeft)}");
        Assert.Equal(0, LitBetween(off, 0.66, 0.9, bandLeft));
    }

    /// <summary>Windows' own: "Backing vocals animate like" sets the backing group in the layout chosen.</summary>
    [Fact]
    public void BackingVocalsAnimateInTheChosenLayout()
    {
        var scene = Fixtures.English();
        var own = Draw("editorial", scene, 14.9);
        var echo = Draw("editorial", With(scene, style => style with { BackingStyle = "echo" }), 14.9);
        Assert.NotEqual(own, echo);
    }

    // Ported from FableLayoutTests.swift: testHonoursEditorialTypographyAndChosenEntrances (the Editorial typography part: the stage
    // layouts draw differently with it on)
    [Theory]
    [InlineData("fable")]
    [InlineData("broadside")]
    public void EditorialTypographyReachesTheStageLayouts(string id)
    {
        var scene = Fixtures.English();
        var plain = Draw(id, scene, 11.9);
        var editorial = Draw(id, With(scene, style => style with { EditorialTypography = true, EditorialAmount = 1 }), 11.9);
        Assert.NotEqual(plain, editorial);
    }

    /// <summary>Windows' own: "Add Editorial typography" and "Randomise lyric fonts" change how Scattered and Spoken set words.</summary>
    [Theory]
    [InlineData("scattered")]
    [InlineData("spoken")]
    public void TypographyChoicesChangeTheWords(string id)
    {
        var scene = Fixtures.English();
        var plain = Draw(id, scene, 11.9);
        Assert.NotEqual(plain, Draw(id, With(scene, style => style with { EditorialTypography = true }), 11.9));
        Assert.NotEqual(plain, Draw(id, With(scene, style => style with { RandomFonts = true }), 11.9));
        // At an amount of 0 the switch adds nothing to a layout without Editorial's typography of its own.
        Assert.Equal(plain, Draw(id, With(scene, style => style with { EditorialTypography = true, EditorialAmount = 0 }), 11.9));
    }

    // Ported from LyricColourTests.swift: testSungSweepTravelsFromWhiteAcrossAWholeLine. The Mac draws one line view with
    // a coral sweep; here Editorial draws the line-timed fixture with the cover's sung colour, white before it.
    [Fact]
    public void SungSweepTravelsFromWhiteAcrossAWholeLine()
    {
        var scene = With(Fixtures.LineTimed(), style => style with { ColourMode = LyricColourMode.SungSweep });
        var line = Assert.IsType<LyricResult.Synced>(scene.Lyrics).Lines[0];
        var end = EstimatedWords.EstimatedSweepEnd(line);
        using var layer = Layer("editorial");
        (int White, int Coloured) Pixels(double time)
        {
            var pixels = Frame(layer, scene, time);
            int white = 0, coloured = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (Math.Max(r, Math.Max(g, b)) < 166) continue;
                if (Math.Min(r, Math.Min(g, b)) > 225) white++;
                else if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 50) coloured++;
            }
            return (white, coloured);
        }
        // Once the line has made its entrance (the Mac's test draws the line without one), the sweep has barely begun.
        var start = Pixels(line.Start + 0.6);
        var middle = Pixels((line.Start + end) / 2);
        // Near its end, before it hands over to the next line (its sweep ends as the next one starts).
        var done = Pixels(end - 0.4);
        Assert.True(start.White > 150, $"{start}");
        Assert.True(start.Coloured < middle.Coloured, $"{start} {middle}");
        Assert.True(middle.White > 50, $"{middle}");
        Assert.True(middle.Coloured > 50, $"{middle}");
        Assert.True(middle.White < start.White, $"{start} {middle}");
        Assert.True(done.White < middle.White && done.White < start.White / 4, $"{done} {middle}");
        Assert.True(done.Coloured > middle.Coloured, $"{done} {middle}");
    }

    /// <summary>Windows' own: each colour mode, the custom colours and the strength change the words' colour.</summary>
    [Fact]
    public void ColourModesChangeTheInk()
    {
        var scene = Fixtures.English();
        var drift = Draw("editorial", scene, 11.9);
        foreach (var mode in new[] { LyricColourMode.SungSweep, LyricColourMode.MusicReactive, LyricColourMode.CustomGradient })
            Assert.NotEqual(drift, Draw("editorial", With(scene, style => style with { ColourMode = mode }), 11.9));
        var custom = With(scene, style => style with { ColourMode = LyricColourMode.CustomGradient });
        Assert.NotEqual(Draw("editorial", custom, 11.9),
                        Draw("editorial", With(custom, style => style with { ColourCustom = new ValueList(["#33CC66", "#3366FF"]) }), 11.9));
        Assert.NotEqual(drift, Draw("editorial", With(scene, style => style with { ColourStrength = 0 }), 11.9));
    }

    /// <summary>Windows' own: the settled ink takes the mode, strength and custom colours; speed scales the drift.</summary>
    [Fact]
    public void TheSettledInkFollowsTheColourSettings()
    {
        var palette = Fixtures.Night;
        var custom = LyricInks.Settled(palette, LyricStyle.Default with
        {
            ColourMode = LyricColourMode.CustomGradient, ColourCustom = new ValueList(["#33CC66", "#3366FF"]),
        });
        Assert.Equal(LyricColourMode.CustomGradient, custom.Mode);
        Assert.Equal(2, custom.StopCount);
        var neutral = LyricInks.Settled(palette, LyricStyle.Default with { ColourStrength = 0 });
        Assert.True(neutral.Stops.S0.Chroma < 0.02, $"{neutral.Stops.S0.Chroma}");
        var sweep = LyricInks.Settled(palette, LyricStyle.Default with { ColourMode = LyricColourMode.SungSweep });
        Assert.True(sweep.Sample(0.5).Sweeps);
        var reactive = LyricStyle.Default with { ColourMode = LyricColourMode.MusicReactive };
        Assert.True(LyricInks.Settled(palette, reactive, 1).Stops.S0.L > LyricInks.Settled(palette, reactive, 0).Stops.S0.L);
        Assert.Equal(LyricInks.DriftPhase(20), LyricInks.DriftPhase(10, speed: 2));
        Assert.Equal(LyricInks.DriftPhase(10, speed: 2), LyricInks.DriftPhase(10, speed: 9)); // clamped to 2
    }
}
