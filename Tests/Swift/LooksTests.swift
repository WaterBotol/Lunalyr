import Combine
import SwiftUI
import XCTest
@testable import VersoCustom

/// Looks: the built-ins, applying, edit detection and the user's own Looks.
/// Every store reads and writes a throwaway suite.
@MainActor
final class LooksTests: XCTestCase {
    private struct Stores {
        let defaults: UserDefaults
        let settings: VersoCustom.Settings
        let highlights: LyricHighlightOptions
        let looks: LookStore
    }

    private func stores(_ defaults: UserDefaults? = nil) -> Stores {
        let defaults = defaults ?? LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Looks")
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        let highlights = LyricHighlightOptions(defaults: defaults)
        let looks = LookStore(defaults: defaults, settings: settings, highlights: highlights)
        return Stores(defaults: defaults, settings: settings, highlights: highlights, looks: looks)
    }

    /// The suite's own values, without the global domain's.
    private func stored(_ defaults: UserDefaults) -> [String: String] {
        let global = UserDefaults.standard.persistentDomain(forName: UserDefaults.globalDomain) ?? [:]
        var values: [String: String] = [:]
        for (key, value) in defaults.dictionaryRepresentation() where global[key] == nil {
            values[key] = String(describing: value)
        }
        return values
    }

    private func builtIn(_ name: String) -> Look {
        Look.builtIns.first { $0.name == name }!
    }

    // MARK: Built-ins

    func testBuiltInsAreTheDesignersSixFromRealOptions() {
        XCTAssertEqual(Look.builtIns.map(\.name), ["Matched", "Quiet", "Night drive", "Poster", "Star chart", "Collage"])
        XCTAssertEqual(Set(Look.builtIns.map(\.id)).count, 6)
        for look in Look.builtIns {
            XCTAssertTrue(look.isBuiltIn)
            XCTAssertNotNil(look.entrance, look.name)
            XCTAssertNotEqual(look.typeface, .google, "built-ins use built-in typefaces")
            XCTAssertNotEqual(look.entrance, .mix)
            XCTAssertNotEqual(look.palette, .custom)
            // Round-trips through the stores' raw values.
            XCTAssertEqual(Settings.BackgroundMode(rawValue: look.wallpaper.rawValue), look.wallpaper)
            XCTAssertEqual(StoredLook(look).look?.values, look.values, look.name)
        }
        let night = builtIn("Night drive")
        XCTAssertEqual(night.wallpaper, .visualizer)
        XCTAssertEqual(night.layout, .cascade)
        XCTAssertEqual(night.typeface, .sans)
        XCTAssertEqual(night.entrance, .glide)
        XCTAssertEqual(night.palette, .album)
        XCTAssertEqual(night.summary, "Visualisers · Cascade · Avenir Next")
        let matched = builtIn("Matched")
        XCTAssertNil(matched.layout, "Match the music")
        XCTAssertEqual(matched.wallpaper, .auto)
        XCTAssertEqual(matched.entrance, .automatic)
        XCTAssertEqual(matched.summary, "Match the music · SF Pro Display")
        XCTAssertEqual(builtIn("Collage").entrance, .cutoutHighlight)
        XCTAssertEqual(builtIn("Star chart").layout, .constellation)
        XCTAssertEqual(builtIn("Poster").layout, .broadside)
        XCTAssertEqual(builtIn("Quiet").wallpaper, .desktop)
    }

    // MARK: Applying

    func testApplyingSetsExactlyTheCoveredKeys() {
        for look in Look.builtIns {
            let s = stores()
            s.settings.lyricScale = 1.3
            s.settings.wordArrivalByStyle = ["echo": WordArrival.orbit.rawValue]
            let before = stored(s.defaults)
            s.looks.apply(look)
            let after = stored(s.defaults)
            let changed = Set(after.keys).union(before.keys).filter { after[$0] != before[$0] }
            let allowed: Set<String> = ["backgroundMode", "lyricComposition", "fontChoice", "custom.wordArrivalByStyle",
                                        LyricHighlightOptions.Keys.mode, LookStore.Keys.current]
            XCTAssertTrue(changed.isSubset(of: allowed), "\(look.name) changed \(changed.subtracting(allowed))")
            XCTAssertEqual(s.looks.liveValues, look.values, look.name)
            XCTAssertEqual(s.settings.backgroundMode, look.wallpaper)
            XCTAssertEqual(s.settings.lyricComposition, look.layout)
            XCTAssertEqual(s.settings.fontChoice, look.typeface)
            XCTAssertEqual(s.highlights.mode, look.palette)
            XCTAssertEqual(s.settings.arrival(in: look.layout ?? .scattered), look.entrance)
            XCTAssertEqual(s.settings.wordArrivalByStyle["echo"], WordArrival.orbit.rawValue, "other layouts keep theirs")
            XCTAssertEqual(s.settings.lyricScale, 1.3)
            XCTAssertEqual(s.looks.current, look.id)
            XCTAssertFalse(s.looks.isEdited)
            XCTAssertEqual(s.looks.displayName, look.name)
        }
    }

