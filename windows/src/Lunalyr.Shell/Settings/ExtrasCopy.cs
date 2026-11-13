// The wording of Settings › General's Discord status and tray lyric rows: the Mac's Share and Menu bar sections
// (Sources/Settings/LyricExtrasInspector.swift, Lunalyr's own lines), with Windows' words where the Mac names its menu bar
// or its listen-along.

using Lunalyr.Core.Extras;

namespace Lunalyr.Shell.Settings;

public static class ExtrasCopy
{
    // ---- Discord ----

    public const string DiscordTitle = "Show what I’m listening to on Discord";
    public const string DiscordCaption = "Needs the Discord desktop app. Shows the line being sung.";

    public const string DiscordMore =
        "Your status shows what you choose below; your profile adds the song, artist, album, cover and progress, with Play this song and Get Lunalyr buttons for others (Discord never shows you your own). Nothing is shared while this is off, and it clears when you pause or quit.";

    public const string StatusShows = "Status shows";
    public const string ShowLyric = "Show the current lyric";
    public const string ShowCover = "Show the album cover";
    public const string CoverCaption = "Looks the cover up by song and artist.";

    public const string CoverMore =
        "The cover comes from Apple’s public search, which is sent only the song title and artist. Without a match, the Lunalyr icon is shown. The Play this song button uses the same search; with both off, nothing is sent to Apple.";

    public const string ShowProgress = "Show the progress bar";
    public const string ShowSongButton = "Show a Play this song button";
    public const string SongButtonCaption = "Opens the song on Spotify, Apple Music, YouTube Music and more.";

    public const string SongButtonMore =
        "Links to the song’s song.link page, which lists it on every service. Found from the same Apple search as the cover (sent only the song title and artist), and only when the result is exactly that song. Until you change it, this follows Show the album cover. Others see it; Discord never shows you your own buttons.";

    public const string ShowGetButton = "Show a Get Lunalyr button";
    public const string KeepWhilePaused = "Keep showing while paused";
    public const string LyricLead = "Lyric lead";
    public const string LyricLeadCaption = "How far ahead of the music Discord shows the line.";
    public const string Connection = "Connection";

    /// <summary>Under the connection row while Discord hasn't answered.</summary>
    public const string WaitingNote = "Open the Discord desktop app; Lunalyr connects once it’s running.";

    /// <summary>The slider's value: "2.5 s earlier".</summary>
    public static string Lead(double seconds) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{seconds:0.0} s earlier");

    /// <summary>What the connection row says (the Mac's <c>ShareStatusRow.discord</c>). Shown only while the switch is on.</summary>
    public static (string Text, bool Connected) ConnectionRow(DiscordConnectionStatus status) => status switch
    {
        DiscordConnectionStatus.Connected { Name: { } name } when name.Trim().Length > 0 => ($"Connected as {name}", true),
        DiscordConnectionStatus.Connected => ("Connected", true),
        _ => ("Waiting for Discord", false),
    };

    /// <summary>Why Discord status can't run this time, for the row's note: <c>--demo</c> and <c>--no-network</c> send nothing to Discord.</summary>
    public static string OffForRun(string reason) => $"Discord status is off for this run ({reason}): nothing is sent to Discord.";

    public static IReadOnlyList<SettingsChoice> StatusTexts { get; } =
        [.. DiscordStatusTexts.All.Select(text => new SettingsChoice(text.Raw(), text.Title()))];

    // ---- Tray lyric ----

    public const string TrayTitle = "Lyric by the tray";
    public const string TrayCaption = "The line being sung, in a small strip beside the taskbar’s clock. It never takes clicks or the keyboard.";
    public const string LongestLine = "Longest line";
    public const string TrayFollows = "Follows Pause wallpaper: a paused wallpaper says so there. Hidden behind full-screen apps.";

    /// <summary>The slider's value: "36 characters".</summary>
    public static string Characters(double count) => $"{(int)Math.Round(count, MidpointRounding.AwayFromZero)} characters";
}
