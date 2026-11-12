using System.Globalization;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Core.Playback;

namespace Lunalyr.Core.Player.Spotify;

/// <summary>
/// The desktop player's search, play and queue for Spotify, through the Web API with the owner's own sign-in
/// (<see cref="SpotifyAccount"/>).
/// </summary>
/// <remarks>
/// <para>
/// How a click plays, following the Mac's rule that a song carries on with what follows it where the player allows:
/// a song from search plays inside its album, from that song on ("Then the rest of its album."); a song with no album,
/// or the last song of its album, plays on its own and says so; an album plays from its start. If Spotify refuses the
/// album form for a reason that may be the request's (not Premium, not the device), the song is played on its own
/// instead ("Couldn't line up what comes next…"), as the Mac's rescue does. A queue click plays that song and the songs
/// Spotify listed after it, in order (the Mac copies the song and the ones after it into a queue of its own).
/// </para>
/// <para>
/// Clicks run one at a time and only the latest plays: a click overtaken by a newer one while it waited (for a token,
/// or behind the previous click) sends nothing (<see cref="PlayOutcome.Superseded"/>). Every play goes through the
/// <see cref="PlaybackGuard"/> first, so <c>--no-playback</c> and <c>--demo</c> send nothing at all.
/// </para>
/// <para>Not verified against the live service (the development rules forbid playing anything); the shapes come from
/// Spotify's published reference and are covered by fixtures.</para>
/// </remarks>
public sealed class SpotifyPlayerSearch : IPlayerSearch
{
    private const string Target = "Spotify";

    private readonly SpotifyApiClient _api;
    private readonly PlaybackGuard _guard;
    private readonly SpotifyOptions _options;
    private readonly CategoryLogger _log;
    private readonly SemaphoreSlim _clicks = new(1, 1);
    private readonly Lock _gate = new();
    private int _latestClick;
    private PlayerQueueSnapshot _lastQueue = PlayerQueueSnapshot.Empty;

