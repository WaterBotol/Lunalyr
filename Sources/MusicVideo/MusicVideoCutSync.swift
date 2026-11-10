import CoreGraphics
import Foundation

// Cut-to-beat alignment for YouTube videos. Music videos are edited on the
// beat. While the muted video plays on the wallpaper, a few tiny frames a
// second are taken from Lunalyr's own web view; the moments the picture
// changes at once (cuts) are lined up with the onsets of the song as the
// System audio capture hears it, both on the same uptime clock. How long
// after its beat a cut shows says how late (or early) the video is.
//
// Beats repeat, so this can only tell where within a beat the video is,
// not which beat: it fine-tunes an offset that is already close (a tap, a
// right estimate, an upload as long as the song) by the few tenths of a
// second that players, clocks and seeks leave, and looks no further than
// `maximumLag` either way. Only a clear peak is believed: a video cut
// evenly to an even beat fits a beat earlier or later just as well and is
// refused, and both halves of the cuts must agree on the lag.
//
// Nothing is downloaded and nothing is heard: the frames are what the muted
// player already shows, the audio is the capture the user switched on, and
// both are analysed in memory and dropped.

enum MusicVideoCutAlign {
    /// One small frame of the video as it was on screen.
    struct Frame: Equatable, Sendable {
        /// Uptime when the frame was on screen.
        var time: TimeInterval
        /// A small grey grid of the picture, values 0...1 (`signature(of:)`).
        var signature: [Float]
    }

    /// A cut: the picture changed at once between two frames.
    struct Cut: Equatable, Sendable {
        /// It fell between these two frames (uptime).
        var from: TimeInterval
        var to: TimeInterval
        var strength: Float
    }

    struct Result: Equatable, Sendable {
        /// How long after its beat a cut shows, in seconds: positive, the
        /// video is late, and its offset grows by this much.
        var lag: Double
        /// Mean onset strength (standard scores) at the cuts, at `lag`…
        var peak: Float
        /// …and at the best lag clearly elsewhere.
        var runnerUp: Float
        var cuts: Int
    }

    enum Verdict: Equatable, Sendable {
        case found(Result)
        /// Enough cuts and audio, and no clear peak (an even beat, cuts off
        /// the beat): this video is not worth trying again soon.
        case unclear
        /// Too few cuts with audio around them yet: keep watching.
        case tooLittle
    }

    /// Onset frames per second, as `AudioEnvelopeRecorder` records them.
    static let rate = MusicVideoAudioAlign.rate
    /// How far either way the cuts are looked for around the beat: under
    /// two beats, so the search stays near the offset in force.
    static let maximumLag = 0.75
    /// Enough for two halves that each say something.
    static let minimumCuts = 8
    /// Seconds of frames before a verdict is given.
    static let minimumSpan = 12.0
    /// The peak must reach this mean standard score at the cuts…
    static let minimumPeak: Float = 0.35
    /// …and beat every lag more than `peakExclusion` away by this ratio
    /// (`MusicVideoAudioAlign`'s confidence rule).
    static let peakRatio: Float = MusicVideoAudioAlign.peakRatio
    /// Under the shortest beat that matters, over the peak's own width.
    static let peakExclusion = 0.22
    /// The odd and the even cuts, aligned apart, agree this closely.
    static let halvesAgree = 0.12
    /// Frames further apart than this do not place a cut well enough.
    static let maximumFrameGap = 0.5
    /// A cut changes the picture at least this much (mean grey change)…
    static let cutFloor: Float = 0.08
    /// …and this many times the usual change between frames.
    static let cutOverMedian: Float = 4
    /// The grid each frame is reduced to.
    static let gridWidth = 16
    static let gridHeight = 9

    // MARK: Frames

