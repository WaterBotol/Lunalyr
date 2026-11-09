import Foundation

// MARK: - How long a lyric card's MP4 lasts
//
// With a video background longer than two seconds the MP4 can last as long
// as the video (the default), with the lines on their own timing inside it:
// lines that end early hold their last one (fading it over the last half
// second when more than three seconds are left), and lines that run past the
// end are cut there, with a note. Or it can last as long as the lines, the
// way it always did, with the background looped (its seam crossfaded) or cut.
// Pictures, GIFs and very short clips (Live Photos) always follow the lines.
// GIF and PNG exports are not affected.

/// What decides an MP4's length.
enum ClipLength: String, CaseIterable, Identifiable {
    case matchBackground, matchLyrics
    var id: String { rawValue }
    var title: String {
        switch self {
        case .matchBackground: return "Match background"
        case .matchLyrics: return "Match lyrics"
        }
    }
}

/// Where in a looping background a moment falls. Each pass after the first
/// starts `crossfade` seconds before the one before it ends, and the two are
/// blended across that overlap.
struct LoopTiming: Equatable {
    var duration: Double
    var crossfade: Double

    var period: Double { max(0.001, duration - crossfade) }

    /// The frame to show at `time`, and, inside a seam, the previous pass's
    /// frame with how much of the new one to show (0 to 1).
    func sample(at time: Double) -> (position: Double, previous: Double?, weight: Double) {
        guard duration > 0 else { return (0, nil, 1) }
        let time = max(0, time)
        let pass = (time / period).rounded(.down)
        let position = time - pass * period
        if pass >= 1, crossfade > 0, position < crossfade {
            return (position, period + position, position / crossfade)
        }
        return (min(position, duration), nil, 1)
    }

    /// When the seams start (each crossfade runs from here for `crossfade` seconds).
    func seams(before end: Double) -> [Double] {
        guard crossfade > 0 else { return [] }
        return stride(from: period, to: end, by: period).map { $0 }
    }
}

extension LyricCard {
    /// A background this short or shorter is treated like a picture or GIF.
    static let shortBackground = 2.0
    /// Held lines fade out only when more than this is left after them.
    static let holdFadeAfter = 3.0
    static let lyricFadeOut = 0.5
    static let loopCrossfade = 0.3
    static let shareCap = 60.0

    /// One page as the card plays it.
    struct PageClip {
        var page: Page
        var start: Double
        var firstInk: Double?
        /// How long the page is on screen, including `tail`.
        var duration: Double
        /// The rest after the last page's last word.
        var tail: Double
    }

    /// Each page's timing; shared by the frames, the song's windows and the length.
    static func pageClips(_ content: Content) -> [PageClip] {
        let movingBackground = content.background?.isAnimated == true
        return content.pages.enumerated().map { index, page in
            let tail = index == content.pages.count - 1 ? 0.32 : 0
            if let animation = page.animation, let firstInk = animation.firstInk {
                let start = min(firstInk, max(animation.line.start, firstInk - 0.12))
                let span = animation.end - start
                if span.isFinite, span > 0 { return PageClip(page: page, start: start, firstInk: firstInk, duration: span + tail, tail: tail) }
            }
            return PageClip(page: page, start: 0, firstInk: nil,
                            duration: content.pages.count == 1 ? (movingBackground ? 3.0 : 0.08) : 1.8, tail: 0)
        }
    }

    /// The MP4's length and what happens at its end.
    struct ClipPlan: Equatable {
        /// The rule in force (a short or still background always follows the lines).
        var length: ClipLength
        /// Whether the choice applies, so the control is shown.
        var offersChoice: Bool
        /// How long the MP4 lasts.
        var duration: Double
        /// How long the selected lines take, with the closing rest.
        var lyricsDuration: Double
        /// Lines that end after the MP4 does.
        var linesCut: Int
        /// The 60-second cap is what leaves lines out.
        var capped: Bool
        /// How a moving background repeats; nil for a picture.
        var backgroundLoop: LoopTiming?

        var holdsLastLine: Bool { duration > lyricsDuration + 0.01 }
        var fadesLastLine: Bool { duration - lyricsDuration > LyricCard.holdFadeAfter }

        /// How much of the lyrics shows at `time` (1 except while a held line fades).
        func lyricOpacity(at time: Double) -> Double {
            guard fadesLastLine else { return 1 }
            return min(1, max(0, (duration - time) / LyricCard.lyricFadeOut))
        }

        /// Said under the control: the length, and what happens to the lines.
        var summary: String {
            if let note { return note }
            let length = String(format: "%.1f s", duration)
            if fadesLastLine { return "\(length). The last line stays up, then fades out." }
            if holdsLastLine { return "\(length). The last line stays up to the end." }
            return length
        }

        /// Said under the control when lines don't fit.
        var note: String? {
            guard linesCut > 0 else { return nil }
            let lines = linesCut == 1 ? "1 line won’t fit" : "\(linesCut) lines won’t fit"
            if capped { return "\(lines) in 60 s; pick fewer lines or turn off Max 60 s" }
            return "\(lines); pick fewer lines or Match lyrics"
        }
    }

    static func clipPlan(_ content: Content) -> ClipPlan {
        let clips = pageClips(content)
        let lyrics = clips.reduce(0) { $0 + $1.duration }
        let background = content.background
        let video = background?.kind == .video ? background?.duration : nil
        let offersChoice = (video ?? 0) > shortBackground
        let length: ClipLength = offersChoice ? content.clipLength : .matchLyrics
        let natural = length == .matchBackground ? (video ?? lyrics) : lyrics
        let capped = content.capsLength && natural > shareCap
        let duration = capped ? shareCap : natural
        func cut(at end: Double) -> Int {
            var start = 0.0, count = 0
            for clip in clips {
                if start + clip.duration - clip.tail > end + 0.05 { count += 1 }
                start += clip.duration
            }
            return count
        }
        let linesCut = cut(at: duration)
        var loop: LoopTiming?
        if let background, background.isAnimated, background.duration > 0 {
            let loops = duration > background.duration + 0.01
            let fade = loops && background.kind == .video && background.duration >= 1 ? loopCrossfade : 0
            loop = LoopTiming(duration: background.duration, crossfade: fade)
        }
        return ClipPlan(length: length, offersChoice: offersChoice, duration: duration, lyricsDuration: lyrics,
                        linesCut: linesCut, capped: capped && cut(at: natural) < linesCut, backgroundLoop: loop)
    }
}
