import AppKit
import XCTest
@testable import VersoCustom

// The action HUD: when a note goes there, where it sits, and that its panel
// can never take focus or clicks. No window is ever ordered on screen.

final class ActionHUDTests: XCTestCase {
    private func isolated() -> (UserDefaults, () -> Void) {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        return (defaults, { defaults.removePersistentDomain(forName: name) })
    }

    func testTheHUDShowsWhenTheWallpaperIsUnlikelyToBeSeen() {
        typealias Extras = LunalyrExtras
        XCTAssertFalse(Extras.showsInHUD(origin: .window, wallpaperPaused: false, wallpaperCovered: false),
                       "a control in a Lunalyr window, over a visible wallpaper, keeps the wallpaper note")
        XCTAssertTrue(Extras.showsInHUD(origin: .elsewhere, wallpaperPaused: false, wallpaperCovered: false))
        XCTAssertTrue(Extras.showsInHUD(origin: .window, wallpaperPaused: true, wallpaperCovered: false),
                      "a paused wallpaper draws no note at all")
        XCTAssertTrue(Extras.showsInHUD(origin: .window, wallpaperPaused: false, wallpaperCovered: true))
    }

    @MainActor
    func testShortcutsAndLinksPostToTheHUDAndMarkTheNote() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let extras = LunalyrExtras(defaults: defaults)
        extras.announce = { _ in }
        var shown: [String] = []
        extras.presentHUD = { text, _ in shown.append(text) }

        extras.hideLyrics()
        XCTAssertEqual(shown, [], "from the menu-bar window, over a visible wallpaper")
        XCTAssertEqual(extras.note?.inHUD, false)

        extras.performing(from: .elsewhere) { extras.showLyrics() }
        XCTAssertEqual(shown, ["Lyrics shown"])
        XCTAssertEqual(extras.note?.inHUD, true, "the wallpaper can leave out a note the HUD showed")
        XCTAssertEqual(extras.actionOrigin, .window, "the origin lasts only while the action runs")

        extras.post("Stopped sharing", symbol: "person.2.slash", origin: .elsewhere)
        XCTAssertEqual(shown.last, "Stopped sharing")

        extras.toggleWallpaperPause()
        XCTAssertEqual(shown.last, "Wallpaper paused", "pausing from the window still says so where it can be seen")
        extras.toggleWallpaperPause()
        XCTAssertEqual(shown.last, "Wallpaper paused", "resumed, the wallpaper draws its own note again")

