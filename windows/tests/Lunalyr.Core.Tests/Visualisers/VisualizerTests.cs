using Lunalyr.Core.Visualisers;
using static Lunalyr.Core.Tests.Visualisers.VisualiserTestSupport;

namespace Lunalyr.Core.Tests.Visualisers;

/// <summary>The analyser, the stereo merge and the smoother. Synthetic signals only.</summary>
public class VisualizerTests
{
    private static AudioSnapshot Tone(double frequency, AudioAnalyzer analyzer)
    {
        const double rate = 48000.0;
        const int count = 2048;
        for (var pass = 0; pass < 18; pass++) analyzer.Append(Sine(frequency, 0.25, rate, count, pass * count), rate);
        return analyzer.Latest();
    }

    // Ported from VisualizerTests.swift: testImportedFFTSeparatesMusicalRegisters
    [Fact]
    public void ImportedFFTSeparatesMusicalRegisters()
    {
        double[] frequencies = [100.0, 1000, 8000];
        var peaks = frequencies.Select(frequency =>
        {
            var analyzer = new AudioAnalyzer();
            analyzer.SetAutoGainEnabled(false);
            var frame = Tone(frequency, analyzer);
            Assert.All(frame.Bands.ToArray(), v => Assert.True(float.IsFinite(v) && v >= 0 && v <= 1));
            return Peak(frame.Bands.ToArray());
        }).ToArray();
        Assert.True(peaks[0] < peaks[1]);
        Assert.True(peaks[1] < peaks[2]);
        // The axis is log-spaced in real Hz from 30 Hz to 16 kHz: each tone peaks in the band containing it, within the
        // FFT's ~23 Hz bins.
        for (var i = 0; i < frequencies.Length; i++)
        {
            var expected = (int)(64 * Math.Log(frequencies[i] / 30) / Math.Log(16_000.0 / 30));
            Assert.True(Math.Abs(peaks[i] - expected) <= (frequencies[i] < 200 ? 2 : 1), $"{frequencies[i]} Hz: {peaks[i]} vs {expected}");
        }
    }

    // Ported from VisualizerTests.swift: testStereoFFTDoesNotCancelOppositePhaseAudio
    [Fact]
    public void StereoFFTDoesNotCancelOppositePhaseAudio()
    {
        var source = new VisualizerAudio();
        for (var pass = 0; pass < 18; pass++)
        {
            var left = Sine(1000, 0.25, 48000, 2048, pass * 2048);
            var right = left.Select(x => -x).ToArray();
            source.Append(left, 48000, 0);
            source.Append(right, 48000, 1);
            source.SetChannelCount(2);
        }
        Assert.True(source.Latest().Bands.Max() > 0.4);
        source.Reset();
        Assert.Equal(0f, source.Latest().Bands.Max());
    }

    // Ported from VisualizerTests.swift: testSmoothingIsContinuousAndIndependentOfFrameRate
    [Fact]
    public void SmoothingIsContinuousAndIndependentOfFrameRate()
    {
        var target = new AudioSnapshot();
        ((Span<float>)target.Bands).Fill(1);
        AudioSnapshot Run(int fps)
        {
            var smoother = new VisualizerSmoother();
            var result = new AudioSnapshot();
            for (var frame = 0; frame <= fps * 2; frame++) result = smoother.Frame(target, (double)frame / fps);
            return result;
        }
        AudioSnapshot thirty = Run(30), sixty = Run(60);
        Assert.True(Math.Abs(thirty.Bands[20] - sixty.Bands[20]) <= 0.00001);
        var smoother = new VisualizerSmoother();
        smoother.Frame(target, 0);
        var first = smoother.Frame(target, 1.0 / 60);
        Assert.True(first.Bands[20] > 0);
        Assert.True(first.Bands[20] < 0.05);
    }

    // Ported from VisualizerTests.swift: testAudioEnvelopeKeepsNoteAttacksAndReleasesVisible
    [Fact]
    public void AudioEnvelopeKeepsNoteAttacksAndReleasesVisible()
    {
        var smoother = new VisualizerSmoother();
        var sound = new AudioSnapshot { Bass = 1 };
        smoother.Frame(sound, 0);
        var frame = sound;
        for (var step = 1; step <= 6; step++) frame = smoother.Frame(sound, step / 60.0);
        Assert.True(frame.Bass > 0.5, "Do not smear a note attack over several hundred milliseconds");
        for (var step = 7; step <= 60; step++) smoother.Frame(sound, step / 60.0);
        for (var step = 61; step <= 90; step++) frame = smoother.Frame(new AudioSnapshot(), step / 60.0);
        Assert.True(frame.Bass < 0.4, "Do not hold the previous note through the next beat");
    }

    // Ported from VisualizerTests.swift: testSilenceAndInvalidSamplesCannotDriveTheVisualiser
    [Fact]
    public void SilenceAndInvalidSamplesCannotDriveTheVisualiser()
    {
        var analyzer = new AudioAnalyzer();
        analyzer.Append(new float[2048], 48000);
        Assert.Equal(0f, analyzer.Latest().Level);
        Assert.Equal(0f, analyzer.Latest().Bands.Max());
        var target = new AudioSnapshot();
        var smoother = new VisualizerSmoother();
        target.Bands[0] = float.NaN;
        smoother.Frame(target, 0);
        Assert.All(smoother.Frame(target, 1).Bands.ToArray(), v => Assert.True(float.IsFinite(v) && v == 0));
    }

    // Ported from VisualizerTests.swift: testWavefieldMapsLiveFFTPeaksToDistinctResonances (the spectrum part: the
    // live FFT's strongest peaks; the mode atlas half needs WavefieldModes, which a later port adds)
    [Fact]
    public void WavefieldMapsLiveFFTPeaksToDistinctResonances()
    {
        var low = Tone(220, new AudioAnalyzer());
        var high = Tone(1100, new AudioAnalyzer());
        Assert.True(low.Wavefield.PeakCount > 0);
        Assert.True(high.Wavefield.PeakCount > 0);
        Assert.True(Math.Abs(low.Wavefield.GetPeak(0).Frequency - 220) <= 6, $"{low.Wavefield.GetPeak(0).Frequency}");
        Assert.True(Math.Abs(high.Wavefield.GetPeak(0).Frequency - 1100) <= 6, $"{high.Wavefield.GetPeak(0).Frequency}");
    }
}
