using Lunalyr.App.Hosting;
using Lunalyr.Core.Compositions;
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
/// The song title's box on the Settings picture, with handles (the Mac's <c>TitleCardHandles</c> and
/// <c>FloatingTitleHandles</c>, 3276613 and dece022). On the desktop: drag the box to move the title card, drag its
/// top-right corner to scale it (60–160%) from its lower-left corner; kept on screen. With the lyrics floating and the
/// title kept on screen: drag the row along the floating window and to its top or foot (whichever half it is dropped
/// in), drag its corner to scale it. Saved as it moves; the picture stands for the main display (or, floating, the
/// display the window is on).
/// </summary>
internal sealed class TitlePreviewHandles
{
    private const double GripSize = 10;
    private static readonly TimeSpan LiveStep = TimeSpan.FromMilliseconds(90);

    private readonly AppHost _host;
    private readonly HandleCanvas _canvas = new() { Visibility = Visibility.Collapsed };
    private readonly Border _outline;
    private readonly Rectangle _grip;
    private readonly TextBlock _label;
    private Drag? _drag;
    private (double Width, double Height)? _desktop;
    private DateTime _desktopAt;
    private FloatingLyricsOptions? _windowFor;
    private DateTime _windowAt;
    private (FloatingDisplay Geometry, FloatingRect Frame)? _window;

    /// <summary>What a drag started from: the desktop card's box (desktop points) or the row's box on the picture.</summary>
    private sealed record Drag(bool Row, bool Resizing, Windows.Foundation.Point Pointer, (double X, double Y, double Width, double Height) Box,
                               OpeningTitlePlacement Placement, OpeningTitleRow RowPlace, (double X, double Y, double Width, double Height) Window,
                               (double Width, double Height) Desktop, double BaseSize)
    {
        public DateTime LastSent { get; set; }
        public OpeningTitlePlacement NowPlacement { get; set; }
        public OpeningTitleRow NowRow { get; set; }
    }

