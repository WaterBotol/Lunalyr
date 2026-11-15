using System.Buffers.Binary;
using System.Diagnostics;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Visualisers;
using Lunalyr.Core.Visualisers.Capture;
using Lunalyr.Platform.Audio;
using Lunalyr.Render;
using Lunalyr.Render.Pacing;
using Lunalyr.Render.Visualisers;
using Lunalyr.Shell.Settings;
using RenderPause = Lunalyr.Render.Pacing.PauseReason;

namespace Lunalyr.Shell.Visualisers;

/// <summary>
/// System audio in the app: the analysis the visualiser layers read, the controller deciding when capture runs, and
/// what it captures from. Off by default until the user turns on "System audio", as on the Mac. Under <c>--demo</c>
/// nothing is ever captured: a built-in synthetic signal feeds the analysis instead, whenever a visualiser shows.
/// </summary>
public sealed class SystemAudioService : IDisposable
{
    private readonly VisualizerAudio _audio;
    private readonly SystemAudioController _controller;
    private readonly Environment _environment;
    private readonly CategoryLogger _log;

    /// <param name="settings">The app's settings now.</param>
    /// <param name="player">The followed player: whether it plays, and its name.</param>
    /// <param name="demo">Use the built-in synthetic signal and never capture.</param>
    public SystemAudioService(Func<AppSettings> settings, Func<(bool Playing, string? Name)> player, bool demo, Logger? logger = null)
    {
        _log = (logger ?? Logger.Null).App;
        _environment = new Environment(settings, player, demo, logger ?? Logger.Null);
        _audio = new VisualizerAudio(outputLatency: () => _environment.OutputLatency);
        _controller = new SystemAudioController(_environment, _audio);
        _controller.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Demo = demo;
        VisualiserServices.UseAudio(this, new AnalysedSound(_audio));
    }

    /// <summary>Raised (on any thread) when the state, the status or the levels change.</summary>
    public event EventHandler? Changed;

    public bool Demo { get; }

    public AudioCaptureState State => _controller.State;

    public string Status => _controller.Status;

    public AudioLevels Levels => _controller.Levels;

    public int Packets => _controller.Packets;

    public bool OffersReconnect => _controller.OffersReconnect;

    public VisualizerAudio Audio => _audio;

    /// <summary>Starts following the settings.</summary>
    public void Start() => _controller.Reconcile();

    /// <summary>The settings changed. Turning System audio on is the user's start: it connects at once.</summary>
    public void SettingsChanged(AppSettings old, AppSettings now)
    {
        if (!old.AudioReactiveEnabled && now.AudioReactiveEnabled) _controller.UserStarted();
        else _controller.Reconcile();
    }

    /// <summary>The wallpaper's surfaces or power saving changed (the render engine's status).</summary>
    public void RenderStatusChanged(IReadOnlyList<SurfaceStatus> surfaces, PowerConstraint? power)
    {
        var wasAsleep = !_environment.DisplaysAwake();
        _environment.Update(surfaces, power);
        VisualiserServices.SetPower(power);
        if (wasAsleep && _environment.DisplaysAwake()) _controller.Woke();
        else _controller.Reconcile();
    }

    public void Reconnect() => _controller.Reconnect();

    public string Diagnostics() => _controller.Diagnostics();

    public void Dispose()
    {
        _controller.Dispose();
        _environment.Dispose();
        VisualiserServices.UseAudio(null, null);
        _log.Debug("System audio: stopped.");
    }

