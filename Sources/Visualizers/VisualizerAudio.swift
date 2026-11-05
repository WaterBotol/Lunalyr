import Accelerate
import Foundation
import simd

/// The two FFTs are independent; maximum band energy preserves stereo-only
/// instruments and avoids cancellation in opposite-phase recordings. The
/// sound types come from one detector over both channels (SoundTypeFeed).
///
/// Reads are aligned to what the listener hears: capture taps the mix before
/// the output device, so visuals are delayed by the device's latency beyond
/// what analysis and drawing already take (bounded, and zero for speakers).
final class VisualizerAudio: @unchecked Sendable {
    static let shared = VisualizerAudio(followsOutput: true)
    /// Capture to light, excluding the device: ~5 ms onset blocks, ~10 ms of
    /// smoothing, up to a frame of waiting, and scan-out.
    static let pipelineLatency = 0.045
    /// The longest the visuals ever wait for the sound.
    static let maximumDelay = 0.25

    private let left = AudioAnalyzer(), right = AudioAnalyzer()
    private let soundTypes = SoundTypeFeed()
    private let lock = NSLock()
    private var channelCount = 1
    private var latencyOverride: Double?
    private let followsOutput: Bool
    private let now: () -> Double
    /// The last merged read; surfaces drawing the same frame share it.
    private var cached: (time: Double, snapshot: AudioSnapshot)?
    static let frameShare = 0.002
    /// The newest display time the sound types were shown at (`latest()`).
    private var soundShownTime = -Double.infinity

    /// `followsOutput` measures the default output device's latency; other
    /// instances (tests) use `setOutputLatency`, or none.
    init(followsOutput: Bool = false, now: @escaping () -> Double = AudioAnalyzer.now) {
        self.followsOutput = followsOutput
        self.now = now
        let feed = soundTypes
        left.observeSpectra { feed.receive($0, channel: 0) }
        right.observeSpectra { feed.receive($0, channel: 1) }
        if followsOutput { OutputLatencyMonitor.shared.start() }
    }

    /// Capture's entry point (AudioPCMProcessor): the buffer was captured
    /// now. It is the stamped overload below with the current time, so the
    /// render harness, which passes simulated times to that one, takes the
    /// same path. Put any per-buffer work there, not here.
    func append(_ pcm: UnsafePointer<Float>, count: Int, sampleRate: Double, channel: Int) {
        append(pcm, count: count, sampleRate: sampleRate, channel: channel, hostTime: now())
    }
    /// `hostTime` is the capture time of the buffer's last sample, on the
    /// `CACurrentMediaTime` clock (an IO timestamp's host time in seconds).
    func append(_ pcm: UnsafePointer<Float>, count: Int, sampleRate: Double, channel: Int, hostTime: Double) {
        (channel == 0 ? left : right).append(pcm, count: count, sampleRate: sampleRate, hostTime: hostTime)
    }
    /// Call on the appending thread, between buffers.
    func setChannelCount(_ count: Int) {
        lock.lock()
        let becameStereo = count > 1 && channelCount <= 1
        channelCount = count
        lock.unlock()
        // The right analyser sat idle while the input was mono. It restarts
        // at the left one's position and hop phase, so from the next buffer
        // on both analyse each instant at the same position and the sound
        // types pair the two channels' spectra again.
        if becameStereo { right.align(with: left) }
        soundTypes.setStereo(count > 1)
    }
    /// A new source: analysis restarts, and the sound-type counters with it.
    func reset() {
        left.reset(); right.reset()
        soundTypes.reset()
        lock.lock(); cached = nil; soundShownTime = -.infinity; lock.unlock()
        setChannelCount(1)
    }

    /// Replaces the measured output latency (nil returns to measuring).
    func setOutputLatency(_ seconds: Double?) {
        lock.lock(); latencyOverride = seconds; cached = nil; lock.unlock()
    }

    /// How long the visuals wait so they land with the sound.
    var visualDelay: Double {
        lock.lock(); let override = latencyOverride; lock.unlock()
        return Self.delay(forOutputLatency: override ?? (followsOutput ? OutputLatencyMonitor.shared.latency : 0))
    }

    static func delay(forOutputLatency latency: Double) -> Double {
        guard latency.isFinite else { return 0 }
        return min(maximumDelay, max(0, latency - pipelineLatency))
    }

    /// The time readers should show now.
    var presentationTime: Double { now() - visualDelay }

    func wavefieldPCM() -> WavefieldPCMWindow? { pcm(count: 2048) }

