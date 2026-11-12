using Lunalyr.Core.Visualisers;
using static Lunalyr.Core.Tests.Visualisers.VisualiserTestSupport;

namespace Lunalyr.Core.Tests.Visualisers;

/// <summary>The analyser's frequency axis, time constants and transient timing. Synthetic signals only.</summary>
public class VisualizerSpectrumTests
{
    /// <summary>
    /// Streams <paramref name="seconds"/> of a sine (null: silence) in 10 ms capture buffers, each stamped with its last
    /// sample's capture time. Returns that time.
    /// </summary>
    private static double Feed(AudioAnalyzer analyzer, double? frequency, double rate, double seconds, double start, ref int sample,
        double amplitude = 0.25)
    {
        var chunk = (int)(rate / 100);
        var buffer = new float[chunk];
        var time = start;
        for (var step = 0; step < (int)Math.Round(seconds * 100, MidpointRounding.AwayFromZero); step++)
        {
            for (var i = 0; i < chunk; i++)
                buffer[i] = frequency is { } f ? (float)(amplitude * Math.Sin(2 * Math.PI * f * (sample + i) / rate)) : 0;
            sample += chunk;
            time = start + sample / rate;
            analyzer.Append(buffer, rate, time);
        }
        return time;
    }

    private static AudioSnapshot Spectrum(double frequency, double rate)
    {
        var analyzer = new AudioAnalyzer();
        analyzer.SetAutoGainEnabled(false);
        var sample = 0;
        var end = Feed(analyzer, frequency, rate, 0.5, 1_000, ref sample);
        return analyzer.Latest(end);
    }

    // Ported from VisualizerSpectrumTests.swift: testBandsAreLogSpacedInHzAndMatchAcrossSampleRates
    [Fact]
    public void BandsAreLogSpacedInHzAndMatchAcrossSampleRates()
    {
        Assert.True(Math.Abs(AudioAnalyzer.BandCentre(0, 48_000) - 31.5) <= 0.5);
        Assert.True(Math.Abs(AudioAnalyzer.BandCentre(63, 48_000) - 15_220) <= 30);
        Assert.Equal(2048, AudioAnalyzer.FftSize(44_100));
        Assert.Equal(4096, AudioAnalyzer.FftSize(96_000)); // The window lasts ~43 ms at every rate
        Assert.Equal(8192, AudioAnalyzer.FftSize(192_000));
        // An octave is ~7 bands everywhere, not 18 at 440 Hz and 2 at 55 Hz.
        foreach (var octave in new[] { 55.0, 220, 880, 3520 })
        {
            var low = Peak(Spectrum(octave, 48_000).Bands.ToArray());
            var high = Peak(Spectrum(octave * 2, 48_000).Bands.ToArray());
            Assert.True(Math.Abs(high - low - 64 / Math.Log2(16_000.0 / 30)) <= 2.1, $"{octave} Hz octave: {low} → {high}");
        }
        double[] rates = [44_100.0, 48_000, 96_000];
        foreach (var frequency in new[] { 55.0, 110, 440, 1760, 7040 })
        {
            var peaks = rates.Select(rate => Peak(Spectrum(frequency, rate).Bands.ToArray())).ToArray();
            for (var i = 0; i < rates.Length; i++)
            {
                var centre = AudioAnalyzer.BandCentre(peaks[i], rates[i]);
                Assert.True(Math.Abs(Math.Log2(centre / frequency)) < (frequency < 100 ? 0.4 : 0.25),
                    $"{frequency} Hz at {rates[i]} Hz peaks at {centre} Hz");
            }
            Assert.True(peaks.Max() - peaks.Min() <= 1, $"{frequency} Hz moves with the sample rate");
        }
    }

