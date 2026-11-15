using Lunalyr.Core.Cards;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Core.Tests.Cards;

/// <summary>The card's sound mix: loudness, sliders, the limiter, fades, loops and ducking (samples only, never played).</summary>
public class LyricCardMixTests
{
    // Ported from LyricCardMixTests.swift: testLoudnessOfSinesAtKnownLevels
    [Fact]
    public void LoudnessOfSinesAtKnownLevels()
    {
        // BS.1770: a 997 Hz sine at 0 dBFS in both channels reads 0 LUFS.
        foreach (var amplitude in new[] { 1.0, 0.5, 0.1, 0.01 })
        {
            var measured = LyricCardMix.Loudness(CardProbe.StereoSine(997, amplitude, 2));
            Assert.NotNull(measured);
            Assert.InRange(measured!.Value, 20 * Math.Log10(amplitude) - 0.15, 20 * Math.Log10(amplitude) + 0.15);
        }
        // The K-weighting's shelf lifts high tones by about 4 dB.
        var high = LyricCardMix.Loudness(CardProbe.StereoSine(8000, 0.1, 2));
        Assert.InRange(high!.Value, -20 + 3.5 - 0.6, -20 + 3.5 + 0.6);
        Assert.Null(LyricCardMix.Loudness(new float[96000]));
    }

    // Ported from LyricCardMixTests.swift: testGatingIgnoresSilenceAroundTheSound
    [Fact]
    public void GatingIgnoresSilenceAroundTheSound()
    {
        var samples = new float[96000 * 2].Concat(CardProbe.StereoSine(997, 0.1, 2)).Concat(new float[96000 * 2]).ToArray();
        var measured = LyricCardMix.Loudness(samples);
        // Ungated, the average over all 6 s would be -24.8 LUFS; the gates keep only the blocks with sound.
        Assert.InRange(measured!.Value, -21, -19);
    }

    // Ported from LyricCardMixTests.swift: testMatchingBringsEveryLevelToTheReference
    [Fact]
    public void MatchingBringsEveryLevelToTheReference()
    {
        foreach (var amplitude in new[] { 0.9, 0.2, 0.02 })
        {
            var samples = CardProbe.StereoSine(500, amplitude, 1.5);
            var gain = LyricCardMix.Linear(LyricCardMix.MatchingGain(samples));
            for (var i = 0; i < samples.Length; i++) samples[i] *= gain;
            Assert.InRange(LyricCardMix.Loudness(samples)!.Value, LyricCardMix.ReferenceLoudness - 0.05, LyricCardMix.ReferenceLoudness + 0.05);
        }
        Assert.Equal(0, LyricCardMix.MatchingGain(new float[9600]));
    }

    // Ported from LyricCardMixTests.swift: testSliderPositionsMapToDecibels
    [Fact]
    public void SliderPositionsMapToDecibels()
    {
        Assert.Equal(double.NegativeInfinity, LyricCardMix.Decibels(0));
        Assert.Equal(0, LyricCardMix.Decibels(1), 9);
        Assert.InRange(LyricCardMix.Decibels(0.5), -12.05, -12.03);
        Assert.InRange(LyricCardMix.Decibels(0.25), -24.09, -24.07);
        Assert.Equal(6, LyricCardMix.Decibels(1.25), 9);
        Assert.Equal(12, LyricCardMix.Decibels(1.5), 9);
        Assert.Equal(12, LyricCardMix.Decibels(3), 9);
        var last = double.NegativeInfinity;
        for (var step = 1; step <= 150; step++)
        {
            var db = LyricCardMix.Decibels(step / 100.0);
            Assert.True(db > last);
            last = db;
        }
        Assert.Equal("Off", LyricCardMix.Label(0));
        Assert.Equal("0 dB", LyricCardMix.Label(1));
        Assert.Equal("−12 dB", LyricCardMix.Label(0.5));
        Assert.Equal("+12 dB", LyricCardMix.Label(1.5));
    }

    // Ported from LyricCardMixTests.swift: testTheLimiterNeverExceedsTheCeiling
    [Fact]
    public void TheLimiterNeverExceedsTheCeiling()
    {
        var random = new Random(7);
        var samples = Enumerable.Range(0, 48000 * 2 * 2).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToList();
        // Hot bursts and single-sample spikes.
        for (var i = 0; i < samples.Count; i += 2)
        {
            if (i / 4800 % 3 != 0) continue;
            samples[i] *= 4;
            samples[i + 1] *= 3;
        }
        for (var i = 101; i < samples.Count; i += 9973) samples[i] = 9;
        samples.AddRange(CardProbe.StereoSine(100, 6, 0.5));
        var array = samples.ToArray();
        LyricCardMix.Limit(array);
        var ceiling = LyricCardMix.Ceiling;
        Assert.True(array.Max(Math.Abs) <= ceiling);
        Assert.Equal(Math.Pow(10, -1.0 / 20), ceiling, 6);
    }

