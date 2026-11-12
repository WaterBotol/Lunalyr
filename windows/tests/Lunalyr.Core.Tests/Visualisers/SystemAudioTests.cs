using System.Buffers.Binary;
using Lunalyr.Core.Visualisers;
using Lunalyr.Core.Visualisers.Capture;

namespace Lunalyr.Core.Tests.Visualisers;

/// <summary>
/// The capture feed and the System audio controller, with synthetic PCM and a fake environment: no device is opened and
/// nothing is captured or played.
/// </summary>
public class SystemAudioTests
{
    private static readonly PcmFormat Stereo = new(PcmEncoding.Float32, 2, 48_000, 8);

    private static byte[] Tone(int frames, PcmFormat format, double amplitude = 0.25, int start = 0)
    {
        var data = new byte[frames * format.BlockAlign];
        for (var f = 0; f < frames; f++)
        {
            var value = amplitude * Math.Sin(2 * Math.PI * 440 * (start + f) / format.SampleRate);
            for (var c = 0; c < format.Channels; c++)
            {
                var slot = data.AsSpan(f * format.BlockAlign + c * format.SampleBytes);
                var v = c == 1 ? -value : value;
                switch (format.Encoding)
                {
                    case PcmEncoding.Float32: BinaryPrimitives.WriteSingleLittleEndian(slot, (float)v); break;
                    case PcmEncoding.Float64: BinaryPrimitives.WriteDoubleLittleEndian(slot, v); break;
                    case PcmEncoding.Int16: BinaryPrimitives.WriteInt16LittleEndian(slot, (short)(v * 32_767)); break;
                    case PcmEncoding.Int24:
                        var i24 = (int)(v * 8_388_607);
                        slot[0] = (byte)i24; slot[1] = (byte)(i24 >> 8); slot[2] = (byte)(i24 >> 16);
                        break;
                    default: BinaryPrimitives.WriteInt32LittleEndian(slot, (int)(v * 2_147_483_647)); break;
                }
            }
        }
        return data;
    }

    // Adapted from AudioInputFormatTests.swift: testFloat32PlanarAndFloat64InterleavedAreRecognised,
    // testSignedIntegerPCMIsRecognised and testUnsupportedOrPaddedFormatsAreNotSilentlyDecodedAsFloat32 (Windows
    // describes formats with WAVEFORMATEX: interleaved only)
    [Fact]
    public void FormatsAreRecognisedAndNothingIsSilentlyMisread()
    {
        Assert.Equal(PcmEncoding.Float32, PcmFormat.From(true, 32, 32, 2, 8, 48_000)!.Value.Encoding);
        Assert.Equal(PcmEncoding.Float64, PcmFormat.From(true, 64, 64, 2, 16, 44_100)!.Value.Encoding);
        Assert.Equal(PcmEncoding.Int16, PcmFormat.From(false, 16, 16, 2, 4, 48_000)!.Value.Encoding);
        Assert.Equal(PcmEncoding.Int24, PcmFormat.From(false, 24, 24, 2, 6, 48_000)!.Value.Encoding);
        Assert.Equal(PcmEncoding.Int32, PcmFormat.From(false, 32, 24, 8, 32, 96_000)!.Value.Encoding);
        Assert.Null(PcmFormat.From(true, 16, 16, 2, 4, 48_000)); // half floats
        Assert.Null(PcmFormat.From(true, 32, 32, 2, 12, 48_000)); // padded float
        Assert.Null(PcmFormat.From(false, 8, 8, 2, 2, 48_000));
        Assert.Null(PcmFormat.From(true, 32, 32, 2, 8, 384_000));
        Assert.Null(PcmFormat.From(true, 32, 32, 0, 8, 48_000));
        Assert.Equal("48000 Hz · 2ch · 32bit float", Stereo.Label(2));
    }

