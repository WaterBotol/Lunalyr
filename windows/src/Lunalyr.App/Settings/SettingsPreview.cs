using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Lunalyr.App.Hosting;
using Lunalyr.Render.Scene;
using Lunalyr.Shell;
using Lunalyr.Shell.Rendering;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Lunalyr.App.Settings;

/// <summary>
/// The live preview at the top of Lyric style (the Mac's <c>SettingsLivePreview</c>): what the wallpaper shows now, with
/// the settings being changed, drawn offscreen by the wallpaper's own layers on a background thread at 15 frames a
/// second and shown in an image. It draws only while its page is showing and the window is visible.
/// </summary>
internal sealed class SettingsPreview : IDisposable
{
    public const int PixelWidth = 640, PixelHeight = 360;
    private static readonly TimeSpan FrameTime = TimeSpan.FromMilliseconds(1000 / 15.0);

    private readonly AppHost _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<bool> _windowVisible;
    private readonly WriteableBitmap _bitmap = new(PixelWidth, PixelHeight);
    private readonly byte[][] _buffers = [new byte[PixelWidth * PixelHeight * 4], new byte[PixelWidth * PixelHeight * 4]];
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly TextBlock _note;
    private readonly FloatingPreviewHandles _handles;
    private readonly TitlePreviewHandles _titles;
    private readonly bool _titleHandles;
    private readonly Thread _thread;
    private volatile bool _active;
    private volatile bool _disposed;
    private volatile bool _visible = true;
    private int _pending;

    private SettingsPreview(AppHost host, DispatcherQueue dispatcher, Func<bool> windowVisible, double width, bool titleHandles)
    {
        _titleHandles = titleHandles;
        _host = host;
        _dispatcher = dispatcher;
        _windowVisible = windowVisible;
        // At most `width` wide, narrower when the page is (Stretch keeps the 16:9 shape): a fixed width ran past the page's
        // edge in a narrow window.
        var image = new Image { Source = _bitmap, Stretch = Stretch.Uniform, MaxWidth = width, MaxHeight = width * PixelHeight / PixelWidth };
        AutomationProperties.SetName(image, "Live wallpaper preview");
        AutomationProperties.SetHelpText(image, "What the wallpaper shows now, with the settings below.");
        _note = SettingsKit.Caption("Starting…");
        _note.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetAccessibilityView(_note, AccessibilityView.Raw);
        var stack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        // The floating lyrics' outline and handles, over the picture while the lyrics float.
        _handles = new FloatingPreviewHandles(host);
        // The song title's box, over them: dragged to move and resize the title card or the floating title row.
        _titles = new TitlePreviewHandles(host);
        var picture = new Grid();
        picture.Children.Add(image);
        picture.Children.Add(_handles.Overlay);
        if (titleHandles) picture.Children.Add(_titles.Overlay);
        var clip = new Border { CornerRadius = new CornerRadius(6), Child = picture, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(clip);
        stack.Children.Add(_note);
        View = new Border
        {
            Style = SettingsKit.Resource<Style>("SettingsPreviewFrame"),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack,
        };
        _thread = new Thread(Loop) { IsBackground = true, Name = "Lunalyr settings preview", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public UIElement View { get; }

    /// <summary>
    /// A preview, or null when this build can't draw one. <paramref name="width"/>: how wide it shows, in DIPs (the
    /// welcome's is smaller). <paramref name="titleHandles"/>: the song title's box can be dragged on it (Lyric style's).
    /// </summary>
    public static SettingsPreview? TryCreate(AppHost host, DispatcherQueue dispatcher, Func<bool> windowVisible, double width = PixelWidth,
                                             bool titleHandles = true)
    {
        try
        {
            return new SettingsPreview(host, dispatcher, windowVisible, width, titleHandles);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            host.Log.App.Warn("The settings preview couldn't start.", ex);
            return null;
        }
    }

    /// <summary>Draws while its page shows; holds still otherwise.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active) _wake.Set();
    }

    /// <summary>The window was minimised, hidden or shown again (read on the UI thread).</summary>
    public void UpdateVisibility()
    {
        _visible = _windowVisible();
        if (_visible) _wake.Set();
    }

    private void Loop()
    {
        SettingsPreviewRenderer? renderer = null;
        var clock = Stopwatch.StartNew();
        var buffer = 0;
        try
        {
            while (!_disposed)
            {
                if (!_active || !_visible)
                {
                    // Off its page, the preview lets its device and layers go (about 100 MB); they come back when it shows.
                    if (!_active && renderer is not null)
                    {
                        renderer.Dispose();
                        renderer = null;
                    }
                    _wake.Wait(TimeSpan.FromSeconds(1));
                    _wake.Reset();
                    continue;
                }

                var started = clock.Elapsed;
                if (Volatile.Read(ref _pending) == 0)
                {
                    var controller = _host.Controller;
                    var readings = _host.Readings;
                    if (controller is not null && readings is not null)
                    {
                        renderer ??= new SettingsPreviewRenderer(PixelWidth, PixelHeight, WallpaperLayers.Factories(readings.WordBreaker, _host.Log));
                        var settings = _host.Settings.Current;
                        // As the wallpaper draws it: a layout remembered for the music playing wins.
                        var composition = controller.ShownComposition;
                        var (seconds, playing) = controller.ReadClock();
                        var scene = controller.Scene;
                        var pixels = _buffers[buffer];
                        renderer.Use(controller.CustomWallpaper);
                        renderer.Use(controller.MotionCover);
                        renderer.Render(scene, controller.BackgroundLayer, composition, seconds, playing, clock.Elapsed.TotalSeconds, pixels);
                        Show(pixels, NoteFor(scene, settings.WallpaperPaused));
                        buffer = 1 - buffer;
                    }
                }

                var wait = FrameTime - (clock.Elapsed - started);
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log.App.Warn("The settings preview stopped.", ex);
            _dispatcher.TryEnqueue(() => _note.Text = "The preview stopped; the wallpaper itself is unaffected.");
        }
        finally
        {
            renderer?.Dispose();
        }
    }

    private static string NoteFor(SceneSnapshot scene, bool paused)
    {
        var what = scene.Title is { } title ? $"Live preview · {title}" : "Live preview · nothing playing";
        return paused ? what + " · the wallpaper itself is paused" : what;
    }

    private void Show(byte[] pixels, string note)
    {
        Interlocked.Exchange(ref _pending, 1);
        var queued = _dispatcher.TryEnqueue(() =>
        {
            try
            {
                if (_disposed) return;
                using (var stream = _bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
                _bitmap.Invalidate();
                if (_note.Text != note) _note.Text = note;
                _handles.Refresh();
                if (_titleHandles) _titles.Refresh();
            }
            finally
            {
                Volatile.Write(ref _pending, 0);
            }
        });
        if (!queued) Volatile.Write(ref _pending, 0);
    }

    public void Dispose()
    {
        _disposed = true;
        _active = false;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
