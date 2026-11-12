using Lunalyr.Core.Compositions;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>
/// Lyric style (the Mac's LyricStylePage): where lyrics show, the layout and Match the music's rotation, the opening
/// title and credits, size, typeface, colours, word entrances and the custom mix, the constellations' and Echo's own
/// sliders, and instrumental breaks.
/// </summary>
internal static class LyricStylePage
{
    private const string Automatic = AppSettings.AllLayoutsKey;

    public static StackPanel Build(SettingsContext ctx, Func<IReadOnlyList<string>> installedFonts, Func<bool> reduceMotion,
                                   UIElement? preview)
    {
        var page = Page();
        if (preview is not null) page.Children.Add(preview);

        // ---- Show lyrics ----
        Section(page, "Show lyrics");
        page.Children.Add(Card("On the desktop", "Editing the Desktop style. The lyrics sit on the wallpaper, under your windows.", ""));
        page.Children.Add(Card("Floating lyrics", "Lyrics floating over your apps, with their own style.", "", FixedToggle(false),
            notYet: SettingsCopy.Milestone7));

        // ---- Layout ----
        Section(page, "Layout");
        var layoutCard = Card("Layout", null, "",
            Combo(ctx, SettingsCopy.Layouts, s => s.LyricComposition, (s, v) => s with { LyricComposition = v }));
        ctx.OnRefresh(s => layoutCard.Description = SettingsCopy.Layouts.FirstOrDefault(c => c.Value == s.LyricComposition)?.Detail ?? "");
        page.Children.Add(layoutCard);

        page.Children.Add(Card("Change layout every song",
                "Each new song takes the next layout, skipping ones that don’t suit its energy. Off: the layout follows the song’s energy alone.",
                "", Toggle(ctx, s => s.RotateLayouts, (s, v) => s with { RotateLayouts = v }))
            .ShowWhen(ctx, s => s.LyricComposition == Automatic));
        page.Children.Add(Expander("Layouts in the rotation",
                "How many songs in a row each layout gets before the next one. 0 leaves a layout out.", "", RotationList(ctx))
            .ShowWhen(ctx, s => s.LyricComposition == Automatic && s.RotateLayouts));

        page.Children.Add(Card("Show the song title at the start",
            "The song’s title, artist and album appear low on the left as a song begins, in the lyrics’ typeface, and leave before the first line is sung. A song that starts singing straight away gets a shorter one, or none.",
            "", Toggle(ctx, s => s.OpeningTitle, (s, v) => s with { OpeningTitle = v })));
        var cover = Card("Show the album cover", "The cover beside the song’s title.", null,
            Toggle(ctx, s => s.OpeningTitleCover, (s, v) => s with { OpeningTitleCover = v }).EnableWhen(ctx, s => s.OpeningTitle));
        cover.Margin = new Thickness(36, 0, 0, 0);
        page.Children.Add(cover);
        page.Children.Add(Card("Show credits in the background",
            "Lines like “Produced by: …” and “作词 : …” appear one at a time behind the lyrics, at full size, in the same layout as backing vocals.",
            "", Toggle(ctx, s => s.CreditsLayer, (s, v) => s with { CreditsLayer = v })));
        page.Children.Add(Card("Backing vocals in the background",
            "Bracketed backing words like (oh oh) are animated word by word like the lyrics, but small and soft behind the lead. Always on in this build.",
            "", FixedToggle(true), notYet: SettingsCopy.LaterBuild));
        page.Children.Add(Card("Add Editorial typography", "Mixes in a second, contrasting typeface.", "", FixedToggle(false),
            notYet: SettingsCopy.LaterBuild));
        page.Children.Add(Card("Lyric size", null, "",
            Slider(ctx, 0.7, 1.5, 0.01, SettingsCopy.Percent, s => s.LyricScale, (s, v) => s with { LyricScale = Math.Round(v, 2) })));

        // ---- Typeface ----
        Section(page, "Typeface");
        page.Children.Add(Card("Randomise lyric fonts", "Words use a mix of built-in typefaces.", "", FixedToggle(false),
            notYet: SettingsCopy.LaterBuild));
        var typeface = Card("Typeface", null, "",
            Combo(ctx, SettingsCopy.FontChoices, s => s.FontChoice, (s, v) => s with { FontChoice = v }));
        ctx.OnRefresh(s => typeface.Description = SettingsCopy.FontChoices.FirstOrDefault(c => c.Value == s.FontChoice)?.Detail ?? "");
        page.Children.Add(typeface);
        page.Children.Add(Card("Font family", "Type to jump to a family installed on this PC.", null, FontList(ctx, installedFonts))
            .ShowWhen(ctx, s => s.FontChoice == "installed"));

        // ---- Colours ----
        Section(page, "Colours");
        page.Children.Add(Card("Colour", "The lyrics drift through the album’s colours.", "", FixedCombo("Album drift"),
            notYet: SettingsCopy.LaterBuild));
        var highlight = Card("Highlight colour", null, "",
            Combo(ctx, SettingsCopy.HighlightModes, s => s.LyricHighlightMode, (s, v) => s with { LyricHighlightMode = v }));
        ctx.OnRefresh(s => highlight.Description = (SettingsCopy.HighlightModes.FirstOrDefault(c => c.Value == s.LyricHighlightMode)?.Detail ?? "")
            + " Used for Cutout labels, tiles and Cascade’s held words.");
        page.Children.Add(highlight);
        page.Children.Add(Card("Your highlight colour", "The text on a tile always keeps at least 4.5:1 contrast.", null, HighlightPicker(ctx))
            .ShowWhen(ctx, s => s.LyricHighlightMode == "custom"));

        // ---- Word entrances ----
        Section(page, "Word entrances");
        var entrance = new ComboBox { MinWidth = 260 };
        var entranceCard = Card("Entrance", null, "", entrance);
        string[] shownFor = [""];
        ctx.OnRefresh(s =>
        {
            // The menu marks entrances the chosen layout leaves out, so it is rebuilt when the layout changes.
            if (shownFor[0] != s.LyricComposition)
            {
                shownFor[0] = s.LyricComposition;
                entrance.Items.Clear();
                foreach (var choice in SettingsCopy.Entrances(s.LyricComposition))
                    entrance.Items.Add(new ComboBoxItem { Content = choice.Title, Tag = choice.Value });
            }
            var current = s.EntranceForControls();
            entrance.SelectedIndex = AppSettings.WordArrivals.ToList().IndexOf(current);
            var parts = new List<string>
            {
                s.LyricComposition == Automatic ? "These controls apply to layouts picked by Match the music." : "These controls apply to the selected layout.",
            };
            if (EntranceSupport.Note(WordArrivals.Parse(current), SettingsCopy.Kind(s.LyricComposition), s.MixEntrances()) is { } note) parts.Add(note);
            if (reduceMotion()) parts.Add(SettingsCopy.ReduceMotionEntrances);
            entranceCard.Description = string.Join(" ", parts);
            AutomationProperties.SetHelpText(entrance, entranceCard.Description);
        });
        entrance.SelectionChanged += (_, _) =>
        {
            if (entrance.SelectedItem is ComboBoxItem { Tag: string value }) ctx.Update(s => s.WithEntranceForControls(value));
        };
        page.Children.Add(entranceCard);
        page.Children.Add(Expander("Custom mix", "Each word takes one of the entrances ticked here.", "", MixList(ctx), expanded: true)
            .ShowWhen(ctx, s => s.EntranceForControls() == "mix"));
        page.Children.Add(Card("Reveal sung words", "Words appear one by one as they are sung.", "", FixedToggle(true),
            notYet: SettingsCopy.LaterBuild));
        page.Children.Add(Card("Faint next-word preview", "A faint preview of each word just before it is sung.", "", FixedToggle(true),
            notYet: SettingsCopy.LaterBuild));

        // ---- The layouts' own controls ----
        Section(page, "Constellation").ShowWhen(ctx, s => s.LyricComposition is Automatic or "constellation" or "constellationEditorial");
        page.Children.Add(Card("Spread", null, "",
                Slider(ctx, AppSettings.SpreadMin, AppSettings.SpreadMax, 0.01, SettingsCopy.Percent, s => s.ConstellationSpread,
                    (s, v) => s with { ConstellationSpread = Math.Round(v, 2) }))
            .ShowWhen(ctx, s => s.LyricComposition is Automatic or "constellation" or "constellationEditorial"));
        page.Children.Add(Card("Links", null, "",
                Slider(ctx, AppSettings.LinksMin, AppSettings.LinksMax, 0.01, SettingsCopy.Percent, s => s.ConstellationLinkOpacity,
                    (s, v) => s with { ConstellationLinkOpacity = Math.Round(v, 2) }))
            .ShowWhen(ctx, s => s.LyricComposition is Automatic or "constellation" or "constellationEditorial"));
        page.Children.Add(Caption("Used when Match the music picks Constellation.").ShowWhen(ctx, s => s.LyricComposition == Automatic));

        Section(page, "Echo").ShowWhen(ctx, s => s.LyricComposition is Automatic or "echo");
        page.Children.Add(Card("Repeats", null, "",
                Slider(ctx, AppSettings.RepeatsMin, AppSettings.RepeatsMax, 0.01, SettingsCopy.Percent, s => s.EchoStrength,
                    (s, v) => s with { EchoStrength = Math.Round(v, 2) }))
            .ShowWhen(ctx, s => s.LyricComposition is Automatic or "echo"));
        page.Children.Add(Caption("Used when Match the music picks Echo.").ShowWhen(ctx, s => s.LyricComposition == Automatic));

        // ---- Instrumental breaks ----
        Section(page, "Instrumental breaks");
        page.Children.Add(Card("Show three dots during instrumental breaks", null, "",
            Toggle(ctx, s => s.ShowInterludes, (s, v) => s with { ShowInterludes = v })));

        // ---- Remember a style ----
        Section(page, "Remember a style");
        page.Children.Add(Card("Use remembered styles", "Saves a layout and colour for a song or artist. A song’s own wins.", "",
            FixedToggle(false), notYet: SettingsCopy.LaterBuild));
        return page;
    }

