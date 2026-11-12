using System.Runtime.CompilerServices;
using Lunalyr.Core.Visualisers;

namespace Lunalyr.Core.Tests.Visualisers;

/// <summary>
/// The sound types (kick, snare, hat, bass, vocal, pad) on synthetic music with ground truth. The first group feeds the
/// detector the same calibrated spectra AudioAnalyzer hands it, straight from memory; the pipeline group goes through
/// VisualizerAudio.Append and Latest(at), as the capture worker and the render loop do. No audio devices are involved,
/// and nothing is ever played.
/// </summary>
public class SoundTypeDetectorTests
{
    /// <summary>Every fixture is synthesised and analysed once per test run.</summary>
    private static class Runs
    {
        public static SoundTypeRun Run(SoundFixture fixture, int hopDivisor = 4) =>
            new(fixture, AudioAnalyzer.FftSize(fixture.Fs), hopDivisor);

        private static readonly Lazy<SoundTypeRun> LazyKick = new(() => Run(SoundFixtures.Kicks(48_000)));
        private static readonly Lazy<SoundTypeRun> LazyEightOhEight =
            new(() => Run(SoundFixtures.Kicks(48_000, SoundSynth.KickKind.EightOhEight)));
        private static readonly Lazy<SoundTypeRun> LazySnare = new(() => Run(SoundFixtures.Snares(48_000)));
        private static readonly Lazy<SoundTypeRun> LazyHats = new(() => Run(SoundFixtures.Hats(48_000)));
        private static readonly Lazy<SoundTypeRun> LazyBass = new(() => Run(SoundFixtures.Bassline(48_000)));
        private static readonly Lazy<SoundTypeRun> LazyVocal = new(() => Run(SoundFixtures.Vocal(48_000, consonants: false)));
        private static readonly Lazy<SoundTypeRun> LazyConsonants = new(() => Run(SoundFixtures.Vocal(48_000, consonants: true)));
        private static readonly Lazy<SoundTypeRun> LazyPad = new(() => Run(SoundFixtures.Pads(48_000)));
        private static readonly Lazy<SoundFixture> LazyMixFixture = new(() => SoundFixtures.Mix(48_000));
        private static readonly Lazy<SoundTypeRun> LazyMix = new(() => Run(MixFixture));
        private static readonly Lazy<SoundTypeRun> LazyNoise = new(() => Run(SoundFixtures.PinkNoise(48_000)));

        public static SoundTypeRun Kick => LazyKick.Value;
        public static SoundTypeRun EightOhEight => LazyEightOhEight.Value;
        public static SoundTypeRun Snare => LazySnare.Value;
        public static SoundTypeRun Hats => LazyHats.Value;
        public static SoundTypeRun Bass => LazyBass.Value;
        public static SoundTypeRun Vocal => LazyVocal.Value;
        public static SoundTypeRun Consonants => LazyConsonants.Value;
        public static SoundTypeRun Pad => LazyPad.Value;
        public static SoundFixture MixFixture => LazyMixFixture.Value;
        public static SoundTypeRun Mix => LazyMix.Value;
        public static SoundTypeRun Noise => LazyNoise.Value;

        /// <summary>3 s of the mix, from 3 s: every class plays in it.</summary>
        public static float[] Clip => MixFixture.Samples[144_000..288_000];

        /// <summary>Its first 2 s.</summary>
        public static float[] ShortClip => Clip[..96_000];
    }

    /// <summary>Capture time of the first sample in the pipeline tests.</summary>
    private const double Origin = 1_000.0;

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    // MARK: - Separation

    // Ported from SoundTypeDetectorTests.swift: testKicksAreKicksAndNothingElse
    [Fact]
    public void KicksAreKicksAndNothingElse()
    {
        var r = Runs.Kick;
        Assert.True(r.F1(SoundClass.Kick).F1 >= 0.95);
        Assert.True(Math.Max(r.Count(SoundClass.Snare), r.Count(SoundClass.Hat)) <= 1, "A beater click is not a snare or a hat");
        Assert.True(r.P95(SoundClass.Bass) <= 0.2, "Kick tails are held out of the bass");
        Assert.True(Math.Max(r.P95(SoundClass.Vocal), r.P95(SoundClass.Pad)) <= 0.1);
        Assert.True(Median(r.HitPeaks(SoundClass.Kick)) >= 0.5, "Each hit flares");
    }

    // Ported from SoundTypeDetectorTests.swift: testAnEightOhEightIsAKickWithABassTail
    [Fact]
    public void AnEightOhEightIsAKickWithABassTail()
    {
        var r = Runs.EightOhEight;
        Assert.True(r.F1(SoundClass.Kick).Recall >= 0.9);
        Assert.True(r.Mean(SoundClass.Bass, SoundClass.Kick, 0.15, 0.45) >= 0.4, "A long 808 tail is bass");
        Assert.True(r.Count(SoundClass.Snare) + r.Count(SoundClass.Hat) <= 2);
    }

    // Ported from SoundTypeDetectorTests.swift: testSnaresAndClapsAreSnares
    [Fact]
    public void SnaresAndClapsAreSnares()
    {
        var r = Runs.Snare;
        Assert.True(r.F1(SoundClass.Snare).F1 >= 0.95);
        Assert.True(r.Count(SoundClass.Kick) <= 1);
        Assert.True(r.Count(SoundClass.Hat) <= 0.2 * r.Fixture.Truth.OnsetsOf(SoundClass.Snare).Count);
        Assert.True(new[] { r.P95(SoundClass.Bass), r.P95(SoundClass.Vocal), r.P95(SoundClass.Pad) }.Max() <= 0.15,
            "A snare's ringing body is not tonal");
    }

