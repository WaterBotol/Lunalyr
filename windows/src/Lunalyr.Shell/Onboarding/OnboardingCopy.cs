// What the welcome says, kept apart from the window so tests can pin the promises in it. The Mac's OnboardingCopy
// (OnboardingFlow.swift, Lunalyr's own lines; reworded shorter for Test Build 88: one line under a title, one caption
// under a control) in Windows' terms: no permission prompt, the notification area instead of the menu bar, the tray
// panel (no global shortcuts and no search in the desktop player: the owner removed both, 14 Nov 2026), a video file chosen for a song instead of the Music library's videos, and no lyrics from
// Apple Music's own app (see docs/APPLE-MUSIC-WINDOWS.md and docs/READINGS-WINDOWS.md).

using Lunalyr.Core.NowPlaying;
using Lunalyr.Shell.Flyout;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Onboarding;

public static class OnboardingCopy
{
    /// <summary>The window's title (Alt+Tab, the taskbar and Narrator name it), as on the Mac.</summary>
    public const string WindowTitle = "Welcome to Lunalyr";

    /// <summary>The window's size in DIPs (the Mac's is 640 × 540 points, title bar included).</summary>
    public const double WindowWidth = 680, WindowHeight = 640;

    // ---- 1. Welcome ----

    public const string WelcomeTitle = "Welcome to Lunalyr";

    public const string Welcome =
        "Your desktop sings along: the lyrics of the song you’re playing, timed word by word, over a wallpaper that moves with the music.";

    /// <summary>The glass logo opening, to Narrator: the whole window is one button that skips it.</summary>
    public const string SkipOpening = "Skip the opening";

    // ---- 2. Your music ----

    public const string MusicTitle = "Let Lunalyr see what’s playing";

    /// <summary>
    /// Windows' counterpart of the Mac's <c>permissionNote</c>: which players Lunalyr follows, and that nothing is asked of
    /// Windows.
    /// </summary>
    public const string MusicNote =
        "Lunalyr follows any app that shares what it plays with Windows: Spotify, Apple Music, YouTube Music or SoundCloud in a browser, and more. Windows asks you nothing.";

    /// <summary>Under the card (the Mac's <c>permissionDetail</c>): what Lunalyr does with what it reads.</summary>
    public const string MusicDetail =
        "It only reads the song and its position, and controls playback only when you use its buttons. Players show here once they share what they play, in the media controls by the volume flyout.";

    public const string PlayersHeading = "Sharing with Windows now";

    public const string NoPlayers = "No player is sharing what it plays right now. Play a song in any music app and it shows here.";

    public const string StartingPlayers = "Looking for players…";

    public const string Follow = "Follow";

    /// <summary>Under the Follow menu (the Mac's followCaption, in Settings › Music players' words).</summary>
    public const string FollowCaption =
        "Automatic follows whichever player is playing. Choosing one means Lunalyr never reads the others.";

    /// <summary>How a player sharing with Windows reads in the list.</summary>
    public static string PlayerStatus(PlaybackStatus status, bool followed)
    {
        var state = status switch
        {
            PlaybackStatus.Playing => "Playing",
            PlaybackStatus.Paused => "Paused",
            PlaybackStatus.Stopped => "Stopped",
            PlaybackStatus.Changing => "Changing songs",
            _ => "Open",
        };
        return followed ? $"{state} · Lunalyr follows this one now" : state;
    }

