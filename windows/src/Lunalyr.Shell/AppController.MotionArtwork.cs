using Lunalyr.Core.Covers;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Colour;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Pacing;
using Lunalyr.Shell.Covers;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell;

/// <summary>What the Covers page says about the playing song's animated cover.</summary>
public enum MotionCoverState
{
    /// <summary>Lookups are off for this run (no source: --no-network, --demo or tests).</summary>
    Unavailable,
    Off,
    NotMatchTheMusic,
    PausedForPower,
    NothingPlaying,
    NotAppleMusic,
    Stream,
    Excluded,
    LookingUp,
    NotFound,
    Unreachable,
    Available,
}

/// <summary>The playing song's animated cover as the Covers page shows it.</summary>
public sealed record MotionCoverStatus(MotionCoverState State, string? Artist = null, string? Album = null, string? Power = null);

// Animated covers (the Mac's NowPlayingMonitor.updateMotionArtwork and NowPlayingView.layer(for:), Lunalyr's own lines;
// the gate's settings check is re-implemented from Settings.swift's behaviour). A cover is looked up only where it could
// be shown: "Use animated covers" on, Match the music, the album not excluded, a song (not a stream) playing in Apple
// Music. Other players' listening is never sent to Apple. The cover found stays while the next song of the same album
// plays, and until a different answer arrives; it is drawn in place of Match the music's effect or pattern, unless
// power saving holds the wallpaper to patterns or colour only.
public sealed partial class AppController
{
    private IMotionCoverSource? _motionSource;
    private string? _motionOffReason;
    private Func<bool> _portraitDisplays = static () => false;

    /// <summary>The album key whose answer <see cref="_motionFiles"/> reflects, and the one being looked up.</summary>
    private string? _motionKey, _motionLookupKey;
    private CancellationTokenSource? _motionCts;
    private MotionCoverAnswer? _motionAnswer;
    private MotionCoverFiles? _motionFiles;
    private bool _motionPowerPaused;
    private string? _motionPowerReason;
    private string? _streamTrack;

    /// <summary>The animated cover's layer as last handed to the renderer, or null.</summary>
    public MotionCoverLayerFactory? MotionCover { get; private set; }

    /// <summary>
    /// Turns animated covers on with <paramref name="source"/> (null: off for this run, with <paramref name="offReason"/>
    /// for the Covers page). <paramref name="portraitDisplays"/> says whether any display is portrait, so the tall cover
    /// is fetched only when one could show it.
    /// </summary>
    public void UseMotionCovers(IMotionCoverSource? source, string? offReason = null, Func<bool>? portraitDisplays = null)
    {
        lock (_gate)
        {
            _motionSource = source;
            _motionOffReason = offReason;
            _portraitDisplays = portraitDisplays ?? (static () => false);
        }
        UpdateMotionArtwork();
    }

    /// <summary>Power saving in force (from the renderer): falling back to patterns or colour only pauses animated covers, as on the Mac.</summary>
    public void SetPowerConstraint(PowerConstraint? power)
    {
        var paused = power is { Fallback: PowerFallback.Patterns or PowerFallback.ColourOnly };
        AppSettings settings;
        lock (_gate)
        {
            if (_disposed || (_motionPowerPaused == paused && _motionPowerReason == power?.Reason)) return;
            _motionPowerPaused = paused;
            _motionPowerReason = power?.Reason;
            settings = _settings;
        }
        if (_motionFiles is not null) _renderer.SetLayers(BackgroundLayerFor(settings), CompositionFor(settings));
        RaiseStatusChanged();
    }

    /// <summary>Why animated covers aren't looked up in this run, or null.</summary>
    public string? MotionCoversOffReason
    {
        get { lock (_gate) return _motionOffReason; }
    }

