// Discord status and the tray lyric (milestone 7), ported from the Mac's LunalyrExtras (Sources/Extras/LunalyrExtras.swift,
// Lunalyr's own lines). Discord's keys are the Mac's without "custom." (custom.discordPresence → discordPresence…). The
// Mac's menu-bar line becomes the tray lyric: its keys say so (custom.menuBarLyric → trayLyric, custom.menuBarLyricLength
// → trayLyricLength), since Windows has no menu bar.

using System.Text.Json.Serialization;
using Lunalyr.Core.Extras;

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    // ---- Discord ----

    /// <summary>Show what is playing on the user's Discord profile (Mac <c>custom.discordPresence</c>). Off by default.</summary>
    public bool DiscordPresence { get; init; }

    /// <summary>What the status under the name shows: "lyric", "song" or "artist" (Mac <c>custom.discordStatusText</c>).</summary>
    public string DiscordStatusText { get; init; } = "lyric";

    /// <summary>Show the current lyric (Mac <c>custom.discordLyric</c>).</summary>
    public bool DiscordLyric { get; init; } = true;

    /// <summary>Show the album cover, found on Apple's public search (Mac <c>custom.discordCover</c>).</summary>
    public bool DiscordCover { get; init; } = true;

    /// <summary>Show a Get Lunalyr button (Mac <c>custom.discordButton</c>).</summary>
    public bool DiscordButton { get; init; } = true;

    /// <summary>
    /// Show a Play this song button (Mac <c>custom.discordSongButton</c>). Unset until the user changes it, and then it
    /// follows <see cref="DiscordCover"/> (<see cref="DiscordShowsSongButton"/>), so a user who switched the cover off
    /// sends nothing to Apple.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DiscordSongButton { get; init; }

    /// <summary>Show the progress bar (Mac <c>custom.discordProgress</c>).</summary>
    public bool DiscordProgress { get; init; } = true;

    /// <summary>Keep showing while paused (Mac <c>custom.discordPaused</c>).</summary>
    public bool DiscordPaused { get; init; }

    /// <summary>Seconds the Discord line runs ahead of the music, 0–6 (Mac <c>custom.discordLyricLead</c>).</summary>
    public double DiscordLyricLead { get; init; } = 2.5;

    /// <summary>The Play this song button: the user's choice, else the cover's.</summary>
    [JsonIgnore]
    public bool DiscordShowsSongButton => DiscordSongButton ?? DiscordCover;

    /// <summary>The choices as the presence takes them (the Mac's <c>discordOptions</c>).</summary>
    public DiscordOptions DiscordOptions() => new()
    {
        StatusText = DiscordStatusTexts.Parse(DiscordStatusText) ?? Core.Extras.DiscordStatusText.Lyric,
        ShowsLyric = DiscordLyric,
        ShowsCover = DiscordCover,
        ShowsButton = DiscordButton,
        ShowsSongButton = DiscordShowsSongButton,
        ShowsProgress = DiscordProgress,
        ShowsWhenPaused = DiscordPaused,
        LyricLead = DiscordLyricLead,
    };

    // ---- Tray lyric ----

    /// <summary>Show the current line beside the tray (Mac <c>custom.menuBarLyric</c>, the menu-bar line). Off by default.</summary>
    public bool TrayLyric { get; init; }

    /// <summary>The longest line, in characters, 16–64 (Mac <c>custom.menuBarLyricLength</c>).</summary>
    public double TrayLyricLength { get; init; } = TrayLyricTicker.DefaultLength;

    private AppSettings NormalizedDiscord()
    {
        var lead = DiscordLyricLead;
        var length = TrayLyricLength;
        return this with
        {
            DiscordStatusText = DiscordStatusTexts.Parse(DiscordStatusText) is { } text ? text.Raw() : "lyric",
            DiscordLyricLead = double.IsFinite(lead) ? Math.Clamp(lead, 0, Core.Extras.DiscordPresence.LeadMax) : 2.5,
            TrayLyricLength = double.IsFinite(length)
                ? Math.Clamp(length, TrayLyricTicker.LengthMin, TrayLyricTicker.LengthMax)
                : TrayLyricTicker.DefaultLength,
        };
    }
}
