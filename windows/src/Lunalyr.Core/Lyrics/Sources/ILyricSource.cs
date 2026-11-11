namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// One place lyrics come from: a lyric service over HTTP or a folder on this PC. A source asks once and reports what it
/// found, unparsed: the payload in its <see cref="LyricFormat"/> plus what the service said it matched (title, artist,
/// length). Parsing the payload into lines, ranking sources against each other, caching and the overall per-source time
/// limit belong to the lookup that calls the sources.
/// </summary>
/// <remarks>
/// <para>A fetch ends in exactly one of three ways:</para>
/// <list type="bullet">
/// <item><description>A <see cref="RawLyrics"/>: the best of the source's own hits for the query, already pre-filtered on
/// title, artist and length the way the Mac app's provider does it.</description></item>
/// <item><description><c>null</c>: the source answered and has nothing usable (no hit, no hit that fits, an
/// instrumental recording, an empty lyric). A lookup may remember this as "no lyrics" for a while.</description></item>
/// <item><description>A <see cref="LyricSourceException"/>: an outage, not an answer (HTTP error, unreadable reply,
/// request timeout, no connection, folder not set). A lookup should ask again next time.</description></item>
/// </list>
/// <para><see cref="OperationCanceledException"/> is thrown only when the caller's token is cancelled. A request that
/// runs past the source's own timeout is a <see cref="LyricSourceException"/> with
/// <see cref="LyricSourceFailure.TimedOut"/>, never a cancellation.</para>
/// <para>Sources are thread-safe and meant to live for the app's lifetime (some remember mirror health).</para>
/// </remarks>
public interface ILyricSource
{
    LyricSourceId Id { get; }

    /// <summary>Whether the source can be asked on this PC at all, and why not. Most sources are always available.</summary>
    LyricSourceAvailability Availability => LyricSourceAvailability.Available;

    Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct);
}

/// <summary>Whether a source can work on this PC; <see cref="Reason"/> says why not, for the source list.</summary>
public sealed record LyricSourceAvailability(bool IsAvailable, string? Reason = null)
{
    public static LyricSourceAvailability Available { get; } = new(true);

    public static LyricSourceAvailability Unavailable(string reason) => new(false, reason);
}

/// <summary>What kind of failure a <see cref="LyricSourceException"/> reports.</summary>
public enum LyricSourceFailure
{
    /// <summary>The service answered with an HTTP error status.</summary>
    HttpStatus,
    /// <summary>The reply could not be read (not JSON, wrong shape, too large).</summary>
    BadResponse,
    /// <summary>A request ran past the source's timeout.</summary>
    TimedOut,
    /// <summary>The service could not be reached (connection refused, DNS, TLS).</summary>
    Unreachable,
    /// <summary>This PC has no network connection.</summary>
    Offline,
    /// <summary>The source needs setting up first (a local folder to read).</summary>
    NotConfigured,
    /// <summary>A local folder could not be read.</summary>
    LocalFiles,
    /// <summary>The service asked us to slow down for longer than a lookup should wait.</summary>
    Busy,
}

/// <summary>A source failed rather than answered. <see cref="Exception.Message"/> is short and fit for the source list.</summary>
public sealed class LyricSourceException : Exception
{
    public LyricSourceException(LyricSourceFailure failure, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        StatusCode = statusCode;
    }

    public LyricSourceFailure Failure { get; }

    /// <summary>The HTTP status, for <see cref="LyricSourceFailure.HttpStatus"/>.</summary>
    public int? StatusCode { get; }

    /// <summary>True when the PC looked offline: nothing should be remembered as a miss.</summary>
    public bool IsOffline => Failure == LyricSourceFailure.Offline;
}