    /// <summary>The playing song's animated cover, for the Covers page.</summary>
    public MotionCoverStatus MotionStatus
    {
        get
        {
            // The monitor's own lock is taken outside the controller's: its events call in holding it.
            var track = Monitor.Track;
            var app = Monitor.Session?.SourceApp;
            lock (_gate)
            {
                var settings = _settings;
                if (!settings.UseMotionArtwork) return new(MotionCoverState.Off);
                if (settings.BackgroundMode != "auto") return new(MotionCoverState.NotMatchTheMusic);
                if (_motionPowerPaused) return new(MotionCoverState.PausedForPower, Power: _motionPowerReason);
                if (_motionSource is null) return new(MotionCoverState.Unavailable);
                if (track is null || track.IsEmpty) return new(MotionCoverState.NothingPlaying);
                var (artist, album) = (track.Artist, NullIfEmpty(track.AlbumTitle));
                if (!IsAppleMusic(app)) return new(MotionCoverState.NotAppleMusic);
                if (_streamTrack == track.TrackKey) return new(MotionCoverState.Stream, artist, album);
                if (settings.ExcludesMotion(artist, album)) return new(MotionCoverState.Excluded, artist, album);
                if (_motionLookupKey is not null) return new(MotionCoverState.LookingUp, artist, album);
                if (_motionFiles is not null) return new(MotionCoverState.Available, artist, album);
                return new(_motionAnswer?.Unreachable == true ? MotionCoverState.Unreachable : MotionCoverState.NotFound, artist, album);
            }
        }
    }

    /// <summary>Animated covers are Apple Music's: only songs playing there are looked up (iTunes for Windows too).</summary>
    public static bool IsAppleMusic(MediaSourceApp? app) =>
        app is not null && (app.Kind == MediaSourceKind.AppleMusic || app.DisplayName == "iTunes");

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>The animated cover's layer id when one is drawn for <paramref name="settings"/>, or null.</summary>
    private string? MotionLayerFor(AppSettings settings)
    {
        var track = Monitor.Track;
        lock (_gate)
        {
            if (_motionFiles is null || _motionPowerPaused || !settings.UseMotionArtwork || settings.BackgroundMode != "auto") return null;
            if (track is not null && settings.ExcludesMotion(track.Artist, NullIfEmpty(track.AlbumTitle))) return null;
            return MotionCoverLayerFactory.LayerId;
        }
    }

    /// <summary>The player decided the song is a stream (it has a timeline but no length), or that it isn't after all.</summary>
    private void SetMotionStream(MediaTrackInfo track, bool stream)
    {
        lock (_gate)
        {
            var key = stream ? track.TrackKey : null;
            if (_streamTrack == key || (!stream && _streamTrack != track.TrackKey)) return;
            _streamTrack = key;
        }
        UpdateMotionArtwork();
    }

    /// <summary>The settings that decide animated covers changed.</summary>
    private void ApplyMotionSettings(AppSettings old, AppSettings settings)
    {
        if (old.UseMotionArtwork != settings.UseMotionArtwork || old.BackgroundMode != settings.BackgroundMode
            || !ReferenceEquals(old.MotionExclusions, settings.MotionExclusions) || old.MotionPlacement != settings.MotionPlacement)
            UpdateMotionArtwork();
    }

