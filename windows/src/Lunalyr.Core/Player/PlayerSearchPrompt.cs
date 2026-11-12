using Lunalyr.Core.NowPlaying;

namespace Lunalyr.Core.Player;

/// <summary>What the prompt's button does.</summary>
public enum PlayerSearchPromptAction
{
    /// <summary>Nothing to press: the words say what to do.</summary>
    None = 0,

    /// <summary>Opens Settings › Music players, where the Spotify client ID goes.</summary>
    SetUp,

    /// <summary>Opens Settings › Music players, where Sign in is.</summary>
    SignIn,
}

/// <summary>
/// What the search pane says instead of a search box when search can't be used for the player chosen: set Spotify up,
/// sign in, or play something in the Spotify app. Windows only (the Mac's players search through AppleScript).
/// </summary>
/// <param name="Text">The sentence over the button.</param>
/// <param name="ActionLabel">The button's words, or null for none.</param>
public sealed record PlayerSearchPrompt(string Text, PlayerSearchPromptAction Action, string? ActionLabel)
{
    /// <summary>
    /// The prompt for <paramref name="availability"/> of <paramref name="app"/> (the player the picker chose, null when
    /// nothing plays), or null when search is ready. <paramref name="spotifyOffReason"/> says why Spotify isn't available
    /// at all in this run ("--demo", "--no-network"), or is null when it is.
    /// </summary>
    public static PlayerSearchPrompt? For(PlayerSearchAvailability availability, MediaSourceApp? app, string? spotifyOffReason = null)
    {
        switch (availability)
        {
            case PlayerSearchAvailability.Ready:
                return null;
            case PlayerSearchAvailability.NeedsSetUp:
                return new("Search and play Spotify from here with your own Spotify developer app: add its client ID, then sign in.",
                    PlayerSearchPromptAction.SetUp, "Set up Spotify in Settings");
            case PlayerSearchAvailability.NeedsSignIn:
                return new(PlayerSearchText.Words(PlayerSearchError.NotSignedIn, "Spotify"), PlayerSearchPromptAction.SignIn, "Sign in in Settings");
        }

        if (PlayerSearches.IsSpotify(app))
            return new(spotifyOffReason is null
                ? "Spotify search isn’t available yet. Try again in a moment."
                : $"Spotify search is off for this run ({spotifyOffReason}).", PlayerSearchPromptAction.None, null);
        const string needs = "Search needs the Spotify app: play something in Spotify, then search here.";
        return new(app is null ? needs : $"{app.DisplayName} has no search Lunalyr can use. {needs}", PlayerSearchPromptAction.None, null);
    }
}