    /// A frame as a `gridWidth` × `gridHeight` grey grid, 0...1. Nil for a
    /// blank or unreadable image (a snapshot that caught no video).
    static func signature(of image: CGImage) -> [Float]? {
        let width = gridWidth, height = gridHeight
        var pixels = [UInt8](repeating: 0, count: width * height)
        let drawn: Bool = pixels.withUnsafeMutableBytes { buffer in
            guard let context = CGContext(data: buffer.baseAddress, width: width, height: height, bitsPerComponent: 8,
                                          bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(),
                                          bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return false }
            context.interpolationQuality = .medium
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        guard drawn else { return nil }
        let values = pixels.map { Float($0) / 255 }
        // All one level: nothing on screen yet (or a black snapshot).
        guard let low = values.min(), let high = values.max(), high - low > 0.02 else { return nil }
        return values
    }

    /// Mean absolute change between two grids.
    static func difference(_ a: [Float], _ b: [Float]) -> Float? {
        guard a.count == b.count, !a.isEmpty else { return nil }
        var sum: Float = 0
        for index in a.indices { sum += abs(a[index] - b[index]) }
        return sum / Float(a.count)
    }

    /// Where the picture changed at once: a change between two frames well
    /// over the usual one between frames, and over its neighbours. Frames
    /// too far apart place nothing.
    static func cuts(in frames: [Frame]) -> [Cut] {
        let sorted = frames.sorted { $0.time < $1.time }
        guard sorted.count >= 3 else { return [] }
        var changes: [(from: TimeInterval, to: TimeInterval, value: Float)?] = []
        for index in 1..<sorted.count {
            let a = sorted[index - 1], b = sorted[index]
            if b.time > a.time, b.time - a.time <= maximumFrameGap, let value = difference(a.signature, b.signature) {
                changes.append((a.time, b.time, value))
            } else {
                changes.append(nil)
            }
        }
        let values = changes.compactMap { $0?.value }.sorted()
        guard !values.isEmpty else { return [] }
        let median = values[values.count / 2]
        let threshold = max(cutFloor, cutOverMedian * median)
        var found: [Cut] = []
        for (index, change) in changes.enumerated() {
            guard let change, change.value >= threshold else { continue }
            let before = index > 0 ? changes[index - 1]?.value ?? 0 : 0
            let after = index + 1 < changes.count ? changes[index + 1]?.value ?? 0 : 0
            guard change.value > before, change.value >= after else { continue }
            found.append(Cut(from: change.from, to: change.to, strength: change.value))
        }
        return found
    }

    // MARK: Alignment

    /// The lag at which the cuts in `frames` land best on the onsets of
    /// `audio` (both on uptime), with the confidence rule applied.
    static func align(frames: [Frame], audio: [AudioEnvelopeRecorder.Frame],
                      maximumLag: Double = maximumLag) -> Verdict {
        guard let first = frames.map(\.time).min(), let last = frames.map(\.time).max(),
              last - first >= minimumSpan else { return .tooLittle }
        let allCuts = cuts(in: frames)
        // Hardly a cut in most of the frames kept: one long take, say.
        guard allCuts.count >= minimumCuts else {
            return last - first >= MusicVideoCutCollector.keep - 3 ? .unclear : .tooLittle
        }

        // The onsets on a grid at `rate`, from `maximumLag` (and a little)
        // before the first frame to as long after the last. Cells without
        // audio stay empty.
        let margin = maximumLag + 0.5
        let gridStart = first - margin
        let count = Int(((last + margin - gridStart) * rate).rounded(.up)) + 1
        var grid = [Float?](repeating: nil, count: count)
        let heard = audio.filter { $0.time >= gridStart - 1 && $0.time <= last + margin + 1 }.sorted { $0.time < $1.time }
        guard heard.count > 1 else { return .tooLittle }
        let onsets = MusicVideoAudioAlign.onsets(heard.map(\.energy))
        for (index, frame) in heard.enumerated() {
            // The first frame after a gap has no rise to measure.
            let afterGap = index == 0 || frame.time - heard[index - 1].time > 2.5 / rate
            let cell = Int(((frame.time - gridStart) * rate).rounded())
            guard grid.indices.contains(cell) else { continue }
            grid[cell] = afterGap ? 0 : onsets[index]
        }
        // Short holes (a frame's jitter) take their neighbour's value.
        for cell in grid.indices where grid[cell] == nil {
            if cell > 0, let left = grid[cell - 1], cell + 1 < grid.count, grid[cell + 1] != nil { grid[cell] = left }
        }
        let present = grid.compactMap { $0 }
        guard present.count > 10 else { return .tooLittle }
        // Standard scores, so "at a beat" means the same in a loud and a
        // quiet song.
        let mean = present.reduce(0, +) / Float(present.count)
        let spread = sqrt(present.reduce(0) { $0 + ($1 - mean) * ($1 - mean) } / Float(present.count))
        guard spread > 1e-6 else { return .tooLittle }
        var prefix = [Double](repeating: 0, count: count + 1)
        var covered = [Int](repeating: 0, count: count + 1)
        for cell in 0..<count {
            let z = grid[cell].map { Double(($0 - mean) / spread) } ?? 0
            prefix[cell + 1] = prefix[cell] + z
            covered[cell + 1] = covered[cell] + (grid[cell] == nil ? 0 : 1)
        }
        let lagCells = Int((maximumLag * rate).rounded())
        // Only cuts with audio all around them, at every lag tried.
        let boxes: [(a: Int, b: Int)] = allCuts.compactMap { cut in
            let a = Int(((cut.from - gridStart) * rate).rounded())
            let b = max(a, Int(((cut.to - gridStart) * rate).rounded()))
            let low = a - lagCells, high = b + lagCells
            guard low >= 0, high < count, covered[high + 1] - covered[low] == high - low + 1 else { return nil }
            return (a, b)
        }
        guard boxes.count >= minimumCuts else { return .tooLittle }

        // For each lag: the mean onset score over each cut's interval,
        // averaged over the cuts. A cut `lag` after its beat finds the
        // beat `lag` earlier.
        let exclusion = Int((peakExclusion * rate).rounded())
        let all = peak(scores(boxes, prefix: prefix, lagCells: lagCells), exclusion: exclusion)
        guard all.best >= minimumPeak, all.best >= peakRatio * max(all.runnerUp, 0.02) else { return .unclear }
        // A chance fit rarely holds for both halves of the cuts.
        let odd = peak(scores(boxes.enumerated().filter { $0.offset % 2 == 1 }.map(\.element), prefix: prefix,
                              lagCells: lagCells), exclusion: exclusion)
        let even = peak(scores(boxes.enumerated().filter { $0.offset % 2 == 0 }.map(\.element), prefix: prefix,
                               lagCells: lagCells), exclusion: exclusion)
        guard abs(odd.slot - all.slot) <= halvesAgree * rate, abs(even.slot - all.slot) <= halvesAgree * rate
        else { return .unclear }
        let lag = ((all.slot - Double(lagCells)) / rate * 100).rounded() / 100
        return .found(Result(lag: lag, peak: all.best, runnerUp: all.runnerUp, cuts: boxes.count))
    }

    /// The mean onset score inside the cuts' intervals at each lag, from
    /// `-lagCells` to `lagCells`.
    private static func scores(_ boxes: [(a: Int, b: Int)], prefix: [Double], lagCells: Int) -> [Float] {
        guard !boxes.isEmpty else { return [] }
        return (-lagCells...lagCells).map { lag in
            var total = 0.0
            for box in boxes {
                let a = box.a - lag, b = box.b - lag
                total += (prefix[b + 1] - prefix[a]) / Double(b - a + 1)
            }
            return Float(total / Double(boxes.count))
        }
    }

    /// The best lag (as a fractional slot: the centre of the peak, which is
    /// as wide as the gaps between frames), its score, and the best score
    /// more than `exclusion` slots away.
    private static func peak(_ scores: [Float], exclusion: Int) -> (slot: Double, best: Float, runnerUp: Float) {
        guard let bestSlot = scores.indices.max(by: { scores[$0] < scores[$1] }) else { return (0, 0, 0) }
        let best = scores[bestSlot]
        let runnerUp = scores.indices.filter { abs($0 - bestSlot) > exclusion }.map { scores[$0] }.max() ?? 0
        var low = bestSlot, high = bestSlot
        while low > 0, bestSlot - (low - 1) <= exclusion, scores[low - 1] >= 0.9 * best { low -= 1 }
        while high + 1 < scores.count, (high + 1) - bestSlot <= exclusion, scores[high + 1] >= 0.9 * best { high += 1 }
        return (Double(low + high) / 2, best, runnerUp)
    }
}

/// Collects the frames for one cut alignment, from the YouTube player's
/// sync checks: only while the video plays steadily on screen, starting
/// over after a seek, a pause or a change of offset (each moves the video
/// against the music). Gives up after `limit`.
struct MusicVideoCutCollector: Equatable {
    /// Seconds of frames kept: enough for a verdict, inside the half
    /// minute of audio the recorder keeps (less the lag searched).
    static let keep = 29.0
    /// A verdict is asked for first after this many seconds of frames…
    static let firstCheck = MusicVideoCutAlign.minimumSpan
    /// …then this often until one is given.
    static let checkEvery = 4.0
    /// The most time spent trying, from the first try.
    static let limit: TimeInterval = 50
    /// After a seek, the player is left this long to settle first.
    static let settle: TimeInterval = 1.5

