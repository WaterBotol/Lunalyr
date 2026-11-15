using Lunalyr.Core.Motion.ThinkingOrbs;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Lunalyr.App.Settings;

/// <summary>
/// A 64 pt live preview of the thinking orb's style beside its picker (the Mac's 13b8ed6): thinking-orbs' 64 design drawn
/// in the theme's ink (upstream's grey, mirrored on a dark theme). It animates only while it is on screen, holds still
/// with Reduce Motion, and in Mix changes style every 4 s.
/// </summary>
internal sealed partial class OrbPreview : Grid
{
    private const double Size = 64;
    private const double FramesPerSecond = 24;
    private const double MixEvery = 4;

    private readonly Canvas _canvas = new() { Width = Size, Height = Size, IsHitTestVisible = false };
    private readonly List<Ellipse> _dots = [];
    private readonly List<Line> _lines = [];
    private readonly OrbFrame _frame = new();
    private readonly Func<string> _style;
    private readonly Func<bool> _reduceMotion;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1 / FramesPerSecond) };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private bool _onScreen;
    private OrbState _mixState = OrbState.Listening;
    private long _mixTurn = -1;

    public OrbPreview(Func<string> style, Func<bool> reduceMotion)
    {
        _style = style;
        _reduceMotion = reduceMotion;
        Width = Size;
        Height = Size;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_canvas);
        AutomationProperties.SetName(this, "Orb style preview");
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        _timer.Tick += (_, _) => Draw();
        Loaded += (_, _) => { _clock.Start(); Draw(); UpdateTimer(); };
        Unloaded += (_, _) => { _timer.Stop(); _clock.Stop(); };
        EffectiveViewportChanged += (_, e) =>
        {
            _onScreen = e.EffectiveViewport.Width > 0 && e.EffectiveViewport.Height > 0;
            UpdateTimer();
        };
        ActualThemeChanged += (_, _) => Draw();
    }

    /// <summary>Draws the current style at once (after a change of the picker).</summary>
    public void Refresh()
    {
        _mixTurn = -1;
        Draw();
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        var animate = IsLoaded && _onScreen && !_reduceMotion();
        if (animate && !_timer.IsEnabled) _timer.Start();
        else if (!animate && _timer.IsEnabled) _timer.Stop();
    }

    private void Draw()
    {
        var seconds = _clock.Elapsed.TotalSeconds;
        var (style, mix) = ThinkingOrbStage.Style(_style());
        var state = style;
        if (mix)
        {
            var turn = (long)(seconds / MixEvery);
            if (turn != _mixTurn)
            {
                _mixState = ThinkingOrbStage.Mix(unchecked((ulong)turn * 0x9E3779B97F4A7C15UL + 1), _mixTurn < 0 ? null : _mixState);
                _mixTurn = turn;
            }
            state = _mixState;
        }
        var resolved = ThinkingOrbEngine.Resolve(state, 64);
        var t = _reduceMotion() ? ThinkingOrbStage.StillT : seconds * ThinkingOrbStage.Speed * resolved.Speed;
        var frame = ThinkingOrbEngine.Frame(resolved.Mode, Size, t, resolved.Opts, _frame);
        var dark = ActualTheme == ElementTheme.Dark;

        while (_lines.Count < frame.Lines.Count)
        {
            var line = new Line { Stroke = new SolidColorBrush(Colors.Gray) };
            _lines.Add(line);
            _canvas.Children.Insert(0, line);
        }
        for (var i = 0; i < _lines.Count; i++)
        {
            var shape = _lines[i];
            if (i >= frame.Lines.Count) { shape.Visibility = Visibility.Collapsed; continue; }
            var l = frame.Lines[i];
            shape.Visibility = Visibility.Visible;
            shape.X1 = l.X1; shape.Y1 = l.Y1; shape.X2 = l.X2; shape.Y2 = l.Y2;
            shape.StrokeThickness = l.W;
            ((SolidColorBrush)shape.Stroke).Color = Ink(l.White, l.A, dark);
        }
        while (_dots.Count < frame.Dots.Count)
        {
            var dot = new Ellipse { Fill = new SolidColorBrush(Colors.Gray) };
            _dots.Add(dot);
            _canvas.Children.Add(dot);
        }
        for (var i = 0; i < _dots.Count; i++)
        {
            var shape = _dots[i];
            if (i >= frame.Dots.Count) { shape.Visibility = Visibility.Collapsed; continue; }
            var d = frame.Dots[i];
            shape.Visibility = Visibility.Visible;
            shape.Width = shape.Height = d.R * 2;
            Canvas.SetLeft(shape, d.X - d.R);
            Canvas.SetTop(shape, d.Y - d.R);
            ((SolidColorBrush)shape.Fill).Color = Ink(d.White, d.A, dark);
        }
    }

    /// <summary>Upstream's ink: grey by <paramref name="white"/> (0 the darkest on paper), mirrored on a dark theme.</summary>
    private static Windows.UI.Color Ink(double white, double alpha, bool dark)
    {
        var w = Math.Clamp(white, 0, 1);
        var g = (byte)Math.Round((dark ? 1 - w : w) * 255);
        return Windows.UI.Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), g, g, g);
    }
}
