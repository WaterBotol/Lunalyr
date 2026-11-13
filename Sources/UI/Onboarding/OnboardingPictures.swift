import AppKit
import SwiftUI

// MARK: - Pictures for the music-video and extras steps
//
// Small, flat, drawn from the sample song and the sample wallpaper, so they
// look the same in light and dark (they are pictures of a desktop) and need
// nothing running. Hidden from VoiceOver: each row's words say what it shows.
// None of these moves. (The look step's layouts are in
// OnboardingLayoutChoices.swift, the Discord recording in
// DiscordStatusPicture.swift: both still under Reduce Motion.)

// MARK: Music videos

/// A music video's still on a desktop, in one of the two framings: filling
/// the screen (its sides cut), or whole across the screen's width with a
/// blurred copy above and below.
struct VideoFramingPicture: View {
    let framing: MusicVideoFraming

    var body: some View {
        let size = LunalyrDesign.Onboarding.framingThumb
        // A 16:9 picture on a desktop shaped like the welcome's (taller than 16:9).
        let filled = CGSize(width: size.height * 16 / 9, height: size.height)
        let whole = CGSize(width: size.width, height: size.width * 9 / 16)
        ZStack {
            switch framing {
            case .fill:
                VideoStill().frame(width: filled.width, height: filled.height)
            case .whole:
                VideoStill()
                    .frame(width: filled.width, height: filled.height)
                    .blur(radius: 8)
                    .overlay(Color.black.opacity(0.5))
                VideoStill().frame(width: whole.width, height: whole.height)
            }
        }
        .frame(width: size.width, height: size.height)
        .clipShape(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.artwork, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.artwork, style: .continuous)
            .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
        .accessibilityHidden(true)
    }
}

/// A made-up frame of a music video: a low sun over the sea, with a
/// singer's silhouette off to the side, where filling the screen crops it.
private struct VideoStill: View {
    var body: some View {
        GeometryReader { proxy in
            let width = proxy.size.width, height = proxy.size.height
            ZStack(alignment: .topLeading) {
                LinearGradient(colors: [SampleWallpaper.rgb(0.98, 0.62, 0.40), SampleWallpaper.rgb(0.86, 0.32, 0.46),
                                        SampleWallpaper.rgb(0.22, 0.12, 0.36)],
                               startPoint: .top, endPoint: .bottom)
                Circle()
                    .fill(SampleWallpaper.rgb(1.0, 0.86, 0.62))
                    .frame(width: height * 0.34, height: height * 0.34)
                    .position(x: width * 0.5, y: height * 0.58)
                Rectangle()
                    .fill(SampleWallpaper.rgb(0.12, 0.08, 0.24))
                    .frame(width: width, height: height * 0.3)
                    .position(x: width * 0.5, y: height * 0.85)
                Capsule()
                    .fill(SampleWallpaper.rgb(0.08, 0.05, 0.16))
                    .frame(width: height * 0.16, height: height * 0.5)
                    .position(x: width * 0.08, y: height * 0.8)
                Circle()
                    .fill(SampleWallpaper.rgb(0.08, 0.05, 0.16))
                    .frame(width: height * 0.14, height: height * 0.14)
                    .position(x: width * 0.08, y: height * 0.5)
            }
        }
    }
}

// MARK: Extras

private typealias Ink = LunalyrDesign.Player.Ink

/// The desktop player in miniature: its card in the album's deep shade, the
/// cover, the song, the seek line and the play disc.
struct MiniDesktopPlayer: View {
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        let look = PlayerLook(increaseContrast: contrast == .increased)
        HStack(spacing: LunalyrDesign.Spacing.small) {
            RoundedRectangle(cornerRadius: LunalyrDesign.Player.Radius.thumb, style: .continuous)
                .fill(LinearGradient(colors: [SampleWallpaper.rgb(0.98, 0.62, 0.38), SampleWallpaper.rgb(0.85, 0.30, 0.48),
                                              SampleWallpaper.rgb(0.24, 0.16, 0.42)],
                                     startPoint: .top, endPoint: .bottom))
                .frame(width: 36, height: 36)
            VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.hairline) {
                Text("Golden Hour Static").font(.system(size: 10, weight: .semibold)).foregroundStyle(Ink.primary.color(look))
                Text("Lunar Tides").font(.system(size: 10)).foregroundStyle(Ink.secondary.color(look))
                Capsule()
                    .fill(Ink.quaternary.color(look))
                    .frame(height: 2)
                    .overlay(alignment: .leading) {
                        GeometryReader { proxy in
                            Capsule().fill(Ink.primary.color(look)).frame(width: proxy.size.width * 0.4, height: 2)
                        }
                    }
                    .padding(.top, LunalyrDesign.Spacing.xSmall)
            }
            .lineLimit(1)
            Circle()
                .fill(.white)
                .frame(width: 20, height: 20)
                .overlay(Image(systemName: "pause.fill").font(.system(size: 8, weight: .semibold)).foregroundStyle(.black))
        }
        .padding(LunalyrDesign.Spacing.small)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.control, style: .continuous)
            .fill(SampleWallpaper.rgb(0.20, 0.10, 0.27)))
        .overlay(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.control, style: .continuous)
            .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
        .environment(\.colorScheme, .dark)
    }
}

/// Floating lyrics in miniature: a line over an app's window, on its soft
/// shade, with the desktop behind.
struct MiniFloatingLyrics: View {
    var body: some View {
        ZStack(alignment: .topLeading) {
            SampleWallpaper()
            // An app's window, running off the picture's lower right.
            VStack(spacing: 0) {
                Rectangle().fill(Color(white: 0.86)).frame(height: LunalyrDesign.Spacing.small + LunalyrDesign.Spacing.xSmall)
                Rectangle().fill(Color(white: 0.97))
            }
            .clipShape(UnevenRoundedRectangle(topLeadingRadius: LunalyrDesign.Radius.tag, style: .continuous))
            .padding(.top, LunalyrDesign.Spacing.small)
            .padding(.leading, LunalyrDesign.Spacing.xxLarge)
            .padding(.bottom, -LunalyrDesign.Spacing.small)
            Text("gold across the bay")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(.white)
                .padding(.horizontal, LunalyrDesign.Spacing.small + LunalyrDesign.Spacing.xSmall)
                .padding(.vertical, LunalyrDesign.Spacing.xSmall)
                .background(Capsule().fill(.black.opacity(0.55)))
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .offset(y: LunalyrDesign.Spacing.small)
        }
        .clipShape(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.control, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.control, style: .continuous)
            .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
    }
}
