using Lunalyr.Core.Colour;
using Lunalyr.Core.Player;
using P = Lunalyr.Core.Player.PlayerTokens;
using RGB = Lunalyr.Core.Colour.ArtworkPalette.Rgb;

namespace Lunalyr.Core.Tests.Player;

/// <summary>
/// The desktop player's ink levels against its card (docs/DESIGN.md, "Desktop player"): checked over covers of every
/// hue, on the brightest wallpaper that could show through the card (white).
/// </summary>
public class PlayerTokensTests
{
    /// <summary>Every hue at a few strengths and brightnesses, plus pale and white-ish covers.</summary>
    private static List<ArtworkPalette> Palettes()
    {
        var result = new List<ArtworkPalette>();
        for (var step = 0; step < 24; step++)
        {
            var hue = step / 24.0;
            foreach (var (saturation, brightness) in new[] { (0.9, 0.95), (0.5, 0.9), (0.35, 1.0), (0.8, 0.5) })
            {
                var pixel = RGB.FromHsb(hue, saturation, brightness);
                result.Add(ArtworkPalette.Of(Enumerable.Repeat(pixel, 100).ToList()));
            }
        }

        result.Add(ArtworkPalette.Neutral);
        return result;
    }

    /// <summary>The card's colour over a white wallpaper: white (worst case), the veil, then the album tint at its opacity.</summary>
    private static RGB Card(ArtworkPalette palette)
    {
        var veiled = new RGB(1, 1, 1).Mixed(new RGB(0, 0, 0), P.Fill.Veil);
        return veiled.Mixed(palette.CardTint, P.Fill.Tint);
    }

    private static double Contrast(PlayerInk ink, RGB ground, bool increaseContrast = false)
    {
        var text = ground.Mixed(new RGB(1, 1, 1), P.Ink.Opacity(ink, increaseContrast));
        return (text.Luminance + 0.05) / (ground.Luminance + 0.05);
    }

    // Ported from PlayerTokensTests.swift: testSmallTypeReadsWhereItSits
    [Fact]
    public void SmallTypeReadsWhereItSits()
    {
        foreach (var palette in Palettes())
        {
            var ground = Card(palette);
            Assert.True(Contrast(PlayerInk.Secondary, ground) >= 4.5, $"{palette}");
            Assert.True(Contrast(PlayerInk.Tertiary, ground) >= 4.5, $"{palette}");
            Assert.True(Contrast(PlayerInk.Tertiary, ground, increaseContrast: true) > Contrast(PlayerInk.Tertiary, ground));
        }
    }

    // Ported from PlayerTokensTests.swift: testLevelsStayInOrder
    [Fact]
    public void LevelsStayInOrder()
    {
        foreach (var increase in new[] { false, true })
        {
            var levels = new[] { PlayerInk.Primary, PlayerInk.Secondary, PlayerInk.Tertiary, PlayerInk.Quaternary }
                .Select(level => P.Ink.Opacity(level, increase)).ToArray();
            Assert.Equal(levels.OrderByDescending(l => l).ToArray(), levels);
        }
    }

    // Ported from PlayerTokensTests.swift: testCardIsConcentricWithTheCover
    [Fact]
    public void CardIsConcentricWithTheCover() => Assert.Equal(P.Radius.Card, P.Radius.Cover + PlayerCard.Padding);

    // Windows only: the card's ground as the painter composes it is the ground the Mac's test checks.
    [Fact]
    public void TheDarkGroundIsTheVeilAndTheTintOverTheWallpaper()
    {
        foreach (var palette in Palettes())
        {
            var expected = Card(palette);
            var ground = PlayerCardColours.WorstGround(palette, light: false);
            Assert.Equal(expected.Red, ground.Red, 9);
            Assert.Equal(expected.Green, ground.Green, 9);
            Assert.Equal(expected.Blue, ground.Blue, 9);
        }
    }

    // Windows only (the Mac's card is always dark): in light app mode the same ink levels in near-black read as well,
    // with a black wallpaper behind the card.
    [Fact]
    public void SmallTypeReadsOnTheLightCard()
    {
        foreach (var palette in Palettes())
        {
            var ground = PlayerCardColours.WorstGround(palette, light: true);
            foreach (var level in new[] { PlayerInk.Primary, PlayerInk.Secondary, PlayerInk.Tertiary })
            {
                var contrast = PlayerCardColours.Contrast(PlayerCardColours.InkOn(ground, light: true, level), ground);
                Assert.True(contrast >= 4.5, $"{level} {contrast:0.00} {palette}");
            }
        }
    }

    // Windows only: the motion keeps the one timing scale (exits shorter than entrances, a short fade under Reduce
    // Motion) and the card's reveal uses it.
    [Fact]
    public void TheRevealUsesTheTimingScale()
    {
        Assert.Equal(P.Motion.Standard, PlayerCard.RevealSeconds(shown: true, reduceMotion: false));
        Assert.Equal(P.Motion.Exit, PlayerCard.RevealSeconds(shown: false, reduceMotion: false));
        Assert.Equal(P.Motion.Reduced, PlayerCard.RevealSeconds(shown: true, reduceMotion: true));
        Assert.Equal(P.Motion.Reduced, PlayerCard.RevealSeconds(shown: false, reduceMotion: true));
        Assert.True(P.Motion.Exit < P.Motion.Standard && P.Motion.QuickExit < P.Motion.Quick && P.Motion.Press < P.Motion.Release);
        Assert.Equal(0, PlayerCard.RevealRise(reduceMotion: true));
        Assert.Equal(P.Motion.Rise, PlayerCard.RevealRise(reduceMotion: false));
    }
}
