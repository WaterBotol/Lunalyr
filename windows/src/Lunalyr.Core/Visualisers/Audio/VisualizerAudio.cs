// Adapted in part from Synesthia by Jon Jaques (MIT): the stereo merge and smoother arrived with Lunalyr for Mac's Synesthia import (VisualizerAudio.swift).

using System.Numerics;

namespace Lunalyr.Core.Visualisers;

/// <summary>
/// The visualisers' audio source: one <see cref="AudioAnalyzer"/> per channel, merged, and one sound-type detector
/// over both channels (<see cref="SoundTypeFeed"/>).
/// </summary>
/// <remarks>
/// The two FFTs are independent; the maximum band energy preserves stereo-only instruments and avoids cancellation in
/// opposite-phase recordings.
///
/// Reads are aligned to what the listener hears: capture taps the mix before the output device, so visuals are delayed
/// by the device's latency beyond what analysis and drawing already take (bounded, and zero for speakers). The latency
/// comes from an injected source (the Mac measures its default output device) unless
/// <see cref="SetOutputLatency"/> overrides it.
/// </remarks>
public sealed class VisualizerAudio
{
    /// <summary>
    /// Capture to light, excluding the device: ~5 ms onset blocks, ~10 ms of smoothing, up to a frame of waiting, and
    /// scan-out.
    /// </summary>
    public const double PipelineLatency = 0.045;
    /// <summary>The longest the visuals ever wait for the sound.</summary>
    public const double MaximumDelay = 0.25;
    /// <summary>Reads within this many seconds of the last one share it.</summary>
    public const double FrameShare = 0.002;

    private readonly AudioAnalyzer _left, _right;
    private readonly SoundTypeFeed _soundTypes = new();
    private readonly Lock _lock = new();
    private int _channelCount = 1;
    private double? _latencyOverride;
    private readonly Func<double>? _outputLatency;
    private readonly Func<double> _now;
    /// <summary>The last merged read; surfaces drawing the same frame share it.</summary>
    private bool _hasCache;
    private double _cachedTime;
    private AudioSnapshot _cachedSnapshot = new();
    /// <summary>The newest display time the sound types were shown at (<see cref="Latest()"/>).</summary>
    private double _soundShownTime = double.NegativeInfinity;

    /// <param name="outputLatency">
    /// The output device's latency in seconds, read on every <see cref="VisualDelay"/>; null means none (0). It is the
    /// replacement for the Mac's OutputLatencyMonitor.
    /// </param>
    /// <param name="now">The clock in seconds (capture times use it too); <see cref="AudioAnalyzer.Now"/> when omitted.</param>
    public VisualizerAudio(Func<double>? outputLatency = null, Func<double>? now = null)
    {
        _outputLatency = outputLatency;
        _now = now ?? AudioAnalyzer.Now;
        _left = new AudioAnalyzer(_now);
        _right = new AudioAnalyzer(_now);
        var feed = _soundTypes;
        _left.ObserveSpectra((in SpectrumPass pass) => feed.Receive(pass, 0));
        _right.ObserveSpectra((in SpectrumPass pass) => feed.Receive(pass, 1));
    }

    /// <summary>
    /// Capture's entry point: the buffer was captured now. It is the stamped overload with the current time, so code
    /// that passes simulated times takes the same path.
    /// </summary>
    public void Append(ReadOnlySpan<float> pcm, double sampleRate, int channel) =>
        Append(pcm, sampleRate, channel, _now());

    /// <summary><paramref name="hostTime"/> is the capture time of the buffer's last sample. Never allocates.</summary>
    public void Append(ReadOnlySpan<float> pcm, double sampleRate, int channel, double hostTime) =>
        (channel == 0 ? _left : _right).Append(pcm, sampleRate, hostTime);

    /// <summary>The number of input channels. Call on the appending thread, between buffers.</summary>
    public void SetChannelCount(int count)
    {
        bool becameStereo;
        lock (_lock)
        {
            becameStereo = count > 1 && _channelCount <= 1;
            _channelCount = count;
        }
        // The right analyser sat idle while the input was mono. It restarts at the left one's position and hop phase,
        // so from the next buffer on both analyse each instant at the same position and the sound types pair the two
        // channels' spectra again.
        if (becameStereo) _right.Align(_left);
        _soundTypes.SetStereo(count > 1);
    }

