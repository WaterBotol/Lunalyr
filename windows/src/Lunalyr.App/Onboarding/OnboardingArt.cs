// The welcome's pictures, drawn with XAML brushes and text, so they need no wallpaper running yet. They are pictures of
// a desktop, so they look the same in light and dark, and Narrator skips them: the step's words say what they show.
// The sample wallpaper is a faithful port of the Mac's OnboardingIllustrations.swift (SampleWallpaper's colours, glow
// centres and radii), whose lines are Lunalyr's own apart from a SwiftUI reduce-motion declaration (a99fd65, not
// translated). Test Build 88's pictures follow: the welcome's recording with the app's icon (WelcomeHero), the desktop
// player in miniature (OnboardingPictures.swift's MiniDesktopPlayer) and the Discord status recording
// (DiscordStatusPicture.swift). The taskbar picture is new for Windows (the Mac's is the menu bar): the overflow ^,
// Lunalyr's icon, the clock and the tray lyric strip.

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

    /// <summary>
    /// The welcome's picture (the Mac's <c>WelcomeHero</c>): a recording of the real desktop with Lunalyr's lyrics on it,
    /// with the app's icon resting on its lower edge. The glass logo opening plays before it, over the whole window.
    /// </summary>
    public static (FrameworkElement View, OnboardingClipView Clip) Hero(double width)
    {
        var clip = new OnboardingClipView(Shell.Onboarding.OnboardingClip.Hero, width, Math.Round(width / Shell.Onboarding.OnboardingClips.Aspect), cornerRadius: 10);
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 0, 0, 30) };
        grid.Children.Add(clip.View);
        var icon = new Image
        {
            Width = 72, Height = 72, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, -30),
        };
        try
        {
            icon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, Shell.Onboarding.OnboardingAssets.Icon)));
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            // No icon: the recording shows alone.
        }
        grid.Children.Add(icon);
        Hide(grid);
        return (grid, clip);
    }

    /// <summary>
    /// The desktop player in miniature (the Mac's <c>MiniDesktopPlayer</c>): its card in the album's deep shade, the cover,
    /// the song, the seek line and the play disc. Made-up song, drawn in code.
    /// </summary>
    public static Border MiniPlayer(double width, double height)
    {
        var row = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cover = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        cover.GradientStops.Add(new GradientStop { Color = Rgb(0.98, 0.62, 0.38), Offset = 0 });
        cover.GradientStops.Add(new GradientStop { Color = Rgb(0.85, 0.30, 0.48), Offset = 0.5 });
        cover.GradientStops.Add(new GradientStop { Color = Rgb(0.24, 0.16, 0.42), Offset = 1 });
        row.Children.Add(new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(5), Background = cover });

        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Text("Golden Hour Static", 11, FontWeights.SemiBold, 1));
        texts.Children.Add(Text("Lunar Tides", 11, FontWeights.Normal, 0.62));
        var seek = new Grid { Height = 2, Margin = new Thickness(0, 5, 0, 0) };
        seek.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.4, GridUnitType.Star) });
        seek.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.6, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(1), Background = new SolidColorBrush(Rgb(1, 1, 1, 0.22)) };
        Grid.SetColumnSpan(track, 2);
        seek.Children.Add(track);
        seek.Children.Add(new Border { CornerRadius = new CornerRadius(1), Background = new SolidColorBrush(Colors.White) });
        texts.Children.Add(seek);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);

        var disc = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Colors.White), VerticalAlignment = VerticalAlignment.Center,
        };
        // The pause sign, filled (the Mac's pause.fill): two bars.
        var pause = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (var bar = 0; bar < 2; bar++)
            pause.Children.Add(new Border { Width = 2.5, Height = 8, CornerRadius = new CornerRadius(0.75), Background = new SolidColorBrush(Colors.Black) });
        disc.Child = pause;
        Grid.SetColumn(disc, 2);
        row.Children.Add(disc);

        var card = new Border
        {
            Style = Settings.SettingsKit.Resource<Style>("OnboardingPicture"),
            Width = width, Height = height, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Rgb(0.20, 0.10, 0.27)), Child = row,
        };
        Hide(card);
        return card;

        static TextBlock Text(string text, double size, Windows.UI.Text.FontWeight weight, double opacity)
        {
            var block = new TextBlock
            {
                Text = text, FontSize = size, FontWeight = weight, Foreground = new SolidColorBrush(Rgb(1, 1, 1, opacity)),
                TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
            };
            Hide(block);
            return block;
        }
    }

    /// <summary>Discord's dark member list, behind the status recording (the Mac's <c>discordBacking</c>).</summary>
    private static readonly Color DiscordBacking = Rgb(0.169, 0.176, 0.192);

    /// <summary>
    /// The Discord status as it looks in a member list (the Mac's <c>DiscordStatusPicture</c>): a recording of the real thing
    /// (<c>DiscordStatus.gif</c>, 254 × 44 pixels, the line changing as the song plays), shown at 1.5 times its 2x size so
    /// its name and line read clearly. Still on its first frame unless <paramref name="animates"/>.
    /// </summary>
    public static (Border View, Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Recording) DiscordStatus(double width, double height, bool animates)
    {
        var image = new Image { Width = 190.5, Height = 33, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Media.Imaging.BitmapImage? recording = null;
        try
        {
            recording = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { AutoPlay = animates };
            recording.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, Shell.Onboarding.OnboardingAssets.DiscordStatus));
            image.Source = recording;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            recording = null;
        }
        var card = new Border
        {
            Style = Settings.SettingsKit.Resource<Style>("OnboardingPicture"),
            Width = width, Height = height, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(DiscordBacking), Child = image,
        };
        Hide(card);
        return (card, recording);
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