    // Ported from LyricCardMixTests.swift: testTheLimiterLeavesQuietSoundAlone
    [Fact]
    public void TheLimiterLeavesQuietSoundAlone()
    {
        var quiet = CardProbe.StereoSine(440, 0.5, 0.5);
        var limited = (float[])quiet.Clone();
        LyricCardMix.Limit(limited);
        Assert.Equal(quiet, limited);
    }

    // Ported from LyricCardMixTests.swift: testFadeOutReachesSilence
    [Fact]
    public void FadeOutReachesSilence()
    {
        var samples = Enumerable.Repeat(1f, 48000 * 2).ToArray();
        LyricCardMix.FadeOut(samples, 48000, 0.3);
        Assert.Equal(1, samples[0]);
        Assert.InRange(samples[(48000 - 14400) * 2], 0.99, 1.01);
        Assert.InRange(samples[(48000 - 7200) * 2], 0.49, 0.51);
        Assert.Equal(0, samples[47999 * 2]);
    }

    // Ported from LyricCardMixTests.swift: testAFadeShorterThanAskedStartsFromFullLevel
    [Fact]
    public void AFadeShorterThanAskedStartsFromFullLevel()
    {
        // Sound that stops 100 frames in: no jump from full level to a third of it.
        var samples = Enumerable.Repeat(1f, 1000 * 2).ToArray();
        LyricCardMix.FadeOut(samples, 100, 0.3);
        Assert.InRange(samples[0], 0.98, 1.02);
        Assert.InRange(samples[50 * 2], 0.48, 0.52);
        Assert.Equal(0, samples[99 * 2]);
        Assert.Equal(0, samples[500 * 2]);
        var none = Enumerable.Repeat(1f, 20).ToArray();
        LyricCardMix.FadeOut(none, 0, 0.3);
        Assert.Equal(new float[20], none);
    }

    // Ported from LyricCardMixTests.swift: testLoopsRepeatAndCrossfadeWithoutADip
    [Fact]
    public void LoopsRepeatAndCrossfadeWithoutADip()
    {
        var source = Enumerable.Repeat(1f, 100 * 2).ToArray();
        var looped = LyricCardMix.Loop(source, 250, 10);
        // Seams at 90 and 180; equal-gain crossfades keep a constant level.
        Assert.Equal(500, looped.Length);
        foreach (var value in looped) Assert.InRange(value, 1 - 1e-5, 1 + 1e-5);
        var ramp = new float[200];
        for (var i = 0; i < 100; i++) ramp[2 * i] = ramp[2 * i + 1] = i;
        var plain = LyricCardMix.Loop(ramp, 250);
        Assert.Equal(0, plain[2 * 100]);
        Assert.Equal(49, plain[2 * 249]);
        var faded = LyricCardMix.Loop(ramp, 250, 10);
        Assert.InRange(faded[2 * 95], 95 * 0.5 + 5 * 0.5 - 1e-4, 95 * 0.5 + 5 * 0.5 + 1e-4); // halfway through the first seam
        Assert.Equal(10, faded[2 * 100]);                                                     // the second pass, past the seam
        Assert.Equal(ramp.Take(80).ToArray(), LyricCardMix.Loop(ramp, 40));
    }

    // Ported from LyricCardMixTests.swift: testDuckingFollowsTheBackgroundWithItsAttackAndRelease
    [Fact]
    public void DuckingFollowsTheBackgroundWithItsAttackAndRelease()
    {
        var trigger = new float[48000 * 3 * 2];
        CardProbe.StereoSine(300, 0.3, 1).CopyTo(trigger, 48000 * 2);
        var gains = LyricCardMix.DuckGains(trigger, 48000 * 3);
        double Db(double seconds) => CardProbe.Db(gains[(int)(seconds * 48000)]);
        Assert.InRange(Db(0.9), -0.01, 0.01);
        Assert.True(Db(1.2) < -9);              // most of the way down within ~3 attack constants
        Assert.InRange(Db(1.9), -12.2, -11.8);
        Assert.True(Db(2.1) < -6);              // release is slower than attack
        Assert.True(Db(2.99) > -1);
    }

