using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Wallpaper;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>
/// Wallpaper (the Mac's <c>WallpaperPage</c>): the type as a grid of tiles, the type's own section, legibility and the
/// adjustments every drawn type shares. GPU effects, line patterns and visualisers add their own rows through the
/// partial methods at the end, implemented in their own files (docs/APP-SHELL.md, "Wallpaper types: hooks for other
/// layers"). Ported from WallpaperPage.swift and QuickControls.swift's WallpaperModeGrid (Lunalyr's own lines).
/// </summary>
internal static partial class WallpaperPage
{
    /// <param name="chooseFile">Windows' file picker for a picture or video; null when cancelled.</param>
    /// <param name="reduceMotion">Windows' "Animation effects" are off.</param>
    public static StackPanel Build(SettingsContext ctx, WallpaperImporter importer, Func<Task<string?>> chooseFile, Func<bool> reduceMotion)
    {
        var page = Page();

        // ---- The type ----
        Section(page, "Wallpaper");
        async void Import()
        {
            if (importer.Importing) return;
            if (await chooseFile() is { } path) await importer.ImportAsync(path);
        }
        page.Children.Add(new Border { Style = Resource<Style>("Card"), Child = TypeGrid(ctx, Import) });
        var summary = Caption("");
        ctx.OnRefresh(s => summary.Text = SettingsCopy.WallpaperTypeOf(s.BackgroundMode).Summary);
        page.Children.Add(summary);
        // A failed import leaves the type as it was, so its message shows here while the Image or video section isn't
        // showing (the Mac's rule).
        var importNote = new InfoBar { Severity = InfoBarSeverity.Warning, Title = "Couldn’t use that file", IsClosable = false };
        page.Children.Add(importNote);

        // ---- The type's own section ----
        page.Children.Add(MatchSection(ctx).ShowWhen(ctx, s => s.BackgroundMode == "auto"));
        page.Children.Add(EffectsSection(ctx, "GPU effects").ShowWhen(ctx, s => s.BackgroundMode == "shader"));
        page.Children.Add(PatternsSection(ctx, "Line patterns").ShowWhen(ctx, s => s.BackgroundMode == "pattern"));
        var visualiser = new StackPanel { Spacing = 4 }.ShowWhen(ctx, s => s.BackgroundMode == "visualizer");
        var added = false;
        AddVisualiserSections(visualiser, ctx, ref added);
        if (!added)
        {
            Section(visualiser, "Visualiser");
            visualiser.Children.Add(Card("Scene", "Scenes that move with the music itself.", "", FixedCombo("Visualisers"),
                notYet: SettingsCopy.Milestone6Later));
            visualiser.Children.Add(Caption(SettingsCopy.VisualisersNotYet));
        }
        page.Children.Add(visualiser);
        var musicVideo = new StackPanel { Spacing = 4 }.ShowWhen(ctx, s => s.BackgroundMode == "musicVideo");
        var musicVideoAdded = false;
        AddMusicVideoSections(musicVideo, ctx, ref musicVideoAdded);
        page.Children.Add(musicVideo);
        page.Children.Add(DesktopSection().ShowWhen(ctx, s => s.BackgroundMode == "desktop"));
        page.Children.Add(ImportedSection(ctx, importer, Import, reduceMotion).ShowWhen(ctx, s => s.BackgroundMode == "custom"));
        page.Children.Add(ColourOnlySection(ctx).ShowWhen(ctx, s => s.BackgroundMode == "colourOnly"));

        void ShowImport()
        {
            var custom = ctx.Current.BackgroundMode == "custom";
            importNote.Message = importer.Message ?? "";
            importNote.IsOpen = importer.Message is not null && !custom;
            importNote.Visibility = importNote.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        }
        importer.Changed += ShowImport;
        ctx.Detached += () => importer.Changed -= ShowImport;
        ctx.OnRefresh(_ => ShowImport());

        // ---- Legibility ----
        Section(page, "Legibility");
        var backing = Card("Automatic text backing", null, "",
            Toggle(ctx, s => s.TextBackingAutomatic, (s, v) => s with { TextBackingAutomatic = v }));
        ctx.OnRefresh(s => backing.Description = SettingsCopy.BackingCaption(s.BackgroundMode == "desktop", s.TextBackingAutomatic));
        page.Children.Add(backing);
        page.Children.Add(Card("Text backing", null, "",
            Slider(ctx, 0, 1, 0.01, SettingsCopy.PercentOrNone, s => s.TextBacking, (s, v) => s with { TextBacking = v }))
            .ShowWhen(ctx, s => !s.TextBackingAutomatic));

        // ---- Adjustments (not for Your wallpaper: nothing of Lunalyr's is drawn to adjust) ----
        var adjustments = new StackPanel { Spacing = 4 }.ShowWhen(ctx, s => s.BackgroundMode != "desktop");
        Section(adjustments, "Adjustments");
        adjustments.Children.Add(Card("Motion speed", null, "",
            Slider(ctx, AppSettings.SpeedMin, AppSettings.SpeedMax, 0.05, SettingsCopy.Multiplier, s => s.EffectSpeed, (s, v) => s with { EffectSpeed = v }))
            .ShowWhen(ctx, s => s.BackgroundMode is not ("custom" or "musicVideo")));
        adjustments.Children.Add(Card("Colour richness", null, "",
            Slider(ctx, AppSettings.VividnessMin, AppSettings.VividnessMax, 0.01, SettingsCopy.Percent, s => s.Vividness, (s, v) => s with { Vividness = v }))
            .ShowWhen(ctx, s => s.BackgroundMode is not ("custom" or "musicVideo")));
        adjustments.Children.Add(Card("Darkening", null, "",
            Slider(ctx, AppSettings.ScrimMin, AppSettings.ScrimMax, 0.01, SettingsCopy.Percent, s => s.ScrimStrength, (s, v) => s with { ScrimStrength = v })));
        adjustments.Children.Add(Card("Pattern visibility", null, "",
            Slider(ctx, 0, 1, 0.01, SettingsCopy.Percent, s => s.PatternOpacity, (s, v) => s with { PatternOpacity = v }))
            .ShowWhen(ctx, s => s.BackgroundMode == "pattern"));
        var reset = Button("Reset Adjustments", () => ctx.Update(s => s.WithDefaultAdjustments()));
        ToolTipService.SetToolTip(reset, SettingsCopy.ResetAdjustmentsHelp);
        AutomationProperties.SetHelpText(reset, SettingsCopy.ResetAdjustmentsHelp);
        ctx.OnRefresh(s => reset.IsEnabled = !s.AdjustmentsAreDefault);
        adjustments.Children.Add(new Border { Style = Resource<Style>("Card"), Child = reset });
        page.Children.Add(adjustments);
        return page;
    }

