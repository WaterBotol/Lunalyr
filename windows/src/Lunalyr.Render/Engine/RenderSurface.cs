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
    /// <summary>The window; the floating surface's changes size and monitor in place (same handle).</summary>
    public SurfaceWindow Window { get; set; } = window;
    public bool IsFloating => Window.Mode == AttachMode.Floating;
    /// <summary>The floating window this surface draws (null for a wallpaper or the preview window).</summary>
    public FloatingPanel? Panel;
    // The scene with this surface's own style (SceneFor), kept while neither changes.
    public Scene.SceneSnapshot? LookSource, LookScene;
    public Scene.LyricStyle? LookStyle;
    public int Width => Window.Width;
    public int Height => Window.Height;
    public float Scale => Window.Monitor.Scale;

    // GPU side, recreated after device loss.
    public GpuDevice? Gpu;
    public IDXGISwapChain2? Swap;
    /// <summary>The DirectComposition target and visual a composition swap chain is shown through.</summary>
    public SurfaceComposition? Composition;
    /// <summary>The swap chain carries alpha: a see-through background shows the desktop beneath.</summary>
    public bool SeeThrough;
    public nint Waitable;
    public ID2D1Bitmap1? Target;
    public bool Hdr;
    public bool AdapterDrivesMonitor;

    // Layers.
    public LayerCrossfade<LayerSlot>? Backgrounds;
    public LayerSlot? Lyric;
    /// <summary>The floating window's shade, stage shadow and edit chrome (the floating surface only).</summary>
    public Floating.FloatingFrameDrawer? FloatingDrawer;
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
    /// <summary>Presents that failed in a row on a window that still exists (a game changing the display mode, say).</summary>
    public int PresentFailures;
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
