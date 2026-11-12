using System.Net.Http.Headers;
using System.Net.Sockets;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Player.Spotify;

/// <summary>Where the Spotify sign-in stands.</summary>
public enum SpotifyAccountState
{
    /// <summary>No client ID in the settings.</summary>
    NotSetUp = 0,

    SignedOut,

    /// <summary>The browser is open on Spotify's sign-in page; waiting for it to come back.</summary>
    SigningIn,

    SignedIn,
}

/// <summary>How a sign-in ended.</summary>
public enum SpotifySignInStatus
{
    SignedIn = 0,
    NotSetUp,
    AlreadySigningIn,
    PortInUse,
    BrowserFailed,

    /// <summary>The user said no on Spotify's page.</summary>
    Denied,

    /// <summary>Lunalyr stopped waiting (closing the window, quitting).</summary>
    Cancelled,
    TimedOut,
    Offline,
    Failed,
}

/// <summary>A sign-in's end, with a sentence for the settings page.</summary>
public sealed record SpotifySignInResult(SpotifySignInStatus Status, string Message)
{
    public bool Succeeded => Status == SpotifySignInStatus.SignedIn;
}

/// <summary>
/// The owner's Spotify sign-in: Authorization Code with PKCE through the system browser and a loopback redirect, the
/// refresh token kept encrypted (<see cref="ISpotifyTokenStore"/>), access tokens refreshed before they expire.
/// Nothing is sent anywhere until the user presses Sign in or uses search; nothing is signed in without them.
/// </summary>
/// <remarks>
/// Thread-safe. Refreshes are single-flight: callers that need a token while one is being fetched wait for it. Tokens,
/// codes and redirect addresses with their query are never logged.
/// </remarks>
public sealed class SpotifyAccount
{
    private const string Service = "Spotify";

