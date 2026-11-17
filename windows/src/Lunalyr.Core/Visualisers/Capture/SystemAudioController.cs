// "System audio": when capture runs, what it reports and how it recovers. Re-implemented for Windows from a
// description of the Mac's AudioReactiveController (part of which traces back to the Verso import, so it is not
// translated), in this structure:
//   * The user's switch is the only thing that turns it on or off; power, sleep, Pause wallpaper and covered or hidden
//     wallpapers pause it and it resumes by itself (AudioCaptureGate decides; the switch is never changed for them).
//   * Capture runs only while it is useful: a visualiser is the wallpaper and is drawing, and audio plays (with hang
//     times so a gap between tracks or a brief cover doesn't restart it). A user start connects at once.
//   * While running, a health check every half second turns what the stream delivers into the status sentence; a
//     stream that stops, or a start that never completes, is shown as needing attention with Reconnect.
//   * The default output device changing, or the device going away, reconnects after the route settles (0.45 s); a
//     failure waits for the user, a route change, a wake or playback starting again.
// Nothing here touches a device: the environment opens sessions (WASAPI loopback on Windows, a synthetic source under
// --demo, fakes in tests).

namespace Lunalyr.Core.Visualisers.Capture;

/// <summary>What the controller reads from the app now: the switch and what the wallpaper shows.</summary>
/// <param name="MusicVideoShowing">Music video's video plays on every wallpaper, so its fallback visualiser shows nowhere.</param>
public readonly record struct SystemAudioPreferences(bool Enabled, bool VisualiserChosen, string? PowerPause = null,
    bool WallpaperPaused = false, bool MusicVideoShowing = false);

/// <summary>A running capture. Disposing it stops it and releases the device.</summary>
public interface ICaptureSession : IDisposable
{
    /// <summary>"System audio", or "Demo signal", for the status sentence.</summary>
    string MethodLabel { get; }

    /// <summary>The output latency the device reports, in seconds, or null when it reports none.</summary>
    double? OutputLatency { get; }
}

/// <summary>Where a session tells the controller what happened to it. Any thread.</summary>
public interface ICaptureSessionEvents
{
    /// <summary>The default output changed, or the device went away: reconnect once the route settles.</summary>
    void RouteChanged(string reason);

    /// <summary>The stream stopped with an error.</summary>
    void Failed(string message);
}

/// <summary>The app around the controller. Every member may be called from any thread.</summary>
public interface ISystemAudioEnvironment
{
    /// <summary>Monotonic seconds.</summary>
    double Now();

    SystemAudioPreferences Preferences();

    /// <summary>Each surface that could show a visualiser, by its own frame clock's rule. Empty while unknown.</summary>
    IReadOnlyCollection<SurfaceDrawing> Surfaces();

    /// <summary>False while the displays sleep or the session is locked or switched away.</summary>
    bool DisplaysAwake();

    /// <summary>The followed player and whether anything plays on this PC.</summary>
    AudioPlayback Playback();

    /// <summary>Opens a session feeding <paramref name="feed"/>; throws when the device can't be opened.</summary>
    ICaptureSession Open(CaptureFeed feed, ICaptureSessionEvents events);

    /// <summary>Runs <paramref name="action"/> once after <paramref name="seconds"/>; disposing cancels it.</summary>
    IDisposable Schedule(double seconds, Action action);
}

/// <summary>System audio for the visualisers. Thread-safe; its event is raised outside its lock.</summary>
public sealed class SystemAudioController : IDisposable
{
    public const string OffMessage = "Choose a visualiser and switch on System audio to follow your music.";
    public const double TickInterval = 0.5;
    public const double StartupTimeout = 12;
    public const double RouteSettle = 0.45;

    private readonly ISystemAudioEnvironment _env;
    private readonly VisualizerAudio _audio;
    private readonly Lock _lock = new();
    private ICaptureSession? _session;
    private CaptureFeed? _feed;
    private IDisposable? _tick, _routeTimer, _watchdog;
    private int _generation;
    private bool _starting, _running, _blocked, _disposed;
    private double _startedAt, _lastSample;
    private double? _lastAudible, _quietSince, _playingHereSince, _musicStartedAt, _userStartedAt;
    private int _packetBaseline;
    private string _note = "";
    private string? _formatProblem;
    private double? _surfacesHiddenSince;
    private bool _outputWasActive;
    private double? _outputLastActiveAt;
    private bool _following;
    private bool _wasPlaying;

    private readonly Action<Action> _background;