    /// <summary>A new source: analysis restarts, and the sound-type counters with it.</summary>
    public void Reset()
    {
        _left.Reset();
        _right.Reset();
        _soundTypes.Reset();
        lock (_lock)
        {
            _hasCache = false;
            _soundShownTime = double.NegativeInfinity;
        }
        SetChannelCount(1);
    }

    /// <summary>Replaces the measured output latency, in seconds (null returns to measuring).</summary>
    public void SetOutputLatency(double? seconds)
    {
        lock (_lock)
        {
            _latencyOverride = seconds;
            _hasCache = false;
        }
    }

    /// <summary>How long the visuals wait so they land with the sound.</summary>
    public double VisualDelay
    {
        get
        {
            double? latencyOverride;
            lock (_lock) latencyOverride = _latencyOverride;
            return Delay(latencyOverride ?? _outputLatency?.Invoke() ?? 0);
        }
    }

    /// <summary>The visual delay for an output latency: what exceeds the pipeline's own latency, at most <see cref="MaximumDelay"/>.</summary>
    public static double Delay(double outputLatency)
    {
        if (!double.IsFinite(outputLatency)) return 0;
        return Math.Min(MaximumDelay, Math.Max(0, outputLatency - PipelineLatency));
    }

    /// <summary>The time readers should show now.</summary>
    public double PresentationTime => _now() - VisualDelay;

    /// <summary>The newest 2048 samples Wavefield expects, delay-aligned.</summary>
    public WavefieldPCMWindow? WavefieldPcm() => Pcm(2048);

    /// <summary>
    /// <paramref name="count"/> samples from the stronger channel, ending with the newest one the listener can hear at
    /// <paramref name="time"/> (default: now, delay-aligned). Allocates the returned array, as the Mac does.
    /// </summary>
    public WavefieldPCMWindow? Pcm(int count, double? time = null)
    {
        var at = time ?? PresentationTime;
        var a = _left.Pcm(at, count);
        bool stereo;
        lock (_lock) stereo = _channelCount > 1;
        if (!stereo) return a;
        var b = _right.Pcm(at, count);
        if (b is null) return a;
        // A mono input is what Wavefield expects. Select the stronger channel so opposite-phase content does not cancel
        // during a stereo downmix.
        return a is null ? b : a.Level >= b.Level ? a : b;
    }

    /// <summary>Samples received so far on the first channel.</summary>
    public long SampleCount => _left.SampleCount;

    /// <summary>Spectra the sound-type detector has analysed since the last reset: one per quarter window, mono or stereo.</summary>
    public int SoundTypePasses => _soundTypes.PassCount;

    /// <summary>
    /// What the listener hears now. Every display and the preview call this each frame, so calls within
    /// <see cref="FrameShare"/> of each other share one read. Never allocates.
    /// </summary>
    /// <remarks>
    /// The sound types never go back in time: when the output latency grows (headphones connected), the display time
    /// steps back by up to <see cref="MaximumDelay"/>, and onsets already shown must not be taken back, nor flare again.
    /// They hold at the newest time shown until the display catches up.
    /// </remarks>
    public AudioSnapshot Latest()
    {
        var time = PresentationTime;
        double soundTime;
        lock (_lock)
        {
            if (_hasCache && Math.Abs(time - _cachedTime) < FrameShare) return _cachedSnapshot;
            soundTime = ShownSoundTime(time);
        }
        var snapshot = Snapshot(time, soundTime);
        lock (_lock)
        {
            _cachedTime = time;
            _cachedSnapshot = snapshot;
            _hasCache = true;
        }
        return snapshot;
    }

    /// <summary>
    /// <c>Latest().Sound</c> alone, for surfaces that analyse their own samples (the web engines): the same display time
    /// and the same guarantee that nothing goes back in time, without the spectrum.
    /// </summary>
    public SoundTypeFeatures LatestSound()
    {
        var time = PresentationTime;
        double soundTime;
        lock (_lock)
        {
            if (_hasCache && Math.Abs(time - _cachedTime) < FrameShare) return _cachedSnapshot.Sound;
            soundTime = ShownSoundTime(time);
        }
        return _soundTypes.Read(soundTime);
    }

    /// <summary>Call with the lock held.</summary>
    private double ShownSoundTime(double time)
    {
        _soundShownTime = Math.Max(_soundShownTime, time);
        return _soundShownTime;
    }

