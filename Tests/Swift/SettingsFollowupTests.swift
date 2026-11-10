import XCTest
@testable import VersoCustom

/// Settings follow-ups to the polish packages: the Share page's status rows
/// (F2), the card shortcut's caption, and the entrances a layout leaves out.
@MainActor
final class SettingsFollowupTests: XCTestCase {
    // MARK: F2 status rows

    func testDiscordRowSaysWhetherDiscordIsThere() {
        XCTAssertEqual(ShareStatusRow.discord(.waitingForDiscord).text, "Waiting for Discord")
        // Switched on but not started yet reads the same, not "Off".
        XCTAssertEqual(ShareStatusRow.discord(.off), ShareStatusRow.discord(.waitingForDiscord))
        XCTAssertEqual(ShareStatusRow.discord(.connected(name: "Sam")).text, "Connected as Sam")
        XCTAssertEqual(ShareStatusRow.discord(.connected(name: "Sam")).status, .success)
        XCTAssertEqual(ShareStatusRow.discord(.connected(name: nil)).text, "Connected")
        XCTAssertEqual(ShareStatusRow.discord(.connected(name: "  ")).text, "Connected", "no empty name")
        XCTAssertNotEqual(ShareStatusRow.discord(.waitingForDiscord).status, .success)
    }

    func testSharingRowShowsTheRelayState() {
        let waiting = ShareStatusRow.sharing(.waitingForMusic)
        XCTAssertEqual(waiting.text, MenuBarListenAlong.sharingTitle(.waitingForMusic))
        XCTAssertEqual(ShareStatusRow.sharing(nil), waiting, "on, with nothing started yet")

        let retrying = ShareStatusRow.sharing(.retrying)
        XCTAssertEqual(retrying.status, .warning, "a relay that can't be reached is not shown as working")
        XCTAssertEqual(retrying.text, MenuBarListenAlong.sharingTitle(.retrying))

        let live = ShareStatusRow.sharing(.live(room: "abc123defg", listeners: 2))
        XCTAssertEqual(live.status, .success)
        XCTAssertEqual(live.text, MenuBarListenAlong.sharingTitle(listeners: 2))
        XCTAssertEqual(live.symbol, MenuBarListenAlong.sharingSymbol(.live(room: "abc123defg", listeners: 2)))
    }

    // MARK: Card shortcut caption

    func testShortcutCaptionNamesTheCardShortcutAsItsRowDoes() {
        XCTAssertTrue(ExtrasInspector.shortcutsCaption.contains(GlobalHotkeys.Action.copyCard.title))
        XCTAssertFalse(ExtrasInspector.shortcutsCaption.localizedCaseInsensitiveContains("copy a lyric card"))
        XCTAssertTrue(ExtrasInspector.shortcutsCaption.contains(SettingsPage.share.title))
        XCTAssertTrue(SettingsPage.extras.matches("lyric card"), "searching for the card finds its shortcut too")
    }

    // MARK: Entrances a layout leaves out

    func testStageLayoutsThatIgnoreEveryChosenMotion() {
        for layout in [LyricComposition.hinge, .broadside, .portal, .mosaic] {
            for arrival in WordArrival.allCases {
                let shown = arrival == .automatic || arrival == .mix || arrival == .cutoutHighlight
                XCTAssertEqual(EntranceSupport.appliesToSungWords(arrival, in: layout), shown, "\(layout) \(arrival)")
            }
        }
    }

    /// Torque and Fable take a chosen motion exactly when the renderer gives
    /// it a pose of its own, plus Cutout · Highlight's tiles.
    func testTorqueAndFableMatchTheRenderer() {
        for layout in [LyricComposition.torque, .fable] {
            for arrival in WordArrival.allCases where arrival != .automatic && arrival != .mix {
                let posed = StageLyricView.chosenEntrance(arrival, progress: 0.3, size: 40, side: 1, reduceMotion: false) != nil
                XCTAssertEqual(EntranceSupport.appliesToSungWords(arrival, in: layout),
                               posed || arrival.highlightsSungWords, "\(layout) \(arrival)")
            }
            XCTAssertFalse(EntranceSupport.appliesToSungWords(.fade, in: layout))
            XCTAssertFalse(EntranceSupport.appliesToSungWords(.satinRibbon, in: layout))
            XCTAssertFalse(EntranceSupport.appliesToSungWords(.splitInk, in: layout))
            XCTAssertTrue(EntranceSupport.appliesToSungWords(.pop, in: layout))
        }
    }

