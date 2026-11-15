using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Motion;

/// <summary>The thinking orb (the Mac's ff727bb): its size, breath, glow and colours.</summary>
public class ThinkingOrbTests
{
    [Theory]
    [InlineData(10, 30)]
    [InlineData(60, 48)]
    [InlineData(200, 104)]
    public void Diameter_is_eight_tenths_of_the_base_size_held_to_30_to_104(double baseSize, double expected) =>
        Assert.Equal(expected, ThinkingOrb.Diameter(baseSize), 6);

    [Fact]
    public void Its_primary_is_the_old_dot_colour_and_the_core_is_lighter()
    {
        var ink = new LyricInk { Mode = LyricColourMode.CustomGradient, Dot = new SrgbColour(0.9, 0.3, 0.4) };
        var colours = ThinkingOrb.Colours(ink, null);
        var dot = Perceptual.ToOkLab(new Double3(0.9, 0.3, 0.4));
        Assert.Equal(dot.L, colours.Primary.L, 6);
        Assert.True(colours.Core.L > colours.Primary.L);
        Assert.True(colours.Halo.L > colours.Primary.L);
    }

    [Fact]
    public void On_dark_lyrics_the_halo_turns_near_white()
    {
        var ink = new LyricInk { Dark = 1, Dot = new SrgbColour(0.2, 0.2, 0.25), DeepBackdrop = 0.7 };
        var colours = ThinkingOrb.Colours(ink, null);
        Assert.Equal(0.97, colours.Halo.L, 3);
        Assert.True(colours.Halo.Chroma <= 0.0201);
    }

    [Fact]
    public void A_grey_primary_with_no_album_colour_gets_faintly_tinted_neighbours()
    {
        // The Mac's rule: hue −0.4 and +0.4 rad at chroma 0.06, same lightness. (Pure white sits at full lightness,
        // where no chroma fits in gamut, so its neighbours clip back to white.)
        var colours = ThinkingOrb.Colours(new LyricInk { Dot = new SrgbColour(0.75, 0.75, 0.75) }, null);
        Assert.InRange(colours.Neighbour1.Chroma, 0.03, 0.08);
        Assert.InRange(colours.Neighbour2.Chroma, 0.03, 0.08);
        Assert.NotEqual(Math.Round(colours.Neighbour1.Hue, 2), Math.Round(colours.Neighbour2.Hue, 2));
    }

    [Fact]
    public void White_lyrics_take_a_grey_album_accent_at_least_faintly_tinted()
    {
        var grey = OkLab.FromLch(0.7, 0.01, 1.0);
        var colours = ThinkingOrb.Colours(new LyricInk { Dot = SrgbColour.White }, grey);
        Assert.True(colours.Neighbour1.Chroma > 0.03);
    }

    [Fact]
    public void Sung_sweep_with_two_colours_uses_the_second_twice()
    {
        var red = OkLab.FromLch(0.7, 0.15, 0.5);
        var blue = OkLab.FromLch(0.7, 0.15, 4.0);
        var ink = new LyricInk { Mode = LyricColourMode.SungSweep, Stops = new InkStops(red, blue, red, blue), StopCount = 2 };
        var colours = ThinkingOrb.Colours(ink, null);
        Assert.Equal(colours.Neighbour1, colours.Neighbour2);
    }
}
