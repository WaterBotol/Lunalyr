import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

// UI snapshots: the Settings window, the menu-bar panel, onboarding and the
// design-system gallery, drawn offscreen to PNG in light and dark at 13-inch
// MacBook Air sizes, against isolated stores and fake players. Nothing opens
// on screen, no Apple event is sent, no permission is asked for, and the
// app's own preferences are never read or written.
//
// RUN: Scripts/render-ui.sh <absolute outdir> [filter] [looks]
//   Scripts/render-ui.sh /tmp/ui                    every case, light and dark
//   Scripts/render-ui.sh /tmp/ui menu-bar           cases whose name contains "menu-bar"
//   Scripts/render-ui.sh /tmp/ui settings/wallpaper light,dark-contrast
// Then look at the PNGs (and <outdir>/index.md, which lists them with their
// sizes and notes). Without LUNALYR_UI_SNAPSHOT_DIR, as in the normal test run,
// `testRenderUISnapshots` skips at once; `testTheHarnessDrawsIsolatedModels`
// keeps the harness honest there.
//
// ADD A VIEW OR A STATE: append a `UISnapshotCase` to your surface's list.
// Each surface has a file of its own, so builders never edit the same one:
//   SettingsSnapshots.swift      UISnapshotCatalogue.settings    (Settings window)
//   MenuBarSnapshots.swift       UISnapshotCatalogue.menuBar     (menu-bar panel)
//   OnboardingSnapshots.swift    UISnapshotCatalogue.onboarding  (first run)
//   DesignSystemSnapshots.swift  UISnapshotCatalogue.designSystem (Sources/UI/Design)
//   LyricCardSnapshots.swift     UISnapshotCatalogue.lyricCard   (the lyric card window)
// A case is a name (its path under the outdir, no extension), a note for the
// index, the chrome and size to draw in, the fake player state, a `prepare`
// that sets the isolated stores up, and the view:
//
//     UISnapshotCase("menu-bar/paused-wallpaper", note: "wallpaper paused",
//                    chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
//                    prepare: { scene in scene.extras.wallpaperPaused = true },
//                    view: { _ in MenuBarView() })
//
// `prepare` and `view` get a `UISnapshotScene` (Support/UISnapshotModels.swift):
// `scene.settings`, `.extras`, `.hotkeys`, `.players`, `.colours`, `.offsets`,
// `.audio`, `.harness` (the fake Music and Spotify), `.navigation` (records
// requests in `scene.navigated` instead of opening anything), `.lyricNow` (the
// line being sung, as the menu panel quotes it), and helpers:
// `showSettingsPage(_:)`, `setSongOffset(_:)`, `audioNeedsAttention(_:)`,
// `hideLyricsForMirroring()`. Player states: `.idle` (Music open, nothing
// playing), `.playing`, `.paused`, `.denied` (refused in Automation),
// `.needsConsent` (macOS hasn't asked), `.notRunning` (no player open).
//
// The view is drawn under `.lunalyrModels(scene.models)` and
// `.defaultAppStorage(scene.defaults)`, so product views must read stores
// through `@Model` / `@Environment(\.lunalyrModels)` (see
// Sources/UI/Design/LunalyrModels.swift), never `.shared`: a `.shared` read
// draws the test host's own state instead of the scene's.
//
// SIZES (`UISnapshotCase.Size`): `.settingsWindow` is the frame Settings first
// opens at on a 13-inch Air (1470 x 956 points, Dock showing);
// `.settingsWindowLargerText` the same Air at its Larger Text resolution;
// `.settingsWindowLarge` the 1200 x 780 the spec allows at most; `.settingsPage`
// the whole scrolling page at the default width; `.menuBarPanel` the panel at
// its own width; `.onboarding(width:)` a window that wide, as tall as its
// content; `.fitting(width:)` and `.frame(_:)` anything else.
//
// LOOKS: light and dark by default; `light-contrast` and `dark-contrast` draw
// with the Increase Contrast appearances, and Lunalyr's own dynamic colours
// (DesignTokens.swift) as under Increase Contrast. SwiftUI's
// `colorSchemeContrast` still reads the system setting, so components should
// take their contrast from those colours (`Color.lunalyrContrastBorder`,
// `LunalyrStatus.border`). Reduce Transparency can't be simulated here: check
// materials by hand.
//
// LIMITS: live wallpaper, Metal and WebKit content draws
// `LiveContentPlaceholder` (the environment's `lunalyrRendersLiveContent` is
// false). Materials and Liquid Glass are flat stand-ins, and sidebar
// selections a neutral fill; see Support/UISnapshotRenderer.swift. The lyric
// view model and lyric sources are the app's shared ones (their files belong
// to the lyrics work), so pages that show lookup results draw them empty.
//
// Environment (set by render-ui.sh from its arguments):
//   LUNALYR_UI_SNAPSHOT_DIR     absolute output directory (required)
//   LUNALYR_UI_SNAPSHOT_FILTER  comma-separated substrings of case names
//   LUNALYR_UI_SNAPSHOT_LOOKS   light,dark,light-contrast,dark-contrast
//   LUNALYR_UI_SNAPSHOT_DEBUG=1 prints the window's materials and glass
// Output: <dir>/<case name>-<look>.png and <dir>/index.md listing them.

