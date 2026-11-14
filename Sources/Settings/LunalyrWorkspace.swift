import AppKit
import SwiftUI

// The accent (`Color.lunalyrAccent`, `LunalyrAccent`) lives with the other
// design tokens in Sources/UI/Design/DesignTokens.swift.

/// The Settings window: the sidebar, then either the picture (the Look
/// pages: canvas and inspector) or a page on the window's fill. The page and
/// the window frame are remembered between openings. Other surfaces open a
/// page through `LunalyrNavigation` (`openSettings(_:)`); the window follows
/// each request, scrolling to the section a destination names.
@MainActor
struct SettingsWindowView: View {
    typealias Page = SettingsPage

    @ObservedObject var previewDisplay: SettingsPreviewDisplay
    @AppStorage("custom.settingsPage") private var page: SettingsPage = .wallpaper
    @Model(\.navigation) private var navigation
    @State private var scrollRequest: SettingsScrollRequest?
    @State private var scrollSerial = 0
    /// The Look page last shown, so the Look row opens it again.
    @State private var lastLook: SettingsPage = .wallpaper
    /// Where each page was scrolled to, so a page revisited opens there.
    @State private var scrollMemory = FormScrollMemory()

    var body: some View {
        HStack(spacing: 0) {
            SettingsSidebar(selection: pageSelection, lastLook: lastLook)
            SettingsDetail(page: page, previewDisplay: previewDisplay, scrollRequest: scrollRequest, select: select)
                .environment(\.formScrollMemory, scrollMemory)
        }
        .background(Color.studio(.window))
        .ignoresSafeArea(.container, edges: .top)
        .lunalyrTint()
        .frame(minWidth: SettingsWindowController.minimumWidth, minHeight: SettingsWindowController.minimumHeight)
        .onAppear {
            if LookObject(page: page) != nil { lastLook = page }
            followNavigation()
        }
        .onChange(of: page) { _, page in
            if LookObject(page: page) != nil { lastLook = page }
        }
        .onChange(of: navigation.settingsRequest) { _, _ in followNavigation() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didChangeScreenParametersNotification)) { _ in
            previewDisplay.refresh()
        }
        // Each Space can have its own desktop picture.
        .onReceive(NSWorkspace.shared.notificationCenter.publisher(for: NSWorkspace.activeSpaceDidChangeNotification)) { _ in
            previewDisplay.refresh()
        }
    }

    /// The page the person picks. A section another surface asked for was
    /// for that one visit: later visits open where the page was left.
    private var pageSelection: Binding<SettingsPage> {
        Binding(get: { page }, set: { select($0) })
    }

    private func select(_ next: SettingsPage) {
        scrollRequest = nil
        page = next
    }

    /// Shows the page another surface asked for, scrolled to its section
    /// when the destination names one.
    private func followNavigation() {
        guard let destination = navigation.consumeSettingsRequest() else { return }
        page = SettingsPage(destination)
        if let anchor = SettingsAnchor(destination) {
            scrollSerial += 1
            scrollRequest = SettingsScrollRequest(anchor: anchor, serial: scrollSerial)
        } else {
            scrollRequest = nil
        }
    }
}

/// The detail beside the sidebar: for a Look page, the picture and its
/// inspector (`LookWorkspace`); for any other page, its title in a 56-point
/// band and the page in one column, 40 points from the sidebar and at most
/// 600 wide (spec §8); About on its own.
@MainActor
struct SettingsDetail: View {
    let page: SettingsPage
    @ObservedObject var previewDisplay: SettingsPreviewDisplay
    let scrollRequest: SettingsScrollRequest?
    let select: (SettingsPage) -> Void
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    /// The page column's width and its distance from the sidebar.
    static let columnWidth: CGFloat = 600
    static let columnInset: CGFloat = 40
    /// `ThemedForm` pads its content by this much on each side.
    static let formPadding: CGFloat = 20