    // Ported from VisualizerSpectrumTests.swift: testNamedRangesFollowTheirFrequencies
    [Fact]
    public void NamedRangesFollowTheirFrequencies()
    {
        // Each tone sits in the middle (in octaves) of one named range.
        (double Frequency, int Component)[] tones =
            [(42, 0), (95, 1), (245, 2), (700, 3), (1900, 4), (4250, 5), (7750, 6), (12_600, 7)];
        foreach (var (frequency, component) in tones)
        {
            var frame = Spectrum(frequency, 48_000);
            Assert.True(Peak(frame.Components.ToArray()) == component, $"{frequency} Hz");
        }
        AudioSnapshot sub = Spectrum(42, 48_000), bass = Spectrum(110, 48_000);
        Assert.True(sub.Components[0] > sub.Components[1] + 0.05);
        Assert.True(bass.Components[1] > bass.Components[0] + 0.05);
        (double Frequency, Func<AudioSnapshot, float> Register)[] registers =
            [(100, s => s.Bass), (800, s => s.Mid), (6000, s => s.Treble)];
        foreach (var (frequency, register) in registers)
        {
            var frame = Spectrum(frequency, 48_000);
            Assert.True(register(frame) == new[] { frame.Bass, frame.Mid, frame.Treble }.Max(), $"{frequency} Hz");
        }
    }

    // Ported from VisualizerSpectrumTests.swift: testEnvelopesUseSecondsAtEverySampleRate
    [Fact]
    public void EnvelopesUseSecondsAtEverySampleRate()
    {
        static (float Band, float Centroid, float Level) AfterRelease(double rate)
        {
            var analyzer = new AudioAnalyzer();
            analyzer.SetAutoGainEnabled(false);
            var sample = 0;
            var toneEnd = Feed(analyzer, 440, rate, 0.6, 50, ref sample);
            var end = Feed(analyzer, null, rate, 0.12, 50, ref sample);
            Assert.True(Math.Abs(end - toneEnd - 0.12) <= 0.001);
            var frame = analyzer.Latest(end);
            return (frame.Bands.Max(), frame.Centroid, frame.Level);
        }
        var reference = AfterRelease(48_000);
        Assert.True(reference.Band > 0.1, "Still releasing 120 ms after the note");
        foreach (var rate in new[] { 44_100.0, 96_000 })
        {
            var other = AfterRelease(rate);
            Assert.True(Math.Abs(other.Band - reference.Band) <= 0.04, $"Band release at {rate} Hz: {other.Band} vs {reference.Band}");
            Assert.True(Math.Abs(other.Centroid - reference.Centroid) <= 0.05, $"Centroid at {rate} Hz");
        }
    }

    // Ported from VisualizerSpectrumTests.swift: testKicksAreTimestampedWithinMillisecondsAndSustainedBassFiresOnce
    [Fact]
    public void KicksAreTimestampedWithinMillisecondsAndSustainedBassFiresOnce()
    {
        const double rate = 48_000.0;
        var analyzer = new AudioAnalyzer();
        var sample = 0;
        var quietEnd = Feed(analyzer, null, rate, 0.5, 10, ref sample);
        // Four kicks: 60 ms bursts of 60 Hz every half second.
        var hits = new List<double>();
        var time = quietEnd;
        for (var k = 0; k < 4; k++)
        {
            hits.Add(time);
            Feed(analyzer, 60, rate, 0.06, 10, ref sample, amplitude: 0.6);
            time = Feed(analyzer, null, rate, 0.44, 10, ref sample);
        }
        foreach (var hit in hits)
        {
            Assert.True(analyzer.Latest(hit - 0.003).Beat < 0.3, "No beat before the kick");
            Assert.True(analyzer.Latest(hit + 0.015).Beat > 0.85,
                "The kick's onset is stamped within ~10 ms of the hit, not a whole FFT hop later");
        }
        // A held bass note fires one pulse and then settles.
        var noteStart = time;
        var noteEnd = Feed(analyzer, 55, rate, 2, 10, ref sample, amplitude: 0.6);
        Assert.True(analyzer.Latest(noteStart + 0.015).Beat > 0.85);
        Assert.True(analyzer.Latest(noteEnd).Beat < 0.01, "A sustained note must not keep re-firing");
    }

