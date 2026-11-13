// The welcome's pictures, drawn with XAML brushes and text, so they need no wallpaper running yet. They are pictures of
// a desktop, so they look the same in light and dark, and Narrator skips them: the step's words say what they show.
// The sample wallpaper and the sample lyric are a faithful port of the Mac's OnboardingIllustrations.swift
// (SampleWallpaper's colours, glow centres and radii; SampleLyric's lines, held word, beat, plays and cycle), whose lines
// are Lunalyr's own apart from two SwiftUI reduce-motion declarations (a99fd65, not translated). The taskbar picture is
// new for Windows (the Mac's is the menu bar): the overflow ^, Lunalyr's icon, the clock and the tray lyric strip.

using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Lunalyr.App.Onboarding;

internal static class OnboardingArt
{
    /// <summary>The sample song's two lines, a word at a time.</summary>
    public static readonly string[][] Lines = [["Static", "on", "the", "radio,"], ["gold", "across", "the", "bay"]];

    public static int WordCount => Lines.Sum(l => l.Length);

    /// <summary>Where a still picture holds: "Static on the" sung, "radio," being sung.</summary>
    public const int Held = 3;

    /// <summary>A word every 0.45 s, then a breath with the lines full; it plays twice, then rests on the full lines.</summary>
    public static readonly TimeSpan Beat = TimeSpan.FromSeconds(0.45);

    public const int Plays = 2;

    public static int Cycle => WordCount + 4;

    /// <summary>How many words are sung <paramref name="beats"/> beats in; all of them once it has played out.</summary>
    public static int Sung(int beats) => beats >= Plays * Cycle ? WordCount : Math.Min(Math.Max(beats, 0) % Cycle, WordCount);

    private static Color Rgb(double r, double g, double b, double a = 1) =>
        Color.FromArgb((byte)Math.Round(a * 255), (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));

    /// <summary>A desktop in an album's colours: warm glows over a deep night gradient, dark enough for white lyrics.</summary>
    public static Grid Wallpaper(double width, double height)
    {
        var grid = new Grid();
        var night = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        night.GradientStops.Add(new GradientStop { Color = Rgb(0.20, 0.10, 0.30), Offset = 0 });
        night.GradientStops.Add(new GradientStop { Color = Rgb(0.07, 0.05, 0.15), Offset = 1 });
        grid.Children.Add(new Border { Background = night });
        var unit = Math.Max(width, height);
        grid.Children.Add(Glow(width, height, unit, new(0.16, 1.0), 0.62, Rgb(0.99, 0.56, 0.32), 0.80));
        grid.Children.Add(Glow(width, height, unit, new(0.88, 0.30), 0.52, Rgb(0.88, 0.27, 0.52), 0.62));
        grid.Children.Add(Glow(width, height, unit, new(0.45, -0.05), 0.42, Rgb(0.40, 0.30, 0.82), 0.50));
        Hide(grid);
        return grid;
    }

