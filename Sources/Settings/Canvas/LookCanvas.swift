import AppKit
import SwiftUI

// MARK: - The Look workspace: canvas and inspector
//
// Spec §1 and §3: the canvas (object switch, picture, strip, choices) on
// the darkest fill, the selected object's settings in a 286-point inspector
// beside it. Below about 1000 points of window the canvas narrows and the
// picture shrinks with it (the inspector keeps its width); in a short window
// the picture gives way to the choices, which keep their size.

@MainActor
struct LookWorkspace: View {
    let object: LookObject
    @ObservedObject var previewDisplay: SettingsPreviewDisplay
    let scrollRequest: SettingsScrollRequest?
    let select: (SettingsPage) -> Void
    @StateObject private var importer = WallpaperImporter()
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.lookFloatingOptions) private var floatingOverride

    private var floating: FloatingLyricsOptions { floatingOverride ?? .shared }

    var body: some View {
        HStack(spacing: 0) {
            LookCanvas(object: object, previewDisplay: previewDisplay, importer: importer, floating: floating,
                       select: { select($0.page) })
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Color.studio(.canvas))
                .focusSection()
            inspector
                .frame(width: LunalyrDesign.Studio.Layout.inspectorWidth)
                .frame(maxHeight: .infinity)
                .background(Color.studio(.window))
                .focusSection()
        }
    }

    private var inspector: some View {
        Group {
            switch object {
            case .wallpaper:
                WallpaperPage(showPerformance: { select(.performance) }, scrollRequest: scrollRequest, importer: importer)
            case .lyrics:
                LyricStylePage(floating: floating)
            case .covers:
                CoversPage()
            }
        }
        .id(object)
        .environment(\.formScrollKey, object.page.rawValue)
        .environment(\.formScrollRestores, scrollRequest == nil)
        .transition(.opacity)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: object)
    }
}

/// The inspector's scrolling column: 20 points in from its sides, sections
/// 22 apart, and a fade at its foot that says there is more below.
struct LookInspectorForm<Content: View>: View {
    let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        ThemedForm {
            VStack(alignment: .leading, spacing: LunalyrDesign.Studio.Layout.inspectorSectionSpacing) {
                content
            }
            .padding(.bottom, 12)
        }
        .overlay(alignment: .bottom) {
            LinearGradient(colors: [Color.studio(.window).opacity(0), Color.studio(.window)],
                           startPoint: .top, endPoint: .bottom)
                .frame(height: 28)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
        }
    }
}

// MARK: - Canvas

@MainActor
struct LookCanvas: View {
    let object: LookObject
    @ObservedObject var previewDisplay: SettingsPreviewDisplay
    @ObservedObject var importer: WallpaperImporter
    @ObservedObject var floating: FloatingLyricsOptions
    let select: (LookObject) -> Void
    @Model(\.settings) private var settings
    @Model(\.nowPlaying) private var playing
    @Model(\.extras) private var extras
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var palette = ArtworkPalette.neutral
    @State private var choicesHeight: CGFloat = 200

    static let padding: CGFloat = 22
    static let switchBand: CGFloat = 56
    static let stripHeight: CGFloat = 22
    static let stripGap: CGFloat = 12

    private var cover: NSImage? { playing.currentTrackArtwork }
    private var tilePalette: ArtworkPalette { playing.current == nil ? ChoicePalette.sample : palette }
    private var aspect: CGFloat {
        let size = previewDisplay.size
        return max(0.2, size.width / max(1, size.height))
    }

    /// The picture's size in a canvas of `size` with choices `choices` tall:
    /// as wide as the canvas allows, shorter when the window is short.
    static func pictureSize(canvas size: CGSize, aspect: CGFloat, choices: CGFloat, banner: CGFloat = 0) -> CGSize {
        let width = max(1, size.width - padding * 2)
        let room = size.height - switchBand - stripGap - stripHeight - banner - 16 - choices - padding
        let height = max(80, min(width / aspect, room))
        return CGSize(width: (height * aspect).rounded(.down), height: height.rounded(.down))
    }

    var body: some View {
        GeometryReader { proxy in
            let inner = max(1, proxy.size.width - Self.padding * 2)
            let picture = Self.pictureSize(canvas: proxy.size, aspect: aspect, choices: choicesHeight)
            VStack(spacing: 0) {
                CanvasSegments(title: "Selected on the picture", selection: Binding(get: { object }, set: { select($0) }),
                               options: LookObject.allCases.map { ($0, $0.title) })
                    .frame(height: Self.switchBand)
                LookPicture(object: object, select: select, previewDisplay: previewDisplay, floating: floating)
                    .frame(width: picture.width, height: picture.height)
                    .overlay(alignment: .topLeading) { stateChip }
                strip
                    .frame(width: inner, height: Self.stripHeight)
                    .padding(.top, Self.stripGap)
                if AutomationStatusBanner.content(for: playing) != nil {
                    AutomationStatusBanner()
                        .frame(width: inner)
                        .padding(.top, 10)
                        .transition(.opacity)
                }
                Spacer(minLength: 16)
                choices(width: inner)
                    .frame(width: inner, alignment: .leading)
                    .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { choicesHeight = $0 }
                    .id(object)
                    .transition(.opacity)
            }
            .padding(.horizontal, Self.padding)
            .padding(.bottom, Self.padding)
            .frame(width: proxy.size.width, height: proxy.size.height)
        }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: object)
        .task(id: cover.map(ObjectIdentifier.init)) {
            palette = ArtworkPalette.of(cover)
        }
    }

    @ViewBuilder private var stateChip: some View {
        if WallpaperStateNote.state(paused: extras.wallpaperPaused, hiddenByMirroring: extras.hiddenByMirroring) != nil {
            WallpaperStateNote()
                .padding(10)
                .transition(.opacity)
        }
    }

    /// The song (or that the picture is a sample) and the view switch.
    private var strip: some View {
        HStack(spacing: 8) {
            Group {
                if let track = playing.current {
                    ArtworkView(image: cover, placeholderSymbol: "music.note", size: 18)
                    Text([track.title, track.artist].filter { !$0.isEmpty }.joined(separator: " · "))
                } else {
                    Image(systemName: "music.note")
                        .font(.system(size: 10, weight: .semibold))
                        .frame(width: 18, height: 18)
                        .background(RoundedRectangle(cornerRadius: 3, style: .continuous).fill(Color.studio(.controlOnCanvas)))
                        .accessibilityHidden(true)
                    Text("Nothing playing. A sample line shows the style.")
                }
            }
            .font(.system(size: 12))
            .foregroundStyle(Color.studio(.ink2))
            .lineLimit(1)
            .truncationMode(.tail)
            Spacer(minLength: 8)
            if object != .covers {
                CanvasSegments(title: "Show lyrics", selection: $floating.surface,
                               options: LyricsSurface.allCases.map { ($0, $0.title) }, small: true)
                    .help("Where the lyrics show: on the desktop, or floating over your apps")
                    .transition(.opacity)
            }
        }
        .accessibilityElement(children: .contain)
    }

    @ViewBuilder private func choices(width: CGFloat) -> some View {
        switch object {
        case .wallpaper:
            WallpaperTypeChoices(width: width, palette: tilePalette, cover: cover,
                                 desktopPicture: previewDisplay.desktopPicture,
                                 chooseImport: { importer.choose(into: settings) })
        case .lyrics:
            LayoutTileChoices(width: width, palette: tilePalette)
        case .covers:
            PlacementChoices(width: width, aspect: aspect, cover: cover)
        }
    }
}