    // Ported from VisualizerSpectrumTests.swift: testVisualsWaitOnlyForHighLatencyOutputs (without the two
    // OutputLatencyMonitor.totalLatency checks: Windows has no Core Audio latency monitor; the output latency is
    // injected)
    [Fact]
    public void VisualsWaitOnlyForHighLatencyOutputs()
    {
        Assert.Equal(0, VisualizerAudio.Delay(0.012)); // Speakers: no added delay
        Assert.True(Math.Abs(VisualizerAudio.Delay(0.2) - (0.2 - VisualizerAudio.PipelineLatency)) <= 0.0001);
        Assert.Equal(VisualizerAudio.MaximumDelay, VisualizerAudio.Delay(2)); // Bounded
        Assert.Equal(0, VisualizerAudio.Delay(double.NaN));

        // A note that began 100 ms ago is not yet audible on AirPods-like output, and is on screen at once on speakers.
        const double rate = 48_000.0;
        const double now = 100.0;
        var source = new VisualizerAudio(now: () => now);
        var buffer = new float[480];
        // One second of capture ending now: silence, then a note for the last 100 ms.
        for (var step = 0; step < 100; step++)
        {
            var end = now - 1 + (step + 1) * 0.01;
            var note = step >= 90;
            for (var i = 0; i < buffer.Length; i++)
                buffer[i] = note ? (float)(0.3 * Math.Sin(2 * Math.PI * 440 * (step * 480 + i) / rate)) : 0;
            source.Append(buffer, rate, 0, end);
        }
        source.SetOutputLatency(0.2);
        Assert.True(Math.Abs(source.VisualDelay - 0.155) <= 0.001);
        Assert.True(source.Latest().Bands.Max() < 0.05, "Bluetooth output: the note has not been heard yet");
        var delayed = source.Pcm(2048);
        source.SetOutputLatency(0.01);
        Assert.True(source.Latest().Bands.Max() > 0.4, "Speakers: the note shows immediately");
        var immediate = source.Pcm(2048);
        var lead = (double)((immediate?.End ?? 0) - (delayed?.End ?? 0));
        Assert.True(Math.Abs(lead - 0.155 * rate) <= 1, "Web scenes receive the same delay-aligned window");
    }

