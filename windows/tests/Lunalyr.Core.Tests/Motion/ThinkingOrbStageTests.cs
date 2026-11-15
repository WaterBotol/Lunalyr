using Lunalyr.Core.Motion.ThinkingOrbs;

namespace Lunalyr.Core.Tests.Motion;

/// <summary>The orb's stage rules: Mix as the Mac picks it, the focus window, the follower.</summary>
public class ThinkingOrbStageTests
{
    private static ulong SplitMix(ulong seed)
    {
        unchecked
        {
            var z = seed + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0x1D075EEDUL)]
    [InlineData(123456789UL)]
    public void Mix_is_splitmix_over_the_macs_pool(ulong seed)
    {
        Assert.Equal(ThinkingOrbStage.MixPool[(int)(SplitMix(seed) % 9)], ThinkingOrbStage.Mix(seed, null));
        var previous = ThinkingOrbStage.MixPool[0];
        var pool = ThinkingOrbStage.MixPool.Where(s => s != previous).ToList();
        Assert.Equal(pool[(int)(SplitMix(seed) % 8)], ThinkingOrbStage.Mix(seed, previous));
    }

    [Fact]
    public void Mix_never_repeats_the_previous_break()
    {
        for (ulong seed = 1; seed < 500; seed++)
            foreach (var previous in ThinkingOrbStage.MixPool)
                Assert.NotEqual(previous, ThinkingOrbStage.Mix(seed * 7919, previous));
    }

    [Fact]
    public void The_pool_is_the_pickers_order_without_mix() =>
        Assert.Equal(ThinkingOrbStage.StyleValues.Where(v => v != "mix"), ThinkingOrbStage.MixPool.Select(s => s.Name()));

    [Fact]
    public void Focus_fills_the_last_window_before_the_hand_off()
    {
        Assert.Equal(0, ThinkingOrbStage.Focus(1, null));
        // span 6: window 2, from 3.6 s to 5.6 s.
        Assert.Equal(0, ThinkingOrbStage.Focus(3.6, 6), 6);
        Assert.Equal(1, ThinkingOrbStage.Focus(5.6, 6), 6);
        // span 3: window 1.5.
        Assert.Equal(0.5, ThinkingOrbStage.Focus(1.1 + 0.75, 3), 6);
    }

    [Fact]
    public void The_follower_rises_fast_and_falls_slow()
    {
        var follower = new OrbAudioFollower();
        follower.Step(0, 0, 0, 0);
        follower.Step(0.06, 1, 0, 0);
        var risen = follower.Level;
        Assert.InRange(risen, 0.6, 0.65); // 1 − e^−1
        follower.Step(0.31, 0, 0, 0);
        Assert.InRange(follower.Level, risen * 0.66, risen * 0.68); // dt capped at 0.1: e^(−0.1/0.25)
    }
}