    private static Border Glow(double width, double height, double unit, Windows.Foundation.Point centre, double radius, Color colour, double opacity)
    {
        var brush = new RadialGradientBrush
        {
            Center = centre,
            GradientOrigin = centre,
            RadiusX = unit * radius / Math.Max(1, width),
            RadiusY = unit * radius / Math.Max(1, height),
        };
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)Math.Round(opacity * 255), colour.R, colour.G, colour.B), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, colour.R, colour.G, colour.B), Offset = 1 });
        return new Border { Background = brush };
    }

    /// <summary>The sample lines as text blocks, one run per word, lit by <see cref="Light"/>.</summary>
    public static (StackPanel View, List<Run> Words) Lyric(double fontSize)
    {
        var view = new StackPanel { Spacing = fontSize * 0.28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var words = new List<Run>();
        foreach (var line in Lines)
        {
            var block = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Variable Display"), FontWeight = FontWeights.SemiBold, FontSize = fontSize,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            };
            for (var i = 0; i < line.Length; i++)
            {
                var run = new Run { Text = (i == 0 ? "" : " ") + line[i] };
                block.Inlines.Add(run);
                words.Add(run);
            }
            Hide(block);
            view.Children.Add(block);
        }
        Hide(view);
        return (view, words);
    }

    private static readonly SolidColorBrush SungBrush = new(Color.FromArgb(235, 255, 255, 255));
    private static readonly SolidColorBrush SingingBrush = new(Colors.White);
    private static readonly SolidColorBrush WaitingBrush = new(Color.FromArgb(92, 255, 255, 255));

    /// <summary>Sung words bright, the word being sung brightest, the rest waiting.</summary>
    public static void Light(List<Run> words, int sung)
    {
        for (var i = 0; i < words.Count; i++) words[i].Foreground = i < sung ? SungBrush : i == sung ? SingingBrush : WaitingBrush;
    }

    /// <summary>The welcome's picture: the desktop as Lunalyr draws it, in miniature.</summary>
    public static (Border View, List<Run> Words) Hero(double width)
    {
        var height = Math.Round(width * 956 / 1470);
        var stage = Wallpaper(width, height);
        var (lyric, words) = Lyric(width * 0.064);
        stage.Children.Add(lyric);
        var frame = new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(8), Child = stage,
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Hide(frame);
        return (frame, words);
    }

    public const string TrayLine = "Static on the radio, gold across the bay";

    /// <summary>
    /// The right-hand end of a Windows 11 taskbar over the sample desktop: the overflow ^, Lunalyr's icon lit, Wi-Fi,
    /// volume and battery, the clock, and the tray lyric strip above the clock while it is switched on.
    /// </summary>
    public static (Border View, UIElement Strip) Taskbar(double width)
    {
        const double height = 112, bar = 40;
        var stage = Wallpaper(width, height);

        var taskbar = new Grid
        {
            Height = bar, VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(232, 32, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), BorderThickness = new Thickness(0, 1, 0, 0),
        };
        var items = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
        items.Children.Add(Glyph("", 12)); // the overflow, "Show hidden icons"
        var lit = new Border
        {
            Width = 32, Height = 30, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)),
            Child = Glyph("", 16), // a crescent, standing in for Lunalyr's tray icon
        };
        items.Children.Add(lit);
        var system = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        system.Children.Add(Glyph("", 14));
        system.Children.Add(Glyph("", 14));
        system.Children.Add(Glyph("", 14));
        items.Children.Add(system);
        var clock = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        clock.Children.Add(Small("9:41"));
        clock.Children.Add(Small("13/11/2026"));
        items.Children.Add(clock);
        taskbar.Children.Add(items);
        stage.Children.Add(taskbar);

        var strip = new Border
        {
            Height = 28, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 0, 14, 0),
            Background = new SolidColorBrush(Color.FromArgb(235, 40, 40, 40)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 8, bar + 8), MaxWidth = 300,
            Child = new TextBlock
            {
                Text = TrayLine, Foreground = new SolidColorBrush(Colors.White), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        Hide(strip.Child);
        stage.Children.Add(strip);

        var frame = new Border
        {
            Height = height, CornerRadius = new CornerRadius(8), Child = stage, HorizontalAlignment = HorizontalAlignment.Stretch,
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new Thickness(1),
        };
        Hide(frame);
        return (frame, strip);

        static FontIcon Glyph(string glyph, double size) => new()
        {
            Glyph = glyph, FontSize = size, Foreground = new SolidColorBrush(Colors.White),
            FontFamily = new FontFamily("Segoe Fluent Icons"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };

        static TextBlock Small(string text)
        {
            var small = new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Right };
            Hide(small);
            return small;
        }
    }

    /// <summary>Pictures are skipped by Narrator: the step's words say what they show.</summary>
    private static void Hide(UIElement element) => AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
}