    /// <summary>The app as the controller sees it.</summary>
    private sealed class Environment(Func<AppSettings> settings, Func<(bool Playing, string? Name)> player, bool demo, Logger logger)
        : ISystemAudioEnvironment, IDisposable
    {
        private readonly OutputActivityMeter? _meter = demo ? null : new OutputActivityMeter(logger);
        private volatile IReadOnlyList<SurfaceStatus> _surfaces = [];
        private PowerConstraint? _power; // replaced whole; a stale read for one tick is harmless
        private volatile ICaptureSession? _session;

        public double OutputLatency => _session?.OutputLatency ?? 0;

        public void Update(IReadOnlyList<SurfaceStatus> surfaces, PowerConstraint? power)
        {
            _surfaces = surfaces;
            _power = power;
        }

        public double Now() => AudioAnalyzer.Now();

        public SystemAudioPreferences Preferences()
        {
            var s = settings();
            var chosen = s.BackgroundMode == "visualizer";
            var power = _power;
            var shown = Wallpaper.WallpaperPower.EffectiveMode("visualizer", power);
            var powerPause = AudioCaptureGate.PowerPause(chosen, shown == "visualizer", Wallpaper.WallpaperPower.Title(shown),
                power?.Kind == PowerConstraintKind.LowPowerVisuals, power?.Kind == PowerConstraintKind.EnergySaver,
                power?.Kind == PowerConstraintKind.Battery);
            // The demo's signal stands in for System audio whenever a visualiser shows; nothing is captured.
            return new SystemAudioPreferences(s.AudioReactiveEnabled || demo, chosen, powerPause, s.WallpaperPaused);
        }

        public IReadOnlyCollection<SurfaceDrawing> Surfaces()
        {
            var surfaces = _surfaces;
            var states = new SurfaceDrawing[surfaces.Count];
            for (var i = 0; i < states.Length; i++)
                states[i] = surfaces[i].State switch
                {
                    SurfaceState.Drawing => SurfaceDrawing.Drawing,
                    SurfaceState.Frozen => SurfaceDrawing.Frozen,
                    _ => SurfaceDrawing.Hidden,
                };
            return states;
        }

        public bool DisplaysAwake()
        {
            var surfaces = _surfaces;
            return surfaces.Count == 0 || surfaces.Any(s => s.Reason is not (RenderPause.DisplayOff or RenderPause.Locked
                or RenderPause.ScreenSaver or RenderPause.SessionInactive));
        }

        private double _outputLastActive = double.NegativeInfinity;

        public AudioPlayback Playback()
        {
            var (playing, name) = player();
            if (demo) return new AudioPlayback(playing, name, playing, playing);
            var now = Now();
            var active = _meter?.IsActive();
            if (active != false) _outputLastActive = now;
            return AudioPlayback.Observed(playing, name, active, now - _outputLastActive);
        }

        public ICaptureSession Open(CaptureFeed feed, ICaptureSessionEvents events)
        {
            var session = demo ? new DemoSignalSession(feed) : WasapiLoopback.Open(feed, events, logger);
            _session = session;
            return new Tracked(session, this);
        }

        public IDisposable Schedule(double seconds, Action action)
        {
            var timer = new Timer(_ => action(), null, TimeSpan.FromSeconds(Math.Max(0, seconds)), Timeout.InfiniteTimeSpan);
            return timer;
        }

        public void Dispose() => _meter?.Dispose();

        /// <summary>Forgets the session's latency when it closes.</summary>
        private sealed class Tracked(ICaptureSession inner, Environment owner) : ICaptureSession
        {
            public string MethodLabel => inner.MethodLabel;
            public double? OutputLatency => inner.OutputLatency;

            public void Dispose()
            {
                if (ReferenceEquals(owner._session, inner)) owner._session = null;
                inner.Dispose();
            }
        }
    }

    /// <summary>The demo's signal, fed through the same path as capture, in 10 ms blocks of real time.</summary>
    private sealed class DemoSignalSession : ICaptureSession
    {
        private const int Rate = 48_000, Block = 480;
        private static readonly PcmFormat Format = new(PcmEncoding.Float32, 1, Rate, 4);
        private readonly CaptureFeed _feed;
        private readonly SyntheticSignal _signal = new(Rate);
        private readonly Thread _thread;
        private volatile bool _stopping;

        public DemoSignalSession(CaptureFeed feed)
        {
            _feed = feed;
            _thread = new Thread(Run) { IsBackground = true, Name = "Lunalyr demo signal" };
            _thread.Start();
        }

        public string MethodLabel => "Demo signal (nothing is captured)";

        public double? OutputLatency => null;

        private void Run()
        {
            var samples = new float[Block];
            var bytes = new byte[Block * 4];
            var next = Stopwatch.GetTimestamp();
            while (!_stopping)
            {
                _signal.Next(samples);
                for (var i = 0; i < Block; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), samples[i]);
                var now = AudioAnalyzer.Now();
                _feed.Write(bytes, Block, Format, now, now);
                next += Stopwatch.Frequency / 100;
                var wait = (next - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                if (wait > 0) Thread.Sleep((int)wait);
                else if (wait < -200) next = Stopwatch.GetTimestamp(); // fell behind (a pause): carry on from now
            }
        }

        public void Dispose()
        {
            _stopping = true;
            if (Thread.CurrentThread != _thread) _thread.Join(1000);
        }
    }
}
