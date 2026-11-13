using Lunalyr.Shell;
using Lunalyr.Shell.LyricTools;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>General: how Lunalyr's windows look, starting at login, updates, the welcome, the tray's shortcuts and sharing.</summary>
internal static class GeneralPage
{
    public static StackPanel Build(SettingsContext ctx, string version, Func<string?> themeOverride,
                                   Lunalyr.Shell.Extras.GlobalHotkeys? hotkeys = null, DiscordHooks? discord = null,
                                   Lunalyr.App.Hosting.PackagingHooks? packaging = null)
    {
        var page = Page();
        if (packaging?.SettingsProblem is { } problem)
            page.Children.Add(new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = true, IsOpen = true,
                Title = "Your settings file couldn’t be read", Message = problem });
        Section(page, "Appearance");
        var theme = Card("App theme", "How Lunalyr’s own windows look. It doesn’t change your wallpaper or Windows’ own setting.", "",
            Combo(ctx, SettingsCopy.AppThemes, s => s.AppTheme, (s, v) => s with { AppTheme = v }, width: 200));
        ctx.OnRefresh(_ =>
        {
            if (themeOverride() is { } forced)
                theme.Description = $"How Lunalyr’s own windows look. This run shows them {forced} (--theme); your choice applies next time.";
        });
        page.Children.Add(theme);

        Section(page, "Startup");
        PackagingRows.AddStartup(page, ctx, packaging);

        Section(page, "Updates");
        PackagingRows.AddUpdates(page, ctx, version, packaging);

        Section(page, "Welcome");
        // The first-run welcome again (docs/APP-SHELL.md, "Onboarding"): the same steps; finishing just closes.
        page.Children.Add(Card("Show welcome again", "Music players, a look and the tray.", "",
            Button("Show Welcome…", () => App.CurrentHost?.ShowWelcomeAgain())));

        Section(page, "Tray & shortcuts");
        ExtrasSections.AddTrayLyric(page, ctx);
        ShortcutsSection.Add(page, ctx, hotkeys);

        Section(page, "Discord & lyric card");
        ExtrasSections.AddDiscord(page, ctx, discord);
        LyricCardRows.Add(page, ctx);
        return page;
    }
}