    /// <summary>
    /// Every type as a tile, the chosen one lit: the five Lunalyr draws in a row of small tiles, the three that start
    /// from something of yours in a row of wide ones. Imported image or video with no file yet asks for one first.
    /// </summary>
    internal static StackPanel TypeGrid(SettingsContext ctx, Action import)
    {
        var grid = new StackPanel { Spacing = 6 };
        AutomationProperties.SetName(grid, "Wallpaper type");
        var tiles = new List<(ToggleButton Tile, string Value)>();
        foreach (var row in new[] { SettingsCopy.WallpaperTypes.Take(5).ToArray(), SettingsCopy.WallpaperTypes.Skip(5).ToArray() })
        {
            var wide = row.Length < 5;
            var line = new Grid { ColumnSpacing = 6 };
            for (var i = 0; i < row.Length; i++)
            {
                var type = row[i];
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var content = new StackPanel
                {
                    Orientation = wide ? Orientation.Horizontal : Orientation.Vertical, Spacing = wide ? 8 : 4,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                content.Children.Add(Icon(type.Glyph, wide ? 14 : 18));
                content.Children.Add(new TextBlock
                {
                    Text = type.ShortTitle, TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                });
                var tile = new ToggleButton
                {
                    Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = wide ? 38 : 58,
                    Padding = new Thickness(4, 6, 4, 6), IsEnabled = type.Available,
                };
                AutomationProperties.SetName(tile, type.Title);
                AutomationProperties.SetHelpText(tile, type.NotYet is null ? type.Summary : $"{type.Summary} {type.NotYet}");
                ToolTipService.SetToolTip(tile, type.NotYet is null ? $"{type.Title}: {type.Summary}" : $"{type.Title}: {type.NotYet}");
                tile.Click += (_, _) =>
                {
                    // A click never unlights the chosen tile: the type stays until another is chosen.
                    tile.IsChecked = ctx.Current.BackgroundMode == type.Value;
                    if (type.Value == "custom" && !Lunalyr.Platform.Wallpaper.CustomWallpaperStore.IsAvailable(ctx.Current.WallpaperPath))
                    {
                        import();
                        return;
                    }
                    ctx.Update(s => s with { BackgroundMode = type.Value });
                };
                Grid.SetColumn(tile, i);
                line.Children.Add(tile);
                tiles.Add((tile, type.Value));
            }
            grid.Children.Add(line);
        }
        ctx.OnRefresh(s =>
        {
            foreach (var (tile, value) in tiles) tile.IsChecked = value == s.BackgroundMode;
        });
        return grid;
    }

    private static StackPanel MatchSection(SettingsContext ctx)
    {
        var section = new StackPanel { Spacing = 4 };
        Section(section, "Match the music");
        section.Children.Add(EffectMenu(ctx));
        AddEffectOptions(section, ctx);
        section.Children.Add(PatternMenu(ctx));
        AddPatternOptions(section, ctx);
        section.Children.Add(Caption(SettingsCopy.MatchTheMusicPicks));
        return section;
    }

    private static StackPanel EffectsSection(SettingsContext ctx, string title)
    {
        var section = new StackPanel { Spacing = 4 };
        Section(section, title);
        section.Children.Add(EffectMenu(ctx));
        AddEffectOptions(section, ctx);
        return section;
    }

    private static StackPanel PatternsSection(SettingsContext ctx, string title)
    {
        var section = new StackPanel { Spacing = 4 };
        Section(section, title);
        section.Children.Add(PatternMenu(ctx));
        AddPatternOptions(section, ctx);
        return section;
    }

    private static Card EffectMenu(SettingsContext ctx) => StyleMenu(ctx, "GPU effect", "", WallpaperCatalog.Effects,
        s => s.ShaderStyle, (s, v) => s with { ShaderStyle = v }, SettingsCopy.EffectsNotYet);

    private static Card PatternMenu(SettingsContext ctx) => StyleMenu(ctx, "Pattern", "", WallpaperCatalog.Patterns,
        s => s.PatternStyle, (s, v) => s with { PatternStyle = v }, SettingsCopy.PatternsNotYet);

    /// <summary>"Match the music" and every style the catalogue has; disabled, with why, while it has none.</summary>
    private static Card StyleMenu(SettingsContext ctx, string title, string glyph, IReadOnlyList<WallpaperStyle> styles,
        Func<AppSettings, string> get, Func<AppSettings, string, AppSettings> set, string notYet)
    {
        if (styles.Count == 0) return Card(title, null, glyph, FixedCombo("Match the music"), notYet: notYet);
        var choices = SettingsCopy.StyleChoices(styles.Select(s => (s.Id, s.Title, s.Detail)));
        // A pinned style this build doesn't have shows as Match the music (which is what draws).
        return Card(title, null, glyph, Combo(ctx, choices, s => choices.Any(c => c.Value == get(s)) ? get(s) : "", set));
    }

    private static StackPanel DesktopSection()
    {
        var section = new StackPanel { Spacing = 4 };
        Section(section, "Your wallpaper");
        section.Children.Add(Card("Lyrics over your own wallpaper", SettingsCopy.YourWallpaper, ""));
        section.Children.Add(Caption(SettingsCopy.YourWallpaperPreview));
        return section;
    }

    private static StackPanel ImportedSection(SettingsContext ctx, WallpaperImporter importer, Action import, Func<bool> reduceMotion)
    {
        var section = new StackPanel { Spacing = 4 };
        Section(section, "Image or video");
        var progress = new ProgressRing { IsActive = false, Width = 18, Height = 18, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(progress, "Importing");
        var choose = Button("Choose…", import, name: "Choose Image or Video");
        ToolTipService.SetToolTip(choose, "Choose a picture or video for the wallpaper");
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(progress);
        controls.Children.Add(choose);
        var file = Card("File", null, "", controls);
        ctx.OnRefresh(s => file.Description = WallpaperImporter.FileLabel(s.WallpaperName, s.WallpaperPath));
        section.Children.Add(file);
        var placement = Card("Placement", null, "",
            Combo(ctx, SettingsCopy.Placements, s => s.WallpaperPlacement, (s, v) => s with { WallpaperPlacement = v }, width: 200));
        ctx.OnRefresh(s => placement.Description = SettingsCopy.Placements.FirstOrDefault(p => p.Value == s.WallpaperPlacement)?.Detail ?? "");
        section.Children.Add(placement);
        var detail = Caption("");
        ctx.OnRefresh(_ => detail.Text = WallpaperImporter.Detail(reduceMotion()));
        section.Children.Add(detail);
        var failure = new InfoBar { Severity = InfoBarSeverity.Warning, Title = "Couldn’t use that file", IsClosable = false };
        section.Children.Add(failure);
        void Show()
        {
            choose.Content = importer.Importing ? "Importing…" : "Choose…";
            choose.IsEnabled = !importer.Importing;
            progress.IsActive = importer.Importing;
            progress.Visibility = importer.Importing ? Visibility.Visible : Visibility.Collapsed;
            failure.Message = importer.Message ?? "";
            failure.IsOpen = importer.Message is not null;
            failure.Visibility = failure.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        }
        importer.Changed += Show;
        ctx.Detached += () => importer.Changed -= Show;
        Show();
        return section;
    }

    private static StackPanel ColourOnlySection(SettingsContext ctx)
    {
        var section = new StackPanel { Spacing = 4 };
        Section(section, "Colour only");
        var gradient = Card("Album gradient", SettingsCopy.AlbumGradientCaption, "",
            Toggle(ctx, s => s.AlbumGradientWallpaper, (s, v) => s with { AlbumGradientWallpaper = v }));
        ctx.OnRefresh(s => gradient.Description = s.AlbumGradientWallpaper ? SettingsCopy.AlbumGradientCaption : SettingsCopy.AlbumGradientOff);
        section.Children.Add(gradient);
        return section;
    }

    // ---- Hooks: implemented by the GPU effects / line patterns / visualiser layers in their own files ----

    /// <summary>
    /// GPU effects: rows of the effects' own options, added to <paramref name="section"/> under the "GPU effect" menu
    /// (in the Match the music and GPU effects sections). Implement in <c>WallpaperPage.Effects.cs</c>; it is called once
    /// per section, so make fresh controls each time.
    /// </summary>
    static partial void AddEffectOptions(StackPanel section, SettingsContext ctx);

    /// <summary>
    /// Line patterns: rows of the patterns' own options, added to <paramref name="section"/> under the "Pattern" menu (in
    /// the Match the music and Line patterns sections). Implement in <c>WallpaperPage.Patterns.cs</c>; called once per
    /// section.
    /// </summary>
    static partial void AddPatternOptions(StackPanel section, SettingsContext ctx);

    /// <summary>
    /// Visualisers: the whole "Visualiser" section and any after it ("Music response"), added to
    /// <paramref name="sections"/> (shown only while Visualisers is the type); set <paramref name="added"/> so the
    /// placeholder goes. Implement in <c>WallpaperPage.Visualiser.cs</c>.
    /// </summary>
    static partial void AddVisualiserSections(StackPanel sections, SettingsContext ctx, ref bool added);

    /// <summary>
    /// Music video: its whole section (what shows, YouTube search and its key, the fallback, this song's file, link and
    /// timing, dimming and blur), shown only while Music video is the type. Implemented in <c>WallpaperPage.MusicVideo.cs</c>.
    /// </summary>
    static partial void AddMusicVideoSections(StackPanel sections, SettingsContext ctx, ref bool added);
}
