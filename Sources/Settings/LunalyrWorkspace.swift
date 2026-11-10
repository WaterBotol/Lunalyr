import AppKit
import SwiftUI

// The accent (`Color.lunalyrAccent`, `LunalyrAccent`) lives with the other
// design tokens in Sources/UI/Design/DesignTokens.swift.

/// The Settings window: pages in a sidebar, and for each page the live
/// preview on a stage beside the page's controls (above them in a narrow
/// window). The page, the preview's state and the window frame are
/// remembered between openings. Other surfaces open a page through
/// `LunalyrNavigation` (`openSettings(_:)`); the window follows each request.
@MainActor
struct SettingsWindowView: View {
    typealias Page = SettingsPage

    @ObservedObject var previewDisplay: SettingsPreviewDisplay
    @AppStorage("custom.settingsPage") private var page: SettingsPage = .wallpaper
    /// Shown from the first run: the preview is how changes are judged.
    @AppStorage("custom.settingsShowsPreview") private var showsPreview = true
    @AppStorage("custom.settingsLargePreview") private var largePreview = false
    @Model(\.navigation) private var navigation
    @State private var scrollRequest: SettingsScrollRequest?
    @State private var scrollSerial = 0
    /// Where each page was scrolled to, so a page revisited opens there.
    @State private var scrollMemory = FormScrollMemory()