    // Ported from SoundTypeDetectorTests.swift: testHiHatsAreHats
    [Fact]
    public void HiHatsAreHats()
    {
        var r = Runs.Hats;
        Assert.True(r.F1(SoundClass.Hat).F1 >= 0.9);
        Assert.Equal(0, r.Count(SoundClass.Kick));
        Assert.True(r.Count(SoundClass.Snare) <= 2);
        Assert.True(new[] { r.P95(SoundClass.Bass), r.P95(SoundClass.Vocal), r.P95(SoundClass.Pad) }.Max() <= 0.1);
    }

    // Ported from SoundTypeDetectorTests.swift: testABassLineIsBassNotKicks
    [Fact]
    public void ABassLineIsBassNotKicks()
    {
        var r = Runs.Bass;
        var bass = r.Spans(SoundClass.Bass);
        Assert.True(bass.Inside >= 0.6, $"inside {bass.Inside}");
        Assert.True(bass.Outside <= 0.1, $"outside {bass.Outside}");
        Assert.True(r.Count(SoundClass.Kick) <= 0.25 * r.Fixture.Truth.ActiveOf(SoundClass.Bass).Count,
            "Only a note from silence, after a gap or an octave drop may pass for a kick");
        Assert.True(Math.Max(r.P95(SoundClass.Vocal), r.P95(SoundClass.Pad)) <= 0.3, "A bass line's harmonics belong to it");
    }

    // Ported from SoundTypeDetectorTests.swift: testASungVoiceIsVocalNotPadOrDrums
    [Fact]
    public void ASungVoiceIsVocalNotPadOrDrums()
    {
        foreach (var (r, consonants) in new[] { (Runs.Vocal, false), (Runs.Consonants, true) })
        {
            var name = r.Fixture.Name;
            var vocal = r.Spans(SoundClass.Vocal);
            Assert.True(vocal.Inside >= 0.6, $"{name}: inside {vocal.Inside}");
            Assert.True(vocal.Outside <= 0.15, $"{name}: outside {vocal.Outside}");
            Assert.True(r.P95(SoundClass.Pad) <= 0.25, $"{name}: a moving voice is not a pad");
            Assert.True(r.P95(SoundClass.Bass) <= 0.1, name);
            Assert.Equal(0, r.Count(SoundClass.Kick));
            Assert.True(r.Count(SoundClass.Snare) + r.Count(SoundClass.Hat) <= (consonants ? 3 : 1), "Consonants are not drums");
        }
    }

    // Ported from SoundTypeDetectorTests.swift: testPadChordsArePad
    [Fact]
    public void PadChordsArePad()
    {
        var r = Runs.Pad;
        Assert.True(r.Spans(SoundClass.Pad).Inside >= 0.6);
        Assert.True(r.P95(SoundClass.Vocal) <= 0.25, "A chord's beating partials do not move like a voice");
        Assert.True(r.P95(SoundClass.Bass) <= 0.25, "A chord's root is not a bass line");
        Assert.True(new[] { r.Count(SoundClass.Kick), r.Count(SoundClass.Snare), r.Count(SoundClass.Hat) }.Max() <= 1);
    }

    // Ported from SoundTypeDetectorTests.swift: testAMixSeparatesEveryClass
    [Fact]
    public void AMixSeparatesEveryClass()
    {
        var r = Runs.Mix;
        Assert.True(r.F1(SoundClass.Kick).F1 >= 0.9);
        Assert.True(r.F1(SoundClass.Snare).F1 >= 0.9);
        Assert.True(r.F1(SoundClass.Hat).F1 >= 0.65, "Dense hats, some inside bright snare tails");
        var bass = r.Spans(SoundClass.Bass);
        var vocal = r.Spans(SoundClass.Vocal);
        Assert.True(bass.Inside >= 0.5);
        Assert.True(bass.Outside <= 0.1);
        Assert.True(vocal.Inside >= 0.35);
        Assert.True(vocal.Outside <= 0.1);
        Assert.True(r.Spans(SoundClass.Pad).Inside >= 0.15);
        // Counters never step back, and an onset time only changes with its counter.
        for (var i = 1; i < r.Reads.Count; i++)
        {
            SoundTypeFeatures a = r.Reads[i - 1], b = r.Reads[i];
            Assert.True(b.KickOnsets >= a.KickOnsets);
            Assert.True(b.HatOnsets >= a.HatOnsets);
            if (b.HatOnsets == a.HatOnsets) Assert.Equal(a.HatTime, b.HatTime);
            else Assert.True(b.HatTime > a.HatTime);
        }
    }

    // Ported from SoundTypeDetectorTests.swift: testPinkNoiseLightsNothing
    [Fact]
    public void PinkNoiseLightsNothing()
    {
        var r = Runs.Noise;
        Assert.True(new[] { r.Count(SoundClass.Kick), r.Count(SoundClass.Snare), r.Count(SoundClass.Hat) }.Max() <= 2);
        Assert.True(new[] { r.P95(SoundClass.Bass), r.P95(SoundClass.Vocal), r.P95(SoundClass.Pad) }.Max() <= 0.25);
    }

    // MARK: - Independence

    private static void AssertMatchesTheMix(SoundTypeRun r, string label)
    {
        var reference = Runs.Mix;
        foreach (var c in new[] { SoundClass.Kick, SoundClass.Snare })
        {
            Assert.True(Math.Abs(r.F1(c).F1 - reference.F1(c).F1) <= 0.10, $"{label} {c}: {r.F1(c).F1} vs {reference.F1(c).F1}");
        }
        Assert.True(Math.Abs(r.F1(SoundClass.Hat).F1 - reference.F1(SoundClass.Hat).F1) <= 0.15,
            $"{label} hat: {r.F1(SoundClass.Hat).F1} vs {reference.F1(SoundClass.Hat).F1}");
        foreach (var c in new[] { SoundClass.Bass, SoundClass.Vocal, SoundClass.Pad })
        {
            var spans = r.Spans(c);
            var expected = reference.Spans(c);
            Assert.True(Math.Abs(spans.Inside - expected.Inside) <= 0.12, $"{label} {c}: {spans.Inside} vs {expected.Inside}");
            Assert.True(spans.Outside <= 0.1, $"{label} {c}: outside {spans.Outside}");
        }
    }

