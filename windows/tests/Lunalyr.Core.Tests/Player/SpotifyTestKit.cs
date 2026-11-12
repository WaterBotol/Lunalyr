using System.Text;
using Lunalyr.Core.Playback;
using Lunalyr.Core.Player.Spotify;
using Lunalyr.Core.Tests.Lyrics.Sources;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Core.Tests.Player;

/// <summary>A request the fake Spotify received, read while it was being sent.</summary>
internal sealed record SpotifyRequest(string Key, string Method, string Address, string? Bearer, string Body)
{
    public string? Form(string name) => FixtureHandler.QueryOf(new Uri("http://x/?" + Body), name);
}

/// <summary>
/// Stands in for accounts.spotify.com and api.spotify.com over <see cref="FixtureHandler"/>: answers by request
/// ("POST token", "GET search", "GET me/player", "GET me/player/queue", "PUT me/player/play") from queues of canned
/// replies (the last one repeats), and records every request. Nothing reaches the network.
/// </summary>
internal sealed class SpotifyFake
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<Reply>> _replies = new(StringComparer.Ordinal);
    private readonly List<SpotifyRequest> _sent = [];

    public SpotifyFake()
    {
        Handler.Responder = request =>
        {
            var key = KeyOf(request.RequestUri!, request.Method);
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            var bearer = request.Headers.Authorization is { Scheme: "Bearer" } auth ? auth.Parameter : null;
            lock (_gate)
            {
                _sent.Add(new SpotifyRequest(key, request.Method.Method, request.RequestUri!.ToString(), bearer, body));
                if (_replies.TryGetValue(key, out var queue) && queue.Count > 0)
                    return queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            }
            return Reply.Text(500, "");
        };
    }

    public FixtureHandler Handler { get; } = new();

    public IReadOnlyList<SpotifyRequest> Sent
    {
        get
        {
            lock (_gate) return [.. _sent];
        }
    }

    public IReadOnlyList<SpotifyRequest> SentTo(string key) => [.. Sent.Where(r => r.Key == key)];

    /// <summary>Replies for <paramref name="key"/>, in order; the last one repeats.</summary>
    public SpotifyFake On(string key, params Reply[] replies)
    {
        lock (_gate) _replies[key] = new Queue<Reply>(replies);
        return this;
    }

    public static string KeyOf(Uri uri, HttpMethod method) =>
        uri.Host == "accounts.spotify.com" ? $"{method.Method} token" : $"{method.Method} {uri.AbsolutePath.Replace("/v1/", "", StringComparison.Ordinal)}";

    public static Reply Json(string fixture, int status = 200, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Spotify", fixture)), headers);

    public static Reply Empty(int status) => new(status, []);
}

internal sealed class MemoryTokenStore : ISpotifyTokenStore
{
    public SpotifyStoredToken? Token { get; set; }

    public int Saves { get; private set; }

    public int Clears { get; private set; }

    public SpotifyStoredToken? Load() => Token;

    public void Save(SpotifyStoredToken token)
    {
        Token = token;
        Saves++;
    }

    public void Clear()
    {
        Token = null;
        Clears++;
    }
}

/// <summary>A browser that opens nothing: it records the address and, when told, plays the user's part.</summary>
internal sealed class FakeBrowser : IBrowserLauncher
{
    public List<Uri> Opened { get; } = [];

    public Func<Uri, bool>? OnOpen { get; set; }

    public bool Open(Uri address)
    {
        Opened.Add(address);
        return OnOpen?.Invoke(address) ?? true;
    }
}

/// <summary>Builds an account and a search over the fake, with fake time, a memory store and a fake browser.</summary>
internal sealed class SpotifyKit
{
    public const string ClientId = "0123456789abcdef0123456789abcdef";

    public SpotifyKit(bool signedIn = true, PlaybackGuard? guard = null, string? clientId = ClientId, Func<SpotifyOptions, SpotifyOptions>? options = null)
    {
        if (signedIn) Store.Token = new SpotifyStoredToken(ClientId, "RT-stored", SpotifyOptions.Scopes);
        Guard = guard ?? new PlaybackGuard();
        var http = Fake.Handler.Client(networkUp: () => NetworkUp, time: Time);
        var settings = new SpotifyOptions { RedirectPort = 0, TimeProvider = Time };
        Account = new SpotifyAccount(http, Store, Browser, clientId, options?.Invoke(settings) ?? settings);
        Search = new SpotifyPlayerSearch(Account, http, Guard);
    }

    /// <summary>What the PC says about its network when a request fails.</summary>
    public bool NetworkUp { get; set; } = true;

    public SpotifyFake Fake { get; } = new();

    public MemoryTokenStore Store { get; } = new();

    public FakeBrowser Browser { get; } = new();

    public ManualTimeProvider Time { get; } = new();

    public PlaybackGuard Guard { get; }

    public SpotifyAccount Account { get; }

    public SpotifyPlayerSearch Search { get; }

    /// <summary>The refresh the first API call makes answers with a fresh access token.</summary>
    public SpotifyKit Refreshes()
    {
        Fake.On("POST token", SpotifyFake.Json("token-refresh.json"));
        return this;
    }

    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
