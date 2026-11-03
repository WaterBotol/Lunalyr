import SwiftUI

enum WordArrival: String, CaseIterable, Identifiable {
    case automatic, fade, lift, pop, orbit, glide, unfold
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
        }
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
        case .cutout: return .unfold
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
    case scattered, constellation, constellationEditorial, echo, editorial, spoken, cascade, cutout

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
        }
    }

    var hasConnections: Bool { self == .constellation || self == .constellationEditorial }
    var hasEditorialTypography: Bool { self == .editorial || self == .constellationEditorial }
    var arrivalOnly: Bool { self == .spoken || self == .cascade || self == .cutout }

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
    var baseSize: CGFloat = 44
    var mood: TrackMood = .neutral
    var composition: LyricComposition = .scattered
    var seed: Int = 0
    /// How present the line is, 0...1. Echo fades its repeats in with it.
    var presence: Double = 1
    var accent: Color = Color(hue: 0.16, saturation: 0.55, brightness: 0.64)
    var energy: Double = 0

    var body: some View {
        ZStack {
            // Echo lays repeats of the whole phrase behind the live line, so
            // it has to sit under the words rather than beside them.
            if composition == .echo {
                EchoBackdrop(
                    text: words.filter { !Settings.shared.revealsWords(in: composition) || time >= $0.start }
                        .map(\.text).joined(separator: " "),
                    font: { Settings.shared.lyricFont(size: $0, hero: false, mood: mood) },
                    baseSize: baseSize,
                    seed: seed,
                    presence: presence
                )
                .frame(width: 0, height: 0)
            }
            if composition.arrivalOnly {
                SpokenTypographyView(words: words, time: time, baseSize: baseSize, mood: mood,
                                     composition: composition, seed: seed, accent: accent, energy: energy)
            } else {
              ScatteredLyricText(
                words: words,
                time: time,
                baseSize: baseSize,
                style: .forLine(seed),
                mood: mood,
                composition: composition,
                seed: seed,
                energy: energy
              )
            }
        }
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
    let activeIndex: Int?
    let seed: Int
    var visibility: [Double] = []

    var body: some View {
        let opacity = Settings.shared.constellationLinkOpacity
        return GeometryReader { proxy in
            Canvas { context, size in
                // Anchors belong to fixed slots outside time-driven transforms.
                // The graph stays identical as words enter, fill and swell.
                let keys = (0..<count).filter { anchors[$0] != nil }
                let boxes = keys.compactMap { index -> VCBox? in
                    guard let anchor = anchors[index] else { return nil }
                    let rect = proxy[anchor]
                    guard rect.width.isFinite, rect.height.isFinite,
                          rect.width > 0, rect.height > 0 else { return nil }
                    return VCBox(x: Double(rect.minX), y: Double(rect.minY),
                                 width: Double(rect.width), height: Double(rect.height))
                }
                guard boxes.count == keys.count, boxes.count > 1, boxes.count <= 64 else { return }
                guard opacity > 0 else { return }
                let viewport = VCBox(x: 0, y: 0, width: Double(size.width), height: Double(size.height))
                let links = connectionCache.segments(boxes: boxes, viewport: viewport)
                for edge in links {
                    let lit = keys[Int(edge.from)] == activeIndex || keys[Int(edge.to)] == activeIndex
                    let a = keys[Int(edge.from)], b = keys[Int(edge.to)]
                    let shown = visibility.isEmpty ? 1 : min(visibility[a], visibility[b])
                    let alpha = min(opacity * (lit ? 1.45 : 1), 0.75) * shown
                    var path = Path()
                    path.move(to: CGPoint(x: CGFloat(edge.ax), y: CGFloat(edge.ay)))
                    path.addLine(to: CGPoint(x: CGFloat(edge.bx), y: CGFloat(edge.by)))
                    context.stroke(path, with: .color(.white.opacity(alpha)),
                                   style: StrokeStyle(lineWidth: 0.75, lineCap: .round))
                    for point in [CGPoint(x: CGFloat(edge.ax), y: CGFloat(edge.ay)), CGPoint(x: CGFloat(edge.bx), y: CGFloat(edge.by))] {
                        let dot = Path(ellipseIn: CGRect(x: point.x - 1.0, y: point.y - 1.0, width: 2, height: 2))
                        context.fill(dot, with: .color(.white.opacity(alpha)))
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
    let text: String
    let font: (CGFloat) -> Font
    let baseSize: CGFloat
    let seed: Int
    /// Rises as the line is sung, so the echoes arrive with the vocal.
    let presence: Double

    private struct Ghost {
        var scale: CGFloat
        var dx: CGFloat
        var dy: CGFloat
        var angle: Double
        var opacity: Double
        var flipped: Bool
    }

    /// Fixed rather than random: a line must set the same way every frame,
    /// and `TimelineView` rebuilds this view continuously.
    private var ghosts: [Ghost] {
        // Every repeat sits well under the live line and well away from the
        // centre. The first pass had them at full size and a fifth opacity,
        // which made them read as five competing lines rather than as an
        // echo of one -- the live lyric was the hardest thing on screen to
        // find. Nothing here is legible enough to be mistaken for the line
        // being sung.
        let variants: [Ghost] = [
            .init(scale: 0.85, dx: -0.30, dy: -0.62, angle: -2.0, opacity: 0.055, flipped: true),
            .init(scale: 0.42, dx: 0.46, dy: -0.44, angle: 1.5, opacity: 0.085, flipped: false),
            .init(scale: 0.30, dx: -0.50, dy: 0.48, angle: -1.0, opacity: 0.10, flipped: false),
            .init(scale: 0.60, dx: 0.34, dy: 0.66, angle: 2.5, opacity: 0.065, flipped: true),
            .init(scale: 0.22, dx: 0.10, dy: -0.78, angle: 0.0, opacity: 0.12, flipped: false),
        ]
        // The seed rotates which variant leads, so two lines in a row do not
        // arrange themselves identically.
        let offset = abs(seed) % variants.count
        return Array(variants[offset...] + variants[..<offset])
    }

    var body: some View {
        ZStack {
            ForEach(Array(ghosts.enumerated()), id: \.offset) { _, ghost in
                Text(text)
                    .font(font(baseSize * ghost.scale))
                    .foregroundStyle(.white.opacity(ghost.opacity * presence * Settings.shared.echoStrength))
                    .lineLimit(1)
                    .fixedSize()
                    .rotationEffect(.degrees(ghost.angle))
                    .scaleEffect(x: 1, y: ghost.flipped ? -1 : 1)
                    .offset(x: baseSize * ghost.dx * 3.2, y: baseSize * ghost.dy * 1.5)
            }
        }
        .allowsHitTesting(false)
    }
}