    // Ported from SoundTypeDetectorTests.swift: testSampleRatesAndHopsGiveTheSameResult
    [Fact]
    public void SampleRatesAndHopsGiveTheSameResult()
    {
        AssertMatchesTheMix(Runs.Run(SoundFixtures.Mix(44_100)), "44.1 kHz");
        AssertMatchesTheMix(Runs.Run(SoundFixtures.Mix(96_000)), "96 kHz");
        AssertMatchesTheMix(Runs.Run(Runs.MixFixture, hopDivisor: 2), "21.3 ms hop");
        AssertMatchesTheMix(Runs.Run(Runs.MixFixture, hopDivisor: 8), "5.3 ms hop");
    }

    // Ported from SoundTypeDetectorTests.swift: testQuietAndLoudMastersGiveTheSameResult
    [Fact]
    public void QuietAndLoudMastersGiveTheSameResult()
    {
        AssertMatchesTheMix(Runs.Run(Runs.MixFixture.Mastered(0.063f, name: "quiet")), "−24 dB master");
        AssertMatchesTheMix(Runs.Run(Runs.MixFixture.Mastered(2, softClip: 1, name: "loud")), "+6 dB limited master");
    }

    // Ported from SoundTypeDetectorTests.swift: testInvalidSpectraAndUnusualRatesStayFinite
    [Fact]
    public void InvalidSpectraAndUnusualRatesStayFinite()
    {
        var detector = new SoundTypeDetector();
        var power = new float[1024];
        Array.Fill(power, 1e-4f);
        power[7] = float.NaN; power[300] = float.PositiveInfinity; power[500] = -3;
        for (var pass = 0; pass < 40; pass++) detector.Process(power, 23.4375f, 0.0107, pass * 0.0107);
        var output = detector.Current;
        Assert.All(new[] { output.Bass, output.Vocal, output.Pad }, v => Assert.True(float.IsFinite(v) && v >= 0 && v <= 1));
        // Too short a spectrum is ignored; the lowest and highest rates the analyser accepts work.
        detector.Process(power.AsSpan(0, 8), 23, 0.01, 1);
        foreach (var (rate, bins) in new[] { (8_000.0, 256), (192_000.0, 4096) })
        {
            var spectrum = new float[bins];
            Array.Fill(spectrum, 1e-3f);
            for (var pass = 0; pass < 10; pass++)
                detector.Process(spectrum, (float)rate / (2 * bins), 0.0107, 2 + pass * 0.0107);
            Assert.True(float.IsFinite(detector.Current.Bass), $"{rate} Hz");
        }
    }

    // MARK: - The pipeline

    /// <summary>
    /// Feeds <paramref name="left"/> (and <paramref name="right"/>, as a second channel) to <paramref name="audio"/> in
    /// <paramref name="chunk"/>-sample buffers stamped with exact capture times, and after each buffer reads every instant
    /// index / fps that the buffer completed.
    /// </summary>
    private static double Feed(VisualizerAudio audio, float[] left, float[]? right = null, double fs = 48_000, int chunk = 512,
        double start = 0, double? fps = null, Action<double, AudioSnapshot>? read = null)
    {
        if (right is not null) audio.SetChannelCount(2);
        int i = 0, frame = 0;
        while (i < left.Length)
        {
            var n = Math.Min(chunk, left.Length - i);
            var end = Origin + start + (i + n) / fs;
            audio.Append(left.AsSpan(i, n), fs, 0, end);
            if (right is not null) audio.Append(right.AsSpan(i, n), fs, 1, end);
            i += n;
            if (fps is { } rate)
            {
                while (Origin + frame / rate <= end)
                {
                    var t = Origin + frame / rate;
                    if (t >= Origin + start) read?.Invoke(t, audio.Latest(t));
                    frame++;
                }
            }
        }
        return Origin + start + left.Length / fs;
    }

