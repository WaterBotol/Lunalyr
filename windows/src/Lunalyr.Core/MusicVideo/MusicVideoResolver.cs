// Ported from the Mac's MusicVideoResolver.swift (MusicVideoResolver; Lunalyr's own code): YouTube is searched the Mac's
// way, by reading its own results page (YouTubeSearchPage), with the Mac's rate limit and failure rules. Windows' own:
// - The guardrail: a page that isn't results (consent, bot check, sign-in, captcha, a redirect, no ytInitialData) is
//   "unavailable" and is never worked around. Nothing is retried, and the page isn't asked again for PageStandDown.
// - The owner's YouTube Data API key (YouTubeDataApi): the fallback when the page fails or is unavailable, and the only
//   search under "Use my API key" (MusicVideoSearchMode.ApiKey), where a missing key is NoApiKey, a refused key
//   KeyRejected and a used-up quota QuotaExceeded. It has its own rate limit, so a quota back-off never stops the page.
// - The Mac's still-image check (it fetches YouTube's numbered stills) is not ported: the best official candidate is used
//   as it is, and a pasted still-image upload gets no note (the Mac's pastedStillNote).
// - The library is a file the user chose for the song (MusicVideoFiles), not Apple Music's AppleScript library.

using System.Globalization;

namespace Lunalyr.Core.MusicVideo;

/// <summary>Videos on this PC for a song (Windows: the file the user chose for it).</summary>
public interface IMusicVideoLibrary
{
    Task<string?> LocalVideoAsync(MusicVideoSong song, CancellationToken ct);
}

public enum MusicVideoPlayability
{
    Playable,
    Protected,
    Unplayable,
}

/// <summary>Whether Media Foundation can play a local file (and it isn't copy-protected).</summary>
public interface IMusicVideoPlayabilityCheck
{
    Task<MusicVideoPlayability> CheckAsync(string path, CancellationToken ct);
}

/// <summary>How YouTube is searched (Settings › Music video; Windows' own, as the Mac has only the page).</summary>
public enum MusicVideoSearchMode
{
    /// <summary>"Search YouTube’s site (no key)": the results page, as on the Mac; the key, if any, when it isn't available.</summary>
    Site,
    /// <summary>"Use my API key": the YouTube Data API only.</summary>
    ApiKey,
}

/// <summary>What a Search YouTube… search found.</summary>
public abstract record MusicVideoSearchOutcome
{
    private MusicVideoSearchOutcome()
    {
    }

    /// <summary>Video results, in YouTube's order (possibly none).</summary>
    public sealed record Results(IReadOnlyList<YouTubeVideoResult> Videos) : MusicVideoSearchOutcome;

    /// <summary>Too many searches for now (<see cref="MusicVideoRateLimit"/>), or the key's quota is used up.</summary>
    public sealed record RateLimited : MusicVideoSearchOutcome;

    /// <summary>Offline, or YouTube's page could not be read.</summary>
    /// <param name="Unavailable">Windows: YouTube answered with something other than results (a consent, bot-check or sign-in page).</param>
    public sealed record Failed(bool Unavailable = false) : MusicVideoSearchOutcome;

    /// <summary>Replaced by a newer search, or the window closed.</summary>
    public sealed record Cancelled : MusicVideoSearchOutcome;

    /// <summary>Windows: "Use my API key" with no key, or the key was refused.</summary>
    public sealed record NeedsKey(bool Rejected) : MusicVideoSearchOutcome;
}

/// <summary>
/// Finds a song's video, in order: the song's own video file (when it can be played here), then a YouTube link the user
/// pasted for the song, then, only with online search on, the cached answer, then YouTube's own search results page (or
/// the owner's Data API key, as a fallback or by choice).
/// </summary>
public sealed class MusicVideoResolver
{
    private readonly IMusicVideoLibrary _library;
    private readonly IMusicVideoPlayabilityCheck _playability;
    private readonly IMusicVideoHttp? _page;
    private readonly YouTubeDataApi? _api;
    private readonly Func<MusicVideoSearchMode> _mode;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<double> _uptime;
    private readonly Func<double, CancellationToken, Task> _sleep;
    private readonly Func<byte[], Task<IReadOnlyList<YouTubeVideoResult>>> _parse;
    private readonly Lock _gate = new();

