using Lunalyr.Render.Pacing;

namespace Lunalyr.Render.Tests;

public sealed class AheadWorkTests
{
    [Fact]
    public void OnePiecePerPassAcrossSurfaces()
    {
        var work = new AheadWork();
        work.NewPass();
        Assert.True(work.TryTake());   // the first monitor sets its next line
        Assert.False(work.TryTake());  // the second waits for the next pass
        work.NewPass();
        Assert.True(work.TryTake());
    }

    [Fact]
    public void OneAllowancePerPassAcrossSurfaces()
    {
        var work = new AheadWork();
        work.NewPass();
        Assert.Equal(AheadWork.AllowanceTicks, work.Remaining);
        work.Spend(AheadWork.AllowanceTicks / 2);      // the first monitor bakes for half the allowance
        Assert.Equal(AheadWork.AllowanceTicks - AheadWork.AllowanceTicks / 2, work.Remaining);
        work.Spend(AheadWork.AllowanceTicks);          // the second overruns it
        Assert.True(work.Remaining <= 0);
        work.Spend(-5);                                // a clock that ran backwards spends nothing
        work.NewPass();
        Assert.Equal(AheadWork.AllowanceTicks, work.Remaining);
        Assert.True(AheadWork.Allowance < TimeSpan.FromMilliseconds(4));
    }

    [Fact]
    public void SurfacesWithWorkShareTheAllowance()
    {
        var work = new AheadWork();
        work.NewPass();
        // One monitor alone has all of it.
        Assert.Equal(AheadWork.AllowanceTicks, work.Open());
        work.NewPass();
        Assert.Equal(AheadWork.AllowanceTicks, work.Open());
        Assert.Equal(AheadWork.AllowanceTicks, work.Open());  // a second monitor finds work too: it shares from the next pass
        work.NewPass();
        var half = AheadWork.AllowanceTicks / 2;
        Assert.Equal(half, work.Open());
        work.Spend(half);
        Assert.Equal(half, work.Open());
        work.Spend(AheadWork.AllowanceTicks);
        work.NewPass();
        Assert.Equal(half, work.Open());
        work.NewPass();
        // Only one had work in the pass before: it has all of it again.
        Assert.Equal(AheadWork.AllowanceTicks, work.Open());
    }
}
