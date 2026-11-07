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
        /// Set on the opening "Title - Artist" cue, which is drawn with the
        /// artist picked out.
        var title: String? = nil
        var artist: String? = nil
        /// From this time the artist's words are tiled with Cutout · Highlight.
        var highlightFrom: Double? = nil
    }

    /// Time between one credit arriving and the next, and how long each stays.
    static let stagger = 8.0
    static let minimumHold = 11.0
    static let fadeIn = 0.5
    static let fadeOut = 1.5

    /// One cue per credit line. Credits that share a time (or have none to
    /// speak of) arrive one after another instead of piling up.
    static func cues(in lines: [LyricsFetcher.Line], title: String = "", artist: String = "") -> [Cue] {
        var cues: [Cue] = []
        var cursor = 0.0
        // The song's title and artist open every song, once, drawn here:
        // the lyric file's own headings, bare names and repeated credits
        // are dropped in its favour.
        let names = LyricCredits.TrackNames(title: title, artist: artist)
        var credits: [LyricsFetcher.Line] = []
        var seenText = Set<String>()
        for line in lines where line.credit == true {
            guard !names.isHeading(line.text), !names.isLoneName(line.text) else { continue }
            guard seenText.insert(LyricQuery.normalize(line.text)).inserted else { continue }
            credits.append(line)
        }
        let trimmedTitle = title.trimmingCharacters(in: .whitespacesAndNewlines)
        let artistName = artist.trimmingCharacters(in: .whitespacesAndNewlines)
        let hasHeading = !trimmedTitle.isEmpty
        if hasHeading {
            let heading = artistName.isEmpty ? trimmedTitle : "\(trimmedTitle) - \(artistName)"
            credits.insert(LyricsFetcher.Line(start: 0.4, end: 0.41, text: heading, words: [], isInstrumental: true, credit: true), at: 0)
        }
        for (position, line) in credits.enumerated() {
            let text = line.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty, line.start.isFinite else { continue }
            let start = max(line.start, cursor)
            cursor = start + stagger
            let spell = min(3.5, 0.2 * Double(text.count))
            // From its last word arriving to leaving takes half what it used to.
            let hold = 0.2 + max(0.4, spell) + (minimumHold + min(5.0, Double(text.count) * 0.12) - 0.2 - max(0.4, spell)) / 2
            let opening = hasHeading && position == 0
            var highlightFrom: Double?
            var words = LyricsFetcher.distributeWords(in: text, from: start + 0.2, to: start + 0.2 + max(0.4, spell))
            if opening, !artistName.isEmpty {
                // The title is sung through, then the artist's words light up
                // and stay lit for as long as the cue is up.
                let titleEnd = start + 0.2 + max(0.4, spell) * 0.55
                let titleWords = LyricsFetcher.distributeWords(in: trimmedTitle, from: start + 0.2, to: titleEnd)
                let artistWords = LyricsFetcher.distributeWords(in: artistName, from: titleEnd + 0.15, to: titleEnd + 0.15 + max(0.4, spell) * 0.45)
                let dash = LyricsFetcher.Word(text: "-", start: titleEnd, end: titleEnd + 0.15)
                words = titleWords + [dash] + artistWords
                highlightFrom = artistWords.first?.start
            }
            guard !words.isEmpty else { continue }
            cues.append(Cue(id: cues.count, text: text, start: start, end: start + hold, words: words,
                            title: opening ? trimmedTitle : nil, artist: opening && !artistName.isEmpty ? artistName : nil,
                            highlightFrom: highlightFrom))
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
        // Thin bands along the top and bottom edges, clear of the middle
        // of the stage where the lyric sits.
        let box = CGSize(width: stageSize.width * 0.7, height: stageSize.height * 0.16)
        ZStack {
            ForEach(cues) { cue in
                let level = LyricCreditCues.level(of: cue, at: time)
                if level > 0.004 {
                    LyricLineView(words: cue.words, time: time, stageSize: box, baseSize: baseSize * 0.72, mood: mood,
                                  composition: style, seed: seed &+ cue.id, presence: 1,
                                  lineEnd: cue.end, exitStart: cue.end, highlightFrom: cue.highlightFrom)
                        .frame(width: box.width, height: box.height)
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