    // Adapted from AudioPCMProcessorTests.swift: testVisualiserReceivesTheDrainedAudio, for every encoding
    [Theory]
    [InlineData(PcmEncoding.Float32, 4)]
    [InlineData(PcmEncoding.Float64, 8)]
    [InlineData(PcmEncoding.Int16, 2)]
    [InlineData(PcmEncoding.Int24, 3)]
    [InlineData(PcmEncoding.Int32, 4)]
    public void TheVisualiserReceivesTheCapturedAudio(PcmEncoding encoding, int width)
    {
        var format = new PcmFormat(encoding, 2, 48_000, width * 2);
        var audio = new VisualizerAudio(now: () => 10);
        var feed = new CaptureFeed(audio);
        for (var packet = 0; packet < 50; packet++)
            feed.Write(Tone(480, format, start: packet * 480), 480, format, 1 + (packet + 1) * 0.01, 1 + (packet + 1) * 0.01);
        Assert.Equal(24_000, audio.SampleCount);
        var report = feed.Read(2)!.Value;
        Assert.Equal(50, report.Packets);
        Assert.NotNull(report.AudibleAt);
        Assert.True(report.Levels.Rms > 0.3, $"rms {report.Levels.Rms}");
        Assert.True(audio.Latest(1.5).Level > 0, "The analyser heard the tone");
    }

    // Ported from AudioPCMProcessorTests.swift: testDigitalSilenceIsNotMistakenForAudio
    [Fact]
    public void DigitalSilenceIsNotMistakenForAudio()
    {
        var feed = new CaptureFeed(new VisualizerAudio(now: () => 0));
        feed.Write(new byte[480 * 8], 480, Stereo, 1, 1);
        feed.Write([], 480, Stereo, 1.01, 1.01, silent: true);
        var report = feed.Read(2)!.Value;
        Assert.Equal(2, report.Packets);
        Assert.Null(report.AudibleAt);
        Assert.Equal(0, report.Levels.Rms);
    }

    // Ported from AudioPCMProcessorTests.swift: testReportsToTheMainThreadAreThrottled
    [Fact]
    public void ReportsAreThrottled()
    {
        var feed = new CaptureFeed(new VisualizerAudio(now: () => 0));
        feed.Write(Tone(480, Stereo), 480, Stereo, 1, 1);
        Assert.NotNull(feed.Read(1));
        feed.Write(Tone(480, Stereo), 480, Stereo, 1.01, 1.01);
        Assert.Null(feed.Read(1.02)); // within 1/15 s
        Assert.Equal(2, feed.Read(1.07)!.Value.Packets);
        Assert.Null(feed.Read(5)); // nothing new
    }

    // Ported from AudioPCMProcessorTests.swift: testMonoAndUnsupportedInput
    [Fact]
    public void MonoAndUnsupportedInput()
    {
        var audio = new VisualizerAudio(now: () => 0);
        var feed = new CaptureFeed(audio);
        var mono = new PcmFormat(PcmEncoding.Float32, 1, 44_100, 4);
        feed.Write(Tone(441, mono), 441, mono, 1, 1);
        var report = feed.Read(1)!.Value;
        Assert.Equal("44100 Hz · 1ch · 32bit float", report.FormatLabel);
        feed.Write(new byte[64], 8, null, 1.1, 1.1);
        var problem = feed.Read(1.1)!.Value;
        Assert.Equal(CaptureFeed.UnsupportedMessage, problem.Problem);
        feed.Write(new byte[64], 8, null, 1.2, 1.2);
        Assert.Null(feed.Read(1.2)); // said once
    }

    // Windows' own: a surround mix is analysed from its front pair, and every piece reaches the analyser stamped with its
    // last sample's capture time, ending on the analyser's hop whatever the packet size.
    [Fact]
    public void PacketsOfAnySizeKeepTheAnalysersHop()
    {
        var surround = new PcmFormat(PcmEncoding.Float32, 6, 48_000, 24);
        var a = new VisualizerAudio(now: () => 0);
        var b = new VisualizerAudio(now: () => 0);
        var small = new CaptureFeed(a);
        var large = new CaptureFeed(b);
        var data = Tone(9_600, surround);
        for (var i = 0; i < 9_600; i += 160)
            small.Write(data.AsSpan(i * 24, 160 * 24), 160, surround, 1 + (i + 160) / 48_000.0, 1);
        large.Write(data, 9_600, surround, 1 + 9_600 / 48_000.0, 1);
        Assert.Equal(a.SampleCount, b.SampleCount);
        Assert.Equal(a.Latest(1.2).Level, b.Latest(1.2).Level, 4);
        Assert.Equal("48000 Hz · 2ch · 32bit float", large.Read(2)!.Value.FormatLabel);
    }

