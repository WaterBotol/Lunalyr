import SwiftUI

private struct LyricReduceMotionOverride: EnvironmentKey {
    static let defaultValue: Bool? = nil
}

extension EnvironmentValues {
    /// Offscreen rendering supplies the system preference explicitly.
    var lyricReduceMotion: Bool {
        get { self[LyricReduceMotionOverride.self] ?? accessibilityReduceMotion }
        set { self[LyricReduceMotionOverride.self] = newValue }
    }
}

enum WordArrival: String, CaseIterable, Identifiable {
    case automatic, fade, lift, pop, orbit, glide, unfold, cutoutFold, cutoutSlide, cutoutHighlight, satinRibbon, splitInk, mix
    static var mixChoices: [Self] { allCases.filter { $0 != .automatic && $0 != .mix } }
    var id: String { rawValue }
    var title: String {
        switch self {
        case .automatic: return "Match the style"
        case .fade: return "Soft focus"
        case .lift: return "Float up"
        case .pop: return "Pop"
        case .orbit: return "Orbit"
        case .glide: return "Glide"
        case .unfold: return "Unfold"
        case .cutoutFold: return "Cutout · Fold"
        case .cutoutSlide: return "Cutout · Slide"
        case .cutoutHighlight: return "Cutout · Highlight"
        case .satinRibbon: return "Satin Ribbon"
        case .splitInk: return "Split Ink"
        case .mix: return "Custom mix"
        }
    }
    var isCutout: Bool { self == .cutoutFold || self == .cutoutSlide }
    /// "Cutout · Highlight": words enter as the layout has them enter, and
    /// each gets a cutout tile from its onset until the line ends
    /// (`LyricHighlightTiming`). This works whether the words are already
    /// visible or enter as they are sung.
    var highlightsSungWords: Bool { self == .cutoutHighlight }
    var isSwipe: Bool { self == .satinRibbon || self == .splitInk }
    var requiresReveal: Bool { isCutout || isSwipe }
    var travelRotation: Double { self == .orbit || self == .cutoutSlide ? 12 : 0 }

    func travelBounds(pointSize: CGFloat, active: Bool = true) -> CGSize {
        guard active else { return .zero }
        let horizontal: CGFloat = self == .orbit ? 0.32 : (self == .glide || self == .cutoutSlide ? 0.72 : 0)
        let vertical: CGFloat = self == .lift ? 0.48 : (self == .glide || self == .orbit ? 0.20 : 0)
        return CGSize(width: horizontal * pointSize * 2, height: vertical * pointSize * 2)
    }

    /// The entrance a word makes: "Cutout · Highlight" keeps the layout's
    /// own (what Match the style picks); every other entrance is its own.
    func motion(in composition: LyricComposition) -> WordArrival {
        highlightsSungWords ? WordArrival.automatic.resolved(for: composition) : self
    }

    func resolved(for composition: LyricComposition) -> WordArrival {
        guard self == .automatic else { return self }
        switch composition {
        case .scattered: return .pop
        case .constellation: return .orbit
        case .constellationEditorial, .echo: return .lift
        case .editorial: return .fade
        case .spoken: return .glide
        case .cascade: return .lift
        case .cutout: return .cutoutFold
        case .hinge, .broadside, .portal, .mosaic, .torque, .fable: return .fade
        }
    }
}

/// How a lyric line is composed on the screen.
///
/// The Lyric Speaker does not set every line the same way. It re-composes
/// around the words themselves: sometimes a web of connected fragments,
/// sometimes one phrase echoed at half a dozen sizes, sometimes an editorial
/// spread mixing two typefaces in a single line. These are those three
/// behaviours, plus the original scattered setting.
///
/// The original layouts share karaoke fill and emphasis in
/// `ScatteredLyricText`; the rolling layouts use `SpokenTypographyView`.
/// Both renderers share timestamp-derived word entrances and preview state.
enum LyricComposition: String, CaseIterable, Identifiable {
    case scattered, constellation, constellationEditorial, echo, editorial, spoken, cascade, cutout, hinge, broadside, portal, mosaic, torque, fable

