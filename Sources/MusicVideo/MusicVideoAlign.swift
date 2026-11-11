import Foundation

// Lining the music video up by hand: in align mode the desktop player's
// seek row becomes the video's own bar, and dragging it moves only the
// video while the song plays on. The rules live here as plain values; the
// controller keeps the state and the sync loop does the seeking.

/// Whether the video can be lined up by hand just now, and if not, why.
enum MusicVideoAlignAvailability: Equatable {
    case available
    case unavailable(String)

    var isAvailable: Bool { self == .available }

    var reason: String? {
        if case .unavailable(let reason) = self { return reason }
        return nil
    }

    static let noVideo = "No music video is showing for this song"
    static let reduceMotion = "Reduce Motion shows a still of the video, so it can\u{2019}t be lined up"
    static let notPlaying = "Play the song to line up its video"
    static let notResponding = "The video isn\u{2019}t playing yet"

    /// - Parameters:
    ///   - showing: a video shows for the playing song (the wallpaper is
    ///     Music video and one was found).
    ///   - reduceMotion: the wallpaper shows the video's still instead.
    ///   - songPlaying: the song plays (a paused video can't be judged).
    ///   - responding: the video's player reported its position lately, so
    ///     it can be sought.
    static func of(showing: Bool, reduceMotion: Bool, songPlaying: Bool, responding: Bool) -> Self {
        guard showing else { return .unavailable(noVideo) }
        if reduceMotion { return .unavailable(Self.reduceMotion) }
        if !songPlaying { return .unavailable(notPlaying) }
        if !responding { return .unavailable(notResponding) }
        return .available
    }
}

/// The video's bar: its whole length, with the song's span laid over it
/// where the offset puts the song inside the video, and a knob for where the
/// video is now. Points from the bar's leading edge.
enum MusicVideoAlignBar {
    struct Layout: Equatable {
        /// The song's span inside the video, clipped to the bar.
        var spanStart: Double
        var spanEnd: Double
        /// Where the video is now.
        var knob: Double
    }

    /// The video's length as drawn: its player's report, else just long
    /// enough for the song at its offset.
    static func videoLength(reported: Double?, songDuration: Double, offset: Double) -> Double {
        if let reported, reported.isFinite, reported > 0 { return reported }
        let song = songDuration.isFinite && songDuration > 0 ? songDuration : 0
        let ahead = offset.isFinite ? max(0, offset) : 0
        return max(1, song + ahead)
    }

    static func x(time: Double, length: Double, width: Double) -> Double {
        guard length > 0, width > 0, time.isFinite else { return 0 }
        return min(max(time / length, 0), 1) * width
    }

    static func layout(videoLength: Double, songDuration: Double, offset: Double, songTime: Double,
                       width: Double) -> Layout {
        let offset = offset.isFinite ? offset : 0
        let song = songDuration.isFinite ? max(0, songDuration) : 0
        return Layout(spanStart: x(time: offset, length: videoLength, width: width),
                      spanEnd: x(time: offset + song, length: videoLength, width: width),
                      knob: x(time: (songTime.isFinite ? songTime : 0) + offset, length: videoLength, width: width))
    }

    /// Seconds of video per point of the bar at full speed.
    static func secondsPerPoint(videoLength: Double, width: Double) -> Double {
        guard videoLength.isFinite, videoLength > 0, width > 0 else { return 0 }
        return videoLength / width
    }

    /// The offsets a drag may reach: the knob stays inside the video (from
    /// its start to just before its end, where it would hold still), and an
    /// offset already outside that is kept, never pulled in by the press.
    static func bounds(videoLength: Double, songTime: Double, current: Double) -> ClosedRange<Double> {
        let range = MusicVideoTiming.offsetRange
        let song = songTime.isFinite ? songTime : 0
        var lower = max(range.lowerBound, -song)
        var upper = min(range.upperBound, max(lower, videoLength - endGuard - song))
        if current.isFinite {
            lower = min(lower, current)
            upper = max(upper, current)
        }
        return lower...upper
    }

