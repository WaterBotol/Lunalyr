using Lunalyr.Core.Visualisers;
using static Lunalyr.Core.Tests.Visualisers.VisualiserTestSupport;

namespace Lunalyr.Core.Tests.Visualisers;

/// <summary>
/// The capture thread's append (analysis and sound types included) and every per-frame read must not allocate once
/// warmed up. Not a Mac test: the Mac states the same rule in AudioAnalyzer's and SoundTypeDetector's documentation.
/// </summary>
public class AudioAllocationTests
{
    [Fact]
    public void AppendAndPerFrameReadsDoNotAllocate()
    {
        var clock = 100.0;
        var audio = new VisualizerAudio(outputLatency: () => 0.1, now: () => clock);
        audio.SetChannelCount(2);
        var smoother = new VisualizerSmoother();
        var gate = new SoundTypeIdleGate();
        var left = Sine(110, 0.3, 48_000, 48_000);
        var right = Sine(2_000, 0.2, 48_000, 48_000);
        // Noise bursts every 0.1 s so the transient classes fire too.
        var random = new Random(3);
        for (var i = 0; i < left.Length; i++)
        {
            if (i % 4_800 < 300) left[i] += (float)(random.NextDouble() - 0.5);
        }
        double sink = 0;
        void Run(int from, int to)
        {
            for (var i = from; i < to; i += 441)
            {
                var n = Math.Min(441, left.Length - i);
                clock += n / 48_000.0;
                audio.Append(left.AsSpan(i, n), 48_000, 0, clock);
                audio.Append(right.AsSpan(i, n), 48_000, 1, clock);
                var frame = audio.Latest();
                var smoothed = smoother.Frame(frame, clock);
                var sound = gate.Frame(smoothed.Sound, live: true, clock);
                var uniforms = new SoundTypeUniforms(sound, sound.PresentationTime, new VisualizerResponse(reduceMotion: true));
                sink += uniforms.Kick + audio.Latest(clock - 0.05).Level + audio.LatestSound().Bass + AmbientSpectrum.At(clock).Level;
                sink += audio.VisualDelay + audio.PresentationTime;
            }
        }
        Run(0, 24_000); // warm up
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run(24_000, 48_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(audio.SoundTypePasses > 0);
        Assert.True(double.IsFinite(sink));
    }
}
