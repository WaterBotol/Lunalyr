// Finds an album's animated cover through Apple's public pages (see MotionArtwork.cs): the lookup order, the album-wide
// answer cache and the session-only memory of albums Apple answered without one are ported from Lunalyr's own lines of
// MotionArtworkProvider.motion(album:artist:title:) and progressive(_:); the request helpers are Windows' own.

using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Covers;

/// <summary>
/// Looks up animated covers. Every request is a plain HTTPS GET of a public page or playlist with Lunalyr's own generic
/// User-Agent: the iTunes search, the album's music.apple.com page and the cover's playlists. Answers are cached per
/// album (bounded, for the session); an album Apple answered without a cover is remembered for the session, but a
/// failure to reach Apple never is, so the next song tries again.
/// </summary>
public sealed class MotionArtworkFinder
{
    /// <summary>Bounded: the app runs all day, and every album played adds a key.</summary>
    public const int CacheCapacity = 256;

    /// <summary>Each request's own timeout (the Mac's 10 s).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private const string Service = "Apple Music covers";

    private readonly LyricHttpClient _http;
    private readonly string _storefront;
    private readonly CategoryLogger _log;
    private readonly BoundedMemo<MotionArtwork> _found = new(CacheCapacity);
    private readonly BoundedMemo<bool> _misses = new(CacheCapacity);

    /// <param name="http">The HTTP client (its User-Agent is Lunalyr's own; tests give one over recorded replies).</param>
    /// <param name="storefront">The Apple Music storefront to search first (two letters); the US one is searched after it. Default: Windows' region.</param>
    public MotionArtworkFinder(LyricHttpClient http, string? storefront = null, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _storefront = string.IsNullOrWhiteSpace(storefront) ? RegionStorefront() : storefront.Trim().ToLowerInvariant();
        _log = (logger ?? Logger.Null).Player;
    }

    /// <summary>The storefront searched first.</summary>
    public string Storefront => _storefront;

    private static string RegionStorefront()
    {
        try
        {
            var region = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
            return region.Length == 2 && region.All(char.IsAsciiLetter) ? region.ToLowerInvariant() : "us";
        }
        catch (ArgumentException)
        {
            return "us";
        }
    }

    /// <summary>
    /// The album's animated cover, or null when it has none (or Apple couldn't be reached). Searches the album by name
    /// (when there is one), then the song, in this storefront and then the US one, and reads the first matching album
    /// page with a cover. Each rendition is resolved to one progressive MP4 where Apple publishes one, both at once.
    /// </summary>
    public async Task<MotionArtwork?> FindAsync(string? album, string artist, string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artist);
        ArgumentNullException.ThrowIfNull(title);
        var key = MotionArtworkPages.CacheKey(album, artist, title);
        if (_found.TryGet(key, out var cached)) return cached;
        if (_misses.TryGet(key, out _)) return null;

        // Only an answer from Apple is a miss: a failure to reach it must not be remembered, even for the session.
        var reachedApple = true;
        string[] countries = _storefront == "us" ? ["us"] : [_storefront, "us"];
        foreach (var country in countries)
        {
            List<(string Entity, string Name)> queries = [("song", title)];
            if (!string.IsNullOrEmpty(album)) queries.Insert(0, ("album", album));
            foreach (var (entity, name) in queries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pages = await AlbumPagesAsync(country, entity, name, album, artist, cancellationToken).ConfigureAwait(false);
                if (pages is null)
                {
                    reachedApple = false;
                    continue;
                }
                foreach (var page in pages)
                {
                    if (await TextAsync(page, cancellationToken).ConfigureAwait(false) is not { } html)
                    {
                        reachedApple = false;
                        continue;
                    }
                    if (MotionArtworkPages.Parse(html) is { } artwork)
                    {
                        var motion = await ProgressiveAsync(artwork, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        _found.Set(key, motion);
                        return motion;
                    }
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (reachedApple) _misses.Set(key, true);
        return null;
    }

    /// <summary>
    /// The cover with each rendition resolved to one progressive MP4 where Apple publishes one. A distinct tall
    /// rendition is resolved at the same time as the square one, so the square cover never waits for it; an album with
    /// a single rendition uses it for both and fetches it once.
    /// </summary>
    private async Task<MotionArtwork> ProgressiveAsync(MotionArtwork artwork, CancellationToken ct)
    {
        if (artwork.Tall is not { } tall || tall == artwork.Square)
        {
            var single = await ProgressiveRenditionAsync(artwork.Square, ct).ConfigureAwait(false) ?? artwork.Square;
            return new MotionArtwork(single, artwork.Tall is null ? null : single);
        }
        var square = ProgressiveRenditionAsync(artwork.Square, ct);
        var tallFile = ProgressiveRenditionAsync(tall, ct);
        await Task.WhenAll(square, tallFile).ConfigureAwait(false);
        return new MotionArtwork(await square.ConfigureAwait(false) ?? artwork.Square, await tallFile.ConfigureAwait(false) ?? tall);
    }

    /// <summary>
    /// Apple's byte-range playlists often point to one complete fragmented MP4: the master itself, or one of its H.264
    /// variants nearest 768 pixels wide. That file starts at once and loops like a local one.
    /// </summary>
    private async Task<Uri?> ProgressiveRenditionAsync(Uri master, CancellationToken ct)
    {
        if (await TextAsync(master, ct).ConfigureAwait(false) is not { } playlist) return null;
        if (MotionArtworkPages.SingleMp4(playlist, master) is { } direct) return direct;
        foreach (var variant in MotionArtworkPages.Variants(playlist, master))
        {
            if (await TextAsync(variant, ct).ConfigureAwait(false) is { } text && MotionArtworkPages.SingleMp4(text, variant) is { } file)
                return file;
        }
        return null;
    }

    /// <summary>The matching album pages in one search, or null when the search could not be completed (as opposed to an empty answer).</summary>
    private async Task<IReadOnlyList<Uri>?> AlbumPagesAsync(string country, string entity, string name, string? album, string artist,
        CancellationToken ct)
    {
        var json = await TextAsync(MotionArtworkPages.SearchUri(country, entity, artist, name), ct).ConfigureAwait(false);
        if (json is null || MotionArtworkPages.SearchResults(json) is not { } results) return null;
        return MotionArtworkPages.MatchingPages(results, album, artist, entity == "song" ? name : null);
    }

    /// <summary>The body of an HTTP 200 reply as text, or null for any other status or a failed request.</summary>
    private async Task<string?> TextAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync(Service, uri, RequestTimeout, null, ct).ConfigureAwait(false);
            if (response.IsOk) return response.Text;
            _log.Debug($"Animated covers: {uri.Host} answered {response.StatusCode}.");
            return null;
        }
        catch (LyricSourceException ex)
        {
            ct.ThrowIfCancellationRequested();
            _log.Debug($"Animated covers: {uri.Host} couldn't be reached ({ex.Message}).");
            return null;
        }
    }

    /// <summary>A small first-in, first-out memo: past its capacity the oldest key goes.</summary>
    private sealed class BoundedMemo<T>(int capacity)
    {
        private readonly Dictionary<string, T> _values = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();
        private readonly Lock _gate = new();

        public bool TryGet(string key, out T value)
        {
            lock (_gate) return _values.TryGetValue(key, out value!);
        }

        public void Set(string key, T value)
        {
            lock (_gate)
            {
                if (_values.TryAdd(key, value)) _order.Enqueue(key);
                else _values[key] = value;
                while (_order.Count > capacity) _values.Remove(_order.Dequeue());
            }
        }
    }
}
