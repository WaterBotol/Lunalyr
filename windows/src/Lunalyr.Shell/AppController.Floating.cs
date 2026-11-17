// The floating lyrics' controller side (the Mac's FloatingLyricsController.update and visibilityInput, Lunalyr's own
// lines): when the windows show, edit position, and what the renderer is told. Once the displays are known each one
// shows what its own choice says (the Mac's 0da0eef7): lyrics on its wallpaper, a floating window, both, or neither,
// each surface in its own style.

using Lunalyr.Core.Floating;
using Lunalyr.Core.Lyrics;
using Lunalyr.Platform.Desktop;
using Lunalyr.Render;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell;

/// <summary>A connected display: its lasting key (as settings keep it) and its monitor's device name (as the renderer knows it).</summary>
public sealed record ConnectedDisplay(string Key, string Device);

public sealed partial class AppController
{
    private bool _floatEditing;
    private DateTimeOffset? _pausedSince;
    private ITimer? _floatPauseTimer;
    private FloatingLyricsOptions? _floatSent;
    private FloatingLyricsOptions[]? _windowsSent;
    private string[]? _withoutSent;
    private readonly Lock _floatGate = new();
    private IReadOnlyList<ConnectedDisplay> _displays = [];
    private LooksCache? _looks;

    // The parked looks worked out for one state of the settings, the displays and the song's layout (reading a look
    // copies its settings, so this is not done again for every scene published).
    private sealed record LooksCache(AppSettings Settings, IReadOnlyList<ConnectedDisplay> Displays, string? Layout,
        Dictionary<string, SurfaceLook> Floating, Dictionary<string, SurfaceLook> Desktop);

    /// <summary>The options last handed to the renderer (the first floating window's, once displays are known).</summary>
    public FloatingLyricsOptions Floating
    {
        get { lock (_floatGate) return _floatSent ?? FloatingLyricsOptions.Off; }
    }

    /// <summary>Edit position is on: the windows show (even with nothing playing) and take the pointer, never the keyboard.</summary>
    public bool FloatingEditing
    {
        get { lock (_gate) return _floatEditing; }
    }

    /// <summary>The connected displays' keys, the main display first (none while they are not known).</summary>
    public IReadOnlyList<string> DisplayKeys
    {
        get { lock (_gate) return [.. _displays.Select(d => d.Key)]; }
    }

    /// <summary>
    /// The connected displays, the main one first (the host reads them at start and after each display change). Until
    /// they are known the lyrics go where the one surface choice says, as before displays had their own.
    /// </summary>
    public void SetDisplays(IReadOnlyList<ConnectedDisplay> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        bool started, ranBefore, runsNow;
        lock (_gate)
        {
            if (_disposed) return;
            ranBefore = RendererRuns(_settings);
            _displays = [.. displays];
            runsNow = RendererRuns(_settings);
            started = _started;
        }
        if (started && ranBefore != runsNow)
        {
            if (runsNow) _renderer.Start();
            else _renderer.Stop();
        }
        UpdateFloating();
    }

