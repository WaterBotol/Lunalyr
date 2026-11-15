import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Typeface › Installed font: which families the list offers, what
/// "Find new fonts" counts, that the choice is kept, and that a family
/// uninstalled since falls back to the default typeface.
@MainActor
final class InstalledFontsTests: XCTestCase {
    private func isolatedSettings(_ name: String = "LunalyrTests.\(UUID().uuidString)") -> (VersoCustom.Settings, UserDefaults) {
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
        let defaults = UserDefaults(suiteName: name)!
        return (VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter()), defaults)
    }

    // MARK: The list

    func testHiddenUnusableAndRepeatedFamiliesAreLeftOut() {
        let raw = ["Zeta Sans", ".SF NS Rounded", "alpha Serif", "Wingdings", "Zeta Sans", "", "Beta Mono"]
        let usable = InstalledFontLibrary.usableFamilies(raw) { $0 != "Wingdings" }
        XCTAssertEqual(usable, ["alpha Serif", "Beta Mono", "Zeta Sans"])
    }

    func testSymbolAndDingbatFontsCantSetLyrics() {
        for family in ["Wingdings", "Webdings", "Zapf Dingbats", "Bodoni Ornaments", "Apple Color Emoji", "Symbol"]
        where NSFontManager.shared.availableFontFamilies.contains(family) {
            XCTAssertFalse(InstalledFontLibrary.drawsLatin(family), family)
        }
        XCTAssertTrue(InstalledFontLibrary.drawsLatin("Helvetica Neue"))
        XCTAssertFalse(InstalledFontLibrary.drawsLatin("No Such Family 4821"))
        let real = InstalledFontLibrary.usableFamilies(InstalledFontLibrary.systemFamilies(),
                                                       isUsable: InstalledFontLibrary.isListable)
        XCTAssertTrue(real.contains("Helvetica Neue"))
        XCTAssertFalse(real.contains { $0.hasPrefix(".") })
        XCTAssertFalse(real.contains("Zapf Dingbats"))
    }

    func testSearchMatchesPartOfTheNameIgnoringCaseAndAccents() {
        let families = ["Avenir Next", "Helvetica Neue", "Didot", "Café Sans"]
        XCTAssertEqual(InstalledFontLibrary.filter(families, matching: "neue"), ["Helvetica Neue"])
        XCTAssertEqual(InstalledFontLibrary.filter(families, matching: "cafe"), ["Café Sans"])
        XCTAssertEqual(InstalledFontLibrary.filter(families, matching: "  "), families)
    }

    func testFindNewFontsCountsOnlyFamiliesAddedSinceTheLastReading() async {
        var installed = ["Avenir Next", "Didot"]
        let library = InstalledFontLibrary(source: { installed }, isUsable: { _ in true },
                                           notifications: NotificationCenter())
        let first = await library.refresh()
        XCTAssertEqual(first, 0, "the first reading finds nothing new")
        XCTAssertEqual(library.families, ["Avenir Next", "Didot"])

        installed += ["Mona Sans", "Hubot Sans"]
        installed.removeAll { $0 == "Didot" }
        let added = await library.refresh()
        XCTAssertEqual(added, 2)
        XCTAssertEqual(library.families, ["Avenir Next", "Hubot Sans", "Mona Sans"])
        let again = await library.refresh()
        XCTAssertEqual(again, 0)

        // A font the automatic refresh picked up still counts when the
        // button is pressed afterwards.
        let announced = await library.findNewFonts()
        XCTAssertEqual(announced, 2)
        installed.append("Inter")
        await library.refresh()
        let pressed = await library.findNewFonts()
        XCTAssertEqual(pressed, 1)
        let pressedAgain = await library.findNewFonts()
        XCTAssertEqual(pressedAgain, 0)

        XCTAssertEqual(InstalledFontLibrary.newFamilies(old: ["A", "B"], new: ["B", "C", "D"]), ["C", "D"])
        XCTAssertEqual(InstalledFontLibrary.foundMessage(0), "No new fonts found.")
        XCTAssertEqual(InstalledFontLibrary.foundMessage(1), "Found 1 new family.")
        XCTAssertEqual(InstalledFontLibrary.foundMessage(3), "Found 3 new families.")
    }

    // MARK: The choice

    func testTheChosenInstalledFamilyIsKeptAndDrawn() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let (settings, defaults) = isolatedSettings(name)
        settings.googleFontFamily = "Lora"
        settings.applyInstalledFont(family: "Helvetica Neue")
        XCTAssertEqual(settings.fontChoice, .installed)
        XCTAssertEqual(settings.chosenFontFamily, "Helvetica Neue")
        XCTAssertEqual(defaults.string(forKey: VersoCustom.Settings.installedFontFamilyKey), "Helvetica Neue")
        XCTAssertEqual(defaults.string(forKey: "fontChoice"), "installed")
        XCTAssertEqual(settings.googleFontFamily, "Lora", "Google Fonts keeps its own family")
        XCTAssertFalse(settings.installedFontMissing)
        XCTAssertEqual(settings.drawnFontChoice, .installed)
        let regular = try? XCTUnwrap(settings.activeCustomFaces.regular)
        XCTAssertNotNil(regular.flatMap { NSFont(name: $0, size: 12) })
        XCTAssertEqual(settings.lyricFont(size: 20, hero: false, mood: .neutral), .custom(regular ?? "", fixedSize: 20))

        // A second launch reads it back and draws it straight away.
        let relaunched = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        XCTAssertEqual(relaunched.fontChoice, .installed)
        XCTAssertEqual(relaunched.installedFontFamily, "Helvetica Neue")
        XCTAssertEqual(relaunched.activeCustomFaces.regular, regular)

        // Choosing a built-in face keeps the family for next time.
        settings.selectFontChoice(.serif)
        XCTAssertEqual(settings.installedFontFamily, "Helvetica Neue")
        XCTAssertNil(settings.chosenFontFamily)
        settings.selectFontChoice(.installed)
        XCTAssertEqual(settings.activeCustomFaces.regular, regular)
    }

    func testAnUninstalledFamilyFallsBackToTheDefaultTypeface() {
        let (settings, defaults) = isolatedSettings()
        defaults.set("installed", forKey: "fontChoice")
        defaults.set("No Such Family 4821", forKey: VersoCustom.Settings.installedFontFamilyKey)
        settings.reloadLyricStyle()
        XCTAssertEqual(settings.fontChoice, .installed)
        XCTAssertTrue(settings.installedFontMissing)
        XCTAssertNil(settings.activeCustomFaces.regular)
        XCTAssertEqual(settings.drawnFontChoice, .manrope, "drawn in the default, Manrope")
        XCTAssertEqual(settings.installedFontFamily, "No Such Family 4821", "kept, for when it comes back")
        XCTAssertEqual(VersoCustom.Settings.renderedFace(choice: settings.drawnFontChoice, moodFace: .serif,
                                             manropeLoaded: true, customVoice: nil), .sans)

        // Nothing picked yet is not "missing".
        settings.installedFontFamily = ""
        settings.resolveInstalledFont()
        XCTAssertFalse(settings.installedFontMissing)
        XCTAssertEqual(settings.drawnFontChoice, .manrope)

        // Another typeface clears the warning.
        settings.applyInstalledFont(family: "No Such Family 4821")
        XCTAssertTrue(settings.installedFontMissing)
        settings.selectFontChoice(.slab)
        XCTAssertFalse(settings.installedFontMissing)
    }

    func testRememberedSongFontsAndLooksCarryTheInstalledFamily() {
        let (settings, _) = isolatedSettings()
        settings.applySongFont(.init(choice: .installed, family: "Helvetica Neue"))
        XCTAssertEqual(settings.effectiveFontChoice, .installed)
        XCTAssertNotNil(settings.activeCustomFaces.regular)
        settings.applySongFont(.init(choice: .installed, family: "No Such Family 4821"))
        XCTAssertNil(settings.activeCustomFaces.regular)
        XCTAssertEqual(settings.drawnFontChoice, .manrope)
        settings.applySongFont(nil)

        let style = StyleMemory.Style(composition: nil, colour: nil, font: "installed", fontFamily: "Helvetica Neue")
        XCTAssertEqual(style.songFont, .init(choice: .installed, family: "Helvetica Neue"))
        XCTAssertEqual(style.fontTitle, "Helvetica Neue")

        let values = LookValues(wallpaper: .auto, layout: nil, typeface: .installed, fontFamily: "Helvetica Neue",
                                entrance: nil, highlight: LyricHighlightMode.allCases[0])
        XCTAssertEqual(values.fontFamily, "Helvetica Neue")
        XCTAssertEqual(values.typefaceName, "Helvetica Neue")
        XCTAssertNil(LookValues(wallpaper: .auto, layout: nil, typeface: .serif, fontFamily: "Helvetica Neue",
                                entrance: nil, highlight: LyricHighlightMode.allCases[0]).fontFamily)
    }

    func testTheTitlesNameEachSource() {
        XCTAssertEqual(VersoCustom.Settings.FontChoice.google.title, "Google Fonts")
        XCTAssertEqual(VersoCustom.Settings.FontChoice.installed.title, "Installed font")
        XCTAssertTrue(VersoCustom.Settings.FontChoice.installed.takesFamily)
        XCTAssertFalse(VersoCustom.Settings.FontChoice.manrope.takesFamily)
    }

    // MARK: The picker

    func testThePickerFitsTheInspector() {
        let scene = UISnapshotScene(player: .playing)
        defer { scene.tearDown() }
        scene.settings.applyInstalledFont(family: "Helvetica Neue")
        let available = LunalyrDesign.Studio.Layout.inspectorWidth - 40
        for view in [AnyView(InstalledFontPicker(open: true)), AnyView(LyricStylePage(floating: FloatingLyricsOptions(defaults: scene.defaults)))] {
            let host = NSHostingController(rootView: view
                .environment(\.moreOptionsOpen, true)
                .environment(\.inspectorMeasuring, true)
                .lunalyrModels(scene.models))
            let width = host.sizeThatFits(in: CGSize(width: available, height: 20_000)).width
            XCTAssertLessThanOrEqual(width, available + 0.5)
        }
    }

    /// Draws the Typeface section with the list open, for a look by eye:
    /// written to the temporary directory, its path printed. Only on request.
    func testSnapshotTheTypefaceSectionWithTheListOpen() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["LUNALYR_FONT_SNAPSHOT"] != nil,
                          "Set TEST_RUNNER_LUNALYR_FONT_SNAPSHOT=1 to draw it")
        let scene = UISnapshotScene(player: .playing)
        defer { scene.tearDown() }
        let library = InstalledFontLibrary(notifications: NotificationCenter())
        await library.refresh()
        XCTAssertGreaterThan(library.families.count, 20)
        scene.settings.applyInstalledFont(family: "Helvetica Neue")
        let width = LunalyrDesign.Studio.Layout.inspectorWidth - 40
        for (look, query) in [(UISnapshotRenderer.Look.light, ""), (.dark, "helv")] {
            let view = VStack(alignment: .leading) {
                InspectorSection("Typeface", value: scene.settings.fontChoice.title) {
                    TypefaceTiles()
                    InstalledFontPicker(library: library, open: true, query: query)
                }
            }
            .padding(20)
            .lunalyrModels(scene.models)
            let image = try UISnapshotRenderer.render(view, chrome: UISnapshotRenderer.Chrome.none, size: .fitting(width: width + 40, maxHeight: 900),
                                                      look: look)
            let url = FileManager.default.temporaryDirectory.appendingPathComponent("installed-fonts-\(look.rawValue).png")
            try UISnapshotRenderer.writePNG(image, to: url)
            print("SNAPSHOT \(url.path)")
        }
    }

    func testFontsForOtherScriptsAreListedButSymbolFontsAreNot() {
        XCTAssertTrue(InstalledFontLibrary.drawsLatin("Geeza Pro"), "Arabic lyrics")
        XCTAssertTrue(InstalledFontLibrary.drawsLatin("Helvetica Neue"))
        XCTAssertFalse(InstalledFontLibrary.drawsLatin("Zapf Dingbats"))
    }
}
