import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// The redesigned menu-bar panel (SPEC.md section 7): the Look switcher, the
/// Wallpaper submenu, Open Lyric Card… and the panel's sizes. Every store
/// reads and writes a throwaway suite.
@MainActor
final class MenuBarPanelTests: XCTestCase {
    private struct Stores {
        let settings: VersoCustom.Settings
        let looks: LookStore
    }

    private func stores() -> Stores {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.MenuBarPanel")
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        let highlights = LyricHighlightOptions(defaults: defaults)
        return Stores(settings: settings, looks: LookStore(defaults: defaults, settings: settings, highlights: highlights))
    }

    private func builtIn(_ name: String) -> Look {
        Look.builtIns.first { $0.name == name }!
    }

    // MARK: Look switcher

    func testATileAppliesItsLookAndUndoesAsOneStep() {
        let s = stores()
        let before = s.settings.backgroundMode
        let undo = UndoManager()
        undo.groupsByEvent = false
        undo.beginUndoGrouping()
        MenuBarLookSwitcher.apply(builtIn("Night drive"), in: s.looks, undoManager: undo)
        undo.endUndoGrouping()
        XCTAssertEqual(s.looks.current, "builtin.nightDrive")
        XCTAssertEqual(s.settings.backgroundMode, .visualizer)
        XCTAssertEqual(s.settings.lyricComposition, .cascade)
        XCTAssertFalse(s.looks.isEdited)
        undo.undo()
        XCTAssertEqual(s.settings.backgroundMode, before)
        XCTAssertNil(s.looks.current)
    }

    func testTheLabelNamesTheLookOnlyWhenTheRingDoesNot() {
        let s = stores()
        XCTAssertEqual(MenuBarLookSwitcher.headerValue(current: nil, isEdited: false), "Custom")
        let matched = builtIn("Matched")
        MenuBarLookSwitcher.apply(matched, in: s.looks, undoManager: nil)
        XCTAssertNil(MenuBarLookSwitcher.headerValue(current: s.looks.currentLook, isEdited: s.looks.isEdited),
                     "the ringed tile already says Matched")
        XCTAssertEqual(MenuBarLookSwitcher.spokenName(matched, chosen: true, isEdited: s.looks.isEdited), "Matched")
        // Any covered setting changed elsewhere: the Look is edited.
        s.settings.backgroundMode = .pattern
        XCTAssertTrue(s.looks.isEdited)
        XCTAssertEqual(MenuBarLookSwitcher.headerValue(current: s.looks.currentLook, isEdited: s.looks.isEdited),
                       "Matched, edited")
        XCTAssertEqual(MenuBarLookSwitcher.spokenName(matched, chosen: true, isEdited: true), "Matched, edited")
        XCTAssertEqual(MenuBarLookSwitcher.spokenName(builtIn("Quiet"), chosen: false, isEdited: true), "Quiet",
                       "only the chosen tile says edited")
        // Changing it back clears it.
        s.settings.backgroundMode = matched.wallpaper
        XCTAssertNil(MenuBarLookSwitcher.headerValue(current: s.looks.currentLook, isEdited: s.looks.isEdited))
        // A saved Look: the Saved Looks row names it, and the label once edited.
        let mine = s.looks.saveCurrent(as: "Late night")
        XCTAssertNil(MenuBarLookSwitcher.headerValue(current: mine, isEdited: false))
        XCTAssertEqual(MenuBarLookSwitcher.headerValue(current: mine, isEdited: true), "Late night, edited")
    }

    func testEveryBuiltInLookHasItsPicture() {
        for look in Look.builtIns {
            XCTAssertNotNil(MenuBarLookArt.art(for: look.id), look.name)
        }
        XCTAssertEqual(Set(Look.builtIns.compactMap { MenuBarLookArt.art(for: $0.id) }).count, MenuBarLookArt.Art.allCases.count)
        XCTAssertNil(MenuBarLookArt.art(for: "a-saved-look"))
    }

    // MARK: Wallpaper submenu

    func testTheWallpaperSubmenuListsAllEightAndChoosesLikeTheGrid() {
        let s = stores()
        s.settings.backgroundMode = .auto
        var importAsked = 0
        let items = MenuBarWallpaperControls.wallpaperItems(settings: s.settings) { importAsked += 1 }
        XCTAssertEqual(items.count, 8)
        XCTAssertEqual(items.filter(\.checked).map(\.title), [VersoCustom.Settings.BackgroundMode.auto.title])
        items.first { $0.id == "\(VersoCustom.Settings.BackgroundMode.pattern)" }?.action()
        XCTAssertEqual(s.settings.backgroundMode, .pattern)
        // Image or video with nothing imported: Settings' importer instead.
        s.settings.customWallpaperPath = ""
        items.first { $0.id == "\(VersoCustom.Settings.BackgroundMode.custom)" }?.action()
        XCTAssertEqual(importAsked, 1)
        XCTAssertEqual(s.settings.backgroundMode, .pattern, "nothing to show yet: the type stays")
    }

    // MARK: Open Lyric Card…

    func testOpenLyricCardIsAnActionNamedAsEverywhereAndNeedsASong() {
        XCTAssertEqual(MenuBarLyricCardRow.title, "Open Lyric Card…")
        XCTAssertEqual(MenuBarLyricCardRow.title, LunalyrExtras.Titles.copyCard)
        XCTAssertTrue(MenuBarLyricCardRow.isEnabled(hasTrack: true))
        XCTAssertFalse(MenuBarLyricCardRow.isEnabled(hasTrack: false), "disabled with nothing playing")
    }

    func testOpenLyricCardRunsItsActionOnlyWithASong() {
        var opened = 0
        XCTAssertTrue(MenuBarLyricCardRow.press(hasTrack: true) { opened += 1 })
        XCTAssertEqual(opened, 1, "a click opens the lyric card window")
        XCTAssertFalse(MenuBarLyricCardRow.press(hasTrack: false) { opened += 1 })
        XCTAssertEqual(opened, 1, "nothing playing: the row is disabled and does nothing")
    }

    // MARK: Sizes and words

    func testThePanelScrollsOnlyPastTheSmallestScreen() {
        XCTAssertEqual(MenuBarView.maximumPanelHeight(screens: [NSRect(x: 0, y: 0, width: 1470, height: 853),
                                                                NSRect(x: 0, y: 0, width: 2560, height: 1400)]), 829)
        XCTAssertEqual(MenuBarView.maximumPanelHeight(screens: []), MenuBarView.fallbackPanelHeight)
    }

    func testIdleSaysWhichPlayersAreFollowed() {
        XCTAssertEqual(MenuBarNowPlaying.followingLine([.appleMusic, .spotify]), "Following Music and Spotify")
        XCTAssertEqual(MenuBarNowPlaying.followingLine([.appleMusic]), "Following Music")
        XCTAssertEqual(MenuBarNowPlaying.followingLine([]), "Following no players")
    }

    func testProgressTimes() {
        XCTAssertEqual(MenuBarProgress.time(83), "1:23")
        XCTAssertEqual(MenuBarProgress.time(213.9), "3:33")
        XCTAssertEqual(MenuBarProgress.time(3723), "1:02:03")
        XCTAssertEqual(MenuBarProgress.fraction(50, of: 100), 0.5)
        XCTAssertEqual(MenuBarProgress.fraction(150, of: 100), 1)
        XCTAssertEqual(MenuBarProgress.fraction(10, of: 0), 0)
    }
}
