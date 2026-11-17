// 4. Pick a typeface (the Mac's TypefaceStep in OnboardingTypefaces.swift, 2ed3445d; Lunalyr's own lines, in Windows'
// controls): tiles that each set a line of the sample song in their face, large and bold over a sample wallpaper. The
// choice goes to every lyric style: the one edited, each kept style and every monitor's (WithTypefaceEverywhere).

using Lunalyr.App.Settings;
using Lunalyr.Shell.Onboarding;
using Lunalyr.Shell.Settings;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Onboarding;

internal sealed partial class OnboardingWindow
{
    private const int TypefaceColumns = 3;
    private const double TypefaceTileWidth = 128, TypefaceTileHeight = 72;

    private StackPanel TypefaceStep(SettingsContext ctx)
    {
        var panel = new StackPanel();
        panel.Children.Add(Header(Shell.Onboarding.OnboardingStep.Typeface, OnboardingCopy.TypefaceTitle, OnboardingCopy.Typeface, bottom: 20));

        IReadOnlyList<OnboardingTypeface> faces;
        try
        {
            faces = OnboardingTypefaces.Offered(InstalledFonts.Families());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log.App.Warn("Couldn't list the installed fonts for the typeface step; only the system face is offered.", ex);
            faces = OnboardingTypefaces.Offered([]);
        }

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, ColumnSpacing = 12, RowSpacing = 12 };
        AutomationProperties.SetName(grid, "Typeface");
        for (var c = 0; c < Math.Min(TypefaceColumns, faces.Count); c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r < (faces.Count + TypefaceColumns - 1) / TypefaceColumns; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tiles = new List<(ToggleButton Tile, OnboardingTypeface Face)>();
        for (var i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            var tile = TypefaceTile(face);
            tile.Click += (_, _) =>
            {
                // A click never unlights the chosen tile.
                var chosen = OnboardingTypefaces.Chosen(ctx.Current, faces);
                tile.IsChecked = chosen == face;
                if (chosen != face)
                {
                    _host.Log.App.Info($"Welcome: the lyrics' typeface is now {face.Name}.");
                    ctx.Update(s => s.WithTypefaceEverywhere(face.FontChoice, face.FontFamily));
                }
            };
            Grid.SetColumn(tile, i % TypefaceColumns);
            Grid.SetRow(tile, i / TypefaceColumns);
            grid.Children.Add(tile);
            tiles.Add((tile, face));
        }
        ctx.OnRefresh(s =>
        {
            var chosen = OnboardingTypefaces.Chosen(s, faces);
            foreach (var (tile, face) in tiles) tile.IsChecked = face == chosen;
        });
        panel.Children.Add(grid);
        panel.Children.Add(new TextBlock
        {
            Text = OnboardingCopy.TypefaceLater, Style = Resource<Style>("SettingsCaption"), TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0),
        });
        return panel;
    }

    /// <summary>One face's tile: the sample line over the sample wallpaper, and the face's name under it.</summary>
    private static ToggleButton TypefaceTile(OnboardingTypeface face)
    {
        var sample = new TextBlock
        {
            Text = string.Join("\n", OnboardingCopy.TypefaceSample), FontFamily = new FontFamily(face.FontChoice == "system" ? "Segoe UI Variable Display" : face.FontFamily),
            FontWeight = FontWeights.Black, FontSize = 19, LineHeight = 21, Foreground = new SolidColorBrush(Colors.White),
            TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 8, 0),
        };
        var picture = new Border
        {
            Width = TypefaceTileWidth, Height = TypefaceTileHeight, CornerRadius = new CornerRadius(6),
            Background = SampleWallpaper(), Child = sample,
        };
        AutomationProperties.SetAccessibilityView(picture, AccessibilityView.Raw);
        var content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(picture);
        content.Children.Add(new TextBlock
        {
            Text = face.Name, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = TypefaceTileWidth,
        });
        var tile = new ToggleButton { Content = content, Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(tile, face.Name);
        AutomationProperties.SetHelpText(tile, $"Sets the lyrics in {face.Name}.");
        ToolTipService.SetToolTip(tile, face.Help);
        return tile;
    }

    /// <summary>The sample wallpaper behind each tile's line: a dark album-like gradient, as the Mac's SampleWallpaper.</summary>
    private static LinearGradientBrush SampleWallpaper() => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
        GradientStops =
        {
            new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x3A, 0x1C, 0x5C), Offset = 0 },
            new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x7A, 0x2E, 0x6A), Offset = 0.55 },
            new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x1D, 0x4E, 0x5F), Offset = 1 },
        },
    };
}
