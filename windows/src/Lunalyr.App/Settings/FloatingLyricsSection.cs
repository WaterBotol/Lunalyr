// The Lyric style page's "Show lyrics" and "Floating lyrics" sections, ported from the Mac's FloatingLyricsSection.swift
// (LyricSurfaceSection, FloatingLyricsSection, FloatingLocationPicker; Lunalyr's own lines): which surface shows the
// lyrics (and so which style the page edits), and the floating window's own size, shade, place and display.

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
    /// <summary>"Show lyrics": on the desktop or floating over the apps; the style controls below edit the chosen one's.</summary>
    public static void AddSurface(StackPanel page, SettingsContext ctx)
    {
        Section(page, "Show lyrics");
        var choice = new RadioButtons { MaxColumns = 2 };
        foreach (var surface in LyricsSurfaces.All) choice.Items.Add(new RadioButton { Content = surface.Title(), Tag = surface.Raw() });
        ctx.OnRefresh(s => choice.SelectedIndex = s.IsFloating ? 1 : 0);
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedItem is RadioButton { Tag: string raw } && LyricsSurfaces.Parse(raw) is { } surface)
                ctx.Update(s => s.WithLyricsSurface(surface));
        };
        var card = Card("Show lyrics", null, "", choice);
        ctx.OnRefresh(s => card.Description = FloatingCopy.SurfaceCaption(s.Surface));
        page.Children.Add(card);
    }

    /// <summary>The floating window's own settings: size, shade, place and display, and edit position.</summary>
    public static void AddFloating(StackPanel page, SettingsContext ctx, FloatingHooks hooks)
    {
        var section = Page();
        Section(section, "Floating lyrics");

        // Size: the presets, and the text size itself.
        var sizes = new RadioButtons { MaxColumns = 4 };
        foreach (var (title, points) in AppSettings.FloatingSizePresets) sizes.Items.Add(new RadioButton { Content = title, Tag = points });
        ctx.OnRefresh(s =>
        {
            var index = -1;
            for (var i = 0; i < AppSettings.FloatingSizePresets.Count; i++)
                if (Math.Abs(AppSettings.FloatingSizePresets[i].Points - s.FloatingTextSize) < 0.01) index = i;
            sizes.SelectedIndex = index;
        });
        sizes.SelectionChanged += (_, _) =>
        {
            if (sizes.SelectedItem is RadioButton { Tag: double points }) ctx.Update(s => s with { FloatingTextSize = points });
        };
        var sizeCard = Card("Size", null, "", sizes);
        ctx.OnRefresh(s => sizeCard.Description = FloatingCopy.SizeName(s.FloatingTextSize));
        section.Children.Add(sizeCard);
        section.Children.Add(Card("Text size", null, "", Slider(ctx, AppSettings.FloatingTextSizeMin, AppSettings.FloatingTextSizeMax, 1,
            FloatingCopy.Points, s => s.FloatingTextSize, (s, v) => s with { FloatingTextSize = Math.Round(v) })));
        section.Children.Add(Card("Shade behind lyrics", FloatingCopy.ShadeCaption, "", Slider(ctx, 0, 1, 0.01, SettingsCopy.Percent,
            s => s.FloatingBackdrop, (s, v) => s with { FloatingBackdrop = Math.Round(v, 2) })));
        section.Children.Add(Card("Fade when the pointer is over them", null, "",
            Toggle(ctx, s => s.FloatingFadeUnderPointer, (s, v) => s with { FloatingFadeUnderPointer = v })));

        // Place: the little screen with its taskbar.
        var place = Card("Place", null, "", LocationPicker(ctx), stacked: true);
        ctx.OnRefresh(s => place.Description = s.Placement.Title());
        section.Children.Add(place);

        // Display: only with more than one.
        var displays = new ComboBox { MinWidth = 260, MaxWidth = 360 };
        var displayCard = Card("Display", null, "", displays);
        IReadOnlyList<DisplayChoice> known = [];
        ctx.OnRefresh(s =>
        {
            known = hooks.Displays();
            displays.Items.Clear();
            displays.Items.Add(new ComboBoxItem { Content = FloatingCopy.MainDisplay, Tag = "" });
            foreach (var d in known) displays.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Key });
            var index = 0;
            for (var i = 0; i < known.Count; i++) if (known[i].Key == s.FloatingDisplay) index = i + 1;
            displays.SelectedIndex = index;
            displayCard.Visibility = known.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        });
        displays.SelectionChanged += (_, _) =>
        {
            if (displays.SelectedItem is ComboBoxItem { Tag: string key }) ctx.Update(s => s with { FloatingDisplay = key });
        };
        section.Children.Add(displayCard);

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
