using Lunalyr.Core.Player.Spotify;

namespace Lunalyr.Shell.Settings;

/// <summary>
/// The words of Settings › Music players › Spotify and of the desktop player's hover switch (Performance), kept here so
/// they are tested. Windows only: the Mac's players search through AppleScript and need no sign-in.
/// </summary>
public static class SpotifyCopy
{
    /// <summary>The redirect address the developer app must list, exactly.</summary>
    public static string RedirectUri { get; } = SpotifyOptions.RedirectUriFor(SpotifyOptions.DefaultRedirectPort).ToString();

    public const string DashboardAddress = "https://developer.spotify.com/dashboard";

    public const string Summary =
        "Search Spotify and play from the desktop player, and see what plays next. It uses your own Spotify developer app and your own sign-in; Lunalyr ships no key of its own.";

    /// <summary>The one-time set-up, in order (APP-SHELL.md, "Setting up the developer app").</summary>
    public static IReadOnlyList<string> Steps { get; } =
    [
        "Sign in at developer.spotify.com/dashboard and choose Create app. Any name and description will do.",
        $"Under Redirect URIs, add exactly {RedirectUri} (the numbers, not “localhost”; no slash at the end).",
        "Under “Which API/SDKs are you planning to use?”, tick Web API, then Save.",
        "Copy the app’s Client ID (32 letters and digits) into the box above. Never copy the client secret: Lunalyr doesn’t use one.",
        "Press Sign in. Your browser opens Spotify’s sign-in page; once you agree, it says “Signed in to Spotify” and can be closed.",
    ];

    public const string Requirements =
        "Spotify’s rules for developer apps: playing from Lunalyr needs Spotify Premium, and only accounts on the app’s User Management list (up to 5) can sign in. Search works without Premium. Lunalyr asks only to read and start playback: nothing about your account, library or playlists.";

    public const string SignOutNote =
        "Signing out forgets the sign-in on this PC. To take away the app’s access altogether, remove it at spotify.com/account/apps.";

    /// <summary>The client ID box's caption for what is typed (empty is fine: Spotify is off).</summary>
    public static string ClientIdCaption(string typed)
    {
        var trimmed = (typed ?? "").Trim();
        if (trimmed.Length == 0) return "Paste the Client ID from your Spotify developer app.";
        return SpotifyOptions.NormalizeClientId(trimmed).Length > 0
            ? "Saved. It stays on this PC, in Lunalyr’s settings."
            : "A Client ID is 32 letters and digits, from your app’s page on the Spotify dashboard.";
    }

    /// <summary>Whether what is typed should be saved: a whole client ID, or nothing (which turns Spotify off).</summary>
    public static bool IsSavable(string typed)
    {
        var trimmed = (typed ?? "").Trim();
        return trimmed.Length == 0 || SpotifyOptions.NormalizeClientId(trimmed).Length > 0;
    }

    /// <summary>The account row's title for a state.</summary>
    public static string State(SpotifyAccountState state) => state switch
    {
        SpotifyAccountState.NotSetUp => "Not set up",
        SpotifyAccountState.SignedOut => "Not signed in",
        SpotifyAccountState.SigningIn => "Signing in…",
        _ => "Signed in to Spotify",
    };

    /// <summary>The account row's description for a state.</summary>
    public static string StateDetail(SpotifyAccountState state) => state switch
    {
        SpotifyAccountState.NotSetUp => "Add your app’s Client ID first, then sign in.",
        SpotifyAccountState.SignedOut => "Sign in to search Spotify and play from the desktop player.",
        SpotifyAccountState.SigningIn => "Finish signing in in your browser. Lunalyr waits up to 5 minutes.",
        _ => "Search and the queue work while the Spotify app is playing.",
    };

    /// <summary>Why the section is off for this run (<c>--demo</c>, <c>--no-network</c>, still starting).</summary>
    public static string Off(string reason) => reason switch
    {
        "--demo" => "Spotify is off in the demo: it follows a built-in silent player and sends nothing anywhere.",
        "--no-network" => "Spotify is off for this run (--no-network): nothing is sent to Spotify.",
        _ => "Spotify is starting. This section is ready in a moment.",
    };

    /// <summary>The Performance page's "Show player on hover" caption (the Mac's two captions; Windows has the tray, not a menu bar).</summary>
    public static string HoverCaption(bool on) => on
        ? "Rest the pointer in the middle of an uncovered desktop to show the player: what played, what plays now and next, and a search. Clicks elsewhere reach the desktop."
        : "The desktop never shows the player.";
}
