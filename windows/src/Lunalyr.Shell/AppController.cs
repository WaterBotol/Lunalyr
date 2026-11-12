using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.NowPlaying;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Lyrics;
using Lunalyr.Shell.Rendering;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell;

/// <summary>Tuning for <see cref="AppController"/>; tests shorten the waits.</summary>
public sealed record AppControllerOptions
{
    /// <summary>
    /// How long to wait after a track change for the player's timeline (its length and position), which arrives after
    /// the title. A track that still has no length after this is treated as a stream; no timeline at all means the
    /// player doesn't share its position.
    /// </summary>
    public TimeSpan TimelineWait { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan TimelinePoll { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// After a "no position" or "stream" verdict, how often to look again for a timeline that arrives late (some
    /// players send theirs seconds after the title); the lookup runs as soon as one with a length appears.
    /// </summary>
    public TimeSpan LateTimelinePoll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// After a lookup found nothing because the network looked unreachable, how long to wait once the connection
    /// returns before looking again (name resolution can lag behind the link coming up). The Mac's 2 s.
    /// </summary>
    public TimeSpan OnlineRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public SongClockOptions? Clock { get; init; }

    /// <summary>
    /// Turns a track's artwork (the player's thumbnail) into the scene's colours, off the caller's thread; null when the
    /// bytes are not an image. Default: <see cref="Artwork.ArtworkPalettes.FromStreamAsync"/> (WIC decode, the Mac's
    /// colour grid, <see cref="PaletteMapper"/>).
    /// </summary>
    public Func<Stream, CancellationToken, Task<Palette?>> ArtworkPalette { get; init; } = Artwork.ArtworkPalettes.FromStreamAsync;

    /// <summary>
    /// Turns a track's artwork into the cover the opening title draws, off the caller's thread; null when the bytes are
    /// not an image. Default: <see cref="Artwork.ArtworkPalettes.CoverAsync"/> (WIC, smooth scaling, premultiplied BGRA).
    /// </summary>
    public Func<byte[], CancellationToken, Task<Core.Colour.CoverArt?>> ArtworkCover { get; init; } = Artwork.ArtworkPalettes.CoverAsync;

    /// <summary>
    /// How long to wait before each further look for a cover when the player has none yet: players often send the
    /// artwork a moment after the title (and some never do; the default colours are used then).
    /// </summary>
    public IReadOnlyList<TimeSpan> ArtworkRetries { get; init; } =
        [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    /// <summary>
    /// When a new track's cover is the one already showing (the same album, or a player still sharing the previous
    /// song's cover), how long to wait before each further look for a different one.
    /// </summary>
    public IReadOnlyList<TimeSpan> ArtworkRechecks { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>
    /// Called (off the controller's lock) when "Match the music" moves its rotation on, with the new cursor to save
    /// (<c>rotateLayoutsCursor</c>), so the next launch carries on from it.
    /// </summary>
    public Action<int>? LayoutCursorChanged { get; init; }

    /// <summary>
    /// The GPU effects and line patterns "Match the music" chooses among; null takes <c>WallpaperCatalog</c>'s (tests pass
    /// their own, as the catalogue is filled by the effect layers' own files).
    /// </summary>
    public Func<(IReadOnlyList<Lunalyr.Core.Compositions.WallpaperCandidate> Effects, IReadOnlyList<Lunalyr.Core.Compositions.WallpaperCandidate> Patterns)>? WallpaperStyles { get; init; }

    /// <summary>How often a better match waiting to replace the lyrics looks whether the line being sung has ended.</summary>
    public TimeSpan SwapPoll { get; init; } = LyricSwap.Poll;

    /// <summary>The longest a better match waits for the line being sung to end before it is swapped in anyway.</summary>
    public TimeSpan SwapLimit { get; init; } = LyricSwap.Limit;

    /// <summary>
    /// Whether the system asks for less motion at start-up (<see cref="SceneSnapshot.ReduceMotion"/>); later changes come
    /// through <see cref="AppController.SetReduceMotion"/>. False unless the app reads the system's setting.
    /// </summary>
    public bool ReduceMotion { get; init; }
}

/// <summary>What the controller is doing, for the tray tooltip and the diagnostics panel.</summary>
public sealed record AppStatus
{
    /// <summary>The followed player's name ("Spotify"), or null when nothing is playing.</summary>
    public string? Player { get; init; }

    /// <summary>The player's GSMTC app id.</summary>
    public string? PlayerAppId { get; init; }

    public PlaybackStatus Playback { get; init; } = PlaybackStatus.Closed;

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    /// <summary>The song length the lookup used, in seconds.</summary>
    public double? Duration { get; init; }

    /// <summary>Where the lyrics on screen came from ("Word-by-word · LRCLIB"), or null.</summary>
    public string? LyricSource { get; init; }

    /// <summary>One calm line (<see cref="StatusText"/>).</summary>
    public string Status { get; init; } = StatusText.NothingPlaying;

    public bool WallpaperPaused { get; init; }

    /// <summary>Why playback commands are off (<c>--no-playback</c>), or null.</summary>
    public string? PlaybackCommandsBlocked { get; init; }

    /// <summary>Lyric lookups started since launch.</summary>
    public int Lookups { get; init; }
}

/// <summary>
/// The app's wiring: the system's media sessions → <see cref="NowPlayingMonitor"/> → on each track change a lyric
/// lookup (with progress) → <see cref="SceneSnapshot"/>s for the renderer, plus the song clock it reads every frame
/// (the player's position + the global offset + the song's own offset). Every failure ends in a calm status line.
/// </summary>
/// <remarks>
/// <para>Read-only towards players: it never sends a playback command. Pausing the wallpaper stops the renderer, not
/// the music.</para>
/// <para>Thread-safe. Monitor events arrive on arbitrary threads; each track change starts a new lookup generation
/// and cancels the previous one, and results from an older generation are dropped. <see cref="StatusChanged"/> is
/// raised on whichever thread made the change.</para>
/// </remarks>
public sealed partial class AppController : IAsyncDisposable
{
    private readonly IMediaSessionSource _source;
    private readonly ILyricLookup _lyrics;
    private readonly IWallpaperRenderer _renderer;
    private readonly SongOffsets _offsets;
    private readonly LyricRomanizer _romanizer;
    private readonly PlaybackGuard _guard;
    private readonly ILyricConnectivity? _connectivity;
    private readonly AppControllerOptions _options;
    private readonly CategoryLogger _app;
    private readonly CategoryLogger _player;
    private readonly CategoryLogger _lyricLog;
    private readonly Logger _logger;
    private readonly Lock _gate = new();
    private readonly Func<(double Seconds, bool Playing)> _clockReader;

    private AppSettings _settings;
    private SceneSnapshot _scene;
    private AppStatus _status;
    private long _generation;
    private CancellationTokenSource? _lookupCts;
    private MediaTrackInfo? _lookupTrack;
    private LyricQuery? _query;
    private LyricResult? _shown;
    private bool _retryWhenOnline;
    // The one look again this track gets when a lookup looked offline while Windows reported a connection.
    private bool _retriedWhileOnline;
    private CancellationTokenSource? _onlineRetryCts;
    private bool _reduceMotion;
    private bool _started;
    private bool _disposed;
    private readonly List<Task> _work = [];
    private long _swapTicket;

    private static readonly IReadOnlyDictionary<int, string> NoReadings = new Dictionary<int, string>();
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<LyricWord>> NoWordReadings = new Dictionary<int, IReadOnlyList<LyricWord>>();
    private long _readingsRequest;

    // Read every frame without the lock.
    private string? _recordingKey;
    private double _globalOffset;
    private int _hasSession;

    public AppController(IMediaSessionSource source, ILyricLookup lyrics, IWallpaperRenderer renderer, AppSettings settings,
        SongOffsets songOffsets, ReadingServices readings, PlaybackGuard guard, Logger logger,
        ILyricConnectivity? connectivity = null, AppControllerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(lyrics);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(songOffsets);
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _lyrics = lyrics;
        _renderer = renderer;
        _offsets = songOffsets;
        _romanizer = new LyricRomanizer(readings);
        _guard = guard;
        _connectivity = connectivity;
        _options = options ?? new AppControllerOptions();
        _app = logger.App;
        _player = logger.Player;
        _lyricLog = logger.Lyrics;
        _logger = logger;
        _settings = settings.Normalized();
        _globalOffset = _settings.LyricOffset;
        _reduceMotion = _options.ReduceMotion;
        _scene = new SceneSnapshot { Style = _settings.Style(), Adjustments = _settings.Adjustments(), Palette = _palette, ReduceMotion = _reduceMotion };
        _status = new AppStatus { WallpaperPaused = _settings.WallpaperPaused, PlaybackCommandsBlocked = guard.Reason };
        // The scrub preview stands in for the player's clock on the wallpaper only (LineBeingSung keeps ReadClock).
        _clockReader = ReadRendererClock;
        Monitor = new NowPlayingMonitor(source, _options.TimeProvider, _options.Clock, message => _player.Debug(message));
    }

    /// <summary>The now-playing monitor the controller follows (read-only).</summary>
    public NowPlayingMonitor Monitor { get; }

    /// <summary>The scene last handed to the renderer.</summary>
    public SceneSnapshot Scene
    {
        get { lock (_gate) return _scene; }
    }

    public AppStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public AppSettings Settings
    {
        get { lock (_gate) return _settings; }
    }

    /// <summary><see cref="Status"/> or <see cref="Scene"/> changed. Raised on any thread.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Hands the renderer its clock, layers and first scene, starts it unless paused, and starts following players.</summary>
    public void Start()
    {
        AppSettings settings;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            settings = _settings;
        }

        _renderer.SetClock(_clockReader);
        ApplyCustomWallpaper(null, settings);
        _renderer.SetLayers(BackgroundLayerFor(settings), CompositionFor(settings));
        _renderer.SetFrameRate(settings.AnimationRate);
        PublishScene();
        if (!settings.WallpaperPaused) _renderer.Start();
        else _app.Info("Wallpaper starts paused (paused from the tray last time).");

        Monitor.SessionChanged += OnSessionChanged;
        Monitor.TrackChanged += OnTrackChanged;
        Monitor.PlaybackChanged += OnPlaybackChanged;
        if (_connectivity is not null) _connectivity.Restored += OnConnectivityRestored;
        if (_guard.Reason is { } reason) _app.Info($"Playback commands are off for this run ({reason}).");
        Monitor.Start();
    }

    /// <summary>
    /// The lyric time for the renderer: the player's position plus the global and this song's offsets, and whether it
    /// is moving; NaN when nothing is playing or the player gives no position. Called every frame: no allocation.
    /// </summary>
    public (double Seconds, bool Playing) ReadClock()
    {
        if (Volatile.Read(ref _hasSession) == 0) return (double.NaN, false);
        var reading = Monitor.Clock.Read();
        if (!reading.HasTimeline) return (double.NaN, false);
        var key = Volatile.Read(ref _recordingKey);
        var song = key is null ? 0 : _offsets.OffsetFor(key);
        return (SongOffsets.LyricTime(reading.Position.TotalSeconds, Volatile.Read(ref _globalOffset), song), reading.IsPlaying);
    }

    /// <summary>Takes in changed settings: style, layers, frame rate, offset, pause, readings, and the lyric sources.</summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalized();
        AppSettings old;
        SceneSnapshot? scene = null;
        MediaTrackInfo? relook = null;
        LyricResult? rereadings = null;
        long generation;
        lock (_gate)
        {
            if (_disposed) return;
            old = _settings;
            _settings = settings;
            Volatile.Write(ref _globalOffset, settings.LyricOffset);
            generation = _generation;
            var style = settings.Style();
            if (style != _scene.Style)
            {
                _scene = _scene with { Style = style };
                scene = _scene;
            }
            // The Wallpaper page's sliders reach the backgrounds through the scene, so a drag applies at once.
            var adjustments = settings.Adjustments();
            if (adjustments != _scene.Adjustments)
            {
                _scene = _scene with { Adjustments = adjustments };
                scene = _scene;
            }

            if ((!old.SourceSnapshot().Equals(settings.SourceSnapshot()) || old.RepairQQGaps != settings.RepairQQGaps)
                && _lookupTrack is not null)
            {
                relook = _lookupTrack;
            }
            else if (old.Readings() != settings.Readings() && _shown is not null)
            {
                rereadings = _shown;
                // The readings shown were made for the old settings ("Replace lyrics" gives them another shape): they
                // go with the new style, and the new ones follow when worked out.
                if (_scene.Readings.Count > 0 || _scene.WordReadings.Count > 0)
                {
                    _scene = _scene with { Readings = NoReadings, WordReadings = NoWordReadings };
                    scene = _scene;
                }
            }

            if (old.WallpaperPaused != settings.WallpaperPaused) _status = _status with { WallpaperPaused = settings.WallpaperPaused };
        }

        ApplyLayoutSettings(old, settings);
        ApplyCustomWallpaper(old, settings);
        MusicVideoSettingsChanged(old, settings);
        if (BackgroundLayerFor(old) != BackgroundLayerFor(settings) || old.LyricComposition != settings.LyricComposition
            || old.RotateLayouts != settings.RotateLayouts)
            _renderer.SetLayers(BackgroundLayerFor(settings), CompositionFor(settings));
        // Turned to Match the music: the song on screen is matched now if its lyrics (or their absence) are known;
        // otherwise when they arrive. Other settings changes never decide it.
        if (settings.LyricComposition == AutomaticComposition
            && (old.LyricComposition != settings.LyricComposition || old.RotateLayouts != settings.RotateLayouts))
            ChooseLayout(onlyWhenSettled: true);
        if (old.AnimationRate != settings.AnimationRate) _renderer.SetFrameRate(settings.AnimationRate);
        if (old.WallpaperPaused != settings.WallpaperPaused)
        {
            if (settings.WallpaperPaused) _renderer.Stop();
            else _renderer.Start();
            _app.Info(settings.WallpaperPaused ? "Wallpaper paused." : "Wallpaper resumed.");
        }

        if (scene is not null) PublishScene();
        if (relook is not null)
        {
            _lyricLog.Info("Lyric source settings changed: looking the song up again.");
            BeginLookup(relook);
        }
        else
        {
            if (rereadings is not null) StartReadings(generation, rereadings, settings.Readings());
            // The opening title's reading of the song's title follows the readings settings too.
            if (old.Readings() != settings.Readings() && Scene.Title is { Length: > 0 } title)
                StartTitleReading(generation, title, settings.Readings());
        }

        RaiseStatusChanged();
    }

    /// <summary>
    /// The system's reduce-motion preference changed (Windows' "Animation effects"): every scene from now on carries it,
    /// and the one on screen is handed over again at once. Read only: the controller never changes the setting.
    /// </summary>
    public void SetReduceMotion(bool reduceMotion)
    {
        lock (_gate)
        {
            if (_disposed || _reduceMotion == reduceMotion) return;
            _reduceMotion = reduceMotion;
            _scene = _scene with { ReduceMotion = reduceMotion };
        }

        _app.Info(reduceMotion ? "Animation effects are off: the wallpaper moves less (Reduce Motion)." : "Animation effects are on: full motion.");
        PublishScene();
        RaiseStatusChanged();
    }

    /// <summary>Completes when no lookup or readings work is running (for tests and orderly shutdown).</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] work;
            lock (_gate)
            {
                _work.RemoveAll(task => task.IsCompleted);
                work = [.. _work];
            }

            if (work.Length == 0) return;
            await Task.WhenAll(work).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _lookupCts?.Cancel();
            CancelOnlineRetryLocked();
            CancelArtworkLocked();
        }

        Monitor.SessionChanged -= OnSessionChanged;
        Monitor.TrackChanged -= OnTrackChanged;
        Monitor.PlaybackChanged -= OnPlaybackChanged;
        if (_connectivity is not null) _connectivity.Restored -= OnConnectivityRestored;
        Monitor.Dispose();
        try
        {
            await WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _app.Warn("A lyric lookup was still finishing at shutdown.");
        }

        _renderer.Stop();
    }

