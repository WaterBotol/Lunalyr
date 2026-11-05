import AppKit
import SwiftUI

// MARK: - Onboarding's illustrations
//
// Drawn in SwiftUI from a few gradients and the sample song the rest of the
// GUI uses, so they need no wallpaper running yet, look the same in light and
// dark mode (they are pictures of a desktop) and are hidden from VoiceOver:
// the step's words say what they show.

/// A desktop in an album's colours: warm glows over a deep night gradient,
/// dark enough for white lyrics.
struct SampleWallpaper: View {
    var body: some View {
        GeometryReader { proxy in
            let unit = max(proxy.size.width, proxy.size.height)
            ZStack {
                LinearGradient(colors: [Self.rgb(0.20, 0.10, 0.30), Self.rgb(0.07, 0.05, 0.15)],
                               startPoint: .top, endPoint: .bottom)
                RadialGradient(colors: [Self.rgb(0.99, 0.56, 0.32).opacity(0.80), .clear],
                               center: UnitPoint(x: 0.16, y: 1.0), startRadius: 0, endRadius: unit * 0.62)
                RadialGradient(colors: [Self.rgb(0.88, 0.27, 0.52).opacity(0.62), .clear],
                               center: UnitPoint(x: 0.88, y: 0.30), startRadius: 0, endRadius: unit * 0.52)
                RadialGradient(colors: [Self.rgb(0.40, 0.30, 0.82).opacity(0.50), .clear],
                               center: UnitPoint(x: 0.45, y: -0.05), startRadius: 0, endRadius: unit * 0.42)
            }
        }
        .accessibilityHidden(true)
    }

    static func rgb(_ red: Double, _ green: Double, _ blue: Double) -> Color {
        Color(.sRGB, red: red, green: green, blue: blue, opacity: 1)
    }
}

/// Two lines of the sample song, lit word by word as Lunalyr lights them:
/// sung words bright, the word being sung glowing, the rest waiting. Moves
/// on its own unless Reduce Motion is on (or the view is drawn for a
/// snapshot), when it holds mid-line.
struct SampleLyric: View {
    static let lines = [["Static", "on", "the", "radio,"], ["gold", "across", "the", "bay"]]
    private static let words = lines.flatMap { $0 }.count
    /// Where a still picture holds: "Static on the" sung, "radio," being sung.
    private static let held = 3

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.lunalyrRendersLiveContent) private var live
    @State private var started = Date()

    var body: some View {
        GeometryReader { proxy in
            let size = proxy.size.width * 0.064
            Group {
                if live && !reduceMotion {
                    TimelineView(.periodic(from: started, by: 0.45)) { context in
                        // Eight words, then a breath with the lines full, then again.
                        let beat = Int(context.date.timeIntervalSince(started) / 0.45) % (Self.words + 4)
                        lines(sung: min(beat, Self.words), size: size)
                    }
                } else {
                    lines(sung: Self.held, size: size)
                }
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
        }
        .accessibilityHidden(true)
    }

    private func lines(sung: Int, size: CGFloat) -> some View {
        VStack(spacing: size * 0.28) {
            ForEach(Self.lines.indices, id: \.self) { index in
                let start = Self.lines[..<index].reduce(0) { $0 + $1.count }
                line(Self.lines[index], firstWord: start, sung: sung)
            }
        }
        .font(AppFont.display(size))
        .multilineTextAlignment(.center)
        .shadow(color: .black.opacity(0.25), radius: size * 0.3, y: size * 0.06)
        .animation(.easeOut(duration: 0.3), value: sung)
    }

    private func line(_ words: [String], firstWord: Int, sung: Int) -> some View {
        words.indices.reduce(Text("")) { text, index in
            let number = firstWord + index
            let word = Text((index == 0 ? "" : " ") + words[index])
            if number < sung { return text + word.foregroundStyle(Color.white) }
            if number == sung { return text + word.foregroundStyle(Color.white.opacity(0.82)) }
            return text + word.foregroundStyle(Color.white.opacity(0.36))
        }
    }
}

/// The welcome's picture: the desktop as Lunalyr draws it, in miniature,
/// with the app's icon resting on its lower edge.
struct WelcomeHero: View {
    var width: CGFloat = 392