    // Ported from VisualizerSpectrumTests.swift: testADCOffsetIsInvisibleAndMusicOnTopOfItIsUnchanged
    // An inaudible DC offset used to read as a bass glow at half height (0.002 lit the lowest bands to 0.5 once the
    // auto-gain boosted it) and kept a silent source "audible", so the idle scene never returned.
    [Fact]
    public void ADCOffsetIsInvisibleAndMusicOnTopOfItIsUnchanged()
    {
        const double rate = 48_000.0;
        foreach (var offset in new[] { 0.002f, 0.05f })
        {
            var audio = new VisualizerAudio(now: () => 0);
            var buffer = new float[480];
            Array.Fill(buffer, offset);
            var time = 1_000.0;
            for (var step = 0; step < 300; step++)
            {
                time += 0.01;
                audio.Append(buffer, rate, 0, time);
            }
            var still = audio.Latest(time);
            Assert.True(still.Bands.Max() < 0.02, $"offset {offset}");
            Assert.True(still.Level < 0.02, $"offset {offset}");
            Assert.True(still.Wavefield.Rms < 0.0012 * 2.4, "A constant offset is not sound");
            Assert.True(Math.Abs(still.Beat) <= 0.001);
            var window = audio.Pcm(2048, time);
            Assert.True((window?.Samples.Max(MathF.Abs) ?? 1) < 0.001, "Web scenes receive the music without the offset");

            // A 110 Hz note riding on the offset reads as it does without it.
            var sample = 0;
            AudioAnalyzer plain = new(), shifted = new();
            plain.SetAutoGainEnabled(false); shifted.SetAutoGainEnabled(false);
            for (var step = 0; step < 50; step++)
            {
                for (var i = 0; i < buffer.Length; i++) buffer[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 110 * (sample + i) / rate));
                sample += buffer.Length;
                var end = 2_000 + (step + 1) * 0.01;
                plain.Append(buffer, rate, end);
                for (var i = 0; i < buffer.Length; i++) buffer[i] += offset;
                shifted.Append(buffer, rate, end);
            }
            AudioSnapshot a = plain.Latest(2_000.5), b = shifted.Latest(2_000.5);
            for (var band = 0; band < AudioSnapshot.BandCount; band++)
                Assert.True(Math.Abs(a.Bands[band] - b.Bands[band]) <= 0.02, $"band {band}");
            Assert.True(Math.Abs(a.Level - b.Level) <= 0.01);
        }
    }

    // Ported from VisualizerSpectrumTests.swift: testNonFiniteSamplesNeverReachReaders
    // Capture sanitises its samples, but nothing non-finite that reaches the analyser may reach a shader, a web scene or
    // the sound types.
    [Fact]
    public void NonFiniteSamplesNeverReachReaders()
    {
        const double rate = 48_000.0;
        var audio = new VisualizerAudio(now: () => 0);
        var buffer = new float[480];
        var time = 10.0;
        for (var step = 0; step < 100; step++)
        {
            for (var i = 0; i < buffer.Length; i++) buffer[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * (step * 480 + i) / rate));
            if (step == 40) { buffer[7] = float.NaN; buffer[8] = float.PositiveInfinity; buffer[9] = float.NegativeInfinity; }
            time += 0.01;
            audio.Append(buffer, rate, 0, time);
            if (step == 42 || step == 99)
            {
                var snapshot = audio.Latest(time);
                Assert.All(snapshot.Bands.ToArray(), v => Assert.True(float.IsFinite(v)));
                Assert.All(snapshot.Waveform.ToArray(), v => Assert.True(float.IsFinite(v)));
                Assert.True(float.IsFinite(snapshot.Level) && float.IsFinite(snapshot.Wavefield.Rms));
                Assert.True(float.IsFinite(snapshot.Sound.Bass) && float.IsFinite(snapshot.Sound.Pad) && float.IsFinite(snapshot.Sound.Vocal));
                Assert.True(audio.Pcm(4096, time)?.Samples.All(float.IsFinite) ?? false);
            }
        }
        Assert.True(audio.Latest(time).Bands.Max() > 0.3, "The note still shows once the bad samples pass");
    }

    // Ported from VisualizerSpectrumTests.swift: testTransientsRiseWithinAFrameWhileBandsKeepTheirSoftAttack
    [Fact]
    public void TransientsRiseWithinAFrameWhileBandsKeepTheirSoftAttack()
    {
        var smoother = new VisualizerSmoother();
        var hit = new AudioSnapshot { Beat = 1, Bass = 1 };
        smoother.Frame(new AudioSnapshot(), 0);
        var first = smoother.Frame(hit, 1.0 / 60);
        Assert.True(first.Beat > 0.7, "A kick lands on the next frame");
        Assert.True(first.Bass < 0.2, "Bands still start with zero velocity");
        var released = first;
        for (var frame = 2; frame <= 30; frame++) released = smoother.Frame(new AudioSnapshot(), frame / 60.0);
        Assert.True(released.Beat < 0.4);
        Assert.True(released.Beat > 0, "The release stays gentle");
    }

    // Ported from VisualizerSpectrumTests.swift: testReadersGetIndependentCopiesWhileCaptureContinues
    [Fact]
    public async Task ReadersGetIndependentCopiesWhileCaptureContinues()
    {
        var analyzer = new AudioAnalyzer();
        var sample = 0;
        var end = Feed(analyzer, 880, 48_000, 0.3, 5, ref sample);
        var snapshot = analyzer.Latest(end);
        var window = analyzer.WavefieldPcm(end);
        var bands = snapshot.Bands.ToArray();
        var samples = window?.Samples.ToArray();
        // Concurrent capture on another thread while this one reads.
        var local = sample;
        var capture = Task.Run(() => Feed(analyzer, null, 48_000, 0.5, 5, ref local), TestContext.Current.CancellationToken);
        for (var i = 0; i < 200; i++)
        {
            var frame = analyzer.Latest();
            Assert.All(frame.Bands.ToArray(), v => Assert.True(float.IsFinite(v)));
        }
        await capture.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(bands, snapshot.Bands.ToArray());
        Assert.Equal(samples, window?.Samples); // A window handed out never changes underneath its reader
        Assert.Equal(2048, window?.Samples.Length);
    }
}
