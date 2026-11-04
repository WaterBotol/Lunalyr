import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

@MainActor
final class WallpaperViewportTests: XCTestCase {
    private final class Probe { weak var view: NSView? }
    private struct BackgroundProbe: NSViewRepresentable {
        let probe: Probe
        func makeNSView(context: Context) -> NSView {
            let view = NSView()
            probe.view = view
            return view
        }
        func updateNSView(_ view: NSView, context: Context) {}
    }
    private func stage(lyrics: CGSize, probe: Probe) -> some View {
        WallpaperViewport(size: CGSize(width: 640, height: 360)) {
            BackgroundProbe(probe: probe)
        } lyrics: {
            Text("A lyric line").fixedSize().frame(width: lyrics.width, height: lyrics.height)
        }
    }
    func testChangingLyricBoundsCannotMoveOrResizeWallpaper() throws {
        let probe = Probe()
        let hosting = NSHostingView(rootView: stage(lyrics: .init(width: 40, height: 60), probe: probe))
        hosting.sizingOptions = []
        let window = NSWindow(contentRect: .init(x: 0, y: 0, width: 640, height: 360),
            styleMask: .borderless, backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = hosting
        defer { window.close() }
        for lyrics in [CGSize(width: 40, height: 60), .init(width: 3000, height: 2200), .init(width: 120, height: 180)] {
            hosting.rootView = stage(lyrics: lyrics, probe: probe)
            hosting.layoutSubtreeIfNeeded()
            let view = try XCTUnwrap(probe.view)
            let rect = view.convert(view.bounds, to: hosting)
            XCTAssertEqual(rect.minX, 0, accuracy: 0.5)
            XCTAssertEqual(rect.minY, 0, accuracy: 0.5)
            XCTAssertEqual(rect.width, 640, accuracy: 0.5)
            XCTAssertEqual(rect.height, 360, accuracy: 0.5)
        }
    }
}