    /// <summary>The analysis as of <paramref name="time"/>, sound types included: a pure function of the time, unlike <see cref="Latest()"/>.</summary>
    public AudioSnapshot Latest(double time) => Snapshot(time, time);

    private AudioSnapshot Snapshot(double time, double soundTime)
    {
        var a = _left.Latest(time);
        a.Sound = _soundTypes.Read(soundTime);
        bool stereo;
        lock (_lock) stereo = _channelCount > 1;
        if (!stereo) return a;
        var b = _right.Latest(time);
        for (var i = 0; i < AudioSnapshot.BandCount; i++) a.Bands[i] = Math.Max(a.Bands[i], b.Bands[i]);
        for (var i = 0; i < AudioSnapshot.ComponentCount; i++) a.Components[i] = Math.Max(a.Components[i], b.Components[i]);
        if (b.Level > a.Level)
        {
            a.Waveform = b.Waveform;
            a.Centroid = b.Centroid;
        }
        a.Wavefield.Bands = Vector3.Max(a.Wavefield.Bands, b.Wavefield.Bands);
        if (b.Wavefield.Rms > a.Wavefield.Rms || !a.Wavefield.IsReceiving)
        {
            var bands = a.Wavefield.Bands;
            a.Wavefield = b.Wavefield;
            a.Wavefield.Bands = bands;
        }
        a.Level = Math.Max(a.Level, b.Level); a.Bass = Math.Max(a.Bass, b.Bass);
        a.Mid = Math.Max(a.Mid, b.Mid); a.Treble = Math.Max(a.Treble, b.Treble);
        a.Beat = Math.Max(a.Beat, b.Beat); a.TrebleBeat = Math.Max(a.TrebleBeat, b.TrebleBeat);
        a.Flux = Math.Max(a.Flux, b.Flux);
        return a;
    }
}

/// <summary>
/// Runs the sound-type detector on the analysers' spectra and keeps what the render loop reads.
/// </summary>
/// <remarks>
/// With stereo input the two channels' power spectra of each pass are averaged, so one detector runs with one set of
/// counters. Averaging power cannot cancel opposite-phase content (a sample downmix can), and a hard-panned instrument
/// still counts, at −3 dB. The two channels' spectra of one instant share a stream position (VisualizerAudio keeps the
/// analysers aligned), which pairs them: a left pass waits for its right partner.
///
/// Each instant reaches the detector at most once, in position order. A pass at or before the newest position processed
/// was already covered, so it is skipped. A left pass whose partner has not come by the time its slot is reused (a
/// buffer of more spectra than the ring holds, or no right channel at all) is used alone, and then its partner is
/// skipped; a right pass without a waiting partner is used alone.
///
/// <see cref="Receive"/> runs on the analysis worker inside an analyser's lock and never allocates; <see cref="Read"/>
/// serves the render loop. Each side has its own lock, and neither calls back into an analyser.
/// </remarks>
public sealed class SoundTypeFeed
{
    /// <summary>
    /// Left passes kept for their right partners. Capture appending both channels a piece of at most 1024 samples at a
    /// time makes one spectrum per channel at 88.2-96 kHz, 2 at 44.1-48 kHz, 4 at 22.05-32 kHz and 8 at 8-16 kHz. A
    /// larger append outgrows the ring: its first instants are analysed from the left channel alone, still once.
    /// </summary>
    private const int PendingCapacity = 8;
    private const int MaximumBins = SoundTypeDetector.MaximumBinCount;

    private readonly SoundTypeDetector _detector = new();
    private readonly Lock _detectorLock = new();
    private readonly Lock _historyLock = new();
    private readonly SoundTypeHistory _history = new();

    // Worker state, guarded by the detector lock.
    private bool _stereo;
    private readonly float[] _pendingPower = new float[PendingCapacity * MaximumBins];
    private readonly float[] _merged = new float[MaximumBins];
    private readonly PendingPass[] _pending = new PendingPass[PendingCapacity];
    private int _pendingNext;
    /// <summary>Stream position of the newest pass processed, −1 before the first.</summary>
    private long _processedPosition = -1;
    private double _lastTime = double.NegativeInfinity;
    private int _processedPasses;