    // Ported from SoundTypeDetectorTests.swift: testTheAnalyzerHandsOverCalibratedSpectraEveryQuarterWindow
    [Fact]
    public void TheAnalyzerHandsOverCalibratedSpectraEveryQuarterWindow()
    {
        var analyzer = new AudioAnalyzer();
        var passes = new List<(long Position, double Time, double Dt, float BinHz, float[] Power)>();
        analyzer.ObserveSpectra((in SpectrumPass pass) =>
            passes.Add((pass.Position, pass.Time, pass.Dt, pass.BinHz, pass.Power.ToArray())));
        // 0.25 s of a 1 kHz tone at amplitude 0.5, in 480-sample buffers.
        const double fs = 48_000.0;
        const float amplitude = 0.5f;
        var samples = new float[12_000];
        for (var n = 0; n < samples.Length; n++) samples[n] = amplitude * (float)Math.Sin(2 * Math.PI * 1000 * n / fs);
        for (var i = 0; i < samples.Length; i += 480)
            analyzer.Append(samples.AsSpan(i, 480), fs, Origin + (i + 480) / fs);
        Assert.Equal(12_000 / 512, passes.Count); // One spectrum per quarter window, full passes and halfway points alike
        var reference = new PlainSpectrum(2048);
        // The analyser removes any DC offset first (AudioAnalyzer.DcCutoff).
        var pole = (float)Math.Exp(-2 * Math.PI * AudioAnalyzer.DcCutoff / fs);
        float previousInput = 0, previousOutput = 0;
        var analysed = samples.Select(x =>
        {
            var y = x - previousInput + pole * previousOutput;
            previousInput = x; previousOutput = y;
            return y;
        }).ToArray();
        for (var k = 0; k < passes.Count; k++)
        {
            var pass = passes[k];
            Assert.Equal((k + 1) * 512, pass.Position);
            Assert.True(Math.Abs(pass.Time - (Origin + pass.Position / fs)) <= 1e-9, "The newest sample's capture time");
            Assert.True(Math.Abs(pass.Dt - 512 / fs) <= 1e-12);
            Assert.Equal(23.4375f, pass.BinHz);
            Assert.Equal(1024, pass.Power.Length); // The plain window's bins, not the zero-padded transform's
            Assert.Equal(0f, pass.Power[0]);
            // Full passes reuse the padded transform; between them a plain FFT runs. Both are the plain FFT of the
            // newest window.
            var expected = reference.Compute(analysed, (int)pass.Position).ToArray();
            var scale = expected.Max();
            var worst = pass.Power.Zip(expected).Max(p => Math.Abs(p.First - p.Second));
            Assert.True(worst < scale * 1e-4, $"pass {k}: {worst} vs {scale}");
            if (pass.Position >= 2048)
            {
                // Calibrated: a sinusoid of amplitude A sums to A²/2 over its main lobe (1 kHz is bin 42.7).
                var lobe = pass.Power[39..47].Sum();
                Assert.True(Math.Abs(lobe - amplitude * amplitude / 2) <= 0.002, $"main lobe {lobe}");
            }
        }
        // Once the observer is removed, nothing more is handed over.
        analyzer.ObserveSpectra(null);
        analyzer.Append(samples.AsSpan(0, 4800), fs, Origin + 1);
        Assert.Equal(12_000 / 512, passes.Count);
    }

    // Ported from SoundTypeDetectorTests.swift: testStereoChannelsShareOneDetectorWithoutCancelling
    [Fact]
    public void StereoChannelsShareOneDetectorWithoutCancelling()
    {
        var clip = Runs.ShortClip;
        VisualizerAudio mono = new(), stereo = new();
        List<SoundTypeFeatures> monoReads = [], stereoReads = [];
        Feed(mono, clip, fps: 50, read: (_, s) => monoReads.Add(s.Sound));
        Feed(stereo, clip, right: clip.Select(x => -x).ToArray(), fps: 50, read: (_, s) => stereoReads.Add(s.Sound));
        Assert.True(monoReads[^1].KickOnsets >= 3);
        // Opposite-phase channels average to the same power, never to silence.
        Assert.Equal(monoReads, stereoReads);
        // Hard-panned instruments: kicks on the left only, hats on the right only, a 32nd note after each kick.
        const double seconds = 2.5;
        const int count = (int)(seconds * 48_000), delay = 3_000;
        var panned = new VisualizerAudio();
        float[] right = [.. new float[delay], .. Runs.Hats.Fixture.Samples.Take(count - delay)];
        var end = Feed(panned, Runs.Kick.Fixture.Samples[..count], right: right);
        var sound = panned.Latest(end).Sound;
        var kicks = Runs.Kick.Fixture.Truth.OnsetsOf(SoundClass.Kick).Count(t => t < seconds - 0.05);
        var hats = Runs.Hats.Fixture.Truth.OnsetsOf(SoundClass.Hat).Count(t => t + delay / 48_000.0 < seconds - 0.05);
        Assert.Equal(kicks, (int)sound.KickOnsets);
        Assert.True(sound.HatOnsets >= 0.85 * hats, $"{sound.HatOnsets} of {hats} hats");
        Assert.True((int)sound.HatOnsets <= hats, $"{sound.HatOnsets} of {hats} hats");
        Assert.Equal(0u, sound.SnareOnsets);
    }

    // Ported from SoundTypeDetectorTests.swift: testLargeStereoBuffersAnalyseEachInstantOnce
    // One append can carry more spectra than the stereo pairing keeps waiting: 8192 samples at 48 kHz are 16 per
    // channel, 4096 at 16 kHz are 32. Every instant still reaches the detector exactly once, so two identical channels
    // give exactly the mono result.
    [Fact]
    public void LargeStereoBuffersAnalyseEachInstantOnce()
    {
        var low = SoundFixtures.Kicks(16_000);
        foreach (var (samples, fs, chunk) in new[] { (Runs.Clip[..(6 * 8192)], 48_000.0, 8192), (low.Samples[..(8 * 4096)], 16_000.0, 4096) })
        {
            var label = $"{(int)fs} Hz, {chunk}-sample buffers";
            VisualizerAudio mono = new(), stereo = new();
            var end = Feed(mono, samples, fs: fs, chunk: chunk);
            Feed(stereo, samples, right: samples, fs: fs, chunk: chunk);
            var sound = mono.Latest(end).Sound;
            Assert.True(sound.KickOnsets + sound.SnareOnsets + sound.HatOnsets > 3, label);
            Assert.Equal(mono.SoundTypePasses, stereo.SoundTypePasses);
            Assert.Equal(sound, stereo.Latest(end).Sound);
        }
    }

