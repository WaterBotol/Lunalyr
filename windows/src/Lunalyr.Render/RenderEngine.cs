using System.Collections.Concurrent;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Desktop;
using Lunalyr.Render.Engine;
using Lunalyr.Render.Layers;
using Lunalyr.Render.Pacing;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render;

public sealed record RenderEngineOptions
{
    /// <summary>Draw into one ordinary window on the primary monitor instead of behind the icons on every monitor.</summary>
    public bool Windowed { get; init; }
    public int WindowedWidth { get; init; } = 1280;
    public int WindowedHeight { get; init; } = 720;

    public PerformanceOptions Performance { get; init; } = PerformanceOptions.Default;

    /// <summary>Background crossfade length in seconds (the Mac's surface fade).</summary>
    public double BackgroundFadeSeconds { get; init; } = 0.8;

    /// <summary>The layers shown until <see cref="RenderEngine.SetLayers"/> picks others.</summary>
    public string BackgroundId { get; init; } = "solid";
    public string? LyricId { get; init; }

    /// <summary>How often a frame-rate summary is logged while drawing.</summary>
    public TimeSpan StatsLogInterval { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// The wallpaper render engine: one surface per monitor behind the desktop icons (or one preview window), each on a
/// flip-model swap chain on the GPU that drives its monitor, drawn on a dedicated render thread while a desktop host
/// thread owns the windows. It pauses or holds a still frame when a wallpaper can't be seen, eases off for power,
/// and recovers from display changes, Explorer restarts and device loss by itself.
/// </summary>
/// <remarks>Every public member is thread-safe. Layers are created and called only on the render thread.</remarks>
public sealed partial class RenderEngine : IDisposable
{
    private readonly RenderEngineOptions _options;
    private readonly Logger _logger;
    private readonly CategoryLogger _log;
    private readonly Lock _lifecycle = new();
    private readonly Lock _registryGate = new();
    private readonly Dictionary<string, IRenderLayerFactory> _factories = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly FrameRecorder _recorder = new();
    private readonly ManualResetEventSlim _surfacesCreated = new();

    private volatile SceneSnapshot _scene = new();
    private volatile PerformanceOptions _performance;
    private volatile Func<(double Seconds, bool Playing)>? _songClock;
    private volatile DesktopState? _desktop;
    private volatile SurfaceStatus[] _status = [];
    private volatile string _statusLine = "";
    private int _inputVersion;
    private string _backgroundId;
    private string? _lyricId;

    private Thread? _thread;
    private volatile bool _stopping;
    private DesktopHost? _host;

    public RenderEngine(RenderEngineOptions? options = null, Logger? logger = null)
    {
        _options = options ?? new RenderEngineOptions();
        _logger = logger ?? Logger.Null;
        _log = _logger.Render;
        _performance = _options.Performance;
        _backgroundId = _options.BackgroundId;
        _lyricId = _options.LyricId;
        RegisterLayer(new SolidColorLayerFactory());
        RegisterLayer(new DebugTextLayerFactory(() => _statusLine));
    }

    /// <summary>
    /// Raised when a surface starts or stops drawing (and why), or the power constraint changes. Raised on a pool
    /// thread; the app reads <see cref="RenderStatusChangedEventArgs.Power"/> to pick lighter layers under power saving.
    /// </summary>
    public event EventHandler<RenderStatusChangedEventArgs>? StatusChanged;

    /// <summary>The surfaces as of the last state change.</summary>
    public IReadOnlyList<SurfaceStatus> Surfaces => _status;

    /// <summary>One line of live stats (fps, frame time, states), refreshed twice a second.</summary>
    public string StatusLine => _statusLine;

    /// <summary>How the wallpapers are attached (null before <see cref="Start"/> or while Explorer is missing).</summary>
    public AttachMode? AttachMode => _host?.Mode;

    /// <summary>Progman's children after the last attach.</summary>
    public string AttachTree => _host?.LastTree ?? "";

    public bool IsRunning => _thread is not null;

    public PerformanceOptions Performance => _performance;

    /// <summary>
    /// The song position, offsets already applied (the app passes <c>SongClock.Read()</c> plus the global and
    /// per-song offsets). Called once per frame on the render thread, so it must be cheap and thread-safe.
    /// </summary>
    public Func<(double Seconds, bool Playing)>? SongClock
    {
        get => _songClock;
        set
        {
            _songClock = value;
            InputChanged();
        }
    }

    /// <summary>Adds or replaces a layer factory (by id). Built in: "solid" and "debug".</summary>
    public void RegisterLayer(IRenderLayerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_registryGate) _factories[factory.Id] = factory;
    }

    public IReadOnlyCollection<string> LayerIds
    {
        get { lock (_registryGate) return [.. _factories.Keys]; }
    }

    /// <summary>Starts the render thread and attaches a surface to every monitor. Returns once they exist.</summary>
    public void Start()
    {
        lock (_lifecycle)
        {
            if (_thread is not null) return;
            _stopping = false;
            var ready = new ManualResetEventSlim();
            Exception? error = null;
            var thread = new Thread(() => RenderThread(ready, e => error = e))
            {
                IsBackground = true, Name = "Lunalyr render", Priority = ThreadPriority.AboveNormal,
            };
            thread.Start();
            ready.Wait();
            if (error is not null)
            {
                thread.Join(2000);
                throw new InvalidOperationException("The render engine couldn't create its graphics devices.", error);
            }
            _thread = thread;
            _host = new DesktopHost(new Sink(this), new DesktopHostOptions
            {
                Windowed = _options.Windowed, WindowedWidth = _options.WindowedWidth, WindowedHeight = _options.WindowedHeight,
            }, _logger);
            _host.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
            try
            {
                _surfacesCreated.Reset();
                _host.Start();
                StartFloatingHost();
                // The host hands the windows over asynchronously; return once their swap chains exist.
                if (_host.Mode is not null) _surfacesCreated.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                StopRenderThread();
                _host = null;
                throw;
            }
            _log.Info("Render engine started");
        }
    }

    /// <summary>Raised when the preview window (windowed mode) is closed by the user.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Destroys the surfaces and stops the render thread.</summary>
    public void Stop()
    {
        lock (_lifecycle)
        {
            if (_thread is null) return;
            // The floating window hands its swap chain back to the render thread before it goes.
            StopFloatingHost();
            _host?.Stop();
            _host = null;
            StopRenderThread();
            _log.Info("Render engine stopped");
        }
    }

    private void StopRenderThread()
    {
        _stopping = true;
        _wake.Set();
        _thread?.Join(5000);
        _thread = null;
    }

    public void Dispose() => Stop();

    /// <summary>Swaps in what is playing and what to show. Paused or idle surfaces redraw once to show it.</summary>
    public void SetScene(SceneSnapshot scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _scene = scene;
        InputChanged();
    }

    /// <summary>
    /// Chooses the background and lyric composition by factory id (<paramref name="lyricId"/> null for none). The
    /// background crossfades; a change while paused shows at once on the still frame.
    /// </summary>
    public void SetLayers(string backgroundId, string? lyricId)
    {
        ArgumentException.ThrowIfNullOrEmpty(backgroundId);
        lock (_registryGate)
        {
            if (!_factories.ContainsKey(backgroundId)) throw new ArgumentException($"No layer registered as \"{backgroundId}\".", nameof(backgroundId));
            if (lyricId is not null && !_factories.ContainsKey(lyricId)) throw new ArgumentException($"No layer registered as \"{lyricId}\".", nameof(lyricId));
        }
        Post(() => ApplyLayers(backgroundId, lyricId));
    }

    /// <summary>
    /// Registers <paramref name="factory"/> in place of the one with its id; when that id is the background on screen, it
    /// crossfades into a new instance (another imported file, another placement). Otherwise the next change uses it.
    /// </summary>
    public void ReplaceLayer(IRenderLayerFactory factory)
    {
        RegisterLayer(factory);
        Post(() =>
        {
            if (_backgroundId == factory.Id) ApplyLayers(factory.Id, _lyricId, renew: true);
        });
    }

    /// <summary>The Performance settings (frame rate, low power, battery, covered and paused wallpapers).</summary>
    public void SetPerformance(PerformanceOptions performance)
    {
        ArgumentNullException.ThrowIfNull(performance);
        _performance = performance;
        _wake.Set();
    }

    /// <summary>Frame-rate cap: 15, 30, 60 fps or the display's refresh rate (power saving may hold it at 30).</summary>
    public void SetFrameRate(FrameRateCap rate) => SetPerformance(_performance with { FrameRate = rate });

    /// <summary>
    /// A PNG of what monitor <paramref name="monitorIndex"/>'s surface draws (its back buffer), for checks. Draws a
    /// fresh frame for it even while it is paused.
    /// </summary>
    public Task<byte[]> CaptureAsync(int monitorIndex, CancellationToken cancellationToken = default)
    {
        if (_thread is null) throw new InvalidOperationException("The engine isn't running.");
        var request = new CaptureRequest(monitorIndex);
        cancellationToken.Register(() => request.Completion.TrySetCanceled(cancellationToken));
        Post(() => QueueCapture(request));
        return request.Completion.Task;
    }

    /// <summary>Frame statistics since the last <see cref="ResetStats"/>.</summary>
    public RenderStats GetStats()
    {
        var (seconds, frames, cpu, interval, slow) = _recorder.Read();
        return new RenderStats(Math.Round(seconds, 2), frames, seconds > 0 ? Math.Round(frames / seconds, 1) : 0, cpu, interval, slow,
            _status, _power, Volatile.Read(ref _rebuilds), Volatile.Read(ref _recoveries));
    }

    public void ResetStats() => _recorder.Reset();

    /// <summary>Recreate every surface's window and swap chain (as after a display change).</summary>
    public void RequestRebuild() => _host?.RequestRebuild();

    /// <summary>
    /// Diagnostics: runs the device-loss recovery as if the GPU had been removed (releases every layer's resources,
    /// recreates the devices and swap chains, recreates the resources). Real loss can't be forced without a driver reset.
    /// </summary>
    public void SimulateDeviceLoss() => Post(() =>
    {
        _log.Warn("Simulating device loss");
        foreach (var gpu in _gpus.Values) gpu.Lost = true;
    });

    private void Post(Action command)
    {
        _commands.Enqueue(command);
        _wake.Set();
    }

    private void InputChanged()
    {
        Interlocked.Increment(ref _inputVersion);
        _wake.Set();
    }

    /// <summary>The desktop host's view of the engine (called on the host thread).</summary>
    private sealed class Sink(RenderEngine engine) : IDesktopSurfaceSink
    {
        public WaitHandle ReleaseSurfaces()
        {
            var done = new ManualResetEvent(false);
            if (engine._thread is null && engine._stopping)
            {
                done.Set();
                return done;
            }
            engine.Post(() =>
            {
                engine.TearDownSurfaces();
                done.Set();
            });
            return done;
        }

        public void SetSurfaces(IReadOnlyList<SurfaceWindow> windows, int generation) =>
            engine.Post(() => engine.CreateSurfaces(windows, generation));

        public void UpdateState(DesktopState state)
        {
            engine._desktop = state;
            engine._wake.Set();
        }
    }
}
