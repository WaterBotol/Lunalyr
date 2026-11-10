import AppKit
import Foundation

// Two sync aids for the music video:
// - Brief listening: a library video with no offset yet runs System audio's
//   capture for a few seconds, so its soundtrack can be lined up with the
//   song (`MusicVideoAudioAlign`). Only when capture's permission is known
//   to be granted, never with a prompt; the audio is analysed on this Mac
//   and nothing is kept.
// - Tap to sync: the user taps when the singing starts in the (muted)
//   video, and the offset is worked out from the song's timed lyrics.

// MARK: - Brief listening

/// Runs the audio capture briefly for the audio alignment. A seam: the live
/// one drives `AudioReactiveController`; tests pass a fake.
@MainActor
protocol MusicVideoBriefListening: AnyObject {
    /// Whether a brief listen may start now: music is playing, power
    /// settings are not sparing the Mac, and capture's permission is already
    /// granted (nothing will ask for it).
    var mayListen: Bool { get }
    /// Starts capture for at most `seconds`; false when it could not.
    func start(for seconds: TimeInterval) -> Bool
    /// Stops it again (capture returns to what the visualisers need).
    func stop()
}

@MainActor
final class LiveMusicVideoListener: MusicVideoBriefListening {
    var mayListen: Bool {
        guard NowPlayingMonitor.shared.isPlaying, Settings.shared.powerConstraint == nil else { return false }
        return AudioReactiveController.shared.mayListenBriefly
    }

    func start(for seconds: TimeInterval) -> Bool {
        AudioReactiveController.shared.listenBriefly(for: seconds)
    }

    func stop() {
        AudioReactiveController.shared.endBriefListen()
    }
}

// MARK: - Tap to sync

/// Where the tap-to-sync flow is.
enum MusicVideoTapState: Equatable {
    case idle
    /// Waiting for the tap. `firstLine`: armed before the song's first
    /// sung line, so the tap marks where the singing starts; otherwise it
    /// marks the next line sung in the video.
    case armed(firstLine: Bool)
    /// The offset now in force (the user's), from `taps` taps averaged.
    case done(offset: Double, taps: Int, firstLine: Bool)
    case failed(MusicVideoTapFailure)
}

enum MusicVideoTapFailure: Equatable {
    /// The song's timed lyrics have not loaded (or it has none).
    case noLyrics
    /// The song is paused, so there is nothing to time the tap against.
    case notPlaying
}

enum MusicVideoTapSync {
    /// How late a tap lands after what it marks: reaction time.
    ///
    /// Longer than the lyric Tap to sync's (`LyricTapSync.reactionTime`,
    /// 0.15) on purpose: this is one tap reacting to a line seen on screen,
    /// a plain visual reaction, while the lyric one is tapped along to the
    /// song again and again, and a rhythm anticipated lands closer to the beat.
    static let reactionTime = 0.2
    /// The tap is waited for this long before it is given up.
    static let armedLimit: TimeInterval = 120

    /// Whether arming now (the song at `songTime`) is before the first sung line.
    static func armedBeforeFirstLine(songTime: Double, lines: [MusicVideoLyricLine]) -> Bool {
        guard let first = lines.map(\.start).min() else { return false }
        return songTime < first
    }

    /// The line the tap marks. Armed before the first line: the first line.
    /// Otherwise the line starting nearest `reference` (the song time the
    /// video shows at the tap), the next one or the one just passed.
    static func line(in lines: [MusicVideoLyricLine], reference: Double, firstLine: Bool) -> MusicVideoLyricLine? {
        let sorted = lines.sorted { $0.start < $1.start }
        guard let first = sorted.first else { return nil }
        if firstLine || reference <= first.start { return first }
        let passed = sorted.last { $0.start <= reference }
        let upcoming = sorted.first { $0.start > reference }
        switch (passed, upcoming) {
        case let (passed?, upcoming?):
            return upcoming.start - reference < reference - passed.start ? upcoming : passed
        case let (passed?, nil): return passed
        case let (nil, upcoming?): return upcoming
        case (nil, nil): return nil
        }
    }

    /// The offset (video time − song time) one tap gives: the video's time
    /// at the tap, less the reaction time, minus the song time of the line.
    static func offset(videoTimeAtTap: Double, lineStart: Double, reaction: Double = reactionTime) -> Double {
        videoTimeAtTap - reaction - lineStart
    }

    /// The taps so far, averaged, to a tenth of a second.
    static func averaged(_ taps: [Double]) -> Double? {
        guard !taps.isEmpty else { return nil }
        return (taps.reduce(0, +) / Double(taps.count) * 10).rounded() / 10
    }

    /// "Video starts 8.4 s ahead", "Video starts 2.0 s behind".
    static func describe(_ offset: Double) -> String {
        if abs(offset) < 0.05 { return "Video starts with the song" }
        return String(format: "Video starts %.1f s %@", abs(offset), offset > 0 ? "ahead" : "behind")
    }

    enum KeyAction: Equatable {
        /// Not for Tap to sync: the key goes where it was going.
        case pass
        case tap
        case cancel
    }

    /// What a key press does while armed. Only plain keys count, and never
    /// while a text field has focus (it is typing): Command, Control and
    /// Option shortcuts, modifier keys alone and repeats pass through. Esc
    /// cancels.
    static func keyAction(keyCode: UInt16, modifiers: NSEvent.ModifierFlags, isRepeat: Bool,
                          textInputFocused: Bool) -> KeyAction {
        if textInputFocused || isRepeat { return .pass }
        if !modifiers.intersection([.command, .control, .option]).isEmpty && keyCode != 53 { return .pass }
        if modifierKeyCodes.contains(keyCode) { return .pass }
        return keyCode == 53 ? .cancel : .tap
    }

    /// Shift, Control, Option, Command (both sides), Caps Lock and Fn.
    static let modifierKeyCodes: Set<UInt16> = [54, 55, 56, 57, 58, 59, 60, 61, 62, 63]

    /// The button's title for each state.
    static func buttonTitle(_ state: MusicVideoTapState) -> String {
        switch state {
        case .idle, .failed: return "Tap to sync"
        case .armed: return "Tap now"
        case .done: return "Tap again"
        }
    }

    /// The line under the button, if any.
    static func note(_ state: MusicVideoTapState) -> String? {
        switch state {
        case .idle:
            return nil
        case .armed(let firstLine):
            return firstLine
                ? "Watch the video. Press any key, or Tap now, the moment the singing starts in the video. Esc cancels."
                : "Watch the video. Press any key, or Tap now, when the next line is sung in the video. Esc cancels."
        case .done(let offset, let taps, let firstLine):
            let from = firstLine ? "from where the singing starts" : "from the next line sung in the video"
            let more = taps == 1 ? " Tap again to refine." : " Averaged over \(taps) taps."
            return "\(describe(offset)), \(from).\(more)"
        case .failed(.noLyrics):
            return "Tap to sync needs this song’s timed lyrics, and they haven’t loaded."
        case .failed(.notPlaying):
            return "Play the song first: the tap is timed against it."
        }
    }
}