    public TitlePreviewHandles(AppHost host)
    {
        _host = host;
        var accent = SettingsKit.Resource<Brush>("AccentFillColorDefaultBrush");
        _outline = new Border
        {
            BorderBrush = accent, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255)),
        };
        AutomationProperties.SetName(_outline, "Song title: drag to move, drag its corner to resize");
        ToolTipService.SetToolTip(_outline, "Drag to move the song title; drag its corner to resize it");
        _outline.PointerPressed += (_, e) => Begin(e, resizing: false);
        _outline.PointerEntered += (_, _) => _canvas.Pointer(InputSystemCursor.Create(InputSystemCursorShape.SizeAll));
        _grip = new Rectangle
        {
            Width = GripSize, Height = GripSize, RadiusX = 2, RadiusY = 2,
            Fill = new SolidColorBrush(Colors.White), Stroke = accent, StrokeThickness = 1.5,
        };
        _grip.PointerPressed += (_, e) => Begin(e, resizing: true);
        _grip.PointerEntered += (_, _) => _canvas.Pointer(InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest));
        _label = new TextBlock
        {
            Text = "Song title", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsHitTestVisible = false,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(217, 255, 255, 255)),
        };
        AutomationProperties.SetAccessibilityView(_label, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        _canvas.Children.Add(_outline);
        _canvas.Children.Add(_label);
        _canvas.Children.Add(_grip);
        _canvas.PointerMoved += Moved;
        _canvas.PointerReleased += (_, e) => End(e);
        _canvas.PointerCaptureLost += (_, _) => End(null);
    }

    /// <summary>Laid over the preview's picture, the same size (above the floating window's handles).</summary>
    public UIElement Overlay => _canvas;

    /// <summary>Puts the box where the title is now (UI thread; from each preview frame). Hidden while the title is off.</summary>
    public void Refresh()
    {
        if (_drag is not null) return;
        var settings = _host.Settings.Current;
        double width = _canvas.ActualWidth, height = _canvas.ActualHeight;
        if (!settings.OpeningTitle || width <= 0 || height <= 0)
        {
            _canvas.Visibility = Visibility.Collapsed;
            return;
        }
        var options = _host.Controller?.Floating;
        if (options is { Enabled: true })
        {
            // Floating: only a title kept on screen has a row to place.
            if (!settings.OpeningTitleAlways || Window(options) is not { } where)
            {
                _canvas.Visibility = Visibility.Collapsed;
                return;
            }
            var window = WindowOnPicture(where.Geometry, where.Frame, width, height);
            Show(RowOnPicture(window, where.Geometry, where.Frame, width, options.TextSize, settings.TitleRow()), corner: settings.TitleRow().Bottom ? 0 : 1);
            return;
        }
        var desktop = Desktop();
        var box = OpeningTitle.Box(desktop.Width, desktop.Height, BaseSize(desktop), 1, settings.TitlePlacement());
        Show((box.X / desktop.Width * width, box.Y / desktop.Height * height, box.Width / desktop.Width * width, box.Height / desktop.Height * height),
             corner: -1);
    }

    /// <summary>Shows the box (picture pixels) with its grip at the top-right corner (-1), or at the right of its foot (0) or top (1).</summary>
    private void Show((double X, double Y, double Width, double Height) box, int corner)
    {
        _canvas.Visibility = Visibility.Visible;
        Canvas.SetLeft(_outline, box.X);
        Canvas.SetTop(_outline, box.Y);
        _outline.Width = Math.Max(4, box.Width);
        _outline.Height = Math.Max(4, box.Height);
        var gripY = corner switch { -1 => box.Y, 0 => box.Y, _ => box.Y + box.Height };
        Canvas.SetLeft(_grip, box.X + box.Width - GripSize / 2);
        Canvas.SetTop(_grip, gripY - GripSize / 2);
        Canvas.SetLeft(_label, box.X + 2);
        Canvas.SetTop(_label, Math.Max(2, box.Y - 17));
    }

    private void Begin(PointerRoutedEventArgs e, bool resizing)
    {
        var settings = _host.Settings.Current;
        double width = _canvas.ActualWidth, height = _canvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var pointer = e.GetCurrentPoint(_canvas).Position;
        var options = _host.Controller?.Floating;
        if (options is { Enabled: true })
        {
            if (Window(options) is not { } where) return;
            var window = WindowOnPicture(where.Geometry, where.Frame, width, height);
            var row = settings.TitleRow();
            _drag = new Drag(true, resizing, pointer, RowOnPicture(window, where.Geometry, where.Frame, width, options.TextSize, row),
                             OpeningTitlePlacement.Standard, row, window, default, 0) { NowRow = row };
        }
        else
        {
            var desktop = Desktop();
            var baseSize = BaseSize(desktop);
            var placement = settings.TitlePlacement();
            _drag = new Drag(false, resizing, pointer, OpeningTitle.Box(desktop.Width, desktop.Height, baseSize, 1, placement), placement,
                             OpeningTitleRow.Standard, default, desktop, baseSize) { NowPlacement = placement };
        }
        _canvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag) return;
        double width = _canvas.ActualWidth, height = _canvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var point = e.GetCurrentPoint(_canvas).Position;
        double dx = point.X - drag.Pointer.X, dy = point.Y - drag.Pointer.Y;
        if (drag.Row)
        {
            drag.NowRow = drag.Resizing
                ? OpeningTitle.RowResized(drag.RowPlace, drag.Box.Width, dx)
                : OpeningTitle.RowMoved(drag.RowPlace, dx, point.Y, drag.Window);
        }
        else
        {
            // Picture pixels to points on the desktop.
            drag.NowPlacement = OpeningTitle.Dragged(drag.Box, drag.Placement, dx / width * drag.Desktop.Width, dy / height * drag.Desktop.Height,
                                                     drag.Resizing, drag.Desktop.Width, drag.Desktop.Height, drag.BaseSize);
            var box = OpeningTitle.Box(drag.Desktop.Width, drag.Desktop.Height, drag.BaseSize, 1, drag.NowPlacement);
            Show((box.X / drag.Desktop.Width * width, box.Y / drag.Desktop.Height * height,
                  box.Width / drag.Desktop.Width * width, box.Height / drag.Desktop.Height * height), corner: -1);
        }
        // The wallpaper and the preview follow, a step at a time.
        if (DateTime.UtcNow - drag.LastSent >= LiveStep)
        {
            drag.LastSent = DateTime.UtcNow;
            Save(drag);
        }
    }

    private void End(PointerRoutedEventArgs? e)
    {
        if (_drag is not { } drag) return;
        _drag = null;
        if (e is not null) _canvas.ReleasePointerCapture(e.Pointer);
        Save(drag);
        Refresh();
    }

    private void Save(Drag drag) =>
        _host.Settings.Update(s => drag.Row ? s.WithTitleRow(drag.NowRow) : s.WithTitlePlacement(drag.NowPlacement));

    /// <summary>The main display in points (read again every few seconds: reading the displays isn't free).</summary>
    private (double Width, double Height) Desktop()
    {
        if (_desktop is { } known && DateTime.UtcNow - _desktopAt < TimeSpan.FromSeconds(5)) return known;
        var monitors = DesktopReader.Monitors();
        var main = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        var found = main is null ? (1920.0, 1080.0) : (main.Bounds.Width / main.Scale, main.Bounds.Height / main.Scale);
        _desktop = (Math.Max(1, found.Item1), Math.Max(1, found.Item2));
        _desktopAt = DateTime.UtcNow;
        return _desktop.Value;
    }

    private static double BaseSize((double Width, double Height) desktop) => ViewportMetrics.For(desktop.Width, desktop.Height, 1).BaseSize;

    private (FloatingDisplay Geometry, FloatingRect Frame)? Window(FloatingLyricsOptions options)
    {
        if (!ReferenceEquals(options, _windowFor) || DateTime.UtcNow - _windowAt > TimeSpan.FromSeconds(2))
        {
            _windowFor = options;
            _windowAt = DateTime.UtcNow;
            _window = FloatingWindowHost.Where(options) is { } found ? (found.Geometry, found.Frame) : null;
        }
        return _window;
    }

    /// <summary>The floating window's frame (points on its display, y up) on the picture.</summary>
    private static (double X, double Y, double Width, double Height) WindowOnPicture(FloatingDisplay geometry, FloatingRect frame,
                                                                                     double width, double height)
    {
        var screen = geometry.ScreenFrame;
        return (frame.MinX / screen.Width * width, (screen.Height - frame.MaxY) / screen.Height * height,
                frame.Width / screen.Width * width, frame.Height / screen.Height * height);
    }

    /// <summary>
    /// The row's box on the picture (the Mac's <c>FloatingTitleHandles.rowBox</c>): its size estimated from the words
    /// (the real row hugs them), placed in the window as the window places it.
    /// </summary>
    private (double X, double Y, double Width, double Height) RowOnPicture((double X, double Y, double Width, double Height) window,
                                                                           FloatingDisplay geometry, FloatingRect frame, double pictureWidth,
                                                                           double textSize, OpeningTitleRow row)
    {
        var scene = _host.Controller?.Scene;
        var title = scene?.Title ?? "Song Title";
        var artist = scene?.Artist ?? "Artist";
        var unit = pictureWidth / geometry.ScreenFrame.Width;
        var size = textSize * OpeningTitle.RowScale(row.Scale);
        var rowWidth = size * (OpeningTitle.RowCover + 0.2 + OpeningTitle.RowTitle * 0.55 * title.Length
                               + (artist.Length == 0 ? 0 : 0.16 + OpeningTitle.RowArtist * 0.5 * artist.Length));
        var placed = OpeningTitle.RowFrame(frame.Width, frame.Height, rowWidth, size * OpeningTitle.RowCover, textSize, row.Centre, row.Bottom);
        return (window.X + placed.X * unit, window.Y + placed.Y * unit, placed.Width * unit, placed.Height * unit);
    }
}