    var id: String { rawValue }

    var title: String {
        switch self {
        case .scattered: return "Scattered"
        case .constellation: return "Constellation"
        case .constellationEditorial: return "Constellation + Editorial"
        case .echo: return "Echo"
        case .editorial: return "Editorial"
        case .spoken: return "Spoken"
        case .cascade: return "Cascade"
        case .cutout: return "Cutout"
        case .hinge: return "Hinge"
        case .broadside: return "Broadside"
        case .portal: return "Portal"
        case .mosaic: return "Mosaic"
        case .torque: return "Torque"
        case .fable: return "Fable"
        }
    }

    var detail: String {
        switch self {
        case .scattered: return "Words set loose across the line."
        case .constellation: return "Fragments linked by hairlines, like a star chart."
        case .constellationEditorial: return "Connected fragments with Editorial’s contrasting typefaces."
        case .echo: return "The phrase repeated at many sizes, some inverted."
        case .editorial: return "Two typefaces in one line, set like a spread."
        case .spoken: return "Each sung word takes focus; recent words settle around it."
        case .cascade: return "A moving column of words, arriving with the voice."
        case .cutout: return "Uneven type and album-colour labels, built word by word."
        case .hinge: return "Heavy words swing open on a spine, page by page, and the book snaps shut."
        case .broadside: return "One strip of big type that slides along, with little words as labels."
        case .portal: return "The sung word in a pill; earlier words drift away as pale ones."
        case .mosaic: return "A collage: each word set huge, on a colour bar, or letter by letter."
        case .torque: return "Words wrung into place with a thick edge, stacking upwards."
        case .fable: return "A new shape and accent every line; each word lands its own way."
        }
    }

    var hasConnections: Bool { self == .constellation || self == .constellationEditorial }
    var hasEditorialTypography: Bool { self == .editorial || self == .constellationEditorial }
    var arrivalOnly: Bool { self == .spoken || self == .cascade || self == .cutout }

    /// A remembered "automatic" choice takes precedence over a global layout.
    @MainActor
    static func selected(settings: Settings, remembered: String?, mood: TrackMood, seed: Int,
                         track: String? = nil) -> Self {
        if let remembered { return Self(rawValue: remembered) ?? matching(mood, seed: seed) }
        return settings.resolvedComposition(for: mood, seed: seed, track: track)
    }

    /// Which composition suits a track, when the choice is left automatic.
    ///
    /// Deliberately coarse: the composition changes the *reading* of a line,
    /// so switching on something as jittery as per-line word count would be
    /// restless. Energy and how played-by-hand the music is are stable for a
    /// whole track, which is the right granularity for this.
    static func matching(_ mood: TrackMood, seed: Int) -> LyricComposition {
        // Dense, mechanical music suits the linked web; sparse organic music
        // suits the echo; everything in between gets the editorial setting,
        // with scattered as the general-purpose fallback.
        if mood.energy > 0.68 && mood.organic < 0.45 { return .constellation }
        if mood.energy < 0.38 && mood.organic > 0.62 { return .echo }
        return abs(seed) % 2 == 0 ? .editorial : .scattered
    }
}

// MARK: - Assembled line

