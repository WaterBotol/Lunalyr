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
}
