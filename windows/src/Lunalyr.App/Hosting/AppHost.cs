using System.Diagnostics;
using System.Reflection;
using Lunalyr.App.Interop;
using Lunalyr.App.Tray;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Pipeline;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Playback;
using Lunalyr.Platform;
using Lunalyr.Platform.Logging;
using Lunalyr.Platform.Lyrics;
using Lunalyr.Platform.Network;
using Lunalyr.Platform.NowPlaying;
using Lunalyr.Platform.Readings;
using Lunalyr.Platform.Settings;
using Lunalyr.Shell;
using Lunalyr.Shell.Lyrics;
using Lunalyr.Shell.Rendering;
using Lunalyr.Shell.Settings;

namespace Lunalyr.App.Hosting;

/// <summary>
/// The running app: logging, crash files, settings, the tray icon, the controller, and the two message loops. The
/// main thread runs a plain Win32 loop while idle; the first "Open settings" ends it and starts WinUI on the same
/// thread, which from then on pumps the tray window as well (Windows App SDK can only start once per process).
/// </summary>
internal sealed class AppHost
{
    /// <summary>The generic User-Agent the development rules ask for: nothing personal in it.</summary>
    public const string UserAgent = "Lunalyr/0.1 (Windows port; dev)";

    private readonly ShellOptions _options;
    private readonly SingleInstance _instance;
    private readonly DateTime _processStart;
    private readonly LoggingHost _logging;
    private readonly SettingsStore<AppSettings> _settings;
    private readonly TrayHost _tray;
    private readonly CategoryLogger _log;
    private Task _init = Task.CompletedTask;
    private Timer? _quitTimer;
    private int _statusQueued;

    // Created by the background start-up; read on the UI thread.
    private volatile AppController? _controller;
    private volatile GsmtcMediaSessionSource? _sessions;
    private volatile NetworkConnectivity? _connectivity;

    private bool _uiRequested;
    private bool _quitting;
    private App? _app;

    public AppHost(ShellOptions options, string dataRoot, SingleInstance instance)
    {
        _options = options;
        _instance = instance;
        DataRoot = dataRoot;
        _processStart = Process.GetCurrentProcess().StartTime;
        Version = typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AppHost).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        LogsFolder = AppPaths.LogsFolder(dataRoot);

        _logging = LoggingHost.Create(LogsFolder, options.LogLevel);
        Log = _logging.Logger;
        _log = Log.App;
        InstallCrashHandlers();

        _log.Info($"Lunalyr {Version} starting (process {Environment.ProcessId}, data folder {dataRoot}).");
        if (PlaybackGuard.Process.Reason is { } reason) _log.Info($"Playback commands and sound are off for this process ({reason}).");
        if (options.NoNetwork) _log.Info("Lyric lookups are off for this run (--no-network).");
        foreach (var problem in options.Problems) _log.Warn($"Command line: {problem}");