final class UISnapshotTests: XCTestCase {
    @MainActor
    func testRenderUISnapshots() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let path = environment["LUNALYR_UI_SNAPSHOT_DIR"], !path.isEmpty else {
            throw XCTSkip("Renders only for Scripts/render-ui.sh, which sets LUNALYR_UI_SNAPSHOT_DIR.")
        }
        // The test host runs in its own folder with a throwaway home.
        guard path.hasPrefix("/") else {
            XCTFail("LUNALYR_UI_SNAPSHOT_DIR must be an absolute path, not \(path)")
            return
        }
        let directory = URL(fileURLWithPath: path, isDirectory: true)
        let filters = (environment["LUNALYR_UI_SNAPSHOT_FILTER"] ?? "")
            .split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        let looks = try (environment["LUNALYR_UI_SNAPSHOT_LOOKS"]).map { list in
            try list.split(separator: ",").map { name -> UISnapshotRenderer.Look in
                guard let look = UISnapshotRenderer.Look(rawValue: name.trimmingCharacters(in: .whitespaces)) else {
                    throw XCTSkip("Unknown look \(name); use light, dark, light-contrast or dark-contrast.")
                }
                return look
            }
        }
        let all = UISnapshotCatalogue.all
        XCTAssertEqual(Set(all.map(\.name)).count, all.count, "Case names repeat")
        let cases = all.filter { snapshot in filters.isEmpty || filters.contains { snapshot.name.contains($0) } }
        XCTAssertFalse(cases.isEmpty, "No snapshot matches \(filters)")