    /// The scroll content's side margins in a detail `width` wide: the
    /// column starts 40 from the sidebar and stops at 600 wide.
    static func margins(for width: CGFloat) -> (leading: CGFloat, trailing: CGFloat) {
        let leading = columnInset - formPadding
        let trailing = max(leading, width - columnInset - columnWidth - formPadding)
        return (leading, trailing)
    }

    var body: some View {
        Group {
            if let object = LookObject(page: page) {
                LookWorkspace(object: object, previewDisplay: previewDisplay, scrollRequest: scrollRequest, select: select)
                    .transition(.opacity)
            } else if page == .about {
                AboutPage()
                    .padding(.top, 28)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .transition(.opacity)
            } else {
                pageWithTitle
                    .transition(.opacity)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                   value: LookObject(page: page) == nil ? page.rawValue : "look")
    }

    private var pageWithTitle: some View {
        GeometryReader { proxy in
            let margins = Self.margins(for: proxy.size.width)
            VStack(alignment: .leading, spacing: 0) {
                Text(page.title)
                    .font(LunalyrDesign.Studio.TypeScale.title)
                    .foregroundStyle(Color.studio(.ink))
                    .accessibilityAddTraits(.isHeader)
                    .frame(height: 56)
                    .padding(.leading, Self.columnInset)
                    .padding(.top, 12)
                SettingsStatusHeader()
                pageContent
                    .contentMargins(.leading, margins.leading, for: .scrollContent)
                    .contentMargins(.trailing, margins.trailing, for: .scrollContent)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            .frame(width: proxy.size.width, height: proxy.size.height, alignment: .topLeading)
            .focusSection()
        }
        .background(Color.studio(.window))
    }

    @ViewBuilder private var pageContent: some View {
        Group {
            switch page {
            case .lyrics: TimingPage()
            case .readings: ReadingsPage()
            case .players: MusicPlayersPage(scrollRequest: scrollRequest)
            case .general: GeneralPage()
            case .extras: ExtrasInspector()
            case .share: ShareInspector()
            case .performance: PerformancePage()
            case .wallpaper, .typography, .covers, .about: EmptyView()
            }
        }
        .id(page)
        .environment(\.formScrollKey, page.rawValue)
        .environment(\.formScrollRestores, scrollRequest == nil)
    }
}

/// On a page without a picture, what the picture would have said: the
/// paused or hidden lyrics chip and Music's access banner, above the page.
@MainActor
private struct SettingsStatusHeader: View {
    @Model(\.extras) private var extras
    @Model(\.nowPlaying) private var playing

    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let chip = WallpaperStateNote.state(paused: extras.wallpaperPaused, hiddenByMirroring: extras.hiddenByMirroring)
        let banner = AutomationStatusBanner.content(for: playing) != nil
        // Always here, so the chip and banner fade in and out as on the stage.
        VStack(spacing: 0) {
            if chip != nil || banner {
                VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.medium) {
                    if chip != nil { WallpaperStateNote().transition(.opacity) }
                    if banner { AutomationStatusBanner().transition(.opacity) }
                }
                .frame(maxWidth: SettingsDetail.columnWidth, alignment: .leading)
                .padding(.horizontal, SettingsDetail.columnInset)
                .padding(.bottom, LunalyrDesign.Spacing.medium)
                .frame(maxWidth: .infinity, alignment: .leading)
                .focusSection()
                .transition(.opacity)
            }
        }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion), value: chip)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion), value: banner)
    }
}

/// Workspace wording that depends on settings, kept out of the views so
/// tests can hold it to what the renderer actually does.
@MainActor
enum WorkspaceCopy {
    /// What Match the music does with the layout.
    static let automaticLayoutDetail =
        "Picks Constellation, Echo, Editorial or Scattered to suit each track’s energy and feel."

