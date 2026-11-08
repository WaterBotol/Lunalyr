import SwiftUI

/// The highlight colour setting, ready to sit in Settings beside the text
/// colour: where highlight tiles take their colour from, a colour well for
/// Custom, and a small live swatch of the lyric colour with a word
/// highlighted on its tile. Self-contained -- it reads and writes
/// `LyricHighlightOptions` and reads the text colour from
/// `LyricColourOptions` -- so it can go in a `Form` section as it is: each
/// control is a row of its own.
@MainActor
struct LyricHighlightColourControls: View {
    @Model(\.highlightColours) private var options

    var body: some View {
        ChoiceMenu("Highlight colour", selection: $options.mode, selectedTitle: \.title) {
            ForEach(LyricHighlightMode.allCases) { Text($0.title).tag($0) }
        }
        InspectorCaption(options.mode.detail)
        if options.mode == .custom {
            ColorPicker("Your highlight colour", selection: Binding(get: { options.customSwatch },
                                                                   set: { options.setCustom($0) }),
                        supportsOpacity: false)
        }
        InspectorCaption("Used for Cutout labels, tiles and Cascade’s held words.",
                         more: "Highlights are Cutout's labels, the tiles of the Cutout · Highlight entrance and Cascade's held words. The text on a tile always keeps at least 4.5:1 contrast.")
    }
}

/// Shows the text and highlight separately, using the same ink as the stage.
@MainActor
struct LyricColourSwatch: View {
    @Model(\.highlightColours) private var options
    @Model(\.colours) private var colours
    @StateObject private var model = LyricHighlightSwatchModel()
    @Environment(\.lunalyrRendersLiveContent) private var rendersLiveContent
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.colorSchemeContrast) private var contrast

    /// The swatch's own backdrop, and the luminance the colours are judged against.
    private static let backdrop = Color(.sRGB, red: 0.09, green: 0.09, blue: 0.1)
    private static let backdropLuminance = 0.02

    var body: some View {
        let moving = colours.mode.drifts || colours.mode == .musicReactive
        TimelineView(.animation(minimumInterval: 1.0 / 15, paused: !moving)) { timeline in
            let ink = model.ink(inputs, now: timeline.date.timeIntervalSinceReferenceDate)
            VStack(spacing: 10) {
                HStack(spacing: 6) {
                    Text("Sing").foregroundStyle(ink.sample(slot: 0.15).rest)
                    Text("along").foregroundStyle(ink.sample(slot: 0.5).sung)
                    Text("tonight").foregroundStyle(ink.sample(slot: 0.85).rest)
                }
                .font(.title2.weight(.heavy))
                Text("Highlight")
                    .font(.callout.weight(.semibold))
                    .foregroundStyle(ink.tileText)
                    .padding(.horizontal, 9).padding(.vertical, 3)
                    .background(CutoutTile(colour: ink.tileColour))
            }
            .lineLimit(1)
            .minimumScaleFactor(0.8)
            .padding(.horizontal, 12).padding(.vertical, 12)
            .frame(maxWidth: .infinity)
            .background(RoundedRectangle(cornerRadius: 8).fill(Self.backdrop))
            .overlay { RoundedRectangle(cornerRadius: 8).strokeBorder(.quaternary) }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Preview of the text colour with a highlighted word")
    }

    private var inputs: LyricInkDirector.Inputs {
        let grid = rendersLiveContent ? DesktopWallpaperController.shared.currentPalette : ColorExtractor.fallbackHSBPalette
        return LyricInkDirector.Inputs(mode: colours.mode, speed: colours.speed, strength: colours.strength,
            custom: colours.customColours,
            album: AlbumWallpaperPalette(grid: grid).artworkStops,
            backdrop: Self.backdropLuminance, vocal: 0.5, reduceMotion: reduceMotion,
            increaseContrast: contrast == .increased, highlight: options.mode, highlightCustom: options.customColour)
    }
}

/// Keeps the swatch's colours on their own clock: settled at once when a
/// setting changes, then drifting in real time.
@MainActor
final class LyricHighlightSwatchModel: ObservableObject {
    private let director = LyricInkDirector()
    private struct Key: Equatable {
        var mode: LyricColourMode
        var speed: Double
        var strength: Double
        var custom: [OKLab]
        var album: [SIMD3<Float>]
        var highlight: LyricHighlightMode
        var highlightCustom: OKLab?
        var reduceMotion: Bool
        var increaseContrast: Bool
    }
    private var key: Key?
    private var clock = 0.0
    private var lastNow: Double?

    func ink(_ inputs: LyricInkDirector.Inputs, now: Double) -> LyricInk {
        let step = lastNow.map { min(0.25, max(0, now - $0)) } ?? 0
        lastNow = now
        let key = Key(mode: inputs.mode, speed: inputs.speed, strength: inputs.strength, custom: inputs.custom, album: inputs.album,
                      highlight: inputs.highlight, highlightCustom: inputs.highlightCustom,
                      reduceMotion: inputs.reduceMotion, increaseContrast: inputs.increaseContrast)
        if key != self.key {
            // A setting changed: show where the colours settle, not the fade.
            self.key = key
            for _ in 0..<24 {
                clock += 0.25
                _ = director.ink(inputs, wall: clock)
            }
        }
        clock += max(step, 0.001)
        return director.ink(inputs, wall: clock)
    }
}
