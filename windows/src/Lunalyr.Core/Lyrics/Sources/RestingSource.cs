namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// A source that failed twice in a row rests for a while (Windows' own; the lyric-sourcing evaluation of 15 Nov 2026
/// found QQ Music answering HTTP 500 to every request and holding each lookup open for about 6 s). While it rests it is
/// not asked: it reports at once that it is resting, so the lookup is not kept waiting for it. The rest starts at 2
/// minutes and doubles with each further failure, up to 15; any answer (lyrics or "not found") ends it. Offline and
/// set-up failures don't count: they are not the service's fault.
/// </summary>
public sealed class RestingSource(ILyricSource inner, TimeProvider? time = null) : ILyricSource
{
    public const int FailuresBeforeRest = 2;
    public static readonly TimeSpan FirstRest = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan LongestRest = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private int _failures;
    private TimeSpan _rest = FirstRest;
    private DateTimeOffset _restingUntil = DateTimeOffset.MinValue;

    public ILyricSource Inner => inner;

    public LyricSourceId Id => inner.Id;

    public LyricSourceAvailability Availability => inner.Availability;

    /// <summary>Until when the source rests, or null when it is asked as usual.</summary>
    public DateTimeOffset? RestingUntil
    {
        get
        {
            lock (_gate) return _restingUntil > _time.GetUtcNow() ? _restingUntil : null;
        }
    }

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        if (RestingUntil is { } until)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((until - _time.GetUtcNow()).TotalMinutes));
            throw new LyricSourceException(LyricSourceFailure.Unreachable,
                $"Resting after repeated failures; asked again in {minutes} min");
        }
        try
        {
            var found = await inner.FetchAsync(query, ct).ConfigureAwait(false);
            Answered();
            return found;
        }
        catch (LyricSourceException exception) when (Counts(exception.Failure, exception.StatusCode))
        {
            Failed();
            throw;
        }
    }

    private static bool Counts(LyricSourceFailure failure, int? status) => failure switch
    {
        LyricSourceFailure.HttpStatus => status is null or >= 500 or 429,
        LyricSourceFailure.TimedOut or LyricSourceFailure.Unreachable or LyricSourceFailure.BadResponse => true,
        _ => false,
    };

    private void Answered()
    {
        lock (_gate)
        {
            _failures = 0;
            _rest = FirstRest;
        }
    }

    private void Failed()
    {
        lock (_gate)
        {
            if (++_failures < FailuresBeforeRest) return;
            _restingUntil = _time.GetUtcNow() + _rest;
            _rest = TimeSpan.FromTicks(Math.Min(LongestRest.Ticks, _rest.Ticks * 2));
            _failures = 0;
        }
    }
}