    func testApplyIsOneUndoStep() {
        let s = stores()
        let undo = UndoManager()
        undo.groupsByEvent = false
        let original = s.looks.liveValues
        undo.beginUndoGrouping()
        s.looks.apply(builtIn("Night drive"), undoManager: undo)
        undo.endUndoGrouping()
        XCTAssertEqual(s.settings.backgroundMode, .visualizer)
        undo.undo()
        XCTAssertEqual(s.looks.liveValues, original)
        XCTAssertNil(s.looks.current)
        XCTAssertEqual(s.settings.wordArrivalByStyle, [:])
        undo.redo()
        XCTAssertEqual(s.looks.current, builtIn("Night drive").id)
        XCTAssertFalse(s.looks.isEdited)
    }

    // MARK: Edited

    func testEditingACoveredSettingMarksTheLookEdited() {
        let edits: [(String, (Stores) -> Void)] = [
            ("wallpaper", { $0.settings.backgroundMode = .pattern }),
            ("layout", { $0.settings.lyricComposition = .echo }),
            ("typeface", { $0.settings.selectFontChoice(.serif) }),
            ("entrance", { $0.settings.wordArrivalByStyle["cascade"] = WordArrival.pop.rawValue }),
            ("highlight", { $0.highlights.mode = .complement }),
        ]
        for (name, edit) in edits {
            let s = stores()
            s.looks.apply(builtIn("Night drive"))
            var published = 0
            let watch = s.looks.objectWillChange.sink { published += 1 }
            edit(s)
            XCTAssertTrue(s.looks.isEdited, name)
            XCTAssertGreaterThan(published, 0, "\(name) republishes")
            XCTAssertEqual(s.looks.displayName, "Night drive, edited")
            XCTAssertEqual(s.looks.current, builtIn("Night drive").id, "the Look stays current")
            watch.cancel()
        }
    }

    func testEditingAnUncoveredSettingDoesNot() {
        let s = stores()
        s.looks.apply(builtIn("Night drive"))
        var published = 0
        let watch = s.looks.objectWillChange.sink { published += 1 }
        s.settings.lyricScale = 1.5
        s.settings.vividness = 0.4
        s.settings.effectSpeed = 2
        s.settings.wordArrivalByStyle["echo"] = WordArrival.pop.rawValue
        XCTAssertFalse(s.looks.isEdited)
        XCTAssertEqual(s.looks.displayName, "Night drive")
        s.settings.lyricComposition = .echo
        s.settings.lyricComposition = .cascade
        XCTAssertFalse(s.looks.isEdited, "putting it back clears edited")
        XCTAssertGreaterThan(published, 0)
        watch.cancel()
    }

    // MARK: User Looks

