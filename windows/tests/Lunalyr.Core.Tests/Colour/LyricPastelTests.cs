using System.Numerics;
using Lunalyr.Core.Colour;

namespace Lunalyr.Core.Tests.Colour;

/// <summary>
/// "Make the colours pastel", for the custom gradient: off keeps the picked colours (lightened only as contrast needs);
/// the contrast floors hold either way; other modes always ease to pastel. Ported from the Mac's LyricPastelTests.swift
/// (cab0212, 7957a6e, 1763cce) and its SungSweepPaletteTests change (939fd78). The stored-setting half is in the Shell
/// tests.
/// </summary>
public sealed class LyricPastelTests
{
    private static readonly OkLab[] Deep = [.. new[] { "#1E3A8A", "#B91C1C", "#047857" }.Select(hex => ColourHex.ToOkLab(hex)!.Value)];
    private static readonly OkLab[] Soft = [.. new[] { "#F9D29D", "#F28FAD", "#9AD0F5" }.Select(hex => ColourHex.ToOkLab(hex)!.Value)];
    private static readonly Vector3[] Album = [new(0.95f, 0.55f, 0.42f), new(0.35f, 0.25f, 0.55f)];

    private static LyricInk Settled(LyricInkInputs inputs)
    {
        var director = new LyricInkDirector();
        var ink = director.Ink(inputs, 0);
        for (var step = 1; step <= 80; step++) ink = director.Ink(inputs, step * 0.25);
        return ink;
    }

    private static LyricInkInputs Inputs(LyricColourMode mode, OkLab[] custom, double backdrop, bool pastel) =>
        new(mode) { Custom = custom, Album = Album, Backdrop = backdrop, Pastel = pastel };

    // Ported from LyricPastelTests.swift: testOffKeepsTheCustomColoursAsPicked
    [Fact]
    public void OffKeepsTheCustomColoursAsPicked()
    {
        var ink = Settled(Inputs(LyricColourMode.CustomGradient, Soft, 0.03, pastel: false));
        foreach (var (stop, picked) in new[] { ink.Stops.S0, ink.Stops.S1, ink.Stops.S2 }.Zip(Soft))
        {
            Assert.Equal(picked.L, stop.L, 0.01);
            Assert.Equal(picked.Chroma, stop.Chroma, 0.01);
        }
        var eased = Settled(Inputs(LyricColourMode.CustomGradient, Soft, 0.03, pastel: true));
        Assert.True(Math.Abs(eased.Stops.S0.Chroma - ink.Stops.S0.Chroma) > 0.01, "on, they are eased to pastel");
    }

    // Ported from LyricPastelTests.swift: testContrastFloorsHoldWithoutPastel
    [Fact]
    public void ContrastFloorsHoldWithoutPastel()
    {
        foreach (var backdrop in new[] { 0.02, 0.1, 0.2 })
        {
            foreach (var custom in new[] { Deep, Soft })
            {
                var ink = Settled(Inputs(LyricColourMode.CustomGradient, custom, backdrop, pastel: false));
                for (var step = 0; step <= 12; step++)
                {
                    // As the stage draws a word.
                    var colour = Perceptual.Readable(ink.Gradient(step / 12.0), backdrop, ink.Contrast, keepingColour: true);
                    var ratio = (Perceptual.Luminance(colour) + 0.05) / (backdrop + 0.05);
                    Assert.True(ratio >= LyricInk.OwnColourContrast - 0.05, $"backdrop {backdrop}, slot {step}: {ratio}");
                }
            }
        }
    }

    // Ported from LyricPastelTests.swift: testOnlyTheCustomGradientCanTurnPastelOff
    [Fact]
    public void OnlyTheCustomGradientCanTurnPastelOff()
    {
        Assert.False(LyricColourOptions.PastelApplies(false, LyricColourMode.CustomGradient));
        foreach (var mode in Enum.GetValues<LyricColourMode>().Where(m => m != LyricColourMode.CustomGradient))
        {
            Assert.True(LyricColourOptions.PastelApplies(false, mode), mode.ToString());
        }
        Assert.True(LyricColourOptions.PastelApplies(true, LyricColourMode.CustomGradient));
    }

    // Ported from LyricPastelTests.swift: testAPickedRedStaysRedWithPastelOff
    /// <summary>Pure red with pastel off stays red over a dimmed video: lifted only to the gentler floor, its hue kept.</summary>
    [Fact]
    public void APickedRedStaysRedWithPastelOff()
    {
        var red = ColourHex.ToOkLab("#FF0000")!.Value;
        var ink = Settled(Inputs(LyricColourMode.CustomGradient, [red, red], 0.2, pastel: false));
        Assert.Equal(LyricInk.OwnColourContrast, ink.Contrast);
        var drawn = Perceptual.Readable(ink.Gradient(0.5), 0.2, ink.Contrast, keepingColour: true);
        Assert.Equal(red.Hue, drawn.Hue, 0.15);
        Assert.True(drawn.Chroma > 0.6 * red.Chroma, "still red, not pink");
        var eased = Settled(Inputs(LyricColourMode.CustomGradient, [red, red], 0.2, pastel: true));
        Assert.Equal(LyricInk.MinimumContrast, eased.Contrast);
    }

    // Ported from LyricPastelTests.swift: testSungSweepUsesTheCoverNotCustomColours
    /// <summary>Sung sweep follows the cover, never the custom gradient's colours (which always exist, so they used to take over every sweep).</summary>
    [Fact]
    public void SungSweepUsesTheCoverNotCustomColours()
    {
        var red = ColourHex.ToOkLab("#FF0000")!.Value;
        var ink = Settled(Inputs(LyricColourMode.SungSweep, [red, red, red], 0.03, pastel: true));
        double[] hues = [ink.Stops.S0.Hue, ink.Stops.S1.Hue];
        Assert.True(hues.Any(hue => Math.Abs(hue - red.Hue) > 0.5), $"cover colours, not red: {string.Join(", ", hues)}");
        var withoutCustom = Settled(Inputs(LyricColourMode.SungSweep, [], 0.03, pastel: true));
        Assert.Equal(withoutCustom.Stops.S0.Hue, ink.Stops.S0.Hue, 0.01);
    }

    // Ported from LyricColourTests.swift: testCustomColoursDoNotReplaceTheCover (the Mac's 939fd78)
    [Fact]
    public void CustomColoursDoNotReplaceTheCover()
    {
        OkLab[] green = [OkLab.FromLch(0.8, 0.15, 2.5)];
        var withCustom = Settled(new LyricInkInputs(LyricColourMode.SungSweep) { Custom = green, Album = Album, Backdrop = 0.05 });
        var without = Settled(new LyricInkInputs(LyricColourMode.SungSweep) { Album = Album, Backdrop = 0.05 });
        foreach (var slot in new[] { 0.0, 0.5, 1.0 })
        {
            Assert.Equal(without.Sample(slot).Sung, withCustom.Sample(slot).Sung);
        }
    }
}