    /// `count` samples from the stronger channel, ending with the newest one
    /// the listener can hear at `time` (default: now, delay-aligned).
    func pcm(count: Int, at time: Double? = nil) -> WavefieldPCMWindow? {
        let time = time ?? presentationTime
        let a = left.pcm(at: time, count: count)
        lock.lock(); let stereo = channelCount > 1; lock.unlock()
        guard stereo, let b = right.pcm(at: time, count: count) else { return a }
        // A mono input is what Wavefield expects. Select the stronger channel
        // so opposite-phase content does not cancel during a stereo downmix.
        return a.map { $0.level >= b.level ? $0 : b } ?? b
    }

    /// Samples received so far on the first channel.
    var sampleCount: Int { left.sampleCount }

    /// Spectra the sound-type detector has analysed since the last reset:
    /// one per quarter window, whether mono or stereo.
    var soundTypePasses: Int { soundTypes.passCount }

    /// What the listener hears now. Every display and the preview call this
    /// each frame, so calls within `frameShare` of each other share one read.
    ///
    /// The sound types never go back in time: when the output latency grows
    /// (headphones connected), the display time steps back by up to
    /// `maximumDelay`, and onsets already shown must not be taken back, nor
    /// flare again. They hold at the newest time shown until the display
    /// catches up; the listener does not hear that stretch twice either.
    func latest() -> AudioSnapshot {
        let time = presentationTime
        lock.lock()
        if let cached, abs(time - cached.time) < Self.frameShare {
            let snapshot = cached.snapshot
            lock.unlock()
            return snapshot
        }
        let soundTime = shownSoundTime(for: time)
        lock.unlock()
        let snapshot = snapshot(at: time, soundAt: soundTime)
        lock.lock(); cached = (time, snapshot); lock.unlock()
        return snapshot
    }

    /// `latest().sound` alone, for surfaces that analyse their own samples
    /// (the web engines): the same display time and the same guarantee that
    /// nothing goes back in time, without the spectrum.
    func latestSound() -> SoundTypeFeatures {
        let time = presentationTime
        lock.lock()
        if let cached, abs(time - cached.time) < Self.frameShare {
            let sound = cached.snapshot.sound
            lock.unlock()
            return sound
        }
        let soundTime = shownSoundTime(for: time)
        lock.unlock()
        return soundTypes.read(at: soundTime)
    }

    /// Call with `lock` held.
    private func shownSoundTime(for time: Double) -> Double {
        soundShownTime = max(soundShownTime, time)
        return soundShownTime
    }

    /// The analysis as of `time`, sound types included: a pure function of
    /// the time, unlike `latest()`.
    func latest(at time: Double) -> AudioSnapshot { snapshot(at: time, soundAt: time) }

    private func snapshot(at time: Double, soundAt soundTime: Double) -> AudioSnapshot {
        var a = left.latest(at: time)
        a.sound = soundTypes.read(at: soundTime)
        lock.lock(); let stereo = channelCount > 1; lock.unlock()
        guard stereo else { return a }
        let b = right.latest(at: time)
        for i in a.bands.indices { a.bands[i] = max(a.bands[i], b.bands[i]) }
        for i in a.components.indices { a.components[i] = max(a.components[i], b.components[i]) }
        if b.level > a.level { a.waveform = b.waveform; a.centroid = b.centroid }
        a.wavefield.bands = simd_max(a.wavefield.bands, b.wavefield.bands)
        if b.wavefield.rms > a.wavefield.rms || !a.wavefield.isReceiving {
            let bands = a.wavefield.bands
            a.wavefield = b.wavefield
            a.wavefield.bands = bands
        }
        a.level = max(a.level, b.level); a.bass = max(a.bass, b.bass)
        a.mid = max(a.mid, b.mid); a.treble = max(a.treble, b.treble)
        a.beat = max(a.beat, b.beat); a.trebleBeat = max(a.trebleBeat, b.trebleBeat)
        a.flux = max(a.flux, b.flux)
        return a
    }
}

/// Runs the sound-type detector on the analysers' spectra and keeps what the
/// render loop reads.
///
/// With stereo input the two channels' power spectra of each pass are
/// averaged, so one detector runs with one set of counters. Averaging power
/// cannot cancel opposite-phase content (a sample downmix can), and a
/// hard-panned instrument still counts, at −3 dB. The two channels' spectra
/// of one instant share a stream position (VisualizerAudio keeps the
/// analysers aligned), which pairs them: a left pass waits for its right
/// partner.
///
/// Each instant reaches the detector at most once, in position order. A
/// pass at or before the newest position processed was already covered, so
/// it is skipped. A left pass whose partner has not come by the time its slot
/// is reused (a buffer of more spectra than the ring holds, or no right
/// channel at all) is used alone, and then its partner is skipped; a right
/// pass without a waiting partner is used alone.
///
/// `receive` runs on the analysis worker inside an analyser's lock and never
/// allocates; `read` serves the render loop. Each side has its own lock, and
/// neither calls back into an analyser.
final class SoundTypeFeed: @unchecked Sendable {
    /// Left passes kept for their right partners. AudioPCMProcessor appends
    /// both channels a piece of at most 1024 samples at a time, which makes
    /// one spectrum per channel at 88.2-96 kHz, 2 at 44.1-48 kHz, 4 at
    /// 22.05-32 kHz and 8 at 8-16 kHz. A larger append outgrows the ring: its
    /// first instants are analysed from the left channel alone, still once.
    private static let pendingCapacity = 8
    private static let maximumBins = SoundTypeDetector.maximumBinCount