    func testSaveRenameDeleteAndPersist() {
        let s = stores()
        s.settings.backgroundMode = .shader
        s.settings.lyricComposition = .echo
        s.settings.selectFontChoice(.slab)
        s.settings.wordArrivalByStyle["echo"] = WordArrival.mix.rawValue
        s.settings.wordEntranceMix = [WordArrival.pop.rawValue, WordArrival.glide.rawValue]
        s.highlights.mode = .custom
        s.highlights.setCustom(Color(.sRGB, red: 0.2, green: 0.4, blue: 0.6))
        let saved = s.looks.saveCurrent(as: "  Late  ")
        XCTAssertEqual(saved.name, "Late")
        XCTAssertFalse(saved.isBuiltIn)
        XCTAssertEqual(saved.summary, "GPU effects · Echo · Rockwell")
        XCTAssertEqual(saved.entrance, .mix)
        XCTAssertEqual(saved.values.entranceMix, [WordArrival.pop.rawValue, WordArrival.glide.rawValue])
        XCTAssertEqual(saved.values.highlightCustomHex, "#336699")
        XCTAssertEqual(s.looks.current, saved.id)
        XCTAssertFalse(s.looks.isEdited)
        XCTAssertEqual(s.looks.looks.count, 7)
        XCTAssertEqual(s.looks.saveCurrent(as: "late").name, "late 2", "names stay distinct")
        XCTAssertEqual(s.looks.saveCurrent(as: " ").name, "My Look")

        XCTAssertTrue(s.looks.rename(saved.id, to: "Late night"))
        XCTAssertFalse(s.looks.rename(saved.id, to: "   "))
        XCTAssertEqual(s.looks.look(id: saved.id)?.name, "Late night")

        // A fresh store reads the same Looks and current one back.
        let reloaded = stores(s.defaults)
        XCTAssertEqual(reloaded.looks.userLooks, s.looks.userLooks)
        XCTAssertEqual(reloaded.looks.current, s.looks.current)
        XCTAssertNotNil(s.defaults.data(forKey: LookStore.Keys.saved))

        // Applying a saved Look puts every covered value back.
        s.looks.apply(builtIn("Poster"))
        s.looks.apply(s.looks.look(id: saved.id)!)
        XCTAssertEqual(s.looks.liveValues, saved.values)
        XCTAssertEqual(s.highlights.customHex, "#336699")
        XCTAssertEqual(s.settings.wordEntranceMix, [WordArrival.pop.rawValue, WordArrival.glide.rawValue])

        let settingsBefore = s.looks.liveValues
        XCTAssertTrue(s.looks.delete(saved.id))
        XCTAssertNil(s.looks.current, "deleting the current Look leaves no Look")
        XCTAssertNil(s.defaults.string(forKey: LookStore.Keys.current))
        XCTAssertEqual(s.looks.liveValues, settingsBefore, "deleting changes no setting")
        XCTAssertEqual(s.looks.displayName, "Custom")
        XCTAssertNil(stores(s.defaults).looks.look(id: saved.id))
    }

    func testBuiltInsCannotBeDeletedOrRenamed() {
        let s = stores()
        for look in Look.builtIns {
            XCTAssertFalse(s.looks.delete(look.id))
            XCTAssertFalse(s.looks.rename(look.id, to: "Other"))
        }
        XCTAssertEqual(s.looks.looks, Look.builtIns)
    }

    func testUnknownStoredValuesAreDropped() throws {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Looks")
        var bad = StoredLook(builtIn("Poster"))
        bad.id = "gone"
        bad.layout = "noSuchLayout"
        var good = StoredLook(builtIn("Quiet"))
        good.id = "kept"
        defaults.set(try JSONEncoder().encode([bad, good]), forKey: LookStore.Keys.saved)
        defaults.set("gone", forKey: LookStore.Keys.current)
        let s = stores(defaults)
        XCTAssertEqual(s.looks.userLooks.map(\.id), ["kept"])
        XCTAssertNil(s.looks.current)
    }

    // MARK: No Look chosen

    func testNoLookChosenLeavesSettingsUntouched() {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Looks")
        defaults.set("pattern", forKey: "backgroundMode")
        let before = stored(defaults)
        let s = stores(defaults)
        XCTAssertNil(s.looks.current)
        XCTAssertFalse(s.looks.isEdited)
        XCTAssertEqual(s.looks.displayName, "Custom")
        s.settings.lyricComposition = .mosaic
        XCTAssertFalse(s.looks.isEdited)
        XCTAssertNil(s.defaults.object(forKey: LookStore.Keys.current))
        XCTAssertNil(s.defaults.object(forKey: LookStore.Keys.saved))
        var after = stored(defaults)
        after["lyricComposition"] = nil
        XCTAssertEqual(after, before, "the store itself writes nothing")
        XCTAssertEqual(s.settings.backgroundMode, .pattern)
    }

    func testDisplayName() {
        XCTAssertEqual(LookStore.displayName(for: nil, isEdited: false), "Custom")
        XCTAssertEqual(LookStore.displayName(for: builtIn("Matched"), isEdited: false), "Matched")
        XCTAssertEqual(LookStore.displayName(for: builtIn("Matched"), isEdited: true), "Matched, edited")
    }
}