    // ---- The controller ----

    private sealed class FakeEnvironment : ISystemAudioEnvironment
    {
        private readonly List<(double At, Action Action, Cancel Token)> _scheduled = [];

        public double Time { get; set; } = 100;
        public SystemAudioPreferences Prefs { get; set; } = new(true, true);
        public List<SurfaceDrawing> SurfaceStates { get; } = [SurfaceDrawing.Drawing];
        public bool Awake { get; set; } = true;
        public AudioPlayback Playing { get; set; } = new(true, "Spotify", null, true);
        public int Opened { get; private set; }
        public string? FailOpen { get; set; }
        public FakeSession? Session { get; private set; }
        public ICaptureSessionEvents? Events { get; private set; }

        public double Now() => Time;
        public SystemAudioPreferences Preferences() => Prefs;
        public IReadOnlyCollection<SurfaceDrawing> Surfaces() => SurfaceStates;
        public bool DisplaysAwake() => Awake;
        public AudioPlayback Playback() => Playing;

        public ICaptureSession Open(CaptureFeed feed, ICaptureSessionEvents events)
        {
            Opened++;
            if (FailOpen is { } message) throw new InvalidOperationException(message);
            Events = events;
            Session = new FakeSession(feed);
            return Session;
        }

        public IDisposable Schedule(double seconds, Action action)
        {
            var token = new Cancel();
            _scheduled.Add((Time + seconds, action, token));
            return token;
        }

        /// <summary>Moves the clock on, running what falls due in order (and anything it schedules).</summary>
        public void Advance(double seconds, Action<double>? everyTick = null)
        {
            var end = Time + seconds;
            while (true)
            {
                var due = _scheduled.Where(s => s.At <= end && !s.Token.Cancelled).OrderBy(s => s.At).FirstOrDefault();
                if (due.Action is null) break;
                _scheduled.Remove(due);
                Time = Math.Max(Time, due.At);
                everyTick?.Invoke(Time);
                due.Action();
            }
            Time = end;
        }

        public sealed class Cancel : IDisposable
        {
            public bool Cancelled { get; private set; }
            public void Dispose() => Cancelled = true;
        }
    }

    private sealed class FakeSession(CaptureFeed feed) : ICaptureSession
    {
        private int _packet;
        public bool Disposed { get; private set; }
        public string MethodLabel => "System audio";
        public double? OutputLatency => 0.02;

        public void Deliver(double now, double amplitude = 0.25)
        {
            feed.Write(Tone(480, Stereo, amplitude, _packet * 480), 480, Stereo, now, now);
            _packet++;
        }

        public void Dispose() => Disposed = true;
    }

    private static (FakeEnvironment Env, SystemAudioController Controller) Make()
    {
        var env = new FakeEnvironment();
        var controller = new SystemAudioController(env, new VisualizerAudio(now: () => env.Time), work => work());
        return (env, controller);
    }

    // Adapted from AudioCaptureControllerTests.swift: testAUserStartConnectsAtOnceThenStandsByWhenTheGraceEnds
    [Fact]
    public void AUserStartConnectsAtOnceThenStandsByWhenTheGraceEnds()
    {
        var (env, controller) = Make();
        env.Playing = new AudioPlayback(false, null, null, false);
        controller.UserStarted();
        Assert.Equal(1, env.Opened);
        Assert.True(controller.Running);
        env.Advance(30);
        Assert.Equal(AudioCaptureStateKind.Connecting, controller.State.Kind);
        Assert.Contains("Listening", controller.Status); // nothing plays: waiting is not a fault
        env.Advance(31);
        Assert.Equal(AudioCaptureState.Standby, controller.State);
        Assert.True(env.Session!.Disposed);
    }

