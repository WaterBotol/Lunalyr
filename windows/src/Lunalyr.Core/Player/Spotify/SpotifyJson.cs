using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lunalyr.Core.Player.Spotify;

// The shapes of Spotify's replies Lunalyr reads, from the Web API reference (developer.spotify.com, read Nov 2026).
// Every field is optional: replies differ between tracks and episodes, and Development Mode has dropped fields before.

/// <summary>The token endpoint's reply (code exchange and refresh).</summary>
internal sealed class SpotifyTokenReply
{
    public string? AccessToken { get; set; }

    public string? TokenType { get; set; }

    public int? ExpiresIn { get; set; }

    /// <summary>Sometimes absent on a refresh: then the old one stays valid.</summary>
    public string? RefreshToken { get; set; }

    public string? Scope { get; set; }
}

internal sealed class SpotifySearchReply
{
    public SpotifyPaging? Tracks { get; set; }

    public SpotifyPaging? Albums { get; set; }
}

internal sealed class SpotifyPaging
{
    public List<SpotifyItem?>? Items { get; set; }
}

/// <summary>A track, an episode, an album (search) or a track's album: the fields Lunalyr reads from any of them.</summary>
internal sealed class SpotifyItem
{
    public string? Id { get; set; }

    public string? Uri { get; set; }

    public string? Name { get; set; }

    public string? Type { get; set; }

    public List<SpotifyNamed?>? Artists { get; set; }

    /// <summary>A track's album.</summary>
    public SpotifyItem? Album { get; set; }

    /// <summary>An episode's show.</summary>
    public SpotifyItem? Show { get; set; }

    public List<SpotifyImage?>? Images { get; set; }

    public int? TrackNumber { get; set; }

    public int? DiscNumber { get; set; }

    public int? TotalTracks { get; set; }

    public bool? IsLocal { get; set; }

    public bool? IsPlayable { get; set; }
}

internal sealed class SpotifyNamed
{
    public string? Name { get; set; }
}

internal sealed class SpotifyImage
{
    public string? Url { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }
}

/// <summary><c>GET /v1/me/player</c>.</summary>
internal sealed class SpotifyPlaybackState
{
    public bool? ShuffleState { get; set; }

    public bool? IsPlaying { get; set; }

    public SpotifyContext? Context { get; set; }

    public SpotifyItem? Item { get; set; }
}

internal sealed class SpotifyContext
{
    public string? Type { get; set; }

    public string? Uri { get; set; }
}

/// <summary><c>GET /v1/me/player/queue</c>.</summary>
internal sealed class SpotifyQueueReply
{
    public SpotifyItem? CurrentlyPlaying { get; set; }

    public List<SpotifyItem?>? Queue { get; set; }
}

/// <summary>The body of <c>PUT /v1/me/player/play</c>.</summary>
internal sealed class SpotifyPlayBody
{
    public string? ContextUri { get; set; }

    public List<string>? Uris { get; set; }

    public SpotifyOffset? Offset { get; set; }
}

internal sealed class SpotifyOffset
{
    public string? Uri { get; set; }
}

/// <summary>What the token store keeps (encrypted): the refresh token, and the client it belongs to.</summary>
internal sealed class SpotifyStoredTokenJson
{
    public string? ClientId { get; set; }

    public string? RefreshToken { get; set; }

    public string? Scope { get; set; }
}

/// <summary>Source-generated metadata for Spotify's JSON (Native AOT safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(SpotifyTokenReply))]
[JsonSerializable(typeof(SpotifySearchReply))]
[JsonSerializable(typeof(SpotifyPlaybackState))]
[JsonSerializable(typeof(SpotifyQueueReply))]
[JsonSerializable(typeof(SpotifyPlayBody))]
[JsonSerializable(typeof(SpotifyStoredTokenJson))]
internal sealed partial class SpotifyJsonContext : JsonSerializerContext;

/// <summary>Reading Spotify's replies, never throwing on an odd one.</summary>
internal static class SpotifyJsonRead
{
    /// <summary>The body as <typeparamref name="T"/>, or null when it is empty or not that shape.</summary>
    public static T? Parse<T>(ReadOnlySpan<byte> body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class
    {
        if (body.IsEmpty) return null;
        try
        {
            return JsonSerializer.Deserialize(body, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The error in a reply: the Web API's <c>{"error":{"status","message","reason"}}</c> or the accounts service's
    /// <c>{"error":"invalid_grant","error_description"}</c>. Missing parts are empty.
    /// </summary>
    public static (string Code, string Message) Error(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) return ("", "");
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)) return ("", "");
            if (error.ValueKind == JsonValueKind.String)
            {
                var description = document.RootElement.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString() ?? "" : "";
                return (error.GetString() ?? "", description);
            }
            if (error.ValueKind == JsonValueKind.Object)
            {
                var reason = error.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                return (reason, message);
            }
        }
        catch (JsonException)
        {
        }
        return ("", "");
    }
}