    func testOtherLayoutsShowEveryEntrance() {
        for layout in LyricComposition.allCases where !layout.fillsStage {
            for arrival in WordArrival.allCases {
                XCTAssertTrue(EntranceSupport.appliesToSungWords(arrival, in: layout), "\(layout) \(arrival)")
                XCTAssertEqual(EntranceSupport.menuTitle(arrival, in: layout), arrival.title)
            }
        }
    }

    func testTheMenuMarksAndExplainsALeftOutEntrance() {
        XCTAssertEqual(EntranceSupport.menuTitle(.satinRibbon, in: .hinge), "Satin Ribbon (whole lines only)")
        XCTAssertEqual(EntranceSupport.menuTitle(.cutoutHighlight, in: .hinge), "Cutout · Highlight")
        // Match the music marks nothing.
        XCTAssertEqual(EntranceSupport.menuTitle(.satinRibbon, in: nil), "Satin Ribbon")
        XCTAssertNil(EntranceSupport.note(selected: .satinRibbon, layout: nil, mix: []))

        XCTAssertEqual(EntranceSupport.note(selected: .splitInk, layout: .torque, mix: []),
                       "Torque moves each sung word its own way, so Split Ink plays only on lyrics timed by the line.")
        XCTAssertNil(EntranceSupport.note(selected: .pop, layout: .torque, mix: [.fade]))
        XCTAssertEqual(EntranceSupport.note(selected: .mix, layout: .fable, mix: [.fade, .pop, .splitInk]),
                       "Fable moves each sung word its own way, so Soft focus and Split Ink play only on lyrics timed by the line.")
        XCTAssertNil(EntranceSupport.note(selected: .mix, layout: .hinge, mix: [.cutoutHighlight]))
        XCTAssertEqual(EntranceSupport.list(["A", "B", "C"]), "A, B and C")
    }

    /// An entrance the layout leaves out no longer locks Reveal sung words,
    /// with a reason that would say it unveils each word.
    func testALeftOutEntranceDoesNotLockReveal() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: name) else { return XCTFail("no defaults") }
        defer { defaults.removePersistentDomain(forName: name) }
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())

        settings.lyricComposition = .hinge
        settings.wordArrivalByStyle[LyricComposition.hinge.rawValue] = WordArrival.satinRibbon.rawValue
        XCTAssertNil(settings.wordRevealLock(in: .hinge))
        settings.wordArrivalByStyle[LyricComposition.hinge.rawValue] = WordArrival.mix.rawValue
        settings.wordEntranceMix = [WordArrival.cutoutFold.rawValue, WordArrival.splitInk.rawValue]
        XCTAssertNil(settings.wordRevealLock(in: .hinge))

        settings.lyricComposition = .torque
        settings.wordArrivalByStyle[LyricComposition.torque.rawValue] = WordArrival.splitInk.rawValue
        XCTAssertNil(settings.wordRevealLock(in: .torque))
        settings.wordArrivalByStyle[LyricComposition.torque.rawValue] = WordArrival.cutoutFold.rawValue
        XCTAssertEqual(settings.wordRevealLock(in: .torque), .entrance(.cutoutFold), "Torque does fold its words")
        settings.wordArrivalByStyle[LyricComposition.torque.rawValue] = WordArrival.mix.rawValue
        XCTAssertEqual(settings.wordRevealLock(in: .torque), .mix)

        settings.lyricComposition = .scattered
        settings.wordArrivalByStyle[LyricComposition.scattered.rawValue] = WordArrival.satinRibbon.rawValue
        XCTAssertEqual(settings.wordRevealLock(in: .scattered), .entrance(.satinRibbon))

        // Match the music's entrance is for every layout it picks: it locks
        // in a stage layout too, as before.
        settings.lyricComposition = nil
        settings.wordArrivalByStyle[settings.wordControlsKey] = WordArrival.satinRibbon.rawValue
        XCTAssertEqual(settings.wordRevealLock(in: .hinge), .entrance(.satinRibbon))
    }
}
