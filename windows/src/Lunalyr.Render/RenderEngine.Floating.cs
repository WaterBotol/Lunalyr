// The floating lyrics on the engine (the Mac's FloatingLyricsController's drawing side): one more surface per floating
// display (the Mac's 0da0eef7: a window on each display whose lyrics float), on the GPU that drives its monitor, drawn
// by the same render thread, pacing, power caps and device-loss recovery as the wallpapers, with the lyric composition
// chosen for them. Which wallpapers draw lyrics is set per monitor (SetDesktopLyrics): a display can show them on its
// wallpaper, floating, both, or not at all.

using System.Numerics;
using Lunalyr.Core.Floating;
using Lunalyr.Platform.Desktop;
using Lunalyr.Render.Engine;
using Lunalyr.Render.Floating;
using Lunalyr.Render.Pacing;
using Lunalyr.Render.Scene;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.Render;

/// <summary>A drag in edit mode placed the floating lyrics on a display.</summary>
public sealed class FloatingPlacedEventArgs(string displayKey, FloatingRect relative, double? heightRatio = null, bool titleCard = false) : EventArgs
{
    /// <summary>The song title card's window was placed, not the lyrics'.</summary>
    public bool TitleCard { get; } = titleCard;

    /// <summary>The display's lasting key.</summary>
    public string DisplayKey { get; } = displayKey;

    /// <summary>The frame relative to the display, in points, y up (the Mac's convention, as settings keep it).</summary>
    public FloatingRect Relative { get; } = relative;

    /// <summary>After a resize, the new height over the old (the text scales with it); null after a move.</summary>
    public double? HeightRatio { get; } = heightRatio;
}

/// <summary>
/// One surface's own look, where it differs from the shared scene and layers: its lyric style (the floating style, or
/// another monitor's), its lyric composition, and for a wallpaper its background and adjustments (the Mac's per-monitor
/// looks). Null members follow the shared ones.
/// </summary>
public sealed record SurfaceLook(LyricStyle? Style = null, string? LyricId = null, string? BackgroundId = null,
    WallpaperAdjustments? Adjustments = null);

/// <summary>One floating window: its options, its window thread, and its fade and stage on the render thread.</summary>
internal sealed class FloatingPanel(string display, bool titleCard = false)
{
    /// <summary>The display's key (empty for the main display), as the options name it.</summary>
    public string Display { get; } = display;

    /// <summary>The song title card's window on that display, rather than the lyrics' (the Mac's b11a1ab3).</summary>
    public bool TitleCard { get; } = titleCard;

    /// <summary>The title card's shade last traced (LUNALYR_FLOAT_TRACE).</summary>
    public double TraceShade = -1;

    public volatile FloatingLyricsOptions Options = FloatingLyricsOptions.Off;
    public FloatingWindowHost? Host;
    public volatile bool Shown, PointerOver;
    public volatile RenderEngine.FloatingLook Look = RenderEngine.DefaultFloatingLook;

    // Render thread.
    public RenderSurface? Surface;
    public double Opacity, FadeFrom, FadeTo, FadeAt;
    public bool WasShown, FadeOutReported;
    public SceneSnapshot? SceneSource;
    public SceneSnapshot? Scene;
    public (double TextSize, double Backdrop, bool Reduce, double Behind, int Inset) SceneKey;

    // Stall diagnostics (render thread).
    public double TraceLast, TraceSong = double.NaN, SongStill, LastStallWarning = double.NegativeInfinity;
    public bool TraceOver;
    public int FramesShown;
}

public sealed partial class RenderEngine
{
    /// <summary><see cref="CaptureAsync"/>'s index for the first floating lyrics window; the next ones count down from it.</summary>
    public const int FloatingCaptureIndex = -1;

    internal sealed record FloatingLook(bool ReduceTransparency, Vector4 Accent);

    internal static readonly FloatingLook DefaultFloatingLook = new(false, new Vector4(0, 0.47f, 0.83f, 1));