        _settings = AppSettingsStore.Open(dataRoot, Log);
        if (_settings.LoadReport.UserMessage is { } message) _log.Warn($"Settings: {message}");
        _tray = new TrayHost(instance.WindowClass, Log);
    }

    public string Version { get; }

    public string DataRoot { get; }

    public string LogsFolder { get; }

    public Logger Log { get; }

    public AppController? Controller => _controller;

    public SettingsStore<AppSettings> Settings => _settings;

    public PlaybackGuard Guard => PlaybackGuard.Process;

    /// <summary>Runs until Quit (tray, <c>--seconds</c>), then shuts down in order. Returns the exit code.</summary>
    public int Run()
    {
        _tray.OpenSettings = OpenSettings;
        _tray.TogglePause = TogglePause;
        _tray.Quit = Quit;
        _tray.Activated = OnActivated;
        _tray.DescribeMenu = DescribeMenu;
        _tray.OnError = ex => ReportCrash(ex, "window procedure", fatal: false);
        _tray.Create();
        _tray.SetTooltip("Lunalyr — starting…");
        _tray.AddIcon();

        if (_options.QuitAfterSeconds is { } seconds)
        {
            _quitTimer = new Timer(_ => _tray.PostQuit(), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
            _log.Info($"Quitting after {seconds:0.#} s (--seconds).");
        }

        _log.Info($"Start-up: tray ready {SinceLaunch():0} ms after launch.");
        _init = Task.Run(InitializeAsync);
        if (_options.OpenSettings) OpenSettings();

        RunWin32Loop();
        if (_uiRequested && !_quitting) RunWinUi();
        return Shutdown();
    }

    // ---- Start-up ----

    private async Task InitializeAsync()
    {
        try
        {
            // The Japanese dictionary expands under the data folder, so --data test runs never touch the real one.
            var readings = WindowsReadingServices.Create(Path.Combine(DataRoot, "Dictionaries"));
            _connectivity = new NetworkConnectivity(message => Log.Lyrics.Info(message));
            // Built the same way with --no-network; only the lookups themselves are refused, so nothing is sent.
            var service = LyricService.CreateDefault(Path.Combine(DataRoot, "Lyrics"), options: new LyricServiceOptions
            {
                PlatformSources = [new AppleMusicWindowsSource()],
                Readings = readings,
                Connectivity = _connectivity,
                Http = new LyricHttpOptions { UserAgent = UserAgent },
                Logger = Log,
            });
            ILyricLookup lookup = new LyricServiceLookup(service, _options.NoNetwork ? "--no-network" : null);
            _log.Info($"Lyric sources: {string.Join(", ", service.SourceList(_settings.Current.SourceSnapshot())
                .Where(s => s.Enabled).Select(s => s.IsAvailable ? s.Title : $"{s.Title} (unavailable)"))}; "
                + $"network {(_connectivity.IsOnline ? "online" : "offline")}.");

            // Read-only: the guard refuses every command while --no-playback is set, and nothing here sends any.
            _sessions = await GsmtcMediaSessionSource.CreateAsync(PlaybackGuard.Process, message => Log.Player.Debug(message)).ConfigureAwait(false);
            var offsets = new SongOffsets(_settings.Current.SongOffsets,
                saved => _settings.Update(s => s with { SongOffsets = new Dictionary<string, double>(saved) }));
            var controller = new AppController(_sessions, lookup, new NullWallpaperRenderer(Log), _settings.Current, offsets, readings,
                PlaybackGuard.Process, Log, _connectivity);
            controller.StatusChanged += (_, _) => QueueStatusUpdate();
            _settings.Changed += (_, e) => controller.ApplySettings(e.NewValue);
            controller.Start();
            controller.ApplySettings(_settings.Current); // anything changed while starting
            _controller = controller;
            QueueStatusUpdate();
            _log.Info($"Start-up: following now playing {SinceLaunch():0} ms after launch ({_sessions.Sessions.Count} media session(s)). "
                + $"Readings: {WindowsReadingServices.Describe(readings)}.");
        }
        catch (Exception ex)
        {
            _log.Error("Couldn't start following now playing.", ex);
            _tray.Post(() => _tray.SetTooltip("Lunalyr — couldn't connect to now playing (see the log)"));
        }
    }

    // ---- Message loops ----

    private static void RunWin32Loop()
    {
        while (Win32.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            Win32.TranslateMessage(msg);
            Win32.DispatchMessage(msg);
        }
    }

    private void RunWinUi()
    {
        var started = Stopwatch.StartNew();
        _log.Info("Starting the settings UI (WinUI).");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(dispatcher));
            _app = new App(this);
            _log.Info($"WinUI started in {started.ElapsedMilliseconds} ms.");
        });
        _app = null;
    }

    // ---- Tray actions (UI thread) ----

    public void OpenSettings()
    {
        if (_quitting) return;
        if (_app is not null)
        {
            _app.ShowSettings();
            return;
        }

        if (_uiRequested) return;
        _uiRequested = true;
        Win32.PostQuitMessage(0); // leave the plain loop; Run() starts WinUI
    }

    private void TogglePause()
    {
        // The controller hears about it through the settings store, which also remembers it for next time.
        if (!_settings.Update(s => s with { WallpaperPaused = !s.WallpaperPaused })) return;
        if (_controller is null) _log.Info(_settings.Current.WallpaperPaused ? "Wallpaper paused." : "Wallpaper resumed.");
        UpdateStatus();
    }

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _log.Info("Quitting.");
        if (_app is not null) _app.Exit();
        else Win32.PostQuitMessage(0);
    }

    private void OnActivated(string[] args)
    {
        var forwarded = ShellOptions.Parse(args);
        _log.Info($"Activated by a second launch{(args.Length > 0 ? $" ({args.Length} argument(s))" : "")}.");
        // A forwarded flag may only make this process stricter, never less strict.
        if (forwarded.NoPlayback && !PlaybackGuard.Process.IsBlocked)
        {
            PlaybackGuard.Process.Block("--no-playback (from a second launch)");
            _log.Info("Playback commands and sound are now off for this process (--no-playback from a second launch).");
            UpdateStatus();
        }

        foreach (var link in forwarded.Links) _log.Info($"Received a link: {link} (links aren't handled yet).");
        if (forwarded.Links.Count == 0 || forwarded.OpenSettings) OpenSettings();
    }

    private (string NowPlaying, bool Paused) DescribeMenu()
    {
        var status = _controller?.Status;
        var line = status?.Title is { } title
            ? (status.Artist is { Length: > 0 } artist ? $"{title} — {artist}" : title)
            : status?.Status ?? "Starting…";
        return (line, _settings.Current.WallpaperPaused);
    }

    // Controller events come from any thread; the tooltip and the settings window are updated on the UI thread, at
    // most once per burst.
    private void QueueStatusUpdate()
    {
        if (Interlocked.Exchange(ref _statusQueued, 1) == 1) return;
        _tray.Post(() =>
        {
            Volatile.Write(ref _statusQueued, 0);
            UpdateStatus();
        });
    }

    private void UpdateStatus()
    {
        var status = _controller?.Status;
        var lines = new List<string> { "Lunalyr" };
        if (status?.Title is { } title) lines.Add(status.Artist is { Length: > 0 } artist ? $"{title} — {artist}" : title);
        lines.Add(status?.Status ?? "Starting…");
        if (_settings.Current.WallpaperPaused) lines.Add("Wallpaper paused");
        _tray.SetTooltip(string.Join('\n', lines));
        _app?.RefreshStatus();
    }

    // ---- Shutdown and crashes ----

    private int Shutdown()
    {
        _quitting = true;
        _quitTimer?.Dispose();
        _tray.Dispose();
        var clean = Task.Run(async () =>
        {
            try
            {
                await _init.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.Warn("Start-up was still running at shutdown.");
            }

            if (_controller is { } controller) await controller.DisposeAsync().ConfigureAwait(false);
            _sessions?.Dispose();
            _connectivity?.Dispose();
            await _settings.DisposeAsync().ConfigureAwait(false); // flushes pending changes
        }).Wait(TimeSpan.FromSeconds(10));

        _log.Info(clean
            ? $"Stopped cleanly, {SinceLaunch() / 1000:0.0} s after launch."
            : "Shutdown took too long; quitting anyway.");
        _logging.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _instance.Dispose();
        return clean ? 0 : 2;
    }

    private void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportCrash(e.ExceptionObject as Exception, "AppDomain.UnhandledException", fatal: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log.Warn("A background task failed and nobody looked at it.", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Logs <paramref name="exception"/> and writes a crash file; when fatal, waits for the log to reach disk.</summary>
    public void ReportCrash(Exception? exception, string context, bool fatal)
    {
        var path = CrashReport.Write(LogsFolder, exception, context, Version);
        _log.Error($"Unhandled exception ({context}){(path is null ? "" : $"; details in {Path.GetFileName(path)}")}.", exception);
        if (fatal) _logging.FlushAsync().Wait(TimeSpan.FromSeconds(2));
    }

    private double SinceLaunch() => (DateTime.Now - _processStart).TotalMilliseconds;
}