    /// <summary>A player sharing with Windows that the player chosen leaves alone (what it plays isn't read).</summary>
    public static string NotFollowed(string chosen) => $"Not followed: Lunalyr follows {chosen} only";

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string List(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count <= 1) return names.Count == 1 ? names[0] : "";
        return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }

    // ---- 3. Pick a look ----

    public const string LookTitle = "Pick a look";

    public const string Importing = "Importing…";

    public const string ImportFailed = "Couldn’t use that file";

    public const string ChooseAnother = "Choose another image or video…";

    /// <summary>What the chosen type draws (the Mac's look-step summary): the imported file's name first, when there is one.</summary>
    public static string LookSummary(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var type = SettingsCopy.WallpaperTypeOf(settings.BackgroundMode);
        return type.Value == "custom" && settings.WallpaperName is { Length: > 0 } name ? $"{name}: {type.Summary}" : type.Summary;
    }

    /// <summary>The layout tiles, to Narrator.</summary>
    public const string LayoutGroup = "Lyric layout";

    /// <summary>
    /// Under the layouts: what the chosen one does (its name is on its tile), or Custom Mix's promise (the Mac's
    /// <c>layoutCaption</c>; Windows calls Automatic "Custom Mix").
    /// </summary>
    public static string LayoutCaption(string? layout) =>
        layout is null or AppSettings.AllLayoutsKey || SettingsCopy.Layouts.FirstOrDefault(l => l.Value == layout)?.Detail is not { Length: > 0 } detail
            ? "Custom Mix picks a layout to suit each song."
            : detail;

    /// <summary>How many more layouts Settings has than the look step shows (the Mac's <c>moreLayouts</c>).</summary>
    public static string MoreLayouts(int count) => $"{count} more in Settings, with Custom Mix.";

    // ---- 4. Music videos ----

    public const string VideosTitle = "Music videos";

    /// <summary>The Mac's line, with Windows' video file chosen for a song in place of the Music library's videos.</summary>
    public const string Videos =
        "Play a song’s official video behind its lyrics, muted. A video file you choose for a song comes first and needs nothing sent.";

    public const string VideoWallpaper = "Play music videos as the wallpaper";

    public const string VideoWallpaperCaption = "Songs without a video show the look you picked.";

    public const string SearchYouTube = "Search YouTube for videos";

    /// <summary>The same facts as Settings › Wallpaper › Music video's caption under Search YouTube for videos.</summary>
    public const string YouTubePrivacy =
        "Off by default. When on, the song’s title and artist are sent to YouTube to find its official video, which then streams from YouTube.";

    /// <summary>
    /// The settings after the step's "Play music videos as the wallpaper" switch (the Mac's <c>VideosStep.videoWallpaper</c>):
    /// on chooses Music video, remembering the type it replaced; off returns to that type.
    /// </summary>
    public static AppSettings WithVideoWallpaper(AppSettings settings, bool on)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (on) return AppSettings.RememberMusicVideoFallback(settings, settings with { BackgroundMode = "musicVideo" });
        return settings.BackgroundMode == "musicVideo" ? settings with { BackgroundMode = settings.MusicVideoFallback } : settings;
    }

    // ---- 5. Lyrics & readings ----

    public const string LyricsTitle = "Lyrics & readings";

    public const string SourcesTitle = "Synced lyrics for each song";

    /// <summary>Where lyrics come from and what the services are sent (the Mac's words; Windows has no Apple Music lyrics of its own).</summary>
    public const string Sources =
        "From lyric services such as LRCLIB, which receive only the song’s title, artist, album and length.";

    public const string TimingTitle = "Lyrics early or late?";

    /// <summary>Nudging the timing: from the tray panel only (Windows has no global shortcuts).</summary>
    public const string Timing = "Nudge them from the tray panel. Each song keeps its timing.";

    public const string ReadingsTitle = "Readings for Japanese, Korean and Chinese";

    public const string Readings = "How lyrics in other scripts sound, below them or in their place.";

    public const string ReadingsDisplay = "Display";

    public const string ReadingsLanguage = "Language";

    /// <summary>Under Show translations under lyrics (the Mac's OnboardingCopy.translations).</summary>
    public const string Translations = Lunalyr.Shell.Settings.TranslationsCopy.Onboarding;

    // ---- 6. Extras ----

    public const string ExtrasTitle = "Extras";

    public const string Extras = "A player on the desktop, lyrics over your apps, and the line you’re hearing on Discord.";

    public const string PlayerTitle = "Desktop player";

    /// <summary>The desktop player's card (the Mac's line without its search: Windows' player has none).</summary>
    public const string Player = "Rest the pointer on an uncovered desktop to see what’s playing and control it.";

    /// <summary>The desktop player's switch, to Narrator (Settings › Performance's Show player on hover).</summary>
    public const string PlayerSwitch = "Show player on hover";

    public const string FloatingTitle = "Floating lyrics";

    public const string Floating = "Lyrics over your apps instead of on the desktop. Turn them on in Settings › Lyric style.";

    public const string DiscordTitle = "Discord status";

    public const string Discord = "Your status shows the line being sung, as it changes.";

    /// <summary>The Discord switch, to Narrator.</summary>
    public const string DiscordSwitch = "Show what I’m listening to on Discord";

    /// <summary>
    /// What the switch sends, and to whom (DiscordPresenceService: a local pipe to the Discord app; the cover from Apple's
    /// public search; cleared when playback stops).
    /// </summary>
    public const string DiscordPrivacy =
        "Off by default. Talks only to the Discord app on this PC (covers come from Apple’s search) and clears when playback stops.";

    // ---- 7. The tray ----

    public const string TrayTitle = "Find Lunalyr in the tray";

    /// <summary>Where the app lives and the way back (the Mac's menu-bar note).</summary>
    public const string TrayNote =
        "Lunalyr lives in the notification area by the clock, with no taskbar button. If Windows tucks it under ^, drag it onto the taskbar, or open Lunalyr again to bring back Settings.";

    public const string TrayLineTitle = ExtrasCopy.TrayTitle;

    public const string TrayLineCaption = "The line being sung, in a small strip beside the taskbar’s clock.";

    public const string LaunchAtLogin = "Launch at login";

    public const string LaunchAtLoginCaption = "Lunalyr starts quietly in the tray when you sign in to Windows.";

    // ---- 8. Done ----

    public const string DoneTitle = "You’re all set";

    /// <summary>The Mac's playing and idle wording.</summary>
    public static string Done(string? playing, string idlePrompt = FlyoutText.IdlePrompt) =>
        playing is { Length: > 0 } title
            ? $"“{title}” is playing, so its lyrics are on your desktop now."
            : $"{idlePrompt} and its lyrics appear on your desktop.";

    public const string DoneFirstRun = "Settings has the rest: lyric style, timing, readings and more.";

    public const string DoneAgain = "You can change all of this in Settings.";

    /// <summary>The Done step's wallpaper row: "Wallpaper: Custom Mix".</summary>
    public static string WallpaperRow(AppSettings settings) =>
        $"Wallpaper: {SettingsCopy.WallpaperTypeOf(settings?.BackgroundMode).Title}";

    /// <summary>Under the wallpaper row: the imported file's name, or what the type draws.</summary>
    public static string WallpaperDetail(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var type = SettingsCopy.WallpaperTypeOf(settings.BackgroundMode);
        return type.Value == "custom" && settings.WallpaperName is { Length: > 0 } name ? name : type.Summary;
    }

    // ---- The footer ----

    public static string Label(OnboardingButton button) => button switch
    {
        OnboardingButton.Back => "Back",
        OnboardingButton.Continue => "Continue",
        OnboardingButton.Done => "Done",
        _ => "Open Settings",
    };

    /// <summary>A button's tooltip and Narrator help text.</summary>
    public static string Help(OnboardingButton button, OnboardingFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return button switch
        {
            OnboardingButton.Back => $"Back to {flow.Previous?.Title() ?? "the last step"} (Alt+Left)",
            OnboardingButton.Continue => $"On to {flow.Next?.Title() ?? "the end"} (Enter)",
            OnboardingButton.Done when flow.FirstRun => "Close and leave Lunalyr in the tray",
            OnboardingButton.Done => "Close the welcome (Enter)",
            _ => "Close the welcome and open Settings (Enter)",
        };
    }

    /// <summary>What Narrator hears as a step appears.</summary>
    public static string Announcement(OnboardingStep step) => $"{step.Position()}, {step.Title()}";
}
