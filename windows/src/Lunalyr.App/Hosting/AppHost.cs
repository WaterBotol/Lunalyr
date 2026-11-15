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
using Lunalyr.Core.Player;
using Lunalyr.Platform;
using Lunalyr.Platform.Desktop;
using Lunalyr.Platform.Logging;
using Lunalyr.Platform.Lyrics;
using Lunalyr.Platform.Network;
using Lunalyr.Platform.NowPlaying;
using Lunalyr.Platform.Readings;
using Lunalyr.Platform.Settings;
using Lunalyr.Shell;
using Lunalyr.Shell.Demo;
using Lunalyr.Shell.Flyout;
using Lunalyr.Shell.Lyrics;
using Lunalyr.Shell.Rendering;
using Lunalyr.Shell.Settings;
using Lunalyr.Render;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Compositions;
using Lunalyr.Render.Pacing;

namespace Lunalyr.App.Hosting;

/// <summary>
/// The running app: logging, crash files, settings, the tray icon, the controller, and the message loop. The main
/// thread starts WinUI right after the tray icon (no window until one is asked for), and WinUI's loop pumps the tray
/// window too; start-up begins inside WinUI's first callback. Starting WinUI later, after a cover had been decoded,
/// failed most times (see docs/APP-SHELL.md, "Process").
/// </summary>
internal sealed partial class AppHost
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
    private volatile IMediaSessionSource? _sessions;
    private volatile NetworkConnectivity? _connectivity;
    private volatile SystemMotion? _motion;
    private Timer? _statsReset;


    private bool _quitting;
    private App? _app;

    // What the first window request asked for while WinUI was still starting (the tray loop can't show either).
    private bool _pendingSettings;
    private (PixelRect Anchor, bool Keyboard)? _pendingFlyout;
    private PlaybackCommands? _transport;

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

    // ---- Settings window (milestone 5) ----

    private string? _requestedPage;
    private volatile ReadingServices? _readings;

    /// <summary>The look <c>--theme</c> asked for this run (light, dark or system), or null for the App theme setting.</summary>
    public string? ThemeOverride { get; private set; }

    /// <summary><c>--quiet-windows</c>: windows open unactivated, at the bottom (checks while someone works at the PC).</summary>
    public bool QuietWindows => _options.QuietWindows;

    /// <summary>Windows' "Animation effects" is off (read, never changed).</summary>
    public bool SystemReducesMotion => _motion?.ReduceMotion ?? false;

    /// <summary>The wallpaper moves less: Animation effects off, and "Follow Windows' Animation effects" on.</summary>
    public bool ReduceMotion => _settings.Current.ReducesMotion(SystemReducesMotion);

    /// <summary>The readings services the wallpaper uses, once started (the settings preview draws with them).</summary>
    public ReadingServices? Readings => _readings;

    /// <summary>The page <c>--settings-page</c> asked for, once.</summary>
    public string? TakeRequestedPage() => Interlocked.Exchange(ref _requestedPage, null);

    /// <summary>Opens settings at <paramref name="page"/> (a page title or short name).</summary>
    public void OpenSettingsPage(string? page)
    {
        if (page is not null) _requestedPage = page;
        OpenSettings();
    }

    /// <summary>Runs until Quit (tray, <c>--seconds</c>), then shuts down in order. Returns the exit code.</summary>
    public int Run()
    {
        _tray.OpenSettings = OpenSettings;
        _tray.ToggleFlyout = ToggleFlyout;
        _tray.TogglePause = TogglePause;
        _tray.Quit = Quit;
        _tray.Activated = OnActivated;
        _tray.DescribeMenu = DescribeMenu;
        // "Animation effects" may have been switched (WM_SETTINGCHANGE): read it again (never changed here).
        _tray.SettingChanged = () => _motion?.Refresh();
        _tray.OnError = ex => ReportCrash(ex, "window procedure", fatal: false);
        WatchWallpaperProblems();
        _tray.Create();
        StartExtras();
        StartPackaging();
        _tray.SetTooltip("Lunalyr — starting…");
        _tray.AddIcon();
        StartPlayer();

        if (_options.QuitAfterSeconds is { } seconds)
        {
            _quitTimer = new Timer(_ => _tray.PostQuit(), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
            _log.Info($"Quitting after {seconds:0.#} s (--seconds).");
        }

        _log.Info($"Start-up: tray ready {SinceLaunch():0} ms after launch.");
        ThemeOverride = _options.Theme;
        if (_options.OpenSettings)
        {
            _requestedPage = _options.SettingsPage;
            _pendingSettings = true;
        }

        // WinUI starts now, before anything else, and pumps the tray window from here on; start-up (players, lyrics,
        // wallpaper) begins inside its first callback. Starting WinUI later, once a cover had been decoded (WIC), failed
        // most times with 0x8007029C, and a retry ended in a fail-fast (docs/APP-SHELL.md, "Process").
        RunWinUi();
        return Shutdown();
    }

    // ---- Wallpaper ----

    private EngineWallpaperRenderer? _wallpaper;

    /// <summary>The desktop renderer with the layers this build has. Pause and power rules follow the settings.</summary>
    private EngineWallpaperRenderer CreateWallpaper(ReadingServices readings)
    {
        // "Reduce flicker (compatibility)" is read once: it takes effect at the next start.
        var engine = new RenderEngine(new RenderEngineOptions
        {
            Performance = PerformanceFrom(_settings.Current),
            WindowSwapChains = _settings.Current.ReduceFlicker,
        }, Log);
        if (_settings.Current.ReduceFlicker) _log.Info("Simpler drawing (Reduce flicker) is on: wallpapers use plain window swap chains.");
        // Every background and composition, as the settings preview has them (WallpaperLayers); the backgroundMode
        // setting and "Custom Mix" pick the background (AppController.Wallpaper.cs).
        foreach (var factory in WallpaperLayers.Factories(readings.WordBreaker, Log)) engine.RegisterLayer(factory);
        _settings.Changed += (_, e) => engine.SetPerformance(PerformanceFrom(e.NewValue));
        _log.Info($"Wallpaper layers: {string.Join(", ", engine.LayerIds.Order())}.");
        return new EngineWallpaperRenderer(engine, Log);
    }

    private static PerformanceOptions PerformanceFrom(AppSettings s) => s.Performance();

    // ---- Start-up ----

    private async Task InitializeAsync()
    {
        try
        {
            // The Japanese dictionary expands under the data folder, so --data test runs never touch the real one.
            var readings = WindowsReadingServices.Create(Path.Combine(DataRoot, "Dictionaries"));
            _readings = readings;
            ILyricLookup lookup;
            if (_options.Demo)
            {
                // The demo: a silent built-in player and invented lyrics. No system media session is opened, no lyric
                // service is built, and nothing goes to the network.
                lookup = new DemoLyricLookup();
                _sessions = new DemoMediaSessionSource();
                _log.Info($"Demo: following the built-in demo player ({string.Join(", ", DemoSongs.Songs.Select(s => $"\"{s.Title}\""))}, "
                    + "in a loop); no real player is followed and nothing is looked up online.");
            }
            else
            {
                var connectivity = new NetworkConnectivity(message => Log.Lyrics.Info(message));
                _connectivity = connectivity;
                // Built the same way with --no-network; only the lookups themselves are refused, so nothing is sent.
                var service = LyricService.CreateDefault(Path.Combine(DataRoot, "Lyrics"), options: new LyricServiceOptions
                {
                    PlatformSources = [new AppleMusicWindowsSource()],
                    Readings = readings,
                    Connectivity = connectivity,
                    Http = new LyricHttpOptions { UserAgent = UserAgent },
                    Logger = Log,
                    // "Search under other names" (the Mac's one setting for both): artists' native names and songs'
                    // original titles.
                    UseArtistAliases = _settings.Current.ArtistAliases,
                    UseOriginalTitles = _settings.Current.ArtistAliases,
                });
                var lyricSources = service.Sources;
                _settings.Changed += (_, e) =>
                {
                    lyricSources.UsesArtistAliases = e.NewValue.ArtistAliases;
                    lyricSources.UsesOriginalTitles = e.NewValue.ArtistAliases;
                };
                lookup = new LyricServiceLookup(service, _options.NoNetwork ? "--no-network" : null);
                _log.Info($"Lyric sources: {string.Join(", ", service.SourceList(_settings.Current.SourceSnapshot())
                    .Where(s => s.Enabled).Select(s => s.IsAvailable ? s.Title : $"{s.Title} (unavailable)"))}; "
                    + $"network {(connectivity.IsOnline ? "online" : "offline")}.");

                // Read-only: the guard refuses every command while --no-playback is set, and nothing here sends any.
                _sessions = await GsmtcMediaSessionSource.CreateAsync(PlaybackGuard.Process, message => Log.Player.Debug(message)).ConfigureAwait(false);
            }
            var offsets = new SongOffsets(_settings.Current.SongOffsets,
                saved => _settings.Update(s => s with { SongOffsets = new Dictionary<string, double>(saved) }));
            _wallpaper = CreateWallpaper(readings);
            // Windows' "Animation effects" off is the wallpaper's Reduce Motion; read, and followed, never changed.
            var motion = new SystemMotion(logger: Log);
            _motion = motion;
            var controller = new AppController(_sessions, lookup, _wallpaper, _settings.Current, offsets, readings,
                PlaybackGuard.Process, Log, _connectivity, new AppControllerOptions
                {
                    // "Custom Mix" carries on from where its rotation got to.
                    LayoutCursorChanged = cursor => _settings.Update(s => s with { RotateLayoutsCursor = cursor }),
                    ReduceMotion = ReduceMotion,
                });
            motion.Changed += (_, _) =>
            {
                controller.SetReduceMotion(ReduceMotion);
                MotionMayNeedNotice();
            };
            if (motion.ReduceMotion)
                _log.Info(ReduceMotion
                    ? "Animation effects are off: the wallpaper moves less (Reduce Motion)."
                    : "Animation effects are off, but the wallpaper keeps its motion (Follow Windows' Animation effects is off).");
            controller.StatusChanged += (_, _) => QueueStatusUpdate();
            WireFloating(_wallpaper, controller);
            _settings.Changed += (_, e) =>
            {
                controller.ApplySettings(e.NewValue);
                if (e.OldValue.MotionFollowsWindows != e.NewValue.MotionFollowsWindows) controller.SetReduceMotion(ReduceMotion);
                if (!e.OldValue.OnboardingDone && e.NewValue.OnboardingDone) MotionMayNeedNotice();
            };
            controller.Start();
            controller.ApplySettings(_settings.Current); // anything changed while starting
            StartVisualisers(controller);
            StartMusicVideo(controller);
            StartMotionCovers(controller);
            StartDiscordAndTicker(controller);
            _controller = controller;
            // A display mirrored before the controller existed: its lyrics start hidden as the extras decided.
            _tray.Post(() =>
            {
                if (_extras is { HiddenByMirroring: true }) controller.SetHiddenByMirroring(true);
            });
            QueueStatusUpdate();
            ExtrasReady();
            MotionMayNeedNotice();
            _log.Info($"Start-up: following now playing {SinceLaunch():0} ms after launch ({_sessions.Sessions.Count} media session(s)). "
                + $"Readings: {WindowsReadingServices.Describe(readings)}.");
            if (_options.StatsPath is not null)
            {
                // Measure the steady state: frame statistics restart once start-up has settled.
                _statsReset = new Timer(_ => _wallpaper?.Engine.ResetStats(), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Couldn't start following now playing.", ex);
            _tray.Post(() => _tray.SetTooltip("Lunalyr — couldn't connect to now playing (see the log)"));
            ExtrasReady();
        }
    }

    // ---- Message loop ----

    /// <summary>WinUI's loop on the main thread, which also pumps the tray window, until Quit.</summary>
    private void RunWinUi()
    {
        var started = Stopwatch.StartNew();
        _log.Info("Starting WinUI.");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _uiDispatcher = dispatcher;
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(dispatcher));
            _app = new App(this);
            _log.Info($"WinUI started in {started.ElapsedMilliseconds} ms ({SinceLaunch():0} ms after launch).");
            _init = Task.Run(InitializeAsync);
        });
        _app = null;
    }

    // ---- Tray actions (UI thread) ----

    public void OpenSettings()
    {
        if (_quitting) return;
        if (_app is not null)
        {
            _app.ShowSettings(TakeRequestedPage());
            return;
        }

        _pendingSettings = true; // WinUI is still starting: shown as soon as it is up
    }

    /// <summary>A left click on the tray icon: the flyout opens beside it, or closes.</summary>
    public void ToggleFlyout(PixelRect anchor, bool keyboard)
    {
        if (_quitting) return;
        if (_app is not null)
        {
            _app.ToggleFlyout(anchor, keyboard);
            return;
        }

        _pendingFlyout = (anchor, keyboard); // WinUI is still starting: opened as soon as it is up
    }

    /// <summary>WinUI is up: show what was asked for while it started (<c>--settings</c>, an early click), else nothing.</summary>
    internal void ShowPendingUi(App app)
    {
        var flyout = _pendingFlyout;
        _pendingFlyout = null;
        if (_pendingSettings) app.ShowSettings(TakeRequestedPage());
        _pendingSettings = false;
        if (flyout is { } f) app.ToggleFlyout(f.Anchor, f.Keyboard);
        if (_options.OpenLyricCard) app.ShowLyricCard();
        // --flyout-capture (checks): the flyout off screen, never activated, captured, then Lunalyr quits.
        if (_options.FlyoutCapturePath is { } flyoutCapture)
            app.CaptureFlyout(_tray.IconRect() ?? new PixelRect(0, 0, 0, 0), flyoutCapture, TimeSpan.FromSeconds(_options.FlyoutCaptureAfter));
        ShowWelcomeIfDue(app);
    }

    /// <summary>Forces light or dark for Lunalyr's windows (<c>--theme</c>), or null to follow Windows.</summary>
    internal string? Theme => ThemeOverride is "light" or "dark" ? ThemeOverride : null;

    /// <summary>Quit has started: windows may close for real.</summary>
    internal bool IsQuitting => _quitting;

    /// <summary>
    /// A press on the flyout's transport, for the followed player. The guard is asked first (refused with
    /// <c>--no-playback</c> and <c>--demo</c>); the GSMTC layer asks it again before anything reaches a player.
    /// </summary>
    internal Task<TransportOutcome> SendTransportAsync(TransportCommand command, bool playing)
    {
        _transport ??= new PlaybackCommands(PlaybackGuard.Process, message => Log.Player.Info(message));
        var monitor = _controller?.Monitor;
        return _transport.SendAsync(monitor?.Session, monitor?.Capabilities ?? Lunalyr.Core.NowPlaying.PlayerCapabilities.None, command, playing);
    }

    /// <summary>
    /// "Search YouTube…" (the tray flyout, its music video row, Settings › Wallpaper › Music video): the Search YouTube
    /// window, as on the Mac. It looks YouTube up for the playing song and uses the video picked for it; it never opens the
    /// browser and nothing in it plays.
    /// </summary>
    internal void OpenMusicVideoSearch()
    {
        if (_quitting || _app is null) return;
        _log.Info("Opening Search YouTube.");
        _app.ShowMusicVideoSearch();
    }

    internal void TogglePause()
    {
        // From the tray: a pause says so in the action HUD (a paused desktop draws nothing new), as on the Mac.
        if (_extras is { } extras)
        {
            extras.Performing(Lunalyr.Shell.Extras.NoteOrigin.Window, extras.ToggleWallpaperPause);
            if (_controller is null) _log.Info(_settings.Current.WallpaperPaused ? "Wallpaper paused." : "Wallpaper resumed.");
            return;
        }

        // The controller hears about it through the settings store, which also remembers it for next time.
        if (!_settings.Update(s => s with { WallpaperPaused = !s.WallpaperPaused })) return;
        // A change to "Animation effects" while paused is taken up as the wallpaper resumes, whatever told us of it.
        if (!_settings.Current.WallpaperPaused) _motion?.Refresh();
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

        OpenLinks(forwarded.Links);
        if (forwarded.Theme is { } theme)
        {
            ThemeOverride = theme;
            _app?.ApplyTheme();
        }
        // A sign-in start of a copy already running opens nothing (--login).
        if (forwarded.Login && !forwarded.OpenSettings) return;
        if (forwarded.Links.Count == 0 || forwarded.OpenSettings) OpenSettingsPage(forwarded.SettingsPage);
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
        RecordWelcomeSeenAtQuit();
        StopPlayer();
        StopExtras();
        StopPackaging();
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

            if (_statsReset is { } reset) await reset.DisposeAsync().ConfigureAwait(false);
            _motion?.Dispose();
            // Read before the controller stops the renderer.
            if (_options.StatsPath is { } statsPath && _wallpaper is { } wallpaper) WriteStats(statsPath, wallpaper.Engine);
            if (_options.FloatingCapturePath is { } capturePath) WriteFloatingCapture(capturePath);
            if (_options.DiagnosticsPath is { } diagnosticsPath)
            {
                try
                {
                    var text = await DiagnosticsAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    Directory.CreateDirectory(Path.GetDirectoryName(diagnosticsPath)!);
                    await File.WriteAllTextAsync(diagnosticsPath, text).ConfigureAwait(false);
                    _log.Info($"Diagnostics written to {diagnosticsPath}.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
                {
                    _log.Warn($"Couldn't write the diagnostics to {diagnosticsPath}.", ex);
                }
            }
            await StopDiscordAndTickerAsync().ConfigureAwait(false);
            StopVisualisers();
            StopMusicVideo();
            if (_controller is { } controller) await controller.DisposeAsync().ConfigureAwait(false);
            _wallpaper?.Dispose(); // detaches from the desktop, leaving the system wallpaper
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

    /// <summary>
    /// <c>--stats</c>: the renderer's frame statistics since they were reset after start-up, in the shape the engine
    /// harness writes (tools/measure.ps1 reads it).
    /// </summary>
    private void WriteStats(string path, RenderEngine engine)
    {
        try
        {
            var stats = engine.GetStats();
            using var process = Process.GetCurrentProcess();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            using var w = new System.Text.Json.Utf8JsonWriter(file, new System.Text.Json.JsonWriterOptions { Indented = true });
            w.WriteStartObject();
            w.WriteString("stack", "lunalyr-app-aot");
            w.WriteString("adapter", string.Join(", ", stats.Surfaces.Select(s => s.Adapter).Distinct()));
            w.WriteStartArray("monitors");
            foreach (var s in stats.Surfaces)
            {
                w.WriteStartObject();
                w.WriteNumber("w", s.Width); w.WriteNumber("h", s.Height); w.WriteNumber("dpi", Math.Round(s.Scale * 96));
                w.WriteNumber("hz", s.RefreshHz); w.WriteString("mode", s.Mode.ToString()); w.WriteString("state", s.State.ToString());
                w.WriteString("reason", s.Reason.ToString()); w.WriteNumber("syncInterval", s.SyncInterval); w.WriteBoolean("hdr", s.Hdr);
                w.WriteBoolean("adapterDrivesMonitor", s.AdapterDrivesMonitor); w.WriteBoolean("idle", s.Idle);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("seconds", stats.Seconds);
            w.WriteNumber("frames", stats.Frames);
            w.WriteNumber("fps", stats.Fps);
            WritePercentiles(w, "cpuMsPerFrame", stats.FrameCpuMs);
            WritePercentiles(w, "frameIntervalMs", stats.FrameIntervalMs);
            w.WriteNumber("slowFrames", stats.SlowFrames);
            w.WriteNumber("workingSetMB", Math.Round(process.WorkingSet64 / 1048576.0, 1));
            w.WriteNumber("privateMB", Math.Round(process.PrivateMemorySize64 / 1048576.0, 1));
            w.WriteNumber("gcGen0", GC.CollectionCount(0));
            w.WriteNumber("gcGen2", GC.CollectionCount(2));
            w.WriteNumber("allocatedMB", Math.Round(GC.GetTotalAllocatedBytes() / 1048576.0, 1));
            w.WriteNumber("rebuilds", stats.Rebuilds);
            w.WriteNumber("deviceRecoveries", stats.DeviceRecoveries);
            w.WriteEndObject();
            _log.Info($"Frame statistics written to {path} ({stats.Fps} fps over {stats.Seconds:0.#} s).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Couldn't write the frame statistics to {path}.", ex);
        }

        static void WritePercentiles(System.Text.Json.Utf8JsonWriter w, string name, Percentiles p)
        {
            w.WriteStartObject(name);
            w.WriteNumber("avg", p.Avg); w.WriteNumber("p50", p.P50); w.WriteNumber("p95", p.P95); w.WriteNumber("p99", p.P99);
            w.WriteEndObject();
        }
    }
}
