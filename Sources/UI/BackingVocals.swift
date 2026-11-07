import SwiftUI

// MARK: - Backing vocals as their own background layer
//
// Sources write backing vocals as bracketed words inside a line: "Hold me
// (oh oh) tonight". Drawn like the lead they flash past at full size, which
// reads as rushed. They are lifted out of the line here and drawn apart: small,
// dim and behind the lead, easing in, drifting a little, and staying a beat
// after they were sung. Only word-timed lines are split (a line-timed line has
// no word times to lift them by).

enum BackingVocals {
    static let settingKey = "custom.backingVocalsLayer"

    static var isOn: Bool { UserDefaults.standard.object(forKey: settingKey) as? Bool ?? true }

    /// A run of backing words sung together: "oh oh", "ooh".
    struct Group: Identifiable, Equatable {
        var id: Int
        var text: String
        var start: Double
        var end: Double
        /// The group's words without their brackets, to animate like any lyric.
        var words: [LyricsFetcher.Word] = []
        var glue: [Bool] = []
    }

    // MARK: Which layout the backing words animate in

    static let styleKey = "custom.backingVocalStyle"
    /// The layouts a backing group can animate in: the ones that set words
    /// loose or link them, not the three that roll a line in (Spoken,
    /// Cascade and Cutout), which would take over the stage.
    static let choices: [LyricComposition] = [.scattered, .constellation, .constellationEditorial, .echo, .editorial]

    /// The saved choice, or nil to follow the lead's layout.
    static var chosenStyle: LyricComposition? {
        UserDefaults.standard.string(forKey: styleKey).flatMap(LyricComposition.init(rawValue:)).flatMap { choices.contains($0) ? $0 : nil }
    }

    /// What a group animates in: the choice, else the lead's own layout,
    /// except that the rolling three become Editorial.
    static func style(forLead lead: LyricComposition, chosen: LyricComposition? = chosenStyle) -> LyricComposition {
        if let chosen { return chosen }
        return choices.contains(lead) ? lead : .editorial
    }

    struct Split {
        var main: [LyricsFetcher.Word]
        var mainGlue: [Bool]
        var backing: [Group]
    }

    private static let opens: Set<Character> = ["(", "（"]
    private static let closes: Set<Character> = [")", "）"]

    /// Separates bracketed backing words from the lead words. `glue` runs
    /// parallel to `words`. A line made only of backing words keeps them all
    /// as backing, so the lead layer is simply empty for it.
    static func split(_ words: [LyricsFetcher.Word], glue: [Bool]) -> Split {
        var main: [LyricsFetcher.Word] = [], mainGlue: [Bool] = []
        var groups: [Group] = []
        var current: [LyricsFetcher.Word] = []
        var currentGlue: [Bool] = []
        var inside = false
        var removedBefore = false

        func flush() {
            guard !current.isEmpty else { return }
            var shown: [LyricsFetcher.Word] = [], shownGlue: [Bool] = []
            for (offset, word) in current.enumerated() {
                let text = word.text.filter { !opens.contains($0) && !closes.contains($0) }
                guard !text.trimmingCharacters(in: .whitespaces).isEmpty else { continue }
                shown.append(LyricsFetcher.Word(text: text, start: word.start, end: word.end))
                shownGlue.append(shown.count > 1 && currentGlue[offset])
            }
            if let first = shown.first, let last = shown.last {
                let text = LyricRuns.text(shown, glue: shownGlue).trimmingCharacters(in: .whitespacesAndNewlines)
                groups.append(Group(id: groups.count, text: text, start: first.start, end: last.end,
                                    words: shown, glue: shownGlue))
            }
            current = []
            currentGlue = []
        }

        for (index, word) in words.enumerated() {
            let joins = glue.indices.contains(index) ? glue[index] : false
            let opening = word.text.contains { opens.contains($0) }
            let closing = word.text.contains { closes.contains($0) }
            if opening { inside = true }
            if inside {
                current.append(word)
                currentGlue.append(current.count > 1 && joins)
                removedBefore = true
            } else {
                main.append(word)
                // The word after a lifted group no longer follows what it was glued to.
                mainGlue.append(removedBefore ? false : joins)
                removedBefore = false
            }
            if closing && inside {
                inside = false
                flush()
            }
        }
        flush()
        return Split(main: main, mainGlue: mainGlue, backing: groups)
    }
}

/// The backing groups of one line, animated like any lyric (in the chosen
/// layout) but small, dim and behind the lead.
struct BackingVocalLayer: View {
    let groups: [BackingVocals.Group]
    let time: Double
    let stageSize: CGSize
    let baseSize: CGFloat
    let seed: Int
    let composition: LyricComposition
    let mood: TrackMood
    @Environment(\.lyricReduceMotion) private var reduceMotion

    /// How long a group is held at least, however brief the singing was.
    private static let minimumHold = 1.1
    private static let fadeIn = 0.45
    private static let fadeOut = 0.9

    var body: some View {
        let style = BackingVocals.style(forLead: composition)
        let box = CGSize(width: stageSize.width * 0.6, height: stageSize.height * 0.26)
        ZStack {
            ForEach(groups) { group in
                let level = Self.level(of: group, at: time)
                if level > 0.004 {
                    let hold = max(group.end, group.start + Self.minimumHold)
                    LyricLineView(words: group.words, time: time, stageSize: box, baseSize: baseSize * 0.5, mood: mood,
                                  composition: style, seed: seed &+ group.id, presence: 1,
                                  lineEnd: hold, exitStart: hold, glue: group.glue)
                        .frame(width: box.width, height: box.height)
                        .opacity(0.62 * level)
                        .shadow(color: .black.opacity(0.3 * level), radius: 6)
                        .position(position(of: group))
                }
            }
        }
        .frame(width: stageSize.width, height: stageSize.height)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    /// 0...1: eases in over the first moments, holds, then fades out slowly.
    static func level(of group: BackingVocals.Group, at time: Double) -> Double {
        let start = group.start - 0.12
        let hold = max(group.end, group.start + minimumHold)
        if time < start { return 0 }
        if time < start + fadeIn { return Easing.smoothstep((time - start) / fadeIn) }
        if time <= hold { return 1 }
        return 1 - Easing.smoothstep((time - hold) / fadeOut)
    }

    /// A steady 0...1 per group and salt, so a group keeps its place.
    private func unit(_ index: Int, _ salt: Int) -> Double {
        var value = UInt64(truncatingIfNeeded: abs(seed) &* 2654435761 &+ index &* 40503 &+ salt &* 9973)
        value ^= value >> 13; value = value &* 0x2545F4914F6CDD1D; value ^= value >> 29
        return Double(value % 10_000) / 10_000
    }

    /// Up near the top or down near the foot, alternating, away from the centre
    /// where the lead sits, drifting slowly.
    private func position(of group: BackingVocals.Group) -> CGPoint {
        let top = (group.id + abs(seed)).isMultiple(of: 2)
        let x = 0.30 + 0.40 * unit(group.id, 1)
        let y = top ? 0.14 + 0.08 * unit(group.id, 2) : 0.80 + 0.07 * unit(group.id, 3)
        let drift = reduceMotion ? 0 : sin(time * 0.45 + Double(group.id)) * Double(baseSize) * 0.07
        return CGPoint(x: stageSize.width * x, y: stageSize.height * y + drift)
    }
}
