import Foundation

/// The shader clock for one visualiser scene.
///
/// The wallpaper clock is a Double that grows for as long as Lunalyr runs,
/// but shaders receive a Float. After about two days of rendering a Float32
/// can no longer represent a 60 fps step, and frames alternate between
/// 0.0078 s and 0.0156 s steps. This timeline integrates the source clock's
/// steps instead of copying it, so:
///
/// - Reduce Motion's slower rate changes speed without jumping the scene
///   (the scene time used to be `clock × 0.15`, which jumped on a toggle);
/// - the origin returns to zero at a natural break (the scene being built,
///   a wake or long pause, music returning after silence, a new album
///   palette) once the scene time has passed `rebaseThreshold`, where a
///   Float still resolves half a millisecond. Continuous playback never jumps.
struct VisualizerTimeline: Sendable {
    /// Seconds after which the next natural break restarts the origin.
    static let rebaseThreshold: Double = 4_096
    /// Last resort for a break-free stream; Float32 still resolves 8 ms here.
    static let forcedRebase: Double = 65_536
    /// A wall-clock gap this long is a pause or a wake, not a frame.
    static let pauseGap: Double = 2
    /// The longest source step one frame may integrate. The wallpaper clock
    /// already clamps its own steps to this; a larger one means the scene
    /// was off screen, and it resumes where it was rather than leaping.
    static let maximumStep: Double = 0.25

    private(set) var time: Double = 0
    private var lastSource: Double?
    private var lastWallClock: Double?

    /// Advances by the source clock's step times `rate`. `wallClock` is a
    /// monotonic time used only to recognise pauses; `atBreak` marks a moment
    /// where a jump would not be noticed. Repeating the same source value
    /// (another display drawing the same frame) changes nothing.
    @discardableResult
    mutating func advance(to source: Double, rate: Double, wallClock: Double, atBreak: Bool = false) -> Double {
        let rate = rate.isFinite ? max(0, rate) : 1
        guard source.isFinite else { return time }
        guard let last = lastSource else {
            // A scene being built adopts the wallpaper clock, as it always
            // did, but being built is itself a break: a large clock restarts.
            lastSource = source
            lastWallClock = wallClock
            time = source * rate
            if time > Self.rebaseThreshold { time = 0 }
            return time
        }
        let paused = lastWallClock.map { wallClock - $0 > Self.pauseGap } ?? false
        lastWallClock = wallClock
        if source == last, !atBreak, !paused { return time }
        lastSource = source
        let step = source - last
        if atBreak || paused || step < 0, time > Self.rebaseThreshold {
            time = 0
            return time
        }
        // Never integrate a backwards step or one larger than a frame may take.
        time += min(Self.maximumStep, max(0, step)) * rate
        if time > Self.forcedRebase { time = 0 }
        return time
    }

    /// The value shaders receive.
    var shaderTime: Float { Float(time) }
}

/// One timeline per scene, shared by every surface showing it: each
/// display, and the copy that fades out during a scene change, stay in
/// phase, and a rebase on one is a rebase on all.
@MainActor
final class VisualizerTimelines {
    static let shared = VisualizerTimelines()
    private var timelines: [AudioVisualizerStyle: VisualizerTimeline] = [:]
    /// Star Trails' simulation, shared by every surface on these timelines.
    let starTrails = StarTrailsSimulations()

    func time(for style: AudioVisualizerStyle, source: Double, rate: Double,
              wallClock: Double, atBreak: Bool) -> Double {
        timelines[style, default: VisualizerTimeline()]
            .advance(to: source, rate: rate, wallClock: wallClock, atBreak: atBreak)
    }
}
