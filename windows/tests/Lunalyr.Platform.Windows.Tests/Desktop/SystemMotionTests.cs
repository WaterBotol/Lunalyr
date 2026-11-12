using Lunalyr.Platform.Desktop;

namespace Lunalyr.Platform.Windows.Tests.Desktop;

/// <summary>
/// Windows' "Animation effects" as the wallpaper's Reduce Motion. A fake reader stands in for the system setting, which
/// the tests never read or change.
/// </summary>
public sealed class SystemMotionTests
{
    [Fact]
    public void AnimationEffectsOffMeansLessMotion()
    {
        Assert.True(SystemMotion.ReduceMotionFrom(false));
        Assert.False(SystemMotion.ReduceMotionFrom(true));
        // A setting that can't be read never takes motion away.
        Assert.False(SystemMotion.ReduceMotionFrom(null));
    }

    [Fact]
    public void FollowsTheSettingAndSaysOnlyWhenItChanges()
    {
        bool? effects = true;
        using var motion = new SystemMotion(() => effects, listen: false);
        Assert.False(motion.ReduceMotion);
        var heard = new List<bool>();
        motion.Changed += (_, reduce) => heard.Add(reduce);

        motion.Refresh();
        Assert.Empty(heard);
        effects = false;
        motion.Refresh();
        motion.Refresh();
        Assert.True(motion.ReduceMotion);
        Assert.Equal([true], heard);
        effects = null;
        motion.Refresh();
        Assert.False(motion.ReduceMotion);
        Assert.Equal([true, false], heard);
    }

    [Fact]
    public void StartsFromTheSettingAndStopsWhenDisposed()
    {
        bool? effects = false;
        var motion = new SystemMotion(() => effects, listen: false);
        Assert.True(motion.ReduceMotion);
        var heard = 0;
        motion.Changed += (_, _) => heard++;
        motion.Dispose();
        effects = true;
        motion.Refresh();
        Assert.Equal(0, heard);
    }
}