    public SpotifyPlayerSearch(SpotifyAccount account, LyricHttpClient http, PlaybackGuard guard)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(guard);
        _options = account.Options;
        _api = new SpotifyApiClient(http, account, _options);
        _guard = guard;
        _log = _options.Logger.Player;
        Account = account;
    }

    public SpotifyAccount Account { get; }

    public string PlayerName => "Spotify";

    public bool SharesQueue => true;

    public async Task<IReadOnlyList<PlayerSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var trimmed = query?.Trim() ?? "";
        if (new StringInfo(trimmed).LengthInTextElements < 2) return [];
        // No market: with a user's token Spotify answers for that account's country.
        var path = $"search?q={Uri.EscapeDataString(trimmed)}&type=track%2Calbum&limit={SpotifyParsing.SongLimit}";
        var response = await _api.SendAsync(HttpMethod.Get, path, null, SpotifyCall.Search, cancellationToken).ConfigureAwait(false);
        var results = SpotifyParsing.Search(response.Body);
        _log.Debug($"Spotify search: {results.Count(r => r.Kind == PlayerSearchResultKind.Song)} songs, "
            + $"{results.Count(r => r.Kind == PlayerSearchResultKind.Album)} albums.");
        return results;
    }

    public Task<PlayOutcome> PlayAsync(PlayerSearchResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return RunClickAsync(click => PlayResultAsync(result, click, cancellationToken), cancellationToken);
    }

    public async Task<PlayerQueueSnapshot> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        var stateReply = await _api.SendAsync(HttpMethod.Get, "me/player?additional_types=track%2Cepisode", null, SpotifyCall.PlayerRead,
            cancellationToken).ConfigureAwait(false);
        // 204 (or nothing): no device is playing anything.
        var state = SpotifyJsonRead.Parse(stateReply.Body, SpotifyJsonContext.Default.SpotifyPlaybackState);
        if (state is null)
        {
            Remember(PlayerQueueSnapshot.Empty);
            return PlayerQueueSnapshot.Empty;
        }

        var queueReply = await _api.SendAsync(HttpMethod.Get, "me/player/queue", null, SpotifyCall.PlayerRead, cancellationToken)
            .ConfigureAwait(false);
        var queue = SpotifyJsonRead.Parse(queueReply.Body, SpotifyJsonContext.Default.SpotifyQueueReply);
        var snapshot = SpotifyParsing.Queue(state, queue);
        Remember(snapshot);
        return snapshot;
    }

    public Task<PlayOutcome> PlayQueueItemAsync(PlayerQueueItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        // The song playing: play/pause is the transport's job, nothing to send.
        if (item.IsCurrent) return Task.FromResult(PlayOutcome.Unchanged);
        // A song without an id can't be found again; the card doesn't offer it (PlayerQueueItem.IsPlayable).
        if (item.PersistentId is null) return Task.FromException<PlayOutcome>(new PlayerSearchException(PlayerSearchError.Failed, "not playable"));
        return RunClickAsync(click => PlayQueueAsync(item, click, cancellationToken), cancellationToken);
    }

    /// <summary>The song and what was listed after it, for a queue click; songs Spotify can't start are left out.</summary>
    internal static (List<string> Uris, int Wanted) LineUp(PlayerQueueSnapshot snapshot, PlayerQueueItem item, int limit)
    {
        var uris = new List<string> { item.PersistentId! };
        var wanted = 1;
        var start = -1;
        for (var i = 0; i < snapshot.Items.Count; i++)
        {
            if (snapshot.Items[i].Id == item.Id)
            {
                start = i;
                break;
            }
        }
        if (start < 0) return (uris, wanted);
        foreach (var next in snapshot.Items.Skip(start + 1))
        {
            if (wanted >= limit) break;
            if (next.IsCurrent) continue;
            wanted++;
            if (next.PersistentId is { } uri) uris.Add(uri);
        }
        return (uris, wanted);
    }

    private async Task<PlayOutcome> PlayResultAsync(PlayerSearchResult result, int click, CancellationToken cancellationToken)
    {
        var target = result.Target;
        if (result.Kind == PlayerSearchResultKind.Album)
        {
            if (!await SendPlayAsync(click, new SpotifyPlayBody { ContextUri = target.Uri }, cancellationToken).ConfigureAwait(false))
                return PlayOutcome.Superseded;
            _log.Info("Spotify: playing an album from its start.");
            return PlayOutcome.Queued(target.AlbumTracks, target.AlbumTracks, fromAlbum: false);
        }

        if (target.AlbumUri is null || target.IsLastOnAlbum)
        {
            // A one-song release (a single) is just "this song only".
            var why = target.AlbumUri is null || target.AlbumTracks == 1 ? QueueSolo.Single : QueueSolo.LastOnAlbum;
            if (!await SendPlayAsync(click, new SpotifyPlayBody { Uris = [target.Uri] }, cancellationToken).ConfigureAwait(false))
                return PlayOutcome.Superseded;
            _log.Info($"Spotify: playing a song alone ({why}).");
            return PlayOutcome.Alone(why);
        }

        try
        {
            var body = new SpotifyPlayBody { ContextUri = target.AlbumUri, Offset = new SpotifyOffset { Uri = target.Uri } };
            if (!await SendPlayAsync(click, body, cancellationToken).ConfigureAwait(false)) return PlayOutcome.Superseded;
        }
        catch (PlayerSearchException ex) when (SpotifyApiClient.MayBeTheRequest(ex))
        {
            _log.Info($"Spotify: the album form was refused ({PlayerSearchText.LogText(ex)}); playing the song alone.");
            if (!await SendPlayAsync(click, new SpotifyPlayBody { Uris = [target.Uri] }, cancellationToken).ConfigureAwait(false))
                return PlayOutcome.Superseded;
            return PlayOutcome.Alone(QueueSolo.Rescued);
        }

        _log.Info("Spotify: playing a song, then the rest of its album.");
        var rest = target.AlbumTracks > 0 && target.DiscNumber <= 1 && target.TrackNumber > 0
            ? Math.Max(1, target.AlbumTracks - target.TrackNumber + 1)
            : 1;
        return PlayOutcome.Queued(rest, rest, fromAlbum: true);
    }

    private async Task<PlayOutcome> PlayQueueAsync(PlayerQueueItem item, int click, CancellationToken cancellationToken)
    {
        PlayerQueueSnapshot snapshot;
        lock (_gate) snapshot = _lastQueue;
        var (uris, wanted) = LineUp(snapshot, item, Math.Max(1, _options.QueueLineUpLimit));
        try
        {
            if (!await SendPlayAsync(click, new SpotifyPlayBody { Uris = uris }, cancellationToken).ConfigureAwait(false))
                return PlayOutcome.Superseded;
        }
        catch (PlayerSearchException ex) when (uris.Count > 1 && SpotifyApiClient.MayBeTheRequest(ex))
        {
            // The line-up was refused (an episode among songs, say): the song still plays, on its own.
            _log.Info($"Spotify: the line-up was refused ({PlayerSearchText.LogText(ex)}); playing the song alone.");
            if (!await SendPlayAsync(click, new SpotifyPlayBody { Uris = [uris[0]] }, cancellationToken).ConfigureAwait(false))
                return PlayOutcome.Superseded;
            return PlayOutcome.Alone(QueueSolo.Rescued);
        }
        _log.Info($"Spotify: playing a queued song and {uris.Count - 1} after it ({wanted - uris.Count} left out).");
        return PlayOutcome.Queued(uris.Count, wanted, fromAlbum: false);
    }

    /// <summary>
    /// One click at a time, numbered so a click overtaken by a newer one plays nothing. The number is taken before
    /// waiting, so the newest click is known at once.
    /// </summary>
    private async Task<PlayOutcome> RunClickAsync(Func<int, Task<PlayOutcome>> play, CancellationToken cancellationToken)
    {
        var click = Interlocked.Increment(ref _latestClick);
        // Refused before anything is sent, not even a token refresh.
        if (_guard.IsBlocked)
        {
            _guard.TryAllow("play", Target, message => _log.Info(message));
            throw new PlayerSearchException(PlayerSearchError.PlaybackOff);
        }

        await _clicks.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsLatest(click))
            {
                _log.Info("Spotify: a newer click came first; this one plays nothing.");
                return PlayOutcome.Superseded;
            }
            return await play(click).ConfigureAwait(false);
        }
        finally
        {
            _clicks.Release();
        }
    }

    /// <summary>
    /// Sends one <c>PUT /me/player/play</c>, unless a newer click came while the token was fetched (false). The guard
    /// is asked immediately before.
    /// </summary>
    private async Task<bool> SendPlayAsync(int click, SpotifyPlayBody body, CancellationToken cancellationToken)
    {
        // A fresh token first, so the check below is the last thing before the command.
        await Account.AccessTokenAsync(null, cancellationToken).ConfigureAwait(false);
        if (!IsLatest(click))
        {
            _log.Info("Spotify: a newer click came first; this one plays nothing.");
            return false;
        }
        if (!_guard.TryAllow("play", Target, message => _log.Info(message)))
            throw new PlayerSearchException(PlayerSearchError.PlaybackOff);
        await _api.SendAsync(HttpMethod.Put, "me/player/play", body, SpotifyCall.PlayerCommand, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private bool IsLatest(int click) => Volatile.Read(ref _latestClick) == click;

    private void Remember(PlayerQueueSnapshot snapshot)
    {
        lock (_gate) _lastQueue = snapshot;
    }
}
