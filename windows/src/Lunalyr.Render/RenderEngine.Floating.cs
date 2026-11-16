// The floating lyrics on the engine (the Mac's FloatingLyricsController's drawing side): one more surface, on the GPU
// that drives its monitor, drawn by the same render thread, pacing, power caps and device-loss recovery as the
// wallpapers, with the lyric composition chosen for them. Only one surface draws lyrics at a time: while they float
// the wallpapers keep their backgrounds and draw none (LyricsPlacement.DrawsLyrics).

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
public sealed class FloatingPlacedEventArgs(string displayKey, FloatingRect relative, double? heightRatio = null) : EventArgs
{
    /// <summary>The display's lasting key.</summary>
    public string DisplayKey { get; } = displayKey;

    /// <summary>The frame relative to the display, in points, y up (the Mac's convention, as settings keep it).</summary>
    public FloatingRect Relative { get; } = relative;

    /// <summary>After a resize, the new height over the old (the text scales with it); null after a move.</summary>
    public double? HeightRatio { get; } = heightRatio;
}

public sealed partial class RenderEngine
{
    /// <summary><see cref="CaptureAsync"/>'s index for the floating lyrics window.</summary>
    public const int FloatingCaptureIndex = -1;

    private volatile FloatingLyricsOptions _floatOptions = FloatingLyricsOptions.Off;
    private FloatingWindowHost? _floatHost;
    private volatile bool _floatingShown, _pointerOver;
    private volatile FloatingLook _floatLook = new(false, new Vector4(0, 0.47f, 0.83f, 1));

    // Render thread.
    private bool _lyricsFloat;
    private double _floatOpacity, _floatFrom, _floatTo, _floatFadeAt;
    private bool _floatWasShown, _fadeOutReported;
    private SceneSnapshot? _floatSource;
    private SceneSnapshot? _floatScene;
    private (double TextSize, double Backdrop, bool Reduce, double Behind) _floatSceneKey;

    private sealed record FloatingLook(bool ReduceTransparency, Vector4 Accent);

    /// <summary>A drag in edit mode ended (raised on a pool thread): keep the frame for that display.</summary>
    public event EventHandler<FloatingPlacedEventArgs>? FloatingPlaced;

    /// <summary>Done was pressed in the floating window (raised on a pool thread).</summary>
    public event EventHandler? FloatingEditDone;

    /// <summary>The floating lyrics' surface as of the last state change, or null when there is none.</summary>
    public SurfaceStatus? FloatingStatus { get; private set; }

    /// <summary>The floating lyrics window (0 when there is none): for read-only checks of its styles.</summary>
    public nint FloatingWindow => _floatHost?.Hwnd ?? 0;

    /// <summary>The floating lyrics' options (any thread). Enabled moves the lyrics off the wallpapers onto the window.</summary>
    public void SetFloating(FloatingLyricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var old = _floatOptions;
        _floatOptions = options;
        if (old.Enabled != options.Enabled) Post(() => ApplyFloatingMode(options.Enabled));
        lock (_lifecycle)
        {
            if (_thread is not null) StartFloatingHost();
        }
        InputChanged();
    }

    /// <summary>A PNG of the floating window's back buffer (draws a fresh frame for it, even while hidden).</summary>
    public Task<byte[]> CaptureFloatingAsync(CancellationToken cancellationToken = default) => CaptureAsync(FloatingCaptureIndex, cancellationToken);

    /// <summary>Starts the window's thread once Floating is chosen, or hands it the options. Under <see cref="_lifecycle"/>.</summary>
    private void StartFloatingHost()
    {
        var options = _floatOptions;
        if (_floatHost is { } host)
        {
            host.Update(options);
            return;
        }
        if (!options.Enabled) return;
        host = new FloatingWindowHost(new FloatingSink(this), options, _logger);
        try
        {
            host.Start();
            _floatHost = host;
        }
        catch (Exception ex)
        {
            _log.Error("The floating lyrics window couldn't start; the lyrics stay off the desktop meanwhile", ex);
        }
    }

    private void StopFloatingHost()
    {
        _floatHost?.Stop();
        _floatHost = null;
        _floatingShown = false;
    }

    // ---- Render thread ----

    /// <summary>The wallpapers drop their lyric layer while the lyrics float, and take it back after.</summary>
    private void ApplyFloatingMode(bool floating)
    {
        if (_lyricsFloat == floating) return;
        _lyricsFloat = floating;
        foreach (var s in _surfaces)
        {
            if (s.IsFloating) continue;
            if (s.Lyric is { } old) DisposeSlot(old);
            s.Lyric = floating || _lyricId is null ? null : NewSlot(_lyricId);
            CreateLayerResources(s);
            s.NeedsFrame = true;
        }
        _log.Info(floating ? "Lyrics float over the apps; the wallpaper draws none" : "Lyrics back on the desktop");
    }

