import Foundation

/// The two FFTs are independent; maximum band energy preserves stereo-only
/// instruments and avoids cancellation in opposite-phase recordings.
final class VisualizerAudio: @unchecked Sendable {
    static let shared = VisualizerAudio()
    private let left = AudioAnalyzer(), right = AudioAnalyzer()
    private let lock = NSLock()
    private var channelCount = 1
    func append(_ pcm: UnsafePointer<Float>, count: Int, sampleRate: Double, channel: Int) {
        (channel == 0 ? left : right).append(pcm, count: count, sampleRate: sampleRate)
    }
    func setChannelCount(_ count: Int) {
        lock.lock(); channelCount = count; lock.unlock()
    }
    func reset() {
        left.reset(); right.reset()
        setChannelCount(1)
    }
    func latest() -> AudioSnapshot {
        var a = left.latest()
        lock.lock(); let stereo = channelCount > 1; lock.unlock()
        guard stereo else { return a }
        let b = right.latest()
        for i in a.bands.indices { a.bands[i] = max(a.bands[i], b.bands[i]) }
        for i in a.components.indices { a.components[i] = max(a.components[i], b.components[i]) }
        if b.level > a.level { a.waveform = b.waveform; a.centroid = b.centroid }
        a.level = max(a.level, b.level); a.bass = max(a.bass, b.bass)
        a.mid = max(a.mid, b.mid); a.treble = max(a.treble, b.treble)
        a.beat = max(a.beat, b.beat); a.trebleBeat = max(a.trebleBeat, b.trebleBeat)
        a.flux = max(a.flux, b.flux)
        return a
    }
}

/// Two cascaded, analytically integrated poles. An input step begins with
/// zero velocity and cannot move the wallpaper abruptly between callbacks.
/// The elapsed-time solution gives the same motion at 15, 30 and 60 fps.
struct VisualizerSmoother {
    private var first = AudioSnapshot(), output = AudioSnapshot()
    private var lastTime: Double?
    mutating func frame(_ target: AudioSnapshot, at time: Double) -> AudioSnapshot {
        let dt = min(0.1, max(0, time - (lastTime ?? time)))
        lastTime = time
        for i in output.bands.indices {
            Self.follow(target.bands[i], first: &first.bands[i], output: &output.bands[i], dt: dt)
        }
        // A small spatial filter keeps adjacent FFT bins from making sharp
        // corners in the aurora or ridges in the tunnel wall.
        var spatial = output
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
        Self.follow(target.beat, first: &first.beat, output: &output.beat, dt: dt)
        Self.follow(target.trebleBeat, first: &first.trebleBeat, output: &output.trebleBeat, dt: dt)
        Self.follow(target.flux, first: &first.flux, output: &output.flux, dt: dt)
        spatial.components = output.components
        spatial.bass = output.bass; spatial.mid = output.mid; spatial.treble = output.treble
        spatial.level = output.level; spatial.centroid = output.centroid
        spatial.beat = output.beat; spatial.trebleBeat = output.trebleBeat; spatial.flux = output.flux
        // Raw waveform phase is unsuitable for slow desktop geometry. The
        // imported scenes use the stable spectrum instead (see SOURCE.md).
        spatial.waveform = [Float](repeating: 0, count: AudioSnapshot.waveformCount)
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
}
