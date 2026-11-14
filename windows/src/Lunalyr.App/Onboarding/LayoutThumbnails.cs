using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Lunalyr.App.Hosting;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Onboarding;
using Lunalyr.Shell.Rendering;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Lunalyr.App.Onboarding;

/// <summary>
/// The look step's layout tiles, drawn by the wallpaper's own composition layers (the Mac draws its
/// <c>LayoutThumbnail</c> through <c>LyricLineView</c>, as the wallpaper does): the sample line over the sample palette,
/// on a background thread into one small software surface, read into a bitmap per tile. Every tile holds the line just
/// sung; the chosen one plays it <see cref="LayoutThumbnail.Plays"/> times at 20 frames a second first, when it may
/// move. The surface and its layers go when the tiles do (<see cref="Dispose"/>).
/// </summary>
internal sealed class LayoutThumbnails : IDisposable
{
    /// <summary>A tile, in DIPs (the Mac's 96 × 56 points), drawn at twice that in pixels.</summary>
    public const double Width = 96, Height = 56;

    private const int PixelWidth = 192, PixelHeight = 112;

    private static readonly TimeSpan FrameTime = TimeSpan.FromMilliseconds(50);

    private readonly AppHost _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, WriteableBitmap> _bitmaps = new(StringComparer.Ordinal);
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Lock _gate = new();
    private readonly Thread _thread;
    private readonly byte[][] _playing = [new byte[PixelWidth * PixelHeight * 4], new byte[PixelWidth * PixelHeight * 4]];
    private IReadOnlyList<string> _layouts = [];
    private string? _selected;
    private bool _animate;
    private int _version;
    private int _pending;
    private volatile bool _disposed;

    public LayoutThumbnails(AppHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Lunalyr welcome layouts", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>The bitmap a layout's tile shows (UI thread).</summary>
    public WriteableBitmap Bitmap(string layout)
    {
        if (!_bitmaps.TryGetValue(layout, out var bitmap))
        {
            bitmap = new WriteableBitmap(PixelWidth, PixelHeight);
            _bitmaps[layout] = bitmap;
        }
        return bitmap;
    }

    /// <summary>Draws these tiles; the chosen one plays first when <paramref name="animate"/> (UI thread).</summary>
    public void Show(IReadOnlyList<string> layouts, string? selected, bool animate)
    {
        foreach (var layout in layouts) Bitmap(layout);
        lock (_gate)
        {
            _layouts = [.. layouts];
            _selected = selected;
            _animate = animate;
            _version++;
        }
        _wake.Set();
    }

    /// <summary>Stops playing: every tile holds (the window went inactive, or the tab changed).</summary>
    public void Hold()
    {
        lock (_gate)
        {
            if (!_animate) return;
            _animate = false;
            _version++;
        }
        _wake.Set();
    }

    private void Loop()
    {
        SettingsPreviewRenderer? renderer = null;
        var scene = LayoutThumbnail.Scene();
        var clock = Stopwatch.StartNew();
        var drawnVersion = 0;
        var wall = 0.0;
        var started = TimeSpan.Zero;
        var buffer = 0;
        try
        {
            while (!_disposed)
            {
                IReadOnlyList<string> layouts;
                string? selected;
                bool animate;
                int version;
                lock (_gate)
                {
                    (layouts, selected, animate, version) = (_layouts, _selected, _animate, _version);
                }
                var readings = _host.Readings;
                if (layouts.Count == 0 || readings is null)
                {
                    Sleep(TimeSpan.FromMilliseconds(250));
                    continue;
                }
                // A small software surface: no second GPU device beside the look step's live preview.
                renderer ??= new SettingsPreviewRenderer(PixelWidth, PixelHeight, WallpaperLayers.Factories(readings.WordBreaker, _host.Log), hardware: false,
                    layoutHeight: LayoutThumbnail.StageHeight);

                if (version != drawnVersion)
                {
                    drawnVersion = version;
                    started = clock.Elapsed;
                    foreach (var layout in layouts) Post(layout, Still(renderer, scene, layout, ref wall), counts: false);
                }

                var elapsed = (clock.Elapsed - started).TotalSeconds;
                if (animate && selected is not null && elapsed < LayoutThumbnail.Plays * LayoutThumbnail.Cycle)
                {
                    var frameStart = clock.Elapsed;
                    if (Volatile.Read(ref _pending) == 0)
                    {
                        var pixels = _playing[buffer];
                        wall += FrameTime.TotalSeconds;
                        renderer.Render(scene, "gradient", selected, LayoutThumbnail.Time(elapsed), playing: true, wall, pixels);
                        Post(selected, pixels, counts: true);
                        buffer = 1 - buffer;
                    }
                    var wait = FrameTime - (clock.Elapsed - frameStart);
                    if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                    continue;
                }
                if (animate && selected is not null)
                {
                    // Played out: back to the line just sung, then still.
                    Post(selected, Still(renderer, scene, selected, ref wall), counts: false);
                    lock (_gate)
                    {
                        if (_version == version) _animate = false;
                    }
                }
                Sleep(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log.App.Warn("The welcome's layout tiles stopped.", ex);
        }
        finally
        {
            renderer?.Dispose();
        }

        void Sleep(TimeSpan time)
        {
            _wake.Wait(time);
            _wake.Reset();
        }
    }

    /// <summary>
    /// The line just sung (<see cref="LayoutThumbnail.StillTime"/>), as the layer draws it after playing up to it: a layer sets a line up over a few frames and
    /// its words arrive with the wall clock, so the second before is played through first.
    /// </summary>
    private static byte[] Still(SettingsPreviewRenderer renderer, SceneSnapshot scene, string layout, ref double wall)
    {
        var pixels = new byte[PixelWidth * PixelHeight * 4];
        const int Frames = 12;
        for (var i = 0; i < Frames; i++)
        {
            wall += 0.1;
            renderer.Render(scene, "gradient", layout, LayoutThumbnail.StillTime - (Frames - 1 - i) * 0.1, playing: true, wall, pixels);
        }
        return pixels;
    }

    private void Post(string layout, byte[] pixels, bool counts)
    {
        if (counts) Interlocked.Exchange(ref _pending, 1);
        var queued = _dispatcher.TryEnqueue(() =>
        {
            try
            {
                if (_disposed || !_bitmaps.TryGetValue(layout, out var bitmap)) return;
                using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
                bitmap.Invalidate();
            }
            finally
            {
                if (counts) Volatile.Write(ref _pending, 0);
            }
        });
        if (!queued && counts) Volatile.Write(ref _pending, 0);
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