    // Adapted from AudioCaptureControllerTests.swift: testNothingPlayingStandsByAfterTheHangTimeAndPlaybackResumesIt
    [Fact]
    public void NothingPlayingStandsByAfterTheHangTimeAndPlaybackResumesIt()
    {
        var (env, controller) = Make();
        controller.Reconcile();
        Assert.True(controller.Running);
        env.Advance(1, now => env.Session?.Deliver(now));
        Assert.Equal(AudioCaptureState.Receiving, controller.State);
        Assert.Contains("no recording is saved", controller.Status);
        env.Playing = new AudioPlayback(false, null, null, false);
        env.Advance(9);
        Assert.True(controller.Running, "A gap between tracks keeps capture");
        env.Advance(2);
        Assert.Equal(AudioCaptureState.Standby, controller.State);
        Assert.False(controller.Running);
        env.Playing = new AudioPlayback(true, "Spotify", null, true);
        env.Advance(0.6);
        Assert.True(controller.Running);
        Assert.Equal(2, env.Opened);
    }

    // Adapted from AudioCaptureControllerTests.swift: testCoveringTheLastWallpaperWhileMusicPlaysPausesCaptureAfterTheHangTime
    [Fact]
    public void CoveringTheLastWallpaperPausesCaptureAfterTheHangTime()
    {
        var (env, controller) = Make();
        controller.Reconcile();
        env.SurfaceStates[0] = SurfaceDrawing.Hidden;
        env.Advance(9, now => env.Session?.Deliver(now));
        Assert.True(controller.Running);
        env.Advance(2);
        Assert.Equal(AudioCaptureState.Paused, controller.State);
        Assert.Equal("Paused while no visualiser is on screen.", controller.Status);
        env.SurfaceStates[0] = SurfaceDrawing.Drawing;
        env.Advance(0.6);
        Assert.True(controller.Running);
    }

    // Adapted from AudioCaptureControllerTests.swift: testPauseWallpaperSuspendsCaptureAtOnceAndResumingResumesIt
    [Fact]
    public void PauseWallpaperStopsCaptureAtOnceAndResumingResumesIt()
    {
        var (env, controller) = Make();
        controller.Reconcile();
        env.Prefs = env.Prefs with { WallpaperPaused = true };
        env.SurfaceStates[0] = SurfaceDrawing.Frozen;
        controller.Reconcile();
        Assert.Equal(AudioCaptureState.Paused, controller.State);
        Assert.False(controller.Running);
        env.Prefs = env.Prefs with { WallpaperPaused = false };
        env.SurfaceStates[0] = SurfaceDrawing.Drawing;
        controller.Reconcile();
        Assert.True(controller.Running);
    }

    // Adapted from AudioCaptureControllerTests.swift: testDisplaySleepReleasesCaptureAndWakingReconnects
    [Fact]
    public void DisplaySleepReleasesCaptureAndWakingReconnects()
    {
        var (env, controller) = Make();
        controller.Reconcile();
        env.Awake = false;
        controller.Reconcile();
        Assert.Equal("Paused while the displays sleep.", controller.Status);
        Assert.True(env.Session!.Disposed);
        env.Awake = true;
        controller.Woke();
        env.Advance(0.5);
        Assert.True(controller.Running);
    }

    // Adapted from AudioCaptureControllerTests.swift: testPowerLimitsKeepTheSwitchOnAndSayWhy
    [Fact]
    public void PowerLimitsKeepTheSwitchOnAndSayWhy()
    {
        var (env, controller) = Make();
        var reason = AudioCaptureGate.PowerPause(true, false, "Colour only", false, false, true)!;
        env.Prefs = env.Prefs with { PowerPause = reason };
        controller.UserStarted();
        Assert.Equal(AudioCaptureState.Paused, controller.State);
        Assert.Equal(reason, controller.Status);
        Assert.Equal(0, env.Opened);
        Assert.True(env.Prefs.Enabled);
    }

    // Adapted from AudioCaptureControllerTests.swift: testAFailedStartWaitsForTheUserOrTheNextPlayback
    [Fact]
    public void AFailedStartWaitsForTheUserOrTheNextPlayback()
    {
        var (env, controller) = Make();
        env.FailOpen = "The output device is gone.";
        controller.Reconcile();
        Assert.Equal(AudioCaptureState.Attention(AudioCaptureProblem.Failed), controller.State);
        Assert.Contains("The output device is gone.", controller.Status);
        Assert.True(controller.OffersReconnect);
        env.FailOpen = null;
        env.Advance(5);
        Assert.Equal(1, env.Opened); // no retry loop
        env.Playing = new AudioPlayback(false, null, null, false);
        env.Advance(1);
        env.Playing = new AudioPlayback(true, "Spotify", null, true);
        env.Advance(1);
        Assert.True(controller.Running, "Playback starting again retries");
        env.FailOpen = "Again.";
        env.Events!.Failed("The stream ended.");
        Assert.Contains("The stream ended.", controller.Status);
        env.FailOpen = null;
        controller.Reconnect();
        Assert.True(controller.Running);
    }

