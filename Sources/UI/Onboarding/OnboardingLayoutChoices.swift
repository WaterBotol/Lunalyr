import AppKit
import SwiftUI

// MARK: Lyric layouts

/// A row of lyric layouts drawn live through `LyricLineView`, as the
/// wallpaper draws them; clicking one chooses it. The chosen one plays the
/// sample line a few times, the others hold mid-line.
struct LayoutChoices: View {
    @Model(\.settings) private var settings
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    /// A few of the reworked layouts; the one in use leads when it is another.
    static let featured: [LyricComposition] = [.broadside, .portal, .mosaic, .cascade]

    static func shown(current: LyricComposition?) -> [LyricComposition] {
        guard let current, !featured.contains(current) else { return featured }
        return [current] + featured.dropLast()
    }

    var body: some View {
        let current = settings.lyricComposition
        let layouts = Self.shown(current: current)
        VStack(spacing: LunalyrDesign.Spacing.small) {
            HStack(alignment: .top, spacing: LunalyrDesign.Spacing.small) {
                ForEach(layouts) { layout in
                    tile(layout, selected: layout == current)
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Lyric layout")
            Text(OnboardingCopy.layoutCaption(current))
                .lunalyrText(.caption)
                .multilineTextAlignment(.center)
                .lineLimit(2)
                .id(current)
                .transition(.opacity)
            Text(OnboardingCopy.moreLayouts(LyricComposition.allCases.count - layouts.count))
                .lunalyrText(.footnote)
        }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: current)
    }

    private func choose(_ layout: LyricComposition) {
        settings.lyricComposition = layout
    }

    private func tile(_ layout: LyricComposition, selected: Bool) -> some View {
        Button {
            choose(layout)
        } label: {
            VStack(spacing: LunalyrDesign.Spacing.xSmall) {
                LayoutThumbnail(composition: layout, plays: selected)
                    .clipShape(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.artwork, style: .continuous))
                    .overlay {
                        // The selection ring: a state, like a chosen desktop picture.
                        RoundedRectangle(cornerRadius: LunalyrDesign.Radius.artwork + 3, style: .continuous)
                            .strokeBorder(selected ? Color.lunalyrAccent : .clear, lineWidth: 2)
                            .padding(-3)
                    }
                Text(layout.title)
                    .font(LunalyrTextStyle.caption.font)
                    .fontWeight(selected ? .semibold : .regular)
                    .foregroundStyle(selected ? AnyShapeStyle(Color.lunalyrAccentText) : AnyShapeStyle(.secondary))
                    .multilineTextAlignment(.center)
                    .lineLimit(2, reservesSpace: true)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .frame(width: LunalyrDesign.Onboarding.layoutThumb.width)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(layout.title)
        .accessibilityHint(layout.detail)
        .accessibilityAddTraits(selected ? .isSelected : [])
        .help(layout.detail)
    }
}

/// One layout setting the sample line over the sample wallpaper, scaled
/// down from a stage the size of a small desktop.
struct LayoutThumbnail: View {
    let composition: LyricComposition
    /// Plays the line (a few times, then holds) rather than holding it.
    var plays = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.lunalyrRendersLiveContent) private var live
    @Environment(\.controlActiveState) private var activeState
    @State private var started = Date()

    static let words: [LyricsFetcher.Word] = {
        let texts = SampleLyric.lines.flatMap { $0 }
        return texts.enumerated().map { index, text in
            LyricsFetcher.Word(text: text, start: 1 + Double(index) * beat, end: 1 + Double(index) * beat + beat * 0.9)
        }
    }()
    static let beat = 0.42
    static let lineEnd = 1 + Double(words.count) * beat
    static let exitStart = lineEnd + 1.2
    /// One play, with a breath before the next.
    static let cycle = exitStart + 1.4
    static let plays = 3
    /// Where a still picture holds: the line just sung in full.
    static let held = lineEnd + 0.9

    /// The time in the line `elapsed` seconds after it started playing.
    static func time(after elapsed: TimeInterval) -> TimeInterval {
        guard elapsed < Double(plays) * cycle else { return held }
        return elapsed.truncatingRemainder(dividingBy: cycle)
    }

    var body: some View {
        let size = LunalyrDesign.Onboarding.layoutThumb
        ZStack {
            SampleWallpaper()
            if plays && live && !reduceMotion && activeState != .inactive {
                TimelineView(.animation(minimumInterval: 1.0 / 30)) { context in
                    line(at: Self.time(after: context.date.timeIntervalSince(started)))
                }
            } else {
                line(at: Self.held)
            }
        }
        .frame(width: size.width, height: size.height)
        .environment(\.colorScheme, .dark)
        .onChange(of: plays) { _, playing in
            if playing { started = Date() }
        }
        .accessibilityHidden(true)
    }

    private func line(at time: TimeInterval) -> some View {
        let stage = LunalyrDesign.Onboarding.layoutStage
        let size = LunalyrDesign.Onboarding.layoutThumb
        let plain = [Bool](repeating: false, count: Self.words.count)
        let glue = composition.hasConnections ? LyricRuns.splittingWords(Self.words, glue: plain) : plain
        return LyricLineView(words: Self.words, time: time, stageSize: stage, baseSize: 56, composition: composition,
                             seed: 3, lineEnd: Self.lineEnd, exitStart: Self.exitStart, glue: glue,
                             liftsBackingVocals: false)
            .modifier(FittedLyricStage(available: CGSize(width: stage.width * 0.9, height: stage.height * 0.86)))
            .frame(width: stage.width, height: stage.height)
            .environment(\.lyricInk, LyricInk())
            .scaleEffect(size.width / stage.width)
            .frame(width: size.width, height: size.height)
    }
}