    private struct PendingPass
    {
        /// <summary>−1 when empty or already merged.</summary>
        public long Position;
        public int Count;
        public float BinHz;
        public double Time;
        public double Dt;
    }

    public SoundTypeFeed() => ClearPending();

    public void SetStereo(bool stereo)
    {
        lock (_detectorLock)
        {
            if (stereo == _stereo) return;
            _stereo = stereo;
            ClearPending();
        }
    }

    /// <summary>A new source: counters, onsets and history start again from zero.</summary>
    public void Reset()
    {
        lock (_detectorLock)
        {
            _detector.Reset();
            ClearPending();
            _processedPosition = -1;
            _processedPasses = 0;
            _lastTime = double.NegativeInfinity;
        }
        lock (_historyLock) _history.Clear();
    }

    /// <summary>The sound types as shown at <paramref name="time"/> (see <see cref="SoundTypeHistory.Read"/>).</summary>
    public SoundTypeFeatures Read(double time)
    {
        lock (_historyLock) return _history.Read(time);
    }

    /// <summary>Passes processed since the last reset.</summary>
    public int PassCount
    {
        get
        {
            lock (_detectorLock) return _processedPasses;
        }
    }

    /// <summary>One analyser's spectrum (<see cref="AudioAnalyzer.ObserveSpectra"/>).</summary>
    public void Receive(in SpectrumPass pass, int channel)
    {
        var source = pass.Power;
        if (source.IsEmpty) return;
        var count = Math.Min(source.Length, MaximumBins);
        source = source[..count];
        lock (_detectorLock)
        {
            if (pass.Position <= _processedPosition) return;
            if (!_stereo)
            {
                if (channel == 0) Process(source, pass.BinHz, pass.Dt, pass.Time, pass.Position);
                return;
            }
            if (channel == 0)
            {
                var slot = _pendingNext;
                _pendingNext = (slot + 1) % PendingCapacity;
                // About to reuse the slot of a pass nobody merged: its partner is late or missing, so it is analysed
                // alone (and the partner skipped).
                var old = _pending[slot];
                if (old.Position > _processedPosition)
                {
                    Process(_pendingPower.AsSpan(slot * MaximumBins, old.Count), old.BinHz, old.Dt, old.Time, old.Position);
                }
                source.CopyTo(_pendingPower.AsSpan(slot * MaximumBins, count));
                _pending[slot] = new PendingPass
                {
                    Position = pass.Position, Count = count, BinHz = pass.BinHz, Time = pass.Time, Dt = pass.Dt,
                };
                return;
            }
            var match = 0;
            while (match < PendingCapacity)
            {
                var left = _pending[match];
                if (left.Position == pass.Position && left.Count == count && left.BinHz == pass.BinHz) break;
                match++;
            }
            if (match >= PendingCapacity)
            {
                Process(source, pass.BinHz, pass.Dt, pass.Time, pass.Position);
                return;
            }
            var waiting = _pendingPower.AsSpan(match * MaximumBins, count);
            var merged = _merged.AsSpan(0, count);
            for (var k = 0; k < count; k++) merged[k] = (waiting[k] + source[k]) * 0.5f;
            var partner = _pending[match];
            _pending[match].Position = -1;
            Process(merged, pass.BinHz, partner.Dt, Math.Min(partner.Time, pass.Time), pass.Position);
        }
    }

    private void ClearPending()
    {
        for (var slot = 0; slot < PendingCapacity; slot++) _pending[slot].Position = -1;
    }

    private void Process(ReadOnlySpan<float> power, float binHz, double dt, double time, long position)
    {
        _processedPosition = position;
        _processedPasses++;
        // After a capture gap the sustained classes faded on screen; the detector continues from there rather than
        // from before the gap.
        if (double.IsFinite(_lastTime)) _detector.Settle(time - _lastTime - dt - SoundTypeHistory.IdleAfter);
        _lastTime = time;
        _detector.Process(power, binHz, dt, time);
        var output = _detector.Current;
        lock (_historyLock) _history.Record(output, time);
    }
}

