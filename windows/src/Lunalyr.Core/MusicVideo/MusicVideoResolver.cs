// Ported from the Mac's MusicVideoResolver.swift (MusicVideoResolver; Lunalyr's own code). Differences, all because the
// Mac's means aren't official ones:
// - YouTube is searched through the YouTube Data API with the owner's key (YouTubeDataApi), not youtube.com's page;
//   without a key Search YouTube reports NoApiKey, a refused key KeyRejected and a used-up quota QuotaExceeded.
// - The Mac's still-image check (it fetches YouTube's numbered stills, an undocumented address) is not ported: the best
//   official candidate is used as it is, and a pasted still-image upload gets no note (the Mac's pastedStillNote).
// - The library is a file the user chose for the song (MusicVideoFiles), not Apple Music's AppleScript library.

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

    /// <summary>Offline, or the reply couldn't be read.</summary>
    public sealed record Failed : MusicVideoSearchOutcome;

    /// <summary>Replaced by a newer search, or the window closed.</summary>
    public sealed record Cancelled : MusicVideoSearchOutcome;

    /// <summary>Windows: no YouTube Data API key, or the key was refused.</summary>
    public sealed record NeedsKey(bool Rejected) : MusicVideoSearchOutcome;
}

/// <summary>
/// Finds a song's video, in order: the song's own video file (when it can be played here), then a YouTube link the user
/// pasted for the song, then, only with online search on, the cached answer, then a YouTube Data API search.
/// </summary>
public sealed class MusicVideoResolver
{
    private readonly IMusicVideoLibrary _library;
    private readonly IMusicVideoPlayabilityCheck _playability;
    private readonly YouTubeDataApi? _api;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<double> _uptime;
    private readonly Func<double, CancellationToken, Task> _sleep;
    private readonly Lock _gate = new();

    /// <summary>Videos YouTube's player refused for each song this session; the song's next search skips them.</summary>
    private readonly Dictionary<string, HashSet<string>> _refused = new(StringComparer.Ordinal);

    /// <summary>Refused videos per song before "can't be embedded" is remembered.</summary>
    public const int RefusalsTried = 3;

    public MusicVideoResolver(IMusicVideoLibrary library, IMusicVideoPlayabilityCheck playability, YouTubeDataApi? api,
        MusicVideoCache cache, MusicVideoLinks links, MusicVideoRateLimit? rateLimit = null, Func<DateTimeOffset>? now = null,
        Func<double>? uptime = null, Func<double, CancellationToken, Task>? sleep = null)
    {
        _library = library;
        _playability = playability;
        _api = api;
        Cache = cache;
        Links = links;
        RateLimit = rateLimit ?? new MusicVideoRateLimit { LongestBackOff = 86_400 };
        _now = now ?? (() => DateTimeOffset.UtcNow);
        var started = System.Diagnostics.Stopwatch.StartNew();
        _uptime = uptime ?? (() => started.Elapsed.TotalSeconds);
        _sleep = sleep ?? ((seconds, ct) => Task.Delay(TimeSpan.FromSeconds(seconds), ct));
    }

    public MusicVideoCache Cache { get; }
    public MusicVideoLinks Links { get; }
    public MusicVideoRateLimit RateLimit { get; }

    /// <summary>Whether YouTube can be searched at all (a Data API client with a key).</summary>
    public bool CanSearch => _api?.HasKey == true;

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

    private enum FetchFailure { None, RateLimited, Failed, Quota, Key }

    /// <summary>
    /// The Data API's results for "&lt;artist&gt; &lt;title&gt; official music video". A reply that can't be read is a
    /// failure, not "no video": it is not cached. The best official candidate not refused this session is the answer.
    /// </summary>
    private async Task<MusicVideoResolution> SearchYouTubeAsync(MusicVideoSong song, CancellationToken ct)
    {
        var (results, failure) = await FetchAsync(YouTubeSearch.Query(song.Title, song.Artist), ct).ConfigureAwait(false);
        switch (failure)
        {
            case FetchFailure.RateLimited: return MusicVideoResolution.Of(MusicVideoMiss.RateLimited);
            case FetchFailure.Quota: return MusicVideoResolution.Of(MusicVideoMiss.QuotaExceeded);
            case FetchFailure.Key: return MusicVideoResolution.Of(MusicVideoMiss.KeyRejected);
            case FetchFailure.Failed: return MusicVideoResolution.Of(MusicVideoMiss.LookupFailed);
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
    /// lookup and nothing cached.
    /// </summary>
    public async Task<MusicVideoSearchOutcome> SearchResultsAsync(string query, CancellationToken ct = default)
    {
        var text = query.Trim();
        if (text.Length == 0) return new MusicVideoSearchOutcome.Failed();
        if (!CanSearch) return new MusicVideoSearchOutcome.NeedsKey(false);
        var (results, failure) = await FetchAsync(text, ct).ConfigureAwait(false);
        return failure switch
        {
            FetchFailure.None => new MusicVideoSearchOutcome.Results(results!),
            FetchFailure.RateLimited or FetchFailure.Quota => new MusicVideoSearchOutcome.RateLimited(),
            FetchFailure.Key => new MusicVideoSearchOutcome.NeedsKey(true),
            _ => ct.IsCancellationRequested ? new MusicVideoSearchOutcome.Cancelled() : new MusicVideoSearchOutcome.Failed(),
        };
    }

    /// <summary>
    /// One rate-limited search (the details request rides on the same slot). A lookup cancelled before or while it waited
    /// its turn sends nothing and uses no slot, so rapid skipping cannot use up the allowance.
    /// </summary>
    private async Task<(IReadOnlyList<YouTubeVideoResult>? Results, FetchFailure Failure)> FetchAsync(string query, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return (null, FetchFailure.Failed);
        double wait;
        lock (_gate) wait = RateLimit.Delay(_uptime());
        if (wait > LongestWait) return (null, FetchFailure.RateLimited);
        if (wait > 0)
        {
            try
            {
                await _sleep(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (null, FetchFailure.Failed);
            }
        }
        if (ct.IsCancellationRequested) return (null, FetchFailure.Failed);
        lock (_gate) RateLimit.Record(_uptime());
        try
        {
            var results = await _api!.SearchAsync(query, ct, () =>
            {
                // Backing off after a 429: the details request waits too.
                lock (_gate)
                {
                    if (RateLimit.BlockedUntil > _uptime()) throw new YouTubeApiException(YouTubeApiFailure.RateLimited);
                }
            }).ConfigureAwait(false);
            return (results, FetchFailure.None);
        }
        catch (OperationCanceledException)
        {
            return (null, FetchFailure.Failed);
        }
        catch (YouTubeApiException ex)
        {
            switch (ex.Failure)
            {
                case YouTubeApiFailure.RateLimited:
                    lock (_gate) RateLimit.BackOff(_uptime(), ex.RetryAfter);
                    return (null, FetchFailure.RateLimited);
                case YouTubeApiFailure.QuotaExceeded:
                    lock (_gate) RateLimit.BackOff(_uptime(), YouTubeQuota.SecondsUntilReset(_now()));
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
