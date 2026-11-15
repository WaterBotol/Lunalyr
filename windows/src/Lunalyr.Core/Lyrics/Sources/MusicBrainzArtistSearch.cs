using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// MusicBrainz's artist search over the shared <see cref="LyricHttpClient"/> (its generic User-Agent, no account, no
/// personal data): <see cref="ArtistAliasReply.SearchUri"/> with a 4 s timeout, at most 1 MB read. MusicBrainz asks
/// each client for at most one request a second; <see cref="ArtistAliases"/> paces requests to that, and the client's
/// per-host spacing applies as well. Any failure is a null reply, so it is asked again next time.
/// </summary>
public sealed class MusicBrainzArtistSearch(LyricHttpClient http) : IArtistAliasSearch
{
    private const string Service = "MusicBrainz";

    /// <summary>Replies larger than this are not read as an artist search.</summary>
    public const int MaxReplyBytes = 1_000_000;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    public async Task<ArtistAliasReply?> SearchAsync(string artist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artist);
        try
        {
            var response = await http.GetAsync(Service, ArtistAliasReply.SearchUri(artist), RequestTimeout, referer: null, cancellationToken)
                .ConfigureAwait(false);
            return response.IsOk && response.Body.Length <= MaxReplyBytes ? ArtistAliasReply.Parse(response.Body) : null;
        }
        catch (LyricSourceException)
        {
            return null;
        }
    }
}
