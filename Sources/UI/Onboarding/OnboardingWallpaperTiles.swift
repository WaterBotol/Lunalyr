import AppKit
import SwiftUI

/// The eight wallpaper types as pictures (`WallpaperTypePicture`), four to
/// a row, each with its name under it, the chosen one ringed: the choice
/// tiles of the redesign (SPEC.md section 3), sized for onboarding's look
/// step. Choosing does what `WallpaperModeGrid` does, Image or video with
/// nothing imported asking for a file. While power saving draws another
/// type, a leaf marks the tile it draws.
struct OnboardingWallpaperTiles: View {
    var chooseImport: () -> Void
    /// The macOS desktop picture, for Your wallpaper's tile.
    var desktopPicture: NSImage?
    @Model(\.settings) private var settings
    @Model(\.nowPlaying) private var playing
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    static let columns = 4
    /// Four tiles 73 x 33 and their gaps: the grid with a two-line caption
    /// fits the look step's reserved height (`Onboarding.choicesHeight`).
    static let width: CGFloat = 320
    static let gap: CGFloat = 9
    static let rowGap: CGFloat = 6
    /// A little flatter than Settings' 139 x 70, to fit onboarding's window.
    static let aspect: CGFloat = 0.45

    var body: some View {
        let palette = ArtworkPalette.of(playing.currentTrackArtwork)
        let modes = Settings.BackgroundMode.generated + Settings.BackgroundMode.own
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: Self.gap), count: Self.columns),
                  spacing: Self.rowGap) {
            ForEach(modes) { mode in
                tile(mode, palette: palette)
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Wallpaper type")
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: settings.backgroundMode)
    }

    private func tile(_ mode: Settings.BackgroundMode, palette: ArtworkPalette) -> some View {
        let chosen = settings.backgroundMode == mode
        let standsIn = settings.effectiveBackgroundMode == mode && !chosen
        return Button {
            WallpaperModeGrid.choose(mode, settings: settings, chooseImport: chooseImport)
        } label: {
            VStack(spacing: 3) {
                WallpaperTypePicture(mode: mode, palette: palette, cover: playing.currentTrackArtwork,
                                     desktopPicture: desktopPicture)
                    .aspectRatio(1 / Self.aspect, contentMode: .fit)
                    .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                    .overlay(alignment: .topTrailing) {
                        if standsIn {
                            Image(systemName: "leaf.fill")
                                .font(.system(size: 9, weight: .bold))
                                .foregroundStyle(.white)
                                .padding(5)
                                .accessibilityHidden(true)
                        }
                    }
                    .modifier(OnboardingChosenRing(chosen: chosen, radius: 6))
                Text(mode.shortTitle)
                    .font(.system(size: 11, weight: chosen ? .semibold : .regular))
                    .foregroundStyle(chosen ? Color.studio(.ink) : Color.studio(.ink2))
                    .lineLimit(1)
                    .minimumScaleFactor(0.85)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(OnboardingTilePressStyle())
        .help("\(mode.title): \(mode.summary)")
        .accessibilityLabel(mode.title)
        .accessibilityValue(standsIn ? "Shown by power saving for now" : "")
        .accessibilityHint(mode.summary)
        .accessibilityAddTraits(chosen ? [.isSelected] : [])
    }
}

/// The chosen ring of every choice tile: 2.5 pt accent, 4 outside the tile,
/// radius tile + 3; a 1-pt edge on every tile under Increase Contrast.
struct OnboardingChosenRing: ViewModifier {
    let chosen: Bool
    var radius: CGFloat = 6
    @Environment(\.colorSchemeContrast) private var contrast

    func body(content: Content) -> some View {
        content
            .overlay {
                if contrast == .increased {
                    RoundedRectangle(cornerRadius: radius, style: .continuous)
                        .strokeBorder(Color.studio(.ink).opacity(0.25), lineWidth: 1)
                }
            }
            .overlay {
                RoundedRectangle(cornerRadius: radius + 3, style: .continuous)
                    .strokeBorder(chosen ? Color.studio(.accent) : .clear, lineWidth: 2.5)
                    .padding(-4)
            }
    }
}

/// A tile dims a little while pressed, so the click registers before the new look arrives.
struct OnboardingTilePressStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.brightness(configuration.isPressed ? -0.08 : 0)
    }
}
