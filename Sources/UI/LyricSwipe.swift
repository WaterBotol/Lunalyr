import SwiftUI

struct LyricSwipePalette {
    let ribbon: Color
    let ink: Color
    let highlight: Color

    init(hue: Double = 0.64, saturation: Double = 0.55) {
        let strength = min(0.72, max(0.24, saturation))
        ribbon = Color(hue: (hue + 0.10).truncatingRemainder(dividingBy: 1), saturation: strength * 0.6, brightness: 1)
        ink = Color(hue: (hue + 0.48).truncatingRemainder(dividingBy: 1), saturation: strength, brightness: 0.76)
        highlight = Color(hue: (hue + 0.42).truncatingRemainder(dividingBy: 1), saturation: strength * 0.5, brightness: 1)
    }
}

private struct LyricSwipePaletteKey: EnvironmentKey {
    static let defaultValue = LyricSwipePalette()
}

extension EnvironmentValues {
    var lyricSwipePalette: LyricSwipePalette {
        get { self[LyricSwipePaletteKey.self] }
        set { self[LyricSwipePaletteKey.self] = newValue }
    }
}

/// Decorative ink stays inside the measured word or phrase, including readings.
struct LyricSwipe: View {
    let style: WordArrival
    let progress: Double
    let direction: Double
    let palette: LyricSwipePalette

    var body: some View {
        GeometryReader { box in
            let width = box.size.width, height = box.size.height
            let phase = progress * 1.74 - 0.37
            if style == .satinRibbon {
                LinearGradient(colors: [.clear, palette.ribbon.opacity(0.23), palette.highlight.opacity(0.65), .clear],
                    startPoint: .leading, endPoint: .trailing)
                    .frame(width: width * 0.42, height: height * 1.35)
                    .rotationEffect(.degrees(-13))
                    .position(x: width * (direction >= 0 ? phase : 1 - phase), y: height / 2)
            } else {
                let move = width * (phase - 0.5) * direction
                HStack(spacing: 0) {
                    Rectangle().fill(palette.ink.opacity(0.68))
                    Rectangle().fill(palette.highlight.opacity(0.88)).frame(width: max(1, width * 0.06))
                }
                .frame(width: width * 0.53, height: height * 0.48)
                .position(x: width / 2 + move, y: height * 0.25)
                Rectangle().fill(palette.ink.opacity(0.52))
                    .frame(width: width * 0.42, height: height * 0.48)
                    .position(x: width / 2 - move, y: height * 0.75)
            }
        }
        .clipped()
        .opacity(sin(progress * .pi))
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}
