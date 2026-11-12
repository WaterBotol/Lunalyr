using Lunalyr.Core.Colour;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Player;

namespace Lunalyr.App.Player;

/// <summary>
/// What the card asks the host for its search and queue: the players to offer, each one's search and whether it can be
/// used, the queue's search, covers for rows, and the way to Settings › Music players. AppHost builds it (with
/// <c>--player-debug fake-search</c>, from the demo search).
/// </summary>
internal sealed class PlayerSearchSource
{
    /// <summary>The players Windows has sessions for (the picker's choices), in its order.</summary>
    public Func<IReadOnlyList<MediaSourceApp>> Apps { get; init; } = () => [];

    /// <summary>A player's search and whether it can be used.</summary>
    public Func<MediaSourceApp?, (PlayerSearchAvailability Availability, IPlayerSearch Search)> For { get; init; } =
        app => (PlayerSearchAvailability.None, new NoPlayerSearch(app?.DisplayName ?? "This player"));

    /// <summary>The search whose queue the strip shows: the playing player's.</summary>
    public Func<MediaSourceApp?, IPlayerSearch>? QueueFor { get; init; }

    /// <summary>Why Spotify is off for this run ("--demo", "--no-network"), or null.</summary>
    public Func<string?> SpotifyOffReason { get; init; } = () => null;

    /// <summary>A row's cover (Spotify's CDN, or the demo's painted ones); null when it can't be had.</summary>
    public Func<Uri, CancellationToken, Task<CoverArt?>>? LoadCover { get; init; }

    /// <summary>Opens a settings page ("players").</summary>
    public Action<string>? OpenSettingsPage { get; init; }

    /// <summary>The player the picker starts on when nothing playing can search (the demo search's Spotify).</summary>
    public MediaSourceApp? Preferred { get; init; }

    /// <summary>A forced pane and text for checks (<c>--player-debug pane=search,query=…</c>).</summary>
    public PlayerPane? DebugPane { get; init; }

    public string? DebugQuery { get; init; }
}

/// <summary>
/// The rows' covers, loaded once each (at most 64 kept) and handed to the painter by address. Loads run off the UI
/// thread; the card hears when one arrives. A cover that can't be had stays a plain tile.
/// </summary>
internal sealed class RowCovers(Func<Uri, CancellationToken, Task<CoverArt?>>? load, Action<Action> post, Action arrived)
{
    private readonly Dictionary<Uri, CoverArt?> _covers = [];
    private readonly HashSet<Uri> _loading = [];
    private readonly Queue<Uri> _order = new();
    private readonly CancellationTokenSource _cancel = new();

    public CoverArt? Get(Uri? uri)
    {
        if (uri is null || load is null) return null;
        if (_covers.TryGetValue(uri, out var cover)) return cover;
        if (_loading.Add(uri)) Start(uri);
        return null;
    }

    private void Start(Uri uri)
    {
        Task<CoverArt?> task;
        try
        {
            task = load!(uri, _cancel.Token);
        }
        catch (Exception)
        {
            task = Task.FromResult<CoverArt?>(null);
        }

        task.ContinueWith(t =>
        {
            var cover = t.IsCompletedSuccessfully ? t.Result : null;
            post(() =>
            {
                _loading.Remove(uri);
                _covers[uri] = cover;
                _order.Enqueue(uri);
                while (_order.Count > 64) _covers.Remove(_order.Dequeue());
                if (cover is not null) arrived();
            });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Cancel() => _cancel.Cancel();
}