    var body: some View {
        NavigationStack {
            HStack(spacing: 0) {
                SettingsSidebar(selection: pageSelection)
                Divider()
                SettingsDetail(page: page, previewDisplay: previewDisplay, showsPreview: showsPreview,
                               largePreview: largePreview, scrollRequest: scrollRequest,
                               select: select)
                    .environment(\.formScrollMemory, scrollMemory)
            }
            .styledCanvas()
            .navigationTitle(page.title)
            .toolbar { previewToolbar }
        }
        .lunalyrTint()
        .frame(minWidth: SettingsWindowController.minimumWidth, minHeight: SettingsWindowController.minimumHeight)
        .onAppear(perform: followNavigation)
        .onChange(of: navigation.settingsRequest) { _, _ in followNavigation() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didChangeScreenParametersNotification)) { _ in
            previewDisplay.refresh()
        }
        // Each Space can have its own desktop picture.
        .onReceive(NSWorkspace.shared.notificationCenter.publisher(for: NSWorkspace.activeSpaceDidChangeNotification)) { _ in
            previewDisplay.refresh()
        }
    }

    /// Larger/Smaller Preview and Show/Hide Preview, at the toolbar's
    /// trailing end. About has no preview, so neither shows there.
    @ToolbarContentBuilder private var previewToolbar: some ToolbarContent {
        if page.showsStage {
            // Pushes the preview's buttons to the trailing end.
            ToolbarItem { Spacer() }
            ToolbarItemGroup {
                if showsPreview {
                    Button {
                        largePreview.toggle()
                    } label: {
                        Label(largePreview ? "Smaller Preview" : "Larger Preview",
                              systemImage: largePreview ? "arrow.down.right.and.arrow.up.left"
                                  : "arrow.up.left.and.arrow.down.right")
                    }
                    .help(largePreview ? "Give the controls their usual room again"
                          : "Make the preview larger: the controls take less room")
                }
                Button {
                    showsPreview.toggle()
                } label: {
                    Label(showsPreview ? "Hide Preview" : "Show Preview", systemImage: showsPreview ? "eye.slash" : "eye")
                }
                .help(showsPreview ? "Hide the preview and give the controls the whole window"
                      : "Show the live preview beside the controls")
            }
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

/// The detail beside the sidebar: the stage and the page's inspector, side
/// by side, or stacked in a narrow window; the inspector alone, centred,
/// with the preview hidden; About on its own.
@MainActor
private struct SettingsDetail: View {
    let page: SettingsPage
    @ObservedObject var previewDisplay: SettingsPreviewDisplay
    let showsPreview: Bool
    let largePreview: Bool
    let scrollRequest: SettingsScrollRequest?
    let select: (SettingsPage) -> Void
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        GeometryReader { proxy in
            if page.showsStage {
                arranged(in: proxy.size)
            } else {
                AboutPage()
                    .transition(.opacity)
            }
        }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion), value: page)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion), value: showsPreview)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion), value: largePreview)
    }

    @ViewBuilder private func arranged(in size: CGSize) -> some View {
        // The stage beside the inspector in a wide detail; above it in a
        // narrow one. The axis follows the width alone, so Show/Hide Preview
        // slides the stage out along it rather than flipping the layout
        // mid-animation; crossing the width threshold still switches at once.
        let wide = Self.stageBeside(width: size.width)
        let beside = wide && showsPreview
        // One layout that switches axis, so the live preview and the page
        // keep their identity as the window narrows past the threshold.
        let layout = wide ? AnyLayout(HStackLayout(spacing: 0)) : AnyLayout(VStackLayout(spacing: 0))
        layout {
            if showsPreview {
                SettingsStage(previewDisplay: previewDisplay,
                              stacked: beside ? nil : SettingsStageLimits(detailHeight: size.height, largePreview: largePreview))
                    .transition(stageTransition(wide: wide))
                Rectangle()
                    .fill(Color(nsColor: .separatorColor))
                    .frame(width: beside ? 1 : nil, height: beside ? nil : 1)
                    .accessibilityHidden(true)
                    .transition(.opacity)
            }
            let fixedWidth = beside ? inspectorWidth : nil
            // With the preview hidden, any status sits above the page.
            VStack(spacing: 0) {
                if !showsPreview { SettingsStatusHeader() }
                inspector
                    // A wide inspector keeps its forms to a readable column.
                    .modifier(ReadableColumn(margin: fixedWidth == nil ? Self.margin(for: size.width) : nil))
            }
            .frame(width: fixedWidth)
            .frame(maxWidth: fixedWidth == nil ? .infinity : nil, maxHeight: .infinity)
            .focusSection()
        }
    }

    /// Whether a detail this wide puts the stage beside the inspector.
    static func stageBeside(width: CGFloat) -> Bool {
        width >= LunalyrDesign.Layout.stageBesideInspectorMinimum
    }

    /// The stage leaves toward the edge it sits on; a plain fade under Reduce Motion.
    private func stageTransition(wide: Bool) -> AnyTransition {
        reduceMotion ? .opacity : .move(edge: wide ? .leading : .top).combined(with: .opacity)
    }

    private var inspectorWidth: CGFloat {
        largePreview ? LunalyrDesign.Layout.inspectorWidth - 60 : LunalyrDesign.Layout.inspectorWidth
    }

    /// The side margin that keeps a full-width form's content no wider than
    /// `SettingsDetail.readableWidth`.
    static func margin(for width: CGFloat) -> CGFloat {
        max(0, ((width - readableWidth) / 2).rounded(.down))
    }

    /// The widest a page's controls get.
    static let readableWidth: CGFloat = 640

    @ViewBuilder private var inspector: some View {
        Group {
            switch page {
            case .wallpaper:
                WallpaperPage(showPerformance: { select(.performance) }, scrollRequest: scrollRequest)
            case .typography: LyricStylePage()
            case .covers: CoversPage()
            case .lyrics: TimingPage()
            case .readings: ReadingsPage()
            case .players: MusicPlayersPage(scrollRequest: scrollRequest)
            case .general: GeneralPage()
            case .extras: ExtrasInspector()
            case .share: ShareInspector()
            case .performance: PerformancePage()
            case .about: AboutPage()
            }
        }
        .id(page)
        .environment(\.formScrollKey, page.rawValue)
        .environment(\.formScrollRestores, scrollRequest == nil)
        .transition(.opacity)
    }
}

/// Insets a page's scrolling content so a wide window keeps it to a
/// readable column, with the scroller still at the window's edge.
private struct ReadableColumn: ViewModifier {
    let margin: CGFloat?

    func body(content: Content) -> some View {
        if let margin, margin > 0 {
            content.contentMargins(.horizontal, margin, for: .scrollContent)
        } else {
            content
        }
    }
}

/// With the preview hidden, what the stage would have said: the paused or
/// hidden lyrics chip and Music's access banner, above the page.
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
                .frame(maxWidth: SettingsDetail.readableWidth - 2 * LunalyrDesign.Spacing.xLarge, alignment: .leading)
                .padding(.horizontal, LunalyrDesign.Spacing.xLarge)
                .padding(.top, LunalyrDesign.Spacing.large)
                .frame(maxWidth: .infinity)
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