/// <summary>Readings (the Mac's "Romanisation"): readings under the lyrics or instead of them, the language and saved readings.</summary>
internal static class ReadingsPage
{
    public static StackPanel Build(SettingsContext ctx, Func<AppController?>? controller = null, Func<Task<string?>>? chooseReadingFile = null)
    {
        var page = Page();
        Section(page, "Romanisation");
        // Display belongs to a lyric style (LyricStyleProfiles): say which one while the lyrics float.
        var styleNote = Caption("");
        ctx.OnRefresh(s => styleNote.Text = FloatingCopy.EditingNote(s.Surface) ?? "");
        page.Children.Add(styleNote.ShowWhen(ctx, s => s.IsFloating));
        page.Children.Add(Card("Display", "Readings follow the original timing. Local transliteration can miss names and unusual pronunciations.", "",
            Combo(ctx, SettingsCopy.ReadingDisplays, s => s.RomanizationDisplay, (s, v) => s with { RomanizationDisplay = v }, width: 200)));
        page.Children.Add(Card("Language",
            "Lunalyr can’t generate Jyutping on this PC: lyrics written in Cantonese show only readings from the lyric source. Cantonese songs in standard written Chinese look like Mandarin, so choose Cantonese for them.",
            "", Combo(ctx, SettingsCopy.ReadingLanguages, s => s.RomanizationLanguage, (s, v) => s with { RomanizationLanguage = v })));
        page.Children.Add(Card("Keep tone marks", "Pinyin with its tone marks (nǐ hǎo) rather than without (ni hao).", "",
            Toggle(ctx, s => s.RomanizationTones, (s, v) => s with { RomanizationTones = v })));
        page.Children.Add(Card("Prefer saved readings", "Use readings that came with the lyrics before Lunalyr’s own.", "",
            Toggle(ctx, s => s.RomanizationSaved, (s, v) => s with { RomanizationSaved = v })));

        Section(page, "Saved readings");
        // The Mac's Import Reading LRC…: readings matched to the lines showing by time, then saved with the recording.
        var import = new Button { Content = "Import…" };
        AutomationProperties.SetName(import, "Import reading LRC");
        page.Children.Add(Card("Import reading LRC", "Use readings from an LRC file for the lyrics showing.", "", import));
        var needsLyrics = Caption(LyricToolsText.ReadingsNeedLyrics);
        page.Children.Add(needsLyrics);
        var warning = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };
        var note = Caption("");
        note.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(note, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(warning, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        page.Children.Add(warning);
        page.Children.Add(note);
        import.Click += async (_, _) =>
        {
            if (chooseReadingFile is null || await chooseReadingFile() is not { Length: > 0 } path) return;
            if (controller?.Invoke() is not { } c) return;
            var result = c.ImportReadingsFile(path);
            warning.Message = result.Failed ? result.Message : "";
            warning.IsOpen = result.Failed;
            note.Text = result.Failed ? "" : result.Message;
            note.Visibility = result.Failed ? Visibility.Collapsed : Visibility.Visible;
            ctx.QueueRefresh();
        };
        ctx.OnRefresh(_ =>
        {
            var showing = controller?.Invoke()?.HasSyncedLyrics == true;
            import.IsEnabled = showing && chooseReadingFile is not null;
            needsLyrics.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
        });
        page.Children.Add(Caption("Saved readings are pronunciation guides. Japanese readings are matched to the lyric’s kana, however they’re spaced; a reading written for other words isn’t shown, and Lunalyr’s own reading is used for that line."));

        Section(page, "Translations");
        page.Children.Add(Card("Show translations under lyrics", "Shown when the lyric source has a translation. Lyrics are never sent anywhere to be translated.",
            "", FixedToggle(false), notYet: SettingsCopy.LaterBuild));
        return page;
    }
}

/// <summary>Music players: which players Lunalyr follows, and what it reads.</summary>
internal static class PlayersPage
{
    public static StackPanel Build(SettingsContext ctx, Func<AppController?> controller,
        Func<Lunalyr.Core.Player.Spotify.SpotifyAccount?>? spotify = null, Func<string?>? spotifyOff = null)
    {
        var page = Page();
        var following = Card("Following", null, "");
        ctx.OnRefresh(_ =>
        {
            var status = controller()?.Status;
            following.Description = status?.Player is { } player
                ? $"{player}{(status.Title is { } title ? $": {title}" : "")}"
                : "No player is sharing what it plays right now.";
        });
        page.Children.Add(following);
        page.Children.Add(Card("Player", "Automatic follows whichever player is playing. Choosing one means Lunalyr never reads the others.", "",
            FixedCombo("Automatic"), notYet: SettingsCopy.LaterBuild));
        page.Children.Add(Card("Players Windows knows about",
            "Lunalyr follows any app that shares what it plays with Windows (the media controls by the volume flyout): Spotify, Apple Music, browsers playing YouTube Music or SoundCloud, and more.",
            ""));
        page.Children.Add(Caption("Lunalyr only reads the song and its position. It controls playback only when you use its own buttons."));
        if (spotify is not null) SpotifySection.Add(page, ctx, spotify, spotifyOff ?? (() => null));
        return page;
    }
}

/// <summary>Performance: frame rate and when the wallpaper pauses.</summary>
internal static class PerformancePage
{
    public static StackPanel Build(SettingsContext ctx)
    {
        var page = Page();
        Section(page, "Rendering and power");
        page.Children.Add(Card("Frame rate", "How smoothly the wallpaper moves. Lower rates use less power.", "",
            Combo(ctx, SettingsCopy.FrameRates, s => s.AnimationRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (s, v) => s with { AnimationRate = int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) }, width: 200)));
        page.Children.Add(Card("Pause while energy saver is on", "The wallpaper holds still while Windows’ energy saver (battery saver) is on.", "",
            Toggle(ctx, s => s.PauseOnBatterySaver, (s, v) => s with { PauseOnBatterySaver = v })));
        var covered = Card("Pause fully covered wallpapers", null, "",
            Toggle(ctx, s => s.PauseWhenCovered, (s, v) => s with { PauseWhenCovered = v }));
        ctx.OnRefresh(s => covered.Description = s.PauseWhenCovered
            ? "Goes by where windows are: a wallpaper pauses while app windows cover its display. If a wallpaper you can see through a window holds still, switch this off."
            : "The wallpaper keeps moving behind your windows.");
        page.Children.Add(covered);
        page.Children.Add(Card("Pause behind full-screen apps and games",
            "Always on: a game or full-screen app pauses the wallpaper on its display, so it never shares the graphics card with it.",
            "", FixedToggle(true)));
        page.Children.Add(Card("Low power visuals", "Caps Line patterns at 30 fps.", "", FixedToggle(false), notYet: SettingsCopy.LaterBuild));

        Section(page, "Desktop");
        // The Mac's Performance › Desktop switch (custom.showControlsOnHover), live: the card follows at once.
        var hover = Card("Show player on hover", null, "",
            Toggle(ctx, s => s.ShowControlsOnHover, (s, v) => s with { ShowControlsOnHover = v }));
        ctx.OnRefresh(s => hover.Description = SpotifyCopy.HoverCaption(s.ShowControlsOnHover));
        page.Children.Add(hover);
        return page;
    }
}