    /// <summary>Lyrics float on some display: on one of the connected displays once they are known, else while the surface is Floating.</summary>
    public bool FloatsAnywhere(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate) return _displays.Count == 0 ? settings.IsFloating : settings.FloatsOn(_displays.Select(d => d.Key));
    }

    /// <summary>
    /// Turns edit position on or off (the settings page's "Edit position…" and "Done", the window's Done). Only while
    /// lyrics float; never saved, so a restart always comes back locked.
    /// </summary>
    public void SetFloatingEditing(bool editing)
    {
        lock (_gate)
        {
            editing = editing && FloatsAnywhere(_settings);
            if (_disposed || _floatEditing == editing) return;
            _floatEditing = editing;
        }
        _app.Info(editing ? "Floating lyrics: edit position on." : "Floating lyrics: edit position off.");
        UpdateFloating();
        RaiseStatusChanged();
    }

    /// <summary>Works out what the floating windows and the wallpapers' lyrics should do and tells the renderer what changed.</summary>
    private void UpdateFloating()
    {
        lock (_floatGate)
        {
            double? hidesAfter;
            FloatingLyricsOptions single;
            List<FloatingLyricsOptions> windows = [];
            List<string> without = [];
            LooksCache? looks = null;
            bool perDisplay;
            lock (_gate)
            {
                if (_disposed) return;
                var settings = _settings;
                var displays = _displays;
                perDisplay = displays.Count > 0;
                bool floats = FloatsAnywhere(settings);
                if (!floats) _floatEditing = false;
                var input = VisibilityInput(settings, _scene, perDisplay ? (floats ? LyricsSurface.Floating : LyricsSurface.Desktop) : settings.Surface);
                hidesAfter = FloatingVisibility.HidesAfter(input);
                bool hasWords = input.HasTrack && ((input.HasTimedLines && !input.AllInstrumental) || input.HasPlainLines);
                bool shown = FloatingVisibility.Shows(input);
                FloatingLyricsOptions Window(bool enabled, string display, string layout) => new()
                {
                    Enabled = enabled,
                    Shown = shown,
                    Editing = _floatEditing,
                    Placeholder = _floatEditing && !hasWords,
                    // Each layout keeps its own floating size and text size once resized.
                    TextSize = settings.FloatingTextSizeFor(layout),
                    Layout = layout,
                    LayoutSize = settings.LayoutSize(layout),
                    Backdrop = settings.FloatingBackdrop,
                    FadesUnderPointer = settings.FloatingFadeUnderPointer,
                    Placement = settings.PlacementFor(display),
                    Display = display,
                    Frames = settings.FloatingFrames,
                    Sizes = settings.FloatingSizes,
                };
                if (!perDisplay)
                {
                    // Lock is re-entrant: CompositionFor takes _gate again.
                    single = Window(settings.IsFloating, settings.FloatingDisplay, settings.IsFloating ? CompositionFor(settings) : "");
                }
                else
                {
                    looks = LooksFor(settings, displays);
                    var main = displays[0].Key;
                    var editing = settings.EditingSlot(main);
                    foreach (var d in displays)
                    {
                        var choice = settings.DisplayChoice(d.Key);
                        if (choice.Floats())
                        {
                            // The window drawing the style being edited draws the scene's own; the others their parked copy.
                            var layout = editing == new LookSlot(d.Key, LyricsSurface.Floating) ? CompositionFor(settings)
                                : looks.Floating[d.Key].LyricId ?? CompositionFor(settings);
                            windows.Add(Window(true, d.Key, layout));
                        }
                        if (!choice.OnDesktop()) without.Add(d.Device);
                    }
                    single = windows.FirstOrDefault() ?? FloatingLyricsOptions.Off;
                }
            }
            // A pause hides the windows once its grace has run out: look again then.
            _floatPauseTimer?.Dispose();
            _floatPauseTimer = hidesAfter is { } after
                ? _options.TimeProvider.CreateTimer(_ => UpdateFloating(), null, TimeSpan.FromSeconds(after + 0.05), Timeout.InfiniteTimeSpan)
                : null;
            // Handed over under the gate, so two threads can never send their options in the opposite order.
            if (!perDisplay)
            {
                if (_windowsSent is not null)
                {
                    // The displays went unknown again: back to one surface choice.
                    _windowsSent = null;
                    _withoutSent = null;
                    _renderer.SetLooks(new Dictionary<string, SurfaceLook>(), new Dictionary<string, SurfaceLook>());
                    _floatSent = null;
                }
                if (single == _floatSent) return;
                _floatSent = single;
                _renderer.SetFloating(single);
                return;
            }
            _floatSent = single;
            if (looks is not null && !ReferenceEquals(looks, _looks))
            {
                _looks = looks;
                _renderer.SetLooks(looks.Floating, looks.Desktop);
            }
            if (_withoutSent is null || !without.SequenceEqual(_withoutSent))
            {
                _withoutSent = [.. without];
                _renderer.SetDesktopLyrics(_withoutSent);
            }
            if (_windowsSent is null || !windows.SequenceEqual(_windowsSent))
            {
                _windowsSent = [.. windows];
                _renderer.SetFloatingWindows(_windowsSent);
            }
        }
    }

    /// <summary>
    /// Each surface's parked look: every floating window's and every desktop's but the slot being edited, which draws
    /// the scene's own style. A parked copy reads its style keys from its kept look and every other key through.
    /// Must hold _gate.
    /// </summary>
    private LooksCache LooksFor(AppSettings settings, IReadOnlyList<ConnectedDisplay> displays)
    {
        var layout = _layout;
        if (_looks is { } cached && ReferenceEquals(cached.Settings, settings) && ReferenceEquals(cached.Displays, displays) && cached.Layout == layout)
            return cached;
        var main = displays[0].Key;
        var editing = settings.EditingSlot(main);
        var available = AvailableCompositions();
        Dictionary<string, SurfaceLook> floating = new(StringComparer.Ordinal), desktop = new(StringComparer.Ordinal);
        SurfaceLook Parked(string display, LyricsSurface surface)
        {
            var look = settings.LookFor(display, surface, main);
            var chosen = look.LyricComposition == AutomaticComposition ? layout ?? "constellation" : look.LyricComposition;
            return new SurfaceLook(look.Style(), available.Contains(chosen) ? chosen : null);
        }
        foreach (var d in displays)
        {
            var choice = settings.DisplayChoice(d.Key);
            if (choice.Floats() && editing != new LookSlot(d.Key, LyricsSurface.Floating)) floating[d.Key] = Parked(d.Key, LyricsSurface.Floating);
            if (choice.OnDesktop() && editing != new LookSlot(d.Key, LyricsSurface.Desktop)) desktop[d.Device] = Parked(d.Key, LyricsSurface.Desktop);
        }
        return new LooksCache(settings, displays, layout, floating, desktop);
    }

    /// <summary>The Mac's <c>visibilityInput</c>: what the window's rule needs, with how long the song has been paused.</summary>
    private FloatingVisibility.Input VisibilityInput(AppSettings settings, Render.Scene.SceneSnapshot scene, LyricsSurface surface)
    {
        bool hasTrack = Monitor.Session is not null && Monitor.Track is not null;
        bool playing = Monitor.PlaybackInfo.IsPlaying;
        var now = _options.TimeProvider.GetUtcNow();
        if (!hasTrack || playing) _pausedSince = null;
        else _pausedSince ??= now;
        var lines = scene.Lyrics is LyricResult.Synced synced ? synced.Lines : null;
        return new FloatingVisibility.Input(surface, _floatEditing, LyricsVisible: !settings.LyricsHidden && !_hiddenByMirroring, hasTrack, playing,
            _pausedSince is { } since ? (now - since).TotalSeconds : null,
            HasTimedLines: lines is { Count: > 0 },
            HasPlainLines: scene.Lyrics is LyricResult.Plain { Lines.Count: > 0 },
            AllInstrumental: lines is { Count: > 0 } && lines.All(l => l.IsInstrumental),
            NotFound: scene.NoneFound);
    }

    /// <summary>
    /// The renderer runs while the wallpaper isn't paused, and while lyrics float even when it is: Pause wallpaper
    /// freezes the wallpapers (PerformanceOptions.PauseWallpaper) but leaves the floating lyrics going, as on the Mac.
    /// </summary>
    private bool RendererRuns(AppSettings settings) => !settings.WallpaperPaused || FloatsAnywhere(settings);

    private void DisposeFloating()
    {
        lock (_floatGate)
        {
            _floatPauseTimer?.Dispose();
            _floatPauseTimer = null;
        }
    }
}
