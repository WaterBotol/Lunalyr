import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Nothing in a Look inspector may be wider than the inspector: one row that
/// is pushes the whole column past the window's edge and clips every row.
/// Every More options is drawn open, for every wallpaper type and both lyric
/// surfaces, on a throwaway settings store.
@MainActor
final class MoreOptionsWidthTests: XCTestCase {
    /// The inspector less its 20-point sides.
    private let available = LunalyrDesign.Studio.Layout.inspectorWidth - 40

    private func measure<V: View>(_ name: String, _ scene: UISnapshotScene, _ view: V,
                                  file: StaticString = #filePath, line: UInt = #line) {
        let host = NSHostingController(rootView: view
            .environment(\.moreOptionsOpen, true)
            .environment(\.inspectorMeasuring, true)
            .lunalyrModels(scene.models))
        let width = host.sizeThatFits(in: CGSize(width: available, height: 20_000)).width
        XCTAssertLessThanOrEqual(width, available + 0.5,
                                 "\(name) is \(width) pt wide; the inspector has \(available) pt",
                                 file: file, line: line)
    }

    func testMusicVideoMoreOptionsFitTheInspector() {
        let scene = UISnapshotScene(player: .playing)
        defer { scene.tearDown() }
        measure("Music video's More options", scene, VStack(alignment: .leading) { MusicVideoMoreOptions() })
    }

    func testEveryWallpaperTypeFitsTheInspector() {
        for mode in Settings.BackgroundMode.allCases {
            let scene = UISnapshotScene(player: .playing)
            scene.settings.backgroundMode = mode
            measure("Wallpaper (\(mode.title))", scene,
                    WallpaperPage(showPerformance: {}, scrollRequest: nil, importer: WallpaperImporter()))
            scene.tearDown()
        }
    }

    func testTextAnimationAndCoversFitTheInspector() {
        for surface in LyricsSurface.allCases {
            let scene = UISnapshotScene(player: .playing)
            let floating = FloatingLyricsOptions(defaults: scene.defaults)
            floating.surface = surface
            measure("Text (\(surface.title))", scene, LyricStylePage(floating: floating))
            measure("Animation (\(surface.title))", scene, AnimationPage(floating: floating))
            scene.tearDown()
        }
        let scene = UISnapshotScene(player: .playing)
        defer { scene.tearDown() }
        measure("Animated covers", scene, CoversPage())
    }
}
