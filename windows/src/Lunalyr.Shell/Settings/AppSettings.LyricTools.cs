// The lyric tools' setting: "When none are found" (Settings › Timing › Without synced lyrics). The key and its raw
// values are the Mac's (noLyrics: notice, trackName, nothing; default notice), taken as data: they sit on lines of the
// Mac's Settings.swift that trace back to the squashed Verso import.

using Lunalyr.Core.Motion;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    /// <summary>The values <see cref="NoLyrics"/> takes, in the menu's order.</summary>
    public static IReadOnlyList<string> NoLyricsValues { get; } = ["notice", "trackName", "nothing"];

    /// <summary>
    /// What the wallpaper shows once a song turns out to have no lyrics (Mac <c>noLyrics</c>): "notice" (a short note
    /// that fades after a few seconds; the default), "trackName" (the song's title and artist) or "nothing".
    /// </summary>
    public string NoLyrics { get; init; } = "notice";

    /// <summary><see cref="NoLyrics"/> as the renderer takes it (anything unknown reads as the note).</summary>
    public NoLyricsDisplay NoLyricsShown() => NoLyrics switch
    {
        "trackName" => NoLyricsDisplay.TrackName,
        "nothing" => NoLyricsDisplay.Nothing,
        _ => NoLyricsDisplay.Notice,
    };

    private AppSettings NormalizedLyricTools() =>
        NoLyricsValues.Contains(NoLyrics) ? this : this with { NoLyrics = new AppSettings().NoLyrics };

    // Show translations under lyrics (AppSettings.Translations.cs) reaches the renderer here too.
    private LyricStyle WithLyricToolsStyle(LyricStyle style) =>
        style with { NoLyrics = NoLyricsShown(), Translations = ShowTranslations };
}
