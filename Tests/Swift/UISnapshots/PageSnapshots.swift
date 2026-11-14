import AppKit
import SwiftUI
@testable import VersoCustom

/// The eight pages without a picture (SPEC.md §8), each beside a plain
/// sidebar-coloured strip at the redesign's 1100 x 720 window, and each whole
/// page. They draw the page views themselves, so they show the form language
/// whatever the window around them does. Owned by the pages builder.
@MainActor
extension UISnapshotCatalogue {
    static var pages: [UISnapshotCase] {
        let screen = LunalyrDesign.Studio.Layout.defaultWindow
        let tall = CGSize(width: screen.width, height: 1300)
        var cases: [UISnapshotCase] = []
        for page in PageSnapshotPage.allCases {
            cases.append(UISnapshotCase("pages/\(page.rawValue)", note: "1100 x 720, a song playing",
                                        chrome: .plain, size: .frame(screen),
                                        looks: [.light, .dark, .lightContrast, .darkContrast],
                                        player: .playing, prepare: page.prepare,
                                        view: { _ in PageSnapshotFrame(page: page) }))
            cases.append(UISnapshotCase("pages/\(page.rawValue)-full", note: "the whole page",
                                        chrome: .plain, size: .frame(tall), player: .playing, prepare: page.prepare,
                                        view: { _ in PageSnapshotFrame(page: page) }))
        }
        cases.append(UISnapshotCase("pages/timing-sources-nothing-playing", note: "nothing playing",
                                    chrome: .plain, size: .frame(tall), player: .idle,
                                    view: { _ in PageSnapshotFrame(page: .timing) }))
        cases.append(UISnapshotCase("pages/general-bold", note: "Window style Bold",
                                    chrome: .plain, size: .frame(screen), player: .playing,
                                    view: { _ in PageSnapshotFrame(page: .general).environment(\.themeStyleOverride, .bold) }))
        return cases
    }
}

enum PageSnapshotPage: String, CaseIterable {
    case timing = "timing-sources", romanisation, players = "music-players", general
    case menuBar = "menu-bar-shortcuts", share = "discord-lyric-card", performance, about

    @MainActor var prepare: (UISnapshotScene) -> Void {
        switch self {
        case .share: return { scene in scene.extras.showsDiscordPresence = true }
        default: return { _ in }
        }
    }
}

/// A page beside a sidebar-coloured strip, on the window fill.
@MainActor
struct PageSnapshotFrame: View {
    let page: PageSnapshotPage

    var body: some View {
        HStack(spacing: 0) {
            Color.studio(.sidebar)
                .frame(width: LunalyrDesign.Studio.Layout.sidebarWidth)
            content
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .background(Color.studio(.window))
    }

    @ViewBuilder private var content: some View {
        switch page {
        case .timing: TimingPage()
        case .romanisation: ReadingsPage()
        case .players: MusicPlayersPage()
        case .general: GeneralPage()
        case .menuBar: ExtrasInspector()
        case .share: ShareInspector()
        case .performance: PerformancePage()
        case .about: AboutPage()
        }
    }
}