    // Ported from SoundTypeDetectorTests.swift: testTheLeftChannelStillCountsAfterTheInputTurnsStereo
    // The right channel's analyser sits idle while the input is mono. When the input turns stereo mid-source (mid-hop
    // here), it restarts in step with the left one, so the channels still pair: kicks on the left alone keep counting.
    [Fact]
    public void TheLeftChannelStillCountsAfterTheInputTurnsStereo()
    {
        var kicks = Runs.Kick.Fixture.Samples[..144_000];
        const int split = 72_300;
        var audio = new VisualizerAudio();
        var switched = Feed(audio, kicks[..split], chunk: 1000);
        var truth = Runs.Kick.Fixture.Truth.OnsetsOf(SoundClass.Kick);
        Assert.Equal(truth.Count(t => t < split / 48_000.0 - 0.05), (int)audio.Latest(switched).Sound.KickOnsets);
        var passes = audio.SoundTypePasses;
        var end = Feed(audio, kicks[split..], right: new float[kicks.Length - split], chunk: 1000, start: split / 48_000.0);
        Assert.Equal(truth.Count(t => t < 3 - 0.05), (int)audio.Latest(end).Sound.KickOnsets);
        // One pass per quarter window.
        Assert.Equal((kicks.Length - split + split % 512) / 512, audio.SoundTypePasses - passes);
    }

    // Ported from SoundTypeDetectorTests.swift: testTheSoundTypesShownNeverGoBackWhenTheDisplayWaitsLonger
    // Connecting headphones makes the display wait up to a quarter second longer for the sound, so the display time
    // steps back. The sound types shown never do: they hold until the display catches up, so no counter steps back and
    // no onset flares twice. (The Mac reads its real clock; here a manual clock moves on by a millisecond between
    // reads, as the real one would.)
    [Fact]
    public void TheSoundTypesShownNeverGoBackWhenTheDisplayWaitsLonger()
    {
        var clock = 5_000.0;
        var audio = new VisualizerAudio(now: () => clock); // measures no output device: no wait
        var clip = Runs.ShortClip;
        // The clip ends a second from now, so the display shows its middle.
        Feed(audio, clip, start: clock + 1 - clip.Length / 48_000.0 - Origin);
        var shown = audio.Latest().Sound;
        Assert.True(shown.HatOnsets > 0);
        clock += 0.001;
        audio.SetOutputLatency(VisualizerAudio.MaximumDelay + VisualizerAudio.PipelineLatency);
        Assert.True(audio.PresentationTime < shown.PresentationTime, "The display time stepped back");
        Assert.Equal(shown, audio.Latest().Sound); // Held at the newest time shown
        Assert.Equal(shown, audio.LatestSound()); // The web engines' read holds too
        // Once the display is past it again, the sound types move on.
        clock += 0.001;
        audio.SetOutputLatency(0);
        Assert.True(audio.Latest().Sound.PresentationTime > shown.PresentationTime);
        // A new source starts over at the display time.
        clock += 0.001;
        audio.SetOutputLatency(VisualizerAudio.MaximumDelay + VisualizerAudio.PipelineLatency);
        audio.Reset();
        Assert.True(audio.Latest().Sound.PresentationTime < shown.PresentationTime);
    }

    // Ported from SoundTypeDetectorTests.swift: testReadsAreIndependentOfFrameRate
    // Read-time evaluation keeps no per-frame state: reading four times as often changes nothing at the instants both
    // rates share, for the snapshot, the smoother's output and the uniforms, calm or not.
    [Fact]
    public void ReadsAreIndependentOfFrameRate()
    {
        static List<SoundTypeFeatures> Run(double fps)
        {
            var audio = new VisualizerAudio();
            var smoother = new VisualizerSmoother();
            var reads = new List<SoundTypeFeatures>();
            Feed(audio, Runs.Clip, fps: fps, read: (t, snapshot) =>
            {
                // The smoother passes sound types through.
                Assert.Equal(snapshot.Sound, smoother.Frame(snapshot, t).Sound);
                reads.Add(snapshot.Sound);
            });
            return reads;
        }
        List<SoundTypeFeatures> fast = Run(120), slow = Run(30);
        Assert.True(slow[^1].HatOnsets > 5);
        var compared = 0;
        for (var k = 0; k < slow.Count && 4 * k < fast.Count; k++)
        {
            var read = slow[k];
            Assert.Equal(fast[4 * k], read);
            foreach (var calm in new[] { false, true })
            {
                var response = new VisualizerResponse(reduceMotion: calm);
                Assert.Equal(new SoundTypeUniforms(fast[4 * k], fast[4 * k].PresentationTime, response),
                    new SoundTypeUniforms(read, read.PresentationTime, response));
            }
            compared++;
        }
        Assert.True(compared > 60);
        // Counters never step back; ages restart only when their counter moves.
        for (var i = 1; i < fast.Count; i++)
        {
            SoundTypeFeatures a = fast[i - 1], b = fast[i];
            Assert.True(b.KickOnsets >= a.KickOnsets);
            Assert.True(b.SnareOnsets >= a.SnareOnsets);
            Assert.True(b.HatOnsets >= a.HatOnsets);
            if (b.KickOnsets == a.KickOnsets) Assert.Equal(a.KickTime, b.KickTime);
        }
    }