    var body: some View {
        PreviewStage(desktop: CGSize(width: 1470, height: 956)) {
            ZStack {
                SampleWallpaper()
                SampleLyric()
            }
        }
        .frame(width: width)
        .overlay(alignment: .bottom) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .interpolation(.high)
                .frame(width: 72, height: 72)
                .shadow(color: .black.opacity(0.25), radius: 8, y: 3)
                .offset(y: 30)
        }
        .padding(.bottom, 30)
        .accessibilityHidden(true)
    }
}

/// The right-hand end of a MacBook's menu bar, from the notch: Lunalyr's
/// icon lit as when its panel is open, the panel peeking out beneath, and
/// the line being sung beside the icon while that is switched on.
struct MenuBarIllustration: View {
    var showsLine: Bool
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    static let line = "Static on the radio, gold across the bay"
    private static let barHeight: CGFloat = 26

    var body: some View {
        ZStack(alignment: .topLeading) {
            SampleWallpaper()
            Rectangle()
                .fill(.black.opacity(0.24))
                .frame(height: Self.barHeight)
            // The notch, at the bar's leading edge.
            UnevenRoundedRectangle(bottomTrailingRadius: 9, style: .continuous)
                .fill(.black)
                .frame(width: 62, height: Self.barHeight + 2)
            statusItems
                .frame(maxWidth: .infinity, alignment: .trailing)
                .frame(height: Self.barHeight)
                .padding(.trailing, 14)
        }
        .frame(height: 104)
        .clipShape(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous)
            .strokeBorder(.primary.opacity(0.12), lineWidth: 0.5))
        .overlay(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous)
            .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
        .environment(\.colorScheme, .dark)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                   value: showsLine)
        .accessibilityHidden(true)
    }

    private var statusItems: some View {
        HStack(spacing: 13) {
            if showsLine {
                Text(Self.line)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .frame(maxWidth: 210, alignment: .trailing)
                    .transition(.opacity)
            }
            lunalyrIcon
            Image(systemName: "battery.75percent")
            Image(systemName: "wifi")
            Image(systemName: "switch.2")
            Text("Wed 9:41")
        }
        .font(.system(size: 13, weight: .medium))
        .foregroundStyle(.white)
    }

    private var lunalyrIcon: some View {
        Image(nsImage: Self.icon)
            .renderingMode(.template)
            .resizable()
            .frame(width: 17, height: 17)
            .padding(.horizontal, 6)
            .padding(.vertical, 3)
            .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(.white.opacity(0.28)))
            .overlay(alignment: .top) { MiniPanel().offset(y: Self.barHeight + 4) }
    }

    private static let icon: NSImage = {
        let image = NSImage(named: "MenuBarIconTemplate") ?? NSImage(systemSymbolName: "music.note", accessibilityDescription: nil) ?? NSImage()
        image.isTemplate = true
        return image
    }()
}

/// The top of Lunalyr's menu-bar panel, as a hint of what the icon opens.
private struct MiniPanel: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                RoundedRectangle(cornerRadius: 4, style: .continuous)
                    .fill(LinearGradient(colors: [SampleWallpaper.rgb(0.98, 0.62, 0.38), SampleWallpaper.rgb(0.85, 0.30, 0.48),
                                                  SampleWallpaper.rgb(0.24, 0.16, 0.42)],
                                         startPoint: .top, endPoint: .bottom))
                    .frame(width: 26, height: 26)
                VStack(alignment: .leading, spacing: 1) {
                    Text("Golden Hour Static").font(.system(size: 10, weight: .semibold))
                    Text("Lunar Tides").font(.system(size: 9)).foregroundStyle(.secondary)
                }
                .lineLimit(1)
            }
            HStack(spacing: 4) {
                ForEach(0..<5) { index in
                    RoundedRectangle(cornerRadius: 4, style: .continuous)
                        .fill(index == 0 ? AnyShapeStyle(Color.lunalyrAccent) : AnyShapeStyle(.white.opacity(0.12)))
                        .frame(width: 24, height: 18)
                }
            }
        }
        .padding(10)
        .frame(width: 156, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color(white: 0.15).opacity(0.94)))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(.white.opacity(0.14), lineWidth: 0.5))
        .shadow(color: .black.opacity(0.35), radius: 10, y: 4)
        .foregroundStyle(.white)
        .fixedSize()
    }
}