    // Ported from LyricCardMixTests.swift: testTheSongIsDuckedOnlyWhileTheBackgroundSounds
    [Fact]
    public async Task TheSongIsDuckedOnlyWhileTheBackgroundSounds()
    {
        var song = CardProbe.StereoSine(600, 0.2, 3);
        var background = new float[48000 * 3 * 2];
        CardProbe.StereoSine(2000, 0.2, 1).CopyTo(background, 48000 * 2);
        SoundLayer[] layers =
        [
            new(1, (_, _) => Task.FromResult<RenderedSound?>(new(song)), SoundRole.Song),
            new(1, (_, _) => Task.FromResult<RenderedSound?>(new(background)), SoundRole.Background),
        ];
        var ducked = await LyricCardSound.MixdownAsync(layers, 3, duck: true, cancellationToken: TestContext.Current.CancellationToken);
        var mono = CardProbe.Left(ducked!.Samples!);
        var before = CardProbe.Db(CardProbe.Amplitude(600, mono, 24000, 43200));
        var during = CardProbe.Db(CardProbe.Amplitude(600, mono, 72000, 91200));
        Assert.InRange(during - before, -13, -11);
        var straight = await LyricCardSound.MixdownAsync(layers, 3, duck: false, cancellationToken: TestContext.Current.CancellationToken);
        var plain = CardProbe.Left(straight!.Samples!);
        Assert.InRange(CardProbe.Db(CardProbe.Amplitude(600, plain, 72000, 91200)), before - 0.2, before + 0.2);
    }

    // Ported from LyricCardMixTests.swift: testEveryLayerOffLeavesNoSound
    [Fact]
    public async Task EveryLayerOffLeavesNoSound()
    {
        SoundLayer[] layers = [new(0, (_, _) => Task.FromResult<RenderedSound?>(new(Enumerable.Repeat(0.5f, 9600).ToArray())))];
        var mixed = await LyricCardSound.MixdownAsync(layers, 0.1, duck: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(mixed);
        Assert.True(mixed!.IsSilent);
    }

    // Ported from LyricCardMixTests.swift: testOnlyLunalyrsOwnRecordingsAreDeleted
    [Fact]
    public void OnlyLunalyrsOwnRecordingsAreDeleted()
    {
        var folder = Directory.CreateTempSubdirectory("card-mix-").FullName;
        try
        {
            var recording = SongRecordings.NewPath(folder);
            var library = Path.Combine(folder, "My Song.m4a");
            var lookalike = Path.Combine(folder, "Music", "Lunalyr song x.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(lookalike)!);
            foreach (var path in new[] { recording, library, lookalike }) File.WriteAllBytes(path, [1, 2, 3]);
            Assert.True(SongRecordings.IsRecording(recording, folder));
            Assert.False(SongRecordings.IsRecording(library, folder));
            Assert.False(SongRecordings.IsRecording(lookalike, folder)); // same name, but not in the recordings folder
            SongRecordings.Discard(new SongAudioClip(library, 0, double.PositiveInfinity), folder);
            SongRecordings.Discard(new SongAudioClip(recording, 3, 9), folder);
            Assert.True(File.Exists(library)); // the song's own file is never deleted
            Assert.False(File.Exists(recording));
            var left = SongRecordings.NewPath(folder);
            File.WriteAllBytes(left, [1]);
            SongRecordings.DiscardLeftovers(folder);
            Assert.False(File.Exists(left));
            Assert.True(File.Exists(library));
            Assert.True(File.Exists(lookalike));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Ported from LyricCardMixTests.swift: testACancelledExportLeavesNothingBehind
    [Fact]
    public void ACancelledExportLeavesNothingBehind()
    {
        var folder = Directory.CreateTempSubdirectory("card-mix-").FullName;
        try
        {
            var own = Path.Combine(folder, "own");
            var chosen = Path.Combine(folder, "chosen");
            Directory.CreateDirectory(own);
            Directory.CreateDirectory(chosen);
            var earlier = Path.Combine(chosen, "Lunalyr Lyric Card.png");
            var made = Path.Combine(chosen, "Lunalyr Lyric Card 2.mp4");
            foreach (var path in new[] { earlier, made, Path.Combine(own, "Lunalyr Lyric Card.mp4") }) File.WriteAllBytes(path, [1]);
            LyricCardExportPlan.DiscardExport([(CardFormat.Mp4, made)], chosen, ownsDirectory: false);
            Assert.False(File.Exists(made));
            Assert.True(File.Exists(earlier)); // an earlier card in the user's folder stays
            LyricCardExportPlan.DiscardExport([], own, ownsDirectory: true);
            Assert.False(Directory.Exists(own));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Windows' own: the platform's decoder may hand over another rate or mono; the mix always gets 48 kHz stereo.
    [Fact]
    public void AnotherRateOrMonoBecomes48kHzStereo()
    {
        var mono = CardProbe.MonoSine(440, 0.5, 1, 44_100);
        var stereo = LyricCardMix.ToStereo48k(mono, 1, 44_100);
        Assert.Equal(48_000 * 2, stereo.Length);
        var left = CardProbe.Left(stereo);
        Assert.InRange(CardProbe.Amplitude(440, left, 4800, 43200), 0.49, 0.51);
        Assert.Equal(left, stereo.Where((_, i) => i % 2 == 1).ToArray());
    }
}
