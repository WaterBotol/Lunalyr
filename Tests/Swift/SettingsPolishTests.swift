import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Settings and onboarding polish (POLISH-AUDIT 2026-11-10, WP4).
@MainActor
final class SettingsPolishTests: XCTestCase {
    // MARK: S9 slider

    /// Grabbing the thumb off-centre keeps that grip: the thumb no longer
    /// jumps up to 11 points to put its centre under the pointer.
    func testGrabbingTheThumbOffCentreDoesNotJump() {
        let thumb = ThemedSliderTrack.thumb, travel: CGFloat = 300
        let centre = thumb / 2 + travel * 0.5
        let offset = ThemedSliderMath.grabOffset(start: centre + 9, fraction: 0.5, travel: travel, thumb: thumb)
        XCTAssertEqual(offset, 9)
        // The pointer hasn't moved: the value stays where it was.
        XCTAssertEqual(ThemedSliderMath.fraction(pointer: centre + 9, grabOffset: offset, travel: travel, thumb: thumb),
                       0.5, accuracy: 1e-9)
        // Moving 30 points moves the thumb 30 points.
        XCTAssertEqual(ThemedSliderMath.fraction(pointer: centre + 39, grabOffset: offset, travel: travel, thumb: thumb),
                       0.6, accuracy: 1e-9)
    }

    /// A click on the track away from the thumb brings the thumb's centre there.
    func testClickingTheTrackBringsTheThumbToThePointer() {
        let thumb = ThemedSliderTrack.thumb, travel: CGFloat = 300
        let offset = ThemedSliderMath.grabOffset(start: thumb / 2 + 240, fraction: 0.1, travel: travel, thumb: thumb)
        XCTAssertEqual(offset, 0)
        XCTAssertEqual(ThemedSliderMath.fraction(pointer: thumb / 2 + 240, grabOffset: offset, travel: travel, thumb: thumb),
                       0.8, accuracy: 1e-9)
        XCTAssertEqual(ThemedSliderMath.fraction(pointer: -50, grabOffset: 0, travel: travel, thumb: thumb), 0)
        XCTAssertEqual(ThemedSliderMath.fraction(pointer: 900, grabOffset: 0, travel: travel, thumb: thumb), 1)
    }

    func testSliderSnapsAndClamps() {
        XCTAssertEqual(ThemedSliderMath.snapped(0.37, in: 0...1, step: 0.1), 0.4, accuracy: 1e-9)
        XCTAssertEqual(ThemedSliderMath.snapped(1.4, in: 0...1, step: nil), 1)
        XCTAssertEqual(ThemedSliderMath.fraction(of: 5, in: 5...5), 0)
        XCTAssertEqual(ThemedSliderMath.fraction(of: -3, in: -10...10), 0.35, accuracy: 1e-9)
    }

    // MARK: S24 scroll memory

    private func hostForm(memory: FormScrollMemory, key: String) -> (NSWindow, NSScrollView?) {
        let form = ThemedForm {
            ForEach(0..<60, id: \.self) { index in
                Text("Row \(index)").frame(height: 30)
            }
        }
        .environment(\.formScrollMemory, memory)
        .environment(\.formScrollKey, key)
        let window = NSWindow(contentRect: NSRect(x: -30_000, y: -30_000, width: 400, height: 300),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = NSHostingView(rootView: form.frame(width: 400, height: 300))
        settle(window)
        return (window, scrollView(in: window.contentView))
    }

    private func settle(_ window: NSWindow) {
        for _ in 0..<6 {
            window.layoutIfNeeded()
            window.displayIfNeeded()
            RunLoop.main.run(until: Date().addingTimeInterval(0.05))
        }
    }

    private func scrollView(in view: NSView?) -> NSScrollView? {
        guard let view else { return nil }
        if let scroll = view as? NSScrollView { return scroll }
        for child in view.subviews { if let found = scrollView(in: child) { return found } }
        return nil
    }

    /// A page records how far it was scrolled, and opens there when visited again.
    func testAPageReopensWhereItWasLeft() throws {
        let memory = FormScrollMemory()
        let (window, found) = hostForm(memory: memory, key: "lyrics")
        let scroll = try XCTUnwrap(found)
        XCTAssertEqual(memory.offsets["lyrics"] ?? 0, 0, accuracy: 1)
        scroll.contentView.scroll(to: NSPoint(x: 0, y: 420))
        scroll.reflectScrolledClipView(scroll.contentView)
        settle(window)
        XCTAssertEqual(memory.offsets["lyrics"] ?? 0, 420, accuracy: 2, "the offset is remembered")
        window.contentView = nil
        window.close()

        let (again, reopened) = hostForm(memory: memory, key: "lyrics")
        defer { again.contentView = nil; again.close() }
        let restored = try XCTUnwrap(reopened)
        XCTAssertEqual(restored.documentVisibleRect.minY, 420, accuracy: 2, "the page opens where it was left")
        XCTAssertEqual(memory.offsets["lyrics"] ?? 0, 420, accuracy: 2, "the first layout doesn't overwrite it")
    }

    /// Another page has its own offset: a fresh page opens at the top.
    func testAnotherPageOpensAtTheTop() throws {
        let memory = FormScrollMemory()
        memory.offsets["wallpaper"] = 500
        let (window, found) = hostForm(memory: memory, key: "covers")
        defer { window.contentView = nil; window.close() }
        XCTAssertEqual(try XCTUnwrap(found).documentVisibleRect.minY, 0, accuracy: 1)
    }

    // MARK: S8 Google Fonts status

    /// A family that can't be used says so as a failure (the page draws it as
    /// a warning), not as the same plain note as success.
    func testAnUnusableFamilyNameIsAFailure() async throws {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        XCTAssertEqual(settings.googleFontState, .idle)
        // Turned away before any cache, installed font or download is tried.
        settings.applyGoogleFont(family: "Not/A Family")
        for _ in 0..<20 where settings.googleFontState == .idle {
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTAssertEqual(settings.googleFontState, .failed)
        XCTAssertNotNil(settings.googleStatus)
        XCTAssertNil(settings.googleRegular)
    }
}