    /// The knob never goes quite to the video's end: there it holds still.
    static let endGuard = 0.5

    /// "Video +3.4 s".
    static func title(offset: Double) -> String { "Video " + MusicVideoTiming.format(offset) }

    /// For VoiceOver: "The video runs 3.4 s ahead of the song".
    static func spoken(offset: Double) -> String {
        guard abs(offset) >= 0.05 else { return "The video runs with the song" }
        let amount = String(format: "%.1f", abs(offset))
        return offset > 0 ? "The video runs \(amount) s ahead of the song" : "The video runs \(amount) s behind the song"
    }
}

/// One press on the video's bar, from mouse-down to release. The video
/// keeps playing under the hand: a drag moves the offset by the pointer's
/// travel (times the fine-scrub gain), not the knob to the pointer, so a
/// hand held still keeps the offset and the video plays on in step. A press
/// away from the knob first brings the knob there.
struct MusicVideoAlignDrag: Equatable {
    /// A press this close to the knob grabs it where it is.
    static let grabRadius: Double = 10

    let offsetAtPress: Double
    let secondsPerPoint: Double
    let bounds: ClosedRange<Double>
    private(set) var offset: Double
    private(set) var gain: Double
    private(set) var travel: Double = 0
    private var lastX: Double
    private var lastY: Double
    private var atEnd: Bool

    init(x: Double, y: Double, knobX: Double, offset: Double, secondsPerPoint: Double,
         bounds: ClosedRange<Double>, option: Bool = false) {
        offsetAtPress = offset
        self.secondsPerPoint = secondsPerPoint.isFinite ? max(0, secondsPerPoint) : 0
        self.bounds = bounds
        gain = ScrubMath.gain(verticalDistance: y, fine: option)
        lastX = x.isFinite ? x : knobX
        lastY = y.isFinite ? y : 0
        var start = offset
        if x.isFinite, abs(x - knobX) > Self.grabRadius {
            start = offset + (x - knobX) * self.secondsPerPoint
        }
        self.offset = min(max(start, bounds.lowerBound), bounds.upperBound)
        atEnd = false
    }

    /// The pointer moved. True when the knob has just reached an end of
    /// the video (one alignment haptic).
    @discardableResult
    mutating func move(x: Double, y: Double, option: Bool = false) -> Bool {
        guard x.isFinite, y.isFinite else { return false }
        let dx = x - lastX
        travel += abs(dx) + abs(y - lastY)
        gain = ScrubMath.gain(verticalDistance: y, fine: option)
        offset = min(max(offset + dx * gain * secondsPerPoint, bounds.lowerBound), bounds.upperBound)
        lastX = x
        lastY = y
        let end = offset <= bounds.lowerBound || offset >= bounds.upperBound
        defer { atEnd = end }
        return end && !atEnd && dx != 0
    }

    /// ⌥ went down or up without the pointer moving.
    mutating func modifiersChanged(option: Bool) {
        gain = ScrubMath.gain(verticalDistance: lastY, fine: option)
    }

    /// "Half speed", "Quarter speed", "Fine", or nil at full speed.
    var rateLabel: String? { ScrubMath.rateLabel(gain: gain) }

    /// Whether the release has anything to keep.
    var changed: Bool { abs(offset - offsetAtPress) >= 0.005 }
}

/// Seeks the video while a drag moves it: as soon as the offset has
/// moved, but no more often than its player handles well (AVPlayer often,
/// YouTube's player seldom: each of its seeks has to buffer). A move that
/// comes too soon is sent by a later check, so the last position is always
/// reached. No drift correction meanwhile: the corrector rests until the
/// release, then resumes with the new offset.
struct MusicVideoAlignSeeker: Equatable {
    /// An offset change smaller than this is not worth a seek.
    static let minimumChange = 0.03

    static func interval(_ style: MusicVideoCorrectionStyle) -> TimeInterval {
        switch style {
        case .rate: return 0.08
        case .seek: return 0.2
        case .ledSeek: return 0.4
        }
    }