    // Every floating window made this run, in the order first asked for. A window whose display stops floating stays
    // (hidden, its options disabled) so it can fade out, and comes back if that display floats again. Swapped whole
    // under _lifecycle; the render thread reads the array it finds.
    private volatile FloatingPanel[] _panels = [];

    // Which wallpapers draw lyrics: all (the default), none, or all but some monitors (by device name).
    private volatile DesktopLyricsChoice _desktopLyrics = DesktopLyricsChoice.Everywhere;

    // Each surface's own look: the floating windows' by display key, the wallpapers' by monitor device name.
    private volatile SurfaceLooks _looks = SurfaceLooks.None;

    private sealed record DesktopLyricsChoice(bool Nowhere, IReadOnlySet<string> Without)
    {
        public static readonly DesktopLyricsChoice Everywhere = new(false, new HashSet<string>());
        public static readonly DesktopLyricsChoice NowhereAtAll = new(true, new HashSet<string>());

        public bool Draws(string device) => !Nowhere && !Without.Contains(device);
    }

    private sealed record SurfaceLooks(IReadOnlyDictionary<string, SurfaceLook> Floating, IReadOnlyDictionary<string, SurfaceLook> Desktop)
    {
        public static readonly SurfaceLooks None = new(new Dictionary<string, SurfaceLook>(), new Dictionary<string, SurfaceLook>());
    }

    /// <summary>A drag in edit mode ended (raised on a pool thread): keep the frame for that display.</summary>
    public event EventHandler<FloatingPlacedEventArgs>? FloatingPlaced;

    /// <summary>Done was pressed in a floating window (raised on a pool thread).</summary>
    public event EventHandler? FloatingEditDone;

    /// <summary>The first floating lyrics surface as of the last state change, or null when there is none.</summary>
    public SurfaceStatus? FloatingStatus { get; private set; }

    /// <summary>The first floating lyrics window (0 when there is none): for read-only checks of its styles.</summary>
    public nint FloatingWindow
    {
        get
        {
            foreach (var p in _panels) if (p.Host?.Hwnd is { } hwnd and not 0) return hwnd;
            return 0;
        }
    }

    /// <summary>
    /// The floating lyrics with one window (any thread): Enabled moves the lyrics off every wallpaper onto the window,
    /// as before displays had their own choice.
    /// </summary>
    public void SetFloating(FloatingLyricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SetDesktopLyricsChoice(options.Enabled ? DesktopLyricsChoice.NowhereAtAll : DesktopLyricsChoice.Everywhere);
        SetFloating(options.Enabled ? [options] : []);
    }

    /// <summary>
    /// The floating windows (any thread): one for each enabled entry, on the display its <see
    /// cref="FloatingLyricsOptions.Display"/> names (the first of two naming the same display wins). A window no longer
    /// listed fades out and hides. Which wallpapers draw lyrics is <see cref="SetDesktopLyrics"/>'s to say.
    /// </summary>
    public void SetFloating(IReadOnlyList<FloatingLyricsOptions> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        // One window per display and kind: the lyrics', and the song title card's.
        static string KeyOf(string display, bool titleCard) => titleCard ? display + "\u0001title" : display;
        var wanted = new Dictionary<string, FloatingLyricsOptions>(StringComparer.Ordinal);
        foreach (var o in windows)
            if (o.Enabled) wanted.TryAdd(KeyOf(o.Display, o.TitleCard), o);
        lock (_lifecycle)
        {
            var panels = _panels.ToList();
            foreach (var p in panels)
                if (!wanted.ContainsKey(KeyOf(p.Display, p.TitleCard)) && p.Options.Enabled)
                    p.Options = p.Options with { Enabled = false, Shown = false, Editing = false, Placeholder = false };
            foreach (var (key, o) in wanted)
            {
                var p = panels.FirstOrDefault(x => KeyOf(x.Display, x.TitleCard) == key);
                if (p is null) panels.Add(p = new FloatingPanel(o.Display, o.TitleCard));
                p.Options = o;
            }
            _panels = [.. panels];
            if (_thread is not null) foreach (var p in panels) StartFloatingHost(p);
        }
        InputChanged();
    }