        var index: [String] = ["# UI snapshots", "", "Rendered \(Date()) at 2x. Case, look, size in points, note.", ""]
        var count = 0
        let started = Date()
        for snapshot in cases {
            for look in looks ?? snapshot.looks {
                let scene = UISnapshotScene(player: snapshot.player)
                defer { scene.tearDown() }
                snapshot.prepare(scene)
                let view = snapshot.view(scene)
                    .lunalyrModels(scene.models)
                    .defaultAppStorage(scene.defaults)
                let image = try UISnapshotRenderer.render(view, chrome: snapshot.chrome.renderer, size: snapshot.size.renderer,
                                                          look: look, pump: scene.pump)
                let file = "\(snapshot.name)-\(look.rawValue).png"
                try UISnapshotRenderer.writePNG(image, to: directory.appendingPathComponent(file))
                let points = "\(Int(CGFloat(image.width) / UISnapshotRenderer.scale)) x \(Int(CGFloat(image.height) / UISnapshotRenderer.scale))"
                index.append("- [\(file)](\(file)) — \(points)\(snapshot.note.isEmpty ? "" : " — " + snapshot.note)")
                print("[ui-snapshot] \(file) \(points)")
                XCTAssertTrue(UISnapshotTests.hasContent(image), "\(file) came out blank")
                count += 1
            }
        }
        try Data((index.joined(separator: "\n") + "\n").utf8).write(to: directory.appendingPathComponent("index.md"))
        print(String(format: "[ui-snapshot] %ld images in %.1f s -> %@", count, Date().timeIntervalSince(started),
                     directory.path))
    }

    /// Runs in the normal suite: the harness draws real pixels from isolated
    /// stores, drawing never writes the app's own preferences, and every
    /// case's name is unique.
    @MainActor
    func testTheHarnessDrawsIsolatedModels() throws {
        let shared = VersoCustom.Settings.shared.backgroundMode
        let scene = UISnapshotScene(player: .playing)
        defer { scene.tearDown() }
        XCTAssertFalse(scene.models.settings === VersoCustom.Settings.shared)
        XCTAssertFalse(scene.models.nowPlaying === NowPlayingMonitor.shared)
        XCTAssertFalse(scene.models.players === PlayerOptions.shared)
        XCTAssertEqual(scene.models.nowPlaying.current?.title, UISnapshotScene.song.name)
        scene.settings.backgroundMode = shared == .desktop ? .visualizer : .desktop
        let image = try UISnapshotRenderer.render(
            ProbeView().lunalyrModels(scene.models).defaultAppStorage(scene.defaults),
            chrome: .none, size: .fitting(width: 320, maxHeight: 200), look: .dark, pump: scene.pump)
        XCTAssertEqual(image.width, 640, "drawn at 2x")
        XCTAssertTrue(Self.hasContent(image))
        XCTAssertEqual(VersoCustom.Settings.shared.backgroundMode, shared, "the shared settings are untouched")
        XCTAssertNil(UserDefaults.standard.object(forKey: "custom.snapshotProbe"))
        XCTAssertEqual(Set(UISnapshotCatalogue.all.map(\.name)).count, UISnapshotCatalogue.all.count, "Case names repeat")
    }

    /// Reads the scene's stores the way product views do.
    private struct ProbeView: View {
        @Model(\.settings) private var settings
        @Model(\.nowPlaying) private var playing
        @AppStorage("custom.snapshotProbe") private var probe = "isolated"

        var body: some View {
            VStack(alignment: .leading, spacing: 8) {
                Text(playing.current?.title ?? "Nothing playing").font(.headline)
                Toggle("Visualisers", isOn: .constant(settings.backgroundMode == .visualizer))
                Text(probe).foregroundStyle(.secondary)
            }
            .padding(20)
        }
    }

    /// Whether the image has more than one colour: a blank capture is uniform.
    static func hasContent(_ image: CGImage) -> Bool {
        guard let pixels = UISnapshotRenderer.pixels(image), pixels.count >= 8 else { return false }
        let first = pixels[0..<4]
        return stride(from: 0, to: pixels.count, by: 4 * 97).contains { offset in
            zip(pixels[offset..<(offset + 4)], first).contains { abs(Int($0) - Int($1)) > 8 }
        }
    }
}

// MARK: - Catalogue

/// Every case, surface by surface; each list lives in its own file.
@MainActor
enum UISnapshotCatalogue {
    static var all: [UISnapshotCase] { settings + menuBar + onboarding + designSystem + lyricCard + pages }
}

/// One snapshot: a surface in one state, drawn in each of `looks`.
struct UISnapshotCase {
    enum Chrome {
        /// The Settings window (`UISnapshotCatalogue.settingsWindowChrome`,
        /// in SettingsSnapshots.swift).
        case settingsWindow
        /// Onboarding's window (`UISnapshotCatalogue.onboardingWindowChrome`,
        /// in OnboardingSnapshots.swift).
        case onboardingWindow
        /// Any titled window: the style mask and configuration given.
        case window(styleMask: NSWindow.StyleMask, configure: @MainActor (NSWindow) -> Void)
        /// The menu-bar panel floating over a desktop-like backdrop.
        case menuBarPanel
        /// The view alone on the window background.
        case plain

