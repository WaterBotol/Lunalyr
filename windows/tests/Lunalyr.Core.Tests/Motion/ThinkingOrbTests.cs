using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Motion;

/// <summary>The thinking orb (the Mac's ff727bb): its size, breath, glow and colours.</summary>
public class ThinkingOrbTests
{
    private static InstrumentalCue Cue(double elapsed, double? progress = 0.2, double fadeOut = 1, ulong seed = 0xABCDEF12345UL) =>
        new(elapsed, 1, progress, seed, 0, fadeOut);

    [Theory]
    [InlineData(10, 30)]
    [InlineData(60, 48)]
    [InlineData(200, 104)]
    public void Diameter_is_eight_tenths_of_the_base_size_held_to_30_to_104(double baseSize, double expected) =>
        Assert.Equal(expected, ThinkingOrb.Diameter(baseSize), 6);

    [Fact]
    public void Reduce_motion_holds_it_still_at_full_size()
    {
        var a = ThinkingOrb.Pose(Cue(0.2), reduceMotion: true, increaseContrast: false)!.Value;
        var b = ThinkingOrb.Pose(Cue(5.7), reduceMotion: true, increaseContrast: false)!.Value;
        Assert.Equal(1, a.Scale);
        Assert.Equal(a.Lobe0, b.Lobe0);
        Assert.Equal(a.Lobe2, b.Lobe2);
    }

    [Fact]
    public void It_grows_in_from_055_and_shrinks_to_030_as_it_leaves()
    {
        var arriving = ThinkingOrb.Pose(Cue(0), false, false)!.Value;
        Assert.Equal(0.55, arriving.Scale, 6); // sin(0) = 0: no breath yet
        var leaving = ThinkingOrb.Pose(Cue(2.6, fadeOut: 0), false, false)!.Value;
        Assert.InRange(leaving.Scale, 0.29, 0.31);
    }

    [Fact]
    public void Lobes_drift_and_stay_inside_their_bounds()
    {
        for (var t = 0.0; t < 20; t += 0.37)
        {
            var pose = ThinkingOrb.Pose(Cue(t), false, false)!.Value;
            foreach (var (lobe, r0) in new[] { (pose.Lobe0, 0.80), (pose.Lobe1, 0.66), (pose.Lobe2, 0.58) })
            {
                Assert.InRange(Math.Sqrt(lobe.X * lobe.X + lobe.Y * lobe.Y), 0.32 * 0.75 - 1e-9, 0.32 * 1.25 + 1e-9);
                Assert.InRange(lobe.Radius, r0 * 0.9 - 1e-9, r0 * 1.1 + 1e-9);
            }
        }
        Assert.NotEqual(ThinkingOrb.Pose(Cue(1), false, false)!.Value.Lobe0, ThinkingOrb.Pose(Cue(2), false, false)!.Value.Lobe0);
    }

    [Fact]
    public void It_glows_through_the_second_half_of_the_break()
    {
        Assert.Equal(0, ThinkingOrb.Pose(Cue(1, progress: 0.5), false, false)!.Value.Glow);
        Assert.Equal(1, ThinkingOrb.Pose(Cue(1, progress: 1), false, false)!.Value.Glow);
        Assert.Equal(0, ThinkingOrb.Pose(Cue(1, progress: null), false, false)!.Value.Glow);
        var start = ThinkingOrb.Pose(Cue(1, progress: 0.1), false, false)!.Value;
        Assert.Equal(0.28, start.HaloAlpha, 6);
        Assert.Equal(0.30, start.CoreAlpha, 6);
        Assert.Equal(0.80, start.LobeAlpha);
        Assert.Equal(0.45, start.LobeEdge);
        var end = ThinkingOrb.Pose(Cue(1, progress: 1), false, false)!.Value;
        Assert.Equal(0.60, end.HaloAlpha, 6);
        Assert.Equal(0.70, end.CoreAlpha, 6);
    }

    [Fact]
    public void Increase_contrast_strengthens_halo_and_lobes()
    {
        var pose = ThinkingOrb.Pose(Cue(1, progress: 0.1), false, increaseContrast: true)!.Value;
        Assert.Equal(0.35, pose.HaloAlpha, 6);
        Assert.Equal(0.95, pose.LobeAlpha);
        Assert.Equal(0.65, pose.LobeEdge);
    }

    [Fact]
    public void A_hidden_cue_has_no_orb() =>
        Assert.Null(ThinkingOrb.Pose(new InstrumentalCue(0, 0, null), false, false));

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
    public void White_lyrics_with_no_album_colour_keep_a_white_orb()
    {
        var colours = ThinkingOrb.Colours(new LyricInk { Dot = SrgbColour.White }, null);
        Assert.True(colours.Neighbour1.Chroma < 0.01);
        Assert.True(colours.Neighbour2.Chroma < 0.01);
    }
}
