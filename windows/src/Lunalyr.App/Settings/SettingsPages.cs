using Lunalyr.Shell;
using Lunalyr.Shell.LyricTools;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>General: how Lunalyr's windows look, starting at login, updates, the welcome, the tray, lyrics on screen and sharing.</summary>
internal static class GeneralPage
{
    public static StackPanel Build(SettingsContext ctx, string version, Func<string?> themeOverride,
                                   DiscordHooks? discord = null,
                                   Lunalyr.App.Hosting.PackagingHooks? packaging = null, MirroringHooks? mirroring = null)
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
            Button("Show welcome…", () => App.CurrentHost?.ShowWelcomeAgain())));

        // Tester feedback on 0.9.0: a log can be weeks away, a paste isn't.
        Section(page, "Troubleshooting");
        var copied = Caption("");
        page.Children.Add(Card("Copy diagnostics",
            "Your graphics card, displays, HDR and refresh rate, what each display's wallpaper is doing and the last problems, ready to paste into a message. Nothing is sent anywhere.",
            "", Button("Copy diagnostics", async () =>
            {
                if (App.CurrentHost is not { } host) return;
                copied.Text = "Collecting…";
                copied.Text = await host.CopyDiagnosticsAsync() ? "Copied. Paste it into your message." : "Couldn't copy to the clipboard.";
            })));
        page.Children.Add(copied);
        page.Children.Add(Card("Reduce flicker (compatibility)",
            "If the wallpaper or music video flickers, this draws the wallpaper a simpler way. Takes effect when Lunalyr next starts. “Your wallpaper” and YouTube music videos need it off and show the album colours instead.",
            "", Toggle(ctx, s => s.ReduceFlicker, (s, v) => s with { ReduceFlicker = v })));

        Section(page, "Tray");
        ExtrasSections.AddTrayLyric(page, ctx);

        Section(page, "Lyrics on screen");
        LyricsOnScreenSection.Add(page, ctx, mirroring);

        Section(page, "Discord and lyric card");
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
        // The Mac's TranslationSection: translations that came with the lyrics. Windows has no on-device translator, so the
        // Mac's controls for translating on the Mac have no counterpart (docs/READINGS-WINDOWS.md).
        page.Children.Add(Card(TranslationsCopy.Title, TranslationsCopy.Description, "",
            Toggle(ctx, s => s.ShowTranslations, (s, v) => s with { ShowTranslations = v })));
        return page;
    }
}

/// <summary>Music players: which players Lunalyr follows, and what it reads.</summary>
internal static class PlayersPage
{
    public static StackPanel Build(SettingsContext ctx, Func<AppController?> controller,
        Func<IReadOnlyList<Lunalyr.Core.NowPlaying.MediaSourceApp>>? sharing = null)
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
        // The Mac's custom.player: a chosen player's session is the only one followed (FollowedSessionSource).
        page.Children.Add(Card("Player", "Automatic follows whichever player is playing. Choosing one means Lunalyr never reads the others.", "",
            PlayerChoiceCombo.Build(ctx, sharing ?? (() => []))));
        page.Children.Add(Card("Players Windows knows about",
            "Lunalyr follows any app that shares what it plays with Windows (the media controls by the volume flyout): Spotify, Apple Music, browsers playing YouTube Music or SoundCloud, and more.",
            ""));
        page.Children.Add(Caption("Lunalyr only reads the song and its position. It controls playback only when you use its own buttons."));
        return page;
    }
}

/// <summary>Performance: frame rate and when the wallpaper pauses.</summary>
internal static class PerformancePage
{
    public static StackPanel Build(SettingsContext ctx, Func<AppController?>? controller = null)
    {
        var page = Page();
        Section(page, "Rendering and power");
        page.Children.Add(Card("Frame rate", "How smoothly the wallpaper moves. Lower rates use less power.", "",
            Combo(ctx, SettingsCopy.FrameRates, s => s.AnimationRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (s, v) => s with { AnimationRate = int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) }, width: 200)));
        // The Mac's Low power visuals (custom.lowPowerVisuals): Line patterns at no more than 30 fps while on.
        page.Children.Add(Card("Low power visuals", PowerCopy.LowPowerVisuals, "",
            Toggle(ctx, s => s.LowPowerVisuals, (s, v) => s with { LowPowerVisuals = v })));
        // The Mac's "Follow macOS Low Power Mode" (key pauseOnBatterySaver): the same easing off while Energy saver is on.
        page.Children.Add(Card("Follow energy saver", PowerCopy.FollowEnergySaver, "",
            Toggle(ctx, s => s.PauseOnBatterySaver, (s, v) => s with { PauseOnBatterySaver = v })));
        // The Mac's "On battery" menu (custom.onBattery), with Windows' own wording.
        page.Children.Add(Card("On battery", PowerCopy.OnBatteryCaption, "",
            Combo(ctx, PowerCopy.OnBatteryChoices, s => s.OnBattery, (s, v) => s with { OnBattery = v }, width: 220)));
        // What power saving is doing now (the Mac's leaf captions): only while it changes something.
        var power = Caption("");
        page.Children.Add(power);
        void Follow(object? sender, EventArgs e) => ctx.QueueRefresh();
        if (controller?.Invoke() is { } app)
        {
            app.StatusChanged += Follow;
            ctx.Detached += () => app.StatusChanged -= Follow;
        }
        ctx.OnRefresh(s =>
        {
            power.Text = PowerCopy.InEffect(s, controller?.Invoke()?.Power) ?? "";
            power.Visibility = power.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        var covered = Card("Pause fully covered wallpapers", null, "",
            Toggle(ctx, s => s.PauseWhenCovered, (s, v) => s with { PauseWhenCovered = v }));
        ctx.OnRefresh(s => covered.Description = s.PauseWhenCovered
            ? "Goes by where windows are: a wallpaper pauses while app windows cover its display. If a wallpaper you can see through a window holds still, switch this off."
            : "The wallpaper keeps moving behind your windows.");
        page.Children.Add(covered);
        page.Children.Add(Card("Pause behind full-screen apps and games",
            "Always on: a game or full-screen app pauses the wallpaper on its display, so it never shares the graphics card with it.",
            "", FixedToggle(true)));

        Section(page, "Desktop");
        // The Mac's Performance › Desktop switch (custom.showControlsOnHover), live: the card follows at once.
        var hover = Card("Show player on hover", null, "",
            Toggle(ctx, s => s.ShowControlsOnHover, (s, v) => s with { ShowControlsOnHover = v }));
        ctx.OnRefresh(s => hover.Description = DesktopPlayerCopy.HoverCaption(s.ShowControlsOnHover));
        page.Children.Add(hover);
        return page;
    }
}
