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
    /// speak of) arrive one after another instead of piling up, and none
    /// before `notBefore` (the song's opening title, `OpeningTitle`, has
    /// the screen until then).
    static func cues(in lines: [LyricsFetcher.Line], title: String = "", artist: String = "",
                     notBefore: Double = 0) -> [Cue] {
        var cues: [Cue] = []
        var cursor = notBefore
        // The song's own title and artist are the opening title's: the lyric
        // file's headings, bare names and repeated credits are dropped.
        let names = LyricCredits.TrackNames(title: title, artist: artist)
        var seenText = Set<String>()
        for line in lines where line.credit == true {
            guard !names.isHeading(line.text), !names.isLoneName(line.text) else { continue }
            guard seenText.insert(LyricQuery.normalize(line.text)).inserted else { continue }
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

    /// 0 while the cue is up, then 0...1 as it leaves.
    static func exitProgress(of cue: Cue, at time: Double) -> Double {
        time <= cue.end ? 0 : Easing.smoothstep((time - cue.end) / fadeOut)
    }

    /// The edge bands the cues hold, and when (`CreditsLayer` places even
    /// cues at the top, odd ones at the foot).
    static func bands(_ cues: [Cue]) -> [LyricCreditBand] {
        cues.map { LyricCreditBand(start: $0.start, end: $0.end + fadeOut, top: $0.id.isMultiple(of: 2)) }
    }
}

/// A screen band a credit cue holds for a while; backing vocals keep to
/// the other band meanwhile (`BackingVocalLayer`).
struct LyricCreditBand: Equatable {
    var start: Double
    var end: Double
    var top: Bool
}

private struct LyricCreditBandsKey: EnvironmentKey {
    static let defaultValue: [LyricCreditBand] = []
}

extension EnvironmentValues {
    /// The credit cues on screen around this line, for backing vocals.
    var lyricCreditBands: [LyricCreditBand] {
        get { self[LyricCreditBandsKey.self] }
        set { self[LyricCreditBandsKey.self] = newValue }
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

    /// Credits are not sung: "Remix by (someone)" keeps its brackets in the
    /// line instead of the bracket being drawn small and apart as a backing vocal.
    static let liftsBackingVocals = false

    var body: some View {
        let style = BackingVocals.style(forLead: composition)
        // Thin bands along the top and bottom edges, clear of the middle
        // of the stage where the lyric sits.
        let box = CGSize(width: stageSize.width * 0.7, height: stageSize.height * 0.16)
        ZStack {
            ForEach(cues) { cue in
                let level = LyricCreditCues.level(of: cue, at: time)
                if level > 0.004 {
                    LyricLineView(words: cue.words, time: time, stageSize: box, baseSize: baseSize * 0.72, mood: mood,
                                  composition: style, seed: seed &+ cue.id, presence: 1,
                                  lineEnd: cue.end, exitStart: cue.end,
                                  liftsBackingVocals: CreditsLayer.liftsBackingVocals)
                        .frame(width: box.width, height: box.height)
                        .layerExit(.credit(towardTop: cue.id.isMultiple(of: 2)),
                                   progress: LyricCreditCues.exitProgress(of: cue, at: time), size: box.height)
                        .opacity(level)
                        .shadow(color: .black.opacity(0.3 * level), radius: 6)
                        // High and low in turn, so one arriving as the last
                        // leaves never sits on it.
                        .position(x: stageSize.width * 0.5,
                                  y: stageSize.height * (cue.id.isMultiple(of: 2) ? 0.1 : 0.9))
                }
            }
        }
        .frame(width: stageSize.width, height: stageSize.height)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