    let style: MusicVideoCorrectionStyle
    /// The offset the last seek went to (or the one in force at the press).
    private(set) var seekedOffset: Double
    private(set) var lastSeek: TimeInterval

    init(style: MusicVideoCorrectionStyle, offset: Double, lastSeek: TimeInterval = -.infinity) {
        self.style = style
        seekedOffset = offset
        self.lastSeek = lastSeek
    }

    /// - Parameters:
    ///   - target: where the video should be, nil outside its span.
    ///   - lead: added to a seek target while playing (the player's latency).
    mutating func decide(offset: Double, target: Double?, sample: MusicVideoDrift.Sample, shouldPlay: Bool,
                         now: TimeInterval, lead: Double) -> MusicVideoCorrector.Action {
        guard let target else { return sample.videoPlaying ? .pause : .none }
        if abs(offset - seekedOffset) >= Self.minimumChange {
            guard now - lastSeek >= Self.interval(style) else { return .none }
            seekedOffset = offset
            lastSeek = now
            return .seek(to: max(0, target + (shouldPlay ? lead : 0)), thenPlay: shouldPlay)
        }
        if shouldPlay { return sample.videoPlaying || sample.buffering ? .none : .play }
        return sample.videoPlaying ? .pause : .none
    }
}

/// What a key does while the video is being lined up (the player's panel
/// has the keyboard): the arrows move the video a tenth of a second (a
/// second with ⇧), Return keeps it, Esc cancels. Anything else is the
/// player's as usual.
enum MusicVideoAlignKey: Equatable {
    case nudge(Double)
    case done
    case cancel

    static let step = 0.1
    static let bigStep = 1.0

    static func action(keyCode: UInt16, shift: Bool, option: Bool, command: Bool, control: Bool,
                       editingText: Bool) -> MusicVideoAlignKey? {
        if keyCode == PlayerKeyMap.KeyCode.escape { return .cancel }
        guard !command, !control, !editingText else { return nil }
        switch keyCode {
        case PlayerKeyMap.KeyCode.left where !option: return .nudge(shift ? -bigStep : -step)
        case PlayerKeyMap.KeyCode.right where !option: return .nudge(shift ? bigStep : step)
        case PlayerKeyMap.KeyCode.returnKey, PlayerKeyMap.KeyCode.enter: return option ? nil : .done
        default: return nil
        }
    }
}

/// Align mode, as the controller keeps it: the song and video it is for,
/// and what to put back if it is cancelled.
struct MusicVideoAlignSession: Equatable {
    var songKey: String
    var video: String
    /// The user's own offset before align mode, nil when there was none
    /// (Esc goes back to the estimate then).
    var restore: MusicVideoOffsets.ManualEntry?
}

/// What the video's bar draws at one moment.
struct MusicVideoAlignDisplay: Equatable {
    var videoLength: Double
    var songDuration: Double
    var offset: Double
    var songTime: Double
    var origin: MusicVideoOffsetOrigin
    /// A press is in flight, and its speed below full ("Fine").
    var dragging = false
    var rateLabel: String?
    /// The offset was set in this align mode (a drag, a key).
    var touched = false
    var available = true

    var videoTime: Double { min(max(songTime + offset, 0), videoLength) }

    func layout(width: Double) -> MusicVideoAlignBar.Layout {
        MusicVideoAlignBar.layout(videoLength: videoLength, songDuration: songDuration, offset: offset,
                                  songTime: songTime, width: width)
    }

    var title: String { MusicVideoAlignBar.title(offset: offset) }

    /// The small line under the title: the drag's speed, else a hint
    /// before anything is moved, else where the offset came from.
    var detail: String {
        if dragging { return rateLabel ?? "Release to keep" }
        if !touched {
            switch origin {
            case .manual: return "Set by you \u{00B7} drag to adjust"
            default: return "Drag until the picture matches the music"
            }
        }
        switch origin {
        case .manual: return "Kept for this video"
        case .estimated: return "Estimated"
        case .none: return "No offset"
        }
    }
}