    /// <summary>
    /// Looks the playing song's album up where its cover could be shown, keeping the current cover until a different
    /// answer arrives (the next song of the same album never cuts away from it and back).
    /// </summary>
    private void UpdateMotionArtwork()
    {
        (string Key, MediaTrackInfo Track, IMotionCoverSource Source, bool Portrait, CancellationTokenSource Cts)? start = null;
        var current = Monitor.Track;
        var app = Monitor.Session?.SourceApp;
        var portrait = _portraitDisplays();
        lock (_gate)
        {
            if (_disposed) return;
            var settings = _settings;
            // Apple Music still playing but its next song not read yet (players briefly report nothing between songs):
            // nothing is decided until it is, so the next song of the same album never cuts away from its cover.
            if (current is null or { IsEmpty: true } && IsAppleMusic(app) && _motionKey is not null) return;
            var allowed = _motionSource is not null && current is { IsEmpty: false } && IsAppleMusic(app)
                && _streamTrack != current.TrackKey && settings.UseMotionArtwork && settings.BackgroundMode == "auto"
                && !settings.ExcludesMotion(current.Artist, NullIfEmpty(current.AlbumTitle));
            if (!allowed)
            {
                CancelMotionLookupLocked();
                _motionKey = null;
                _motionAnswer = null;
            }
            else
            {
                var key = MotionArtworkPages.CacheKey(NullIfEmpty(current!.AlbumTitle), current.Artist, current.Title);
                if (key == _motionKey)
                {
                    CancelMotionLookupLocked();
                }
                else if (key != _motionLookupKey)
                {
                    CancelMotionLookupLocked();
                    _motionLookupKey = key;
                    _motionCts = new CancellationTokenSource();
                    start = (key, current, _motionSource!, portrait, _motionCts);
                }
            }
        }
        if (start is not { } lookup)
        {
            // Nothing new to look up: the cover shown follows the gate (a cover kept for a song now left out goes).
            ShowMotion(allowedFiles: null, keepCurrent: true);
            return;
        }
        _player.Info($"Animated cover: checking Apple Music for this album.");
        RaiseStatusChanged();
        Track(LookUpMotionAsync(lookup.Key, lookup.Track, lookup.Source, lookup.Portrait, lookup.Cts));
    }

    private async Task LookUpMotionAsync(string key, MediaTrackInfo track, IMotionCoverSource source, bool portrait, CancellationTokenSource cts)
    {
        MotionCoverAnswer answer;
        try
        {
            await Task.Yield();
            answer = await source.FindAsync(new MotionCoverRequest(NullIfEmpty(track.AlbumTitle), track.Artist, track.Title, portrait), cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _player.Warn("Animated cover: the lookup failed; showing Match the music's wallpaper.", ex);
            answer = new MotionCoverAnswer(null, Unreachable: true);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_motionCts, cts)) _motionCts = null;
            }
            cts.Dispose();
        }
        lock (_gate)
        {
            if (_disposed || _motionLookupKey != key) return;
            _motionLookupKey = null;
            _motionKey = key;
            _motionAnswer = answer;
        }
        _player.Info(answer.Files is not null ? "Animated cover: found; it plays behind the lyrics."
            : answer.Unreachable ? "Animated cover: Apple Music couldn't be reached; Match the music's wallpaper stays."
            : "Animated cover: this album has none; Match the music's wallpaper stays.");
        ShowMotion(answer.Files, keepCurrent: false);
    }

    /// <summary>
    /// Hands the renderer the cover's layer when its files or placement change, then the background. With
    /// <paramref name="keepCurrent"/> the files stay as they are unless the gate now forbids them.
    /// </summary>
    private void ShowMotion(MotionCoverFiles? allowedFiles, bool keepCurrent)
    {
        AppSettings settings;
        MotionCoverLayerFactory? replace = null;
        bool changed;
        lock (_gate)
        {
            if (_disposed) return;
            settings = _settings;
            var files = keepCurrent ? (_motionKey is null && _motionLookupKey is null ? null : _motionFiles) : allowedFiles;
            changed = files != _motionFiles;
            _motionFiles = files;
            var placement = WallpaperPlacements.Parse(settings.MotionPlacement);
            if (files is not null && (MotionCover is not { } shown || shown.SquarePath != files.Square || shown.TallPath != files.Tall
                                      || shown.Placement != placement))
            {
                replace = new MotionCoverLayerFactory(files.Square, files.Tall, placement, log: _logger);
                MotionCover = replace;
            }
        }
        if (replace is not null) _renderer.ReplaceLayer(replace);
        if (changed || replace is not null) _renderer.SetLayers(BackgroundLayerFor(settings), CompositionFor(settings));
        RaiseStatusChanged();
    }

    // Must hold _gate.
    private void CancelMotionLookupLocked()
    {
        if (_motionCts is { } old) _ = old.CancelAsync();
        _motionCts = null;
        _motionLookupKey = null;
    }
}
