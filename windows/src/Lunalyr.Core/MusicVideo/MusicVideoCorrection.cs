// Ported from the Mac's MusicVideoCorrection.swift (Lunalyr's own code; no line of the Verso import). How the sync loop
// corrects drift, per player: a rate-steered player eases small drift with the playback rate and seeks only real jumps;
// YouTube's rates are too coarse, so it seeks rarely (hysteresis), each seek led by the time this session's player takes
// to land one. On Windows the YouTube player uses the led seeks; a local file needs no correction at all (its frames are
// chosen by the song's clock, MusicVideoFrameSync), so the rate style is ported for fidelity and its tests.

namespace Lunalyr.Core.MusicVideo;

/// <summary>Small drift corrected with the playback rate instead of a seek.</summary>
public sealed class MusicVideoRateNudge
{
    /// <summary>Drift beyond this is a jump: sought.</summary>
    public const double SeekThreshold = 1.5;
    /// <summary>Start easing past this drift…</summary>
    public const double Engage = 0.12;
    /// <summary>…and stop once within this.</summary>
    public const double Release = 0.04;
    /// <summary>The furthest from 1 the rate goes.</summary>
    public const double MaximumNudge = 0.03;
    /// <summary>Drift is eased away over about this many seconds (until the nudge caps).</summary>
    public const double Horizon = 6.0;

    public bool Easing { get; private set; }

    /// <summary>The rate for a playing video <paramref name="drift"/> seconds ahead (+) or behind (−).</summary>
    public double Rate(double drift)
    {
        if (Easing && Math.Abs(drift) < Release) Easing = false;
        else if (!Easing && Math.Abs(drift) > Engage) Easing = true;
        if (!Easing) return 1;
        var nudge = Math.Clamp(drift / Horizon, -MaximumNudge, MaximumNudge);
        // In steps of 0.005, so the rate is not reset on every check.
        return 1 - Math.Round(nudge * 200, MidpointRounding.AwayFromZero) / 200;
    }

    public void Reset() => Easing = false;
}

/// <summary>
/// How long this session's YouTube player takes from <c>seekTo</c> until it plays at the target: the lead added to the next
/// seek. Measured from the drift once the video plays after a seek: latency = lead − residual.
/// </summary>
public sealed class MusicVideoSeekLatency
{
    public const double Initial = 0.25;
    public const double BoundsMin = 0, BoundsMax = 2.5;
    public const int Kept = 5;

    private readonly List<double> _samples = [];

    public IReadOnlyList<double> Samples => _samples;

    /// <summary>The lead for the next seek: the median of the last few landings.</summary>
    public double Lead
    {
        get
        {
            if (_samples.Count == 0) return Initial;
            var sorted = _samples.Order().ToList();
            var middle = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
        }
    }

    public void Record(double lead, double residual)
    {
        if (!double.IsFinite(lead) || !double.IsFinite(residual)) return;
        var latency = lead - residual;
        // Wild values are a scrub or a stall during the seek, not latency.
        if (latency <= -0.5 || latency >= BoundsMax + 1) return;
        _samples.Add(Math.Clamp(latency, BoundsMin, BoundsMax));
        while (_samples.Count > Kept) _samples.RemoveAt(0);
    }
}

/// <summary>
/// When a YouTube video is sought: only for drift that is large or lasts, and once correcting, until it is well within
/// step. Drift that simply stays between release and engage is sought once it has lasted <see cref="LingerChecks"/>; each
/// such seek doubles the wait for the next.
/// </summary>
public sealed class MusicVideoSeekHysteresis
{
    public const double Engage = 1.0;
    public const double Jump = 2.5;
    public const double Release = 0.3;
    public const double RetargetRelease = 0.12;
    public const int Retries = 2;
    public const int Persistence = 2;
    public const int LingerChecks = 12;

    private int _over;
    private bool _correcting;
    private int _tries;
    private int _lingering;
    private int _lingerWait = LingerChecks;
    private double _releaseNow = Release;

    /// <summary>Whether to seek now, for a playing, settled video <paramref name="drift"/> off.</summary>
    public bool ShouldSeek(double drift)
    {
        var size = Math.Abs(drift);
        if (_correcting)
        {
            if (size <= _releaseNow || _tries >= Retries)
            {
                _correcting = false;
                _tries = 0;
                _over = 0;
                _releaseNow = Release;
                return false;
            }
            _tries++;
            return true;
        }
        if (size > Jump) return StartCorrecting();
        if (size > Engage)
        {
            _over++;
            return _over >= Persistence && StartCorrecting();
        }
        _over = 0;
        if (size <= Release)
        {
            _lingering = 0;
            _lingerWait = LingerChecks;
            return false;
        }
        _lingering++;
        if (_lingering < _lingerWait) return false;
        _lingerWait *= 2;
        return StartCorrecting();
    }

    /// <summary>The target itself moved (a nudge, a tap): what is off now is corrected at once.</summary>
    public void Retarget()
    {
        _correcting = true;
        _releaseNow = RetargetRelease;
        _tries = 0;
        _over = 0;
        _lingering = 0;
        _lingerWait = LingerChecks;
    }

    private bool StartCorrecting()
    {
        _correcting = true;
        _tries = 0;
        _over = 0;
        _lingering = 0;
        return true;
    }

