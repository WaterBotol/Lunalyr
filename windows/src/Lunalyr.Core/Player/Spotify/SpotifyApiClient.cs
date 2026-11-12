using System.Net.Http.Headers;
using System.Text.Json;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Player.Spotify;

/// <summary>What a Web API request is, for reading its errors.</summary>
internal enum SpotifyCall
{
    /// <summary>A search: no player involved.</summary>
    Search = 0,

    /// <summary>Reading the player (<c>/me/player</c>, its queue).</summary>
    PlayerRead,

    /// <summary>A playback command (<c>PUT /me/player/play</c>): Premium and an active device needed.</summary>
    PlayerCommand,
}

/// <summary>
/// Sends Web API requests with the account's access token over the app's <see cref="LyricHttpClient"/>: one refresh
/// and one retry after a 401, never more; after a 429 nothing is sent until its Retry-After has passed; every failure
/// becomes a <see cref="PlayerSearchException"/>.
/// </summary>
internal sealed class SpotifyApiClient(LyricHttpClient http, SpotifyAccount account, SpotifyOptions options)
{
    private const string Service = "Spotify";

    private readonly CategoryLogger _log = options.Logger.Player;
    private readonly Lock _gate = new();
    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;

    public SpotifyAccount Account => account;

    /// <summary>
    /// Sends one request and returns a successful reply (2xx). <paramref name="pathAndQuery"/> is relative to the API
    /// address ("me/player/queue"); <paramref name="body"/> is sent as JSON when given.
    /// </summary>
    public async Task<LyricHttpResponse> SendAsync(HttpMethod method, string pathAndQuery, SpotifyPlayBody? body, SpotifyCall call,
        CancellationToken cancellationToken)
    {
        ThrowIfQuiet();
        var token = await account.AccessTokenAsync(null, cancellationToken).ConfigureAwait(false);
        var response = await SendOnceAsync(method, pathAndQuery, body, token, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == 401)
        {
            _log.Debug("Spotify: the access token was refused; refreshing once.");
            token = await account.AccessTokenAsync(token, cancellationToken).ConfigureAwait(false);
            response = await SendOnceAsync(method, pathAndQuery, body, token, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == 401)
            {
                _log.Info("Spotify: the access token was refused again after a refresh.");
                throw new PlayerSearchException(PlayerSearchError.SignInExpired, statusCode: 401);
            }
        }

        if (response.StatusCode is >= 200 and < 300) return response;

        if (response.StatusCode == 429)
        {
            var wait = response.RetryAfter is { } after && after > TimeSpan.Zero ? after : options.DefaultRetryAfter;
            lock (_gate)
            {
                var until = options.TimeProvider.GetUtcNow() + wait;
                if (until > _quietUntil) _quietUntil = until;
            }
            _log.Info($"Spotify: rate limited; nothing more is sent for {wait.TotalSeconds:0.#} s.");
            throw new PlayerSearchException(PlayerSearchError.RateLimited, retryAfter: wait, statusCode: 429);
        }

        var (reason, message) = SpotifyJsonRead.Error(response.Body);
        var error = Classify(response.StatusCode, reason, message, call);
        _log.Info($"Spotify: {call} answered HTTP {response.StatusCode}{(reason.Length > 0 ? $" ({reason})" : "")}: {error}.");
        throw new PlayerSearchException(error, message.Length > 0 ? message : null, statusCode: response.StatusCode)
        {
            Reason = reason.Length > 0 ? reason : null,
            ServiceMessage = message.Length > 0 ? message : null,
        };
    }

    /// <summary>
    /// What an error reply means. The status decides, by the kind of request; Spotify's <c>reason</c> (documented only for
    /// some replies) can only sharpen it.
    /// </summary>
    internal static PlayerSearchError Classify(int status, string reason, string message, SpotifyCall call)
    {
        if (reason == "PREMIUM_REQUIRED") return PlayerSearchError.PremiumRequired;
        if (reason == "NO_ACTIVE_DEVICE") return PlayerSearchError.NoActiveDevice;
        return status switch
        {
            401 => PlayerSearchError.SignInExpired,
            403 when call == SpotifyCall.PlayerCommand => message.Contains("premium", StringComparison.OrdinalIgnoreCase) || reason.Length == 0
                ? PlayerSearchError.PremiumRequired
                : PlayerSearchError.Failed,
            403 => PlayerSearchError.NotAllowed,
            404 when call != SpotifyCall.Search => PlayerSearchError.NoActiveDevice,
            429 => PlayerSearchError.RateLimited,
            _ => PlayerSearchError.Failed,
        };
    }

    /// <summary>
    /// Whether a refused play could have been about the request rather than the player: a 400, or a 404 that doesn't say
    /// it is about the device. Then a plainer request may still work.
    /// </summary>
    internal static bool MayBeTheRequest(PlayerSearchException error) =>
        error.StatusCode == 400
        || (error.StatusCode == 404 && error.Reason != "NO_ACTIVE_DEVICE"
            && error.ServiceMessage?.Contains("device", StringComparison.OrdinalIgnoreCase) != true);

    internal static PlayerSearchException FromTransport(LyricSourceException ex) => ex.Failure switch
    {
        LyricSourceFailure.Offline => new PlayerSearchException(PlayerSearchError.Offline, inner: ex),
        _ => new PlayerSearchException(PlayerSearchError.Failed, ex.Message, inner: ex),
    };

    private void ThrowIfQuiet()
    {
        TimeSpan left;
        lock (_gate) left = _quietUntil - options.TimeProvider.GetUtcNow();
        if (left > TimeSpan.Zero) throw new PlayerSearchException(PlayerSearchError.RateLimited, retryAfter: left, statusCode: 429);
    }

    private async Task<LyricHttpResponse> SendOnceAsync(HttpMethod method, string pathAndQuery, SpotifyPlayBody? body, string token,
        CancellationToken cancellationToken)
    {
        var address = new Uri(options.ApiAddress.TrimEnd('/') + "/" + pathAndQuery.TrimStart('/'));
        try
        {
            return await http.SendAsync(Service, () =>
            {
                var request = new HttpRequestMessage(method, address);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (body is not null)
                {
                    var json = JsonSerializer.SerializeToUtf8Bytes(body, SpotifyJsonContext.Default.SpotifyPlayBody);
                    request.Content = new ByteArrayContent(json);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }
                return request;
            }, options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (LyricSourceException ex)
        {
            throw FromTransport(ex);
        }
    }
}
