using Lunalyr.Core.Cards;
using Lunalyr.Platform.Media;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests.Cards;

/// <summary>
/// The sound under a card's MP4, end to end: synthetic tones written as WAV, mixed, encoded as AAC under the video and
/// decoded back for measuring. Nothing is ever played, and no real sound is recorded.
/// </summary>
public class LyricCardSoundTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Ported from LyricCardMixTests.swift: testExportedLayersSitAtTheRequestedOffsets
    [Fact]
    public async Task ExportedLayersSitAtTheRequestedOffsets()
    {
        using var media = new CardMedia();
        var exporter = CardMedia.Exporter();
        var video = media.File("v.mp4");
        Assert.True(exporter.Video(CardMedia.Job(CardProbe.Content()), video, cancellationToken: Token));
        var loud = media.Tone("loud.wav", 600, 0.5, 3);
        // The Mac writes the quiet one as AAC in an M4A; here it is the sound of a small video, which is read the same way.
        var quiet = media.Video("quiet.mp4", 3);
        Assert.True(CardMedia.AddSound(quiet, media.Tone("quiet.wav", 900, 0.05, 3)));
        // The K-weighting treats 600 and 900 Hz slightly differently; account for it exactly.
        var songSource = LyricCardMix.Loudness(CardProbe.StereoSine(600, 0.5, 2))!.Value;
        var videoSource = LyricCardMix.Loudness(CardProbe.StereoSine(900, 0.05, 2))!.Value;
        var weighting = (songSource - CardProbe.Db(0.5)) - (videoSource - CardProbe.Db(0.05));
        foreach (var (songLevel, videoLevel) in new[] { (1.0, 1.0), (0.5, 1.0), (0.5, 1.5), (1.5, 1.0), (0.25, 1.0), (1.0, 0.5) })
        {
            var copy = media.File($"v-{songLevel}-{videoLevel}.mp4");
            File.Copy(video, copy);
            var song = LyricCardSound.SongLayer(exporter.Decoder, new SongAudioClip(loud, 0, 3), [(0, 2.5)], (float)songLevel);
            var other = LyricCardSound.FromFile(exporter.Decoder, quiet, (float)videoLevel, SoundRole.Background);
            var length = MediaProbe.Read(copy)!.Duration;
            Assert.True(await exporter.MuxAsync(copy, length, [song!, other!], cancellationToken: Token));
            var pcm = CardMedia.DecodeMono(copy);
            int from = 4800, to = pcm.Length - 24000;
            var songDb = CardProbe.Db(CardProbe.Amplitude(600, pcm, from, to));
            var videoDb = CardProbe.Db(CardProbe.Amplitude(900, pcm, from, to));
            var asked = LyricCardMix.Decibels(songLevel) - LyricCardMix.Decibels(videoLevel);
            Assert.InRange(songDb - videoDb, asked - weighting - 1, asked - weighting + 1);
            Assert.True(Math.Abs(weighting) < 1);
            Assert.True(pcm.Max(Math.Abs) <= 0.95);
        }
    }

    // Ported from LyricCardMixTests.swift: testABackgroundsShortSoundTrackRepeatsWithItsPicture
    [Fact]
    public async Task ABackgroundsShortSoundTrackRepeatsWithItsPicture()
    {
        using var media = new CardMedia();
        var background = media.Video("background.mp4", 2.5);
        Assert.True(CardMedia.AddSound(background, media.Tone("tone.wav", 500, 0.4, 1.5), 1.5));
        var length = MediaProbe.Read(background)!.Duration;
        Assert.InRange(length, 2.4, 2.6);

        var layer = LyricCardSound.FromFile(AudioFileDecoder.Instance, background, role: SoundRole.Background);
        var rendered = await layer!.Render(5, Token);
        var left = CardProbe.Left(rendered!.Samples);
        double Rms(double from, double to) => CardProbe.RmsDb(left, (int)(from * 48000), (int)(to * 48000));
        Assert.True(Rms(0.2, 1.3) > -20, "the first pass sounds");
        Assert.True(Rms(1.7, 2.3) < -60, "silent while the picture runs on past its sound");
        var second = length + 0.2;
        Assert.True(Rms(second, second + 1) > -20, "the second pass starts with the picture's");
    }

    // Ported from LyricCardMixTests.swift: testLoudLayersAreLimitedNotClipped
    [Fact]
    public async Task LoudLayersAreLimitedNotClipped()
    {
        using var media = new CardMedia();
        var exporter = CardMedia.Exporter();
        var video = media.File("hot.mp4");
        Assert.True(exporter.Video(CardMedia.Job(CardProbe.Content()), video, cancellationToken: Token));
        var layers = new List<SoundLayer>();
        foreach (var frequency in new[] { 200.0, 330, 470, 610 })
            layers.Add(LyricCardSound.FromFile(exporter.Decoder, media.Tone($"t{(int)frequency}.wav", frequency, 0.9, 2), 1.5f)!);
        Assert.True(await exporter.MuxAsync(video, MediaProbe.Read(video)!.Duration, layers, cancellationToken: Token));
        var pcm = CardMedia.DecodeMono(video);
        // -1 dBFS before encoding; AAC may overshoot a little.
        Assert.True(pcm.Max(Math.Abs) <= 0.97);
    }

    // Ported from LyricCardExportProgressTests.swift: testTheSoundIsMixedOffTheMainThread (adapted: Windows has no main
    // thread to keep free in a test, so this checks the strand is rendered off the caller's thread, the bar only moves
    // forward to 1 and one track is written)
    [Fact]
    public void TheSoundIsMixedOffTheCallersThread()
    {
        using var media = new CardMedia();
        var video = media.Video("v.mp4", 1);
        var renderedOn = new List<int>();
        var samples = CardProbe.StereoSine(440, 0.2, 1);
        var layer = new SoundLayer(1, (_, _) =>
        {
            lock (renderedOn) renderedOn.Add(Environment.CurrentManagedThreadId);
            return Task.FromResult<RenderedSound?>(new(samples));
        });
        var reported = new List<double>();
        // The card window's thread stands in as a thread of its own that waits for the mix.
        var ok = false;
        var caller = 0;
        var window = new Thread(() =>
        {
            caller = Environment.CurrentManagedThreadId;
            ok = CardMedia.Exporter().MuxAsync(video, 1, [layer], progress: value => { lock (reported) reported.Add(value); }, cancellationToken: Token)
                .GetAwaiter().GetResult();
        });
        window.Start();
        Assert.True(window.Join(TimeSpan.FromSeconds(60)));
        Assert.True(ok);
        Assert.Single(renderedOn);
        Assert.NotEqual(caller, renderedOn[0]);
        Assert.Equal(reported.Order(), reported);
        Assert.Equal(1, reported[^1]);
        Assert.True(MediaProbe.Read(video)!.IsAac);
    }

    // Ported from LyricCardExportProgressTests.swift: testCancellingTheMixLeavesTheVideoUntouched
    [Fact]
    public async Task CancellingTheMixLeavesTheVideoUntouched()
    {
        using var media = new CardMedia();
        var video = media.Video("v.mp4", 1);
        var before = File.ReadAllBytes(video);
        var started = new TaskCompletionSource();
        var layer = new SoundLayer(1, async (_, ct) =>
        {
            started.TrySetResult();
            // A slow strand: waits until the export is cancelled.
            while (!ct.IsCancellationRequested) await Task.Delay(5, CancellationToken.None);
            return new RenderedSound(new float[96_000]);
        });
        using var cancel = new CancellationTokenSource();
        var task = CardMedia.Exporter().MuxAsync(video, 1, [layer], cancellationToken: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        cancel.Cancel();
        bool ok;
        try { ok = await task; }
        catch (OperationCanceledException) { ok = false; }
        Assert.False(ok);
        Assert.Equal(before, File.ReadAllBytes(video));
        Assert.Equal(["v.mp4"], Directory.GetFiles(media.Folder).Select(Path.GetFileName)); // no half-written mix is left behind
    }

    // Ported from LyricCardExportProgressTests.swift: testTheExportSaysWhatItIsDoingAndOnlyMovesForward
    [Fact]
    public async Task TheExportSaysWhatItIsDoingAndOnlyMovesForward()
    {
        using var media = new CardMedia();
        var content = CardProbe.Content() with { ExtraSounds = [new CardSoundTrack(media.Tone("t.wav", 330, 0.3, 2))] };
        var phases = new List<ExportPhase>();
        var progress = new List<double>();
        var files = await CardMedia.Exporter().ExportAsync(CardMedia.Job(content), new HashSet<CardFormat>(CardFormats.All), media.File("out"),
            phase: p => { lock (phases) phases.Add(p); }, progress: v => { lock (progress) progress.Add(v); }, cancellationToken: Token);
        Assert.Equal([CardFormat.Mp4, CardFormat.Gif, CardFormat.Png], files.Select(f => f.Format));
        Assert.Equal(new ExportPhase[]
        {
            new ExportPhase.Drawing(CardFormat.Mp4), new ExportPhase.MixingSound(), new ExportPhase.Drawing(CardFormat.Gif),
            new ExportPhase.EncodingGif(), new ExportPhase.Drawing(CardFormat.Png),
        }, phases);
        Assert.Equal(progress.Order(), progress); // the bar never goes back
        Assert.Equal(1, progress[^1], 9);
        Assert.True(progress.Select(p => Math.Round(p * 20)).Distinct().Count() > 8); // the bar moves through the GIF too
    }

    // Ported from LyricCardBackgroundTests.swift: testSeveralAudioFilesAreMixedIntoTheExportedMP4
    [Fact]
    public async Task SeveralAudioFilesAreMixedIntoTheExportedMp4()
    {
        using var media = new CardMedia();
        var content = CardProbe.SpacedContent() with
        {
            ExtraSounds = [new(media.Tone("a.wav", 440, 0.3, 0.4)), new(media.Tone("b.wav", 440, 0.3, 0.7), 0.4)],
        };
        var files = await CardMedia.Exporter().ExportAsync(CardMedia.Job(content), new HashSet<CardFormat> { CardFormat.Mp4 }, media.File("many"),
            cancellationToken: Token);
        Assert.True(MediaProbe.Read(files[0].Path)!.IsAac);
    }

    // Ported from LyricCardBackgroundTests.swift: testTwoSoundsAtDifferentLevelsMixIntoOneTrack
    [Fact]
    public async Task TwoSoundsAtDifferentLevelsMixIntoOneTrack()
    {
        using var media = new CardMedia();
        var exporter = CardMedia.Exporter();
        var video = media.File("mixed.mp4");
        Assert.True(exporter.Video(CardMedia.Job(CardProbe.SpacedContent()), video, cancellationToken: Token));
        var first = LyricCardSound.FromFile(exporter.Decoder, media.Tone("a.wav", 440, 0.3, 0.5), 1.0f);
        var second = LyricCardSound.FromFile(exporter.Decoder, media.Tone("b.wav", 440, 0.3, 0.5), 0.3f);
        Assert.True(await exporter.MuxAsync(video, MediaProbe.Read(video)!.Duration, [first!, second!], cancellationToken: Token));
        Assert.True(MediaProbe.Read(video)!.IsAac);
    }

    // Ported from LyricCardBackgroundTests.swift: testAnyAudioFileCanBeLaidUnderTheMP4EvenWhenItNeedsEncoding
    [Fact]
    public async Task AnyAudioFileCanBeLaidUnderTheMp4EvenWhenItNeedsEncoding()
    {
        using var media = new CardMedia();
        var wav = media.Tone("tone.wav", 330, 0.3, 0.5);
        var content = CardProbe.SpacedContent() with { SoundFile = wav };
        Assert.Equal(wav, content.SoundSource);
        var notes = new List<string>();
        var files = await CardMedia.Exporter().ExportAsync(CardMedia.Job(content), new HashSet<CardFormat> { CardFormat.Mp4 }, media.File("wavout"),
            notes: notes.Add, cancellationToken: Token);
        Assert.Empty(notes);
        var info = MediaProbe.Read(files[0].Path)!;
        Assert.True(info.IsAac);
        Assert.InRange(info.AudioSeconds, info.VideoSeconds - 0.4, info.VideoSeconds + 0.4);
    }

    // Ported from LyricCardBackgroundTests.swift: testAFileWithNoSoundIsReportedNotSilentlyIgnored
    [Fact]
    public async Task AFileWithNoSoundIsReportedNotSilentlyIgnored()
    {
        using var media = new CardMedia();
        var content = CardProbe.SpacedContent() with { SoundFile = media.File("missing.m4a") };
        var notes = new List<string>();
        await CardMedia.Exporter().ExportAsync(CardMedia.Job(content), new HashSet<CardFormat> { CardFormat.Mp4 }, media.File("none"),
            notes: notes.Add, cancellationToken: Token);
        Assert.Single(notes);
        Assert.Contains("missing.m4a", notes[0], StringComparison.Ordinal);
    }

    // Ported from LyricCardBackgroundTests.swift: testTheSongsSoundIsCutToTheStretchesTheCardPlays (the MP4's half)
    [Fact]
    public async Task TheSongsSoundIsCutToTheStretchesTheCardPlays()
    {
        using var media = new CardMedia();
        var card = CardProbe.SpacedContent();
        var windows = LyricCardSound.SongWindows(card);
        // A recording of the song that begins at 9.0 s and runs 5 s.
        var clip = new SongAudioClip(media.Tone("song.wav", 440, 0.3, 5), 9.0, 14.0);
        var exporter = CardMedia.Exporter();
        var video = media.File("song.mp4");
        Assert.True(exporter.Video(CardMedia.Job(card), video, cancellationToken: Token));
        var layer = LyricCardSound.SongLayer(exporter.Decoder, clip, windows);
        Assert.True(await exporter.MuxAsync(video, MediaProbe.Read(video)!.Duration, [layer!], cancellationToken: Token));
        var info = MediaProbe.Read(video)!;
        Assert.True(info.IsAac);
        Assert.InRange(info.AudioSeconds, windows[0].Duration - 0.3, windows[0].Duration + 0.3);
    }

    // Ported from LyricCardLengthTests.swift: testAnExtraSoundLoopsToTheLengthAndFadesAtTheEnd
    [Fact]
    public async Task AnExtraSoundLoopsToTheLengthAndFadesAtTheEnd()
    {
        using var media = new CardMedia();
        var content = CardProbe.Content() with { ExtraSounds = [new(media.Tone("short.wav", 500, 0.3, 0.4))] };
        var files = await CardMedia.Exporter().ExportAsync(CardMedia.Job(content), new HashSet<CardFormat> { CardFormat.Mp4 }, media.File("extra"),
            cancellationToken: Token);
        var file = files[0].Path;
        var length = MediaProbe.Read(file)!.Duration;
        var lyrics = LyricCardTiming.PageClips(content).Sum(c => c.Duration);
        Assert.InRange(length, lyrics - 0.05, lyrics + 0.05);
        var pcm = CardMedia.DecodeMono(file);
        var end = Math.Min(pcm.Length, (int)(length * 48000));
        // Sound all the way through the repeats (no gaps), then a fade.
        var levels = new List<double>();
        for (var at = 4800; at < end - 19200; at += 4800) levels.Add(CardProbe.RmsDb(pcm, at, at + 4800));
        Assert.True(levels.Count > 10);
        Assert.True(levels.Max() - levels.Min() < 2, string.Join(", ", levels.Select(l => l.ToString("0.0"))));
        Assert.True(CardProbe.RmsDb(pcm, end - 1440, end) < levels.Min() - 20);
    }

    // Windows' own: the song's sound recorded through the capture path (synthetic sound only: a fake capture plays a
    // tone in place of the PC's output), written as the recording, laid under the card and read back.
    [Fact]
    public async Task ARecordedSongGoesUnderTheCard()
    {
        using var media = new CardMedia();
        var card = CardProbe.SpacedContent();
        var range = LyricCardSound.SongRange(card)!.Value;
        var clock = new FakeSongClock(range.From - 1.1);
        var source = new SyntheticCapture(clock, 523);
        var updates = new List<RecordingUpdate>();
        var recorder = new SongRecorder(source);
        var recording = recorder.RecordAsync(range.From, range.To, clock.Read, u => { lock (updates) updates.Add(u); }, media.Folder, Token);
        var clip = await recording.WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.True(SongRecordings.IsRecording(clip.Path, media.Folder));
        Assert.True(clip.Covers(range.From, range.To));
        Assert.Contains(updates, u => u is RecordingUpdate.Recording);
        var exporter = CardMedia.Exporter();
        var files = await exporter.ExportAsync(CardMedia.Job(card) with { Content = card with { SongAudio = clip } },
            new HashSet<CardFormat> { CardFormat.Mp4 }, media.File("song"), cancellationToken: Token);
        var pcm = CardMedia.DecodeMono(files[0].Path);
        Assert.True(CardProbe.Amplitude(523, pcm, 4800, 48000) > 0.05);
        SongRecordings.Discard(clip, media.Folder);
        Assert.False(File.Exists(clip.Path));
    }

    // Windows' own: under --demo (or any run that must keep away from the PC's sound) nothing is ever captured.
    [Fact]
    public async Task ARefusingCaptureNeverRecords()
    {
        using var media = new CardMedia();
        var clock = new FakeSongClock(9.5);
        var recorder = new SongRecorder(new RefusingSongCaptureSource("Recording the song is off for this run (--demo)."));
        var error = await Assert.ThrowsAsync<SongRecordingException>(() =>
            recorder.RecordAsync(10, 11, clock.Read, _ => { }, media.Folder, Token));
        Assert.Contains("--demo", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(media.Folder));
    }

    /// <summary>A song position that moves on in real time while "playing".</summary>
    private sealed class FakeSongClock(double start)
    {
        private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();

        public (double Seconds, bool Playing) Read() => (start + _watch.Elapsed.TotalSeconds, true);
    }

    /// <summary>A capture of a tone that follows the clock: what loopback would hear, made up in memory.</summary>
    private sealed class SyntheticCapture(FakeSongClock clock, double frequency) : ISongCaptureSource, ISongCapture
    {
        private double _from = double.NaN;
        private long _written;

        public double SampleRate => 48_000;

        public string? Failure => null;

        public ISongCapture Start()
        {
            _from = clock.Read().Seconds;
            return this;
        }

        public void Drain(List<float> stereo)
        {
            var due = (long)((clock.Read().Seconds - _from) * SampleRate);
            for (; _written < due; _written++)
            {
                var x = (float)(Math.Sin(_written * 2 * Math.PI * frequency / SampleRate) * 0.3);
                stereo.Add(x);
                stereo.Add(x);
            }
        }

        public void Dispose() { }
    }
}
