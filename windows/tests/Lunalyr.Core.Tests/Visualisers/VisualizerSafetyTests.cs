using Lunalyr.Core.Visualisers;

namespace Lunalyr.Core.Tests.Visualisers;

/// <summary>How strongly a frame may react to transients.</summary>
public class VisualizerSafetyTests
{
    // Ported from VisualizerSafetyTests.swift: testCalmRemovesTransientsAndOnlyReduceMotionSlowsTheScene
    [Fact]
    public void CalmRemovesTransientsAndOnlyReduceMotionSlowsTheScene()
    {
        var shipped = new VisualizerResponse();
        Assert.False(shipped.Calm);
        Assert.Equal(1f, shipped.TransientScale);
        Assert.Equal(1, shipped.MotionScale);

        var dimmed = new VisualizerResponse(dimFlashingLights: true, punch: 2);
        Assert.True(dimmed.Calm);
        Assert.Equal(0f, dimmed.TransientScale); // Dim Flashing Lights overrides Punch
        Assert.Equal(1, dimmed.MotionScale); // Dim Flashing Lights is about flashes, not travel
        Assert.True(dimmed.PatternRate < 1);

        var reduced = new VisualizerResponse(reduceMotion: true);
        Assert.True(reduced.Calm);
        Assert.True(Math.Abs(reduced.MotionScale - 0.15) <= 0.0001);
        Assert.Equal(2f, new VisualizerResponse(punch: 7).TransientScale); // Punch is clamped to its slider range
        Assert.Equal(1f, new VisualizerResponse(punch: double.NaN).TransientScale);
    }
}
