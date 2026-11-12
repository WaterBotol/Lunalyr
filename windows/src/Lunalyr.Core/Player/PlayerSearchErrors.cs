using System.Globalization;

namespace Lunalyr.Core.Player;

/// <summary>
/// What went wrong with a search, a play or a queue read (the Mac's <c>PlayerSearchError</c>, whose AppleScript cases,
/// <c>notRunning</c>, <c>blocked</c> and <c>scriptFailed</c>, have no Windows counterpart; the Web API's cases are new).
/// </summary>
public enum PlayerSearchError
{
    /// <summary>Something unexpected: an odd reply, a server error, no answer. "… did not answer. Try again."</summary>
    Failed = 0,

    /// <summary>The player offers nothing to search.</summary>
    Unsupported,

    /// <summary>No Spotify client ID in the settings.</summary>
    NotSetUp,

    /// <summary>Nobody is signed in to Spotify.</summary>
    NotSignedIn,

    /// <summary>The sign-in was revoked or ran out (<c>invalid_grant</c>, or a 401 a refresh didn't cure).</summary>
    SignInExpired,

    /// <summary>Spotify's player commands need Premium.</summary>
    PremiumRequired,

    /// <summary>Spotify has no active device to play on.</summary>
    NoActiveDevice,

    /// <summary>Spotify asked Lunalyr to slow down (HTTP 429).</summary>
    RateLimited,

    /// <summary>This PC has no network connection.</summary>
    Offline,

    /// <summary>Spotify refused a read (HTTP 403): in Development Mode, an account not on the app's user list.</summary>
    NotAllowed,

    /// <summary>Playback commands are off for this process (<c>--no-playback</c>, <c>--demo</c>).</summary>
    PlaybackOff,
}

/// <summary>A search, play or queue read failed. <see cref="PlayerSearchText"/> words it for the card.</summary>
public sealed class PlayerSearchException : Exception
{
    public PlayerSearchException()
        : this(PlayerSearchError.Failed)
    {
    }

    public PlayerSearchException(string message)
        : this(PlayerSearchError.Failed, message)
    {
    }

    public PlayerSearchException(string message, Exception inner)
        : this(PlayerSearchError.Failed, message, inner: inner)
    {
    }

    public PlayerSearchException(PlayerSearchError error, string? detail = null, TimeSpan? retryAfter = null, int statusCode = 0, Exception? inner = null)
        : base(detail ?? error.ToString(), inner)
    {
        Error = error;
        RetryAfter = retryAfter;
        StatusCode = statusCode;
    }

    public PlayerSearchError Error { get; }

    /// <summary>For <see cref="PlayerSearchError.RateLimited"/>: how long Spotify asked to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The HTTP status behind the error, or 0.</summary>
    public int StatusCode { get; }

    /// <summary>Spotify's own error reason, when its reply gave one (e.g. "NO_ACTIVE_DEVICE"); for the log.</summary>
    public string? Reason { get; init; }

    /// <summary>Spotify's own error message, when its reply gave one; never shown to the user as is.</summary>
    public string? ServiceMessage { get; init; }
}

/// <summary>The card's and the log's words for search and queue failures (the Mac's <c>DesktopPlayerModel.words</c>).</summary>
public static class PlayerSearchText
{
    /// <summary>
    /// Under an empty strip. Only a player that shares what plays next says nothing is playing (the Mac:
    /// <c>emptyQueueMessage</c>, where that player is Music; on Windows it is Spotify once signed in).
    /// </summary>
    public static string EmptyQueueMessage(string playerName, bool sharesQueue) =>
        sharesQueue ? "Nothing is playing." : $"{playerName} doesn’t share what plays next.";

    /// <summary>The sentence to show for a failure from <paramref name="playerName"/>'s search.</summary>
    public static string Words(Exception error, string playerName) => error switch
    {
        PlayerSearchException search => Words(search.Error, playerName, search.RetryAfter),
        _ => Words(PlayerSearchError.Failed, playerName),
    };

    /// <inheritdoc cref="Words(Exception, string)"/>
    public static string Words(PlayerSearchError error, string playerName, TimeSpan? retryAfter = null) => error switch
    {
        PlayerSearchError.Unsupported => $"{playerName} has no search Lunalyr can use.",
        PlayerSearchError.NotSetUp => "Add your Spotify client ID in Lunalyr’s settings, then sign in to search and play from here.",
        PlayerSearchError.NotSignedIn => "Sign in to Spotify in Lunalyr’s settings to search and play from here.",
        PlayerSearchError.SignInExpired => "Your Spotify sign-in has ended. Sign in again in Lunalyr’s settings.",
        PlayerSearchError.PremiumRequired => "Playing from Lunalyr needs Spotify Premium. Search still works.",
        PlayerSearchError.NoActiveDevice => "Spotify isn’t playing on any device. Start it playing, then try again.",
        PlayerSearchError.RateLimited => retryAfter is { } wait && wait > TimeSpan.Zero
            ? $"Spotify asked Lunalyr to slow down. Try again in {Seconds(wait)}."
            : "Spotify asked Lunalyr to slow down. Try again in a moment.",
        PlayerSearchError.Offline => "You’re offline. Try again when you’re back online.",
        PlayerSearchError.NotAllowed => "Spotify turned this account away. Check it’s on your developer app’s user list, then sign in again.",
        PlayerSearchError.PlaybackOff => "Playback commands are off for this run.",
        _ => $"{playerName} did not answer. Try again.",
    };

    /// <summary>For the log: what went wrong, never a song title, query or token.</summary>
    public static string LogText(Exception error) => error switch
    {
        PlayerSearchException { Error: PlayerSearchError.Failed, StatusCode: > 0 } search => $"HTTP {search.StatusCode}",
        PlayerSearchException { Error: PlayerSearchError.Failed } => "unexpected reply",
        PlayerSearchException search => search.Error.ToString(),
        OperationCanceledException => "cancelled",
        _ => error.GetType().Name,
    };

    private static string Seconds(TimeSpan wait)
    {
        var seconds = (int)Math.Ceiling(wait.TotalSeconds);
        return seconds == 1 ? "a second" : $"{seconds.ToString(CultureInfo.InvariantCulture)} seconds";
    }
}