    private let detector = SoundTypeDetector()
    private let detectorLock = NSLock()
    private let historyLock = NSLock()
    private var history = SoundTypeHistory()

    // Worker state, guarded by detectorLock.
    private var stereo = false
    private let pendingPower: UnsafeMutablePointer<Float>
    private let merged: UnsafeMutablePointer<Float>
    private var pending = [PendingPass](repeating: PendingPass(), count: SoundTypeFeed.pendingCapacity)
    private var pendingNext = 0
    /// Stream position of the newest pass processed, −1 before the first.
    private var processedPosition = -1
    private var lastTime = -Double.infinity
    private var processedPasses = 0

    private struct PendingPass {
        /// −1 when empty or already merged.
        var position = -1
        var count = 0
        var binHz: Float = 0
        var time = 0.0
        var dt = 0.0
    }

    init() {
        let size = Self.pendingCapacity * Self.maximumBins
        pendingPower = .allocate(capacity: size)
        pendingPower.initialize(repeating: 0, count: size)
        merged = .allocate(capacity: Self.maximumBins)
        merged.initialize(repeating: 0, count: Self.maximumBins)
    }

    deinit {
        pendingPower.deallocate()
        merged.deallocate()
    }

    func setStereo(_ stereo: Bool) {
        detectorLock.lock()
        defer { detectorLock.unlock() }
        guard stereo != self.stereo else { return }
        self.stereo = stereo
        clearPending()
    }

    /// A new source: counters, onsets and history start again from zero.
    func reset() {
        detectorLock.lock()
        detector.reset()
        clearPending()
        processedPosition = -1
        processedPasses = 0
        lastTime = -.infinity
        detectorLock.unlock()
        historyLock.lock()
        history = SoundTypeHistory()
        historyLock.unlock()
    }

    /// The sound types as shown at `time` (see SoundTypeHistory.read).
    func read(at time: Double) -> SoundTypeFeatures {
        historyLock.lock()
        defer { historyLock.unlock() }
        return history.read(at: time)
    }

    /// Passes processed since the last reset.
    var passCount: Int {
        detectorLock.lock()
        defer { detectorLock.unlock() }
        return processedPasses
    }

    /// One analyser's spectrum (AudioAnalyzer.observeSpectra).
    func receive(_ pass: AudioAnalyzer.SpectrumPass, channel: Int) {
        guard let source = pass.power.baseAddress else { return }
        let count = min(pass.power.count, Self.maximumBins)
        detectorLock.lock()
        defer { detectorLock.unlock() }
        guard pass.position > processedPosition else { return }
        guard stereo else {
            if channel == 0 {
                process(source, count: count, binHz: pass.binHz, dt: pass.dt, time: pass.time, position: pass.position)
            }
            return
        }
        if channel == 0 {
            let slot = pendingNext
            pendingNext = (slot + 1) % Self.pendingCapacity
            // About to reuse the slot of a pass nobody merged: its partner is
            // late or missing, so it is analysed alone (and the partner skipped).
            let old = pending[slot]
            if old.position > processedPosition {
                process(pendingPower + slot * Self.maximumBins, count: old.count, binHz: old.binHz, dt: old.dt,
                        time: old.time, position: old.position)
            }
            (pendingPower + slot * Self.maximumBins).update(from: source, count: count)
            pending[slot] = PendingPass(position: pass.position, count: count, binHz: pass.binHz, time: pass.time,
                                        dt: pass.dt)
            return
        }
        var slot = 0
        while slot < Self.pendingCapacity {
            let left = pending[slot]
            if left.position == pass.position, left.count == count, left.binHz == pass.binHz { break }
            slot += 1
        }
        guard slot < Self.pendingCapacity else {
            process(source, count: count, binHz: pass.binHz, dt: pass.dt, time: pass.time, position: pass.position)
            return
        }
        var half: Float = 0.5
        vDSP_vasm(pendingPower + slot * Self.maximumBins, 1, source, 1, &half, merged, 1, vDSP_Length(count))
        let left = pending[slot]
        pending[slot].position = -1
        process(merged, count: count, binHz: pass.binHz, dt: left.dt, time: min(left.time, pass.time),
                position: pass.position)
    }

    private func clearPending() {
        var slot = 0
        while slot < Self.pendingCapacity { pending[slot].position = -1; slot += 1 }
    }