/// One lyric line, arranged by its composition.
///
/// The wallpaper and the settings preview both render through this, so a
/// preview cannot quietly drift from what actually appears on screen.
@MainActor
struct LyricLineView: View {
    let words: [LyricsFetcher.Word]
    let time: TimeInterval
    let stageSize: CGSize
    var baseSize: CGFloat = 44
    var mood: TrackMood = .neutral
    var composition: LyricComposition = .scattered
    var seed: Int = 0
    /// How present the line is, 0...1. Echo fades its repeats in with it.
    var presence: Double = 1
    var accent: Color = Color(hue: 0.16, saturation: 0.55, brightness: 0.64)
    var energy: Double = 0
    var readings: [TimedWordKey: String] = [:]
    var wordTimed = true
    var lineEnd: Double?
    var phraseSweep = 0.0
    /// When the line begins to leave; its words then exit one by one.
    var exitStart: Double = .infinity
    /// When the line's first word could first be previewed.
    var previewSince: Double = -.infinity
    /// Per word: continues the previous word's run (see `LyricRuns`).
    var glue: [Bool] = []
    /// Line-timed preview: draw only the first word, at this opacity.
    var firstRunOnly: Double?
    /// See `ScatteredLyricText.heldFirstRun` and `hidesFirstRun`.
    var heldFirstRun = false
    var hidesFirstRun = false
    /// Rolling stages: room kept at the foot, the onset after the window,
    /// and each word's place in the song (its colour).
    var bottomInset: CGFloat = 0
    var rollingHeightInTypeSizes: CGFloat = 7.2
    var following: Double?
    var ordinals: [TimedWordKey: Int] = [:]
    var rollingLineEnds: [TimedWordKey: Double] = [:]
    /// Rolling stages: the tokens each glued run is made of.
    var parts: [TimedWordKey: [LyricsFetcher.Word]] = [:]
    /// Bracketed words are lifted out as backing vocals. Credits and the
    /// song's title are not sung, so their brackets ("Song (Remix)",
    /// "(feat. …)") stay in the line.
    var liftsBackingVocals = true
    @StateObject private var echoText = RenderMemo<EchoPhrase, (text: String, rightToLeft: Bool)>()

    /// The words Echo repeats, and how they run together.
    struct EchoPhrase: Equatable {
        let words: [LyricsFetcher.Word]
        let glue: [Bool]
    }

    var body: some View {
        // Bracketed backing vocals are lifted out and drawn apart, behind the
        // lead (see BackingVocals). Only word-timed lines are split.
        let split = Self.liftsBacking(wordTimed: wordTimed, firstRunOnly: firstRunOnly, allowed: liftsBackingVocals)
            ? BackingVocals.split(words, glue: glue) : nil
        if let split, !split.backing.isEmpty {
            ZStack {
                BackingVocalLayer(groups: split.backing, time: time, stageSize: stageSize, baseSize: baseSize,
                                  seed: seed, composition: composition, mood: mood)
                lineBody(words: split.main, glue: split.mainGlue)
            }
        } else {
            lineBody(words: words, glue: glue)
        }
    }

    /// Whether a line's bracketed words are drawn apart as backing vocals:
    /// only word-timed lines, not a first-run preview, with the setting on.
    static func liftsBacking(wordTimed: Bool, firstRunOnly: Double?, allowed: Bool,
                             settingOn: Bool = BackingVocals.isOn) -> Bool {
        allowed && wordTimed && firstRunOnly == nil && settingOn
    }

