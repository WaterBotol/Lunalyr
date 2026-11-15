namespace Lunalyr.Core.NowPlaying;

/// <summary>What the song clock says right now. Cheap to get: no allocation.</summary>
/// <param name="Position">Position in the song to show, from its start. Never negative, never past <paramref name="Duration"/>.</param>
/// <param name="Duration">Song length, or null when the player doesn't report one.</param>
/// <param name="IsPlaying">True while the player reports Playing; the position is frozen otherwise.</param>
/// <param name="Rate">Effective playback rate used for extrapolation (1.0 when the player reports none).</param>
/// <param name="Quality">How the player keeps its timeline up to date.</param>
/// <param name="Confidence">0–1: how much to trust <paramref name="Position"/> (0 = no timeline at all).</param>
/// <param name="HasTimeline">True once a timeline snapshot for the current track has been taken in.</param>
public readonly record struct SongClockReading(
    TimeSpan Position,
    TimeSpan? Duration,
    bool IsPlaying,
    double Rate,
    TimelineQuality Quality,
    double Confidence,
    bool HasTimeline);

/// <summary>Raised when a timeline snapshot shows the listener jumped within the same track.</summary>
public sealed class SeekDetectedEventArgs(TimeSpan from, TimeSpan to) : EventArgs
{
    /// <summary>The position the clock was showing just before the seek.</summary>
    public TimeSpan From { get; } = from;

    /// <summary>The new position.</summary>
    public TimeSpan To { get; } = to;

    public bool IsBackward => To < From;
}

/// <summary>
/// Turns the player's sparse, late timeline snapshots into a smooth song position for word-timed lyrics.
/// </summary>
/// <remarks>
/// <para>
/// Feed it with <see cref="UpdateTimeline"/> (GSMTC timeline properties), <see cref="UpdatePlayback(PlaybackStatus,
/// double?)"/> (status and rate), <see cref="ResetTrack"/> on a track change and <see cref="ResetSession"/> when the
/// followed player changes. Read it every frame with <see cref="Read"/>.
/// </para>
/// <para>
/// Between snapshots the position is extrapolated from the snapshot's LastUpdatedTime at the playback rate, and
/// frozen while paused. A snapshot that disagrees by more than <see cref="SongClockOptions.SeekThreshold"/> is a seek
/// (jump + <see cref="SeekDetected"/>); smaller disagreements are drift and are slewed out without the shown position
/// ever running backwards. While paused, snapshots are applied directly. Wall-clock timestamps are converted to the
/// monotonic clock once, when a snapshot arrives; everything after that uses monotonic time only.
/// </para>
/// <para>Thread-safe: updates arrive on thread-pool threads, reads come from the render thread.</para>
/// </remarks>
public sealed class SongClock
{
    private const double MinRate = 0.1;
    private const double MaxRate = 16;

    private readonly TimeProvider _time;
    private readonly SongClockOptions _options;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();

    // Playback state.
    private PlaybackStatus _status = PlaybackStatus.Closed;
    private bool _playing;
    private double _rate = 1.0;
    private double _lastBadRate = 1.0;

    // Anchor: the true position at a monotonic timestamp.
    private bool _hasTimeline;
    private TimeSpan _anchorPosition;
    private long _anchorTimestamp;
    private TimeSpan? _duration;
    private MediaTimeline? _lastSnapshot;
    private MediaTimeline? _previousTrackSnapshot;
    private bool _timestampSuspect;
    private readonly WholeSecondPhase _phase = new();
    private bool _correctsWholeSeconds;

    // Track changes: when the last one was (null: none since the session started) and whether the current timeline
    // arrived after it (so it belongs to the track the metadata now describes).
    private long? _trackChangeTimestamp;
    private bool _timelineSinceTrackChange;

    // Drift slew: shown = target + offset, the offset decaying linearly to zero over _slewDuration of playing time.
    private TimeSpan _slewOffset;
    private long _slewStart;
    private TimeSpan _slewDuration;