    // Adapted from AudioCaptureControllerTests.swift: testRouteAndFormatChangesRebuildTheSession
    [Fact]
    public void RouteChangesRebuildTheSession()
    {
        var (env, controller) = Make();
        controller.Reconcile();
        var first = env.Session!;
        env.Events!.RouteChanged("Audio output changed; reconnecting…");
        env.Advance(0.3);
        Assert.False(first.Disposed, "The route settles first");
        env.Advance(0.2);
        Assert.True(first.Disposed);
        Assert.NotSame(first, env.Session);
        Assert.Equal(2, env.Opened);
        Assert.Equal("Audio output changed; reconnecting…", controller.Status);
    }

    // Windows' own: a stream that stops while something plays needs attention; one that stops because nothing plays is quiet.
    [Fact]
    public void AStreamThatStopsWhilePlayingNeedsAttention()
    {
        var (env, controller) = Make();
        controller.Reconcile();
        env.Advance(1, now => env.Session?.Deliver(now));
        Assert.Equal(AudioCaptureState.Receiving, controller.State);
        env.Advance(2);
        Assert.Equal(AudioCaptureState.Attention(AudioCaptureProblem.Stalled), controller.State);
        Assert.True(controller.OffersReconnect);
        env.Playing = new AudioPlayback(false, null, null, false);
        env.Advance(1);
        Assert.Equal(AudioCaptureState.Quiet, controller.State);
        controller.Dispose();
    }

    // Windows' own: Windows can't say which app a sound comes from, so a playing player whose output has been silent for
    // a moment plays somewhere else; a short silent bar changes nothing.
    [Fact]
    public void PlaybackAsWindowsObservesIt()
    {
        var elsewhere = AudioPlayback.Observed(true, "Spotify", false, 5);
        Assert.True(elsewhere.PlayingElsewhere);
        Assert.False(elsewhere.OutputActive);
        var rest = AudioPlayback.Observed(true, "Spotify", false, 1);
        Assert.True(rest.PlayingHere);
        Assert.True(rest.OutputActive);
        var here = AudioPlayback.Observed(true, "Spotify", true, 0);
        Assert.True(here.PlayingHere);
        Assert.True(AudioPlayback.Observed(false, null, true, 0).OutputActive);
        Assert.False(AudioPlayback.Observed(false, null, false, 9).OutputActive);
        Assert.True(AudioPlayback.Observed(true, "Music", null, 0).OutputActive); // an unreadable meter counts as playing
    }

    // Windows' own: a player playing somewhere else (Spotify Connect, another output device) gets no packets through
    // loopback; that is never "needs attention", and capture stands by after the hang time.
    [Fact]
    public void APlayerPlayingElsewhereNeverNeedsAttention()
    {
        var (env, controller) = Make();
        env.Playing = AudioPlayback.Observed(true, "Spotify", false, 30);
        controller.Reconcile();
        // Nothing has played here since capture was armed: it stands by at once, saying why.
        Assert.Equal(AudioCaptureState.Standby, controller.State);
        Assert.Contains("nothing from Spotify is playing on this PC", controller.Status);
        Assert.Equal(0, env.Opened);
        // Switched on by the user, it connects for the start's grace and still never needs attention.
        controller.UserStarted();
        env.Advance(5);
        Assert.False(controller.State.NeedsAttention, controller.Status);
        Assert.Contains("Nothing from Spotify is playing on this PC", controller.Status);
    }

    // Windows' own: switching off stops capture and says so.
    [Fact]
    public void SwitchingOffStopsCapture()
    {
        var (env, controller) = Make();
        controller.Reconcile();
        env.Prefs = env.Prefs with { Enabled = false };
        controller.Reconcile();
        Assert.Equal(AudioCaptureState.Off, controller.State);
        Assert.Equal(SystemAudioController.OffMessage, controller.Status);
        Assert.True(env.Session!.Disposed);
        Assert.False(controller.OffersReconnect);
    }
}
