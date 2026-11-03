import SwiftUI

/// Fit the actual rotated ink around its intended centre. Font estimates
/// cannot bound wide glyphs, custom typefaces or the Cutout label padding.
enum WordStageFit {
    static func scale(ink: CGSize, stageWidth: CGFloat, centreX: CGFloat,
                      scale: Double, angle: Double, margin: CGFloat) -> CGFloat {
        guard ink.width.isFinite, ink.height.isFinite, stageWidth.isFinite,
              centreX.isFinite, scale.isFinite, angle.isFinite, margin.isFinite,
              ink.width > 0, ink.height > 0, stageWidth > 0, scale > 0 else { return 0 }
        let radians = angle * .pi / 180
        let rotatedWidth = ink.width * CGFloat(abs(cos(radians)))
            + ink.height * CGFloat(abs(sin(radians)))
        let clearance = max(0, min(centreX, stageWidth - centreX) - max(0, margin))
        return min(1, clearance * 2 / max(1, rotatedWidth * CGFloat(scale)))
    }
}

/// A free lyric stage. Onsets move the composition; held notes keep their
/// focus. Every exit fades while it is still inside the stage, with no mask.
@MainActor
struct SpokenTypographyView: View {
    let words: [LyricsFetcher.Word]
    let time: Double
    let baseSize: CGFloat
    let mood: TrackMood
    let composition: LyricComposition
    let seed: Int
    let accent: Color
    let energy: Double
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @StateObject private var typography = RenderMemo<SpokenTypography.Key, [SpokenTypography.Word]>()

    private var nextIndex: Int? { words.firstIndex { $0.start > time } }
    private var cursor: Double {
        words.enumerated().reduce(0) { total, item in
            let word = item.element
            if reduceMotion { return total + (time >= word.start ? 1 : 0) }
            let cadence = item.offset + 1 < words.count
                ? words[item.offset + 1].start - word.start : word.end - word.start
            return total + vc_motion_handoff(time - word.start, cadence)
        }
    }
    var body: some View {
        let settings = Settings.shared
        let key = SpokenTypography.Key(words: words, baseSize: baseSize, mood: mood,
            composition: composition, fontIdentity: LyricFontIdentity(settings: settings))
        let prepared = typography.value(for: key) { SpokenTypography.prepare(key, settings: settings) }
        let arrival = settings.arrival(in: composition).resolved(for: composition)
        GeometryReader { geometry in
            let position = cursor
            let next = nextIndex
            ZStack {
                ForEach(prepared) { word in
                    let rank = position - 1 - Double(word.index)
                    if rank >= -2 && rank <= 5.2 {
                        token(word, next: next, rank: rank, size: geometry.size, arrival: arrival)
                    }
                }
            }
            .frame(width: geometry.size.width, height: geometry.size.height)
        }
        .frame(height: baseSize * 7.2)
        .accessibilityElement(children: .combine)
    }
    private func token(_ token: SpokenTypography.Word, next: Int?, rank: Double, size: CGSize, arrival: WordArrival) -> some View {
        let word = token.source
        let presentation = WordPresentation.make(word: word, index: token.index, nextIndex: next,
                                                  time: time, composition: composition)
        let style: Int32 = composition == .spoken ? 0 : composition == .cascade ? 1 : 2
        let pose = vc_rolling_pose(style, rank, time - word.start, word.end - word.start,
                                   token.hash, energy, reduceMotion ? 1 : 0)
        let focus = exp(-rank * rank * 2.4)
        let shown = presentation.opacity * (presentation.preview ? 1 : pose.opacity)
        let columnShift = composition == .spoken ? 0 : -size.width * 0.13
        let centreX = size.width * 0.5 + columnShift + CGFloat(pose.x) * baseSize
        return Text(word.text)
            .font(token.font)
            .foregroundStyle(composition == .cascade && token.hero ? accent : .white)
            .lineLimit(1).fixedSize(horizontal: true, vertical: true)
            .padding(.horizontal, token.tile ? 11 : 2).padding(.vertical, token.tile ? 3 : 0)
            .background {
                if token.tile {
                    RoundedRectangle(cornerRadius: 3)
                        .fill(accent.opacity(0.75))
                        .scaleEffect(x: presentation.preview ? 1 : max(0.05, vc_motion_ease(presentation.progress)),
                                     y: 1, anchor: .leading)
                        .rotationEffect(.degrees(reduceMotion ? 0 : -1.2))
                }
            }
            .modifier(WordEntrance(progress: presentation.progress, style: arrival,
                size: token.pointSize, direction: token.hash % 2 == 0 ? 1 : -1, visibility: shown))
            .visualEffect { ink, geometry in
                ink.scaleEffect(WordStageFit.scale(ink: geometry.size, stageWidth: size.width,
                    centreX: centreX, scale: pose.scale, angle: pose.angle, margin: baseSize * 0.30))
            }
            .scaleEffect(CGFloat(pose.scale))
            .rotationEffect(.degrees(pose.angle))
            .blur(radius: pose.blur)
            .shadow(color: .black.opacity(0.25), radius: 7, y: 2)
            .position(x: centreX,
                      y: size.height * 0.54 + CGFloat(pose.y) * baseSize)
            .zIndex(focus)
            .accessibilityHidden(shown < 0.05)
    }
}
