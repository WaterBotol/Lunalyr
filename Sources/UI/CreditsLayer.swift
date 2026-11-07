import SwiftUI

// MARK: - Production credits as their own background layer
//
// Lyric services publish credits ("Produced by: …", "作词 : …") as timed rows
// ahead of the lyric. They are not sung, so the lead layer skips them
// (`Line.credit`), but they are worth showing: each one is introduced in the
// background while the song plays on, animated word by word like a lyric in
// the layout chosen for backing vocals, at the lead's own size and brightness
// (unlike backing vocals, which are small and dim).

enum LyricCreditCues {
    static let settingKey = "custom.creditsLayer"

    static var isOn: Bool { UserDefaults.standard.object(forKey: settingKey) as? Bool ?? true }

    struct Cue: Identifiable, Equatable {
        var id: Int
        var text: String
        var start: Double
        var end: Double
        var words: [LyricsFetcher.Word]
    }

    /// Time between one credit arriving and the next, and how long each stays.
    static let stagger = 8.0
    static let minimumHold = 11.0
    static let fadeIn = 0.5
    static let fadeOut = 1.5

    /// One cue per credit line. Credits that share a time (or have none to
    /// speak of) arrive one after another instead of piling up.
    static func cues(in lines: [LyricsFetcher.Line]) -> [Cue] {
        var cues: [Cue] = []
        var cursor = 0.0
        for line in lines where line.credit == true {
            let text = line.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty, line.start.isFinite else { continue }
            let start = max(line.start, cursor)
            cursor = start + stagger
            let spell = min(3.5, 0.2 * Double(text.count))
            // From its last word arriving to leaving takes half what it used to.
            let hold = 0.2 + max(0.4, spell) + (minimumHold + min(5.0, Double(text.count) * 0.12) - 0.2 - max(0.4, spell)) / 2
            let words = LyricsFetcher.distributeWords(in: text, from: start + 0.2, to: start + 0.2 + max(0.4, spell))
            guard !words.isEmpty else { continue }
            cues.append(Cue(id: cues.count, text: text, start: start, end: start + hold, words: words))
        }
        return cues
    }

    /// 0...1: eases in, holds, then fades out slowly.
    static func level(of cue: Cue, at time: Double) -> Double {
        if time < cue.start { return 0 }
        if time < cue.start + fadeIn { return Easing.smoothstep((time - cue.start) / fadeIn) }
        if time <= cue.end { return 1 }
        return 1 - Easing.smoothstep((time - cue.end) / fadeOut)
    }
}

struct CreditsLayer: View {
    let cues: [LyricCreditCues.Cue]
    let time: Double
    let stageSize: CGSize
    let baseSize: CGFloat
    let seed: Int
    let composition: LyricComposition
    let mood: TrackMood

    var body: some View {
        let style = BackingVocals.style(forLead: composition)
        let box = CGSize(width: stageSize.width * 0.7, height: stageSize.height * 0.3)
        ZStack {
            ForEach(cues) { cue in
                let level = LyricCreditCues.level(of: cue, at: time)
                if level > 0.004 {
                    LyricLineView(words: cue.words, time: time, stageSize: box, baseSize: baseSize, mood: mood,
                                  composition: style, seed: seed &+ cue.id, presence: 1,
                                  lineEnd: cue.end, exitStart: cue.end)
                        .frame(width: box.width, height: box.height)
                        .opacity(level)
                        .shadow(color: .black.opacity(0.3 * level), radius: 6)
                        // High and low in turn, so one arriving as the last
                        // leaves never sits on it.
                        .position(x: stageSize.width * 0.5,
                                  y: stageSize.height * (cue.id.isMultiple(of: 2) ? 0.2 : 0.8))
                }
            }
        }
        .frame(width: stageSize.width, height: stageSize.height)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}
