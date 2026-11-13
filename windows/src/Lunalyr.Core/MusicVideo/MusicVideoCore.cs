// The music-video wallpaper's decisions as plain values, ported from the Mac's MusicVideoCore.swift (Lunalyr's own code;
// `git blame` finds no line of the Verso import in it). Free of Windows APIs so every rule is tested with plain values.
// The Mac's YouTubeSearchPage (youtube.com's results page) is in YouTubeSearchPage.cs, and its ranking in YouTubeSearch.cs;
// the owner's YouTube Data API key (YouTubeDataApi.cs) is Windows' own fallback.

using System.Globalization;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.NowPlaying;

namespace Lunalyr.Core.MusicVideo;

/// <summary>Where a song's video comes from.</summary>
public abstract record MusicVideoSource
{
    private MusicVideoSource()
    {
    }

    /// <summary>A video file on this PC that Media Foundation can play (Windows: a file the user chose for the song).</summary>
    public sealed record Local(string Path) : MusicVideoSource;

    /// <summary>A YouTube video, played in the IFrame Player.</summary>
    public sealed record YouTube(string VideoId) : MusicVideoSource;

    /// <summary>Where it came from, for Settings' words.</summary>
    public string Origin => this is Local ? "your video file" : "YouTube";
}

/// <summary>The song a video is looked up for.</summary>
public sealed record MusicVideoSong(string Title, string Artist, string? Album, double Duration, string? PersistentId = null)
{
    /// <summary>Caches and offsets are keyed by the recording, as lyric offsets are (<see cref="LyricQuery.RecordingKey"/>).</summary>
    public string Key => new LyricQuery(Title, Artist, Album, Duration).RecordingKey;
}

/// <summary>A now-playing track as the music video controller sees it (the Mac's <c>NowPlayingMonitor.Track</c>).</summary>
public sealed record MusicVideoTrack(string Title, string Artist, double Duration, string? Album = null, bool IsStream = false,
    bool IsAdvert = false)
{
    /// <summary>
    /// The song looked up. Windows' own: an artist that still holds Apple Music for Windows' "Artist — Album"
    /// (<see cref="AppleMusicWindowsMetadata"/>) is split here too, whichever app reported it, when the album is missing or
    /// is the part after the dash. With the joined text the usual "Tame Impala - Lost in Yesterday (Official Video)" doesn't
    /// match (its "Tame Impala" segment isn't the joined artist, so it isn't taken off), so the search, its cache key, the
    /// picker and the pasted link all use the artist alone.
    /// </summary>
    public MusicVideoSong Song => JoinedArtist() is { } joined
        ? new(Title, joined.Artist, joined.Album, Duration)
        : new(Title, Artist, Album, Duration);

    private (string Artist, string Album)? JoinedArtist()
    {
        if (AppleMusicWindowsMetadata.Split(Artist) is not { } joined) return null;
        return string.IsNullOrWhiteSpace(Album) || string.Equals(Album.Trim(), joined.Album, StringComparison.OrdinalIgnoreCase)
            ? joined : null;
    }
}

// ---- YouTube IDs ----

public static class YouTubeVideoId
{
    /// <summary>YouTube's IDs are eleven characters of URL-safe base64.</summary>
    public static bool IsValid(string? id)
    {
        if (id is not { Length: 11 }) return false;
        foreach (var c in id)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_')) return false;
        }
        return true;
    }

    /// <summary>The video ID in a watch, short, embed, shorts or YouTube Music link.</summary>
    public static string? FromUrl(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? candidate = null;
        if (host is "youtu.be" or "www.youtu.be")
        {
            candidate = path.FirstOrDefault();
        }
        else if (host is "youtube.com" or "youtube-nocookie.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal)
                 || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal))
        {
            if (path.FirstOrDefault() == "watch") candidate = QueryValue(uri.Query, "v");
            else if (path.Length >= 2 && path[0] is "embed" or "shorts" or "v" or "live") candidate = path[1];
        }
        return candidate is not null && IsValid(candidate) ? candidate : null;
    }

    private static string? QueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = equals < 0 ? pair : pair[..equals];
            if (Uri.UnescapeDataString(key) == name) return equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..]);
        }
        return null;
    }
}

// ---- Position mapping ----

/// <summary>
/// Song position to video position. Official videos often open with a scene before the music starts; the per-song offset
/// says how far into the video the song begins (positive: the video runs ahead).
/// </summary>
public static class MusicVideoTiming
{
    /// <summary>The offsets the nudge can reach, in seconds.</summary>
    public const double OffsetMin = -300, OffsetMax = 300;

    public static double ClampedOffset(double value) => double.IsFinite(value) ? Math.Clamp(value, OffsetMin, OffsetMax) : 0;

