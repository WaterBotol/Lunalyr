// What the welcome says, kept apart from the window so tests can pin the promises in it. The Mac's OnboardingCopy
// (OnboardingFlow.swift, Lunalyr's own lines) in Windows' terms: no permission prompt, the notification area instead of
// the menu bar, the tray panel and Windows' shortcuts, and only what this build can do (no translations yet, no lyrics
// from Apple Music's own app: see docs/APPLE-MUSIC-WINDOWS.md and docs/READINGS-WINDOWS.md).

using Lunalyr.Core.NowPlaying;
using Lunalyr.Shell.Flyout;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Onboarding;

public static class OnboardingCopy
{
    /// <summary>The window's title (Alt+Tab, the taskbar and Narrator name it), as on the Mac.</summary>
    public const string WindowTitle = "Welcome to Lunalyr";

    /// <summary>The window's size in DIPs (the Mac's is 640 × 540 points, title bar included).</summary>
    public const double WindowWidth = 680, WindowHeight = 600;

    // ---- 1. Welcome ----

    public const string WelcomeTitle = "Welcome to Lunalyr";

    public const string Welcome =
        "Your desktop becomes the lyrics of the song you’re playing, timed word by word, over a wallpaper that moves with the music.";

    // ---- 2. Your music ----

    public const string MusicTitle = "Let Lunalyr see what’s playing";

    /// <summary>
    /// Windows' counterpart of the Mac's <c>permissionNote</c>: which players Lunalyr follows, that nothing is asked of
    /// Windows, and what Lunalyr does with what it reads.
    /// </summary>
    public const string MusicNote =
        "Lunalyr follows any app that shares what it plays with Windows, the ones in the media controls by the volume flyout: Spotify, Apple Music, browsers playing YouTube Music or SoundCloud, and more. Windows asks you nothing and Lunalyr needs no permission. It only reads the song and its position, and controls playback only when you use its buttons.";

    public const string PlayersHeading = "Sharing with Windows now";

    public const string NoPlayers = "No player is sharing what it plays right now. Play a song in any music app and it shows here.";

    public const string StartingPlayers = "Looking for players…";

    public const string Follow = "Follow";

    /// <summary>Settings › Music players' caption for the same choice, which this build can't change yet.</summary>
    public const string FollowCaption =
        "Automatic follows whichever player is playing. Choosing one player arrives in a later build.";

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

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string List(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count <= 1) return names.Count == 1 ? names[0] : "";
        return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }

    // ---- 3. Pick a look ----

    public const string LookTitle = "Pick a look";

    public const string Layout = "Layout";

    public const string LayoutCaption = "How the lyrics are set on the wallpaper. Match the music picks one for each song.";

    public const string Importing = "Importing…";

    public const string ImportFailed = "Couldn’t use that file";

    public const string ChooseAnother = "Choose Another Image or Video…";

    /// <summary>What the chosen type draws (the Mac's look-step summary): the imported file's name first, when there is one.</summary>
    public static string LookSummary(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var type = SettingsCopy.WallpaperTypeOf(settings.BackgroundMode);
        return type.Value == "custom" && settings.WallpaperName is { Length: > 0 } name ? $"{name}: {type.Summary}" : type.Summary;
    }

    // ---- 4. Lyrics & readings ----

    public const string LyricsTitle = "Lyrics & readings";

    public const string SourcesTitle = "Synced lyrics for each song";

    /// <summary>Where lyrics come from and what the services are sent (the Mac's words; Windows has no Apple Music lyrics of its own).</summary>
    public const string Sources =
        "Lunalyr finds synced lyrics in lyric services such as LRCLIB. The services receive only the song’s title, artist, album and length.";

    public const string TimingTitle = "Lyrics early or late?";

    /// <summary>Nudging the timing, with the keys this keyboard types for the two shortcuts.</summary>
    public static string Timing(string earlier, string later) =>
        $"Nudge them a tenth of a second at a time from the tray panel, or press {earlier} and {later} once keyboard shortcuts are on. Lunalyr remembers the timing for each song.";

    public const string ReadingsTitle = "Readings for Japanese, Korean and Chinese";

    public const string Readings =
        "Romanisation shows how lyrics in other scripts sound, below them or in their place. Readings are worked out on this PC.";

    public const string ReadingsDisplay = "Display";

    public const string ReadingsLanguage = "Language";

    /// <summary>Translations: the Mac's promise, and that Windows doesn't show them yet.</summary>
    public const string Translations =
        "Translations under the lyrics arrive in a later build. Lyrics are never sent anywhere to be translated.";

    // ---- 5. The tray ----

    public const string TrayTitle = "Find Lunalyr in the tray";

    /// <summary>Where the app lives and the way back (the Mac's menu-bar note).</summary>
    public const string TrayNote =
        "Lunalyr lives in the notification area by the clock and has no taskbar button once Settings is closed. If Windows tucks its icon under ^ (Show hidden icons), drag it onto the taskbar to keep it pinned there. Opening Lunalyr again brings back Settings.";

    public const string TrayLineTitle = ExtrasCopy.TrayTitle;

    public const string TrayLineCaption = "The line being sung, in a small strip beside the taskbar’s clock.";

    public const string LaunchAtLogin = "Launch at login";

    public const string LaunchAtLoginCaption = "Lunalyr starts quietly in the tray when you sign in to Windows.";

    // ---- 6. Done ----

    public const string DoneTitle = "You’re all set";

    /// <summary>The Mac's playing and idle wording.</summary>
    public static string Done(string? playing, string idlePrompt = FlyoutText.IdlePrompt) =>
        playing is { Length: > 0 } title
            ? $"“{title}” is playing, so its lyrics are on your desktop now."
            : $"{idlePrompt} and its lyrics appear on your desktop.";

    public const string DoneFirstRun = "Settings has the rest: lyric style, timing, shortcuts and more.";

    public const string DoneAgain = "You can change all of this in Settings.";

    /// <summary>The Done step's wallpaper row: "Wallpaper: Match the music".</summary>
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