    private readonly LyricHttpClient _http;
    private readonly ISpotifyTokenStore _store;
    private readonly IBrowserLauncher _browser;
    private readonly CategoryLogger _log;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);

    private string _clientId = "";
    private SpotifyStoredToken? _stored;
    private AccessToken? _access;
    private bool _signingIn;
    private int _generation;
    private SpotifyAccountState _state;

    public SpotifyAccount(LyricHttpClient http, ISpotifyTokenStore store, IBrowserLauncher browser, string? clientId, SpotifyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(browser);
        _http = http;
        _store = store;
        _browser = browser;
        Options = options ?? new SpotifyOptions();
        _log = Options.Logger.Player;
        _clientId = SpotifyOptions.NormalizeClientId(clientId);
        _stored = store.Load();
        _state = ComputeState();
    }

    public SpotifyOptions Options { get; }

    /// <summary>The client ID in use (from the settings), or empty.</summary>
    public string ClientId
    {
        get
        {
            lock (_gate) return _clientId;
        }
    }

    public SpotifyAccountState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    /// <summary>The scopes Spotify granted at sign-in, space-separated, or empty.</summary>
    public string GrantedScope
    {
        get
        {
            lock (_gate) return Usable(_stored)?.Scope ?? "";
        }
    }

    /// <summary>Raised (on any thread) when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Takes the client ID from the settings. A different client's sign-in isn't used (its refresh token belongs to
    /// that client), so changing it shows as signed out until the user signs in again.
    /// </summary>
    public void SetClientId(string? clientId)
    {
        var normalized = SpotifyOptions.NormalizeClientId(clientId);
        lock (_gate)
        {
            if (normalized == _clientId) return;
            _clientId = normalized;
            _access = null;
            _generation++;
        }
        _log.Info(normalized.Length == 0 ? "Spotify: no client ID set." : "Spotify: client ID changed.");
        UpdateState();
    }

    /// <summary>
    /// Opens Spotify's sign-in page in the system browser and waits (up to <see cref="SpotifyOptions.SignInTimeout"/>)
    /// for it to come back to the loopback address, then exchanges the code for tokens and keeps the refresh token.
    /// Call only when the user presses Sign in.
    /// </summary>
    public async Task<SpotifySignInResult> SignInAsync(CancellationToken cancellationToken = default)
    {
        string clientId;
        int generation;
        lock (_gate)
        {
            clientId = _clientId;
            if (clientId.Length == 0)
                return new(SpotifySignInStatus.NotSetUp, "Add your Spotify client ID first, then sign in.");
            if (_signingIn)
                return new(SpotifySignInStatus.AlreadySigningIn, "A Spotify sign-in is already open in your browser.");
            _signingIn = true;
            generation = ++_generation;
        }
        UpdateState();

        try
        {
            LoopbackRedirectListener listener;
            try
            {
                listener = LoopbackRedirectListener.Start(Options.RedirectPort);
            }
            catch (SocketException ex)
            {
                _log.Warn($"Spotify sign-in: couldn't listen on 127.0.0.1:{Options.RedirectPort} ({ex.SocketErrorCode}).");
                return new(SpotifySignInStatus.PortInUse,
                    $"Another app is using port {Options.RedirectPort}, so Spotify can’t hand the sign-in back. Close it and try again.");
            }

            using (listener)
            {
                var verifier = SpotifyPkce.CreateVerifier();
                var state = SpotifyPkce.CreateState();
                var authorize = SpotifyPkce.AuthorizeUri(clientId, listener.RedirectUri, SpotifyPkce.Challenge(verifier), state, SpotifyOptions.Scopes);
                if (!_browser.Open(authorize))
                {
                    _log.Warn("Spotify sign-in: couldn't open the browser.");
                    return new(SpotifySignInStatus.BrowserFailed, "Couldn’t open your browser for the Spotify sign-in.");
                }
                _log.Info("Spotify sign-in: waiting for the browser.");

                LoopbackCallback callback;
                using (var deadline = new CancellationTokenSource(Options.SignInTimeout, Options.TimeProvider))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
                {
                    try
                    {
                        callback = await listener.WaitAsync(state, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _log.Info("Spotify sign-in: timed out.");
                        return new(SpotifySignInStatus.TimedOut, "The Spotify sign-in took too long. Try again.");
                    }
                    catch (OperationCanceledException)
                    {
                        _log.Info("Spotify sign-in: cancelled.");
                        return new(SpotifySignInStatus.Cancelled, "Spotify sign-in was cancelled.");
                    }
                }

                if (callback.Code is not { } code)
                {
                    var denied = callback.Error == "access_denied";
                    _log.Info($"Spotify sign-in: {(denied ? "declined on Spotify's page" : $"Spotify answered {callback.Error}")}.");
                    return denied
                        ? new(SpotifySignInStatus.Denied, "Spotify sign-in was cancelled.")
                        : new(SpotifySignInStatus.Failed, "Spotify didn’t accept the sign-in. Check the client ID and the redirect address in your developer app.");
                }

                return await ExchangeAsync(clientId, code, verifier, listener.RedirectUri, generation, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate) _signingIn = false;
            UpdateState();
        }
    }

    /// <summary>Forgets the sign-in: the stored refresh token and the access token in memory. Nothing is sent to Spotify.</summary>
    public void SignOut()
    {
        lock (_gate)
        {
            _stored = null;
            _access = null;
            _generation++;
        }
        _store.Clear();
        _log.Info("Signed out of Spotify.");
        UpdateState();
    }

    /// <summary>
    /// An access token valid for at least <see cref="SpotifyOptions.RefreshMargin"/>, refreshing it when it is not.
    /// <paramref name="rejected"/> is a token the Web API just refused (401): it is never handed out again.
    /// </summary>
    internal async Task<string> AccessTokenAsync(string? rejected, CancellationToken cancellationToken)
    {
        if (Valid(rejected) is { } quick) return quick;
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (Valid(rejected) is { } fresh) return fresh;
            return await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refresh.Release();
        }
    }

    private string? Valid(string? rejected)
    {
        lock (_gate)
        {
            var access = _access;
            if (access is null || access.Token == rejected) return null;
            return Options.TimeProvider.GetUtcNow() < access.ExpiresAt - Options.RefreshMargin ? access.Token : null;
        }
    }

    private async Task<string> RefreshAsync(CancellationToken cancellationToken)
    {
        string clientId;
        SpotifyStoredToken stored;
        int generation;
        lock (_gate)
        {
            clientId = _clientId;
            if (clientId.Length == 0) throw new PlayerSearchException(PlayerSearchError.NotSetUp);
            stored = Usable(_stored) ?? throw new PlayerSearchException(PlayerSearchError.NotSignedIn);
            generation = _generation;
        }

        var response = await PostTokenAsync(
        [
            new("grant_type", "refresh_token"),
            new("refresh_token", stored.RefreshToken),
            new("client_id", clientId),
        ], cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == 200 && Parse(response) is { } reply)
        {
            lock (_gate)
            {
                if (generation != _generation) throw new PlayerSearchException(PlayerSearchError.NotSignedIn, "signed out during a refresh");
                _access = reply.Access;
                // A new refresh token replaces the old one; without one the old one stays valid.
                if (reply.Refresh is { Length: > 0 } rotated && rotated != stored.RefreshToken)
                {
                    _stored = stored with { RefreshToken = rotated, Scope = reply.Scope ?? stored.Scope };
                    // Kept in memory even if the file can't be written; the next rotation or sign-in tries again.
                    TrySave(_stored);
                }
            }
            _log.Debug("Spotify: access token refreshed.");
            return reply.Access.Token;
        }

        var (code, _) = SpotifyJsonRead.Error(response.Body);
        if (response.StatusCode is 400 or 401 && code == "invalid_grant")
        {
            // Revoked, expired (six months) or issued to another client: only a new sign-in helps.
            bool current;
            lock (_gate)
            {
                current = generation == _generation;
                if (current)
                {
                    _stored = null;
                    _access = null;
                    _generation++;
                }
            }
            if (current)
            {
                _store.Clear();
                _log.Info("Spotify: the sign-in is no longer valid (invalid_grant); signed out.");
                UpdateState();
            }
            throw new PlayerSearchException(PlayerSearchError.SignInExpired, statusCode: response.StatusCode);
        }

        if (response.StatusCode == 429)
            throw new PlayerSearchException(PlayerSearchError.RateLimited, retryAfter: response.RetryAfter ?? Options.DefaultRetryAfter, statusCode: 429);

        _log.Warn($"Spotify: token refresh answered HTTP {response.StatusCode}{(code.Length > 0 ? $" ({code})" : "")}.");
        throw new PlayerSearchException(PlayerSearchError.Failed, statusCode: response.StatusCode);
    }

    private async Task<SpotifySignInResult> ExchangeAsync(string clientId, string code, string verifier, Uri redirectUri, int generation,
        CancellationToken cancellationToken)
    {
        LyricHttpResponse response;
        try
        {
            response = await PostTokenAsync(
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("redirect_uri", redirectUri.OriginalString),
                new("client_id", clientId),
                new("code_verifier", verifier),
            ], cancellationToken).ConfigureAwait(false);
        }
        catch (PlayerSearchException ex) when (ex.Error == PlayerSearchError.Offline)
        {
            return new(SpotifySignInStatus.Offline, "You’re offline. Sign in again when you’re back online.");
        }
        catch (PlayerSearchException)
        {
            return new(SpotifySignInStatus.Failed, "Spotify did not answer. Try again.");
        }

        if (response.StatusCode == 200 && Parse(response) is { Refresh: { Length: > 0 } refresh } reply)
        {
            var stored = new SpotifyStoredToken(clientId, refresh, reply.Scope ?? "");
            lock (_gate)
            {
                if (generation != _generation || clientId != _clientId)
                    return new(SpotifySignInStatus.Cancelled, "The Spotify settings changed during the sign-in. Sign in again.");
            }
            if (!TrySave(stored))
                return new(SpotifySignInStatus.Failed, "Couldn’t save the Spotify sign-in in Lunalyr’s data folder. Try again.");
            lock (_gate)
            {
                if (generation != _generation || clientId != _clientId)
                {
                    // Signed out or changed while the file was written: don't leave this sign-in behind.
                    _store.Clear();
                    return new(SpotifySignInStatus.Cancelled, "The Spotify settings changed during the sign-in. Sign in again.");
                }
                _stored = stored;
                _access = reply.Access;
                _generation++;
            }
            _log.Info($"Signed in to Spotify (scopes: {(reply.Scope is { Length: > 0 } scope ? scope : "none listed")}).");
            return new(SpotifySignInStatus.SignedIn, "Signed in to Spotify.");
        }

        var (error, _) = SpotifyJsonRead.Error(response.Body);
        _log.Warn($"Spotify sign-in: the code exchange answered HTTP {response.StatusCode}{(error.Length > 0 ? $" ({error})" : "")}.");
        return error == "invalid_client"
            ? new(SpotifySignInStatus.Failed, "Spotify didn’t recognise the client ID. Check it in your developer dashboard.")
            : new(SpotifySignInStatus.Failed, "Spotify didn’t accept the sign-in. Check the client ID and the redirect address in your developer app.");
    }

    /// <summary>POSTs a form to the token endpoint. Network failures become <see cref="PlayerSearchException"/>.</summary>
    private async Task<LyricHttpResponse> PostTokenAsync(KeyValuePair<string, string>[] form, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(Service, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, Options.TokenAddress) { Content = new FormUrlEncodedContent(form) };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return request;
            }, Options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (LyricSourceException ex)
        {
            throw SpotifyApiClient.FromTransport(ex);
        }
    }

    private (AccessToken Access, string? Refresh, string? Scope)? Parse(LyricHttpResponse response)
    {
        var reply = SpotifyJsonRead.Parse(response.Body, SpotifyJsonContext.Default.SpotifyTokenReply);
        if (reply?.AccessToken is not { Length: > 0 } token) return null;
        var lifetime = TimeSpan.FromSeconds(reply.ExpiresIn is int seconds && seconds > 0 ? seconds : 3600);
        return (new AccessToken(token, Options.TimeProvider.GetUtcNow() + lifetime), reply.RefreshToken, reply.Scope);
    }

    /// <summary>Writes the token; false (logged, no token in the message) when the file or the encryption failed.</summary>
    private bool TrySave(SpotifyStoredToken token)
    {
        try
        {
            _store.Save(token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Security.Cryptography.CryptographicException)
        {
            _log.Warn($"Spotify: couldn't save the sign-in ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <summary>The stored token, when it belongs to the client in use. Call under the lock.</summary>
    private SpotifyStoredToken? Usable(SpotifyStoredToken? stored) =>
        stored is not null && _clientId.Length > 0 && string.Equals(stored.ClientId, _clientId, StringComparison.Ordinal) ? stored : null;

    private SpotifyAccountState ComputeState()
    {
        if (_clientId.Length == 0) return SpotifyAccountState.NotSetUp;
        if (_signingIn) return SpotifyAccountState.SigningIn;
        return Usable(_stored) is not null ? SpotifyAccountState.SignedIn : SpotifyAccountState.SignedOut;
    }

    private void UpdateState()
    {
        bool changed;
        lock (_gate)
        {
            var next = ComputeState();
            changed = next != _state;
            _state = next;
        }
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record AccessToken(string Token, DateTimeOffset ExpiresAt)
    {
        public override string ToString() => $"AccessToken {{ ExpiresAt = {ExpiresAt:O} }}";
    }
}