    // Timeline quality learning.
    private TimelineQuality _quality = TimelineQuality.Unknown;
    private bool _seekOnlyConfirmed;
    private int _liveEvidence;
    private long _lastTimelineTimestamp;
    private long _lastStatusChangeTimestamp;
    private long _lastResetTimestamp;

    public SongClock(TimeProvider time, SongClockOptions? options = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _options = options ?? SongClockOptions.Default;
        _options.Validate();
        _log = log;
        var now = _time.GetTimestamp();
        _anchorTimestamp = _slewStart = _lastTimelineTimestamp = _lastStatusChangeTimestamp = _lastResetTimestamp = now;
    }

    /// <summary>
    /// Raised (outside the clock's lock, on the updating thread) when a snapshot shows a jump within the same track:
    /// a seek in either direction, or a repeat-one loop back to the start.
    /// </summary>
    public event EventHandler<SeekDetectedEventArgs>? SeekDetected;

    public SongClockOptions Options => _options;

    /// <summary>For diagnostics: whether the whole-second correction is on, the last fraction it added, and how sure it is.</summary>
    public (bool Active, double LastFraction, double? Uncertainty) WholeSecondState
    {
        get { lock (_gate) return (_correctsWholeSeconds && _phase.Active, _lastFraction, _phase.Uncertainty); }
    }

    private double _lastFraction;
    private TimeSpan _reportLag;

    /// <summary>
    /// Whether the followed player is known to cut its position to whole seconds (Apple Music for Windows), so the
    /// clock recovers the lost fraction (<see cref="WholeSecondPhase"/>). Off for every other player.
    /// </summary>
    public bool CorrectsWholeSeconds
    {
        get { lock (_gate) return _correctsWholeSeconds; }
        set { lock (_gate) { _correctsWholeSeconds = value; _phase.Restart(); } }
    }

    /// <summary>
    /// How far behind the sound the followed player's playing position is when it is published, added while playing.
    /// Apple Music for Windows hands out the position from its previous ~1 s tick (<see cref="AppleMusicReportLag"/>);
    /// zero for every other player.
    /// </summary>
    public TimeSpan ReportLag
    {
        get { lock (_gate) return _reportLag; }
        set { lock (_gate) _reportLag = value; }
    }

    /// <summary>
    /// Apple Music for Windows' lag, measured on 13 Nov 2026 with Tap to Sync on laptop speakers once the whole-second
    /// correction had settled: +1.11, +0.98 and +0.91 s on three songs from three lyric sources (with the 0.16 s lyric
    /// lead already applied), about one of its 1.02 s timeline ticks.
    /// </summary>
    public static readonly TimeSpan AppleMusicReportLag = TimeSpan.FromSeconds(1.0);

    public TimeSpan Position => Read().Position;

    public bool IsPlaying => Read().IsPlaying;

    public TimelineQuality Quality => Read().Quality;

    public double Confidence => Read().Confidence;