    public void Reset()
    {
        _over = 0;
        _correcting = false;
        _tries = 0;
        _lingering = 0;
        _lingerWait = LingerChecks;
        _releaseNow = Release;
    }
}

/// <summary>How a surface's drift is corrected.</summary>
public enum MusicVideoCorrectionStyle
{
    /// <summary>Seek past <see cref="MusicVideoDrift.SeekThreshold"/> (the original rule).</summary>
    Seek,
    /// <summary>Ease small drift with the rate; seek only jumps.</summary>
    Rate,
    /// <summary>Seek rarely, led by the measured seek latency (YouTube).</summary>
    LedSeek,
}

/// <summary>The sync loop's decisions, with the state each style keeps.</summary>
public sealed class MusicVideoCorrector(MusicVideoCorrectionStyle style)
{
    /// <summary>A changed offset seeks a rate-steered player past this; less is eased.</summary>
    public const double RetargetSeek = 0.3;

    private readonly MusicVideoRateNudge _nudge = new();
    private readonly MusicVideoSeekHysteresis _hysteresis = new();
    private double? _pendingLead;
    private bool _retargeted;

    public MusicVideoCorrectionStyle Style { get; } = style;
    public double CurrentRate { get; private set; } = 1;
    public MusicVideoSeekLatency Latency { get; } = new();
    public double LastSeek { get; private set; } = double.NegativeInfinity;

    private double Threshold => Style switch
    {
        MusicVideoCorrectionStyle.Rate => MusicVideoRateNudge.SeekThreshold,
        MusicVideoCorrectionStyle.LedSeek => MusicVideoSeekHysteresis.Engage,
        _ => MusicVideoDrift.SeekThreshold,
    };

    /// <param name="fixedLead">The surface's own lead, for Seek and Rate.</param>
    public MusicVideoAction Decide(double? target, MusicVideoDrift.Sample sample, bool shouldPlay, double now, double fixedLead)
    {
        var sinceLastSeek = now - LastSeek;
        var settled = sinceLastSeek >= MusicVideoDrift.SeekSettle;
        var lead = Style == MusicVideoCorrectionStyle.LedSeek ? Latency.Lead : fixedLead;
        var steady = shouldPlay && sample.VideoPlaying && !sample.Buffering;
        if (target is { } goal && steady)
        {
            var drift = sample.VideoTime - goal;
            // The first steady check after a seek tells how long it took.
            if (_pendingLead is { } used)
            {
                _pendingLead = null;
                if (Style == MusicVideoCorrectionStyle.LedSeek) Latency.Record(used, drift);
            }
            switch (Style)
            {
                case MusicVideoCorrectionStyle.Rate:
                {
                    if (!settled) return MusicVideoAction.None;
                    var deliberate = _retargeted;
                    _retargeted = false;
                    if (Math.Abs(drift) > MusicVideoRateNudge.SeekThreshold || (deliberate && Math.Abs(drift) > RetargetSeek))
                        return SeekTo(goal + lead, true, now);
                    var next = _nudge.Rate(drift);
                    if (next != CurrentRate)
                    {
                        CurrentRate = next;
                        return MusicVideoAction.Rate(next);
                    }
                    return MusicVideoAction.None;
                }
                case MusicVideoCorrectionStyle.LedSeek:
                    if (!settled) return MusicVideoAction.None;
                    return _hysteresis.ShouldSeek(drift) ? SeekTo(goal + Latency.Lead, true, now) : MusicVideoAction.None;
            }
        }
        var basic = MusicVideoDrift.Decide(target, sample, shouldPlay, sinceLastSeek, lead, Threshold);
        switch (basic.Kind)
        {
            case MusicVideoActionKind.Play:
                ResetRate();
                return MusicVideoAction.Play;
            case MusicVideoActionKind.Pause:
                ResetRate();
                _hysteresis.Reset();
                _pendingLead = null;
                return MusicVideoAction.Pause;
            case MusicVideoActionKind.Seek:
                return SeekTo(basic.Value, basic.ThenPlay, now, basic.ThenPlay ? lead : null);
            default:
                return MusicVideoAction.None;
        }
    }

    private MusicVideoAction SeekTo(double to, bool thenPlay, double now, double? led = null)
    {
        LastSeek = now;
        _pendingLead = thenPlay ? led ?? (Style == MusicVideoCorrectionStyle.LedSeek ? Latency.Lead : null) : null;
        ResetRate();
        return MusicVideoAction.Seek(to, thenPlay);
    }

    /// <summary>The offset in force changed (a nudge, a tap): applied at the next steady check, however small.</summary>
    public void TargetMoved()
    {
        switch (Style)
        {
            case MusicVideoCorrectionStyle.Rate:
                _retargeted = true;
                break;
            case MusicVideoCorrectionStyle.LedSeek:
                _hysteresis.Retarget();
                break;
        }
    }

    /// <summary>Someone else sought the video at <paramref name="now"/> (a drag on the video's bar): it gets time to land.</summary>
    public void NoteSeek(double now)
    {
        if (!double.IsFinite(now)) return;
        LastSeek = Math.Max(LastSeek, now);
        _pendingLead = null;
        ResetRate();
    }

    private void ResetRate()
    {
        CurrentRate = 1;
        _nudge.Reset();
    }
}