        extras.wallpaperCovered = { true }
        extras.hideLyrics()
        XCTAssertEqual(shown.last, "Lyrics hidden", "covered by windows where the pointer is")
    }

    @MainActor
    func testNestedOriginsRestoreTheOuterOne() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let extras = LunalyrExtras(defaults: defaults)
        let inner: LunalyrExtras.NoteOrigin = extras.performing(from: .elsewhere) {
            extras.performing(from: .window) { extras.actionOrigin }
        }
        XCTAssertEqual(inner, .window)
        XCTAssertEqual(extras.actionOrigin, .window)
    }

    @MainActor
    func testWithoutAHUDTheNoteIsNotMarkedAsShownThere() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let extras = LunalyrExtras(defaults: defaults)
        extras.announce = { _ in }
        extras.performing(from: .elsewhere) { extras.hideLyrics() }
        XCTAssertEqual(extras.note?.inHUD, false, "no HUD (tests, before launch): the wallpaper keeps it")
    }

    func testTheHUDSitsCentredJustUnderTheMenuBar() {
        let screen = CGRect(x: 0, y: 0, width: 1512, height: 982)
        let visible = CGRect(x: 0, y: 70, width: 1512, height: 982 - 70 - 33)
        let frame = ActionHUD.frame(panelSize: ActionHUD.panelSize, screenFrame: screen, visibleFrame: visible)
        XCTAssertEqual(frame.midX, screen.midX, accuracy: 1)
        XCTAssertLessThanOrEqual(frame.maxY - ActionHUD.margin, visible.maxY, "never over the menu bar")
        XCTAssertGreaterThan(frame.maxY, visible.maxY - 40, "right under it")
        XCTAssertTrue(screen.contains(frame))

        // A second display to the left and higher up.
        let left = CGRect(x: -2560, y: 200, width: 2560, height: 1440)
        let leftFrame = ActionHUD.frame(panelSize: ActionHUD.panelSize, screenFrame: left,
                                        visibleFrame: CGRect(x: -2560, y: 200, width: 2560, height: 1440 - 25))
        XCTAssertEqual(leftFrame.midX, left.midX, accuracy: 1)
        XCTAssertTrue(left.contains(leftFrame))

        // Menu bar set to hide: the visible frame reaches the top.
        let hidden = ActionHUD.frame(panelSize: ActionHUD.panelSize, screenFrame: screen, visibleFrame: screen)
        XCTAssertLessThanOrEqual(hidden.maxY - ActionHUD.margin, screen.maxY - 24, "clear of the menu bar dropping in")

        // A narrow screen never gets a panel wider than itself.
        let narrow = CGRect(x: 0, y: 0, width: 400, height: 600)
        XCTAssertLessThanOrEqual(ActionHUD.frame(panelSize: ActionHUD.panelSize, screenFrame: narrow,
                                                 visibleFrame: narrow).width, 400)
    }

    func testThePointersScreenIncludesItsTopRow() {
        let main = CGRect(x: 0, y: 0, width: 1512, height: 982)
        let external = CGRect(x: 1512, y: 0, width: 2560, height: 1440)
        XCTAssertEqual(ActionHUD.index(of: CGPoint(x: 700, y: 982), in: [main, external]), 0,
                       "the pointer in the menu bar is on that screen")
        XCTAssertEqual(ActionHUD.index(of: CGPoint(x: 2000, y: 1200), in: [main, external]), 1)
        XCTAssertNil(ActionHUD.index(of: CGPoint(x: -5, y: 5), in: [main, external]))
    }

    @MainActor
    func testThePanelNeverTakesFocusOrClicks() {
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 10, height: 10),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        ActionHUD.configure(panel)
        XCTAssertTrue(panel.ignoresMouseEvents)
        XCTAssertTrue(panel.styleMask.contains(.nonactivatingPanel))
        XCTAssertTrue(panel.becomesKeyOnlyIfNeeded)
        XCTAssertFalse(panel.hidesOnDeactivate, "shows while another app is in front")
        XCTAssertTrue(panel.collectionBehavior.contains(.canJoinAllSpaces))
        XCTAssertTrue(panel.collectionBehavior.contains(.fullScreenAuxiliary))
        XCTAssertTrue(panel.collectionBehavior.contains(.ignoresCycle))
        XCTAssertEqual(panel.sharingType, .none, "left out of screenshots and screen sharing")
        XCTAssertFalse(panel.isVisible)
    }

    @MainActor
    func testAShortcutsActionSaysWhatHappened() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let extras = LunalyrExtras(defaults: defaults)
        extras.announce = { _ in }
        let said = LunalyrCommand.intentDialogText(for: .toggleLyrics, run: { _ in extras.hideLyrics() }, extras: extras)
        XCTAssertEqual(said, "Lyrics hidden")
        let quiet = LunalyrCommand.intentDialogText(for: .lyricCard, run: { _ in }, extras: extras)
        XCTAssertEqual(quiet, LunalyrCommand.lyricCard.doneText, "a command that opens a window posts nothing")
        for command in LunalyrCommand.allCases { XCTAssertFalse(command.doneText.isEmpty) }
    }

    @MainActor
    func testTheCardShortcutSaysItOpensTheCard() {
        XCTAssertEqual(GlobalHotkeys.Action.copyCard.title, "Open lyric card")
    }
}
