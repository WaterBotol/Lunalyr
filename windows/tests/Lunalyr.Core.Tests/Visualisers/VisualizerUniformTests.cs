using System.Runtime.InteropServices;
using Lunalyr.Core.Visualisers;

namespace Lunalyr.Core.Tests.Visualisers;

/// <summary>What the scenes' uniforms carry, the CPU exposure limiter and the memory crossfades start from.</summary>
public class VisualizerUniformTests
{
    private static AudioSnapshot Spectrum(double frequency, double rate)
    {
        var analyzer = new AudioAnalyzer();
        analyzer.SetAutoGainEnabled(false);
        var chunk = (int)(rate / 100);
        var buffer = new float[chunk];
        var sample = 0;
        var time = 1_000.0;
        for (var step = 0; step < 50; step++)
        {
            for (var i = 0; i < chunk; i++) buffer[i] = (float)(0.25 * Math.Sin(2 * Math.PI * frequency * (sample + i) / rate));
            sample += chunk;
            time = 1_000 + sample / rate;
            analyzer.Append(buffer, rate, time);
        }
        return analyzer.Latest(time);
    }

    private static int Peak(float[] values) => Array.IndexOf(values, values.Max());

    // Ported from VisualizerTests.swift: testGPUUniformLayoutMatchesImportedMetalShaders. The Mac's 54 floats keep their
    // offsets; Windows pads the struct to 56 floats (224 bytes) for a whole number of constant-buffer registers.
    [Fact]
    public void GPUUniformLayoutMatchesImportedMetalShaders()
    {
        Assert.Equal(224, Marshal.SizeOf<VizUniforms>());
        Assert.Equal(VizUniforms.ByteSize, Marshal.SizeOf<VizUniforms>());
        Assert.Equal(48, (int)Marshal.OffsetOf<VizUniforms>(nameof(VizUniforms.P)));
        Assert.Equal(164, (int)Marshal.OffsetOf<VizUniforms>(nameof(VizUniforms.Intro)));
        Assert.Equal(168, (int)Marshal.OffsetOf<VizUniforms>(nameof(VizUniforms.SoundKick)));
        Assert.Equal(188, (int)Marshal.OffsetOf<VizUniforms>(nameof(VizUniforms.SoundPad)));
        Assert.Equal(192, (int)Marshal.OffsetOf<VizUniforms>(nameof(VizUniforms.KickOnsets)));
        Assert.Equal(212, (int)Marshal.OffsetOf<VizUniforms>(nameof(VizUniforms.HatAge)));
    }

    // Ported from VisualizerSafetyTests.swift: testPunchDefaultReproducesTheShippedBeatAndCalmZeroesIt
    [Fact]
    public void PunchDefaultReproducesTheShippedBeatAndCalmZeroesIt()
    {
        var sound = new AudioSnapshot { Beat = 1, TrebleBeat = 1, Flux = 1 };
        VizUniforms Uniforms(bool reduced, double punch = 1) =>
            VisualizerUniforms.Build(VisualizerStyle.Aurora, 64, 36, 3, sound, 1, 1, reduced, punch: punch);
        var shipped = Uniforms(false);
        Assert.Equal(0.20, shipped.Beat, 0.0001);
        Assert.Equal(0.12, shipped.TrebleBeat, 0.0001);
        Assert.Equal(0.14, shipped.Flux, 0.0001);
        Assert.Equal(0.40, Uniforms(false, 2).Beat, 0.0001);
        Assert.Equal(0, Uniforms(false, 0).Beat);
        var calm = Uniforms(true, 2);
        Assert.Equal([0f, 0f, 0f], [calm.Beat, calm.TrebleBeat, calm.Flux]);
        Assert.Equal(1, calm.ReduceMotion);
        Assert.Equal(3, calm.Time); // The timeline, not the uniforms, applies Reduce Motion's rate
    }

    // Ported from VisualizerTests.swift: testVisualiserControlsReachUniformsWithoutClippingSensitivity
    [Fact]
    public void VisualiserControlsReachUniformsWithoutClippingSensitivity()
    {
        var u = VisualizerUniforms.Build(VisualizerStyle.Silk, 640, 360, 12, new AudioSnapshot(), 3, 1, false, vividness: 1.7);
        Assert.Equal(3, u.Sensitivity);
        Assert.Equal(1.7, u.P[15], 0.001);
    }

    // Ported from VisualizerSpectrumTests.swift: testFosforaRibbonsEachFollowTheirOwnRegister
    [Fact]
    public void FosforaRibbonsEachFollowTheirOwnRegister()
    {
        static float[] Ribbons(double frequency)
        {
            var u = VisualizerUniforms.Build(VisualizerStyle.FosforaAurora, 64, 36, 0, Spectrum(frequency, 48_000), 1, 1, false);
            return [u.SubBass, u.P[3], u.LowMid, u.P[4], u.HighMid, u.Presence, u.Air];
        }
        (double Frequency, int Ribbon)[] tones = [(42, 0), (95, 1), (245, 2), (700, 3), (1900, 4), (4250, 5), (12_600, 6)];
        foreach (var (frequency, ribbon) in tones)
        {
            var ribbons = Ribbons(frequency);
            Assert.True(Peak(ribbons) == ribbon, $"{frequency} Hz lights ribbon {ribbon}: {string.Join(", ", ribbons)}");
        }
        float[] sub = Ribbons(42), bass = Ribbons(110);
        Assert.True(sub[0] > sub[1] + 0.3, "A 42 Hz tone stays off the bass ribbon");
        Assert.True(bass[1] > bass[0] + 0.3, "A 110 Hz tone stays off the sub-bass ribbon");
        float[] lowMid = Ribbons(245), mid = Ribbons(700);
        Assert.True(lowMid[2] > lowMid[3] + 0.3);
        Assert.True(mid[3] > mid[2] + 0.1);
    }