    /// <summary>
    /// Where the video should be when the song is at <paramref name="songTime"/>, or null when that is past the video's end
    /// (or before its start): the video holds still there instead of looping or jumping.
    /// </summary>
    public static double? VideoTime(double songTime, double offset, double? videoDuration)
    {
        if (!double.IsFinite(songTime)) return null;
        var time = songTime + ClampedOffset(offset);
        if (time < 0) return null;
        if (videoDuration is { } length && double.IsFinite(length) && length > 0 && time >= length - 0.05) return null;
        return time;
    }

    /// <summary>"+12.0 s", "−0.5 s", "0.0 s".</summary>
    public static string Format(double seconds)
    {
        if (!(Math.Abs(seconds) >= 0.05)) return "0.0 s";
        var text = seconds.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + " s";
        return text.Replace('-', '−');
    }
}

/// <summary>What the sync loop does to the video on each check.</summary>
public static class MusicVideoDrift
{
    /// <summary>The video's own position and whether it is playing.</summary>
    /// <param name="Buffering">Still loading or buffering: seeking again would only restart that.</param>
    public readonly record struct Sample(double VideoTime, bool VideoPlaying, bool Buffering = false);

    /// <summary>Drift beyond this is corrected with a seek; the goal is under 0.5 s.</summary>
    public const double SeekThreshold = 0.35;

    /// <summary>A seek needs time to land; until then the old position is expected.</summary>
    public const double SeekSettle = 1.2;

    /// <summary>A held video (paused, or before its start) is only moved for a real jump.</summary>
    public const double PausedThreshold = 1.0;

    /// <param name="target">Where the video should be (<see cref="MusicVideoTiming.VideoTime"/>), null past its end or before its start.</param>
    /// <param name="shouldPlay">The song is playing and the surface is visible.</param>
    /// <param name="sinceLastSeek">Seconds since the last seek this loop issued.</param>
    /// <param name="seekLead">Added to a seek target, for players that take a moment to resume after seeking.</param>
    /// <param name="threshold">Drift sought away while playing.</param>
    public static MusicVideoAction Decide(double? target, Sample sample, bool shouldPlay, double sinceLastSeek, double seekLead = 0,
        double threshold = SeekThreshold)
    {
        if (target is not { } goal) return sample.VideoPlaying ? MusicVideoAction.Pause : MusicVideoAction.None;
        var drift = sample.VideoTime - goal;
        var settled = sinceLastSeek >= SeekSettle;
        if (!shouldPlay)
        {
            if (sample.VideoPlaying) return MusicVideoAction.Pause;
            if (settled && Math.Abs(drift) > PausedThreshold) return MusicVideoAction.Seek(goal, false);
            return MusicVideoAction.None;
        }
        if (settled && !sample.Buffering && Math.Abs(drift) > threshold) return MusicVideoAction.Seek(goal + seekLead, true);
        return sample.VideoPlaying || sample.Buffering ? MusicVideoAction.None : MusicVideoAction.Play;
    }
}

public enum MusicVideoActionKind { None, Play, Pause, Seek, Rate }

/// <summary>What the sync loop does to the video on a check (the Mac's <c>MusicVideoDrift.Action</c> and <c>MusicVideoCorrector.Action</c>).</summary>
/// <param name="Value">The seek's target or the rate.</param>
public readonly record struct MusicVideoAction(MusicVideoActionKind Kind, double Value = 0, bool ThenPlay = false)
{
    public static MusicVideoAction None => new(MusicVideoActionKind.None);
    public static MusicVideoAction Play => new(MusicVideoActionKind.Play);
    public static MusicVideoAction Pause => new(MusicVideoActionKind.Pause);
    public static MusicVideoAction Seek(double to, bool thenPlay) => new(MusicVideoActionKind.Seek, to, thenPlay);
    public static MusicVideoAction Rate(double rate) => new(MusicVideoActionKind.Rate, rate);

    public bool IsSeek => Kind == MusicVideoActionKind.Seek;

    public override string ToString() => Kind switch
    {
        MusicVideoActionKind.Seek => $"Seek({Value:0.###}, {ThenPlay})",
        MusicVideoActionKind.Rate => $"Rate({Value:0.###})",
        _ => Kind.ToString(),
    };
}

// ---- Duration estimate ----

public static class MusicVideoDurationEstimate
{
    /// <summary>A video this much longer than the song (seconds) probably opens with that much intro.</summary>
    public const double RangeMin = 2, RangeMax = 40;

    /// <summary>The guessed intro, or null when the lengths say nothing useful.</summary>
    public static double? Intro(double? videoDuration, double songDuration)
    {
        if (videoDuration is not { } video || !double.IsFinite(video) || !double.IsFinite(songDuration) || songDuration <= 0) return null;
        var difference = video - songDuration;
        if (difference is < RangeMin or > RangeMax) return null;
        return Math.Round(difference * 10, MidpointRounding.AwayFromZero) / 10;
    }
}

// ---- Rate limiting ----

