using Lunalyr.Core.NowPlaying;
using Lunalyr.Shell;
using Lunalyr.Shell.LyricTools;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsGroups;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>
/// General: the music player followed (once its own page), lyrics on screen, the desktop player and the tray line, then
/// the app itself: theme, starting at login, updates and the welcome.
/// </summary>
internal static class GeneralPage
{
    public static StackPanel Build(SettingsContext ctx, string version, Func<string?> themeOverride, Func<AppController?> controller,
                                   Func<IReadOnlyList<MediaSourceApp>> sharing, Lunalyr.App.Hosting.PackagingHooks? packaging = null,
                                   MirroringHooks? mirroring = null)
    {
        var page = Page();
        if (packaging?.SettingsProblem is { } problem)
            page.Children.Add(new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = true, IsOpen = true,
                Title = "Your settings file couldn’t be read", Message = problem });

        // ---- Music: which player is followed (the Mac's custom.player; a chosen player's session is the only one read) ----
        Section(page, "Music");
        var player = Card("Music player", null, "", PlayerChoiceCombo.Build(ctx, sharing));
        ctx.OnRefresh(_ =>
        {
            var status = controller()?.Status;
            var now = status?.Player is { } name
                ? $"Following {name}{(status.Title is { } title ? $": {title}" : "")}."
                : "No player is sharing what it plays right now.";
            player.Description = $"{now} Automatic follows whichever player is playing.";
        });
        page.Children.Add(player);
        page.Children.Add(Caption("Lunalyr only reads the song and its position. It controls playback only when you use its own buttons."));

        // ---- Lyrics on screen ----
        Section(page, "Lyrics on screen");
        LyricsOnScreenSection.Add(page, ctx, mirroring);
        // The desktop player (once Performance › Desktop): the card follows at once.
        var hover = Card("Show player on hover", null, "",
            Toggle(ctx, s => s.ShowControlsOnHover, (s, v) => s with { ShowControlsOnHover = v }));
        ctx.OnRefresh(s => hover.Description = DesktopPlayerCopy.HoverCaption(s.ShowControlsOnHover));
        page.Children.Add(hover);
        ExtrasSections.AddTrayLyric(page, ctx);
        // Once on Lyric style › Layout: it governs every animation, not a layout.
        page.Children.Add(Advanced(Card("Follow Windows’ Animation effects",
            "On: with Animation effects off in Windows, lyrics keep still and the music video shows a still. Off: Lunalyr animates anyway. Visualiser beat flashes always follow Windows.",
            "", Toggle(ctx, s => s.MotionFollowsWindows, (s, v) => s with { MotionFollowsWindows = v, MotionNoticeShown = true }))));

        // ---- App ----
        Section(page, "App");
        var theme = Card("App theme", "How Lunalyr’s own windows look.", "",
            Combo(ctx, SettingsCopy.AppThemes, s => s.AppTheme, (s, v) => s with { AppTheme = v }, width: 200));
        ctx.OnRefresh(_ =>
        {
            if (themeOverride() is { } forced)
                theme.Description = $"How Lunalyr’s own windows look. This run shows them {forced} (--theme); your choice applies next time.";
        });
        page.Children.Add(theme);
        PackagingRows.AddStartup(page, ctx, packaging);
        PackagingRows.AddUpdates(page, ctx, version, packaging);
        // The first-run welcome again (docs/APP-SHELL.md, "Onboarding"): the same steps; finishing just closes.
        page.Children.Add(Advanced(Card("Show welcome again", "Music players, a look and the tray.", "",
            Button("Show welcome…", () => App.CurrentHost?.ShowWelcomeAgain()))));
        return page;
    }
}

/// <summary>Sharing: Discord status and the lyric card (both once on General).</summary>
internal static class SharingPage
{
    public static StackPanel Build(SettingsContext ctx, DiscordHooks? discord = null)
    {
        var page = Page();
        Section(page, "Discord");
        ExtrasSections.AddDiscord(page, ctx, discord);
        Section(page, "Lyric card");
        LyricCardRows.Add(page, ctx);
        return page;
    }
}

