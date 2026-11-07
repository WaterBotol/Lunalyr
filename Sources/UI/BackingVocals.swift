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
            let joined = LyricRuns.text(current, glue: currentGlue)
            let text = joined.filter { !opens.contains($0) && !closes.contains($0) }
                .trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty, let first = current.first, let last = current.last {
                groups.append(Group(id: groups.count, text: text, start: first.start, end: last.end))
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

/// The backing groups of one line, drawn small, dim and behind the lead.
struct BackingVocalLayer: View {
    let groups: [BackingVocals.Group]
    let time: Double
    let stageSize: CGSize
    let baseSize: CGFloat
    let seed: Int
    let composition: LyricComposition
    let mood: TrackMood
    @Environment(\.lyricInk) private var ink
    @Environment(\.lyricReduceMotion) private var reduceMotion

    /// How long a group is held at least, however brief the singing was.
    private static let minimumHold = 1.1
    private static let fadeIn = 0.45
    private static let fadeOut = 0.9

    var body: some View {
        ZStack {
            ForEach(groups) { group in
                let level = Self.level(of: group, at: time)
                if level > 0.004 {
                    let sample = ink.sample(slot: unit(group.id, 7), seed: UInt64(abs(seed) &+ group.id))
                    Text(verbatim: group.text)
                        .font(font)
                        .foregroundStyle(sample.rest)
                        .opacity(0.62 * level)
                        .scaleEffect(0.94 + 0.06 * min(1, level * 1.6))
                        .blur(radius: reduceMotion ? 0 : (1 - level) * 3)
                        .position(position(of: group))
                        .shadow(color: .black.opacity(0.3 * level), radius: 6)
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

    private var font: Font {
        switch composition {
        case .editorial, .constellationEditorial:
            return .system(size: baseSize * 0.48, weight: .regular, design: .serif).italic()
        case .cutout:
            return .system(size: baseSize * 0.42, weight: .semibold, design: .rounded)
        default:
            return Settings.shared.lyricFont(size: baseSize * 0.46, hero: false, mood: mood)
        }
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
        let x = 0.16 + 0.68 * unit(group.id, 1)
        let y = top ? 0.10 + 0.14 * unit(group.id, 2) : 0.78 + 0.14 * unit(group.id, 3)
        let drift = reduceMotion ? 0 : sin(time * 0.45 + Double(group.id)) * Double(baseSize) * 0.09
        return CGPoint(x: stageSize.width * x, y: stageSize.height * y + drift)
    }
}
