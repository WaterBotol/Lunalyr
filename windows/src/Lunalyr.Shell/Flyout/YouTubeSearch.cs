using Lunalyr.Core.Playback;

namespace Lunalyr.Shell.Flyout;

/// <summary>Opens a web address in the user's default browser. The app's is the shell; tests use a recording fake.</summary>
public interface IUrlLauncher
{
    /// <summary>Opens <paramref name="url"/>; false if it couldn't be opened.</summary>
    bool Launch(Uri url);
}

/// <summary>A launcher that never opens anything (the app uses it while playback is off for the run).</summary>
public sealed class RefusingUrlLauncher : IUrlLauncher
{
    public static RefusingUrlLauncher Instance { get; } = new();

    public bool Launch(Uri url) => false;
}

/// <summary>
/// "Search YouTube": a YouTube search for the playing song, opened in the user's browser, only when the user clicks
/// the flyout's row. The Mac's row opens its own picker for the music-video wallpaper (milestone 6 here); until that
/// exists the browser shows the results. Nothing is fetched by Lunalyr itself.
/// </summary>
public static class YouTubeSearch
{
    /// <summary>The search page for <paramref name="title"/> by <paramref name="artist"/>, or null without a title.</summary>
    public static Uri? UrlFor(string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var query = string.IsNullOrWhiteSpace(artist) ? title.Trim() : $"{artist.Trim()} {title.Trim()}";
        return new Uri("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query));
    }

    /// <summary>
    /// Opens the search for the song through <paramref name="launcher"/>. Refused (nothing opened) while the guard
    /// blocks sound for the run (<c>--no-playback</c>, <c>--demo</c>) or without a song.
    /// </summary>
    public static bool Open(IUrlLauncher launcher, PlaybackGuard guard, string? title, string? artist, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(guard);
        if (!guard.AllowsSound)
        {
            log?.Invoke($"Not opening a YouTube search: playback and sound are off for this run ({guard.Reason}).");
            return false;
        }

        if (UrlFor(title, artist) is not { } url) return false;
        var opened = launcher.Launch(url);
        log?.Invoke(opened ? "Opened a YouTube search for the song in the browser (a click in the tray panel)."
            : "Couldn't open the YouTube search in the browser.");
        return opened;
    }
}