    @ViewBuilder
    private func lineBody(words: [LyricsFetcher.Word], glue: [Bool]) -> some View {
        ZStack {
            if composition.fillsStage {
                StageLyricView(words: words, glue: glue, time: time, stageSize: stageSize, baseSize: baseSize, mood: mood,
                               composition: composition, seed: seed, exitStart: exitStart, firstRunOnly: firstRunOnly,
                               readings: readings)
            } else if composition.arrivalOnly {
                SpokenTypographyView(words: words, time: time, baseSize: baseSize, mood: mood,
                                     composition: composition, seed: seed, accent: accent, energy: energy, readings: readings,
                                     stageHeight: stageSize.height, stageHeightInTypeSizes: rollingHeightInTypeSizes, bottomInset: bottomInset, following: following,
                                     ordinals: ordinals, parts: parts, lineEnd: lineEnd, lineEnds: rollingLineEnds)
            } else {
              ScatteredLyricText(
                words: words,
                stageWidth: stageSize.width,
                time: time,
                baseSize: baseSize,
                style: .forLine(seed),
                mood: mood,
                composition: composition,
                seed: seed,
                energy: energy,
                readings: readings,
                wordTimed: wordTimed,
                lineEnd: lineEnd,
                phraseSweep: phraseSweep,
                exitStart: exitStart,
                previewSince: previewSince,
                glue: glue,
                firstRunOnly: firstRunOnly,
                heldFirstRun: heldFirstRun,
                hidesFirstRun: hidesFirstRun
              )
              // Echo lays repeats of the whole phrase behind the live line,
              // stepped off its top and bottom edges, so it needs the
              // line's height.
              .background {
                  if composition == .echo {
                      GeometryReader { proxy in
                          echoBackdrop(words: words, glue: glue, lineHeight: proxy.size.height)
                              .frame(width: proxy.size.width, height: proxy.size.height)
                      }
                  }
              }
            }
        }
    }
    private func echoBackdrop(words: [LyricsFetcher.Word], glue: [Bool], lineHeight: CGFloat) -> some View {
        let reveals = wordTimed && Settings.shared.revealsWords(in: composition)
        // Set as the line reads: glued characters and syllables together,
        // as the live line shows them.
        let phrase = echoText.value(for: EchoPhrase(words: words, glue: glue)) {
            (LyricRuns.text(words, glue: glue), LyricScript.lineIsRightToLeft(words.map(\.text)))
        }
        return EchoBackdrop(
            text: phrase.text,
            revealed: reveals ? EchoBackdrop.revealed(words, at: time) : 1,
            rightToLeft: phrase.rightToLeft,
            font: { Settings.shared.lyricFont(size: $0, hero: false, mood: mood) },
            baseSize: baseSize,
            stageSize: stageSize,
            seed: seed,
            presence: presence,
            time: time,
            lineHeight: lineHeight
        )
        .frame(width: 0, height: 0)
    }
}

// MARK: - Constellation

/// Where each word landed, collected from the layout so the linking lines
/// can be drawn between real positions rather than guessed ones.
struct WordAnchorKey: PreferenceKey {
    static let defaultValue: [Int: Anchor<CGRect>] = [:]
    static func reduce(value: inout [Int: Anchor<CGRect>],
                       nextValue: () -> [Int: Anchor<CGRect>]) {
        value.merge(nextValue()) { _, new in new }
    }
}

/// The hairline web drawn between words.
///
/// SwiftUI will not tell you where a laid-out child ended up, so each word
/// publishes its bounds through a preference and the lines are drawn in an
/// overlay once every position is known. That is why this is a separate pass
/// rather than something the layout itself could do.
@MainActor
struct ConstellationLines: View {
    @StateObject private var connectionCache = ConnectionGeometryCache()
    let anchors: [Int: Anchor<CGRect>]
    let count: Int
    let seed: Int
    var visibility: [Double] = []
    var emphasis: [Double] = []
    var colour: Color = .white
    /// Each slot's clearance around its ink: links end at the letters, not
    /// at the slot's edge.
    var insets: [CGSize] = []

