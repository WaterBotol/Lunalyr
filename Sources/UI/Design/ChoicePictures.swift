import AppKit
import SwiftUI

// MARK: - Choice pictures (redesign "On the picture")
//
// Visual choices are shown as pictures, not words (design/research-2026-11-14.md,
// F5; work/ui-redesign-mockups/final/SPEC.md). These draw what each choice looks
// like in the playing song's colours, cheaply: a few shapes, no live rendering,
// so a whole grid of them costs nothing per frame.

/// What one wallpaper type draws, in the song's colours (`palette`).
struct WallpaperTypePicture: View {
    let mode: Settings.BackgroundMode
    var palette: ArtworkPalette = .neutral
    /// The playing song's cover (Image or video, Music video), if any.
    var cover: NSImage?
    /// The macOS desktop picture (Your wallpaper), if known.
    var desktopPicture: NSImage?

    var body: some View {
        GeometryReader { proxy in
            ZStack { content(size: proxy.size) }
                .frame(width: proxy.size.width, height: proxy.size.height)
                .clipped()
        }
        .accessibilityHidden(true)
    }

    @ViewBuilder private func content(size: CGSize) -> some View {
        let accent = palette.accentColor
        let deep = palette.deepColor
        switch mode {
        case .auto:
            ChoicePictureGlow(palette: palette)
            Image(systemName: mode.symbol)
                .font(.system(size: size.height * 0.3, weight: .medium))
                .foregroundStyle(.white)
        case .pattern:
            deep
            Path { path in
                for index in 0..<9 {
                    let x = CGFloat(index) * size.width / 6 - size.width * 0.3
                    path.move(to: CGPoint(x: x, y: size.height))
                    path.addLine(to: CGPoint(x: x + size.width * 0.5, y: 0))
                }
            }
            .stroke(accent.opacity(0.8), lineWidth: max(1, size.height * 0.025))
        case .shader:
            deep
            RadialGradient(colors: [accent, .clear], center: UnitPoint(x: 0.3, y: 0.7),
                           startRadius: 0, endRadius: size.width * 0.55)
            RadialGradient(colors: [palette.accent.mixed(with: .init(0.92, 0.32, 0.55), 0.5).color.opacity(0.8), .clear],
                           center: UnitPoint(x: 0.8, y: 0.25), startRadius: 0, endRadius: size.width * 0.45)
        case .visualizer:
            deep
            HStack(alignment: .bottom, spacing: max(1, size.width * 0.015)) {
                ForEach(0..<9, id: \.self) { index in
                    Capsule().fill(accent)
                        .frame(width: max(2, size.width * 0.025), height: size.height * Self.bars[index] * 0.7)
                }
            }
            .frame(maxHeight: .infinity, alignment: .bottom)
            .padding(.bottom, size.height * 0.12)
        case .colourOnly:
            palette.deep.mixed(with: palette.accent, 0.45).color
        case .desktop:
            if let desktopPicture {
                Image(nsImage: desktopPicture).resizable().scaledToFill()
            } else {
                SampleWallpaper()
            }
        case .custom:
            if let cover {
                Image(nsImage: cover).resizable().scaledToFill()
            } else {
                deep
                Image(systemName: mode.symbol)
                    .font(.system(size: size.height * 0.3, weight: .medium))
                    .foregroundStyle(.white.opacity(0.85))
            }
        case .musicVideo:
            if let cover {
                Image(nsImage: cover).resizable().scaledToFill()
                    .overlay(Color.black.opacity(0.35))
            } else {
                deep
            }
            Image(systemName: "play.fill")
                .font(.system(size: size.height * 0.3))
                .foregroundStyle(.white)
        }
    }

    private static let bars: [CGFloat] = [0.3, 0.55, 0.8, 0.45, 0.65, 0.9, 0.5, 0.35, 0.6]
}

/// Match the music's picture: the song's colours as a soft glow.
struct ChoicePictureGlow: View {
    var palette: ArtworkPalette

    var body: some View {
        GeometryReader { proxy in
            ZStack {
                palette.deepColor
                RadialGradient(colors: [palette.accentColor.opacity(0.9), .clear], center: UnitPoint(x: 0.25, y: 0.8),
                               startRadius: 0, endRadius: proxy.size.width * 0.7)
            }
        }
    }
}

/// One lyric layout's picture: the app's own thumbnail scaled to fill, or,
/// for Match the music (nil), the song's colours with its symbol.
struct LayoutPicture: View {
    let composition: LyricComposition?
    var palette: ArtworkPalette = .neutral

    var body: some View {
        GeometryReader { proxy in
            if let composition {
                let thumb = LunalyrDesign.Onboarding.layoutThumb
                LayoutThumbnail(composition: composition)
                    .scaleEffect(max(proxy.size.width / thumb.width, proxy.size.height / thumb.height))
                    .frame(width: proxy.size.width, height: proxy.size.height)
                    .clipped()
            } else {
                ZStack {
                    ChoicePictureGlow(palette: palette)
                    Image(systemName: Settings.BackgroundMode.auto.symbol)
                        .font(.system(size: proxy.size.height * 0.36, weight: .medium))
                        .foregroundStyle(.white)
                }
            }
        }
        .accessibilityHidden(true)
    }
}
