using Lunalyr.Core.Colour;

namespace Lunalyr.Core.Tests.Colour;

/// <summary>Lyric colour modes keep a colour on every backdrop; only an intentionally neutral palette stays white.</summary>
public class LyricColourStaysColourTests
{
    // stride(from: 0, to: 2π, by: 0.35), computed as Swift's stride does (start + i × step).
    private static IEnumerable<double> Hues()
    {
        for (var i = 0; i * 0.35 < 2 * Math.PI; i++) yield return i * 0.35;
    }

    // Ported from LyricColourStaysColourTests.swift: testALiftedColourNeverFadesToWhiteOnAnyBackdrop
    [Fact]
    public void ALiftedColourNeverFadesToWhiteOnAnyBackdrop()
    {
        foreach (var hue in Hues())
        {
            var pastel = LyricInk.Pastel(OkLab.FromLch(0.8, 0.12, hue));
            for (var step = 0; step <= 13; step++)
            {
                var backdrop = step / 50.0;
                var ink = Perceptual.Readable(pastel, backdrop, keepingColour: true);
                Assert.True(ink.Chroma >= 0.04, $"hue {hue} on backdrop {backdrop} faded to white");
                var difference = Math.Abs(ink.Hue - pastel.Hue);
                Assert.True((difference % (2 * Math.PI) < Math.PI ? difference : 2 * Math.PI - difference) < 0.2);
            }
        }
    }

    // Ported from LyricColourStaysColourTests.swift: testTheTintCostsAtMostAFewPercentOfContrast
    [Fact]
    public void TheTintCostsAtMostAFewPercentOfContrast()
    {
        foreach (var hue in Hues())
        {
            var pastel = LyricInk.Pastel(OkLab.FromLch(0.8, 0.12, hue));
            foreach (var backdrop in new[] { 0.12, 0.16, 0.2, 0.24, 0.27 })
            {
                var ink = Perceptual.Readable(pastel, backdrop, keepingColour: true);
                Assert.True(Perceptual.Contrast(Perceptual.Luminance(ink), backdrop) >= Perceptual.TintedContrast - 0.02,
                            $"hue {hue} on {backdrop}");
            }
        }
    }

    // Ported from LyricColourStaysColourTests.swift: testEasyBackdropsAreUnchangedAndNeutralStaysNeutral
    [Fact]
    public void EasyBackdropsAreUnchangedAndNeutralStaysNeutral()
    {
        var pastel = OkLab.FromLch(0.84, 0.14, 1.2);
        Assert.Equal(Perceptual.Clipped(pastel), Perceptual.Readable(pastel, 0.05, keepingColour: true));
        var grey = OkLab.FromLch(0.8, 0.0, 0);
        Assert.True(Perceptual.Readable(grey, 0.9, keepingColour: true).Chroma < 0.01);
    }

    // Ported from LyricColourStaysColourTests.swift: testIncreaseContrastStillWinsOverColour
    [Fact]
    public void IncreaseContrastStillWinsOverColour()
    {
        var pastel = OkLab.FromLch(0.84, 0.14, 4.8);
        var strict = Perceptual.Readable(pastel, 0.15, LyricInk.IncreasedContrast, keepingColour: true);
        Assert.True(Perceptual.Contrast(Perceptual.Luminance(strict), 0.15) >= 4.4);
    }
}