    private(set) var frames: [MusicVideoCutAlign.Frame] = []
    private(set) var startedAt: TimeInterval?
    private var offset: Double?
    private var lastCheck: TimeInterval?
    /// The measured drift (video − target) at each frame, for the result.
    private var drifts: [Double] = []

    /// Whether a frame should be taken now. `steady`: the video plays on
    /// screen with the song.
    mutating func shouldCapture(now: TimeInterval, offset: Double, steady: Bool, lastSeek: TimeInterval) -> Bool {
        if startedAt == nil { startedAt = now }
        if !steady || now - lastSeek < Self.settle || self.offset.map({ abs($0 - offset) > 0.001 }) == true {
            restart()
            if steady { self.offset = offset }
            return false
        }
        self.offset = offset
        return true
    }

    mutating func add(_ frame: MusicVideoCutAlign.Frame, drift: Double?) {
        guard frame.signature.count == MusicVideoCutAlign.gridWidth * MusicVideoCutAlign.gridHeight else { return }
        frames.append(frame)
        if let drift, drift.isFinite { drifts.append(drift) }
        if let newest = frames.last?.time { frames.removeAll { newest - $0.time > Self.keep } }
        if drifts.count > frames.count { drifts.removeFirst(drifts.count - frames.count) }
    }

    /// The frames' span, in seconds.
    var span: Double {
        guard let first = frames.first?.time, let last = frames.last?.time else { return 0 }
        return last - first
    }

    /// Whether to ask for a verdict now.
    mutating func checkDue(now: TimeInterval) -> Bool {
        guard span >= Self.firstCheck else { return false }
        if let lastCheck, now - lastCheck < Self.checkEvery { return false }
        lastCheck = now
        return true
    }

    func expired(now: TimeInterval) -> Bool {
        startedAt.map { now - $0 >= Self.limit } ?? false
    }

    /// The offset the frames were taken under.
    var offsetUsed: Double? { offset }

    /// How far the video was from where the sync loop wanted it, on
    /// average, while the frames were taken.
    var meanDrift: Double {
        drifts.isEmpty ? 0 : drifts.reduce(0, +) / Double(drifts.count)
    }

    private mutating func restart() {
        frames = []
        drifts = []
        offset = nil
        lastCheck = nil
    }
}