    private RenderSurface? FloatingSurface()
    {
        foreach (var s in _surfaces) if (s.IsFloating) return s;
        return null;
    }

    /// <summary>The window was made, resized or moved to another monitor.</summary>
    private void SetFloatingWindow(SurfaceWindow window)
    {
        if (_stopping) return;
        var s = FloatingSurface();
        if (s is not null && s.Window.Hwnd != window.Hwnd)
        {
            TearDownFloating();
            s = null;
        }
        if (s is null)
        {
            s = new RenderSurface(-1, window);
            try
            {
                AcquireGpu(s);
            }
            catch (Exception ex)
            {
                _log.Error("Couldn't create the floating lyrics' swap chain", ex);
            }
            s.Lyric = _lyricId is null ? null : NewSlot(_lyricId);
            CreateLayerResources(s);
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

    private void TearDownFloating()
    {
        if (FloatingSurface() is not { } s) return;
        ReleaseGpu(s);
        foreach (var slot in s.AllLayers()) DisposeSlot(slot);
        s.Lyric = null;
        s.FloatingDrawer?.Dispose();
        s.FloatingDrawer = null;
        if (s.Captures is { } captures)
            foreach (var r in captures) r.Completion.TrySetException(new InvalidOperationException("The floating lyrics window went away."));
        s.Captures = null;
        _surfaces.Remove(s);
        FloatingStatus = null;
    }

    /// <summary>
    /// The window's fade (the Mac's panel alpha): in over 0.25 s when shown, out when hidden (then the window is hidden),
    /// down to 18% under the pointer. At once under Reduce Motion, and while nothing can be seen.
    /// </summary>
    private void UpdateFloatingFade(double now)
    {
        var o = _floatOptions;
        bool shown = _floatingShown;
        double target = !(o.Enabled && o.Shown) ? 0
            : _pointerOver && o.FadesUnderPointer && !o.Editing ? FloatingFrameDrawer.PointerAlpha : 1;
        if (shown && !_floatWasShown)
        {
            // Just shown: fade in from nothing.
            _floatOpacity = 0;
            _floatFrom = 0;
            _floatFadeAt = now;
        }
        _floatWasShown = shown;
        if (target != _floatTo)
        {
            _floatFrom = _floatOpacity;
            _floatTo = target;
            _floatFadeAt = now;
            _fadeOutReported = false;
        }
        var s = FloatingSurface();
        bool instant = _scene.ReduceMotion || s is null || s.State != SurfaceState.Drawing;
        double before = _floatOpacity;
        if (!shown) _floatOpacity = 0;
        else if (instant) _floatOpacity = target;
        else
        {
            var t = Math.Clamp((now - _floatFadeAt) / FloatingFrameDrawer.FadeSeconds, 0, 1);
            _floatOpacity = _floatFrom + (_floatTo - _floatFrom) * t;
        }
        if (s is not null && (_floatOpacity != before || _floatOpacity != target)) s.NeedsFrame = true;
        if (target == 0 && shown && (_floatOpacity <= 0 || instant) && !_fadeOutReported)
        {
            _fadeOutReported = true;
            _floatHost?.FadedOut();
        }
    }

    /// <summary>The floating window's frame: the stage (the lyric, on the floating scene), then the shade, its shadow and the chrome.</summary>
    private void DrawFloating(RenderSurface s, in FrameContext frame)
    {
        WatchFloatingStalls(frame);
        var dc = frame.Dc;
        var o = _floatOptions;
        var look = _floatLook;
        var drawer = s.FloatingDrawer ??= new FloatingFrameDrawer();
        drawer.Prepare(dc, _dwrite, frame.Size, frame.Scale);
        bool hasStage = false;
        if (_floatOpacity > 0.002 && s.Lyric is { Broken: false, HasResources: true } lyric)
        {
            var stageFrame = frame with { Scene = FloatingScene(frame.Scene, o, look) };
            drawer.BeginStage(dc);
            DrawLayer(s, lyric, stageFrame, 1);
            FloatingFrameDrawer.EndStage(dc, s.Target!);
            hasStage = true;
        }
        dc.Clear(new Color4(0, 0, 0, 0));
        drawer.Compose(dc, _dwrite, _floatOpacity, o.Backdrop, look.ReduceTransparency, hasStage, o.Editing, o.Placeholder, look.Accent);
    }

    private static readonly bool FloatTrace = Environment.GetEnvironmentVariable("LUNALYR_FLOAT_TRACE") == "1";
    private double _traceLast, _traceSong = double.NaN, _songStill, _lastStallWarning = double.NegativeInfinity;
    private bool _traceOver;
    private int _floatFramesShown;

    /// <summary>
    /// Diagnostics for a tester's "floating lyrics freeze after a hover" (Apple Music): always on, as warnings (so Copy
    /// diagnostics carries them), at most one per 10 s: the window not drawing for over 0.25 s, or the song clock standing
    /// still for over 0.3 s while playing, each with the hover state. LUNALYR_FLOAT_TRACE=1 also logs every gap over
    /// 0.1 s and each hover change.
    /// </summary>
    private void WatchFloatingStalls(in FrameContext frame)
    {
        var at = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        var song = frame.SongSeconds;
        var gap = _traceLast > 0 ? at - _traceLast : 0;
        // The first frames after the window shows (or comes back after a long gap) set up its lines: not a stall.
        _floatFramesShown = gap > 1 ? 0 : _floatFramesShown + 1;
        var settled = _floatFramesShown > 60;
        if (frame.IsPlaying && double.IsFinite(song) && double.IsFinite(_traceSong) && gap > 0 && gap < 1)
        {
            // The song clock holding still (or stepping back) while the wall clock moves on.
            _songStill = song - _traceSong < gap * 0.25 ? _songStill + gap : 0;
            if (settled && _songStill > 0.3 && at - _lastStallWarning > 10)
            {
                _lastStallWarning = at;
                _log.Warn($"Floating lyrics: the song clock stood still for {_songStill * 1000:0} ms while playing (pointer {(_pointerOver ? "over" : "away")}, opacity {_floatOpacity:0.00}, song {song:0.00} s)");
            }
        }
        else _songStill = 0;
        if (settled && gap > 0.25 && gap <= 1 && _floatOpacity > 0.002 && at - _lastStallWarning > 10)
        {
            _lastStallWarning = at;
            _log.Warn($"Floating lyrics: no frame for {gap * 1000:0} ms (pointer {(_pointerOver ? "over" : "away")}, opacity {_floatOpacity:0.00}, song {song:0.00} s)");
        }
        if (FloatTrace)
        {
            if (gap > 0.1) _log.Info($"FLOATTRACE gap {gap * 1000:0} ms, over {_pointerOver}, opacity {_floatOpacity:0.00}, song {song:0.00}");
            if (_pointerOver != _traceOver) _log.Info($"FLOATTRACE pointer {(_pointerOver ? "over" : "left")} at song {song:0.00}");
        }
        _traceOver = _pointerOver;
        _traceLast = at;
        _traceSong = song;
    }

    /// <summary>The floating stage's scene, made again only when the shared scene or the shade changes (never per frame).</summary>
    private SceneSnapshot FloatingScene(SceneSnapshot scene, FloatingLyricsOptions o, FloatingLook look)
    {
        // What the shade lies over, in steps of 0.05 so the scene is not made again every frame.
        var behind = Math.Round(FloatingBehind() * 20) / 20;
        var key = (o.TextSize, o.Backdrop, look.ReduceTransparency, behind);
        if (!ReferenceEquals(scene, _floatSource) || key != _floatSceneKey || _floatScene is null)
        {
            _floatSource = scene;
            _floatSceneKey = key;
            _floatScene = FloatingFrameDrawer.SceneFor(scene, o.TextSize, o.Backdrop, look.ReduceTransparency, behind);
        }
        return _floatScene;
    }

    /// <summary>
    /// What the floating lyrics float over (the Mac's a6f7a6d8): with a music video as the wallpaper, the video as the
    /// wallpaper's own lyrics take it (its dimming and the scrim, kept light); otherwise an unknown app window.
    /// </summary>
    private double FloatingBehind()
    {
        foreach (var s in _surfaces)
            if (!s.IsFloating && s.Backgrounds is { } backgrounds
                && backgrounds.Current.Layer is (Backgrounds.MusicVideoLayer or Backgrounds.YouTubeVideoLayer) and IMeasuredStage stage
                && stage.StageUnbacked is { } video)
                return video;
        return Core.Colour.LyricLegibility.UnknownBackdropLuminance;
    }

    /// <summary>The floating window's view of the engine (called on the window's thread).</summary>
    private sealed class FloatingSink(RenderEngine engine) : IFloatingWindowSink
    {
        public void SetWindow(SurfaceWindow window) => engine.Post(() => engine.SetFloatingWindow(window));

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
                engine.TearDownFloating();
                done.Set();
            });
            return done;
        }

        public void WindowShown(bool shown)
        {
            engine._floatingShown = shown;
            engine.InputChanged();
        }

        public void PointerOver(bool over)
        {
            engine._pointerOver = over;
            engine._wake.Set();
        }

        public void Look(bool reduceTransparency, Vector4 accent)
        {
            engine._floatLook = new FloatingLook(reduceTransparency, accent);
            engine.InputChanged();
        }

        public void Placed(string displayKey, FloatingRect relative, double? heightRatio) => Raise(() =>
            engine.FloatingPlaced?.Invoke(engine, new FloatingPlacedEventArgs(displayKey, relative, heightRatio)));

        public void EditDone() => Raise(() => engine.FloatingEditDone?.Invoke(engine, EventArgs.Empty));

        private void Raise(Action action) => ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { action(); }
            catch (Exception ex) { engine._log.Error("A floating lyrics handler failed", ex); }
        }, null);
    }
}