    /// <summary>
    /// The monitors (by device name) whose wallpaper draws no lyrics, the others drawing them: the displays whose lyrics
    /// only float, or that show the wallpaper alone (any thread).
    /// </summary>
    public void SetDesktopLyrics(IReadOnlyCollection<string> monitorsWithout)
    {
        ArgumentNullException.ThrowIfNull(monitorsWithout);
        SetDesktopLyricsChoice(monitorsWithout.Count == 0 ? DesktopLyricsChoice.Everywhere
            : new DesktopLyricsChoice(false, new HashSet<string>(monitorsWithout, StringComparer.Ordinal)));
    }

    /// <summary>
    /// Each surface's own look (any thread): the floating windows' by display key, the wallpapers' by monitor device
    /// name. Surfaces not named follow the shared scene and layers.
    /// </summary>
    public void SetLooks(IReadOnlyDictionary<string, SurfaceLook> floating, IReadOnlyDictionary<string, SurfaceLook> desktop)
    {
        ArgumentNullException.ThrowIfNull(floating);
        ArgumentNullException.ThrowIfNull(desktop);
        lock (_registryGate)
        {
            foreach (var look in floating.Values.Concat(desktop.Values))
                foreach (var id in new[] { look.LyricId, look.BackgroundId })
                    if (id is not null && !_factories.ContainsKey(id)) throw new ArgumentException($"No layer registered as \"{id}\".", nameof(floating));
        }
        _looks = new SurfaceLooks(new Dictionary<string, SurfaceLook>(floating, StringComparer.Ordinal),
            new Dictionary<string, SurfaceLook>(desktop, StringComparer.Ordinal));
        Post(RefreshLyricSlots);
        Post(() => RefreshBackgrounds());
        InputChanged();
    }

    private void SetDesktopLyricsChoice(DesktopLyricsChoice choice)
    {
        var old = _desktopLyrics;
        _desktopLyrics = choice;
        if (old.Nowhere != choice.Nowhere || !old.Without.SetEquals(choice.Without))
        {
            Post(RefreshLyricSlots);
            Post(() => _log.Info(choice.Nowhere ? "Lyrics float over the apps; the wallpapers draw none"
                : choice.Without.Count == 0 ? "Lyrics on every wallpaper" : $"Lyrics on every wallpaper but {string.Join(", ", choice.Without)}"));
        }
    }

    /// <summary>A PNG of a floating window's back buffer (draws a fresh frame for it, even while hidden): the first, by default.</summary>
    public Task<byte[]> CaptureFloatingAsync(CancellationToken cancellationToken = default) => CaptureAsync(FloatingCaptureIndex, cancellationToken);

    /// <summary>Starts a window's thread once its display floats, or hands it the options. Under <see cref="_lifecycle"/>.</summary>
    private void StartFloatingHost(FloatingPanel panel)
    {
        var options = panel.Options;
        if (panel.Host is { } host)
        {
            host.Update(options);
            return;
        }
        if (!options.Enabled) return;
        host = new FloatingWindowHost(new FloatingSink(this, panel), options, _logger);
        try
        {
            host.Start();
            panel.Host = host;
        }
        catch (Exception ex)
        {
            _log.Error("A floating lyrics window couldn't start; its lyrics stay off meanwhile", ex);
        }
    }

    private void StartFloatingHosts()
    {
        foreach (var p in _panels) StartFloatingHost(p);
    }

    private void StopFloatingHosts()
    {
        foreach (var p in _panels)
        {
            p.Host?.Stop();
            p.Host = null;
            p.Shown = false;
        }
    }

    private bool AnyFloatingShown()
    {
        foreach (var p in _panels) if (p.Shown) return true;
        return false;
    }

    // ---- Render thread ----