    // Ported from SoundTypeDetectorTests.swift: testCallbackSizeDoesNotChangeTheResult
    [Fact]
    public void CallbackSizeDoesNotChangeTheResult()
    {
        static (HashSet<long> All, SoundTypeFeatures End) Onsets(int chunk)
        {
            var audio = new VisualizerAudio();
            var all = new HashSet<long>();
            // The rings hold the newest onsets of each class: collected after every buffer, they give every onset
            // whatever the buffer size.
            var end = Feed(audio, Runs.ShortClip, chunk: chunk, fps: 25, read: (_, snapshot) =>
            {
                foreach (var (ring, tag) in new[] { (snapshot.Sound.KickRing, 0), (snapshot.Sound.SnareRing, 1), (snapshot.Sound.HatRing, 2) })
                {
                    foreach (var onset in ring)
                    {
                        if (double.IsFinite(onset.Time)) all.Add((long)Math.Round(onset.Time * 1e5, MidpointRounding.AwayFromZero) * 4 + tag);
                    }
                }
            });
            return (all, audio.Latest(end).Sound);
        }
        var reference = Onsets(512);
        Assert.True(reference.All.Count > 8);
        foreach (var chunk in new[] { 441, 4096 })
        {
            // Buffer boundaries move the capture timestamps by rounding only.
            var other = Onsets(chunk);
            SoundTypeFeatures a = other.End, b = reference.End;
            Assert.True(reference.All.SetEquals(other.All), $"{chunk}-sample buffers");
            Assert.Equal(new[] { b.KickOnsets, b.SnareOnsets, b.HatOnsets }, new[] { a.KickOnsets, a.SnareOnsets, a.HatOnsets });
            foreach (var (x, y) in new[] { (a.KickTime, b.KickTime), (a.SnareTime, b.SnareTime), (a.HatTime, b.HatTime) })
                Assert.True(Math.Abs(x - y) <= 1e-9, $"{chunk}: {x} vs {y}");
            foreach (var c in SoundClassExtensions.All)
            {
                Assert.True(Math.Abs(SoundTypeRun.Value(a, c) - SoundTypeRun.Value(b, c)) <= 1e-5,
                    $"{chunk}: {c} {SoundTypeRun.Value(a, c)} vs {SoundTypeRun.Value(b, c)}");
            }
        }
    }

    // Ported from SoundTypeDetectorTests.swift: testSilenceFadesEveryEnvelopeAndHoldsTheCounters
    [Fact]
    public void SilenceFadesEveryEnvelopeAndHoldsTheCounters()
    {
        var audio = new VisualizerAudio();
        var music = Runs.ShortClip;
        float loudest = 0;
        var quietFrom = Feed(audio, music, fps: 20, read: (_, snapshot) =>
            loudest = new[] { loudest, snapshot.Sound.Bass, snapshot.Sound.Vocal, snapshot.Sound.Pad }.Max());
        var playing = audio.Latest(quietFrom).Sound;
        Assert.True(loudest > 0.3);
        Assert.True(playing.KickOnsets > 0);
        // Two seconds of digital silence while capture continues.
        var end = Feed(audio, new float[96_000], start: 2);
        var silent = audio.Latest(end).Sound;
        foreach (var c in SoundClassExtensions.All)
            Assert.True(SoundTypeRun.Value(silent, c) < 0.02, $"{c} fades in silence");
        Assert.Equal(playing.KickOnsets, silent.KickOnsets); // Silence never adds onsets
        Assert.Equal(playing.HatOnsets, silent.HatOnsets);
        Assert.Equal(playing.KickTime, silent.KickTime); // Onset times hold
        Assert.True(end - silent.KickTime > 1.9, "Ages keep growing");
        // Capture stops: values fade with the analyser's idle fade, counters hold.
        var stopped = Feed(audio, music[..24_000], start: 4);
        var paused = audio.Latest(stopped + 2).Sound;
        Assert.True(new[] { paused.Bass, paused.Vocal, paused.Pad }.Max() < 0.001);
        Assert.True(paused.KickOnsets >= playing.KickOnsets);
        // A new source starts from zero.
        audio.Reset();
        Assert.Equal(new SoundTypeFeatures { PresentationTime = stopped + 3 }, audio.Latest(stopped + 3).Sound);
    }

    // MARK: - Reading and publishing

    // Ported from SoundTypeDetectorTests.swift: testTheHistoryShowsOnlyWhatWasHeardByTheDisplayTime
    [Fact]
    public void TheHistoryShowsOnlyWhatWasHeardByTheDisplayTime()
    {
        var history = new SoundTypeHistory();
        var output = new SoundTypeDetector.Output();
        // A pass every 10 ms; a kick at 1.000 s (fired, then topped up) and one at 1.200 s.
        for (var pass = 0; pass <= 40; pass++)
        {
            var time = 0.9 + pass * 0.01;
            if (pass == 11) { output.KickOnsets = 1; output.NewestKick = new SoundOnset(1.0, 0.6f); }
            if (pass == 12) output.NewestKick.Strength = 0.8f;
            if (pass == 31) { output.KickOnsets = 2; output.NewestKick = new SoundOnset(1.2, 0.5f); }
            output.Bass = pass / 40f;
            history.Record(output, time);
        }
        var before = history.Read(0.999);
        Assert.Equal(0u, before.KickOnsets); // A display lagging capture has not heard the kick yet
        Assert.Equal(0f, before.Kick);
        Assert.Equal(double.NegativeInfinity, before.KickTime);
        var peak = history.Read(1.0 + 0.010 * Math.Log(27)); // the normal kick shape peaks here
        Assert.Equal(1u, peak.KickOnsets);
        Assert.True(Math.Abs(peak.Kick - 0.8) <= 1e-4, "The top-up raised the strength");
        Assert.Equal(new SoundOnset(1.0, 0.8f), peak.KickRing[0]);
        var between = history.Read(1.105);
        Assert.True(Math.Abs(between.Bass - (20f + 0.5f) / 40) <= 1e-5, "Interpolated between the passes around it");
        var both = history.Read(1.25);
        Assert.Equal(2u, both.KickOnsets);
        Assert.Equal(1.2, both.KickTime);
        Assert.Equal(1.2, both.KickRing[0].Time);
        Assert.Equal(1.0, both.KickRing[1].Time);
        Assert.Equal(double.NegativeInfinity, both.KickRing[2].Time);
        Assert.Equal(both, history.Read(1.25)); // A read is a pure function of the display time
        // After the newest pass the value holds, then fades like the bands do once capture stops.
        Assert.Equal(1f, history.Read(1.4).Bass);
        Assert.True(Math.Abs(history.Read(1.3 + 0.25 + 0.2).Bass - Math.Exp(-1)) <= 1e-4);
    }

