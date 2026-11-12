using Lunalyr.Core.NowPlaying;

namespace Lunalyr.Core.Player;

/// <summary>
/// Searching and playing, one implementation per player (the Mac's <c>PlayerSearching</c>). On Windows: Spotify
/// through its Web API with the owner's own sign-in (<see cref="Spotify.SpotifyPlayerSearch"/>), and
/// <see cref="NoPlayerSearch"/> for every other player.
/// </summary>
/// <remarks>
/// Every method may be called from any thread and throws <see cref="PlayerSearchException"/> for anything the user
/// should hear about (its <see cref="PlayerSearchText.Words(PlayerSearchException, string)"/> is the sentence to show).
/// Play methods send a real playback command: call them only for an explicit click, never from background logic or
/// tests with a real player. They go through <see cref="Playback.PlaybackGuard"/>.
/// </remarks>
public interface IPlayerSearch
{
    /// <summary>The player's name for the card's wording ("Spotify").</summary>
    string PlayerName { get; }

    /// <summary>Whether the player shares what plays next (the card's empty-strip wording depends on it).</summary>
    bool SharesQueue { get; }

    /// <summary>Songs, then albums. Fewer than two characters typed (after trimming) finds nothing and asks nobody.</summary>
    Task<IReadOnlyList<PlayerSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Plays a result and says how it plays (a song that plays on its own, and why).</summary>
    Task<PlayOutcome> PlayAsync(PlayerSearchResult result, CancellationToken cancellationToken = default);

    /// <summary>The song playing and what the player says comes after it; empty when nothing plays.</summary>
    Task<PlayerQueueSnapshot> GetQueueAsync(CancellationToken cancellationToken = default);

    /// <summary>Plays a song from the last <see cref="GetQueueAsync"/>, carrying on with what was listed after it.</summary>
    Task<PlayOutcome> PlayQueueItemAsync(PlayerQueueItem item, CancellationToken cancellationToken = default);
}

/// <summary>
/// A player with no search or queue Lunalyr can use (every player but Spotify on Windows; the Mac's <c>NoSearch</c>):
/// search and play say so, the queue is empty and a queue click does nothing.
/// </summary>
public sealed class NoPlayerSearch(string playerName) : IPlayerSearch
{
    public string PlayerName { get; } = string.IsNullOrWhiteSpace(playerName) ? "This player" : playerName;

    public bool SharesQueue => false;

    public Task<IReadOnlyList<PlayerSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<PlayerSearchResult>>(new PlayerSearchException(PlayerSearchError.Unsupported));

    public Task<PlayOutcome> PlayAsync(PlayerSearchResult result, CancellationToken cancellationToken = default) =>
        Task.FromException<PlayOutcome>(new PlayerSearchException(PlayerSearchError.Unsupported));

    public Task<PlayerQueueSnapshot> GetQueueAsync(CancellationToken cancellationToken = default) => Task.FromResult(PlayerQueueSnapshot.Empty);

    public Task<PlayOutcome> PlayQueueItemAsync(PlayerQueueItem item, CancellationToken cancellationToken = default) =>
        Task.FromResult(PlayOutcome.Unchanged);
}

/// <summary>Whether the card should offer search for the player playing, and if not, why.</summary>
public enum PlayerSearchAvailability
{
    /// <summary>The player offers nothing to search (not Spotify, or Spotify isn't available in this run).</summary>
    None = 0,

    /// <summary>Spotify is playing but no client ID is in the settings yet.</summary>
    NeedsSetUp,

    /// <summary>Spotify is playing and set up, but nobody is signed in.</summary>
    NeedsSignIn,

    /// <summary>Search, play and the queue work.</summary>
    Ready,
}

/// <summary>
/// Picks the search for the player Windows says is playing: Spotify's when the GSMTC session is the Spotify app and the
/// Spotify service exists in this run, <see cref="NoPlayerSearch"/> otherwise.
/// </summary>
/// <remarks>
/// Only the Spotify desktop app's session counts. Spotify's web player reports as its browser, and that session can't
/// be told apart from any other tab, so it gets no search.
/// </remarks>
public sealed class PlayerSearches(Spotify.SpotifyPlayerSearch? spotify)
{
    /// <summary>No Spotify service (demo, <c>--no-network</c>, tests): every player gets <see cref="NoPlayerSearch"/>.</summary>
    public static PlayerSearches Off { get; } = new(null);

    public Spotify.SpotifyPlayerSearch? Spotify { get; } = spotify;

    /// <summary>Whether the session's app is the Spotify desktop app.</summary>
    public static bool IsSpotify(MediaSourceApp? app) => app?.Kind == MediaSourceKind.Spotify;

    /// <summary>The search for <paramref name="app"/> (the GSMTC session's <c>SourceApp</c>; null when nothing plays).</summary>
    public IPlayerSearch For(MediaSourceApp? app) =>
        IsSpotify(app) && Spotify is { } spotify ? spotify : new NoPlayerSearch(app?.DisplayName ?? "This player");

    /// <summary>Whether to offer search for <paramref name="app"/>, from the Spotify account's state.</summary>
    public PlayerSearchAvailability Availability(MediaSourceApp? app)
    {
        if (!IsSpotify(app) || Spotify is not { } spotify) return PlayerSearchAvailability.None;
        return spotify.Account.State switch
        {
            Player.Spotify.SpotifyAccountState.NotSetUp => PlayerSearchAvailability.NeedsSetUp,
            Player.Spotify.SpotifyAccountState.SignedIn => PlayerSearchAvailability.Ready,
            _ => PlayerSearchAvailability.NeedsSignIn,
        };
    }
}