    /// Why "Reveal sung words" cannot be switched off, if it cannot.
    static func revealLockReason(_ lock: Settings.WordRevealLock?) -> String? {
        switch lock {
        case .layout(let style)?: return "\(style.title) builds each line from the words as they’re sung, so they’re always revealed."
        case .entrance(let arrival)?: return "\(arrival.title) unveils each word as it’s sung, so this stays on."
        case .mix?: return "Your custom mix includes entrances that unveil each word as it’s sung, so this stays on."
        case nil: return nil
        }
    }

    /// How "Add Editorial typography" relates to the chosen layout.
    static func editorialNote(layout: LyricComposition?) -> String? {
        guard let layout else {
            return "Editorial picks already mix two typefaces; this adds the mix to the other layouts Match the music chooses."
        }
        return layout.hasEditorialTypography
            ? "\(layout.title) already mixes two typefaces. Switch this on to set how many words take the second one."
            : nil
    }

    /// Why the Darkening slider starts higher under Increase Contrast, and
    /// whether a lighter choice is waiting for it to be switched off.
    static func increasedContrastDarkening(preferred: Double, increaseContrast: Bool) -> String? {
        guard increaseContrast else { return nil }
        let start = "Increase Contrast is on, so Darkening starts at a stronger setting."
        return preferred < Settings.increasedContrastScrimFloor
            ? start + " Your lighter setting returns when it’s off."
            : start
    }

    /// Why no animated cover is looked up for the playing song, when none
    /// is: covers come from Apple Music and are looked up only for songs
    /// playing in Music (`usesAppleCatalogue`), never for a stream. Saying
    /// "no animated cover found" there would claim a search that never ran.
    static func coverLookupSkipped(track: NowPlayingMonitor.Track, player: PlayerKind?) -> String? {
        if let player, !player.backend.usesAppleCatalogue {
            return "Animated covers come from Apple Music, so they play only for songs playing in Music."
        }
        return track.isStream ? "Radio streams have no animated cover." : nil
    }

    /// The same page with nothing playing. Telling someone who follows
    /// Spotify alone to play an album in Music would send them to a player
    /// Lunalyr doesn't read.
    static func coverIdlePrompt(followed: [PlayerKind]) -> String {
        followed.contains { $0.backend.usesAppleCatalogue }
            ? "Play an album in Music to check its cover."
            : "Animated covers come from Apple Music, and Lunalyr follows \(PlayerKind.names(followed)) only."
    }

    /// What becomes of an imported picture or video. Reduce Motion holds a
    /// video on a still frame, so the caption says that instead of
    /// promising a loop.
    static func importedWallpaperDetail(reduceMotion: Bool) -> String {
        let videos = reduceMotion ? "Reduce Motion is on, so videos hold on a still frame." : "Videos loop silently."
        return "Images stay still. \(videos) Lunalyr keeps a copy so moving the original won’t break your wallpaper."
    }

    /// The imported file's name under File, or why there is none to show.
    static func importedFileLabel(name: String, path: String,
                                  available: (String) -> Bool = CustomWallpaperStore.isAvailable) -> String {
        if path.isEmpty { return "None chosen" }
        if !available(path) {
            return name.isEmpty ? "The imported file is missing. Choose it again."
                : "“\(name)” is missing. Choose it again."
        }
        return name.isEmpty ? "Imported file" : name
    }

    /// Under System audio while power saving shows another wallpaper type:
    /// capture only runs while a visualiser is on screen.
    static func systemAudioWaits(showing effective: Settings.BackgroundMode) -> String {
        "Available once Visualisers return; power saving is showing \(effective.title) for now."
    }

    static let reduceMotionEntrances = "Reduce Motion is on, so every entrance plays as a simple fade."
    /// The wallpaper shows the album's still cover instead of its animation.
    static let reduceMotionCovers = "Reduce Motion is on, so animated covers show as stills."
    static let randomisedTypeface = "While Randomise is on, synced lyrics use the mix; this typeface still sets titles, unsynced lyrics, Echo’s repeats and the hover controls."
}