    /// <summary>Videos YouTube's player refused for each song this session; the song's next search skips them.</summary>
    private readonly Dictionary<string, HashSet<string>> _refused = new(StringComparer.Ordinal);

    /// <summary>Until when (uptime) the results page is not asked, after it came back unavailable.</summary>
    private double _pageUnavailableUntil = double.NegativeInfinity;

    /// <summary>Refused videos per song before "can't be embedded" is remembered.</summary>
    public const int RefusalsTried = 3;

    /// <summary>Windows: after an unavailable page (a wall), seconds before the page is asked again. Nothing is retried sooner.</summary>
    public const double PageStandDown = 900;

    /// <param name="api">The owner's Data API client, or null (never built under --demo or --no-network).</param>
    /// <param name="page">GETs for YouTube's results page, or null for no page search (--demo, --no-network).</param>
    /// <param name="searchMode">The owner's choice; the page (<see cref="MusicVideoSearchMode.Site"/>) when null.</param>
    /// <param name="parse">Reads a results page off the caller's thread; a seam for tests.</param>
    public MusicVideoResolver(IMusicVideoLibrary library, IMusicVideoPlayabilityCheck playability, YouTubeDataApi? api,
        MusicVideoCache cache, MusicVideoLinks links, MusicVideoRateLimit? rateLimit = null, Func<DateTimeOffset>? now = null,
        Func<double>? uptime = null, Func<double, CancellationToken, Task>? sleep = null, IMusicVideoHttp? page = null,
        Func<MusicVideoSearchMode>? searchMode = null, Func<byte[], Task<IReadOnlyList<YouTubeVideoResult>>>? parse = null,
        MusicVideoRateLimit? apiRateLimit = null)
    {
        _library = library;
        _playability = playability;
        _api = api;
        _page = page;
        _mode = searchMode ?? (() => MusicVideoSearchMode.Site);
        Cache = cache;
        Links = links;
        RateLimit = rateLimit ?? new MusicVideoRateLimit();
        ApiRateLimit = apiRateLimit ?? new MusicVideoRateLimit { LongestBackOff = 86_400 };
        _now = now ?? (() => DateTimeOffset.UtcNow);
        var started = System.Diagnostics.Stopwatch.StartNew();
        _uptime = uptime ?? (() => started.Elapsed.TotalSeconds);
        _sleep = sleep ?? ((seconds, ct) => Task.Delay(TimeSpan.FromSeconds(seconds), ct));
        _parse = parse ?? (body => YouTubeSearchPage.ResultsOffThreadAsync(body));
    }

    public MusicVideoCache Cache { get; }
    public MusicVideoLinks Links { get; }

    /// <summary>The results page's rate limit, the Mac's: 2 s apart, 8 a minute, a 429 backs off as asked (an hour at most) or a minute.</summary>
    public MusicVideoRateLimit RateLimit { get; }

    /// <summary>Windows: the Data API's, the same spacing, with a back-off of up to a day for a used-up quota.</summary>
    public MusicVideoRateLimit ApiRateLimit { get; }

    public MusicVideoSearchMode Mode => _mode();

    private bool HasKey => _api?.HasKey == true;

    /// <summary>Whether YouTube can be searched at all: the page (unless the owner chose the key), or a key.</summary>
    public bool CanSearch => HasKey || (Mode == MusicVideoSearchMode.Site && _page is not null);

    /// <summary>Whether the results page came back unavailable lately (Windows' guardrail), so isn't being asked.</summary>
    public bool PageUnavailable
    {
        get
        {
            lock (_gate) return _uptime() < _pageUnavailableUntil;
        }
    }

    /// <summary>Waits longer than this are not sat out: the lookup reports RateLimited and is tried again later.</summary>
    public double LongestWait { get; set; } = 8;

