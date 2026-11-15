using Lunalyr.Core.Visualisers;
using static Lunalyr.Core.Tests.Visualisers.VisualiserTestSupport;

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

    // Ported from VisualizerSafetyTests.swift: testNativeWavefieldDropsPulsesAndUnamplifiedEnergyWhenCalm
    [Fact]
    public void NativeWavefieldDropsPulsesAndUnamplifiedEnergyWhenCalm()
    {
        const double rate = 48_000.0;
        var analyzer = new AudioAnalyzer();
        for (var pass = 0; pass < 18; pass++) analyzer.Append(Sine(220, 0.25, rate, 2048, pass * 2048), rate);
        var sound = analyzer.Latest();
        sound.Beat = 1; sound.Flux = 1; sound.TrebleBeat = 1;
        WavefieldModes lively = new(), calm = new();
        var loud = lively.Frame(sound, 0, 0, 3, reduced: false);
        var quiet = calm.Frame(sound, 0, 0, 3, reduced: true);
        for (var step = 1; step <= 30; step++)
        {
            loud = lively.Frame(sound, step / 60.0, 0, 3, reduced: false);
            quiet = calm.Frame(sound, step / 60.0, 0, 3, reduced: true);
        }
        Assert.True(loud.Pulse > 0.5f, $"{loud.Pulse}");
        Assert.Equal(0f, quiet.Pulse);
        Assert.Equal(0f, quiet.Change);
        Assert.Equal((0f, 0f, 0f), (quiet.OnsetsAndEnergy.X, quiet.OnsetsAndEnergy.Y, quiet.OnsetsAndEnergy.Z));
        foreach (var mode in quiet.Modes) Assert.True(mode.Drive.Y == 0, "No mode may pulse while calm");
        Assert.True(quiet.OnsetsAndEnergy.W <= loud.OnsetsAndEnergy.W);
    }
}
