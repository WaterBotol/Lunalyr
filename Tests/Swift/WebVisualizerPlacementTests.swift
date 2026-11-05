import AppKit
import MetalKit
import SwiftUI
import WebKit
import XCTest
@testable import VersoCustom

/// Where a web scene's surface draws, and the regression that left every web
/// visualiser black on the desktop with "Heavy scenes on main display only".
final class WebVisualizerPlacementTests: XCTestCase {
    func testOnlyThePreviewAndOtherDisplaysSkipTheLivePage() {
        let main: CGDirectDisplayID = 1, other: CGDirectDisplayID = 2
        func place(_ preview: Bool, _ heavy: Bool, _ display: CGDirectDisplayID?) -> WebVisualizerPlacement {
            WebVisualizerView.placement(preview: preview, heavyScenesOnMainDisplayOnly: heavy, display: display,
                                        mainDisplay: main)
        }
        for heavy in [false, true] {
            XCTAssertEqual(place(true, heavy, nil), .preview)
            XCTAssertEqual(place(true, heavy, main), .preview)
            XCTAssertEqual(place(false, heavy, main), .live, "The main display always draws the live scene")
            XCTAssertEqual(place(false, heavy, nil), .live, "An unknown display draws the scene, never nothing")
        }
        XCTAssertEqual(place(false, false, other), .live)
        XCTAssertEqual(place(false, true, other), .standIn)
    }

    @MainActor
    func testMainDisplayFollowsTheScreenConfiguration() async throws {
        final class Box { var id: CGDirectDisplayID = 7 }
        let box = Box(), center = NotificationCenter()
        let monitor = MainDisplayMonitor(center: center) { box.id }
        XCTAssertEqual(monitor.id, 7)
        box.id = 9
        center.post(name: NSApplication.didChangeScreenParametersNotification, object: nil)
        try await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(monitor.id, 9, "A new main display moves the live scene to it")
    }

    @MainActor
    func testMainDisplayWallpaperKeepsItsLivePage() throws {
        let wallpaper = try HostedWallpaper(display: CGMainDisplayID())
        defer { wallpaper.close() }
        let first = try XCTUnwrap(wallpaper.webView(after: 0.4), "The main display's wallpaper starts the live page")
        XCTAssertTrue(wallpaper.webView(after: 1.2) === first,
                      "The same page keeps running; it used to be torn down about 10 ms after it started")
    }


    @MainActor
    func testOtherDisplayWallpaperShowsTheNativeStandIn() throws {
        let wallpaper = try HostedWallpaper(display: CGMainDisplayID() &+ 4242)
        defer { wallpaper.close() }
        XCTAssertNil(wallpaper.webView(after: 0.6), "No web page for a display that shows the stand-in")
        XCTAssertNotNil(wallpaper.first(MTKView.self), "The closest native scene draws instead")
    }
}

/// The wallpaper view hosted the way DesktopWallpaperController hosts it, with
/// its own settings: Wavecore, "Heavy scenes on main display only" on.
@MainActor
private final class HostedWallpaper {
    private let window: NSWindow
    private let host: NSView
    private let activity = WindowRenderActivity()
    private let suite = "WebVisualizerPlacementTests.\(UUID().uuidString)"

    init(display: CGDirectDisplayID) throws {
        let screen = try XCTUnwrap(NSScreen.screens.first { $0.versoDisplayID == CGMainDisplayID() } ?? NSScreen.screens.first,
                                   "Needs a display")
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let settings = Settings(defaults: defaults, notifications: NotificationCenter())
        settings.backgroundMode = .visualizer
        settings.audioVisualizerStyle = .wavecore
        settings.lowPowerVisuals = false
        settings.respectLowPower = false
        settings.onBattery = .full
        try XCTSkipIf(settings.sparesGPU, "Power saving would swap the visualiser")
        let preferences = VisualizerPreferences(defaults: defaults)
        preferences.heavyScenesOnMainDisplayOnly = true

        let frame = CGRect(x: screen.frame.minX + 24, y: screen.frame.minY + 24, width: 320, height: 200)
        window = NSWindow(contentRect: CGRect(origin: .zero, size: frame.size), styleMask: .borderless,
                          backing: .buffered, defer: false, screen: screen)
        window.setFrame(frame, display: false)
        window.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) - 1)
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenNone]
        window.isOpaque = true
        window.backgroundColor = .black
        window.isReleasedWhenClosed = false
        let view = NowPlayingView(displayID: display, renderActivity: activity, settings: settings,
                                  visualizerPreferences: preferences)
            .environmentObject(NowPlayingMonitor.shared)
        let hosting = NSHostingView(rootView: view)
        hosting.frame = CGRect(origin: .zero, size: frame.size)
        host = hosting
        window.contentView = hosting
        window.orderFrontRegardless()
        activity.start(window: window, displayID: display)
    }

    /// The web view in the window once `seconds` have passed.
    func webView(after seconds: Double) -> WKWebView? {
        let end = Date().addingTimeInterval(seconds)
        while Date() < end { RunLoop.main.run(until: min(end, Date().addingTimeInterval(0.01))) }
        return first(WKWebView.self)
    }

    func first<View: NSView>(_ type: View.Type) -> View? { Self.find(type, in: host) }

    private static func find<View: NSView>(_ type: View.Type, in view: NSView) -> View? {
        if let match = view as? View { return match }
        for sub in view.subviews { if let match = find(type, in: sub) { return match } }
        return nil
    }

    func close() {
        activity.stop()
        window.orderOut(nil)
        window.contentView = nil
        window.close()
        UserDefaults().removePersistentDomain(forName: suite)
    }
}
