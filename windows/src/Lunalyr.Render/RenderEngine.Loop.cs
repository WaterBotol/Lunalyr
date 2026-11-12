using System.Diagnostics;
using System.Numerics;
using Lunalyr.Platform.Desktop;
using Lunalyr.Render.Devices;
using Lunalyr.Render.Engine;
using Lunalyr.Render.Pacing;
using Lunalyr.Render.Scene;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using static Lunalyr.Render.Engine.RenderNative;

namespace Lunalyr.Render;

// Everything in this file runs on the render thread.
public sealed partial class RenderEngine
{
    private const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);
    private const double HousekeepingSeconds = 0.5, CatalogCheckSeconds = 2, IdlePollSeconds = 0.25, AllPausedPollSeconds = 1;

    private readonly List<RenderSurface> _surfaces = [];
    private readonly Dictionary<long, GpuDevice> _gpus = [];
    private AdapterCatalog _catalog = null!;
    private ID2D1Factory1 _d2d = null!;
    private IDWriteFactory _dwrite = null!;
    private nint _timer;
    private readonly long _epoch = Stopwatch.GetTimestamp();
    private int _surfaceGeneration;
    private int _seenInputVersion;
    private PowerConstraint? _power;
    private int _rebuilds, _recoveries;
    private double _nextHousekeeping, _nextCatalogCheck, _nextStatsLog;
    private double _lastFrameAt = double.NaN;
    private (double Song, bool Playing, double At) _lastClock = (double.NaN, false, 0);
    private (int Frames, double CpuMs) _logWindow, _statusWindow;
    private double _statusWindowStart;
    private RenderSurface[] _due = [];
    private nint[] _waitHandles = [];
    // When each song was first seen, for the opening title (FrameContext.SongJoined). Kept here, not in a layer, so a
    // layout or device change mid-song does not make the song look joined part-way.
    private readonly Lunalyr.Core.Compositions.OpeningTitleMemory _joins = new();

    private double Now() => Stopwatch.GetElapsedTime(_epoch).TotalSeconds;

    private void RenderThread(ManualResetEventSlim ready, Action<Exception> fail)
    {
        try
        {
            _d2d = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
            _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
            _catalog = new AdapterCatalog(_log);
            unsafe { _timer = CreateWaitableTimerEx(0, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS); }
        }
        catch (Exception ex)
        {
            fail(ex);
            ready.Set();
            return;
        }
        ready.Set();
        try
        {
            Loop();
        }
        catch (Exception ex)
        {
            _log.Error("Render loop stopped on an error", ex);
        }
        finally
        {
            TearDownSurfaces();
            // Anything still queued (a release the host waits for) must not be left hanging.
            while (_commands.TryDequeue(out var command))
            {
                try { command(); } catch (Exception) { }
            }
            foreach (var gpu in _gpus.Values) gpu.Dispose();
            _gpus.Clear();
            _catalog.Dispose();
            _dwrite.Dispose();
            _d2d.Dispose();
            if (_timer != 0) CloseHandle(_timer);
            _timer = 0;
        }
    }

    private unsafe void Loop()
    {
        nint wake = _wake.SafeWaitHandle.DangerousGetHandle();
        while (!_stopping)
        {
            while (_commands.TryDequeue(out var command))
            {
                try { command(); }
                catch (Exception ex) { _log.Error("Render command failed", ex); }
            }
            if (_stopping) break;

            double now = Now();
            var (song, playing) = ReadClock(now);
            int inputVersion = Volatile.Read(ref _inputVersion);
            if (inputVersion != _seenInputVersion)
            {
                _seenInputVersion = inputVersion;
                foreach (var s in _surfaces) s.NeedsFrame = true;
            }
            if (now >= _nextHousekeeping) Housekeeping(now);
            UpdateStates(now);

            // Which surfaces draw now, and when the next gated one is due.
            if (_due.Length < _surfaces.Count) _due = new RenderSurface[_surfaces.Count];
            int dueCount = 0;
            double earliest = double.PositiveInfinity;
            bool anyIdle = false;
            foreach (var s in _surfaces)
            {
                if (!WantsFrame(s, now, out double at)) { anyIdle |= s.Idle; continue; }
                if (at <= now + 0.0005) _due[dueCount++] = s;
                else earliest = Math.Min(earliest, at);
            }

            if (dueCount == 0)
            {
                double wait = !double.IsPositiveInfinity(earliest) ? earliest - now : anyIdle ? IdlePollSeconds : AllPausedPollSeconds;
                WaitQuietly(wake, Math.Min(wait, Math.Max(0, _nextHousekeeping - now) + 0.001));
                if (double.IsPositiveInfinity(earliest)) _lastFrameAt = double.NaN; // idle or paused: a gap, not a slow frame
                continue;
            }

            // Wait until the due swap chains can take a frame (frame-latency waitables), or a command arrives.
            if (_waitHandles.Length < dueCount + 1) _waitHandles = new nint[dueCount + 1];
            _waitHandles[0] = wake;
            for (int i = 0; i < dueCount; i++) _waitHandles[i + 1] = _due[i].Waitable;
            uint r;
            fixed (nint* handles = _waitHandles) r = WaitForMultipleObjects((uint)(dueCount + 1), handles, false, 100);
            if (r == WAIT_OBJECT_0 || r == WAIT_TIMEOUT || r > WAIT_OBJECT_0 + (uint)dueCount) continue;
            int first = (int)(r - WAIT_OBJECT_0 - 1);

            long t0 = Stopwatch.GetTimestamp();
            now = Now();
            int drawn = 0;
            double expected = 0;
            for (int i = first; i < dueCount; i++)
            {
                var s = _due[i];
                // The first one's waitable was consumed by the wait; the others only if they are ready too.
                if (i != first && WaitForSingleObject(s.Waitable, 0) != WAIT_OBJECT_0) continue;
                DrawSurface(s, now, song, playing);
                drawn++;
                if (expected == 0) expected = s.ExpectedIntervalMs;
            }
            if (drawn > 0)
            {
                double cpuMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                double interval = double.IsNaN(_lastFrameAt) ? double.NaN : (now - _lastFrameAt) * 1000;
                _recorder.Add(cpuMs, interval, expected);
                _logWindow = (_logWindow.Frames + 1, _logWindow.CpuMs + cpuMs);
                _statusWindow = (_statusWindow.Frames + 1, _statusWindow.CpuMs + cpuMs);
                _lastFrameAt = now;
            }
            RecoverLostDevices();
        }
    }

    private unsafe void WaitQuietly(nint wake, double seconds)
    {
        if (seconds <= 0) return;
        if (seconds < 0.05 && _timer != 0)
        {
            // Sub-tick precision for a gated frame, without raising the system timer resolution.
            long due = -(long)(seconds * 10_000_000);
            SetWaitableTimer(_timer, &due, 0, 0, 0, false);
            nint* handles = stackalloc nint[2] { wake, _timer };
            WaitForMultipleObjects(2, handles, false, 1000);
        }
        else
        {
            WaitForSingleObject(wake, (uint)Math.Clamp(seconds * 1000, 1, 1000));
        }
    }

    private (double Song, bool Playing) ReadClock(double now)
    {
        var clock = _songClock;
        if (clock is null) return (double.NaN, false);
        double song;
        bool playing;
        try
        {
            (song, playing) = clock();
        }
        catch (Exception ex)
        {
            _log.Warn("Song clock failed", ex);
            return (double.NaN, false);
        }
        // Playing/paused flips and seeks are new input for idle and paused surfaces.
        var last = _lastClock;
        if (SongClockChange.IsNewInput(last.Song, last.Playing, song, playing, now - last.At))
        {
            foreach (var s in _surfaces) s.NeedsFrame = true;
            _lastClock = (song, playing, now);
        }
        else if (playing)
        {
            _lastClock = (song, playing, now);
        }
        return (song, playing);
    }

    // ---- Decisions ----

    private void UpdateStates(double now)
    {
        var desktop = _desktop;
        var perf = _performance;
        bool changed = false;
        var power = PowerPolicy.Constraint(perf, desktop?.EnergySaverOn ?? false, desktop?.OnBattery ?? false);
        if (power != _power)
        {
            _log.Info(power is { } p ? $"Power saving: {p.Reason} ({p.Fallback}); frame rate held at 30 fps at most" : "Power saving off");
            _power = power;
            changed = true;
        }
        var rate = PowerPolicy.EffectiveRate(perf.FrameRate, power is not null);
        var system = desktop?.System ?? default;
        bool aligned = desktop is not null && desktop.Generation == _surfaceGeneration;
        for (int i = 0; i < _surfaces.Count; i++)
        {
            var s = _surfaces[i];
            var (state, reason) = s.Window.Mode == Platform.Desktop.AttachMode.Windowed
                ? SurfaceActivity.ForWindow(system, desktop?.WindowVisible ?? true)
                : SurfaceActivity.ForWallpaper(system, aligned && i < desktop!.Monitors.Count ? desktop.Monitors[i] : default,
                    perf.PauseWhenCovered, perf.PauseWallpaper);
            var plan = FramePacing.Plan(s.Window.Monitor.RefreshHz, rate);
            if (plan != s.Plan || rate != s.Rate)
            {
                s.Plan = plan;
                s.Rate = rate;
                s.NextDue = now;
                _log.Debug($"Surface {i}: {rate} at {s.Window.Monitor.RefreshHz} Hz -> sync interval {plan.SyncInterval}"
                    + (plan.IsGated ? $", every {plan.GatePeriod * 1000:0.0} ms" : ""));
                changed = true;
            }
            if (state != s.State || reason != s.Reason)
            {
                _log.Info(state == SurfaceState.Drawing ? $"Surface {i} ({s.Window.Monitor.DeviceName}) drawing"
                    : $"Surface {i} ({s.Window.Monitor.DeviceName}) {state.ToString().ToLowerInvariant()}: {reason}");
                s.State = state;
                s.Reason = reason;
                s.NeedsFrame = true;
                if (state == SurfaceState.Drawing) s.NextDue = now;
                changed = true;
            }
        }
        if (changed) PublishStatus();
    }

    private bool WantsFrame(RenderSurface s, double now, out double at)
    {
        at = now;
        s.Idle = false;
        if (s.Dead || s.Swap is null) return false;
        if (s.Captures is { Count: > 0 }) return true;
        switch (s.State)
        {
            case SurfaceState.Hidden:
                // A swap chain that has never presented shows nothing at all. Under windows or a full-screen app
                // part of it may still peek through (coverage allows slivers), so it gets one still frame; with the
                // displays off or the session locked nothing can be seen and nothing is drawn.
                return !s.Presented && s.Reason is PauseReason.Covered or PauseReason.FullScreenApp;
            case SurfaceState.Frozen:
                return s.NeedsFrame; // one still frame for whatever changed, then nothing
            default:
                if (s.NeedsFrame) return true;
                if (IsIdle(s, now))
                {
                    s.Idle = true;
                    return false;
                }
                if (s.Plan.IsGated) at = s.NextDue;
                return true;
        }
    }

    private bool IsIdle(RenderSurface s, double now)
    {
        bool backgroundAnimates = false;
        if (s.Backgrounds is not null)
            foreach (var e in s.Backgrounds.Entries)
                backgroundAnimates |= !e.Layer.Broken && e.Layer.Layer.IsAnimating;
        bool lyricsAdvance = s.Lyric is { Broken: false } lyric && lyric.Layer.IsAnimating;
        var quiet = FrameClockPolicy.QuietAfter(backgroundAnimates, lyricsAdvance, s.Backgrounds?.FadesEnd(_options.BackgroundFadeSeconds));
        return FrameClockPolicy.IsIdle(quiet, now);
    }

    // ---- Drawing ----

    private void DrawSurface(RenderSurface s, double now, double song, bool playing)
    {
        var gpu = s.Gpu!;
        var dc = gpu.Dc;
        var frame = new FrameContext
        {
            Dc = dc, DWrite = _dwrite, D2DFactory = _d2d, Size = new Vector2(s.Width, s.Height), Scale = s.Scale,
            WallSeconds = now, SongSeconds = song, IsPlaying = playing, Scene = _scene,
            DeltaSeconds = double.IsNaN(s.LastFrameWall) ? 0 : Math.Clamp(now - s.LastFrameWall, 0, 0.25),
            SongJoined = _joins.Joined(_scene.TrackKey, song),
        };
        dc.Target = s.Target;
        dc.BeginDraw();
        dc.Clear(new Color4(0, 0, 0, 1));
        s.Backgrounds?.Layers(now, _options.BackgroundFadeSeconds, s.Drawn);
        foreach (var d in s.Drawn)
            if (d.Opacity > 0) DrawLayer(s, d.Layer, frame, d.Opacity);
        if (s.Lyric is not null) DrawLayer(s, s.Lyric, frame, 1);
        var result = dc.EndDraw();
        if (result.Failure)
        {
            dc.Target = null;
            if (result.Code == D2DERR_RECREATE_TARGET) MarkLost(gpu, "EndDraw: recreate target");
            else _log.Warn($"EndDraw failed: 0x{result.Code:X8}");
            return;
        }
        if (s.Captures is { Count: > 0 }) CaptureBackBuffer(s);
        dc.Target = null;

        var present = s.Swap!.Present(s.Plan.SyncInterval, PresentFlags.None);
        if (present == Vortice.DXGI.ResultCode.DeviceRemoved || present == Vortice.DXGI.ResultCode.DeviceReset)
        {
            MarkLost(gpu, $"Present: 0x{present.Code:X8}, reason 0x{gpu.D3D.DeviceRemovedReason.Code:X8}");
        }
        else if (present.Failure)
        {
            // Explorer took the window with it: stop drawing here; the host re-attaches.
            s.Dead = true;
            _log.Warn($"Present failed on surface {s.Index} (0x{present.Code:X8}); waiting for re-attach");
            _host?.RequestVisibilityCheck();
        }
        else
        {
            s.Presented = true;
        }
        s.LastFrameWall = now;
        s.NeedsFrame = false;
        if (s.Plan.IsGated) s.NextDue = FramePacing.NextDue(s.NextDue, now, s.Plan.GatePeriod);
    }

    private void DrawLayer(RenderSurface s, LayerSlot slot, in FrameContext frame, double opacity)
    {
        if (slot.Broken || !slot.HasResources) return;
        var dc = frame.Dc;
        dc.Transform = Matrix3x2.Identity;
        bool pushed = opacity < 1;
        if (pushed)
        {
            dc.PushLayer(new LayerParameters1
            {
                ContentBounds = new RawRectF(0, 0, s.Width, s.Height), MaskTransform = Matrix3x2.Identity,
                MaskAntialiasMode = AntialiasMode.PerPrimitive, Opacity = (float)opacity, LayerOptions = LayerOptions1.None,
            }, null!);
        }
        try
        {
            slot.Layer.Render(in frame);
        }
        catch (Exception ex)
        {
            // Layers shouldn't throw; one that does is logged sparingly and dropped after many failures.
            if (slot.Errors++ % 600 == 0) _log.Error($"Layer \"{slot.Id}\" failed to render", ex);
            if (slot.Errors > 1200) slot.Broken = true;
        }
        finally
        {
            if (pushed) dc.PopLayer();
            dc.Transform = Matrix3x2.Identity;
        }
    }

    private unsafe void CaptureBackBuffer(RenderSurface s)
    {
        var requests = s.Captures!;
        s.Captures = null;
        try
        {
            var gpu = s.Gpu!;
            using var back = s.Swap!.GetBuffer<ID3D11Texture2D>(0);
            var d = back.Description;
            d.Usage = ResourceUsage.Staging;
            d.BindFlags = BindFlags.None;
            d.CPUAccessFlags = CpuAccessFlags.Read;
            d.MiscFlags = ResourceOptionFlags.None;
            using var staging = gpu.D3D.CreateTexture2D(d);
            gpu.D3DContext.CopyResource(staging, back);
            var map = gpu.D3DContext.Map(staging, 0, MapMode.Read);
            int w = (int)d.Width, h = (int)d.Height, row = w * 4;
            var pixels = new byte[row * h];
            try
            {
                for (int y = 0; y < h; y++)
                    new ReadOnlySpan<byte>((byte*)map.DataPointer + (long)y * map.RowPitch, row).CopyTo(pixels.AsSpan(y * row, row));
            }
            finally
            {
                gpu.D3DContext.Unmap(staging, 0);
            }
            // PNG encoding is slow for a 4K frame: off the render thread.
            _ = Task.Run(() =>
            {
                try
                {
                    var png = PngEncoder.EncodeBgra(pixels, w, h, row);
                    foreach (var r in requests) r.Completion.TrySetResult(png);
                }
                catch (Exception ex)
                {
                    foreach (var r in requests) r.Completion.TrySetException(ex);
                }
            });
        }
        catch (Exception ex)
        {
            foreach (var r in requests) r.Completion.TrySetException(ex);
        }
    }

    private void QueueCapture(CaptureRequest request)
    {
        if (request.MonitorIndex < 0 || request.MonitorIndex >= _surfaces.Count)
        {
            request.Completion.TrySetException(new ArgumentOutOfRangeException("monitorIndex", request.MonitorIndex,
                $"There are {_surfaces.Count} surface(s)."));
            return;
        }
        var s = _surfaces[request.MonitorIndex];
        if (s.Dead || s.Swap is null)
        {
            request.Completion.TrySetException(new InvalidOperationException("That surface has no swap chain right now."));
            return;
        }
        (s.Captures ??= []).Add(request);
    }

    // ---- Surfaces, devices, layers ----

    private void CreateSurfaces(IReadOnlyList<SurfaceWindow> windows, int generation)
    {
        if (_stopping) return;
        TearDownSurfaces();
        _surfaceGeneration = generation;
        Interlocked.Increment(ref _rebuilds);
        double now = Now();
        for (int i = 0; i < windows.Count; i++)
        {
            var s = new RenderSurface(i, windows[i]);
            try
            {
                AcquireGpu(s);
            }
            catch (Exception ex)
            {
                _log.Error($"Couldn't create a swap chain for {s.Window.Monitor.DeviceName}", ex);
            }
            var background = NewSlot(_backgroundId);
            // A wallpaper fades in from its black window once it can draw (the Mac's first-layer fade).
            s.Backgrounds = background is null ? null : new LayerCrossfade<LayerSlot>(background, ready: true, fadingInAt: now);
            s.Lyric = _lyricId is null ? null : NewSlot(_lyricId);
            CreateLayerResources(s);
            _surfaces.Add(s);
            _log.Info($"Surface {i}: {s.Window.Monitor.DeviceName} {s.Width}x{s.Height} @{s.Window.Monitor.Dpi} dpi "
                + $"{s.Window.Monitor.RefreshHz} Hz on {s.Gpu?.Name ?? "no GPU"}{(s.AdapterDrivesMonitor ? "" : " (fallback adapter)")}"
                + (s.Hdr ? ", HDR" : ""));
        }
        _lastFrameAt = double.NaN;
        UpdateStates(now);
        // A paused wallpaper can't run its fade-in: its still frame shows the background at once.
        foreach (var s in _surfaces)
            if (s.State != SurfaceState.Drawing && s.Backgrounds is { Entries.Count: 1 } fade)
                s.Backgrounds = new LayerCrossfade<LayerSlot>(fade.Current, ready: true);
        PublishStatus();
        _surfacesCreated.Set();
    }

    private void TearDownSurfaces()
    {
        foreach (var s in _surfaces)
        {
            ReleaseGpu(s);
            foreach (var slot in s.AllLayers()) DisposeSlot(slot);
            s.Backgrounds = null;
            s.Lyric = null;
            if (s.Captures is { } captures)
                foreach (var r in captures) r.Completion.TrySetException(new InvalidOperationException("The surface was rebuilt."));
            s.Captures = null;
        }
        _surfaces.Clear();
    }

    private void AcquireGpu(RenderSurface s)
    {
        nint monitor = s.Window.Monitor.Handle;
        var adapters = _catalog.Adapters;
        var info = AdapterSelection.For(monitor, adapters) ?? throw new InvalidOperationException("No graphics adapter.");
        if (!_gpus.TryGetValue(info.Luid, out var gpu))
        {
            var adapter = _catalog.AdapterFor(info.Luid) ?? throw new InvalidOperationException("The adapter went away.");
            gpu = GpuDevice.Create(adapter, info.Luid, info.Name, _d2d);
            _gpus[info.Luid] = gpu;
            _log.Info($"Created the D3D11/D2D device on {info.Name}");
        }
        gpu.Users++;
        s.Gpu = gpu;
        s.AdapterDrivesMonitor = AdapterSelection.Drives(info, monitor);
        s.Hdr = _catalog.IsHdr(monitor);
        if (!s.AdapterDrivesMonitor) _log.Warn($"No adapter output shows {s.Window.Monitor.DeviceName}; rendering on {info.Name}");
        CreateSwapChain(s);
    }

    private void CreateSwapChain(RenderSurface s)
    {
        var gpu = s.Gpu!;
        var desc = new SwapChainDescription1
        {
            Width = (uint)s.Width, Height = (uint)s.Height, Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2, BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard, AlphaMode = Vortice.DXGI.AlphaMode.Ignore, Scaling = Scaling.Stretch,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };
        // The swap chain must come from the factory that made the device's adapter.
        using var dxgiDevice = gpu.D3D.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        using var swap1 = factory.CreateSwapChainForHwnd(gpu.D3D, s.Window.Hwnd, desc);
        factory.MakeWindowAssociation(s.Window.Hwnd, WindowAssociationFlags.IgnoreAll);
        s.Swap = swap1.QueryInterface<IDXGISwapChain2>();
        s.Swap.MaximumFrameLatency = 1;
        s.Waitable = s.Swap.FrameLatencyWaitableObject;
        using var surface = s.Swap.GetBuffer<IDXGISurface>(0);
        s.Target = gpu.Dc.CreateBitmapFromDxgiSurface(surface,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore), 96, 96,
                BitmapOptions.Target | BitmapOptions.CannotDraw));
        s.NeedsFrame = true;
        s.Presented = false;
        s.Dead = false;
        s.LastFrameWall = double.NaN;
    }

    private void ReleaseGpu(RenderSurface s)
    {
        foreach (var slot in s.AllLayers()) ReleaseSlotResources(slot);
        if (s.Gpu is { } gpu) gpu.Dc.Target = null;
        s.Target?.Dispose();
        s.Target = null;
        if (s.Waitable != 0) CloseHandle(s.Waitable);
        s.Waitable = 0;
        s.Swap?.Dispose();
        s.Swap = null;
        if (s.Gpu is { } g)
        {
            s.Gpu = null;
            if (--g.Users <= 0)
            {
                _gpus.Remove(g.Luid);
                g.Dispose();
            }
        }
    }

    private LayerSlot? NewSlot(string id)
    {
        IRenderLayerFactory? factory;
        lock (_registryGate) _factories.TryGetValue(id, out factory);
        if (factory is null)
        {
            _log.Warn($"No layer registered as \"{id}\"");
            return null;
        }
        try
        {
            return new LayerSlot(id, factory.Create());
        }
        catch (Exception ex)
        {
            _log.Error($"Creating layer \"{id}\" failed", ex);
            return null;
        }
    }

    private void CreateLayerResources(RenderSurface s)
    {
        if (s.Gpu is null) return;
        foreach (var slot in s.AllLayers())
        {
            if (slot.HasResources || slot.Broken) continue;
            try
            {
                slot.Layer.CreateResources(s.Gpu.Dc, _dwrite, new Vector2(s.Width, s.Height), s.Scale);
                slot.HasResources = true;
            }
            catch (Exception ex)
            {
                slot.Broken = true;
                _log.Error($"Layer \"{slot.Id}\" couldn't create its resources", ex);
            }
        }
    }

    private void ReleaseSlotResources(LayerSlot slot)
    {
        if (!slot.HasResources) return;
        slot.HasResources = false;
        try { slot.Layer.ReleaseResources(); }
        catch (Exception ex) { _log.Warn($"Layer \"{slot.Id}\" failed to release its resources", ex); }
    }

    private void DisposeSlot(LayerSlot slot)
    {
        ReleaseSlotResources(slot);
        try { slot.Layer.Dispose(); }
        catch (Exception ex) { _log.Warn($"Layer \"{slot.Id}\" failed to dispose", ex); }
    }

    private void ApplyLayers(string backgroundId, string? lyricId)
    {
        bool backgroundChanged = backgroundId != _backgroundId, lyricChanged = lyricId != _lyricId;
        _backgroundId = backgroundId;
        _lyricId = lyricId;
        if (!backgroundChanged && !lyricChanged) return;
        double now = Now();
        foreach (var s in _surfaces)
        {
            if (backgroundChanged && NewSlot(backgroundId) is { } slot)
            {
                if (s.Backgrounds is null)
                {
                    s.Backgrounds = new LayerCrossfade<LayerSlot>(slot, ready: true, fadingInAt: now);
                }
                else
                {
                    var before = s.Backgrounds.Entries.Select(e => e.Layer).ToArray();
                    // A paused wallpaper can't run a fade: the still frame shows the new layer at once.
                    s.Backgrounds.Change(slot, ready: true, s.State == SurfaceState.Drawing ? now : double.NegativeInfinity,
                        _options.BackgroundFadeSeconds);
                    DisposeDropped(s, before);
                }
            }
            if (lyricChanged)
            {
                if (s.Lyric is { } old) DisposeSlot(old);
                s.Lyric = lyricId is null ? null : NewSlot(lyricId);
            }
            CreateLayerResources(s);
            s.NeedsFrame = true;
        }
        _log.Info($"Layers: background \"{backgroundId}\", lyrics \"{lyricId ?? "none"}\"");
    }

    private void DisposeDropped(RenderSurface s, LayerSlot[] before)
    {
        foreach (var slot in before)
        {
            bool kept = false;
            if (s.Backgrounds is not null) foreach (var e in s.Backgrounds.Entries) kept |= ReferenceEquals(e.Layer, slot);
            if (!kept) DisposeSlot(slot);
        }
    }

    // ---- Recovery and housekeeping ----

    private void MarkLost(GpuDevice gpu, string why)
    {
        if (gpu.Lost) return;
        gpu.Lost = true;
        _log.Warn($"GPU device on {gpu.Name} lost ({why})");
    }

    private void RecoverLostDevices()
    {
        bool any = false;
        foreach (var gpu in _gpus.Values) any |= gpu.Lost;
        if (!any) return;
        Interlocked.Increment(ref _recoveries);
        var affected = _surfaces.Where(s => s.Gpu is { Lost: true }).ToList();
        foreach (var s in affected) ReleaseGpu(s);
        // Anything else still holding a lost device (none expected) goes too.
        foreach (var lost in _gpus.Values.Where(g => g.Lost).ToList())
        {
            _gpus.Remove(lost.Luid);
            lost.Dispose();
        }
        try
        {
            _catalog.Refresh();
        }
        catch (Exception ex)
        {
            _log.Error("Re-reading the adapters failed", ex);
        }
        foreach (var s in affected) TryReacquire(s);
        _log.Info($"Recovered {affected.Count} surface(s) after device loss");
    }

    private void TryReacquire(RenderSurface s)
    {
        if (s.Gpu is not null || !IsWindow(s.Window.Hwnd)) return;
        try
        {
            AcquireGpu(s);
            CreateLayerResources(s);
            s.NeedsFrame = true;
        }
        catch (Exception ex)
        {
            ReleaseGpu(s);
            _log.Warn($"Recreating the swap chain for surface {s.Index} failed; retrying", ex);
        }
    }

    private void Housekeeping(double now)
    {
        _nextHousekeeping = now + HousekeepingSeconds;

        // Surfaces left without a device (a failed recovery) try again.
        foreach (var s in _surfaces) if (s.Gpu is null && !s.Dead) TryReacquire(s);

        // Release background layers a finished fade covers.
        foreach (var s in _surfaces)
        {
            if (s.Backgrounds is not { Entries.Count: > 1 } fade) continue;
            if (fade.FadesEnd(_options.BackgroundFadeSeconds) is { } end && now < end) continue;
            var before = fade.Entries.Select(e => e.Layer).ToArray();
            fade.Settle(now, _options.BackgroundFadeSeconds);
            DisposeDropped(s, before);
        }

        // A display or adapter change (an HDR switch, a monitor moving to the other GPU) makes the factory stale.
        if (now >= _nextCatalogCheck)
        {
            _nextCatalogCheck = now + CatalogCheckSeconds;
            if (!_catalog.IsCurrent) OnDisplayConfigurationChanged();
        }

        var (frames, cpu) = _logWindow;
        if (now >= _nextStatsLog)
        {
            if (frames > 0 && _nextStatsLog > 0)
            {
                double seconds = _options.StatsLogInterval.TotalSeconds;
                _log.Info($"Render: {frames / seconds:0.0} fps, {cpu / frames:0.00} ms per frame; "
                    + string.Join(", ", _surfaces.Select(s => $"{s.Index}:{s.State}{(s.Idle ? "(idle)" : "")}")));
            }
            _logWindow = default;
            _nextStatsLog = now + _options.StatsLogInterval.TotalSeconds;
        }
        // Running counters only: percentiles sort tens of thousands of samples, far too slow for the render thread.
        var (statusFrames, statusCpu) = _statusWindow;
        double statusSeconds = now - _statusWindowStart;
        _statusWindow = default;
        _statusWindowStart = now;
        _statusLine = $"{(statusSeconds > 0 ? statusFrames / statusSeconds : 0):0.0} fps · {(statusFrames > 0 ? statusCpu / statusFrames : 0):0.00} ms/frame · "
            + string.Join(" | ", _surfaces.Select(s => $"{s.Gpu?.Name ?? "-"} sync {s.Plan.SyncInterval}{(s.Plan.IsGated ? "g" : "")} {s.State}{(s.Reason != PauseReason.None ? $" ({s.Reason})" : "")}"))
            + (_power is { } p ? $" · {p.Reason}" : "");
    }

    private void OnDisplayConfigurationChanged()
    {
        _log.Info("Display configuration changed (adapters, outputs or HDR); re-checking each surface's GPU");
        try
        {
            _catalog.Refresh();
        }
        catch (Exception ex)
        {
            _log.Error("Re-reading the adapters failed", ex);
            return;
        }
        var adapters = _catalog.Adapters;
        foreach (var s in _surfaces)
        {
            if (s.Gpu is null) continue;
            nint monitor = s.Window.Monitor.Handle;
            var info = AdapterSelection.For(monitor, adapters);
            bool hdr = _catalog.IsHdr(monitor);
            if (info is not null && info.Luid == s.Gpu.Luid && hdr == s.Hdr) continue;
            _log.Info($"Surface {s.Index}: now on {info?.Name ?? "no adapter"}{(hdr ? ", HDR" : "")}; recreating its swap chain");
            ReleaseGpu(s);
            TryReacquire(s);
        }
    }

    private void PublishStatus()
    {
        var status = new SurfaceStatus[_surfaces.Count];
        for (int i = 0; i < status.Length; i++) status[i] = _surfaces[i].Status();
        _status = status;
        var handler = StatusChanged;
        if (handler is null) return;
        var args = new RenderStatusChangedEventArgs(status, _power);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { handler(this, args); }
            catch (Exception ex) { _log.Error("A StatusChanged handler failed", ex); }
        }, null);
    }
}
