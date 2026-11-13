// Which player Lunalyr follows: the Mac's PlayerOptions.Choice (PlayerOptions.swift, Lunalyr's own lines, blame-clean),
// in Windows' terms. The Mac's players are installed apps it asks through AppleScript; Windows' are the apps that share
// what they play with its media controls (GSMTC sessions), told apart by their SourceAppUserModelId. Automatic follows
// the session Windows calls current; a choice follows that one player's session and no other. The Mac's
// webPlayers/localPlayers switches exist because macOS asks permission per app; Windows asks nothing, so Automatic
// already follows every app that shares, and those switches have no Windows counterpart.

namespace Lunalyr.Core.NowPlaying;

/// <summary>
/// A stored player choice (settings key <c>player</c>, the Mac's <c>custom.player</c>): <c>automatic</c>, one of the
/// Mac's raw values where the player is the same (<c>appleMusic</c>, <c>spotify</c>, <c>edge</c>, <c>chrome</c>,
/// <c>brave</c>, <c>arc</c>), a Windows player (<c>firefox</c>, <c>opera</c>, <c>vivaldi</c>, <c>mediaPlayer</c>),
/// or <c>app:</c> and the exact AppUserModelID of any other app that shares with Windows (foobar2000, MusicBee…).
/// </summary>
public static class PlayerChoice
{
    public const string Automatic = "automatic";

    /// <summary>The prefix of a choice that names an app by its AppUserModelID.</summary>
    public const string AppPrefix = "app:";

    private sealed record Known(string Raw, string Title, Func<MediaSourceApp, bool> Matches);

    // The Mac's order (music apps, then browsers), with Windows' own players where the Mac has its local ones.
    private static readonly Known[] KnownChoices =
    [
        new("appleMusic", "Apple Music", app => app.Kind == MediaSourceKind.AppleMusic),
        new("spotify", "Spotify", app => app.Kind == MediaSourceKind.Spotify),
        new("mediaPlayer", "Media Player", app => app.Kind == MediaSourceKind.MediaPlayer),
        new("edge", "Web in Edge", app => app.IsBrowser && app.DisplayName == "Microsoft Edge"),
        new("chrome", "Web in Chrome", app => app.IsBrowser && app.DisplayName == "Google Chrome"),
        new("firefox", "Web in Firefox", app => app.IsBrowser && app.DisplayName == "Firefox"),
        new("brave", "Web in Brave", app => app.IsBrowser && app.DisplayName == "Brave"),
        new("arc", "Web in Arc", app => app.IsBrowser && app.DisplayName == "Arc"),
        new("opera", "Web in Opera", app => app.IsBrowser && app.DisplayName == "Opera"),
        new("vivaldi", "Web in Vivaldi", app => app.IsBrowser && app.DisplayName == "Vivaldi"),
    ];

    /// <summary>The music apps every picker offers, as the Mac's offers Music and Spotify whether or not they run.</summary>
    public static IReadOnlyList<string> MusicApps { get; } = ["appleMusic", "spotify"];

    /// <summary><paramref name="raw"/> when it is a choice this build knows; otherwise Automatic (the Mac's fallback).</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Automatic;
        if (raw == Automatic || KnownChoices.Any(k => k.Raw == raw)) return raw;
        return raw.StartsWith(AppPrefix, StringComparison.Ordinal) && raw.Length > AppPrefix.Length
               && !string.IsNullOrWhiteSpace(raw[AppPrefix.Length..])
            ? raw
            : Automatic;
    }

    public static bool IsAutomatic(string? choice) => Normalize(choice) == Automatic;

    /// <summary>The choice that follows <paramref name="app"/> and nothing else (the Mac's <c>Choice.only</c>).</summary>
    public static string Only(MediaSourceApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        foreach (var known in KnownChoices) if (known.Matches(app)) return known.Raw;
        return string.IsNullOrWhiteSpace(app.AppId) ? Automatic : AppPrefix + app.AppId;
    }

    /// <summary>
    /// Whether a session of <paramref name="app"/> is followed under <paramref name="choice"/>: Automatic follows every
    /// player (the one Windows calls current); a choice only its own.
    /// </summary>
    public static bool Follows(string? choice, MediaSourceApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var normalized = Normalize(choice);
        if (normalized == Automatic) return true;
        if (normalized.StartsWith(AppPrefix, StringComparison.Ordinal))
            return string.Equals(app.AppId, normalized[AppPrefix.Length..], StringComparison.OrdinalIgnoreCase);
        return KnownChoices.First(k => k.Raw == normalized).Matches(app);
    }

    /// <summary>The choice's name in a menu ("Automatic", "Apple Music", "Web in Edge", an app's own name).</summary>
    public static string Title(string? choice)
    {
        var normalized = Normalize(choice);
        if (normalized == Automatic) return "Automatic";
        if (normalized.StartsWith(AppPrefix, StringComparison.Ordinal))
            return MediaSourceApp.FromAppId(normalized[AppPrefix.Length..]).DisplayName;
        return KnownChoices.First(k => k.Raw == normalized).Title;
    }

    /// <summary>
    /// What a picker offers (the Mac's <c>Choice.offered</c>): Automatic, Apple Music and Spotify always, a choice for
    /// each other app sharing with Windows now (in the known order, then as Windows lists them), and the current choice
    /// even if its app has gone. Each once.
    /// </summary>
    public static IReadOnlyList<string> Offered(IEnumerable<MediaSourceApp> sharing, string? current)
    {
        ArgumentNullException.ThrowIfNull(sharing);
        var seen = sharing.Where(app => !string.IsNullOrWhiteSpace(app.AppId)).Select(Only).Where(c => c != Automatic).ToList();
        var offered = new List<string> { Automatic };
        offered.AddRange(MusicApps);
        foreach (var known in KnownChoices)
            if (seen.Contains(known.Raw) && !offered.Contains(known.Raw)) offered.Add(known.Raw);
        foreach (var other in seen)
            if (!offered.Contains(other)) offered.Add(other);
        var chosen = Normalize(current);
        if (!offered.Contains(chosen)) offered.Add(chosen);
        return offered;
    }
}
