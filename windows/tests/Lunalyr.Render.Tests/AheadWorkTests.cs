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
}
