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
    private (Dictionary<string, SurfaceLook> Floating, Dictionary<string, SurfaceLook> Desktop)? _looksSent;
    // The imported files other monitors draw, each registered once under its own layer id.
    private readonly HashSet<string> _ownCustoms = new(StringComparer.Ordinal);

    // One parked slot: the settings it draws with, and its style and adjustments worked out once.
    private sealed record ParkedSlot(AppSettings Look, Render.Scene.LyricStyle Style, Render.Scene.WallpaperAdjustments Adjustments);

    // The parked slots worked out for one state of the settings, the displays and the song's remembered style (reading
    // a look copies its settings, so this is not done again for every scene published). Floating by display key, desktop
    // by monitor device name.
    private sealed record LooksCache(AppSettings Settings, IReadOnlyList<ConnectedDisplay> Displays, RememberedStyle? Remembered,
        Dictionary<string, ParkedSlot> Floating, Dictionary<string, ParkedSlot> Desktop);

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
            (Dictionary<string, SurfaceLook> Floating, Dictionary<string, SurfaceLook> Desktop)? looks = null;
            bool perDisplay, shown, hasWords, editingPosition;
            AppSettings settings;
            IReadOnlyList<ConnectedDisplay> displays;
            LooksCache? parked = null;
            string? songLayout;
            lock (_gate)
            {
                if (_disposed) return;
                settings = _settings;
                displays = _displays;
                perDisplay = displays.Count > 0;
                bool floats = FloatsAnywhere(settings);
                if (!floats) _floatEditing = false;
                var input = VisibilityInput(settings, _scene, perDisplay ? (floats ? LyricsSurface.Floating : LyricsSurface.Desktop) : settings.Surface);
                hidesAfter = FloatingVisibility.HidesAfter(input);
                hasWords = input.HasTrack && ((input.HasTimedLines && !input.AllInstrumental) || input.HasPlainLines);
                shown = FloatingVisibility.Shows(input);
                editingPosition = _floatEditing;
                songLayout = _layout;
                if (perDisplay)
                {
                    parked = LooksFor(settings, displays);
                    _looks = parked;
                }
            }
            FloatingLyricsOptions Window(bool enabled, string display, string layout) => new()
            {
                Enabled = enabled,
                Shown = shown,
                Editing = editingPosition,
                Placeholder = editingPosition && !hasWords,
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
            // Worked out outside _gate: a background reads the player's monitor, then the controller's lock, as
            // PublishLayers does (CompositionFor takes _gate itself).
            if (!perDisplay)
            {
                single = Window(settings.IsFloating, settings.FloatingDisplay, settings.IsFloating ? CompositionFor(settings) : "");
            }
            else
            {
                looks = SurfaceLooks(settings, displays, parked!, songLayout);
                var main = displays[0].Key;
                var editing = settings.EditingSlot(main);
                foreach (var d in displays)
                {
                    var choice = settings.DisplayChoice(d.Key);
                    if (choice.Floats())
                    {
                        // The window drawing the style being edited draws the scene's own; the others their parked copy.
                        var layout = editing == new LookSlot(d.Key, LyricsSurface.Floating) ? CompositionFor(settings)
                            : looks.Value.Floating[d.Key].LyricId ?? CompositionFor(settings);
                        var window = Window(true, d.Key, layout);
                        windows.Add(window);
                        // The song title card in a window of its own beside the lyrics' box (the Mac's b11a1ab3), shown
                        // with them; it draws what that display's style's opening title would, and its shade goes with it.
                        if (settings.FloatingTitleCard)
                        {
                            windows.Add(window with
                            {
                                TitleCard = true,
                                Shown = FloatingTitleVisibility.Shows(shown, card: true),
                                Placeholder = false,
                                TitleFrames = settings.FloatingTitleFrames,
                                TitleStart = settings.TitleStart(),
                            });
                        }
                    }
                    if (!choice.OnDesktop()) without.Add(d.Device);
                }
                single = windows.FirstOrDefault() ?? FloatingLyricsOptions.Off;
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
                    _looksSent = null;
                    _renderer.SetLooks(new Dictionary<string, SurfaceLook>(), new Dictionary<string, SurfaceLook>());
                    _floatSent = null;
                }
                if (single == _floatSent) return;
                _floatSent = single;
                _renderer.SetFloating(single);
                return;
            }
            _floatSent = single;
            if (looks is { } now && (_looksSent is not { } sent || !SameLooks(sent.Floating, now.Floating) || !SameLooks(sent.Desktop, now.Desktop)))
            {
                _looksSent = now;
                _renderer.SetLooks(now.Floating, now.Desktop);
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
    /// the scene's own. A parked copy reads its style keys from its kept look and every other key through, and a
    /// desktop's carries its monitor's wallpaper. The song's remembered style (style memory) applies to every surface,
    /// as on the Mac. Must hold _gate.
    /// </summary>
    private LooksCache LooksFor(AppSettings settings, IReadOnlyList<ConnectedDisplay> displays)
    {
        var remembered = RememberedLocked(settings)?.Style;
        if (_looks is { } cached && ReferenceEquals(cached.Settings, settings) && ReferenceEquals(cached.Displays, displays)
            && Equals(cached.Remembered, remembered))
            return cached;
        var main = displays[0].Key;
        var editing = settings.EditingSlot(main);
        Dictionary<string, ParkedSlot> floating = new(StringComparer.Ordinal), desktop = new(StringComparer.Ordinal);
        ParkedSlot Parked(string display, LyricsSurface surface)
        {
            var look = StyleMemory.Applied(settings.LookFor(display, surface, main), remembered);
            return new ParkedSlot(look, look.Style(), look.Adjustments());
        }
        foreach (var d in displays)
        {
            if (settings.DisplayChoice(d.Key).Floats() && editing != new LookSlot(d.Key, LyricsSurface.Floating))
                floating[d.Key] = Parked(d.Key, LyricsSurface.Floating);
            // Every monitor draws its own wallpaper, whatever its lyrics do.
            if (editing != new LookSlot(d.Key, LyricsSurface.Desktop)) desktop[d.Device] = Parked(d.Key, LyricsSurface.Desktop);
        }
        return new LooksCache(settings, displays, remembered, floating, desktop);
    }

    /// <summary>
    /// What each surface with a parked look draws now: its style and layout (<paramref name="songLayout"/>, the song's
    /// match, for Custom Mix), and a wallpaper's background and adjustments. Never under _gate: a background reads the
    /// player's monitor first (BackgroundLayerFor).
    /// </summary>
    private (Dictionary<string, SurfaceLook> Floating, Dictionary<string, SurfaceLook> Desktop) SurfaceLooks(AppSettings settings,
        IReadOnlyList<ConnectedDisplay> displays, LooksCache parked, string? songLayout)
    {
        var available = AvailableCompositions();
        string? Composition(AppSettings look)
        {
            var chosen = look.LyricComposition == AutomaticComposition ? songLayout ?? "constellation" : look.LyricComposition;
            return available.Contains(chosen) ? chosen : null;
        }
        var floating = parked.Floating.ToDictionary(p => p.Key, p => new SurfaceLook(p.Value.Style, Composition(p.Value.Look)), StringComparer.Ordinal);
        var desktop = new Dictionary<string, SurfaceLook>(StringComparer.Ordinal);
        for (var i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            if (!parked.Desktop.TryGetValue(d.Device, out var p)) continue;
            var lyrics = settings.DisplayChoice(d.Key).OnDesktop();
            desktop[d.Device] = new SurfaceLook(lyrics ? p.Style : null, lyrics ? Composition(p.Look) : null,
                WallpaperOf(settings, p.Look, primary: i == 0), p.Adjustments);
        }
        return (floating, desktop);
    }

    /// <summary>
    /// The background a monitor's own look draws. A music video plays on the primary display only (one YouTube player,
    /// as the Mac keeps it to the main display): another monitor draws its "Without a video" type. An imported file
    /// other than the edited monitor's gets a layer of its own.
    /// </summary>
    private string WallpaperOf(AppSettings settings, AppSettings look, bool primary)
    {
        if (!primary && look.BackgroundMode == "musicVideo") look = look with { BackgroundMode = look.MusicVideoFallback };
        var id = BackgroundLayerFor(look);
        if (id != "custom" || (look.WallpaperPath == settings.WallpaperPath && look.WallpaperPlacement == settings.WallpaperPlacement)) return id;
        var own = "custom." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(look.WallpaperPath + "\n" + look.WallpaperPlacement)))[..12].ToLowerInvariant();
        if (_ownCustoms.Add(own))
            _renderer.ReplaceLayer(new Render.Backgrounds.CustomLayerFactory(look.WallpaperPath,
                Core.Colour.WallpaperPlacements.Parse(look.WallpaperPlacement), log: _logger, id: own));
        return own;
    }

    private static bool SameLooks(IReadOnlyDictionary<string, SurfaceLook> a, IReadOnlyDictionary<string, SurfaceLook> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && p.Value == other);

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