/// <summary>Readings &amp; translations (the Mac's "Romanisation"): readings under the lyrics or instead of them, the language and saved readings, and translations.</summary>
internal static class ReadingsPage
{
    public static StackPanel Build(SettingsContext ctx, Func<AppController?>? controller = null, Func<Task<string?>>? chooseReadingFile = null)
    {
        var page = Page();
        Section(page, "Readings");
        // Display belongs to a lyric style (LyricStyleProfiles): say which one while the lyrics float.
        var styleNote = Caption("");
        ctx.OnRefresh(s => styleNote.Text = FloatingCopy.EditingNote(s.Surface) ?? "");
        page.Children.Add(styleNote.ShowWhen(ctx, s => s.IsFloating));
        page.Children.Add(Card("Show readings", "Pronunciation in Latin letters, under the lyrics or instead of them.", "",
            Combo(ctx, SettingsCopy.ReadingDisplays, s => s.RomanizationDisplay, (s, v) => s with { RomanizationDisplay = v }, width: 200)));

        // Advanced readings
        var advancedStart = page.Children.Count;
        page.Children.Add(Card("Language",
            "Lunalyr can’t generate Jyutping on this PC: lyrics written in Cantonese show only readings from the lyric source. Cantonese songs in standard written Chinese look like Mandarin, so choose Cantonese for them.",
            "", Combo(ctx, SettingsCopy.ReadingLanguages, s => s.RomanizationLanguage, (s, v) => s with { RomanizationLanguage = v })));
        page.Children.Add(Card("Keep tone marks", "Pinyin with its tone marks (nǐ hǎo) rather than without (ni hao).", "",
            Toggle(ctx, s => s.RomanizationTones, (s, v) => s with { RomanizationTones = v })));
        page.Children.Add(Card("Prefer saved readings", "Use readings that came with the lyrics before Lunalyr’s own.", "",
            Toggle(ctx, s => s.RomanizationSaved, (s, v) => s with { RomanizationSaved = v })));
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
        AdvancedFrom(page, advancedStart);

        Section(page, "Translations");
        // The Mac's TranslationSection: translations that came with the lyrics. Windows has no on-device translator, so the
        // Mac's controls for translating on the Mac have no counterpart (docs/READINGS-WINDOWS.md).
        page.Children.Add(Card(TranslationsCopy.Title, TranslationsCopy.Description, "",
            Toggle(ctx, s => s.ShowTranslations, (s, v) => s with { ShowTranslations = v })));
        return page;
    }
}

/// <summary>Performance: frame rate, power, when the wallpaper pauses, and troubleshooting.</summary>
internal static class PerformancePage
{
    /// <summary>
    /// "Lighter wallpaper": Low power visuals and Follow energy saver as one menu (the Settings reorganisation). Both keys
    /// stay; the menu writes only when it is changed, so the four stored combinations are never rewritten on opening.
    /// </summary>
    internal static readonly SettingsChoice[] LighterChoices =
    [
        new("never", "Never"), new("energySaver", "With energy saver"), new("always", "Always"),
    ];

    internal static string Lighter(AppSettings s) => s.LowPowerVisuals ? "always" : s.PauseOnBatterySaver ? "energySaver" : "never";

    internal static AppSettings WithLighter(AppSettings s, string value) => value switch
    {
        "always" => s with { LowPowerVisuals = true },
        "energySaver" => s with { LowPowerVisuals = false, PauseOnBatterySaver = true },
        _ => s with { LowPowerVisuals = false, PauseOnBatterySaver = false },
    };

    public static StackPanel Build(SettingsContext ctx, Func<AppController?>? controller = null)
    {
        var page = Page();
        Section(page, "Rendering and power");
        page.Children.Add(Card("Frame rate", "How smoothly the wallpaper moves. Lower rates use less power.", "",
            Combo(ctx, SettingsCopy.FrameRates, s => s.AnimationRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (s, v) => s with { AnimationRate = int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) }, width: 200)));
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

        // Advanced rendering and power
        // The Mac's Low power visuals (custom.lowPowerVisuals) and "Follow macOS Low Power Mode" (pauseOnBatterySaver).
        page.Children.Add(Advanced(Card("Lighter wallpaper", "Line patterns at no more than 30 fps, to save power.", "",
            Combo(ctx, LighterChoices, Lighter, WithLighter, width: 220))));
        var covered = Card("Pause when windows cover it", null, "",
            Toggle(ctx, s => s.PauseWhenCovered, (s, v) => s with { PauseWhenCovered = v }));
        ctx.OnRefresh(s => covered.Description = s.PauseWhenCovered
            ? "A wallpaper pauses while app windows cover its display. If one you can see through a window holds still, switch this off."
            : "The wallpaper keeps moving behind your windows.");
        page.Children.Add(Advanced(covered));
        // A game or full-screen app always pauses the wallpaper on its display; its fixed switch was cut (15 Nov 2026).

        // Tester feedback on 0.9.0: a log can be weeks away, a paste isn't.
        Section(page, "Troubleshooting");
        const string diagnosticsText =
            "Your graphics card, displays, HDR and refresh rate, what each display's wallpaper is doing and the last problems, ready to paste into a message. Nothing is sent anywhere.";
        Card? diagnostics = null;
        diagnostics = Card("Copy diagnostics", diagnosticsText, "", Button("Copy diagnostics", async () =>
        {
            if (App.CurrentHost is not { } host) return;
            // The result shows in the row itself (a caption under it left a gap while it was empty).
            diagnostics!.Description = "Collecting…";
            diagnostics.Description = await host.CopyDiagnosticsAsync()
                ? "Copied. Paste it into your message."
                : "Couldn't copy to the clipboard. " + diagnosticsText;
        }));
        page.Children.Add(diagnostics);
        page.Children.Add(Advanced(Card("Simpler drawing (if it flickers)",
            "Draws the wallpaper a simpler way; takes effect when Lunalyr next starts. “Your wallpaper” and YouTube music videos need it off and show the album colours instead.",
            "", Toggle(ctx, s => s.ReduceFlicker, (s, v) => s with { ReduceFlicker = v }))));
        return page;
    }
}
