using Lunalyr.Core.Colour;

namespace Lunalyr.Core.Tests.Colour;

/// <summary>Dark lyrics on light wallpapers (the Mac's next/tester-fixes 8a52096, 15 Nov 2026).</summary>
public class DarkLyricsTests
{
    private static double Luminance(SrgbColour c) => Perceptual.Luminance(Perceptual.ToOkLab(new Double3(c.R, c.G, c.B)));

    [Fact]
    public void ADeepColourReachesTheRatioWithItsMarginAndKeepsItsHue()
    {
        var pink = ColourHex.ToOkLab("#F28FAD")!.Value;
        var deep = Perceptual.Deep(pink, 0.6);
        Assert.True(Perceptual.Luminance(deep) <= (0.6 + 0.05) / (3 * 1.15) - 0.05 + 1e-6);
        Assert.True(LyricHighlight.HueDistance(deep.Hue, pink.Hue) < 0.2);
        Assert.Equal(0, Perceptual.Deep(pink, 0.05).L, 3); // no colour can be dark enough: black
    }

    [Fact]
    public void TheSwitchHasHysteresisAndFadesOverSixTenthsOfASecond()
    {
        var director = new LyricInkDirector();
        LyricInk Frame(double unbacked, double t) =>
            director.Ink(new LyricInkInputs(LyricColourMode.AlbumDrift) { Backdrop = 0.2, Unbacked = unbacked }, t);
        Assert.Equal(1, Frame(0.6, 0).Dark); // the first frame jumps
        Assert.Equal(1, Frame(0.3, 0.1).Dark); // between the thresholds: stays dark
        var fading = Frame(0.2, 0.4); // below 0.26: turning light, 0.3 s of 0.6
        Assert.InRange(fading.Dark, 0.4, 0.6);
        Assert.False(fading.Settled);
        for (var t = 0.5; t <= 1.2; t += 0.1) Frame(0.2, t);
        Assert.Equal(0, Frame(0.2, 1.3).Dark);
        // Dark words are dark, and readable on the light wallpaper.
        var dark = new LyricInkDirector().Ink(new LyricInkInputs(LyricColourMode.AlbumDrift) { Backdrop = 0.2, Unbacked = 0.7 }, 0);
        Assert.True(Luminance(dark.Sample(0.3).Rest) < 0.2);
        Assert.True(Perceptual.Contrast(Luminance(dark.Sample(0.3).Rest), 0.7) >= 2.99);
    }

    [Fact]
    public void FloatingLyricsStayLight()
    {
        var ink = new LyricInkDirector().Ink(new LyricInkInputs(LyricColourMode.AlbumDrift) { Backdrop = 0.2, Unbacked = null }, 0);
        Assert.Equal(0, ink.Dark);
    }
}