    // Ported from SoundTypeDetectorTests.swift: testOnsetShapesPeakAtTheirStrength
    [Fact]
    public void OnsetShapesPeakAtTheirStrength()
    {
        SoundTypeShapes normal = SoundTypeShapes.Normal, calm = SoundTypeShapes.Calm;
        foreach (var shape in new[] { normal.Kick, normal.Snare, normal.Hat, calm.Kick, calm.Snare, calm.Hat })
        {
            var peakAge = shape.Attack * Math.Log((shape.Attack + shape.Release) / shape.Attack);
            Assert.True(Math.Abs(shape.Value(peakAge) - 1) <= 1e-9);
            Assert.Equal(0, shape.Value(0));
            Assert.Equal(0, shape.Value(-1));
            Assert.True(shape.Value(4) < 0.01);
        }
        var ring = new SoundOnsetRing();
        ring[0] = new SoundOnset(1, 0.9f);
        ring[1] = new SoundOnset(0.95, 0.4f);
        Assert.Equal(SoundOnsetRing.Capacity, ring.Count);
        Assert.Equal(new[] { 0.9f, 0.4f, 0, 0, 0, 0, 0, 0 }, ring.Select(o => o.Strength).ToArray());
        // Stored inline, so snapshots carry their rings without heap storage.
        Assert.Equal(SoundOnsetRing.Capacity * Unsafe.SizeOf<SoundOnset>(), Unsafe.SizeOf<SoundOnsetRing>());
        var kick = SoundTypeShapes.Normal.Kick;
        // The largest flare, not the sum, so dense hits never saturate.
        Assert.True(Math.Abs(kick.Envelope(ring, 1.033) - (float)(0.9 * kick.Value(0.033))) <= 1e-6);
        // An onset after the display time does not show yet.
        Assert.True(Math.Abs(kick.Envelope(ring, 0.99) - (float)(0.4 * kick.Value(0.04))) <= 1e-6);
    }

    // Ported from SoundTypeDetectorTests.swift: testUniformsCarrySoundTypesWithPunchCalmAndWrap (the sound-type part:
    // the Mac goes through AudioVisualizerView.Coordinator.uniforms, which builds VizUniforms with
    // SoundTypeUniforms(frame.sound, at: presentationTime ?? frame.sound.presentationTime, response:
    // VisualizerResponse(reduceMotion: reduced, punch: punch)); here that is called directly. VizUniforms' own
    // beatCount and reduceMotion fields belong to the renderer and are not checked.)
    [Fact]
    public void UniformsCarrySoundTypesWithPunchCalmAndWrap()
    {
        var frame = new AudioSnapshot();
        frame.Sound.KickRing[0] = new SoundOnset(99.97, 1); // 30 ms before display: near the peak
        frame.Sound.Bass = 0.6f; frame.Sound.Vocal = 0.3f; frame.Sound.Pad = 0.2f;
        frame.Sound.KickOnsets = (1u << 24) + 7; frame.Sound.KickTime = 99.97;
        frame.Sound.HatOnsets = 12; frame.Sound.HatTime = 98.5;
        frame.Sound.PresentationTime = 100;
        SoundTypeUniforms Make(bool calm, double punch = 2, double? time = null) =>
            new(frame.Sound, time ?? frame.Sound.PresentationTime, new VisualizerResponse(reduceMotion: calm, punch: punch));
        SoundTypeUniforms normal = Make(calm: false), calmed = Make(calm: true);
        Assert.Equal(1f, normal.Kick); // Punch scales transients, clamped to 1
        Assert.True(Math.Abs(Make(calm: false, punch: 1).Kick - 0.998) <= 0.002);
        Assert.Equal(0f, Make(calm: false, punch: 0).Kick);
        Assert.True(Math.Abs(normal.Bass - 0.6) <= 1e-6, "Punch leaves sustained classes alone");
        Assert.True(Math.Abs(normal.Vocal - 0.3) <= 1e-6);
        Assert.True(Math.Abs(normal.Pad - 0.2) <= 1e-6);
        Assert.Equal(7f, normal.KickOnsets); // Counters wrap at 2^24 so a float holds them exactly
        Assert.Equal(12f, normal.HatOnsets);
        Assert.Equal(0f, normal.SnareOnsets);
        Assert.True(Math.Abs(normal.KickAge - 0.03) <= 1e-5);
        Assert.True(Math.Abs(normal.HatAge - 1.5) <= 1e-5);
        Assert.Equal(60f, normal.SnareAge); // 60 means no onset yet
        Assert.True(Math.Abs(Make(calm: false, time: 100.03).KickAge - 0.06) <= 1e-5, "Ages are taken at the presentation time");
        Assert.True(calmed.Kick < 0.25, "Calm reads the slow shape, still rising at 30 ms, at half strength");
        var calmPeakTime = 99.97 + 0.12 * Math.Log(6);
        Assert.True(Math.Abs(Make(calm: true, time: calmPeakTime).Kick - 0.5) <= 1e-4, "Calm flares peak at half strength");
        Assert.True(Math.Abs(Make(calm: true, punch: 0.5, time: calmPeakTime).Kick - 0.25) <= 1e-4,
            "Below Punch 1 calm follows Punch: never harder than Punch alone");
        Assert.Equal(0f, Make(calm: true, punch: 0, time: calmPeakTime).Kick); // Beat punch Off stays off under calm
        Assert.True(Math.Abs(calmed.Bass - 0.6) <= 1e-6, "Calm keeps the sustained classes");
        Assert.Equal(7f, calmed.KickOnsets);
        // Silent or synthetic input lights nothing.
        var ambient = AmbientSpectrum.At(3);
        var idle = new SoundTypeUniforms(ambient.Sound, ambient.Sound.PresentationTime, new VisualizerResponse());
        Assert.Equal(new[] { 0f, 0, 0, 0, 0, 0 }, new[] { idle.Kick, idle.Snare, idle.Hat, idle.Bass, idle.Vocal, idle.Pad });
        Assert.Equal(new[] { 60f, 60, 60 }, new[] { idle.KickAge, idle.SnareAge, idle.HatAge });
    }