/// <summary>
/// Spaces the online lookups so YouTube is never hammered: two seconds apart, eight a minute at most. A 429 backs off for as
/// long as the reply asks, or a minute. Windows adds a long back-off for the Data API's daily quota (<see cref="BackOff"/>
/// accepts up to <see cref="LongestBackOff"/>).
/// </summary>
public sealed class MusicVideoRateLimit
{
    private readonly List<double> _sent = [];

    public MusicVideoRateLimit(double minimumGap = 2, double window = 60, int maximumPerWindow = 8, double defaultBackOff = 60)
    {
        MinimumGap = minimumGap;
        Window = window;
        MaximumPerWindow = maximumPerWindow;
        DefaultBackOff = defaultBackOff;
    }

    public double MinimumGap { get; }
    public double Window { get; }
    public int MaximumPerWindow { get; }
    public double DefaultBackOff { get; }

    /// <summary>The Mac caps a Retry-After at an hour; the Data API's daily quota needs up to a day.</summary>
    public double LongestBackOff { get; init; } = 3600;

    public IReadOnlyList<double> Sent => _sent;
    public double BlockedUntil { get; private set; }

    /// <summary>Seconds to wait before the next request may go, 0 for now.</summary>
    public double Delay(double now)
    {
        var wait = Math.Max(0, BlockedUntil - now);
        if (_sent.Count > 0) wait = Math.Max(wait, _sent[^1] + MinimumGap - now);
        var recent = _sent.Where(t => t > now - Window).ToList();
        if (recent.Count >= MaximumPerWindow) wait = Math.Max(wait, recent[0] + Window - now);
        return Math.Max(0, wait);
    }

    public void Record(double now)
    {
        _sent.RemoveAll(t => t <= now - Window);
        _sent.Add(now);
    }

    /// <summary>A 429 or 503: no request until <paramref name="retryAfter"/> (or the default) has passed.</summary>
    public void BackOff(double now, double? retryAfter)
    {
        var seconds = retryAfter is { } r && double.IsFinite(r) && r > 0 ? Math.Min(r, LongestBackOff) : DefaultBackOff;
        BlockedUntil = Math.Max(BlockedUntil, now + seconds);
    }
}

// ---- Resolution ----

/// <summary>Why a song has no video on screen.</summary>
public enum MusicVideoMiss
{
    /// <summary>No video file for the song, and online lookups are off (the Mac: not in the Music library).</summary>
    NotInLibrary,
    /// <summary>The song's video file is copy-protected, and online lookups are off.</summary>
    ProtectedInLibrary,
    /// <summary>Looked up online and nothing official was found.</summary>
    NotFound,
    /// <summary>YouTube refused to play the video outside its own site.</summary>
    NotEmbeddable,
    /// <summary>Too many lookups for now; tried again later.</summary>
    RateLimited,
    /// <summary>The lookup failed (offline, or a service did not answer).</summary>
    LookupFailed,
    /// <summary>YouTube's player did not load or stopped. Passing: never cached, tried again later.</summary>
    PlayerUnreachable,
    /// <summary>The song's own video file would not play here.</summary>
    LocalFileFailed,
    /// <summary>Live radio, adverts and streams have no video.</summary>
    NotASong,
    /// <summary>Windows: "Use my API key" is chosen (or YouTube's site can't be asked this run) but no YouTube Data API key is set.</summary>
    NoApiKey,
    /// <summary>Windows: the YouTube Data API refused the key (invalid, or the API not enabled for it).</summary>
    KeyRejected,
    /// <summary>Windows: the key's daily YouTube Data API quota is used up; tried again once it resets.</summary>
    QuotaExceeded,
}

/// <summary>How a player that could not show a video failed.</summary>
public enum MusicVideoPlaybackFailure
{
    /// <summary>YouTube refused the video for good here (not embeddable, removed, a bad ID): remembered for the song.</summary>
    Refused,
    /// <summary>Something passing (offline, a player error, no answer in time): not remembered, tried again later.</summary>
    Transient,
}

public abstract record MusicVideoResolution
{
    private MusicVideoResolution()
    {
    }

    public sealed record Found(MusicVideoSource Source) : MusicVideoResolution;

    public sealed record Missing(MusicVideoMiss Miss) : MusicVideoResolution;

    /// <summary>The video found, or null for a miss.</summary>
    public MusicVideoSource? Video => (this as Found)?.Source;

    /// <summary>Whether the answer may be stored: transient failures are retried.</summary>
    public bool IsCacheable => this switch
    {
        Found { Source: MusicVideoSource.YouTube } => true,
        Found => false,
        Missing m => m.Miss is MusicVideoMiss.NotFound or MusicVideoMiss.NotEmbeddable,
        _ => false,
    };

    public static MusicVideoResolution Of(MusicVideoSource source) => new Found(source);

    public static MusicVideoResolution Of(MusicVideoMiss miss) => new Missing(miss);
}
