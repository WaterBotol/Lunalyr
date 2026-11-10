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

    // MARK: S7 undo

    private func throwawayDefaults() throws -> (UserDefaults, () -> Void) {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        return (defaults, { defaults.removePersistentDomain(forName: name) })
    }

    /// Forget All can be undone and redone, and what comes back is saved.
    func testForgetAllCanBeUndone() throws {
        let (defaults, cleanUp) = try throwawayDefaults()
        defer { cleanUp() }
        let memory = StyleMemory(defaults: defaults)
        let track = NowPlayingMonitor.Track(title: "Hello", artist: "Adele", duration: 295, genre: nil, album: "25")
        memory.remember(.init(composition: "echo", colour: nil), for: track, scope: .song)
        memory.remember(.init(composition: nil, colour: "pastel"), for: track, scope: .artist)
        let undo = UndoManager()
        undo.groupsByEvent = false
        undo.beginUndoGrouping()
        memory.changeUndoably("Forget All Remembered Styles", undoManager: undo) { memory.forgetAll() }
        undo.endUndoGrouping()
        XCTAssertTrue(memory.songs.isEmpty && memory.artists.isEmpty)
        XCTAssertEqual(undo.undoActionName, "Forget All Remembered Styles")

        undo.undo()
        XCTAssertEqual(memory.songs.count, 1)
        XCTAssertEqual(memory.artists.count, 1)
        XCTAssertEqual(StyleMemory(defaults: defaults).snapshot, memory.snapshot, "the restored styles are saved")
        XCTAssertTrue(undo.canRedo)
        undo.redo()
        XCTAssertTrue(memory.songs.isEmpty && memory.artists.isEmpty)
    }

    /// Reset Adjustments puts back every value it changed, Softening included.
    func testResetAdjustmentsCanBeUndone() throws {
        let (defaults, cleanUp) = try throwawayDefaults()
        defer { cleanUp() }
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        settings.effectSpeed = 1.6
        settings.vividness = 0.7
        settings.scrimStrength = 0.4
        settings.motionBlur = 9
        let before = settings.advancedValues
        settings.resetAdvanced()
        XCTAssertTrue(settings.advancedIsDefault)
        settings.restoreAdvanced(before)
        XCTAssertEqual(settings.advancedValues, before)
        XCTAssertEqual(settings.motionBlur, 9)
    }

    /// An undone change registers its redo under the same name.
    func testSettingsChangesRedo() {
        let undo = UndoManager()
        undo.groupsByEvent = false
        var value = 2
        undo.beginUndoGrouping()
        value = 3
        undo.registerSettingsChange("Change", undo: { value = 2 }, redo: { value = 3 })
        undo.endUndoGrouping()
        undo.undo()
        XCTAssertEqual(value, 2)
        XCTAssertEqual(undo.redoActionName, "Change")
        undo.redo()
        XCTAssertEqual(value, 3)
        XCTAssertTrue(undo.canUndo)
    }

    // MARK: S25, S10, S15

    /// An offset beyond ±10 s (tap to sync allows ±30) widens the slider, so
    /// the thumb isn't pinned and a drag can't lose the value.
    func testTheSongOffsetSliderWidensForLargeOffsets() {
        XCTAssertEqual(TimingPage.offsetRange(for: 2), -10...10)
        XCTAssertEqual(TimingPage.offsetRange(for: -10), -10...10)
        XCTAssertEqual(TimingPage.offsetRange(for: 18.5), -30...30)
        XCTAssertEqual(TimingPage.offsetRange(for: -24), -30...30)
        XCTAssertTrue(TimingPage.offsetRange(for: 30).contains(30))
    }

    /// ↑ and ↓ walk the pages the search leaves, holding at either end.
    func testSidebarArrowsWalkTheShownPages() {
        let all = SettingsSidebar.visiblePages(matching: "")
        XCTAssertEqual(all, SettingsPage.Group.allCases.flatMap(\.pages))
        let first = all.first, last = all.last
        XCTAssertEqual(SettingsSidebar.page(from: all[1], by: -1, in: all), first)
        XCTAssertEqual(SettingsSidebar.page(from: all[0], by: -1, in: all), first)
        XCTAssertEqual(SettingsSidebar.page(from: all[0], by: 1, in: all), all[1])
        XCTAssertEqual(SettingsSidebar.page(from: all[all.count - 1], by: 1, in: all), last)
        // A selection the search hides: ↓ starts at the top, ↑ at the bottom.
        let some = Array(all.prefix(3))
        XCTAssertEqual(SettingsSidebar.page(from: all[5], by: 1, in: some), some.first)
        XCTAssertEqual(SettingsSidebar.page(from: all[5], by: -1, in: some), some.last)
        XCTAssertNil(SettingsSidebar.page(from: all[0], by: 1, in: []))
    }

    /// The colour swatch only ticks when its colours move, Reduce Motion is
    /// off and Settings is the active window.
    func testTheColourSwatchRestsWhenItCan() {
        XCTAssertFalse(LyricColourSwatch.swatchPaused(moving: true, reduceMotion: false, active: true))
        XCTAssertTrue(LyricColourSwatch.swatchPaused(moving: false, reduceMotion: false, active: true))
        XCTAssertTrue(LyricColourSwatch.swatchPaused(moving: true, reduceMotion: true, active: true))
        XCTAssertTrue(LyricColourSwatch.swatchPaused(moving: true, reduceMotion: false, active: false))
    }
}
