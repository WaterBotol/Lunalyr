using Lunalyr.Shell;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>General: how Lunalyr's windows look, starting at login, updates, the welcome, the tray's shortcuts and sharing.</summary>
internal static class GeneralPage
{
    public static StackPanel Build(SettingsContext ctx, string version, Func<string?> themeOverride)
    {
        var page = Page();
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
        page.Children.Add(Card("Launch at login", "Start Lunalyr in the tray when you sign in to Windows.", "", FixedToggle(false),
            notYet: SettingsCopy.Milestone8));

        Section(page, "Updates");
        page.Children.Add(Card($"Lunalyr {version.Split('+')[0]}", "Lunalyr checks for new versions and installs them when you agree.", "",
            Button("Check for Updates…", () => { }), notYet: SettingsCopy.Milestone8));

        Section(page, "Welcome");
        page.Children.Add(Card("Show welcome again", "Music players, a look and the tray.", "",
            Button("Show Welcome…", () => { }), notYet: SettingsCopy.LaterBuild));

        Section(page, "Tray & shortcuts");
        page.Children.Add(Card("Lyric in the tray", "The current line beside the tray icon.", "", FixedToggle(false),
            notYet: SettingsCopy.Milestone7));
        page.Children.Add(Card("Keyboard shortcuts", "Hide lyrics, pause the wallpaper or copy a lyric card from anywhere.", "",
            FixedToggle(false), notYet: SettingsCopy.Milestone7));

        Section(page, "Discord & lyric card");
        page.Children.Add(Card("Discord status", "Show the song and line playing in your Discord status.", "", FixedToggle(false),
            notYet: SettingsCopy.Milestone7));
        page.Children.Add(Card("Lyric cards", "Save or copy a lyric as a picture, GIF or video.", "",
            Button("Make a Lyric Card…", () => { }), notYet: SettingsCopy.Milestone7));
        return page;
    }
}

/// <summary>Readings (the Mac's "Romanisation"): readings under the lyrics or instead of them, the language and saved readings.</summary>
internal static class ReadingsPage
{
    public static StackPanel Build(SettingsContext ctx)
    {
        var page = Page();
        Section(page, "Romanisation");
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
        page.Children.Add(Card("Import reading LRC", "Use readings from an LRC file for the lyrics showing.", "",
            Button("Import…", () => { }), notYet: SettingsCopy.LaterBuild));
        page.Children.Add(Caption("Saved readings are pronunciation guides. Japanese readings are matched to the lyric’s kana, however they’re spaced; a reading written for other words isn’t shown, and Lunalyr’s own reading is used for that line."));

        Section(page, "Translations");
        page.Children.Add(Card("Show translations under lyrics", "Shown when the lyric source has a translation. Lyrics are never sent anywhere to be translated.",
            "", FixedToggle(false), notYet: SettingsCopy.LaterBuild));
        return page;
    }
}

/// <summary>Wallpaper: the background type and the adjustments every drawn type shares.</summary>
internal static class WallpaperPage
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
        var typeCard = new Border { Style = Resource<Style>("Card"), Child = types };
        page.Children.Add(typeCard);

        Section(page, "Legibility");
        page.Children.Add(Card("Automatic text backing",
            "Light covers and images get a soft shadow behind the lyrics, just strong enough for white text to stay readable. Always on in this build.",
            "", FixedToggle(true), notYet: SettingsCopy.LaterBuild));

        Section(page, "Adjustments");
        page.Children.Add(Card("Motion speed", null, "", FixedSlider(0.2, 2, 1), notYet: SettingsCopy.LaterBuild));
        page.Children.Add(Card("Colour richness", null, "", FixedSlider(0.4, 1.8, 1), notYet: SettingsCopy.LaterBuild));
        page.Children.Add(Card("Darkening", null, "", FixedSlider(0, 2, 1), notYet: SettingsCopy.LaterBuild));
        return page;
    }
}

/// <summary>Covers (the Mac's "Animated covers"): Apple Music's motion artwork behind the lyrics.</summary>
internal static class CoversPage
{
    public static StackPanel Build()
    {
        var page = Page();
        Section(page, "Animated covers");
        page.Children.Add(Card("Use animated covers", "Apple Music’s moving album artwork behind the lyrics, with Match the music.", "",
            FixedToggle(false), notYet: SettingsCopy.LaterBuild));
        Section(page, "Framing");
        page.Children.Add(Card("Placement", "How the cover fills the screen.", "", FixedCombo("Fill the screen"),
            notYet: SettingsCopy.LaterBuild));
        page.Children.Add(Card("Softening", "A soft blur over the cover.", "", FixedSlider(0, 25, 5), notYet: SettingsCopy.LaterBuild));
        page.Children.Add(Caption("The album cover beside the song’s title at the start is under Lyric style › Layout."));
        return page;
    }
}

/// <summary>Music players: which players Lunalyr follows, and what it reads.</summary>
internal static class PlayersPage
{
    public static StackPanel Build(SettingsContext ctx, Func<AppController?> controller)
    {
        var page = Page();
        Section(page, "Music players");
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
        page.Children.Add(Card("Show player on hover", "Rest the pointer on an uncovered desktop to show the player.", "", FixedToggle(false),
            notYet: SettingsCopy.LaterBuild));
        return page;
    }
}