    /// <param name="background">Runs device opening off the caller's thread (default: the thread pool); tests run it inline.</param>
    public SystemAudioController(ISystemAudioEnvironment environment, VisualizerAudio audio, Action<Action>? background = null)
    {
        _env = environment;
        _audio = audio;
        _background = background ?? (work => Task.Run(work));
    }

    /// <summary>Raised when <see cref="State"/>, <see cref="Status"/> or the levels change (on any thread).</summary>
    public event EventHandler? Changed;

    public AudioCaptureState State { get; private set; } = AudioCaptureState.Off;

    public string Status { get; private set; } = OffMessage;

    /// <summary>The live levels while receiving (the settings page's meters).</summary>
    public AudioLevels Levels { get; private set; }

    /// <summary>Packets received by the current session.</summary>
    public int Packets { get; private set; }

    public string FormatLabel { get; private set; } = "";

    public long DroppedFrames { get; private set; }

    /// <summary>The session's method ("System audio"), or empty.</summary>
    public string MethodLabel { get; private set; } = "";

    /// <summary>Capture IO is running.</summary>
    public bool Running
    {
        get { lock (_lock) return _running; }
    }

    /// <summary>Reconnect helps whenever capture is meant to be running.</summary>
    public bool OffersReconnect => State.Kind is not (AudioCaptureStateKind.Off or AudioCaptureStateKind.Paused);

    /// <summary>Follows the settings: call at start-up and whenever they change.</summary>
    public void Reconcile()
    {
        lock (_lock) ReconcileLocked();
        Raise();
    }

    /// <summary>The user switched System audio on: connect at once (unless something pauses it).</summary>
    public void UserStarted()
    {
        lock (_lock)
        {
            _userStartedAt = _env.Now();
            _blocked = false;
            ReconcileLocked();
        }
        Raise();
    }

    /// <summary>The user asked to reconnect.</summary>
    public void Reconnect()
    {
        lock (_lock)
        {
            TearDown();
            _userStartedAt = _env.Now();
            _blocked = false;
            ReconcileLocked();
        }
        Raise();
    }

    /// <summary>Displays woke, the session came back, or the surfaces changed: follow demand (after the route settles for a wake).</summary>
    public void Woke()
    {
        lock (_lock)
        {
            _routeTimer?.Dispose();
            _routeTimer = _env.Schedule(RouteSettle, () =>
            {
                lock (_lock)
                {
                    _blocked = false;
                    ReconcileLocked();
                }
                Raise();
            });
        }
    }

    /// <summary>A diagnostics report (the Mac's Copy Audio Diagnostics).</summary>
    public string Diagnostics()
    {
        lock (_lock)
        {
            var playback = _env.Playback();
            var prefs = _env.Preferences();
            var surfaces = _env.Surfaces();
            var latency = _session?.OutputLatency;
            return string.Join('\n',
                "Lunalyr audio · Windows",
                $"Method: {(MethodLabel.Length > 0 ? MethodLabel : "none")}; state: {State}; running: {_running}; connecting: {_starting}; packets: {Packets}; dropped: {DroppedFrames}",
                $"Format: {FormatLabel}",
                $"Levels: rms {Levels.Rms:0.000}; bass {Levels.Bass:0.000}; mid {Levels.Mid:0.000}; treble {Levels.Treble:0.000}",
                $"Demand: enabled {prefs.Enabled}; visualiser {prefs.VisualiserChosen}; displays awake {_env.DisplaysAwake()}; surfaces {surfaces.Count(s => s == SurfaceDrawing.Drawing)} of {surfaces.Count} drawing; wallpaper paused {prefs.WallpaperPaused}",
                $"Player: {(playback.PlayerPlaying ? playback.PlayerTitle + " playing" : "nothing playing")}; output active: {(playback.AnyOutput is { } any ? any.ToString() : "unknown")}",
                $"Alignment: output latency {(latency is { } l ? $"{l * 1000:0} ms" : "not reported")}; visuals delayed {_audio.VisualDelay * 1000:0} ms",
                $"Status: {Status}");
        }
    }

    // ---- Demand ----

    private AudioCaptureGate.Inputs Demand(double now, SystemAudioPreferences prefs, AudioPlayback playback) => new()
    {
        Enabled = prefs.Enabled,
        VisualiserChosen = prefs.VisualiserChosen,
        PowerPause = prefs.PowerPause,
        MusicVideoShowing = prefs.MusicVideoShowing,
        DisplaysAwake = _env.DisplaysAwake(),
        SurfacesHiddenFor = _surfacesHiddenSince is { } hidden ? now - hidden : null,
        SurfacesFrozen = AudioCaptureGate.Frozen(_env.Surfaces(), prefs.WallpaperPaused),
        OutputIdleFor = playback.OutputActive ? null : _outputLastActiveAt is { } last ? now - last : double.PositiveInfinity,
        SinceUserStart = _userStartedAt is { } started ? now - started : null,
    };