    /// <summary>The lyric composition a surface draws now, or null for none.</summary>
    private string? LyricIdFor(RenderSurface s)
    {
        if (s.Panel is { } panel) return _looks.Floating.GetValueOrDefault(panel.Display)?.LyricId ?? _lyricId;
        if (!_desktopLyrics.Draws(s.Window.Monitor.DeviceName)) return null;
        return _looks.Desktop.GetValueOrDefault(s.Window.Monitor.DeviceName)?.LyricId ?? _lyricId;
    }

    /// <summary>A surface's own look, or null for the shared scene and layers.</summary>
    private SurfaceLook? LookOf(RenderSurface s) => s.Panel is { } panel
        ? _looks.Floating.GetValueOrDefault(panel.Display)
        : _looks.Desktop.GetValueOrDefault(s.Window.Monitor.DeviceName);

    /// <summary>The background a wallpaper draws: its own look's, else the shared one.</summary>
    private string BackgroundIdFor(RenderSurface s) =>
        (s.Panel is null ? _looks.Desktop.GetValueOrDefault(s.Window.Monitor.DeviceName)?.BackgroundId : null) ?? _backgroundId;

    /// <summary>
    /// The scene a surface draws: the shared one, with the surface's own style and adjustments if it has them (made again
    /// only when one of them changes).
    /// </summary>
    private SceneSnapshot SceneFor(RenderSurface s, SceneSnapshot scene)
    {
        var look = LookOf(s);
        var style = look?.Style is { } own && !ReferenceEquals(own, scene.Style) ? own : null;
        var adjustments = look?.Adjustments is { } mine && mine != scene.Adjustments ? mine : null;
        if (style is null && adjustments is null) return scene;
        if (!ReferenceEquals(scene, s.LookSource) || !ReferenceEquals(look, s.LookUsed) || s.LookScene is null)
        {
            s.LookSource = scene;
            s.LookUsed = look;
            s.LookScene = scene with { Style = style ?? scene.Style, Adjustments = adjustments ?? scene.Adjustments };
        }
        return s.LookScene;
    }

    /// <summary>A surface's lyric layer made again when the composition it should draw changed (or it should draw none).</summary>
    private void RefreshLyricSlot(RenderSurface s)
    {
        var id = LyricIdFor(s);
        if (s.Lyric?.Id == id) return;
        if (s.Lyric is { } old) DisposeSlot(old);
        s.Lyric = id is null ? null : NewSlot(id);
        CreateLayerResources(s);
        s.NeedsFrame = true;
    }

    private void RefreshLyricSlots()
    {
        foreach (var s in _surfaces) RefreshLyricSlot(s);
    }

    /// <summary>A floating window was made, resized or moved to another monitor.</summary>
    private void SetFloatingWindow(FloatingPanel panel, SurfaceWindow window)
    {
        if (_stopping) return;
        var s = panel.Surface;
        if (s is not null && s.Window.Hwnd != window.Hwnd)
        {
            TearDownFloating(panel);
            s = null;
        }
        if (s is null)
        {
            s = new RenderSurface(-1, window) { Panel = panel };
            try
            {
                AcquireGpu(s);
            }
            catch (Exception ex)
            {
                _log.Error("Couldn't create the floating lyrics' swap chain", ex);
            }
            var id = LyricIdFor(s);
            s.Lyric = id is null ? null : NewSlot(id);
            CreateLayerResources(s);
            panel.Surface = s;
            _surfaces.Add(s);
            _log.Info($"Floating lyrics: {window.Width}x{window.Height} on {window.Monitor.DeviceName} @{window.Monitor.Dpi} dpi "
                + $"{window.Monitor.RefreshHz} Hz, {s.Gpu?.Name ?? "no GPU"}");
        }
        else if (s.Window.Monitor.DeviceName != window.Monitor.DeviceName || s.Window.Monitor.Dpi != window.Monitor.Dpi)
        {
            // Another monitor (maybe another GPU) or another scale: a fresh swap chain there.
            ReleaseGpu(s);
            s.Window = window;
            TryReacquire(s);
            foreach (var slot in s.AllLayers()) if (slot.HasResources) slot.Layer.Resize(new Vector2(s.Width, s.Height), s.Scale);
            _log.Info($"Floating lyrics moved to {window.Monitor.DeviceName} ({window.Width}x{window.Height} @{window.Monitor.Dpi} dpi)");
        }
        else if (s.Width != window.Width || s.Height != window.Height)
        {
            ResizeFloating(s, window);
        }
        else
        {
            s.Window = window;
        }
        UpdateStates(Now());
        PublishStatus();
        s.NeedsFrame = true;
    }

