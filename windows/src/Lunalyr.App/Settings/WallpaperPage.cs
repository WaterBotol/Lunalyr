using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>
/// Wallpaper (the Mac's <c>WallpaperPage</c>): the type, its own options, legibility and the adjustments every drawn type
/// shares. GPU effects, line patterns and visualisers add their own rows through the partial methods at the end,
/// implemented in their own files (docs/APP-SHELL.md, "Wallpaper types: hooks for other layers").
/// </summary>
internal static partial class WallpaperPage
{
    public static StackPanel Build(SettingsContext ctx)
    {
        var page = Page();
        Section(page, "Wallpaper");
        var types = new RadioButtons { MaxColumns = 1 };
        AutomationProperties.SetName(types, "Wallpaper type");
        foreach (var type in SettingsCopy.WallpaperTypes)
        {
            var texts = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2, 0, 6) };
            texts.Children.Add(new TextBlock { Text = type.Title });
            texts.Children.Add(Caption(type.Summary));
            if (type.NotYet is { } note) texts.Children.Add(Caption(note));
            var radio = new RadioButton { Content = texts, Tag = type.Value, IsEnabled = type.Available };
            AutomationProperties.SetName(radio, type.Title);
            AutomationProperties.SetHelpText(radio, type.NotYet is null ? type.Summary : $"{type.Summary} {type.NotYet}");
            types.Items.Add(radio);
        }
        ctx.OnRefresh(s =>
        {
            var chosen = SettingsCopy.WallpaperTypeFor(s.BackgroundMode);
            for (var i = 0; i < types.Items.Count; i++)
                if (types.Items[i] is RadioButton { Tag: string value } && value == chosen) types.SelectedIndex = i;
        });
        types.SelectionChanged += (_, _) =>
        {
            if (types.SelectedItem is RadioButton { Tag: string value, IsEnabled: true }) ctx.Update(s => s with { BackgroundMode = value });
        };
        page.Children.Add(new Border { Style = Resource<Style>("Card"), Child = types });

        // The type's own section: shown while its type is chosen.
        var effects = new StackPanel { Spacing = 4 }.ShowWhen(ctx, s => s.BackgroundMode is "auto" or "shader");
        AddEffectOptions(effects, ctx);
        page.Children.Add(effects);
        var patterns = new StackPanel { Spacing = 4 }.ShowWhen(ctx, s => s.BackgroundMode is "auto" or "pattern");
        AddPatternOptions(patterns, ctx);
        page.Children.Add(patterns);
        var visualiser = new StackPanel { Spacing = 4 }.ShowWhen(ctx, s => s.BackgroundMode == "visualizer");
        var added = false;
        AddVisualiserSections(visualiser, ctx, ref added);
        if (!added)
        {
            Section(visualiser, "Visualiser");
            visualiser.Children.Add(Card("Scene", "Scenes that move with the music itself.", "", FixedCombo("Visualisers"),
                notYet: SettingsCopy.Milestone6Later));
        }
        page.Children.Add(visualiser);

        Section(page, "Legibility");
        page.Children.Add(Card("Automatic text backing",
            "Light covers and images get a soft shadow behind the lyrics, just strong enough for white text to stay readable.",
            "", Toggle(ctx, s => s.TextBackingAutomatic, (s, v) => s with { TextBackingAutomatic = v })));

        Section(page, "Adjustments");
        page.Children.Add(Card("Motion speed", null, "",
            Slider(ctx, AppSettings.SpeedMin, AppSettings.SpeedMax, 0.05, v => $"{v:0.00}×", s => s.EffectSpeed, (s, v) => s with { EffectSpeed = v })));
        page.Children.Add(Card("Colour richness", null, "",
            Slider(ctx, AppSettings.VividnessMin, AppSettings.VividnessMax, 0.01, SettingsCopy.Percent, s => s.Vividness, (s, v) => s with { Vividness = v })));
        page.Children.Add(Card("Darkening", null, "",
            Slider(ctx, AppSettings.ScrimMin, AppSettings.ScrimMax, 0.01, SettingsCopy.Percent, s => s.ScrimStrength, (s, v) => s with { ScrimStrength = v })));
        return page;
    }

    // ---- Hooks: implemented by the GPU effects / line patterns / visualiser layers in their own files ----

    /// <summary>
    /// GPU effects: rows of the effects' own options, added to <paramref name="section"/> (shown with Match the music and
    /// GPU effects, under the "GPU effect" menu). Implement in <c>WallpaperPage.Effects.cs</c>.
    /// </summary>
    static partial void AddEffectOptions(StackPanel section, SettingsContext ctx);

    /// <summary>
    /// Line patterns: rows of the patterns' own options, added to <paramref name="section"/> (shown with Match the music
    /// and Line patterns, under the "Pattern" menu). Implement in <c>WallpaperPage.Patterns.cs</c>.
    /// </summary>
    static partial void AddPatternOptions(StackPanel section, SettingsContext ctx);

    /// <summary>
    /// Visualisers: the whole "Visualiser" section and any after it ("Music response"), added to
    /// <paramref name="sections"/> (shown only while Visualisers is the type); set <paramref name="added"/> so the
    /// placeholder goes. Implement in <c>WallpaperPage.Visualiser.cs</c>.
    /// </summary>
    static partial void AddVisualiserSections(StackPanel sections, SettingsContext ctx, ref bool added);
}