    // Ported from VisualizerSafetyTests.swift: testExposureLimiterCapsRisesAndKeepsFallsImmediate
    [Fact]
    public void ExposureLimiterCapsRisesAndKeepsFallsImmediate()
    {
        var limiter = new ExposureLimiter();
        const float dt = 1f / 60;
        // A new surface fades in from black at the capped rate.
        Assert.Equal(0, limiter.Gain(0.4f, 0), 0.0001);
        float shown = 0;
        for (var i = 0; i < 30; i++)
        {
            limiter.Gain(0.4f, dt);
            var next = limiter.Shown ?? 0;
            Assert.True(next - shown <= ExposureLimiter.MaxRisePerSecond * dt + 0.00001f);
            shown = next;
        }
        // A 10 Hz strobe between dark and bright frames.
        float previous = limiter.Shown ?? 0, risen = 0;
        for (var frame = 0; frame < 60; frame++)
        {
            var mean = frame / 3 % 2 == 0 ? 0.05f : 0.6f;
            var gain = limiter.Gain(mean, dt);
            var displayed = mean * MathF.Pow(gain, 2.2f);
            Assert.Equal(limiter.Shown ?? -1, displayed, 0.002);
            if (displayed > previous) risen += displayed - previous;
            previous = displayed;
        }
        Assert.True(risen <= ExposureLimiter.MaxRisePerSecond * 1.0f + 0.001f,
            "Over one second the display may brighten by at most the capped rise in total");
        // Falls are immediate; a stall cannot release a flash.
        Assert.Equal(1, limiter.Gain(0.01f, dt));
        limiter.Gain(0.9f, 30);
        Assert.True((limiter.Shown ?? 1) <= 0.01f + ExposureLimiter.MaxRisePerSecond * ExposureLimiter.MaxStep + 0.0001f);
        // Turning the limiter on keeps the current brightness.
        limiter.Adopt();
        Assert.Equal(1, limiter.Gain(0.7f, dt));
        Assert.Equal(1, limiter.Gain(float.NaN, float.NaN));
    }

    // Ported from VisualizerSafetyTests.swift: testANewSurfaceForAVisibleSceneKeepsItsBrightness (the memory's rules;
    // the GPU half is in Lunalyr.Render.Tests' VisualiserLayerTests).
    [Fact]
    public void ANewSurfaceForAVisibleSceneKeepsItsBrightness()
    {
        var memory = new ExposureMemory();
        var key = new ExposureMemory.Key(VisualizerStyle.Tunnel, "1", false);
        Assert.Null(memory.Shown(key, 0)); // A scene coming on screen fades in
        for (var frame = 1; frame <= 300; frame++) memory.Record(0.3f, key, frame / 60.0);
        var now = 301.0 / 60;
        Assert.Equal(0.3f, memory.Shown(key, now)); // The copy continues at the brightness on screen
        foreach (var other in new[]
                 {
                     new ExposureMemory.Key(VisualizerStyle.Tunnel, "2", false),
                     new ExposureMemory.Key(VisualizerStyle.Tunnel, "1", true),
                     new ExposureMemory.Key(VisualizerStyle.Aurora, "1", false),
                 })
            Assert.Null(memory.Shown(other, now));
        var later = now + ExposureMemory.Freshness + 0.1;
        Assert.Null(memory.Shown(key, later)); // The scene left the screen
        Assert.Null(memory.Shown(key, now - 1)); // A level from the future is ignored
        memory.Record(float.NaN, key, now);
        Assert.Equal(0.3f, memory.Shown(key, now));
    }

    // Windows' own: stored scene names, including the Mac's web scenes, read as native scenes.
    [Fact]
    public void StoredScenesReadAsNativeScenes()
    {
        foreach (var style in VisualizerStyles.All) Assert.Equal(style, VisualizerStyles.Parse(style.RawValue()));
        Assert.Equal(VisualizerStyle.Wavefield, VisualizerStyles.Parse("wavefieldOriginal"));
        Assert.Equal(VisualizerStyle.FosforaAurora, VisualizerStyles.Parse("wavecore"));
        Assert.Equal(VisualizerStyle.StarTrails, VisualizerStyles.Parse("light3D"));
        Assert.Equal(VisualizerStyle.Silk, VisualizerStyles.Parse(null));
        Assert.Equal(VisualizerStyle.Silk, VisualizerStyles.Parse("nonsense"));
    }
}
