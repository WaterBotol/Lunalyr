using Lunalyr.App.Hosting;
using Lunalyr.Core.Floating;
using Lunalyr.Platform.Desktop;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Lunalyr.App.Settings;

/// <summary>
/// The floating lyrics' outline on the Settings preview, with handles (the Mac's preview, relayed 15 Nov 2026): drag
/// inside to move the floating window, drag a handle to resize it. The real window follows as it moves; a resize is kept
/// for the layout showing and scales its text, while the place stays shared by every layout (AppHost.PlaceFloating). The
/// preview stands for the whole display the window is on.
/// </summary>
internal sealed class FloatingPreviewHandles
{
    private const double HandleSize = 10;
    private static readonly TimeSpan LiveStep = TimeSpan.FromMilliseconds(90);

    private readonly AppHost _host;
    private readonly HandleCanvas _canvas = new() { Visibility = Visibility.Collapsed };
    private readonly Border _outline;
    private readonly (Rectangle Handle, FloatingEdges Edges)[] _handles;
    private Drag? _drag;

    private sealed record Drag(string DisplayKey, FloatingDisplay Geometry, FloatingRect Start, Windows.Foundation.Point Pointer,
                               FloatingEdges? Edges, double StartText)
    {
        public DateTime LastSent { get; set; }
        public FloatingRect Now { get; set; }
    }

