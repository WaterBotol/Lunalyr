using Lunalyr.Core.Compositions;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>Constellation's fill, the Mac's rule (next/tester-fixes, 15 Nov 2026).</summary>
public class FlowFillTests
{
    [Fact]
    public void TheMeasureAndTargetsFollowTheStage()
    {
        var (fill, measure) = FlowFill.Constellation(new SizeD(1000, 400), 20);
        Assert.Equal(750, fill.TargetWidth);
        Assert.Equal(300, fill.TargetHeight);
        Assert.Equal(50, fill.MaxWordGap);
        Assert.Equal(24, fill.MaxRowGap);
        Assert.Equal(525, measure); // max(12·20, 0.7·750), under 750
        Assert.Equal(225, FlowFill.Constellation(new SizeD(300, 400), 20).Measure); // 12 ems is 240, but never past the 225 target
    }

    [Fact]
    public void GapsOnlyGrowAndStopAtTheirMost()
    {
        Assert.Equal(10, FlowFill.Gap(10, 100, 200, 3, 50)); // already wider than the target: unchanged
        Assert.Equal(30, FlowFill.Gap(10, 140, 100, 3, 50)); // 10 + 40/2
        Assert.Equal(50, FlowFill.Gap(10, 1000, 100, 3, 50)); // capped
        Assert.Equal(10, FlowFill.Gap(10, 1000, 100, 1, 50)); // one word: no gap to grow
    }

    [Fact]
    public void AFilledRowIsWiderAndItsWordsNeverOverlap()
    {
        SizeD[] slots = [new(40, 20), new(60, 20), new(30, 20)];
        var plain = new PointD[3];
        var filled = new PointD[3];
        var natural = EditorialFlow.Place(slots, 500, 5, 2, RowAlignment.Center, false, plain);
        var wide = EditorialFlow.Place(slots, 500, 5, 2, RowAlignment.Center, false, filled, fill: new FlowFill(400, 100, 100, 20));
        Assert.True(wide.Width > natural.Width);
        for (var i = 1; i < 3; i++)
            Assert.True(filled[i].X - slots[i].Width / 2 >= filled[i - 1].X + slots[i - 1].Width / 2);
    }
}