    private func process(_ power: UnsafePointer<Float>, count: Int, binHz: Float, dt: Double, time: Double,
                         position: Int) {
        processedPosition = position
        processedPasses += 1
        // After a capture gap the sustained classes faded on screen; the
        // detector continues from there rather than from before the gap.
        if lastTime.isFinite { detector.settle(idle: time - lastTime - dt - SoundTypeHistory.idleAfter) }
        lastTime = time
        detector.process(power: UnsafeBufferPointer(start: power, count: count), binHz: binHz, dt: dt, time: time)
        let output = detector.output
        historyLock.lock()
        history.record(output, at: time)
        historyLock.unlock()
    }
}

/// Two cascaded, analytically integrated poles. An input step begins with
/// zero velocity and cannot move the wallpaper abruptly between callbacks.
/// The elapsed-time solution gives the same motion at 15, 30 and 60 fps.
///
/// Transient envelopes (beat, treble beat, flux) are different: they already
/// jump and decay, so a slow two-pole attack only delays them (it took about
/// 90 ms to reach half of a kick). They rise through one fast pole instead
/// and keep the gentle two-pole release.
///
/// The sound types pass through untouched: they are already evaluated at the
/// presentation time, and smoothing them would delay every flare and make it
/// depend on the frame rate.
struct VisualizerSmoother {
    private var first = AudioSnapshot(), output = AudioSnapshot()
    private var lastTime: Double?
    /// Attack time constant for transient envelopes, in seconds.
    static let transientAttack: Float = 0.012

    mutating func frame(_ target: AudioSnapshot, at time: Double) -> AudioSnapshot {
        let dt = min(0.1, max(0, time - (lastTime ?? time)))
        lastTime = time
        for i in output.bands.indices {
            Self.follow(target.bands[i], first: &first.bands[i], output: &output.bands[i], dt: dt)
        }
        // A small spatial filter keeps adjacent FFT bins from making sharp
        // corners in the aurora or ridges in the tunnel wall.
        var spatial = output
        spatial.wavefield = target.wavefield
        spatial.sound = target.sound
        for i in output.bands.indices {
            spatial.bands[i] = (output.bands[max(0, i - 2)] + output.bands[max(0, i - 1)] * 2
                + output.bands[i] * 3 + output.bands[min(63, i + 1)] * 2 + output.bands[min(63, i + 2)]) / 9
        }
        for i in output.components.indices {
            Self.follow(target.components[i], first: &first.components[i], output: &output.components[i], dt: dt)
        }
        Self.follow(target.bass, first: &first.bass, output: &output.bass, dt: dt)
        Self.follow(target.mid, first: &first.mid, output: &output.mid, dt: dt)
        Self.follow(target.treble, first: &first.treble, output: &output.treble, dt: dt)
        Self.follow(target.level, first: &first.level, output: &output.level, dt: dt)
        Self.follow(target.centroid, first: &first.centroid, output: &output.centroid, dt: dt)
        Self.followTransient(target.beat, first: &first.beat, output: &output.beat, dt: dt)
        Self.followTransient(target.trebleBeat, first: &first.trebleBeat, output: &output.trebleBeat, dt: dt)
        Self.followTransient(target.flux, first: &first.flux, output: &output.flux, dt: dt)
        spatial.components = output.components
        spatial.bass = output.bass; spatial.mid = output.mid; spatial.treble = output.treble
        spatial.level = output.level; spatial.centroid = output.centroid
        spatial.beat = output.beat; spatial.trebleBeat = output.trebleBeat; spatial.flux = output.flux
        // Follow the waveform envelope to keep rapid phase changes from
        // jittering the geometry while retaining its amplitude and shape.
        for sample in output.waveform.indices {
            Self.follow(abs(target.waveform[sample]), first: &first.waveform[sample],
                        output: &output.waveform[sample], dt: dt)
        }
        spatial.waveform = output.waveform
        return spatial
    }
    private static func follow(_ value: Float, first: inout Float, output: inout Float, dt: Double) {
        let target = value.isFinite ? min(1, max(0, value)) : 0
        // The FFT already has attack/release filtering. Another slow envelope
        // erased note attacks; these poles round the geometry without hiding
        // a beat behind several hundred milliseconds of delay.
        let tau: Float = target > first ? 0.055 : 0.22
        let step = Float(dt) / tau, decay = exp(-step)
        let old = first - target
        output = target + (output - target + old * step) * decay
        first = target + old * decay
    }
    private static func followTransient(_ value: Float, first: inout Float, output: inout Float, dt: Double) {
        let target = value.isFinite ? min(1, max(0, value)) : 0
        guard target > output else {
            follow(target, first: &first, output: &output, dt: dt)
            return
        }
        output += (target - output) * (1 - exp(-Float(dt) / transientAttack))
        first = output
    }
}