    public async Task<MusicVideoResolution> ResolveAsync(MusicVideoSong song, bool online, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(song.Title)) return MusicVideoResolution.Of(MusicVideoMiss.NotASong);
        var libraryMiss = MusicVideoMiss.NotInLibrary;
        if (await _library.LocalVideoAsync(song, ct).ConfigureAwait(false) is { } path)
        {
            switch (await _playability.CheckAsync(path, ct).ConfigureAwait(false))
            {
                case MusicVideoPlayability.Playable:
                    return MusicVideoResolution.Of(new MusicVideoSource.Local(path));
                case MusicVideoPlayability.Protected:
                    libraryMiss = MusicVideoMiss.ProtectedInLibrary;
                    break;
            }
        }
        var key = song.Key;
        if (Links.VideoId(key) is { } pasted) return MusicVideoResolution.Of(new MusicVideoSource.YouTube(pasted));
        if (!online) return MusicVideoResolution.Of(libraryMiss);
        if (Cache.Lookup(key, _now()) is { } cached) return cached;
        if (!CanSearch) return MusicVideoResolution.Of(MusicVideoMiss.NoApiKey);
        // Skipped past while the file was checked: nothing is sent.
        if (ct.IsCancellationRequested) return MusicVideoResolution.Of(MusicVideoMiss.LookupFailed);
        var result = await SearchYouTubeAsync(song, ct).ConfigureAwait(false);
        Cache.Store(result, key, _now());
        return result;
    }

    /// <summary>
    /// The player found the video cannot be embedded. With <paramref name="videoId"/> (a found video, online lookups on),
    /// the song's next candidate is tried: true means look the song up again. Otherwise, or once
    /// <see cref="RefusalsTried"/> videos were refused, "can't be embedded" is remembered. A pasted link never comes here.
    /// </summary>
    public bool MarkNotEmbeddable(MusicVideoSong song, string? videoId = null)
    {
        var key = song.Key;
        if (videoId is not null)
        {
            lock (_gate)
            {
                if (_refused.Count > 200) _refused.Clear();
                if (!_refused.TryGetValue(key, out var set)) _refused[key] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(videoId);
                if (set.Count < RefusalsTried)
                {
                    Cache.Remove(key);
                    return true;
                }
            }
        }
        Cache.Store(MusicVideoResolution.Of(MusicVideoMiss.NotEmbeddable), key, _now());
        return false;
    }

    private enum FetchFailure { None, RateLimited, Failed, Unavailable, Cancelled, Quota, Key }

    /// <summary>
    /// YouTube's results for "&lt;artist&gt; &lt;title&gt; official music video". A page with no results at all is taken as
    /// YouTube having changed its format (or asking for consent), not as "no video": it is not cached. The best official
    /// candidate not refused this session is the answer.
    /// </summary>
    private async Task<MusicVideoResolution> SearchYouTubeAsync(MusicVideoSong song, CancellationToken ct)
    {
        var (results, failure) = await SearchAsync(YouTubeSearch.Query(song.Title, song.Artist), automatic: true, ct).ConfigureAwait(false);
        switch (failure)
        {
            case FetchFailure.None:
                break;
            case FetchFailure.RateLimited:
                return MusicVideoResolution.Of(MusicVideoMiss.RateLimited);
            case FetchFailure.Quota:
                return MusicVideoResolution.Of(MusicVideoMiss.QuotaExceeded);
            case FetchFailure.Key:
                return MusicVideoResolution.Of(MusicVideoMiss.KeyRejected);
            default:
                return MusicVideoResolution.Of(MusicVideoMiss.LookupFailed);
        }
        var found = YouTubeSearch.OfficialVideos(results!, song.Title, song.Artist, song.Duration);
        HashSet<string> skipped;
        lock (_gate) skipped = _refused.TryGetValue(song.Key, out var set) ? [.. set] : [];
        var candidates = found.Where(v => !skipped.Contains(v.VideoId)).ToList();
        // Every official video was refused by the player already.
        if (candidates.Count == 0 && found.Count > 0) return MusicVideoResolution.Of(MusicVideoMiss.NotEmbeddable);
        return candidates.Count > 0
            ? MusicVideoResolution.Of(new MusicVideoSource.YouTube(candidates[0].VideoId))
            : MusicVideoResolution.Of(MusicVideoMiss.NotFound);
    }

    /// <summary>
    /// Search YouTube…: the results for what the user typed, in YouTube's order, with the same rate limit as the automatic
    /// lookup and nothing cached. A page with no results data at all (YouTube changed it, or asks for consent) is Failed,
    /// not "no videos".
    /// </summary>
    public async Task<MusicVideoSearchOutcome> SearchResultsAsync(string query, CancellationToken ct = default)
    {
        var text = query.Trim();
        if (text.Length == 0) return new MusicVideoSearchOutcome.Failed();
        if (!CanSearch) return new MusicVideoSearchOutcome.NeedsKey(false);
        var (results, failure) = await SearchAsync(text, automatic: false, ct).ConfigureAwait(false);
        return failure switch
        {
            FetchFailure.None => new MusicVideoSearchOutcome.Results(results!),
            FetchFailure.RateLimited or FetchFailure.Quota => new MusicVideoSearchOutcome.RateLimited(),
            FetchFailure.Key => new MusicVideoSearchOutcome.NeedsKey(true),
            FetchFailure.Cancelled => new MusicVideoSearchOutcome.Cancelled(),
            FetchFailure.Unavailable => new MusicVideoSearchOutcome.Failed(Unavailable: true),
            _ => ct.IsCancellationRequested ? new MusicVideoSearchOutcome.Cancelled() : new MusicVideoSearchOutcome.Failed(),
        };
    }

    /// <summary>
    /// One search, in the owner's order: the results page first (the Mac's way), then, when it fails or isn't available
    /// and a key is set, the Data API once. Under "Use my API key", the Data API only. At most one request to each.
    /// </summary>
    private async Task<(IReadOnlyList<YouTubeVideoResult>? Results, FetchFailure Failure)> SearchAsync(string query, bool automatic,
        CancellationToken ct)
    {
        if (Mode == MusicVideoSearchMode.ApiKey || _page is null) return await ApiAsync(query, ct).ConfigureAwait(false);
        var (results, failure) = await PageAsync(query, ct).ConfigureAwait(false);
        // The automatic lookup takes a page without videos as a failure (the Mac's rule); the picker shows it as none.
        if (failure == FetchFailure.None && automatic && results!.Count == 0) failure = FetchFailure.Failed;
        if (failure is FetchFailure.None or FetchFailure.Cancelled || !HasKey || ct.IsCancellationRequested) return (results, failure);
        return await ApiAsync(query, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One rate-limited GET of YouTube's results page, read off the caller's thread. A lookup cancelled before or while it
    /// waited its turn sends nothing and uses no slot, so rapid skipping cannot use up the allowance. The request carries
    /// nothing but the query (the HTTP client adds the Mac's User-Agent and Accept-Language): no key, no cookies.
    /// </summary>
    private async Task<(IReadOnlyList<YouTubeVideoResult>? Results, FetchFailure Failure)> PageAsync(string query, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return (null, FetchFailure.Cancelled);
        double wait;
        lock (_gate)
        {
            // The page was a wall lately: it isn't asked again yet.
            if (_uptime() < _pageUnavailableUntil) return (null, FetchFailure.Unavailable);
            wait = RateLimit.Delay(_uptime());
        }
        if (wait > LongestWait) return (null, FetchFailure.RateLimited);
        if (wait > 0)
        {
            try
            {
                await _sleep(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (null, FetchFailure.Cancelled);
            }
        }
        if (ct.IsCancellationRequested) return (null, FetchFailure.Cancelled);
        lock (_gate) RateLimit.Record(_uptime());
        MusicVideoHttpReply reply;
        try
        {
            reply = await _page!.GetAsync(YouTubeSearchPage.RequestUrl(query)!, new Dictionary<string, string>(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, ct.IsCancellationRequested ? FetchFailure.Cancelled : FetchFailure.Failed);
        }
        if (reply.Status is 429 or 503)
        {
            var retry = double.TryParse(reply.Header("Retry-After"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? seconds : (double?)null;
            lock (_gate) RateLimit.BackOff(_uptime(), retry);
            return (null, FetchFailure.RateLimited);
        }
        // About a megabyte of HTML and JSON: read off the caller's thread.
        var videos = reply.Status == 200 ? await _parse(reply.Body).ConfigureAwait(false) : [];
        switch (YouTubeSearchPage.Inspect(reply, videos))
        {
            case YouTubePageReply.Results page:
                return (page.Videos, FetchFailure.None);
            case YouTubePageReply.Unavailable:
                // Never worked around: no retry, no cookies, no other User-Agent. The page stands down for a while.
                lock (_gate) _pageUnavailableUntil = _uptime() + PageStandDown;
                return (null, FetchFailure.Unavailable);
            default:
                return (null, FetchFailure.Failed);
        }
    }

    /// <summary>
    /// One rate-limited Data API search (the details request rides on the same slot). A lookup cancelled before or while it
    /// waited its turn sends nothing and uses no slot.
    /// </summary>
    private async Task<(IReadOnlyList<YouTubeVideoResult>? Results, FetchFailure Failure)> ApiAsync(string query, CancellationToken ct)
    {
        if (!HasKey) return (null, FetchFailure.Key);
        if (ct.IsCancellationRequested) return (null, FetchFailure.Cancelled);
        double wait;
        lock (_gate) wait = ApiRateLimit.Delay(_uptime());
        if (wait > LongestWait) return (null, FetchFailure.RateLimited);
        if (wait > 0)
        {
            try
            {
                await _sleep(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (null, FetchFailure.Cancelled);
            }
        }
        if (ct.IsCancellationRequested) return (null, FetchFailure.Cancelled);
        lock (_gate) ApiRateLimit.Record(_uptime());
        try
        {
            var results = await _api!.SearchAsync(query, ct, () =>
            {
                // Backing off after a 429: the details request waits too.
                lock (_gate)
                {
                    if (ApiRateLimit.BlockedUntil > _uptime()) throw new YouTubeApiException(YouTubeApiFailure.RateLimited);
                }
            }).ConfigureAwait(false);
            return (results, FetchFailure.None);
        }
        catch (OperationCanceledException)
        {
            return (null, FetchFailure.Cancelled);
        }
        catch (YouTubeApiException ex)
        {
            switch (ex.Failure)
            {
                case YouTubeApiFailure.RateLimited:
                    lock (_gate) ApiRateLimit.BackOff(_uptime(), ex.RetryAfter);
                    return (null, FetchFailure.RateLimited);
                case YouTubeApiFailure.QuotaExceeded:
                    lock (_gate) ApiRateLimit.BackOff(_uptime(), YouTubeQuota.SecondsUntilReset(_now()));
                    return (null, FetchFailure.Quota);
                case YouTubeApiFailure.KeyRejected:
                    return (null, FetchFailure.Key);
                default:
                    return (null, FetchFailure.Failed);
            }
        }
    }
}

/// <summary>When the YouTube Data API's daily quota starts again: midnight Pacific Time, as Google documents it.</summary>
public static class YouTubeQuota
{
    public static double SecondsUntilReset(DateTimeOffset now)
    {
        TimeZoneInfo? pacific = null;
        try
        {
            pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Below: 08:00 UTC, Pacific Standard Time's midnight.
        }
        if (pacific is null)
        {
            var next = new DateTimeOffset(now.UtcDateTime.Date.AddHours(8), TimeSpan.Zero);
            if (next <= now) next = next.AddDays(1);
            return Math.Clamp((next - now).TotalSeconds, 60, 86_400);
        }
        var local = TimeZoneInfo.ConvertTime(now, pacific);
        var midnight = new DateTimeOffset(local.Date.AddDays(1), pacific.GetUtcOffset(local.Date.AddDays(1)));
        return Math.Clamp((midnight - now).TotalSeconds, 60, 86_400);
    }
}
