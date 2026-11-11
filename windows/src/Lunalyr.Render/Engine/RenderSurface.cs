using Lunalyr.Platform.Desktop;
using Lunalyr.Render.Devices;
using Lunalyr.Render.Pacing;
using Lunalyr.Render.Scene;
using Vortice.Direct2D1;
using Vortice.DXGI;

namespace Lunalyr.Render.Engine;

/// <summary>A layer instance on one surface. A class, so the crossfade tells two instances of the same id apart.</summary>
internal sealed class LayerSlot(string id, IRenderLayer layer)
{
    public string Id { get; } = id;
    public IRenderLayer Layer { get; } = layer;
    public bool HasResources { get; set; }
    public bool Broken { get; set; }
    public int Errors { get; set; }
}

/// <summary>One monitor's wallpaper (or the preview window) on the render thread.</summary>
internal sealed class RenderSurface(int index, SurfaceWindow window)
{
    public int Index { get; } = index;
    public SurfaceWindow Window { get; } = window;
    public int Width => Window.Width;
    public int Height => Window.Height;
    public float Scale => Window.Monitor.Scale;

    // GPU side, recreated after device loss.
    public GpuDevice? Gpu;
    public IDXGISwapChain2? Swap;
    public nint Waitable;
    public ID2D1Bitmap1? Target;
    public bool Hdr;
    public bool AdapterDrivesMonitor;

    // Layers.
    public LayerCrossfade<LayerSlot>? Backgrounds;
    public LayerSlot? Lyric;
    public readonly List<LayerCrossfade<LayerSlot>.Drawn> Drawn = new(4);

    // Pacing and state.
    public SurfaceState State = SurfaceState.Drawing;
    public PauseReason Reason = PauseReason.None;
    public PacingPlan Plan = new(1, 0);
    public FrameRateCap Rate = FrameRateCap.Fps30;
    public double NextDue;
    public double LastFrameWall = double.NaN;
    public bool NeedsFrame = true;
    /// <summary>The current swap chain has presented at least once.</summary>
    public bool Presented;
    public bool Idle;
    /// <summary>Present failed for a reason other than device loss (the window went away): skipped until the next rebuild.</summary>
    public bool Dead;
    public List<CaptureRequest>? Captures;

    public double ExpectedIntervalMs => Plan.IsGated ? Plan.GatePeriod * 1000 : Plan.SyncInterval * 1000.0 / Math.Max(1, Window.Monitor.RefreshHz);

    public IEnumerable<LayerSlot> AllLayers()
    {
        if (Backgrounds is not null) foreach (var e in Backgrounds.Entries) yield return e.Layer;
        if (Lyric is not null) yield return Lyric;
    }

    public SurfaceStatus Status() => new(Index, Window.Monitor.DeviceName, Width, Height, Scale, Window.Monitor.RefreshHz, Window.Mode,
        Gpu?.Name ?? "", AdapterDrivesMonitor, Hdr, State, Reason, Rate, Plan.SyncInterval, Plan.GatePeriod, Idle);
}

internal sealed class CaptureRequest(int monitorIndex)
{
    public int MonitorIndex { get; } = monitorIndex;
    public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