    /// <summary>A number box per layout in the rotation (0–5 songs in a row).</summary>
    private static StackPanel RotationList(SettingsContext ctx)
    {
        var list = new StackPanel { Spacing = 6, Padding = new Thickness(40, 4, 0, 4) };
        foreach (var layout in LayoutRotation.Order)
        {
            var title = SettingsCopy.Kind(layout)?.Title() ?? layout;
            var count = new TextBlock
            {
                Style = Resource<Style>("SettingsCaption"),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 120,
            };
            var box = new NumberBox
            {
                Minimum = 0, Maximum = LayoutRotation.MaximumCount, SmallChange = 1, LargeChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 130,
            };
            AutomationProperties.SetName(box, $"{title}: songs in a row");
            ctx.OnRefresh(s =>
            {
                var n = (s.RotateLayoutCounts ?? new Dictionary<string, int>()).TryGetValue(layout, out var c) ? c : 1;
                box.Value = n;
                count.Text = SettingsCopy.RotationCount(n);
            });
            box.ValueChanged += (_, e) =>
            {
                if (!double.IsFinite(e.NewValue)) return;
                var n = (int)Math.Clamp(Math.Round(e.NewValue), 0, LayoutRotation.MaximumCount);
                ctx.Update(s => s with
                {
                    RotateLayoutCounts = new Dictionary<string, int>(s.RotateLayoutCounts ?? new Dictionary<string, int>()) { [layout] = n },
                });
            };
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Style = Resource<Style>("SettingsTitle") };
            row.Children.Add(name);
            Grid.SetColumn(count, 1);
            row.Children.Add(count);
            Grid.SetColumn(box, 2);
            row.Children.Add(box);
            list.Children.Add(row);
        }
        return list;
    }

    /// <summary>A check box per entrance a custom mix can hold.</summary>
    private static StackPanel MixList(SettingsContext ctx)
    {
        var list = new StackPanel { Spacing = 0, Padding = new Thickness(40, 0, 0, 4) };
        foreach (var choice in SettingsCopy.MixChoices)
        {
            var box = new CheckBox { Content = choice.Title };
            ctx.OnRefresh(s => box.IsChecked = (s.WordEntranceMix ?? []).Contains(choice.Value));
            box.Checked += (_, _) => ctx.Update(s => s.WithMixEntrance(choice.Value, true));
            box.Unchecked += (_, _) => ctx.Update(s => s.WithMixEntrance(choice.Value, false));
            list.Children.Add(box);
        }
        list.Children.Add(Caption("With none ticked, each layout uses its own entrance."));
        return list;
    }

    /// <summary>The installed families, filled when first shown.</summary>
    private static ComboBox FontList(SettingsContext ctx, Func<IReadOnlyList<string>> installedFonts)
    {
        var combo = new ComboBox { MinWidth = 260, MaxWidth = 360, PlaceholderText = "Select a family" };
        var filled = false;
        void Fill(AppSettings s)
        {
            if (!filled && s.FontChoice == "installed")
            {
                filled = true;
                foreach (var family in installedFonts()) combo.Items.Add(family);
            }
            var index = combo.Items.IndexOf(s.FontFamily);
            if (index < 0 && !string.IsNullOrWhiteSpace(s.FontFamily))
            {
                combo.Items.Insert(0, s.FontFamily);
                index = 0;
            }
            combo.SelectedIndex = index;
        }
        ctx.OnRefresh(Fill);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string family) ctx.Update(s => s with { FontFamily = family });
        };
        return combo;
    }

    /// <summary>A swatch button that opens Windows' colour picker for the custom highlight.</summary>
    private static Button HighlightPicker(SettingsContext ctx)
    {
        var swatch = new Border { Width = 40, Height = 20, Style = Resource<Style>("SettingsSwatch") };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        content.Children.Add(swatch);
        content.Children.Add(label);
        var button = new Button { Content = content };
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorSliderVisible = true, IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true, ColorSpectrumShape = ColorSpectrumShape.Ring,
        };
        AutomationProperties.SetName(picker, "Your highlight colour");
        var flyout = new Flyout { Content = picker };
        button.Flyout = flyout;
        ctx.OnRefresh(s =>
        {
            var colour = Parse(s.LyricHighlightCustom);
            swatch.Background = new SolidColorBrush(colour);
            label.Text = s.LyricHighlightCustom;
            AutomationProperties.SetItemStatus(button, s.LyricHighlightCustom);
            picker.Color = colour;
        });
        // Saved when the picker closes, so dragging across the spectrum doesn't write a file per step.
        flyout.Closed += (_, _) =>
        {
            var c = picker.Color;
            ctx.Update(s => s with { LyricHighlightCustom = $"#{c.R:X2}{c.G:X2}{c.B:X2}" });
        };
        return button;

        static Windows.UI.Color Parse(string hex)
        {
            var text = (hex ?? "").Trim().TrimStart('#');
            if (text.Length == 6 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
                return Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return Windows.UI.Color.FromArgb(255, 0xFF, 0x92, 0xA3);
        }
    }
}