        @MainActor var renderer: UISnapshotRenderer.Chrome {
            switch self {
            case .settingsWindow:
                return UISnapshotCatalogue.settingsWindowChrome
            case .onboardingWindow:
                return UISnapshotCatalogue.onboardingWindowChrome
            case .window(let styleMask, let configure):
                return .window(styleMask: styleMask, configure: configure)
            case .menuBarPanel:
                return .panel(cornerRadius: 12, margin: 28)
            case .plain:
                return .none
            }
        }
    }

    enum Size {
        /// The frame Settings first opens at on a 13-inch Air with the Dock showing.
        case settingsWindow
        /// The frame Settings first opens at on the same Air at its Larger
        /// Text resolution (1024 x 665 points).
        case settingsWindowLargerText
        /// The largest Settings window the spec allows: 1200 x 780.
        case settingsWindowLarge
        /// The whole scrolling page at the 13-inch width.
        case settingsPage
        /// The menu-bar panel at its own width, as tall as its content.
        case menuBarPanel
        /// A window `width` wide, as tall as its content.
        case onboarding(width: CGFloat)
        /// Any content `width` wide, as tall as it needs.
        case fitting(width: CGFloat)
        /// A window frame of exactly this size.
        case frame(CGSize)

        @MainActor var renderer: UISnapshotRenderer.Size {
            switch self {
            case .settingsWindow: return .frame(UISnapshotDisplay.settingsFrame.size)
            case .settingsWindowLargerText: return .frame(UISnapshotDisplay.largerTextSettingsFrame.size)
            case .settingsWindowLarge: return .frame(CGSize(width: 1200, height: 780))
            case .settingsPage: return .fullPage(width: UISnapshotDisplay.settingsFrame.width, height: 4200)
            case .menuBarPanel: return .fitting(width: 0, maxHeight: 900)
            case .onboarding(let width): return .fitting(width: width, maxHeight: 900)
            case .fitting(let width): return .fitting(width: width, maxHeight: 2400)
            case .frame(let size): return .frame(size)
            }
        }
    }

    let name: String
    var note = ""
    let chrome: Chrome
    let size: Size
    var looks: [UISnapshotRenderer.Look] = [.light, .dark]
    var player: UISnapshotScene.Player = .idle
    var prepare: @MainActor (UISnapshotScene) -> Void = { _ in }
    let view: @MainActor (UISnapshotScene) -> AnyView

    init<V: View>(_ name: String, note: String = "", chrome: Chrome, size: Size,
                  looks: [UISnapshotRenderer.Look] = [.light, .dark], player: UISnapshotScene.Player = .idle,
                  prepare: @escaping @MainActor (UISnapshotScene) -> Void = { _ in },
                  view: @escaping @MainActor (UISnapshotScene) -> V) {
        self.name = name
        self.note = note
        self.chrome = chrome
        self.size = size
        self.looks = looks
        self.player = player
        self.prepare = prepare
        self.view = { AnyView(view($0)) }
    }
}

/// The 13-inch MacBook Air every size is worked out for.
@MainActor
enum UISnapshotDisplay {
    /// "Looks like 1470 x 956", its default.
    static let screen = CGSize(width: 1470, height: 956)
    /// Less the menu bar (37 points with the notch) and a default Dock.
    static let visibleFrame = NSRect(x: 0, y: 66, width: 1470, height: 956 - 37 - 66)
    static var settingsFrame: NSRect { SettingsWindowController.initialFrame(in: visibleFrame) }
    /// "Larger Text": looks like 1024 x 665, less the menu bar and Dock (as
    /// `SettingsWindowTests` has it).
    static let largerTextVisibleFrame = NSRect(x: 0, y: 66, width: 1024, height: 550)
    static var largerTextSettingsFrame: NSRect { SettingsWindowController.initialFrame(in: largerTextVisibleFrame) }
}