    public PlaybackStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>The current position and its quality. A pure function of the clock's state and the current time.</summary>
    public SongClockReading Read()
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            var quality = EffectiveQuality(now);
            return new SongClockReading(
                ShownPosition(now), _duration, _playing, _rate, quality, ConfidenceFor(quality, now), _hasTimeline);
        }
    }

    /// <summary>Takes in the player's playback status and rate. Pausing freezes the shown position where it is.</summary>
    public void UpdatePlayback(PlaybackStatus status, double? playbackRate)
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            PersistQuality(now);
            var playing = status == PlaybackStatus.Playing;
            var rate = SanitizeRate(playbackRate);
            if (playing != _playing || rate != _rate)
            {
                Rebase(now);
                _phase.Restart();
                if (playing != _playing) _lastStatusChangeTimestamp = now;
                _playing = playing;
                _rate = rate;
                // The remaining backward slew must still respect the no-backwards rule at the new rate.
                if (_playing && _slewOffset > TimeSpan.Zero)
                    _slewDuration = Max(_slewDuration, MinBackwardSlew(_slewOffset, _rate));
            }

            _status = status;
        }
    }

    /// <inheritdoc cref="UpdatePlayback(PlaybackStatus, double?)"/>
    public void UpdatePlayback(MediaPlaybackInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        UpdatePlayback(info.Status, info.PlaybackRate);
    }

    /// <summary>
    /// Takes in a timeline snapshot. Duplicates of the last snapshot are ignored. Returns true if it moved the clock.
    /// </summary>
    public bool UpdateTimeline(MediaTimeline timeline)
    {
        var now = _time.GetTimestamp();
        var wallNow = _time.GetUtcNow();
        SeekDetectedEventArgs? seek = null;
        lock (_gate)
        {
            PersistQuality(now);

            if (_hasTimeline && _lastSnapshot == timeline)
                return false;
            if (!_hasTimeline && _previousTrackSnapshot == timeline)
            {
                // Re-read straight after a track change but still the old track's timeline: don't seed from it.
                _log?.Invoke("Song clock: ignored the previous track's timeline after a track change.");
                return false;
            }

            var duration = timeline.Duration;
            var position = timeline.RelativePosition;
            var rawAge = wallNow - timeline.LastUpdatedTime;
            var age = rawAge;
            var suspect = false;
            if (age < TimeSpan.Zero)
            {
                if (-age > _options.FutureTolerance)
                {
                    _log?.Invoke($"Song clock: timeline LastUpdatedTime is {(-age).TotalSeconds:0.###} s in the future; treating it as now.");
                    suspect = true;
                }

                age = TimeSpan.Zero;
            }
            else if (age > _options.MaxSnapshotAge)
            {
                _log?.Invoke($"Song clock: timeline LastUpdatedTime {timeline.LastUpdatedTime:O} is implausibly old; treating it as now.");
                age = TimeSpan.Zero;
                suspect = true;
            }

            if (_playing)
                position += age * _rate;

            // A player that cuts its position to whole seconds (Apple Music for Windows) is put back where it really is.
            var snapshotTimestamp = now - (long)(age.TotalSeconds * _time.TimestampFrequency);
            if (_correctsWholeSeconds)
            {
                var fraction = _phase.Correct(timeline.RelativePosition, snapshotTimestamp, _time.TimestampFrequency, _playing, _rate);
                _lastFraction = fraction.TotalSeconds;
                position += fraction;
            }
            if (_playing)
                position += _reportLag;

            if (duration is { } d && position > d)
            {
                if (timeline.RelativePosition > d + _options.DurationChangeTolerance)
                    _log?.Invoke($"Song clock: position {timeline.RelativePosition} is past the duration {d}; clamped.");
                else if (_playing && age > TimeSpan.Zero)
                    _log?.Invoke($"Song clock: extrapolating a {age.TotalSeconds:0.#} s old timeline runs past the end; clamped.");
                position = d;
            }

            if (!_hasTimeline)
            {
                ClearSlew(now);
                _timelineSinceTrackChange = _trackChangeTimestamp is not null;
            }
            else
            {
                var shown = ShownPosition(now);
                var delta = position - shown;
                var durationChanged = _duration is { } oldD && duration is { } newD
                    && (oldD - newD).Duration() > _options.DurationChangeTolerance;

                if (durationChanged)
                {
                    _log?.Invoke("Song clock: the duration changed; treating the snapshot as a new track (no seek).");
                    ClearSlew(now);
                }
                else if (delta.Duration() > _options.SeekThreshold)
                {
                    ClearSlew(now);
                    seek = new SeekDetectedEventArgs(shown, position);
                }
                else if (!_playing)
                {
                    ClearSlew(now); // Paused: nothing is moving, so apply the player's position directly.
                }
                else
                {
                    _slewOffset = shown - position;
                    _slewStart = now;
                    _slewDuration = _slewOffset > TimeSpan.Zero
                        ? Max(_options.SlewDuration, MinBackwardSlew(_slewOffset, _rate))
                        : _options.SlewDuration;
                    NoteMatchingUpdate(now, rawAge, suspect);
                }
            }

            _anchorPosition = position;
            _anchorTimestamp = now;
            _duration = duration;
            _lastSnapshot = timeline;
            _lastTimelineTimestamp = now;
            _hasTimeline = true;
            _timestampSuspect = suspect;
        }

        if (seek is not null)
            SeekDetected?.Invoke(this, seek);
        return true;
    }

    /// <summary>
    /// Starts a new track: the position goes back to 0 (and runs from there if playing) until the new track's
    /// timeline arrives. The learned timeline quality is kept: it belongs to the player, not the track.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Players send the new track's timeline and its metadata in either order, and may publish the metadata in
    /// stages. The current timeline is therefore kept, not discarded, when either
    /// </para>
    /// <list type="bullet">
    /// <item>it is a start-of-track snapshot (position within <see cref="SongClockOptions.SeekThreshold"/>) that
    /// arrived within <see cref="SongClockOptions.StatusSettleTime"/> before this call (timeline first), or</item>
    /// <item>it arrived after the previous track change, which was within
    /// <see cref="SongClockOptions.MetadataSettleTime"/> (metadata still settling).</item>
    /// </list>
    /// <para>Otherwise the old track's last snapshot is remembered, and an identical re-read of it is ignored.</para>
    /// </remarks>
    public void ResetTrack()
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            PersistQuality(now);
            if (_hasTimeline && _lastSnapshot is { } last
                && last.RelativePosition <= _options.SeekThreshold
                && _time.GetElapsedTime(_lastTimelineTimestamp, now) <= _options.StatusSettleTime)
            {
                _log?.Invoke("Song clock: track changed right after a start-of-track timeline; keeping that timeline.");
                KeepTimelineForNewTrack(now);
                _trackChangeTimestamp = now;
                return;
            }

            if (_hasTimeline && _timelineSinceTrackChange && _trackChangeTimestamp is { } previousChange
                && _time.GetElapsedTime(previousChange, now) <= _options.MetadataSettleTime)
            {
                _log?.Invoke("Song clock: metadata changed again soon after a track change; keeping the timeline that came since.");
                KeepTimelineForNewTrack(now);
                return;
            }

            ResetTrackCore(now);
            _phase.Restart();
            _trackChangeTimestamp = now;
        }
    }

    private void KeepTimelineForNewTrack(long now)
    {
        _previousTrackSnapshot = null;
        _timelineSinceTrackChange = true;
        _lastResetTimestamp = now;
        _liveEvidence = 0;
    }

    /// <summary>Starts over for a different player: forgets everything, including the learned timeline quality.</summary>
    public void ResetSession()
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            ResetTrackCore(now);
            _phase.Restart();
            _previousTrackSnapshot = null;
            _trackChangeTimestamp = null;
            _status = PlaybackStatus.Closed;
            _playing = false;
            _rate = 1.0;
            _quality = TimelineQuality.Unknown;
            _seekOnlyConfirmed = false;
            _liveEvidence = 0;
            _lastStatusChangeTimestamp = now;
        }
    }

    private void ResetTrackCore(long now)
    {
        if (_hasTimeline) _previousTrackSnapshot = _lastSnapshot;
        _hasTimeline = false;
        _lastSnapshot = null;
        _anchorPosition = TimeSpan.Zero;
        _anchorTimestamp = now;
        _duration = null;
        _timestampSuspect = false;
        _timelineSinceTrackChange = false;
        _lastResetTimestamp = now;
        _lastTimelineTimestamp = now;
        _liveEvidence = 0;
        ClearSlew(now);
    }

    private void NoteMatchingUpdate(long now, TimeSpan rawAge, bool suspect)
    {
        var sinceLastUpdate = _time.GetElapsedTime(_lastTimelineTimestamp, now);
        var settledSince = Math.Max(_lastStatusChangeTimestamp, _lastResetTimestamp);
        var settled = _time.GetElapsedTime(settledSince, now) >= _options.StatusSettleTime;

        // A matching update after a long silence is the seek-only signature: the player stayed quiet and we were right.
        if (_quality == TimelineQuality.SeekOnly && sinceLastUpdate >= _options.SeekOnlyAfter)
            _seekOnlyConfirmed = true;

        // Live evidence: a fresh, matching update mid-playback, soon after the previous one and away from any status
        // change (players send those on pause/resume/track change even if they never update otherwise).
        if (settled && !suspect && rawAge <= _options.FreshSnapshotAge && sinceLastUpdate < _options.SeekOnlyAfter)
        {
            if (++_liveEvidence >= _options.LiveEvidenceRequired && _quality != TimelineQuality.Live)
            {
                _quality = TimelineQuality.Live;
                _log?.Invoke("Song clock: the player updates its timeline live.");
            }
        }
        else if (sinceLastUpdate >= _options.SeekOnlyAfter)
        {
            _liveEvidence = 0;
        }
    }

    private void PersistQuality(long now)
    {
        var quality = EffectiveQuality(now);
        if (quality != _quality)
        {
            _quality = quality;
            _liveEvidence = 0;
            _log?.Invoke("Song clock: no timeline updates while playing; the player only reports seeks (SeekOnly).");
        }
    }

    private TimelineQuality EffectiveQuality(long now)
    {
        if (_quality == TimelineQuality.Unknown && _playing && _hasTimeline)
        {
            var since = Math.Max(_lastTimelineTimestamp, Math.Max(_lastStatusChangeTimestamp, _lastResetTimestamp));
            if (_time.GetElapsedTime(since, now) >= _options.SeekOnlyAfter)
                return TimelineQuality.SeekOnly;
        }

        return _quality;
    }

    private double ConfidenceFor(TimelineQuality quality, long now)
    {
        if (!_hasTimeline) return _playing ? 0.25 : 0.0;
        if (_timestampSuspect) return 0.5;
        if (!_playing) return 1.0;
        return quality switch
        {
            TimelineQuality.Live => _time.GetElapsedTime(_lastTimelineTimestamp, now) <= _options.LiveStaleAfter ? 1.0 : 0.6,
            TimelineQuality.SeekOnly => _seekOnlyConfirmed ? 0.95 : 0.85,
            _ => 0.75,
        };
    }

    // The true (target) position: anchor extrapolated at the rate while playing, clamped to [0, duration].
    private TimeSpan TargetPosition(long now)
    {
        var position = _anchorPosition;
        if (_playing) position += _time.GetElapsedTime(_anchorTimestamp, now) * _rate;
        return Clamp(position);
    }

    private TimeSpan CurrentOffset(long now)
    {
        if (_slewOffset == TimeSpan.Zero || !_playing) return _slewOffset;
        var elapsed = _time.GetElapsedTime(_slewStart, now);
        if (elapsed >= _slewDuration) return TimeSpan.Zero;
        return _slewOffset * (1.0 - (elapsed / _slewDuration));
    }

    private TimeSpan ShownPosition(long now) => Clamp(TargetPosition(now) + CurrentOffset(now));

    // Moves the anchor to now (before a status or rate change) without changing what is shown.
    private void Rebase(long now)
    {
        var offset = CurrentOffset(now);
        var remaining = _playing ? _slewDuration - _time.GetElapsedTime(_slewStart, now) : _slewDuration;
        _anchorPosition = TargetPosition(now);
        _anchorTimestamp = now;
        _slewOffset = offset;
        _slewStart = now;
        _slewDuration = offset == TimeSpan.Zero || remaining <= TimeSpan.Zero ? _options.SlewDuration : remaining;
    }

    private void ClearSlew(long now)
    {
        _slewOffset = TimeSpan.Zero;
        _slewStart = now;
        _slewDuration = _options.SlewDuration;
    }

    // Shortest slew that keeps the shown position moving forward at ≥ (1 − fraction) × rate while falling back.
    private TimeSpan MinBackwardSlew(TimeSpan offset, double rate) => offset / (rate * _options.MaxBackwardSlewFraction);

    private TimeSpan Clamp(TimeSpan position)
    {
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        if (_duration is { } d && position > d) return d;
        return position;
    }

    private double SanitizeRate(double? rate)
    {
        if (rate is not { } r) return 1.0;
        if (double.IsNaN(r) || double.IsInfinity(r) || r <= 0)
        {
            if (!r.Equals(_lastBadRate)) _log?.Invoke($"Song clock: ignoring playback rate {r}; using 1.0.");
            _lastBadRate = r;
            return 1.0;
        }

        return Math.Clamp(r, MinRate, MaxRate);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