    var body: some View {
        let opacity = Settings.shared.constellationLinkOpacity
        return GeometryReader { proxy in
            Canvas { context, size in
                // Anchors belong to fixed slots outside time-driven transforms.
                // The graph stays identical as words enter, fill and swell.
                // A word with no usable box yet (it has just been laid out, or
                // has zero size mid-transition) is left out; the others keep
                // their links instead of every link vanishing for a frame.
                var keys: [Int] = []
                var boxes: [VCBox] = []
                for index in 0..<count {
                    guard let anchor = anchors[index] else { continue }
                    let inset = insets.indices.contains(index) ? insets[index] : .zero
                    let rect = proxy[anchor].insetBy(dx: inset.width, dy: inset.height)
                    guard rect.width.isFinite, rect.height.isFinite, rect.width > 0, rect.height > 0 else { continue }
                    keys.append(index)
                    boxes.append(VCBox(x: Double(rect.minX), y: Double(rect.minY),
                                       width: Double(rect.width), height: Double(rect.height)))
                }
                guard boxes.count > 1, boxes.count <= 64 else { return }
                guard opacity > 0 else { return }
                let viewport = VCBox(x: 0, y: 0, width: Double(size.width), height: Double(size.height))
                let links = connectionCache.segments(boxes: boxes, viewport: viewport)
                for edge in links {
                    let a = keys[Int(edge.from)], b = keys[Int(edge.to)]
                    let shown = visibility.isEmpty ? 1 : min(visibility[a], visibility[b])
                    // Use the same continuous vocal envelope as the glyphs.
                    // A binary active-word switch made links flash at every
                    // onset/end even though their geometry never changed.
                    let heat = emphasis.isEmpty ? 0 : max(emphasis[a], emphasis[b])
                    let alpha = min(opacity * (1 + 0.45 * heat), 0.75) * shown
                    var path = Path()
                    path.move(to: CGPoint(x: CGFloat(edge.ax), y: CGFloat(edge.ay)))
                    path.addLine(to: CGPoint(x: CGFloat(edge.bx), y: CGFloat(edge.by)))
                    context.stroke(path, with: .color(colour.opacity(alpha)),
                                   style: StrokeStyle(lineWidth: 0.75, lineCap: .round))
                    for point in [CGPoint(x: CGFloat(edge.ax), y: CGFloat(edge.ay)), CGPoint(x: CGFloat(edge.bx), y: CGFloat(edge.by))] {
                        let dot = Path(ellipseIn: CGRect(x: point.x - 1.0, y: point.y - 1.0, width: 2, height: 2))
                        context.fill(dot, with: .color(colour.opacity(alpha)))
                    }
                }
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

// MARK: - Echo

/// The phrase repeated behind itself at descending sizes, a few copies
/// inverted.
///
/// The inversion is the detail that makes it read as the Lyric Speaker
/// rather than as a drop shadow: on the real display the repeats are mirrored
/// through the horizontal, so the line appears reflected in the glass.
struct EchoBackdrop: View {
    /// The whole line. Ghosts are always laid out from all of it and revealed
    /// through a mask: setting only the sung words re-centred every ghost at
    /// each onset, so the whole backdrop jumped sideways word by word.
    let text: String
    /// How much of the line has been sung, 0...1, weighted by characters.
    var revealed: Double = 1
    var rightToLeft = false
    let font: (CGFloat) -> Font
    let baseSize: CGFloat
    let stageSize: CGSize
    let seed: Int
    /// Rises as the line is sung, so the echoes arrive with the vocal.
    let presence: Double
    /// Drives a slow, seeded drift so the reflections never sit frozen.
    var time: Double = 0
    /// The live line's height: the repeats step away from its edges.
    var lineHeight: CGFloat = 0
    @Environment(\.lyricReduceMotion) private var reduceMotion
    @Environment(\.lyricInk) private var ink

    private struct Ghost {
        var scale: CGFloat
        var dx: CGFloat
        var dy: CGFloat
        var angle: Double
        var opacity: Double
        var flipped: Bool
    }

    /// The share of the line revealed so far: each word counts by its
    /// length and arrives on its own reveal curve, so this never steps.
    static func revealed(_ words: [LyricsFetcher.Word], at time: Double) -> Double {
        var total = 0.0, shown = 0.0
        for word in words {
            let weight = Double(max(1, word.text.count))
            total += weight
            shown += weight * vc_settle(time - word.start, min(0.46, max(0.16, (word.end - word.start) * 0.65)))
        }
        return total > 0 ? shown / total : 1
    }

    /// Each repeat's drift seed. Never traps: any seed works, however large
    /// or negative (a seed's multiple used to overflow into a negative
    /// number, which `UInt64` refuses).
    static func wanderSeed(_ seed: Int, slot: Int) -> UInt64 {
        UInt64(seed.magnitude) &* 31 &+ UInt64(slot.magnitude)
    }

    /// Fixed rather than random: a line must set the same way every frame,
    /// and `TimelineView` rebuilds this view continuously.
    ///
    /// The repeats recede in steps from the live line: smaller and fainter
    /// with each step away, rising above it, and mirrored below it as a
    /// reflection in glass. The earlier set scattered five copies around the
    /// line at random-looking places and near-invisible strengths, which
    /// read as a smudge rather than an echo. `dx` and `dy` are in ems of the
    /// base size; `dx` is mirrored by the seed, so lines alternate the side
    /// the echo drifts to.
    /// `dy` is measured from the line's nearer edge (negative above). Two
    /// strengths only: the repeat next to the line, and those further off.
    private static let variants: [Ghost] = [
        .init(scale: 0.62, dx: 0.30, dy: -0.48, angle: 0, opacity: 0.18, flipped: false),
        .init(scale: 0.42, dx: 0.62, dy: -1.16, angle: 0, opacity: 0.08, flipped: false),
        .init(scale: 0.28, dx: 0.90, dy: -1.66, angle: 0, opacity: 0.08, flipped: false),
        .init(scale: 0.78, dx: 0.0, dy: 0.58, angle: 0, opacity: 0.18, flipped: true),
        .init(scale: 0.52, dx: -0.30, dy: 1.44, angle: 0, opacity: 0.08, flipped: true),
    ]

    var body: some View {
        // The seed decides which way the echo drifts, so two lines in a row
        // do not arrange themselves identically.
        let side: CGFloat = seed.isMultiple(of: 2) ? 1 : -1
        let drift = reduceMotion ? 0.0 : 1.0
        let tint = ink.sample(slot: 0.5, seed: UInt64(seed.magnitude)).rest
        let strength = presence * Settings.shared.echoStrength
        ZStack {
            ForEach(0..<Self.variants.count, id: \.self) { slot in
                let ghost = Self.variants[slot]
                let wander = Self.wanderSeed(seed, slot: slot)
                let x = baseSize * ghost.dx * side + CGFloat(drift * vc_noise2(wander, time * 0.09)) * baseSize * 0.08
                let edge = (ghost.dy < 0 ? -lineHeight : lineHeight) / 2
                let y = edge + baseSize * ghost.dy + CGFloat(drift * vc_noise2(wander ^ 0x5EED, time * 0.07)) * baseSize * 0.03
                // Kept inside the title-safe area, 5% in from every edge.
                let stage = CGSize(width: stageSize.width * 0.90, height: stageSize.height * 0.90)
                let centre = CGPoint(x: stage.width / 2 + baseSize * ghost.dx * side, y: stage.height / 2 + edge + baseSize * ghost.dy)
                Text(text)
                    .font(font(baseSize * ghost.scale))
                    .foregroundStyle(tint.opacity(ghost.opacity * strength))
                    .lineLimit(1)
                    .fixedSize()
                    .mask(LyricSweepMask(progress: revealed, rightToLeft: rightToLeft, feather: 0.2))
                    .visualEffect { ink, geometry in
                        ink.scaleEffect(WordStageFit.scale(ink: geometry.size, stage: stage,
                            centre: centre, scale: 1.02, angle: ghost.angle, margin: 4))
                    }
                    .rotationEffect(.degrees(ghost.angle + drift * vc_noise(wander ^ 0xA9, time * 0.11) * 0.4))
                    .scaleEffect(x: 1, y: ghost.flipped ? -1 : 1)
                    .offset(x: x, y: y)
            }
        }
        .allowsHitTesting(false)
    }
}


// MARK: - A new layout for every song

/// With the layout on Match the music, each new song takes the next layout in
/// a rotation you set up: which layouts take part, and how many songs in a
/// row each one gets (0 leaves it out). Layouts that don't suit a song's
/// energy are skipped for that song. A song keeps its layout for as long as
/// the app runs, so replaying it looks the same. "Change layout every song"
/// off brings back the older choice by energy alone.
@MainActor
final class LyricLayoutRotation: ObservableObject {
    static let shared = LyricLayoutRotation()
    static let enabledKey = "custom.rotateLayouts"
    private static let countsKey = "custom.rotateLayoutCounts"
    private static let cursorKey = "custom.rotateLayoutsCursor"
    static let maximumCount = 5

    /// The order songs step through: contrasting looks sit next to each other.
    static let order: [LyricComposition] = [
        .cutout, .hinge, .constellation, .broadside, .editorial, .portal, .spoken, .mosaic, .echo, .torque, .cascade,
        .fable, .scattered, .constellationEditorial,
    ]

    private let defaults: UserDefaults
    private var assigned: [String: LyricComposition] = [:]
    private var cursor: Int

    /// Songs in a row per layout; a layout missing here gets 1.
    @Published private(set) var counts: [LyricComposition: Int]

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        cursor = defaults.integer(forKey: Self.cursorKey)
        let saved = defaults.dictionary(forKey: Self.countsKey) as? [String: Int] ?? [:]
        counts = Dictionary(uniqueKeysWithValues: saved.compactMap { key, value in
            LyricComposition(rawValue: key).map { ($0, min(max(value, 0), Self.maximumCount)) }
        })
    }

    var isEnabled: Bool {
        defaults.object(forKey: Self.enabledKey) as? Bool ?? true
    }

    func count(for layout: LyricComposition) -> Int { counts[layout] ?? 1 }

    func setCount(_ value: Int, for layout: LyricComposition) {
        counts[layout] = min(max(value, 0), Self.maximumCount)
        defaults.set(Dictionary(uniqueKeysWithValues: counts.map { ($0.key.rawValue, $0.value) }), forKey: Self.countsKey)
    }

    /// The layouts in turn, each as many times as its count says.
    var sequence: [LyricComposition] {
        Self.order.flatMap { Array(repeating: $0, count: count(for: $0)) }
    }

    /// Layouts that read well for a song of this energy: slow songs skip the
    /// fast, busy ones, and the busiest songs skip the slowest.
    static func suits(_ layout: LyricComposition, mood: TrackMood) -> Bool {
        switch layout {
        case .cascade, .constellation, .constellationEditorial: return mood.energy >= 0.3
        case .echo: return mood.energy <= 0.8
        default: return true
        }
    }

    func layout(for track: String, mood: TrackMood) -> LyricComposition {
        if let existing = assigned[track] { return existing }
        let turns = sequence
        guard !turns.isEmpty else {
            // Every layout is switched off: fall back to the energy match.
            return LyricComposition.matching(mood, seed: Self.stableSeed(track))
        }
        var pick = turns[(cursor + 1) % turns.count], at = (cursor + 1) % turns.count
        for step in 1...turns.count {
            let index = (cursor + step) % turns.count
            if Self.suits(turns[index], mood: mood) { pick = turns[index]; at = index; break }
        }
        cursor = at
        defaults.set(cursor, forKey: Self.cursorKey)
        assigned[track] = pick
        return pick
    }

    private static func stableSeed(_ text: String) -> Int { TrackMood.seed(title: text, artist: "") }

    /// For tests.
    func reset() {
        assigned = [:]
        cursor = 0
        counts = [:]
        defaults.removeObject(forKey: Self.cursorKey)
        defaults.removeObject(forKey: Self.countsKey)
    }
}

/// Layouts gathered by the feel they give a song, for the pickers.
enum LyricCompositionGroup: String, CaseIterable, Identifiable {
    case calm = "Calm", bold = "Bold", playful = "Playful"
    var id: String { rawValue }

    var layouts: [LyricComposition] {
        switch self {
        case .calm: return [.scattered, .constellation, .constellationEditorial, .editorial, .spoken]
        case .bold: return [.echo, .cascade, .hinge, .broadside, .torque]
        case .playful: return [.cutout, .portal, .mosaic, .fable]
        }
    }
}

/// The layout choices in the pickers, under a heading for each group.
struct LyricCompositionOptions: View {
    var body: some View {
        ForEach(LyricCompositionGroup.allCases) { group in
            Section(group.rawValue) {
                ForEach(group.layouts) { Text($0.title).tag(Optional($0)) }
            }
        }
    }
}
