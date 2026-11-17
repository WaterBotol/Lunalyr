// The Lyric style page's "Editing", "Displays" and "Floating lyrics" sections, ported from the Mac's
// FloatingLyricsSection.swift (LyricSurfaceSection, DisplayLyricsSection, FloatingLyricsSection, FloatingLocationPicker;
// Lunalyr's own lines): which style the page edits, what each display shows, and the floating windows' own size,
// shade and place.

using Lunalyr.Core.Floating;
using Lunalyr.Platform.Desktop;
using Lunalyr.Shell.Settings;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>What the floating section needs from the app: the displays, and edit position (not a setting).</summary>
internal sealed record FloatingHooks(Func<IReadOnlyList<DisplayChoice>> Displays, Func<bool> Editing, Action<bool> SetEditing);

internal static class FloatingLyricsSection
{
    /// <summary>The section's tag, so `--settings-page floating` can bring it into view.</summary>
    public const string SectionTag = "floating-lyrics";

    /// <summary>"Editing": the Desktop style or the Floating style; the style controls below edit the chosen one (the Mac's 4f9b1454).</summary>
    public static void AddSurface(StackPanel page, SettingsContext ctx)
    {
        // The row is its own heading.
        var choice = new RadioButtons { MaxColumns = 2 };
        foreach (var surface in LyricsSurfaces.All) choice.Items.Add(new RadioButton { Content = surface.Title(), Tag = surface.Raw() });
        ctx.OnRefresh(s => choice.SelectedIndex = s.IsFloating ? 1 : 0);
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedItem is RadioButton { Tag: string raw } && LyricsSurfaces.Parse(raw) is { } surface)
                ctx.Update(s => s.WithLyricsSurface(surface));
        };
        ToolTipService.SetToolTip(choice, FloatingCopy.SurfaceHelp);
        AutomationProperties.SetHelpText(choice, FloatingCopy.SurfaceHelp);
        var card = Card("Editing", null, "", choice);
        ctx.OnRefresh(s => card.Description = FloatingCopy.SurfaceCaption(s.Surface));
        page.Children.Add(card);
    }

    /// <summary>"Displays": a menu per connected display, listed by name, saying what it shows (the Mac's DisplayLyricsSection).</summary>
    public static void AddDisplays(StackPanel page, SettingsContext ctx, FloatingHooks hooks)
    {
        var section = Page();
        section.Margin = new Thickness(0, 18, 0, 0);
        Section(section, FloatingCopy.DisplaysTitle);
        var rows = new StackPanel { Spacing = 4 };
        section.Children.Add(rows);
        section.Children.Add(Caption(FloatingCopy.DisplaysCaption + " " + FloatingCopy.DisplaysMore));
        List<(string Key, string Name)> shown = [];
        List<(string Key, ComboBox Menu)> menus = [];
        var filling = false;
        ctx.OnRefresh(s =>
        {
            var known = FloatingCopy.Named([.. hooks.Displays().Select(d => (d.Key, d.Name))]);
            filling = true;
            try
            {
                // Rebuilt only when the displays themselves change: a menu cleared while it was still closing over the
                // item just picked failed with "Element not found" (14 Nov).
                if (!known.SequenceEqual(shown))
                {
                    shown = known;
                    rows.Children.Clear();
                    menus.Clear();
                    var main = known.Count > 0 ? known[0].Key : null;
                    foreach (var (key, name) in known)
                    {
                        var menu = new ComboBox { MinWidth = 220 };
                        foreach (var choice in DisplayLyricsChoices.All) menu.Items.Add(new ComboBoxItem { Content = choice.Title(), Tag = choice.Raw() });
                        var help = FloatingCopy.DisplayHelp(name);
                        ToolTipService.SetToolTip(menu, help);
                        AutomationProperties.SetHelpText(menu, help);
                        var title = known.Count > 1 ? name : FloatingCopy.ThisDisplay;
                        AutomationProperties.SetName(menu, title);
                        menu.SelectionChanged += (_, _) =>
                        {
                            if (filling || menu.SelectedItem is not ComboBoxItem { Tag: string raw } || DisplayLyricsChoices.Parse(raw) is not { } picked) return;
                            if (ctx.Current.DisplayChoice(key) != picked) ctx.Update(c => c.WithDisplayChosen(key, picked, main));
                        };
                        rows.Children.Add(Card(title, null, null, menu));
                        menus.Add((key, menu));
                    }
                }
                foreach (var (key, menu) in menus)
                {
                    var index = DisplayLyricsChoices.All.ToList().IndexOf(s.DisplayChoice(key));
                    if (menu.SelectedIndex != index) menu.SelectedIndex = index;
                }
            }
            finally
            {
                filling = false;
            }
            section.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        page.Children.Add(section);
    }

    /// <summary>The floating windows' own settings: size, shade and place, and edit position.</summary>
    public static void AddFloating(StackPanel page, SettingsContext ctx, FloatingHooks hooks)
    {
        var section = Page();
        section.Tag = SectionTag;
        section.Margin = new Thickness(0, 18, 0, 0);
        Section(section, "Floating lyrics");

        // Size: the text size itself. The Small / Medium / Large buttons set the same value and were cut in the
        // Settings reorganisation (15 Nov 2026).
        section.Children.Add(Card("Text size", null, "", Slider(ctx, AppSettings.FloatingTextSizeMin, AppSettings.FloatingTextSizeMax, 1,
            FloatingCopy.Points, s => s.FloatingTextSize, (s, v) => s.WithFloatingTextSize(Math.Round(v)))));
        section.Children.Add(Card("Shade behind lyrics", FloatingCopy.ShadeCaption, "", Slider(ctx, 0, 1, 0.01, SettingsCopy.Percent,
            s => s.FloatingBackdrop, (s, v) => s with { FloatingBackdrop = Math.Round(v, 2) })));
        section.Children.Add(Card("Fade when the pointer is over them", null, "",
            Toggle(ctx, s => s.FloatingFadeUnderPointer, (s, v) => s with { FloatingFadeUnderPointer = v })));

        // Place: the little screen with its taskbar.
        var place = Card("Place", null, "", LocationPicker(ctx), stacked: true);
        ctx.OnRefresh(s => place.Description = s.Placement.Title());
        section.Children.Add(place);


        // Edit position and Reset size.
        var edit = Button(FloatingCopy.EditPosition, () => hooks.SetEditing(!hooks.Editing()));
        var reset = Button(FloatingCopy.ResetSize, () => ctx.Update(s => s.WithFloatingFramesReset()));
        AutomationProperties.SetHelpText(reset, FloatingCopy.ResetHelp);
        ToolTipService.SetToolTip(reset, FloatingCopy.ResetHelp);
        ctx.OnRefresh(_ =>
        {
            var editing = hooks.Editing();
            edit.Content = editing ? FloatingCopy.Done : FloatingCopy.EditPosition;
            AutomationProperties.SetName(edit, editing ? "Done placing the floating lyrics" : "Edit position");
            var help = editing ? FloatingCopy.DoneHelp : FloatingCopy.EditHelp;
            AutomationProperties.SetHelpText(edit, help);
            ToolTipService.SetToolTip(edit, help);
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(edit);
        buttons.Children.Add(reset);
        section.Children.Add(Card("Position", FloatingCopy.LockedCaption + " " + FloatingCopy.PauseNote, "", buttons));

        page.Children.Add(section.ShowWhen(ctx, s => s.IsFloating));
    }

    /// <summary>A small screen with its taskbar, and a spot for each preset: click one to put the lyrics there. Custom is the place set by dragging.</summary>
    private static FrameworkElement LocationPicker(SettingsContext ctx)
    {
        var screen = new Grid { Width = 196, Height = 124, HorizontalAlignment = HorizontalAlignment.Left };
        screen.Children.Add(new Border { Style = Resource<Style>("FloatingPickerScreen") });
        var taskbar = new Border { Style = Resource<Style>("FloatingPickerTaskbar"), VerticalAlignment = VerticalAlignment.Bottom };
        screen.Children.Add(taskbar);
        var grid = new Grid { Margin = new Thickness(8, 8, 8, 14), RowSpacing = 6, ColumnSpacing = 6 };
        for (var i = 0; i < 3; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var spots = new List<(FloatingPlacement Placement, Button Button, Border Mark)>();
        foreach (var preset in FloatingPlacements.Presets)
        {
            var (column, row) = preset.Cell()!.Value;
            var mark = new Border { Style = Resource<Style>("FloatingPickerSpot") };
            var button = new Button
            {
                Content = mark, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            };
            AutomationProperties.SetName(button, preset.Title());
            ToolTipService.SetToolTip(button, preset.Title());
            var chosen = preset;
            button.Click += (_, _) => ctx.Update(s => s with { FloatingPlacement = chosen.Raw() });
            Grid.SetRow(button, row);
            Grid.SetColumn(button, column);
            grid.Children.Add(button);
            spots.Add((preset, button, mark));
        }
        screen.Children.Add(grid);

        var custom = new ToggleButton { Content = "Custom", Padding = new Thickness(10, 3, 10, 3) };
        AutomationProperties.SetName(custom, FloatingPlacement.Custom.Title());
        ToolTipService.SetToolTip(custom, "The place and size you set with Edit position");
        custom.Click += (_, _) => ctx.Update(s => s with { FloatingPlacement = FloatingPlacement.Custom.Raw() });

        ctx.OnRefresh(s =>
        {
            foreach (var (placement, button, mark) in spots)
            {
                var selected = s.Placement == placement;
                mark.Style = Resource<Style>(selected ? "FloatingPickerSpotSelected" : "FloatingPickerSpot");
                AutomationProperties.SetItemStatus(button, selected ? "Selected" : "");
            }
            custom.IsChecked = s.Placement == FloatingPlacement.Custom;
        });

        var column2 = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
        column2.Children.Add(screen);
        column2.Children.Add(custom);
        return column2;
    }
}
