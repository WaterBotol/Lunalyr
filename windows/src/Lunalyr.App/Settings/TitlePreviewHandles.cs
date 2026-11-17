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
/// top-right corner to scale it (60–160%) from its lower-left corner; kept on screen. With the lyrics floating, the song
/// title card's own window (the Mac's b11a1ab3): drag it to move it, drag its lower-right corner to resize it, no smaller
/// than its least and kept on the display; its frame is kept for the first floating display. Saved as it moves; the
/// picture stands for a desktop laid out as the preview draws it (or, floating, the display the window is on).
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
    private InputSystemCursorShape _gripCursor = InputSystemCursorShape.SizeNortheastSouthwest;
    private FloatingLyricsOptions? _windowFor;
    private DateTime _windowAt;
    private (string Display, FloatingDisplay Geometry, FloatingRect Frame)? _window;
    private object? _framesFor, _startFor;

    /// <summary>What a drag started from: the desktop card's box (desktop points) or the row's box on the picture.</summary>
    private sealed record Drag(bool Resizing, Windows.Foundation.Point Pointer, (double X, double Y, double Width, double Height) Box,
                               OpeningTitlePlacement Placement, (double Width, double Height) Desktop, double BaseSize)
    {
        public DateTime LastSent { get; set; }
        public OpeningTitlePlacement NowPlacement { get; set; }

        /// <summary>The floating title card's window being placed: its display and frame (relative to it) at the start.</summary>
        public (string DisplayKey, FloatingDisplay Geometry, FloatingRect Start)? Card { get; init; }
        public FloatingRect NowCard { get; set; }
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
        _grip.PointerEntered += (_, _) => _canvas.Pointer(InputSystemCursor.Create(_gripCursor));
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
        var options = _host.Controller?.Floating;
        // Floating, the title card's window is placed, while it shows.
        if (!settings.OpeningTitle || (options is { Enabled: true } && !settings.FloatingTitleCard))
        {
            _canvas.Visibility = Visibility.Collapsed;
            return;
        }
        // Shown first: a collapsed canvas is never laid out, so it would have no size to place the box in.
        _canvas.Visibility = Visibility.Visible;
        double width = _canvas.ActualWidth, height = _canvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            _outline.Visibility = _grip.Visibility = _label.Visibility = Visibility.Collapsed;
            return;
        }
        _outline.Visibility = _grip.Visibility = _label.Visibility = Visibility.Visible;
        if (options is { Enabled: true })
        {
            if (CardWindow(options, settings) is not { } where)
            {
                _canvas.Visibility = Visibility.Collapsed;
                return;
            }
            Show(WindowOnPicture(where.Geometry, where.Frame, width, height), corner: 2);
            return;
        }
        var desktop = Desktop();
        var box = OpeningTitle.Box(desktop.Width, desktop.Height, BaseSize(desktop), 1, settings.TitlePlacement());
        Show((box.X / desktop.Width * width, box.Y / desktop.Height * height, box.Width / desktop.Width * width, box.Height / desktop.Height * height),
             corner: -1);
    }

    /// <summary>Shows the box (picture pixels) with its grip at the top-right corner (-1) or the lower-right one (2).</summary>
    private void Show((double X, double Y, double Width, double Height) box, int corner)
    {
        _canvas.Visibility = Visibility.Visible;
        _label.Text = corner == 2 ? "Song title card" : "Song title";
        _gripCursor = corner == 2 ? InputSystemCursorShape.SizeNorthwestSoutheast : InputSystemCursorShape.SizeNortheastSouthwest;
        Canvas.SetLeft(_outline, box.X);
        Canvas.SetTop(_outline, box.Y);
        _outline.Width = Math.Max(4, box.Width);
        _outline.Height = Math.Max(4, box.Height);
        var gripY = corner == 2 ? box.Y + box.Height : box.Y;
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
            if (CardWindow(options, settings) is not { } where) return;
            _drag = new Drag(resizing, pointer, default, OpeningTitlePlacement.Standard, default, 0)
            {
                Card = (where.Display, where.Geometry, where.Frame), NowCard = where.Frame,
            };
        }
        else
        {
            var desktop = Desktop();
            var baseSize = BaseSize(desktop);
            var placement = settings.TitlePlacement();
            _drag = new Drag(resizing, pointer, OpeningTitle.Box(desktop.Width, desktop.Height, baseSize, 1, placement), placement,
                             desktop, baseSize) { NowPlacement = placement };
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
        if (drag.Card is { } card)
        {
            drag.NowCard = CardDragged(card.Start, card.Geometry, dx, dy, width, height, drag.Resizing);
            Show(WindowOnPicture(card.Geometry, drag.NowCard, width, height), corner: 2);
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
        if ((drag.Card is null || drag.NowCard != drag.Card.Value.Start) && DateTime.UtcNow - drag.LastSent >= LiveStep)
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
        // A click that moved nothing leaves the card following the lyrics' box.
        if (drag.Card is not { } card || drag.NowCard != card.Start) Save(drag);
        Refresh();
    }

    private void Save(Drag drag) =>
        _host.Settings.Update(s => drag.Card is { } card ? s.WithTitleFrame(drag.NowCard, card.DisplayKey) : s.WithTitlePlacement(drag.NowPlacement));

    /// <summary>
    /// The title card's window moved or resized by a drag on the picture (the Mac's FloatingTitleHandles): picture pixels to
    /// points on the display, y up; no smaller than its least, kept on the display.
    /// </summary>
    private static FloatingRect CardDragged(FloatingRect start, FloatingDisplay geometry, double dx, double dy, double pictureWidth,
                                            double pictureHeight, bool resizing) =>
        FloatingTitleFrame.DraggedOnPicture(start, geometry.ScreenFrame, geometry.Area, dx, dy, pictureWidth, pictureHeight, resizing);

    /// <summary>
    /// The desktop the picture stands for, in points: the preview lays the wallpaper out as a 1080-line display
    /// (SettingsPreviewRenderer), so the box sits on the card it draws; the place is saved as shares of the screen, so it
    /// lands in the same place on every display.
    /// </summary>
    private static (double Width, double Height) Desktop() =>
        (1080.0 * SettingsPreview.PixelWidth / SettingsPreview.PixelHeight, 1080.0);

    private static double BaseSize((double Width, double Height) desktop) => ViewportMetrics.For(desktop.Width, desktop.Height, 1).BaseSize;

    /// <summary>The title card's window beside the first floating lyrics (Settings edits that display's): its display and frame.</summary>
    private (string Display, FloatingDisplay Geometry, FloatingRect Frame)? CardWindow(FloatingLyricsOptions options, Shell.Settings.AppSettings settings)
    {
        if (!ReferenceEquals(options, _windowFor) || !ReferenceEquals(settings.FloatingTitleFrames, _framesFor)
            || !Equals(settings.FloatingTitleStart, _startFor) || DateTime.UtcNow - _windowAt > TimeSpan.FromSeconds(2))
        {
            _windowFor = options;
            _framesFor = settings.FloatingTitleFrames;
            _startFor = settings.FloatingTitleStart;
            _windowAt = DateTime.UtcNow;
            _window = CardWhere(options, settings);
        }
        return _window;
    }

    /// <summary>Where the title card's window beside <paramref name="options"/>' lyrics is: its display and frame (any thread).</summary>
    internal static (string Display, FloatingDisplay Geometry, FloatingRect Frame)? CardWhere(FloatingLyricsOptions options,
                                                                                         Shell.Settings.AppSettings settings)
    {
        var card = options with { TitleCard = true, TitleFrames = settings.FloatingTitleFrames, TitleStart = settings.TitleStart() };
        return FloatingWindowHost.Where(card) is { } found ? (found.Display.Key, found.Geometry, found.Frame) : null;
    }

    /// <summary>The title card's window on a picture of <paramref name="width"/> × <paramref name="height"/> pixels, to draw it there.</summary>
    internal static Shell.Rendering.SettingsPreviewRenderer.PictureCard CardOnPicture(FloatingDisplay geometry, FloatingRect frame, double textSize,
                                                                                     int width, int height)
    {
        var box = WindowOnPicture(geometry, frame, width, height);
        // The picture is laid out from its height, as the wallpaper's picture is.
        var scale = (float)(height / geometry.ScreenFrame.Height);
        return new((int)Math.Round(box.X), (int)Math.Round(box.Y), (int)Math.Round(box.Width), (int)Math.Round(box.Height), scale, textSize);
    }

    /// <summary>The floating window's frame (points on its display, y up) on the picture.</summary>
    private static (double X, double Y, double Width, double Height) WindowOnPicture(FloatingDisplay geometry, FloatingRect frame,
                                                                                     double width, double height)
    {
        var screen = geometry.ScreenFrame;
        return (frame.MinX / screen.Width * width, (screen.Height - frame.MaxY) / screen.Height * height,
                frame.Width / screen.Width * width, frame.Height / screen.Height * height);
    }
}