    // ---- Now playing ----

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        var session = Monitor.Session;
        Volatile.Write(ref _hasSession, session is null ? 0 : 1);
        lock (_gate)
        {
            if (_disposed) return;
            // A different player: whatever was being looked up belongs to the old one. Its colours stay until the new
            // player's cover arrives; with nothing playing the wallpaper goes back to the default colours.
            CancelLookupLocked();
            _retriedWhileOnline = false;
            CancelArtworkLocked();
            _tools = LyricToolsState.Empty;
            if (session is null) _palette = PaletteMapper.Fallback;
            _cover = null;
            _scene = new SceneSnapshot { Style = _settings.Style(), Adjustments = _settings.Adjustments(), Palette = _palette, ReduceMotion = _reduceMotion };
            _status = _status with
            {
                Player =session?.SourceApp.DisplayName,
                PlayerAppId = session?.SourceAppUserModelId,
                Playback = Monitor.PlaybackInfo.Status,
                Title = null,
                Artist = null,
                Album = null,
                Duration = null,
                LyricSource = null,
                Status = session is null ? StatusText.NothingPlaying : StatusText.WaitingForTrack,
            };
        }

        _player.Info(session is null ? "No player is sharing what it plays." : $"Following {session.SourceApp.DisplayName} ({session.SourceAppUserModelId}).");
        MusicVideoTrackChanging(nothingPlaying: session is null);
        PublishScene();
        RaiseStatusChanged();
    }

    private void OnTrackChanged(object? sender, EventArgs e)
    {
        if (Monitor.Track is not { } track) return;
        // The cover's colours load beside the lyric lookup (and even when lookups are off).
        BeginArtwork(track, Monitor.Session);
        ForgetLyricTools();
        MusicVideoTrackChanging(nothingPlaying: false);
        lock (_gate)
        {
            // A new track gets its own look again (the Mac's onlineRetries, reset on a track change).
            _retriedWhileOnline = false;
        }

        BeginLookup(track);
    }

    private void OnPlaybackChanged(object? sender, EventArgs e)
    {
        var playback = Monitor.PlaybackInfo.Status;
        lock (_gate)
        {
            if (_disposed || _status.Playback == playback) return;
            _status = _status with { Playback = playback };
        }

        _player.Debug($"Playback: {playback}.");
        RaiseStatusChanged();
    }

    private void OnConnectivityRestored(object? sender, EventArgs e) => RetryWhenOnline(_options.OnlineRetryDelay, "Back online");

    /// <summary>
    /// Looks the song up again after a lookup found nothing because the network looked unreachable (ported from the
    /// Mac's <c>LyricsViewModel.connectionRestored</c>): after <paramref name="delay"/>, if the same lookup is still the
    /// latest and nothing is showing.
    /// </summary>
    private void RetryWhenOnline(TimeSpan delay, string reason)
    {
        MediaTrackInfo track;
        long generation;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed || !_retryWhenOnline || _lookupTrack is null) return;
            _retryWhenOnline = false;
            track = _lookupTrack;
            generation = _generation;
            CancelOnlineRetryLocked();
            cts = _onlineRetryCts = new CancellationTokenSource();
        }

        Track(RetryAsync());

        async Task RetryAsync()
        {
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _options.TimeProvider, cts.Token).ConfigureAwait(false);
                else await Task.Yield(); // Off the finished lookup's flow.
                lock (_gate)
                {
                    if (_disposed || generation != _generation || !ReferenceEquals(_lookupTrack, track) || _shown is not null) return;
                }

                _lyricLog.Info($"{reason}: looking the song up again.");
                BeginLookup(track);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // A newer track, or shutdown.
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_onlineRetryCts, cts)) _onlineRetryCts = null;
                }

                cts.Dispose();
            }
        }
    }

    // Must hold _gate.
    private void CancelOnlineRetryLocked()
    {
        _onlineRetryCts?.Cancel();
        _onlineRetryCts = null;
    }

    // ---- Lookups ----

    private void BeginLookup(MediaTrackInfo track)
    {
        long generation;
        CancellationTokenSource cts;
        RomanizationOptions titleReadings;
        var disabled = _lyrics.DisabledReason;
        lock (_gate)
        {
            if (_disposed) return;
            CancelLookupLocked();
            generation = _generation;
            cts = new CancellationTokenSource();
            _lookupCts = cts;
            _lookupTrack = track;
            _scene = new SceneSnapshot
            {
                Palette = _palette,
                Title = track.Title,
                Artist = track.Artist,
                Style = _settings.Style(),
                Adjustments = _settings.Adjustments(),
                Status = disabled is null ? StatusText.LookingUp : StatusText.LookupsOff,
                NoneFound = disabled is not null,
                ReduceMotion = _reduceMotion,
                Album = track.AlbumTitle.Length > 0 ? track.AlbumTitle : null,
                TrackKey = track.TrackKey,
                Cover = _cover,
                // Looking the same song up again keeps its title's reading.
                TitleReading = _scene.Title == track.Title ? _scene.TitleReading : null,
            };
            titleReadings = _settings.Readings();
            _status = _status with
            {
                Title = track.Title,
                Artist = track.Artist,
                Album = track.AlbumTitle.Length > 0 ? track.AlbumTitle : null,
                Duration = null,
                LyricSource = null,
                Status = _scene.Status!,
                Lookups = _status.Lookups + (disabled is null ? 1 : 0),
            };
        }

        _player.Info($"Now playing: \"{track.Title}\" by \"{track.Artist}\".");
        PublishScene();
        RaiseStatusChanged();
        StartTitleReading(generation, track.Title, titleReadings);
        if (disabled is not null)
        {
            _lyricLog.Info($"Not looking lyrics up: {disabled}.");
            ChooseLayout();
            cts.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_lookupCts, cts)) _lookupCts = null;
            }

            return;
        }

        Track(RunLookupAsync(generation, track, cts));
    }

    // Must hold _gate. Moves to a new generation so anything still running for the old one is ignored.
    private void CancelLookupLocked()
    {
        _generation++;
        _lookupCts?.Cancel();
        _lookupCts = null;
        _lookupTrack = null;
        _query = null;
        _shown = null;
        _retryWhenOnline = false;
        CancelOnlineRetryLocked();
        Volatile.Write(ref _recordingKey, null);
        _offsets.ActiveSource = null;
    }

    private async Task RunLookupAsync(long generation, MediaTrackInfo track, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await Task.Yield(); // Off the player's event thread.
            var reading = await WaitForTimelineAsync(ct).ConfigureAwait(false);
            string? verdict = null;
            TimeSpan duration;
            // Until the player gives a position and a length, say why nothing shows, and keep looking: a timeline that
            // arrives late still gets the song its lyrics. The next track change cancels the wait.
            while (true)
            {
                if (reading.HasTimeline && reading.Duration is { } known && known > TimeSpan.Zero)
                {
                    duration = known;
                    break;
                }

                var now = reading.HasTimeline ? StatusText.Stream : StatusText.NoPosition;
                if (now != verdict)
                {
                    verdict = now;
                    if (reading.HasTimeline) MusicVideoNotASong(track);
                    _lyricLog.Info(reading.HasTimeline
                        ? "The track has no length (a stream or radio), so lyrics aren't looked up unless one appears."
                        : "The player gave no position for this track yet, so lyrics can't follow it.");
                    ShowStatus(generation, now);
                }

                await Task.Delay(_options.LateTimelinePoll, _options.TimeProvider, ct).ConfigureAwait(false);
                reading = Monitor.Clock.Read();
            }

            if (verdict is not null)
            {
                _lyricLog.Info("The player's timeline arrived: looking the song up.");
                ShowStatus(generation, StatusText.LookingUp);
            }

            // The genre hints at the language sung (LyricLanguage), as on the Mac.
            var query = new LyricQuery(track.Title, track.Artist, track.AlbumTitle.Length > 0 ? track.AlbumTitle : null, duration.TotalSeconds)
            {
                Genre = track.Genre.Length > 0 ? track.Genre : null,
            };
            lock (_gate)
            {
                if (generation != _generation) return;
                _query = query;
                Volatile.Write(ref _recordingKey, query.RecordingKey);
                _status = _status with { Duration = duration.TotalSeconds };
                _scene = _scene with { Duration = duration.TotalSeconds };
            }

            MusicVideoSongKnown(track, duration.TotalSeconds);

            await LookUpAndShowAsync(generation, query, query, search: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer track (or shutdown) took over.
        }
        catch (Exception ex)
        {
            _lyricLog.Error("The lyric lookup failed.", ex);
            ShowStatus(generation, StatusText.LookupFailed);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_lookupCts, cts)) _lookupCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Asks for <paramref name="asked"/> (the playing song, or a search's own title and artist) and shows what comes
    /// back for the playing song's <paramref name="query"/>. The first result shows at once; each later one (an
    /// upgrade, or LRCLIB demoted by a later agreement) swaps in between lines, so a line is never re-timed while it is
    /// sung (LyricSwap, as on the Mac). Every report is kept for Settings › Timing (<see cref="LyricTools"/>).
    /// </summary>
    private async Task LookUpAndShowAsync(long generation, LyricQuery query, LyricQuery asked, bool search, CancellationToken ct)
    {
        Task? swap = null;
        try
        {
            LyricSourceSnapshot options;
            bool repair;
            lock (_gate)
            {
                if (generation != _generation) return;
                options = _settings.SourceSnapshot();
                repair = _settings.RepairQQGaps;
            }

            var applied = 0;
            Func<LyricLookupReport, ValueTask> progress = update =>
            {
                KeepReport(generation, update);
                var step = LyricSwap.Reception(update, applied, rerun: false);
                if (update.Result is { } result && step != LyricReception.Ignore)
                {
                    applied = Math.Max(update.Revision, 1);
                    if (step == LyricReception.Show) Show(generation, query, result, repair, complete: false);
                    else swap = SwapAtLineBoundaryAsync(generation, query, result, repair, complete: false, ct);
                }

                return ValueTask.CompletedTask;
            };
            var report = search
                ? await _lyrics.SearchAsync(asked, options, progress, ct).ConfigureAwait(false)
                : await _lyrics.LookupAsync(asked, options, progress, ct).ConfigureAwait(false);
            KeepReport(generation, report);

            if (report.Result is not null)
            {
                var step = LyricSwap.Reception(report, applied, rerun: false);
                if (step == LyricReception.Show) Show(generation, query, report.Result, repair, complete: true);
                // A swap still waiting for its line to end is replaced by the final one, which also marks the lookup done.
                else if (step == LyricReception.SwapAtBoundary || swap is { IsCompleted: false })
                    swap = SwapAtLineBoundaryAsync(generation, query, report.Result, repair, complete: true, ct);
                else MarkComplete(generation);
                _lyricLog.Info($"Showing {report.Result.Provenance.Label}{(report.Pinned ? " (your saved choice)" : report.Cached ? " (saved)" : "")}.");
            }
            else if (report.Offline)
            {
                // As on the Mac (foundNothingOffline): look again when the connection returns. When Windows already
                // reports a connection (it came back before the lookup finished, or one request failed on a working
                // link), look again at once instead, once per track.
                var online = _connectivity?.IsOnline == true;
                var again = false;
                lock (_gate)
                {
                    if (generation == _generation)
                    {
                        _retryWhenOnline = true;
                        again = online && !_retriedWhileOnline;
                        if (again) _retriedWhileOnline = true;
                    }
                }

                _lyricLog.Info(online
                    ? "No lyrics: the services looked unreachable, though Windows reports a connection."
                    : "No lyrics: offline. Looking again when the connection is back.");
                ShowStatus(generation, StatusText.Offline);
                if (again) RetryWhenOnline(TimeSpan.Zero, "Still online");
            }
            else if (report.SkippedStream)
            {
                ShowStatus(generation, StatusText.Stream);
            }
            else
            {
                _lyricLog.Info(search ? "The search found no lyrics." : "No lyrics found for this song.");
                ShowStatus(generation, StatusText.NoLyrics);
            }
        }
        finally
        {
            // A better match still waiting for its line to end belongs to this lookup: the next track cancels it.
            if (swap is not null)
            {
                try
                {
                    await swap.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _lyricLog.Error("Swapping in the better lyrics failed.", ex);
                }
            }
        }
    }

    /// <summary>
    /// Swaps in a better result between lines, so a line is never re-timed while it is sung: at once when no line is
    /// being sung, otherwise once the line being sung ends (or after <see cref="AppControllerOptions.SwapLimit"/>). A
    /// later swap takes the place of one still waiting. Ported from the Mac's <c>LyricsViewModel.upgrade</c>.
    /// </summary>
    private async Task SwapAtLineBoundaryAsync(long generation, LyricQuery query, LyricResult result, bool repair, bool complete,
        CancellationToken ct)
    {
        var ticket = Interlocked.Increment(ref _swapTicket);
        if (LineBeingSung() is { } sung)
        {
            var time = _options.TimeProvider;
            var deadline = time.GetUtcNow() + _options.SwapLimit;
            try
            {
                while (time.GetUtcNow() < deadline)
                {
                    await Task.Delay(_options.SwapPoll, time, ct).ConfigureAwait(false);
                    if (Interlocked.Read(ref _swapTicket) != ticket) return;
                    if (LineBeingSung() != sung) break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }

        if (Interlocked.Read(ref _swapTicket) != ticket || ct.IsCancellationRequested) return;
        Show(generation, query, result, repair, complete);
    }

    /// <summary>The index of the line on screen being sung now, if any (<see cref="LyricSwap.LineBeingSung"/>).</summary>
    private int? LineBeingSung()
    {
        LyricResult? shown;
        lock (_gate) shown = _shown;
        if (shown is not LyricResult.Synced synced) return null;
        return LyricSwap.LineBeingSung(synced.Lines, new LyricLineIndex(synced.Lines), ReadClock().Seconds);
    }

    private async Task<SongClockReading> WaitForTimelineAsync(CancellationToken ct)
    {
        var reading = Monitor.Clock.Read();
        var waited = TimeSpan.Zero;
        while ((!reading.HasTimeline || reading.Duration is null) && waited < _options.TimelineWait)
        {
            await Task.Delay(_options.TimelinePoll, _options.TimeProvider, ct).ConfigureAwait(false);
            waited += _options.TimelinePoll;
            reading = Monitor.Clock.Read();
        }

        return reading;
    }

    private void Show(long generation, LyricQuery query, LyricResult result, bool repair, bool complete)
    {
        var presented = LyricPresentation.Prepare(result, query, repair, _lyrics.WordDistributor);
        if (presented is null)
        {
            if (complete) ShowStatus(generation, StatusText.NoLyrics);
            return;
        }

        RomanizationOptions readings;
        lock (_gate)
        {
            if (generation != _generation) return;
            _shown = presented;
            _offsets.ActiveSource = result.Provenance.Source;
            _scene = _scene with { Lyrics = presented, Readings = NoReadings, WordReadings = NoWordReadings, Status = null, NoneFound = false };
            _status = _status with { LyricSource = presented.Provenance.Label, Status = complete ? StatusText.Showing : StatusText.LookingUp };
            readings = _settings.Readings();
        }

        ChooseLayout(presented);
        PublishScene();
        RaiseStatusChanged();
        StartReadings(generation, presented, readings);
    }

    private void MarkComplete(long generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _status.Status == StatusText.Showing) return;
            _status = _status with { Status = StatusText.Showing };
        }

        RaiseStatusChanged();
    }

    // Readings come from the system's language services and can take a moment on a long Japanese song, so they are
    // worked out off the lookup's flow and added to the scene when ready.
    private void StartReadings(long generation, LyricResult shown, RomanizationOptions options)
    {
        // Only the latest request's readings are shown: readings worked out for other settings (a different display
        // gives them a different shape) are dropped when they arrive.
        long request;
        lock (_gate) request = ++_readingsRequest;
        if (shown is not LyricResult.Synced || options.Display == RomanizationDisplay.Off)
        {
            var cleared = false;
            lock (_gate)
            {
                if (generation == _generation && ReferenceEquals(_shown, shown) && (_scene.Readings.Count > 0 || _scene.WordReadings.Count > 0))
                {
                    _scene = _scene with { Readings = NoReadings, WordReadings = NoWordReadings };
                    cleared = true;
                }
            }

            if (cleared) PublishScene();
            return;
        }

        Track(Task.Run(() =>
        {
            try
            {
                var (readings, words) = LyricPresentation.SceneReadings(shown, _romanizer, options);
                lock (_gate)
                {
                    if (generation != _generation || !ReferenceEquals(_shown, shown) || request != _readingsRequest) return;
                    _scene = _scene with { Readings = readings, WordReadings = words };
                }

                PublishScene();
                RaiseStatusChanged();
            }
            catch (Exception ex)
            {
                _lyricLog.Warn("Couldn't work out the readings for this song.", ex);
            }
        }));
    }

    private void ShowStatus(long generation, string status)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            _scene = _scene with { Lyrics = null, Readings = NoReadings, WordReadings = NoWordReadings, Status = status, NoneFound = StatusText.IsNoneFound(status) };
            _status = _status with { Status = status, LyricSource = null };
        }

        // A verdict that the song has no lyrics settles its layout too (waiting for a timeline or a lookup does not).
        if (status != StatusText.LookingUp && status != StatusText.NoPosition) ChooseLayout();
        PublishScene();
        RaiseStatusChanged();
    }

    private void Track(Task task)
    {
        lock (_gate)
        {
            _work.RemoveAll(t => t.IsCompleted);
            _work.Add(task);
        }
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _app.Warn("A status listener failed.", ex);
        }
    }
}