    private void ReconcileLocked()
    {
        if (_disposed) return;
        var now = _env.Now();
        var prefs = _env.Preferences();
        // The surfaces: the hidden hang time starts when the last one stops drawing.
        if (AudioCaptureGate.AnySurfaceDrawing(_env.Surfaces())) _surfacesHiddenSince = null;
        else _surfacesHiddenSince ??= now;
        var playback = _env.Playback();
        TrackOutput(playback, now);
        var decision = AudioCaptureGate.Decide(Demand(now, prefs, playback));
        // Playback and the stream's health are followed (on a half-second tick) only while they decide something.
        Follow(decision.FollowsPlayback || prefs.Enabled && prefs.VisualiserChosen && !prefs.MusicVideoShowing);
        switch (decision.Kind)
        {
            case AudioCaptureGate.DecisionKind.Off:
                TearDown();
                _blocked = false;
                _userStartedAt = null;
                Set(AudioCaptureState.Off, OffMessage);
                Follow(false);
                break;
            case AudioCaptureGate.DecisionKind.Paused:
                TearDown();
                _blocked = false;
                Set(AudioCaptureState.Paused, decision.Reason!.Value.Message);
                break;
            case AudioCaptureGate.DecisionKind.Standby:
                TearDown();
                _blocked = false;
                Set(AudioCaptureState.Standby, AudioCaptureGate.StandbyMessage(playback));
                break;
            default:
                if (_blocked) return;
                if (_session is null && !_starting) Connect("");
                else UpdateHealth(now, playback);
                break;
        }
    }

    /// <summary>The output hang time starts when playback stops; playback starting again retries a failure.</summary>
    private void TrackOutput(AudioPlayback playback, double now)
    {
        var active = playback.OutputActive;
        if (active != _outputWasActive)
        {
            if (!active) _outputLastActiveAt = now;
            else if (_blocked) _blocked = false;
            _outputWasActive = active;
        }
        if (playback.PlayerPlaying != _wasPlaying)
        {
            _wasPlaying = playback.PlayerPlaying;
            _musicStartedAt = playback.PlayerPlaying ? now : null;
            if (playback.PlayerPlaying && _blocked) _blocked = false;
        }
    }

    private void Follow(bool wanted)
    {
        if (wanted == _following) return;
        _following = wanted;
        _tick?.Dispose();
        _tick = null;
        if (wanted) ScheduleTick();
        else
        {
            _outputWasActive = false;
            _outputLastActiveAt = null;
            _surfacesHiddenSince = null;
        }
    }

    private void ScheduleTick()
    {
        _tick = _env.Schedule(TickInterval, () =>
        {
            lock (_lock)
            {
                if (!_following || _disposed) return;
                ReconcileLocked();
                if (_following) ScheduleTick();
            }
            Raise();
        });
    }

    // ---- Sessions ----

