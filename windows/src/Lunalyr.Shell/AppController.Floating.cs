// The floating lyrics' controller side (the Mac's FloatingLyricsController.update and visibilityInput, Lunalyr's own
// lines): when the window shows, edit position, and what the renderer is told.

using Lunalyr.Core.Floating;
using Lunalyr.Core.Lyrics;
using Lunalyr.Platform.Desktop;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell;

public sealed partial class AppController
{
    private bool _floatEditing;
    private DateTimeOffset? _pausedSince;
    private ITimer? _floatPauseTimer;
    private FloatingLyricsOptions? _floatSent;
    private readonly Lock _floatGate = new();

    /// <summary>The options last handed to the renderer.</summary>
    public FloatingLyricsOptions Floating
    {
        get { lock (_floatGate) return _floatSent ?? FloatingLyricsOptions.Off; }
    }

    /// <summary>Edit position is on: the window shows (even with nothing playing) and takes the pointer, never the keyboard.</summary>
    public bool FloatingEditing
    {
        get { lock (_gate) return _floatEditing; }
    }

    /// <summary>
    /// Turns edit position on or off (the settings page's "Edit position…" and "Done", the window's Done). Only while the
    /// lyrics float; never saved, so a restart always comes back locked.
    /// </summary>
    public void SetFloatingEditing(bool editing)
    {
        lock (_gate)
        {
            editing = editing && _settings.IsFloating;
            if (_disposed || _floatEditing == editing) return;
            _floatEditing = editing;
        }
        _app.Info(editing ? "Floating lyrics: edit position on." : "Floating lyrics: edit position off.");
        UpdateFloating();
        RaiseStatusChanged();
    }

    /// <summary>Works out what the floating window should do and tells the renderer when that changed.</summary>
    private void UpdateFloating()
    {
        FloatingLyricsOptions options;
        lock (_floatGate)
        {
            double? hidesAfter;
            lock (_gate)
            {
                if (_disposed) return;
                var settings = _settings;
                if (!settings.IsFloating) _floatEditing = false;
                var input = VisibilityInput(settings, _scene);
                hidesAfter = FloatingVisibility.HidesAfter(input);
                bool hasWords = input.HasTrack && ((input.HasTimedLines && !input.AllInstrumental) || input.HasPlainLines);
                options = new FloatingLyricsOptions
                {
                    Enabled = settings.IsFloating,
                    Shown = FloatingVisibility.Shows(input),
                    Editing = _floatEditing,
                    Placeholder = _floatEditing && !hasWords,
                    TextSize = settings.FloatingTextSize,
                    Backdrop = settings.FloatingBackdrop,
                    FadesUnderPointer = settings.FloatingFadeUnderPointer,
                    Placement = settings.Placement,
                    Display = settings.FloatingDisplay,
                    Frames = settings.FloatingFrames,
                    Sizes = settings.FloatingSizes,
                };
            }
            // A pause hides the window once its grace has run out: look again then.
            _floatPauseTimer?.Dispose();
            _floatPauseTimer = hidesAfter is { } after
                ? _options.TimeProvider.CreateTimer(_ => UpdateFloating(), null, TimeSpan.FromSeconds(after + 0.05), Timeout.InfiniteTimeSpan)
                : null;
            if (options == _floatSent) return;
            _floatSent = options;
            // Handed over under the gate, so two threads can never send their options in the opposite order.
            _renderer.SetFloating(options);
        }
    }

    /// <summary>The Mac's <c>visibilityInput</c>: what the window's rule needs, with how long the song has been paused.</summary>
    private FloatingVisibility.Input VisibilityInput(AppSettings settings, Render.Scene.SceneSnapshot scene)
    {
        bool hasTrack = Monitor.Session is not null && Monitor.Track is not null;
        bool playing = Monitor.PlaybackInfo.IsPlaying;
        var now = _options.TimeProvider.GetUtcNow();
        if (!hasTrack || playing) _pausedSince = null;
        else _pausedSince ??= now;
        var lines = scene.Lyrics is LyricResult.Synced synced ? synced.Lines : null;
        return new FloatingVisibility.Input(settings.Surface, _floatEditing, LyricsVisible: !settings.LyricsHidden && !_hiddenByMirroring, hasTrack, playing,
            _pausedSince is { } since ? (now - since).TotalSeconds : null,
            HasTimedLines: lines is { Count: > 0 },
            HasPlainLines: scene.Lyrics is LyricResult.Plain { Lines.Count: > 0 },
            AllInstrumental: lines is { Count: > 0 } && lines.All(l => l.IsInstrumental),
            NotFound: scene.NoneFound);
    }

    /// <summary>
    /// The renderer runs while the wallpaper isn't paused, and while the lyrics float even when it is: Pause wallpaper
    /// freezes the wallpapers (PerformanceOptions.PauseWallpaper) but leaves the floating lyrics going, as on the Mac.
    /// </summary>
    private static bool RendererRuns(AppSettings settings) => !settings.WallpaperPaused || settings.IsFloating;

    private void DisposeFloating()
    {
        lock (_floatGate)
        {
            _floatPauseTimer?.Dispose();
            _floatPauseTimer = null;
        }
    }
}
