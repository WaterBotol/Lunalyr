using System.Text.Json;
using System.Text.Json.Serialization;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Apple's public catalogue (the iTunes Search API, <c>itunes.apple.com/search</c> and <c>/lookup</c>) over the shared
/// <see cref="LyricHttpClient"/>: its generic User-Agent, no account, no personal data. Only a song's title and artist,
/// and the storefront, are sent. A 4 s timeout and at most 2 MB read, as on the Mac. Any failure is a null reply, so
/// it is asked again next time. <see cref="OriginalTitles"/> paces the requests.
/// </summary>
public sealed class ItunesCatalogue(LyricHttpClient http) : IOriginalTitleCatalogue
{
    private const string Service = "Apple’s catalogue";

    public const string Address = "https://itunes.apple.com/";

    /// <summary>Replies larger than this are not read.</summary>
    public const int MaxReplyBytes = 2_000_000;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    /// <summary>The search the Mac asks: songs only, at most 15, in one storefront.</summary>
    public static Uri SearchUri(string term, string country) => LyricHttpClient.BuildUri(Address + "search",
        [new("term", term), new("entity", "song"), new("country", country), new("limit", "15")]);

    /// <summary>One catalogue entry, as a storefront names it in a language.</summary>
    public static Uri LookupUri(long id, string country, string language) => LyricHttpClient.BuildUri(Address + "lookup",
        [new("id", id.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("country", country), new("lang", language)]);

    public Task<IReadOnlyList<CatalogueSong>?> SearchAsync(string term, string country, CancellationToken cancellationToken) =>
        AskAsync(SearchUri(term, country), cancellationToken);

    public Task<IReadOnlyList<CatalogueSong>?> LookupAsync(long id, string country, string language, CancellationToken cancellationToken) =>
        AskAsync(LookupUri(id, country, language), cancellationToken);

    private async Task<IReadOnlyList<CatalogueSong>?> AskAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.GetAsync(Service, uri, RequestTimeout, referer: null, cancellationToken).ConfigureAwait(false);
            return response.IsOk && response.Body.Length <= MaxReplyBytes ? Parse(response.Body) : null;
        }
        catch (LyricSourceException)
        {
            return null;
        }
    }

    /// <summary>The songs of an iTunes Search reply; null when it is not one.</summary>
    public static IReadOnlyList<CatalogueSong>? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var reply = JsonSerializer.Deserialize(json, ItunesJsonContext.Default.ItunesReplyDto);
            return reply?.Results is { } results
                ? [.. results.Select(song => new CatalogueSong(song.TrackId, song.TrackName, song.ArtistName, song.CollectionName, song.TrackTimeMillis))]
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed class ItunesReplyDto
{
    [JsonPropertyName("results")]
    public List<ItunesSongDto>? Results { get; set; }
}

internal sealed class ItunesSongDto
{
    [JsonPropertyName("trackId")]
    public long? TrackId { get; set; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; set; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; set; }

    [JsonPropertyName("collectionName")]
    public string? CollectionName { get; set; }

    [JsonPropertyName("trackTimeMillis")]
    public long? TrackTimeMillis { get; set; }
}

[JsonSerializable(typeof(ItunesReplyDto))]
internal sealed partial class ItunesJsonContext : JsonSerializerContext;