    private void Connect(string reason)
    {
        TearDown();
        _blocked = false;
        var generation = ++_generation;
        _starting = true;
        _note = reason;
        _formatProblem = null;
        _quietSince = null;
        _lastAudible = null;
        _lastSample = 0;
        _packetBaseline = 0;
        _startedAt = _env.Now();
        Levels = default;
        Packets = 0;
        _audio.Reset();
        var feed = new CaptureFeed(_audio);
        _feed = feed;
        Set(AudioCaptureState.Connecting, reason.Length > 0 ? reason : "Connecting audio…");
        _watchdog = _env.Schedule(StartupTimeout, () =>
        {
            lock (_lock)
            {
                if (generation != _generation || !_starting) return;
                Fail(AudioCaptureProblem.Failed, "Audio startup timed out. Reconnect to try again.");
            }
            Raise();
        });
        var events = new SessionEvents(this, generation);
        // Opening a device can take a moment: never on the caller's thread, never under the lock.
        _background(() =>
        {
            ICaptureSession? session = null;
            string? error = null;
            try
            {
                session = _env.Open(feed, events);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            lock (_lock)
            {
                if (generation != _generation || _disposed)
                {
                    session?.Dispose();
                    return;
                }
                _watchdog?.Dispose();
                _watchdog = null;
                if (session is null)
                {
                    var note = _note.Length > 0 ? _note + "\n" : "";
                    Fail(AudioCaptureProblem.Failed, $"{note}Couldn’t start audio: {error} Reconnect to try again.");
                }
                else
                {
                    _session = session;
                    MethodLabel = session.MethodLabel;
                    _starting = false;
                    _running = true;
                    _startedAt = _env.Now();
                    // Demand may have changed while connecting.
                    ReconcileLocked();
                }
            }
            Raise();
        });
    }

    private sealed class SessionEvents(SystemAudioController owner, int generation) : ICaptureSessionEvents
    {
        public void RouteChanged(string reason) => owner.OnRouteChanged(generation, reason);

        public void Failed(string message)
        {
            lock (owner._lock)
            {
                if (generation != owner._generation) return;
                owner.Fail(AudioCaptureProblem.Failed, $"Audio stopped: {message} Reconnect to retry.");
            }
            owner.Raise();
        }
    }

    /// <summary>The output route changed: a running session reconnects once it settles; an idle one is rebuilt on demand.</summary>
    private void OnRouteChanged(int generation, string reason)
    {
        lock (_lock)
        {
            if (generation != _generation || !_env.Preferences().Enabled) return;
            _routeTimer?.Dispose();
            _routeTimer = _env.Schedule(RouteSettle, () =>
            {
                lock (_lock)
                {
                    _routeTimer = null;
                    var wasLive = _running || _starting;
                    TearDown();
                    _blocked = false;
                    var now = _env.Now();
                    var playback = _env.Playback();
                    if (wasLive && AudioCaptureGate.Decide(Demand(now, _env.Preferences(), playback)).Kind == AudioCaptureGate.DecisionKind.Capture)
                        Connect(reason);
                    else
                        ReconcileLocked();
                }
                Raise();
            });
        }
    }

    private void Fail(AudioCaptureProblem problem, string message)
    {
        TearDown();
        _blocked = true;
        Set(AudioCaptureState.Attention(problem), message);
    }

    private void TearDown()
    {
        _generation++;
        _watchdog?.Dispose();
        _watchdog = null;
        var hadSession = _session is not null || _starting;
        _session?.Dispose();
        _session = null;
        _feed = null;
        _starting = false;
        _running = false;
        if (hadSession)
        {
            Levels = default;
            _audio.Reset();
        }
        Packets = 0;
        _packetBaseline = 0;
    }

    // ---- Health ----

    private void UpdateHealth(double now, AudioPlayback playback)
    {
        if (!_running || _feed is not { } feed) return;
        if (feed.Read(now) is { } report)
        {
            _lastSample = report.DeliveredAt;
            if (report.AudibleAt is { } audible) _lastAudible = audible;
            Levels = report.Levels;
            Packets = report.Packets;
            DroppedFrames = report.DroppedFrames;
            FormatLabel = report.FormatLabel;
            if (report.Problem is { } problem) _formatProblem = problem;
        }
        if (_formatProblem is { } formatProblem)
        {
            Set(AudioCaptureState.Attention(AudioCaptureProblem.Failed), formatProblem);
            return;
        }
        var quiet = Levels.Rms < 0.00015f || _lastSample <= 0 || now - _lastSample > 0.5;
        var playingHere = playback.PlayingHere;
        if (playingHere && quiet) _quietSince ??= now;
        else _quietSince = null;
        if (playingHere) _playingHereSince ??= now;
        else _playingHereSince = null;
        var packets = Math.Max(0, Packets - _packetBaseline);
        var elapsed = now - _startedAt;
        var packetAge = _lastSample > 0 ? now - _lastSample : 0;
        // Silence only counts while the player plays here, and after the session began.
        var silentSince = Max(_lastAudible ?? _startedAt, _startedAt, _musicStartedAt ?? _startedAt, _playingHereSince ?? _startedAt);
        var silentFor = packets > 0 ? now - silentSince : 0;
        var health = AudioCaptureHealthRules.Evaluate(elapsed, packets, packetAge, silentFor, quiet, playingHere, playback.OutputActive);
        var (state, message) = health.Status(playback, MethodLabel, _note);
        if (state.Kind == AudioCaptureStateKind.Receiving) _note = "";
        Set(state, message);
    }

    private static double Max(double a, double b, double c, double d) => Math.Max(Math.Max(a, b), Math.Max(c, d));

    private bool _changed;

    private void Set(AudioCaptureState state, string message)
    {
        if (State == state && Status == message) return;
        State = state;
        Status = message;
        _changed = true;
    }

    private void Raise()
    {
        bool changed;
        lock (_lock)
        {
            changed = _changed || _running;
            _changed = false;
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            TearDown();
            Follow(false);
            _routeTimer?.Dispose();
            _disposed = true;
        }
    }
}
