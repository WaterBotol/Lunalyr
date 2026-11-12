using System.Globalization;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Playback;
using Lunalyr.Core.Player;

namespace Lunalyr.Shell.Player;

/// <summary>
/// <c>--player-debug fake-search</c>: a search for checks and screenshots of the card's search and queue under
/// <c>--demo</c>. It answers from invented songs (covers painted in code, <see cref="CoverScheme"/>), lists an invented
/// queue around the demo song, and never plays anything: every play is refused through the playback guard, as a real
/// search's would be with <c>--demo</c>. Nothing here touches the network or a player.
/// </summary>
public sealed class DemoPlayerSearch : IPlayerSearch
{
    /// <summary>The covers' addresses: <c>lunalyr-demo://cover/{id}</c>, painted by the app, never fetched.</summary>
    public const string CoverScheme = "lunalyr-demo";

    private static readonly (string Id, PlayerSearchResultKind Kind, string Title, string Artist, string Album)[] Catalogue =
    [
        ("s1", PlayerSearchResultKind.Song, "Evening Tide", "The Lunalyr Demo Band", "Harbour Lights"),
        ("s2", PlayerSearchResultKind.Song, "Evenings on the Quay", "Mira Vale", "Lantern Songs"),
        ("s3", PlayerSearchResultKind.Song, "Every Evening, Again", "Sora & the Tidewater", "Small Hours"),
        ("s4", PlayerSearchResultKind.Song, "Evening Bus", "Northbound Signals", "Evening Bus"),
        ("s5", PlayerSearchResultKind.Song, "The Evening Shift", "Hollow Pine Orchestra", "Night Ledger"),
        ("s6", PlayerSearchResultKind.Song, "Neon Harbour", "The Lunalyr Demo Band", "Harbour Lights"),
        ("s7", PlayerSearchResultKind.Song, "夕暮れの窓", "The Lunalyr Demo Band", "Harbour Lights"),
        ("a1", PlayerSearchResultKind.Album, "Evening Atlas", "Mira Vale", ""),
        ("a2", PlayerSearchResultKind.Album, "Evenings", "The Lunalyr Demo Band", ""),
    ];

    private static readonly (string Title, string Artist)[] UpNext =
    [
        ("夕暮れの窓", "The Lunalyr Demo Band"),
        ("Evening Tide", "The Lunalyr Demo Band"),
        ("Lantern Song", "Mira Vale"),
        ("Small Hours", "Sora & the Tidewater"),
        ("Night Ledger", "Hollow Pine Orchestra"),
        ("Platform Nine", "Northbound Signals"),
    ];

    private readonly PlaybackGuard _guard;
    private readonly Func<PlayedTrack?> _playing;
    private readonly TimeSpan _delay;
    private readonly CategoryLogger _log;

    /// <param name="playing">The song the demo player is playing (the queue's current row).</param>
    /// <param name="delay">How long a search "takes", so the card shows "Searching…" (zero in tests).</param>
    public DemoPlayerSearch(PlaybackGuard guard, Func<PlayedTrack?> playing, TimeSpan delay, Logger? logger = null)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _playing = playing ?? throw new ArgumentNullException(nameof(playing));
        _delay = delay;
        _log = (logger ?? Logger.Null).Player;
    }

    public string PlayerName => "Spotify";

    public bool SharesQueue => true;

    public async Task<IReadOnlyList<PlayerSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var words = (query ?? "").Trim();
        if (new StringInfo(words).LengthInTextElements < 2) return [];
        if (_delay > TimeSpan.Zero) await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        var wanted = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [.. Catalogue
            .Where(c => wanted.All(w => $"{c.Title} {c.Artist} {c.Album}".Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Kind)
            .Select(c => new PlayerSearchResult(c.Id, c.Kind, c.Title, c.Artist, c.Album, CoverFor(c.Id),
                new PlayerSearchTarget($"demo:{c.Id}", c.Kind == PlayerSearchResultKind.Song ? "demo:album" : null)))];
    }

    public Task<PlayOutcome> PlayAsync(PlayerSearchResult result, CancellationToken cancellationToken = default) => Refuse();

    public Task<PlayerQueueSnapshot> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        var playing = _playing();
        if (playing is null) return Task.FromResult(PlayerQueueSnapshot.Empty);
        var items = new List<PlayerQueueItem>
        {
            new("q1", playing.Title, playing.Artist, 1, IsCurrent: true, PersistentId: "demo:q1") { ArtworkUri = CoverFor("q1") },
        };
        var index = 2;
        foreach (var (title, artist) in UpNext.Where(u => u.Title != playing.Title))
        {
            var id = $"q{index}";
            items.Add(new PlayerQueueItem(id, title, artist, index, PersistentId: $"demo:{id}") { ArtworkUri = CoverFor(id) });
            index++;
        }

        return Task.FromResult(new PlayerQueueSnapshot(items) { ContextUri = "demo:album" });
    }

    public Task<PlayOutcome> PlayQueueItemAsync(PlayerQueueItem item, CancellationToken cancellationToken = default) =>
        item.IsCurrent ? Task.FromResult(PlayOutcome.Unchanged) : Refuse();

    /// <summary>Every play is refused: through the guard when it is on (it always is with <c>--demo</c>), and anyway.</summary>
    private Task<PlayOutcome> Refuse()
    {
        if (_guard.IsBlocked) _guard.TryAllow("play", "the demo search", message => _log.Info(message));
        else _log.Info("Demo search: plays nothing.");
        return Task.FromException<PlayOutcome>(new PlayerSearchException(PlayerSearchError.PlaybackOff));
    }

    public static Uri CoverFor(string id) => new($"{CoverScheme}://cover/{id}");
}