/// <summary>
/// Smooths successive snapshots for drawing: two cascaded, analytically integrated poles. An input step begins with
/// zero velocity and cannot move the wallpaper abruptly between callbacks, and the elapsed-time solution gives the same
/// motion at 15, 30 and 60 fps.
/// </summary>
/// <remarks>
/// Transient envelopes (beat, treble beat, flux) already jump and decay, so a slow two-pole attack only delays them;
/// they rise through one fast pole instead and keep the gentle two-pole release. The sound types pass through
/// untouched: they are already evaluated at the presentation time. A reference type (the Mac's is a struct) holding two
/// snapshots; one per surface, used from one thread. <see cref="Frame"/> never allocates.
/// </remarks>
public sealed class VisualizerSmoother
{
    /// <summary>Attack time constant for transient envelopes, in seconds.</summary>
    public const float TransientAttack = 0.012f;

    private AudioSnapshot _first = new(), _output = new();
    private double? _lastTime;

    public AudioSnapshot Frame(in AudioSnapshot target, double time)
    {
        var dt = Math.Min(0.1, Math.Max(0, time - (_lastTime ?? time)));
        _lastTime = time;
        for (var i = 0; i < AudioSnapshot.BandCount; i++)
            Follow(target.Bands[i], ref _first.Bands[i], ref _output.Bands[i], dt);
        // A small spatial filter keeps adjacent FFT bins from making sharp corners in the aurora or ridges in the tunnel
        // wall.
        var spatial = _output;
        spatial.Wavefield = target.Wavefield;
        spatial.Sound = target.Sound;
        for (var i = 0; i < AudioSnapshot.BandCount; i++)
        {
            spatial.Bands[i] = (_output.Bands[Math.Max(0, i - 2)] + _output.Bands[Math.Max(0, i - 1)] * 2
                + _output.Bands[i] * 3 + _output.Bands[Math.Min(63, i + 1)] * 2 + _output.Bands[Math.Min(63, i + 2)]) / 9;
        }
        for (var i = 0; i < AudioSnapshot.ComponentCount; i++)
            Follow(target.Components[i], ref _first.Components[i], ref _output.Components[i], dt);
        Follow(target.Bass, ref _first.Bass, ref _output.Bass, dt);
        Follow(target.Mid, ref _first.Mid, ref _output.Mid, dt);
        Follow(target.Treble, ref _first.Treble, ref _output.Treble, dt);
        Follow(target.Level, ref _first.Level, ref _output.Level, dt);
        Follow(target.Centroid, ref _first.Centroid, ref _output.Centroid, dt);
        FollowTransient(target.Beat, ref _first.Beat, ref _output.Beat, dt);
        FollowTransient(target.TrebleBeat, ref _first.TrebleBeat, ref _output.TrebleBeat, dt);
        FollowTransient(target.Flux, ref _first.Flux, ref _output.Flux, dt);
        spatial.Components = _output.Components;
        spatial.Bass = _output.Bass; spatial.Mid = _output.Mid; spatial.Treble = _output.Treble;
        spatial.Level = _output.Level; spatial.Centroid = _output.Centroid;
        spatial.Beat = _output.Beat; spatial.TrebleBeat = _output.TrebleBeat; spatial.Flux = _output.Flux;
        // Follow the waveform envelope to keep rapid phase changes from jittering the geometry while retaining its
        // amplitude and shape.
        for (var sample = 0; sample < AudioSnapshot.WaveformCount; sample++)
            Follow(MathF.Abs(target.Waveform[sample]), ref _first.Waveform[sample], ref _output.Waveform[sample], dt);
        spatial.Waveform = _output.Waveform;
        return spatial;
    }

    private static void Follow(float value, ref float first, ref float output, double dt)
    {
        var target = float.IsFinite(value) ? SwiftMath.Min(1, SwiftMath.Max(0, value)) : 0;
        // The FFT already has attack/release filtering. Another slow envelope erased note attacks; these poles round
        // the geometry without hiding a beat behind several hundred milliseconds of delay.
        var tau = target > first ? 0.055f : 0.22f;
        var step = (float)dt / tau;
        var decay = MathF.Exp(-step);
        var old = first - target;
        output = target + (output - target + old * step) * decay;
        first = target + old * decay;
    }

    private static void FollowTransient(float value, ref float first, ref float output, double dt)
    {
        var target = float.IsFinite(value) ? SwiftMath.Min(1, SwiftMath.Max(0, value)) : 0;
        if (!(target > output))
        {
            Follow(target, ref first, ref output, dt);
            return;
        }
        output += (target - output) * (1 - MathF.Exp(-(float)dt / TransientAttack));
        first = output;
    }
}
