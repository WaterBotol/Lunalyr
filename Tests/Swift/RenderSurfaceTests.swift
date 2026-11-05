import AppKit
import Combine
import XCTest
@testable import VersoCustom

/// The surfaces System audio follows are the frame clocks themselves. Each
/// test keeps its own registry, and no window is ever put on screen.
final class RenderSurfaceTests: XCTestCase {
    @MainActor
    private func window() -> NSWindow {
        let window = NSWindow(contentRect: CGRect(x: 0, y: 0, width: 64, height: 40), styleMask: .borderless,
                              backing: .buffered, defer: true)
        window.isReleasedWhenClosed = false
        return window
    }

    /// Lets the main queue run what is already queued on it.
    @MainActor
    private func drainMainQueue() async {
        await withCheckedContinuation { continuation in DispatchQueue.main.async { continuation.resume() } }
    }

    @MainActor
    func testEveryAttachedFrameClockIsListedByItsOwnRule() async {
        let surfaces = RenderSurfaces()
        XCTAssertEqual(surfaces.states(pauseWhenCovered: true, wallpaperPaused: false), [])

        // A wallpaper whose display coverage has not hidden draws.
        let wallpaper = WindowRenderActivity(surfaces: surfaces)
        let desktop = window()
        wallpaper.start(window: desktop, displayID: 4_242)
        XCTAssertEqual(surfaces.states(pauseWhenCovered: true, wallpaperPaused: false), [.drawing])

        // The Settings preview follows its own window, which is not on
        // screen here, so it does not draw -- whatever the preference says.
        let preview = WindowRenderActivity(surfaces: surfaces)
        let settings = window()
        preview.follow(window: settings)
        XCTAssertEqual(Set(surfaces.states(pauseWhenCovered: false, wallpaperPaused: false)), [.hidden, .drawing])
        XCTAssertEqual(surfaces.states(pauseWhenCovered: false, wallpaperPaused: false).count, 2)

        // Changes reach listeners once per burst, after every surface has
        // its new state; detached surfaces are forgotten.
        await drainMainQueue()
        var announcements = 0
        let watch = surfaces.changes.sink { announcements += 1 }
        preview.stop()
        wallpaper.stop()
        XCTAssertEqual(surfaces.states(pauseWhenCovered: true, wallpaperPaused: false), [])
        XCTAssertEqual(announcements, 0, "Delivered on the next main-queue turn")
        await drainMainQueue()
        XCTAssertEqual(announcements, 1)
        watch.cancel()
        desktop.close()
        settings.close()
    }

    @MainActor
    func testAReleasedSurfaceDropsOutWithoutBeingStopped() {
        let surfaces = RenderSurfaces()
        let desktop = window()
        var wallpaper: WindowRenderActivity? = WindowRenderActivity(surfaces: surfaces)
        wallpaper?.start(window: desktop, displayID: 4_243)
        XCTAssertEqual(surfaces.states(pauseWhenCovered: true, wallpaperPaused: false).count, 1)
        wallpaper = nil
        XCTAssertEqual(surfaces.states(pauseWhenCovered: true, wallpaperPaused: false), [])
        desktop.close()
    }

    @MainActor
    func testAPreviewWithoutAWindowIsNeverListed() {
        let surfaces = RenderSurfaces()
        let preview = WindowRenderActivity(surfaces: surfaces)
        preview.follow(window: nil)
        XCTAssertEqual(surfaces.states(pauseWhenCovered: true, wallpaperPaused: true), [],
                       "Nothing to draw in, so it neither counts as drawing nor as covered")
    }

    /// Pause wallpaper freezes the wallpapers, which their frame clocks and
    /// System audio both read from the same rule, while the Settings preview
    /// is left to its own window: hidden here, as it is never on screen, but
    /// never frozen.
    @MainActor
    func testPauseWallpaperFreezesTheWallpapersButNotThePreview() {
        let surfaces = RenderSurfaces()
        let wallpaper = WindowRenderActivity(surfaces: surfaces)
        let desktop = window()
        wallpaper.start(window: desktop, displayID: 4_244)
        let preview = WindowRenderActivity(surfaces: surfaces)
        let settings = window()
        preview.follow(window: settings)

        XCTAssertEqual(wallpaper.state(pauseWhenCovered: false, wallpaperPaused: true), .frozen)
        XCTAssertFalse(wallpaper.shouldRender(pauseWhenCovered: false, wallpaperPaused: true))
        XCTAssertTrue(wallpaper.shouldRender(pauseWhenCovered: false, wallpaperPaused: false))
        XCTAssertEqual(preview.state(pauseWhenCovered: false, wallpaperPaused: true), .hidden)
        XCTAssertEqual(Set(surfaces.states(pauseWhenCovered: false, wallpaperPaused: true)), [.frozen, .hidden])
        XCTAssertFalse(AudioCaptureGate.frozen(surfaces.states(pauseWhenCovered: false, wallpaperPaused: true),
                                               wallpaperPaused: true), "The preview may be seen again at any moment")

        // With Settings closed, only the frozen wallpaper is left.
        preview.stop()
        XCTAssertTrue(AudioCaptureGate.frozen(surfaces.states(pauseWhenCovered: false, wallpaperPaused: true),
                                              wallpaperPaused: true))
        wallpaper.stop()
        desktop.close()
        settings.close()
    }
}