    private void ResizeFloating(RenderSurface s, SurfaceWindow window)
    {
        s.Window = window;
        if (s.Gpu is not { } gpu || s.Swap is null)
        {
            TryReacquire(s);
            return;
        }
        try
        {
            gpu.Dc.Target = null;
            s.Target?.Dispose();
            s.Target = null;
            s.Swap.ResizeBuffers(2, (uint)s.Width, (uint)s.Height, Format.B8G8R8A8_UNorm, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
            using var surface = s.Swap.GetBuffer<IDXGISurface>(0);
            s.Target = gpu.Dc.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96,
                    BitmapOptions.Target | BitmapOptions.CannotDraw));
            foreach (var slot in s.AllLayers()) if (slot.HasResources) slot.Layer.Resize(new Vector2(s.Width, s.Height), s.Scale);
            s.Presented = false;
            s.NeedsFrame = true;
        }
        catch (Exception ex)
        {
            _log.Warn("Resizing the floating lyrics' swap chain failed; making a new one", ex);
            ReleaseGpu(s);
            TryReacquire(s);
        }
    }

    private void TearDownFloating(FloatingPanel panel)
    {
        if (panel.Surface is not { } s) return;
        ReleaseGpu(s);
        foreach (var slot in s.AllLayers()) DisposeSlot(slot);
        s.Lyric = null;
        s.FloatingDrawer?.Dispose();
        s.FloatingDrawer = null;
        if (s.Captures is { } captures)
            foreach (var r in captures) r.Completion.TrySetException(new InvalidOperationException("The floating lyrics window went away."));
        s.Captures = null;
        _surfaces.Remove(s);
        panel.Surface = null;
        PublishStatus();
    }

    private void UpdateFloatingFades(double now)
    {
        foreach (var p in _panels) UpdateFloatingFade(p, now);
    }

    /// <summary>
    /// A window's fade (the Mac's panel alpha): in over 0.25 s when shown, out when hidden (then the window is hidden),
    /// down to 18% under the pointer. At once under Reduce Motion, and while nothing can be seen.
    /// </summary>
    private void UpdateFloatingFade(FloatingPanel p, double now)
    {
        var o = p.Options;
        bool shown = p.Shown;
        double target = !(o.Enabled && o.Shown) ? 0
            : p.PointerOver && o.FadesUnderPointer && !o.Editing ? FloatingFrameDrawer.PointerAlpha : 1;
        if (shown && !p.WasShown)
        {
            // Just shown: fade in from nothing.
            p.Opacity = 0;
            p.FadeFrom = 0;
            p.FadeAt = now;
        }
        p.WasShown = shown;
        if (target != p.FadeTo)
        {
            p.FadeFrom = p.Opacity;
            p.FadeTo = target;
            p.FadeAt = now;
            p.FadeOutReported = false;
        }
        var s = p.Surface;
        bool instant = _scene.ReduceMotion || s is null || s.State != SurfaceState.Drawing;
        double before = p.Opacity;
        if (!shown) p.Opacity = 0;
        else if (instant) p.Opacity = target;
        else
        {
            var t = Math.Clamp((now - p.FadeAt) / FloatingFrameDrawer.FadeSeconds, 0, 1);
            p.Opacity = p.FadeFrom + (p.FadeTo - p.FadeFrom) * t;
        }
        if (s is not null && (p.Opacity != before || p.Opacity != target)) s.NeedsFrame = true;
        if (target == 0 && shown && (p.Opacity <= 0 || instant) && !p.FadeOutReported)
        {
            p.FadeOutReported = true;
            p.Host?.FadedOut();
        }
    }

    /// <summary>A floating window's frame: the stage (the lyric, on the floating scene), then the shade, its shadow and the chrome.</summary>
    private void DrawFloating(RenderSurface s, FloatingPanel p, in FrameContext frame)
    {
        WatchFloatingStalls(p, frame);
        var dc = frame.Dc;
        var o = p.Options;
        var look = p.Look;
        var drawer = s.FloatingDrawer ??= new FloatingFrameDrawer();
        drawer.Prepare(dc, _dwrite, frame.Size, frame.Scale, s.Window.Inset);
        bool hasStage = false;
        if (p.Opacity > 0.002 && s.Lyric is { Broken: false, HasResources: true } lyric)
        {
            var stageFrame = frame with { Scene = FloatingScene(p, frame.Scene, o, look, s.Window.Inset) };
            drawer.BeginStage(dc);
            DrawLayer(s, lyric, stageFrame, 1);
            FloatingFrameDrawer.EndStage(dc, s.Target!);
            hasStage = true;
        }
        dc.Clear(new Color4(0, 0, 0, 0));
        // The title card's shade comes and goes with its card (none drawn: none at all).
        var backdrop = !p.TitleCard ? o.Backdrop
            : hasStage && s.Lyric?.Layer is Compositions.LyricCompositionLayer { TitlePresence: var presence } ? o.Backdrop * presence : 0;
        if (FloatTrace && p.TitleCard && (Math.Abs(backdrop - p.TraceShade) > 0.1 || (backdrop == 0) != (p.TraceShade == 0)))
        {
            p.TraceShade = backdrop;
            _log.Info($"FLOATTRACE title card shade {backdrop:0.00} at song {frame.SongSeconds:0.00}");
        }
        drawer.Compose(dc, _dwrite, p.Opacity, backdrop, look.ReduceTransparency, hasStage, o.Editing, o.Placeholder, look.Accent);
    }

    private static readonly bool FloatTrace = Environment.GetEnvironmentVariable("LUNALYR_FLOAT_TRACE") == "1";

    /// <summary>
    /// Diagnostics for a tester's "floating lyrics freeze after a hover" (Apple Music): always on, as warnings (so Copy
    /// diagnostics carries them), at most one per 10 s per window: the window not drawing for over 0.25 s, or the song
    /// clock standing still for over 0.3 s while playing, each with the hover state. LUNALYR_FLOAT_TRACE=1 also logs
    /// every gap over 0.1 s and each hover change.
    /// </summary>
    private void WatchFloatingStalls(FloatingPanel p, in FrameContext frame)
    {
        var at = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        var song = frame.SongSeconds;
        var gap = p.TraceLast > 0 ? at - p.TraceLast : 0;
        bool over = p.PointerOver;
        // The first frames after the window shows (or comes back after a long gap) set up its lines: not a stall.
        p.FramesShown = gap > 1 ? 0 : p.FramesShown + 1;
        var settled = p.FramesShown > 60;
        if (frame.IsPlaying && double.IsFinite(song) && double.IsFinite(p.TraceSong) && gap > 0 && gap < 1)
        {
            // The song clock holding still (or stepping back) while the wall clock moves on.
            p.SongStill = song - p.TraceSong < gap * 0.25 ? p.SongStill + gap : 0;
            if (settled && p.SongStill > 0.3 && at - p.LastStallWarning > 10)
            {
                p.LastStallWarning = at;
                _log.Warn($"Floating lyrics: the song clock stood still for {p.SongStill * 1000:0} ms while playing (pointer {(over ? "over" : "away")}, opacity {p.Opacity:0.00}, song {song:0.00} s)");
            }
        }
        else p.SongStill = 0;
        if (settled && gap > 0.25 && gap <= 1 && p.Opacity > 0.002 && at - p.LastStallWarning > 10)
        {
            p.LastStallWarning = at;
            _log.Warn($"Floating lyrics: no frame for {gap * 1000:0} ms (pointer {(over ? "over" : "away")}, opacity {p.Opacity:0.00}, song {song:0.00} s)");
        }
        if (FloatTrace)
        {
            if (gap > 0.1) _log.Info($"FLOATTRACE gap {gap * 1000:0} ms, over {over}, opacity {p.Opacity:0.00}, song {song:0.00}");
            if (over != p.TraceOver) _log.Info($"FLOATTRACE pointer {(over ? "over" : "left")} at song {song:0.00}");
        }
        p.TraceOver = over;
        p.TraceLast = at;
        p.TraceSong = song;
    }

    /// <summary>A floating stage's scene, made again only when its scene or the shade changes (never per frame).</summary>
    private SceneSnapshot FloatingScene(FloatingPanel p, SceneSnapshot scene, FloatingLyricsOptions o, FloatingLook look, int inset)
    {
        // What the shade lies over, in steps of 0.05 so the scene is not made again every frame.
        var behind = Math.Round(FloatingBehind(p) * 20) / 20;
        var key = (o.TextSize, o.Backdrop, look.ReduceTransparency, behind, inset);
        if (!ReferenceEquals(scene, p.SceneSource) || key != p.SceneKey || p.Scene is null)
        {
            p.SceneSource = scene;
            p.SceneKey = key;
            p.Scene = FloatingFrameDrawer.SceneFor(scene, o.TextSize, o.Backdrop, look.ReduceTransparency, behind, inset, p.TitleCard);
        }
        return p.Scene;
    }

    /// <summary>
    /// What a floating window floats over (the Mac's a6f7a6d8): with a music video as the wallpaper beneath it (that
    /// monitor's first, then any), the video as the wallpaper's own lyrics take it (its dimming and the scrim, kept
    /// light); otherwise an unknown app window.
    /// </summary>
    private double FloatingBehind(FloatingPanel p)
    {
        var device = p.Surface?.Window.Monitor.DeviceName;
        double? any = null;
        foreach (var s in _surfaces)
            if (s.Panel is null && s.Backgrounds is { } backgrounds
                && backgrounds.Current.Layer is (Backgrounds.MusicVideoLayer or Backgrounds.YouTubeVideoLayer) and IMeasuredStage stage
                && stage.StageUnbacked is { } video)
            {
                if (s.Window.Monitor.DeviceName == device) return video;
                any ??= video;
            }
        return any ?? Core.Colour.LyricLegibility.UnknownBackdropLuminance;
    }

    /// <summary>A floating window's view of the engine (called on the window's thread).</summary>
    private sealed class FloatingSink(RenderEngine engine, FloatingPanel panel) : IFloatingWindowSink
    {
        public void SetWindow(SurfaceWindow window) => engine.Post(() => engine.SetFloatingWindow(panel, window));

        public WaitHandle ReleaseWindow()
        {
            var done = new ManualResetEvent(false);
            if (engine._thread is null || engine._stopping)
            {
                done.Set();
                return done;
            }
            engine.Post(() =>
            {
                engine.TearDownFloating(panel);
                done.Set();
            });
            return done;
        }

        public void WindowShown(bool shown)
        {
            panel.Shown = shown;
            engine.InputChanged();
        }

        public void PointerOver(bool over)
        {
            panel.PointerOver = over;
            engine._wake.Set();
        }

        public void Look(bool reduceTransparency, Vector4 accent)
        {
            panel.Look = new FloatingLook(reduceTransparency, accent);
            engine.InputChanged();
        }

        public void Placed(string displayKey, FloatingRect relative, double? heightRatio) => Raise(() =>
            engine.FloatingPlaced?.Invoke(engine, new FloatingPlacedEventArgs(displayKey, relative, heightRatio, panel.TitleCard)));

        public void EditDone() => Raise(() => engine.FloatingEditDone?.Invoke(engine, EventArgs.Empty));

        private void Raise(Action action) => ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { action(); }
            catch (Exception ex) { engine._log.Error("A floating lyrics handler failed", ex); }
        }, null);
    }
}