    /// <summary>A pass every 10 ms with a kick every 0.25 s from 1 s (fired 13 ms before its pass, strength 0.7) and a steady pad.</summary>
    private static SoundTypeHistory SteadyKicks(double end = 4.5)
    {
        var history = new SoundTypeHistory();
        var output = new SoundTypeDetector.Output { Pad = 0.8f };
        for (var pass = 0; pass * 0.01 <= end; pass++)
        {
            var time = pass * 0.01;
            if (pass >= 100 && pass % 25 == 0)
            {
                output.KickOnsets = unchecked(output.KickOnsets + 1);
                output.NewestKick = new SoundOnset(time - 0.013, 0.7f);
            }
            history.Record(output, time);
        }
        return history;
    }

    /// <summary>The scene in the gate tests shows its idle stand-in from 2 s until 92/30 s, an instant that is a frame at 30, 60 and 120 fps.</summary>
    private const double IdleFrom = 2.0, LiveFrom = 92.0 / 30;

    // Ported from SoundTypeDetectorTests.swift: testAnIdleSceneShowsNoNewOnsetsAndHoldsItsCounters
    // While a scene shows its idle stand-in nothing new flares and its counters hold; when the music returns they carry
    // on from there, with no jump, and the hit that brought the music back flares. Hits that started before finish their
    // flares. The pad fades over a quarter second.
    [Fact]
    public void AnIdleSceneShowsNoNewOnsetsAndHoldsItsCounters()
    {
        var history = SteadyKicks();
        var gate = new SoundTypeIdleGate();
        var last = new SoundTypeFeatures();
        var returned = false;
        for (var frame = 0; frame <= 270; frame++)
        {
            var t = frame / 60.0;
            var live = t < IdleFrom || t >= LiveFrom;
            var read = history.Read(t);
            var shown = gate.Frame(read, live, 500 + t);
            Assert.True(shown.KickOnsets >= last.KickOnsets, $"Never decreases ({t} s)");
            Assert.True(unchecked(shown.KickOnsets - last.KickOnsets) <= 1, $"Never jumps ({t} s)");
            if (t < IdleFrom)
            {
                Assert.Equal(read, shown); // Live: unchanged
            }
            else if (!live)
            {
                Assert.Equal(5u, shown.KickOnsets); // Idle: the five kicks that started before, no more
                Assert.True(Math.Abs(shown.KickTime - (2 - 0.013)) <= 1e-9);
                Assert.All(shown.KickRing, onset => Assert.True(onset.Time < IdleFrom, $"{t} s"));
                Assert.True(Math.Abs(shown.Pad - 0.8f * (float)Math.Max(0, 1 - (t - IdleFrom) / SoundTypeIdleGate.Ramp)) <= 1e-4);
                var before = new SoundOnsetRing();
                var filled = 0;
                foreach (var onset in read.KickRing)
                {
                    if (onset.Time < IdleFrom) before[filled++] = onset;
                }
                Assert.True(Math.Abs(shown.Kick - SoundTypeShapes.Normal.Kick.Envelope(before, t)) <= 1e-6);
                if (t < 2.2) Assert.True(shown.Kick > 0.2, "The hit before going idle finishes its flare");
            }
            else
            {
                // The three kicks that started while idle never count.
                Assert.Equal(unchecked(read.KickOnsets - 3), shown.KickOnsets);
                Assert.All(shown.KickRing, onset => Assert.True(onset.Time < IdleFrom || onset.Time >= LiveFrom - SoundTypeIdleGate.Lead));
                Assert.True(Math.Abs(shown.Pad - 0.8f * (float)Math.Min(1, (t - LiveFrom) / SoundTypeIdleGate.Ramp)) <= 1e-4);
                if (!returned)
                {
                    returned = true;
                    Assert.True(Math.Abs(shown.KickRing[0].Time - (3 - 0.013)) <= 1e-9, "The kick that brought the music back shows");
                    Assert.True(shown.Kick > 0.3);
                }
            }
            last = shown;
        }
        Assert.True(returned);
        // A new source restarts the counters, and the gate follows.
        Assert.Equal(0u, gate.Frame(new SoundTypeFeatures(), true, 600).KickOnsets);
    }

    // Ported from SoundTypeDetectorTests.swift: testTheIdleGateIsTheSameAtEveryFrameRate
    // The idle fade and the onsets it hides depend on time alone: 30 and 120 fps show the same sound types at the
    // instants they share.
    [Fact]
    public void TheIdleGateIsTheSameAtEveryFrameRate()
    {
        var history = SteadyKicks();
        List<SoundTypeFeatures> Run(double fps)
        {
            var gate = new SoundTypeIdleGate();
            var frames = new List<SoundTypeFeatures>();
            for (var frame = 0; frame <= (int)(4.5 * fps); frame++)
            {
                var t = frame / fps;
                frames.Add(gate.Frame(history.Read(t), t < IdleFrom || t >= LiveFrom, 500 + t));
            }
            return frames;
        }
        List<SoundTypeFeatures> slow = Run(30), fast = Run(120);
        for (var k = 0; k < slow.Count && 4 * k < fast.Count; k++)
        {
            SoundTypeFeatures read = slow[k], other = fast[4 * k];
            var at = $"{k / 30.0} s";
            Assert.True(Math.Abs(read.Pad - other.Pad) <= 1e-5, at);
            Assert.True(Math.Abs(read.Kick - other.Kick) <= 1e-6, at);
            Assert.Equal(other.KickOnsets, read.KickOnsets);
            Assert.Equal(other.KickRing, read.KickRing);
        }
    }
}