    public FloatingPreviewHandles(AppHost host)
    {
        _host = host;
        var accent = SettingsKit.Resource<Brush>("AccentFillColorDefaultBrush");
        _outline = new Border
        {
            BorderBrush = accent, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 255, 255, 255)),
        };
        AutomationProperties.SetName(_outline, "Floating lyrics: drag to move, drag a corner or edge to resize");
        _outline.PointerPressed += (_, e) => Begin(e, null);
        _canvas.Children.Add(_outline);
        FloatingEdges[] edges =
        [
            FloatingEdges.Left | FloatingEdges.Top, FloatingEdges.Top, FloatingEdges.Right | FloatingEdges.Top, FloatingEdges.Right,
            FloatingEdges.Right | FloatingEdges.Bottom, FloatingEdges.Bottom, FloatingEdges.Left | FloatingEdges.Bottom, FloatingEdges.Left,
        ];
        _handles = [.. edges.Select(edge =>
        {
            var handle = new Rectangle
            {
                Width = HandleSize, Height = HandleSize, RadiusX = 2, RadiusY = 2,
                Fill = new SolidColorBrush(Colors.White), Stroke = accent, StrokeThickness = 1.5,
            };
            handle.PointerPressed += (_, e) => Begin(e, edge);
            handle.PointerEntered += (_, _) => Cursor(edge);
            _canvas.Children.Add(handle);
            return (handle, edge);
        })];
        _outline.PointerEntered += (_, _) => Cursor(null);
        _canvas.PointerMoved += Moved;
        _canvas.PointerReleased += (_, e) => End(e);
        _canvas.PointerCaptureLost += (_, _) => End(null);
    }

    /// <summary>Laid over the preview's picture, the same size.</summary>
    public UIElement Overlay => _canvas;

    /// <summary>Puts the outline where the floating window is now (UI thread; from each preview frame). Hidden unless floating.</summary>
    public void Refresh()
    {
        if (_drag is not null) return;
        var options = _host.Controller?.Floating;
        if (options is not { Enabled: true })
        {
            _canvas.Visibility = Visibility.Collapsed;
            return;
        }
        // Reading the displays isn't free: again only when the options change, or every couple of seconds.
        if (!ReferenceEquals(options, _shownFor) || DateTime.UtcNow - _shownAt > TimeSpan.FromSeconds(2))
        {
            _shownFor = options;
            _shownAt = DateTime.UtcNow;
            _shown = FloatingWindowHost.Where(options) is { } found ? (found.Geometry, found.Frame) : null;
        }
        if (_shown is not { } where)
        {
            _canvas.Visibility = Visibility.Collapsed;
            return;
        }
        _canvas.Visibility = Visibility.Visible;
        Place(where.Geometry, where.Frame);
    }

    private FloatingLyricsOptions? _shownFor;
    private DateTime _shownAt;
    private (FloatingDisplay Geometry, FloatingRect Frame)? _shown;

    private void Place(FloatingDisplay geometry, FloatingRect frame)
    {
        double width = _canvas.ActualWidth, height = _canvas.ActualHeight;
        var screen = geometry.ScreenFrame;
        if (width <= 0 || height <= 0 || screen.Width <= 0 || screen.Height <= 0) return;
        double x = frame.MinX / screen.Width * width, y = (screen.Height - frame.MaxY) / screen.Height * height;
        double w = frame.Width / screen.Width * width, h = frame.Height / screen.Height * height;
        Canvas.SetLeft(_outline, x);
        Canvas.SetTop(_outline, y);
        _outline.Width = Math.Max(4, w);
        _outline.Height = Math.Max(4, h);
        foreach (var (handle, edges) in _handles)
        {
            var hx = edges.HasFlag(FloatingEdges.Left) ? x : edges.HasFlag(FloatingEdges.Right) ? x + w : x + w / 2;
            var hy = edges.HasFlag(FloatingEdges.Top) ? y : edges.HasFlag(FloatingEdges.Bottom) ? y + h : y + h / 2;
            Canvas.SetLeft(handle, hx - HandleSize / 2);
            Canvas.SetTop(handle, hy - HandleSize / 2);
        }
    }

    private void Begin(PointerRoutedEventArgs e, FloatingEdges? edges)
    {
        var options = _host.Controller?.Floating;
        if (options is not { Enabled: true } || FloatingWindowHost.Where(options) is not { } where) return;
        _drag = new Drag(where.Display.Key, where.Geometry, where.Frame, e.GetCurrentPoint(_canvas).Position, edges, options.TextSize)
        {
            Now = where.Frame,
        };
        _canvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag) return;
        double width = _canvas.ActualWidth, height = _canvas.ActualHeight;
        var screen = drag.Geometry.ScreenFrame;
        if (width <= 0 || height <= 0) return;
        var point = e.GetCurrentPoint(_canvas).Position;
        // Preview pixels to points on the display, y up.
        var dx = (point.X - drag.Pointer.X) / width * screen.Width;
        var dy = -(point.Y - drag.Pointer.Y) / height * screen.Height;
        drag.Now = drag.Edges is { } edges
            ? FloatingFrame.Resized(drag.Start, dx, dy, edges, drag.Geometry.Area)
            : FloatingFrame.Dragged(drag.Start, dx, dy, resizing: false, drag.Geometry.Area);
        Place(drag.Geometry, drag.Now);
        // The real window follows, a step at a time.
        if (DateTime.UtcNow - drag.LastSent >= LiveStep)
        {
            drag.LastSent = DateTime.UtcNow;
            Send(drag, quiet: true);
        }
    }

    private void End(PointerRoutedEventArgs? e)
    {
        if (_drag is not { } drag) return;
        _drag = null;
        if (e is not null) _canvas.ReleasePointerCapture(e.Pointer);
        Send(drag, quiet: false);
    }

    private void Send(Drag drag, bool quiet)
    {
        double? ratio = drag.Edges is not null && drag.Start.Height > 0 ? drag.Now.Height / drag.Start.Height : null;
        _host.PlaceFloating(drag.DisplayKey, drag.Now, ratio, drag.StartText, quiet);
    }

    private void Cursor(FloatingEdges? edges)
    {
        var shape = edges switch
        {
            FloatingEdges.Left or FloatingEdges.Right => InputSystemCursorShape.SizeWestEast,
            FloatingEdges.Top or FloatingEdges.Bottom => InputSystemCursorShape.SizeNorthSouth,
            FloatingEdges.Left | FloatingEdges.Top or FloatingEdges.Right | FloatingEdges.Bottom => InputSystemCursorShape.SizeNorthwestSoutheast,
            FloatingEdges.Right | FloatingEdges.Top or FloatingEdges.Left | FloatingEdges.Bottom => InputSystemCursorShape.SizeNortheastSouthwest,
            _ => InputSystemCursorShape.SizeAll,
        };
        _canvas.Pointer(InputSystemCursor.Create(shape));
    }
}

/// <summary>A canvas that can set its own pointer cursor (UIElement.ProtectedCursor is only settable from a subclass).</summary>
internal sealed partial class HandleCanvas : Canvas
{
    public void Pointer(InputCursor cursor) => ProtectedCursor = cursor;
}
