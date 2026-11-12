using System.Text.RegularExpressions;
using Lunalyr.Core.Logging;

namespace Lunalyr.Core.Player.Spotify;

/// <summary>How the Spotify service signs in and talks to the Web API. Every value can be changed for tests.</summary>
public sealed partial record SpotifyOptions
{
    /// <summary>
    /// The fixed loopback port of the redirect address, <c>http://127.0.0.1:47823/callback</c>, which the owner registers
    /// in their Spotify developer app (docs/APP-SHELL.md, "Spotify search and queue").
    /// </summary>
    public const int DefaultRedirectPort = 47823;

    /// <summary>
    /// What Lunalyr asks for: reading the player and its queue (<c>user-read-playback-state</c>,
    /// <c>user-read-currently-playing</c>) and starting playback (<c>user-modify-playback-state</c>). Search needs no
    /// scope. Nothing about the account, its library or its playlists.
    /// </summary>
    public const string Scopes = "user-read-playback-state user-read-currently-playing user-modify-playback-state";

    /// <summary>The redirect address for <paramref name="port"/>.</summary>
    public static Uri RedirectUriFor(int port) => new($"http://127.0.0.1:{port}/callback");

    /// <summary>The loopback port to listen on; 0 lets Windows choose (tests only: Spotify needs the registered one).</summary>
    public int RedirectPort { get; init; } = DefaultRedirectPort;

    /// <summary>The Web API's base address.</summary>
    public string ApiAddress { get; init; } = "https://api.spotify.com/v1";

    /// <summary>The token endpoint.</summary>
    public string TokenAddress { get; init; } = SpotifyPkce.TokenAddress;

    /// <summary>Each request's own time limit.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a sign-in waits for the browser to come back.</summary>
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>An access token this close to its expiry is refreshed before use.</summary>
    public TimeSpan RefreshMargin { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long to hold off after a 429 that didn't say (Spotify normally sends Retry-After).</summary>
    public TimeSpan DefaultRetryAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Songs at most in a queue click's line-up (the song and what was listed after it).</summary>
    public int QueueLineUpLimit { get; init; } = 50;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public Logger Logger { get; init; } = Logger.Null;

    /// <summary>
    /// A client ID as the settings keep it: trimmed, and empty unless it looks like one (32 letters and digits; Spotify's
    /// are hexadecimal).
    /// </summary>
    public static string NormalizeClientId(string? clientId)
    {
        var trimmed = clientId?.Trim() ?? "";
        return ClientIdShape().IsMatch(trimmed) ? trimmed : "";
    }

    [GeneratedRegex("^[A-Za-z0-9]{32}$")]
    private static partial Regex ClientIdShape();
}
