import AppKit
import SwiftUI

// MARK: - Choices under the picture
//
// Pictures for the selected object's main choice (spec §3, "Choices"):
// static drawings in the playing song's colours (or the sample wallpaper's
// while nothing plays), never a second live preview.

/// The colours the tiles are drawn in.
enum ChoicePalette {
    /// The sample wallpaper's colours (`SampleWallpaper`), while nothing plays.
    static let sample = ArtworkPalette(accent: ArtworkPalette.RGB(0.99, 0.56, 0.32),
                                       deep: ArtworkPalette.RGB(0.10, 0.07, 0.20), fromArtwork: false)
}

/// Wallpaper type: eight pictures, four to a row. Choosing one is what the
/// old type grid did, including asking for a file for Image or video when
/// nothing is imported yet.
@MainActor
struct WallpaperTypeChoices: View {
    let width: CGFloat
    let palette: ArtworkPalette
    var cover: NSImage?
    var desktopPicture: NSImage?
    let chooseImport: () -> Void
    @Model(\.settings) private var settings

    var body: some View {
        let tile = ChoiceGridMetrics.tile(count: 4, ratio: 0.5, width: width)
        VStack(alignment: .leading, spacing: 12) {
            ChoiceHeader(title: "Wallpaper type", value: settings.backgroundMode.title)
            VStack(alignment: .leading, spacing: 12) {
                ForEach(Array(ChoiceGridMetrics.rows(LookChoices.wallpaperTypes, count: 4).enumerated()), id: \.offset) { _, row in
                    HStack(alignment: .top, spacing: ChoiceGridMetrics.gap) {
                        ForEach(row) { mode in
                            ChoiceTile(name: mode.title, chosen: settings.backgroundMode == mode, size: tile,
                                       hint: mode.summary, action: { Self.choose(mode, settings: settings, chooseImport: chooseImport) }) {
                                WallpaperTypePicture(mode: mode, palette: palette, cover: cover, desktopPicture: desktopPicture)
                                    .overlay(alignment: .topTrailing) { powerLeaf(mode) }
                            }
                        }
                    }
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Wallpaper type")
        }
    }

    /// While power saving draws another type, a leaf marks the one it draws.
    @ViewBuilder private func powerLeaf(_ mode: Settings.BackgroundMode) -> some View {
        if settings.effectiveBackgroundMode == mode && settings.backgroundMode != mode {
            Image(systemName: "leaf.fill")
                .font(.system(size: 9, weight: .bold))
                .foregroundStyle(.white)
                .padding(5)
                .help("Power saving shows \(mode.title) for now")
        }
    }

    /// The type grid's choice: Image or video with nothing imported asks
    /// for a file instead.
    static func choose(_ mode: Settings.BackgroundMode, settings: Settings, chooseImport: () -> Void) {
        if mode == .custom && !CustomWallpaperStore.isAvailable(settings.customWallpaperPath) {
            chooseImport()
            return
        }
        settings.backgroundMode = mode
    }
}

/// Layout: Match the music and every layout as the app's own thumbnails, six
/// to a row; the names are VoiceOver labels and help tags, the header names
/// the chosen one.
@MainActor
struct LayoutTileChoices: View {
    let width: CGFloat
    let palette: ArtworkPalette
    @Model(\.settings) private var settings
    @AppStorage(LyricLayoutRotation.enabledKey) private var rotateLayouts = true

    var body: some View {
        let tile = ChoiceGridMetrics.tile(count: 6, ratio: 56.0 / 96.0, width: width)
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .firstTextBaseline) {
                ChoiceHeader(title: "Layout", value: LookChoices.layoutTitle(settings.lyricComposition))
                Spacer(minLength: 8)
                Toggle("Change layout every song", isOn: $rotateLayouts)
                    .toggleStyle(StudioCheckboxStyle())
                    .font(.system(size: 13))
                    .foregroundStyle(Color.studio(.ink))
                    .disabled(settings.lyricComposition != nil)
                    .help(settings.lyricComposition == nil
                          ? "Each new song takes the next layout"
                          : "Applies while the layout is Match the music")
                    .fixedSize()
            }
            VStack(alignment: .leading, spacing: ChoiceGridMetrics.gap) {
                ForEach(Array(ChoiceGridMetrics.rows(LookChoices.layouts, count: 6).enumerated()), id: \.offset) { _, row in
                    HStack(spacing: ChoiceGridMetrics.gap) {
                        ForEach(row, id: \.self) { layout in
                            ChoiceTile(name: LookChoices.layoutTitle(layout), chosen: settings.lyricComposition == layout,
                                       size: tile, showsName: false,
                                       action: { settings.lyricComposition = layout }) {
                                LayoutPicture(composition: layout, palette: palette)
                            }
                        }
                    }
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Layout")
        }
    }
}

/// Animated covers' Placement: where the square cover sits on the screen.
@MainActor
struct PlacementChoices: View {
    let width: CGFloat
    let aspect: CGFloat
    var cover: NSImage?
    @Model(\.settings) private var settings

    var body: some View {
        let tile = ChoiceGridMetrics.tile(count: 4, ratio: 1 / max(0.2, aspect), width: width)
        VStack(alignment: .leading, spacing: 12) {
            ChoiceHeader(title: "Placement", value: settings.motionPlacement.title)
            HStack(alignment: .top, spacing: ChoiceGridMetrics.gap) {
                ForEach(Settings.MotionPlacement.allCases) { placement in
                    ChoiceTile(name: placement.title, chosen: settings.motionPlacement == placement, size: tile,
                               hint: placement.detail, action: { settings.motionPlacement = placement }) {
                        PlacementPicture(placement: placement, cover: cover)
                    }
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Placement")
            Text(settings.motionPlacement.detail)
                .font(LunalyrDesign.Studio.TypeScale.caption)
                .foregroundStyle(Color.studio(.ink2))
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

/// The square cover on a wide screen: filled (centred, top, bottom) or whole.
struct PlacementPicture: View {
    let placement: Settings.MotionPlacement
    var cover: NSImage?

    var body: some View {
        GeometryReader { proxy in
            let w = proxy.size.width, h = proxy.size.height
            ZStack {
                Color.black
                if placement == .fit {
                    art.frame(width: h, height: h)
                } else {
                    art.frame(width: w, height: w).offset(y: offset(w: w, h: h))
                }
            }
            .frame(width: w, height: h)
            .clipped()
        }
        .accessibilityHidden(true)
    }

    @ViewBuilder private var art: some View {
        if let cover {
            Image(nsImage: cover).resizable()
        } else {
            SampleWallpaper()
        }
    }

    private func offset(w: CGFloat, h: CGFloat) -> CGFloat {
        switch placement {
        case .fillTop: return (w - h) / 2
        case .fillBottom: return -(w - h) / 2
        default: return 0
        }
    }
}
