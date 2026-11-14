import SwiftUI
import XCTest
@testable import VersoCustom

/// The picture pages: the Look row and its objects, the lyric line's
/// handles, the choice grids and the Looks' naming rules.
@MainActor
final class LookCanvasTests: XCTestCase {
    // MARK: Sidebar

    func testTheThreeLookPagesAreOneLookRow() {
        XCTAssertEqual(SettingsSidebarRow(.wallpaper), .look)
        XCTAssertEqual(SettingsSidebarRow(.typography), .look)
        XCTAssertEqual(SettingsSidebarRow(.covers), .look)
        XCTAssertEqual(SettingsSidebarRow(.lyrics), .page(.lyrics))
        XCTAssertEqual(SettingsSidebarRow(.about), .page(.about))
        // Every page has a row, and the rows read as the spec lists them.
        let titles = SettingsSidebarRow.all.map(\.title)
        XCTAssertEqual(titles, ["Look", "Timing & sources", "Romanisation", "Music players", "General",
                                "Menu bar & shortcuts", "Discord & lyric card", "Performance", "About"])
        XCTAssertEqual(Set(SettingsSidebarRow.all.flatMap(\.pages)), Set(SettingsPage.allCases))
        XCTAssertEqual(SettingsSidebarRow.groups.map(\.title), ["Words", "App"])
    }

    func testTheLookRowOpensTheLastObjectOrWhatTheSearchFound() {
        XCTAssertEqual(SettingsSidebarRow.look.destination(lastLook: .typography), .typography)
        XCTAssertEqual(SettingsSidebarRow.look.destination(lastLook: .covers), .covers)
        // A page without a picture is never "the last Look".
        XCTAssertEqual(SettingsSidebarRow.look.destination(lastLook: .general), .wallpaper)
        // A search lands on the object it found.
        XCTAssertEqual(SettingsSidebarRow.look.destination(lastLook: .wallpaper, query: "typeface"), .typography)
        XCTAssertEqual(SettingsSidebarRow.look.destination(lastLook: .wallpaper, query: "softening"), .covers)
        XCTAssertEqual(SettingsSidebarRow.look.destination(lastLook: .typography, query: "darkening"), .wallpaper)
        // …unless the object shown already matches.
        XCTAssertEqual(SettingsSidebarRow.look.destination(lastLook: .typography, query: "colour"), .typography)
        XCTAssertEqual(SettingsSidebarRow.page(.general).destination(lastLook: .covers, query: "x"), .general)
    }

    func testSearchKeepsTheLookRowWhileAnyLookPageMatches() {
        XCTAssertTrue(SettingsSidebar.visibleRows(matching: "darkening").contains(.look))
        XCTAssertTrue(SettingsSidebar.visibleRows(matching: "animated cover").contains(.look))
        XCTAssertFalse(SettingsSidebar.visibleRows(matching: "discord").contains(.look))
        XCTAssertEqual(SettingsSidebar.visibleRows(matching: ""), SettingsSidebarRow.all)
        let rows = SettingsSidebarRow.all
        XCTAssertEqual(SettingsSidebar.page(from: SettingsSidebarRow.look, by: 1, in: rows), .page(.lyrics))
        XCTAssertEqual(SettingsSidebar.page(from: SettingsSidebarRow.look, by: -1, in: rows), .look)
    }

    func testObjectsMapToTheirStoredPages() {
        for object in LookObject.allCases {
            XCTAssertEqual(LookObject(page: object.page), object)
        }
        XCTAssertNil(LookObject(page: .performance))
        XCTAssertEqual(LookObject.allCases.map(\.title), ["Wallpaper", "Lyric style", "Animated covers"])
        // Destinations from other surfaces land on the right object.
        XCTAssertEqual(LookObject(page: SettingsPage(.wallpaper)), .wallpaper)
        XCTAssertEqual(LookObject(page: SettingsPage(.musicResponse)), .wallpaper)
        XCTAssertEqual(LookObject(page: SettingsPage(.lyricStyle)), .lyrics)
        XCTAssertEqual(LookObject(page: SettingsPage(.animatedCovers)), .covers)
    }

    // MARK: Lyric size handles

    func testDraggingACornerOutwardGrowsLyricSizeWithinItsRange() {
        let corner = CGPoint(x: 100, y: 50)
        // Half as far again from the centre: half as large again.
        XCTAssertEqual(LyricSizeDrag.scale(startScale: 1, start: corner, translation: CGSize(width: 50, height: 25)),
                       1.5, accuracy: 0.001)
        XCTAssertEqual(LyricSizeDrag.scale(startScale: 1, start: corner, translation: CGSize(width: -10, height: -5)),
                       0.9, accuracy: 0.001)
        // Any corner: the top-left one grows when dragged up and left.
        XCTAssertEqual(LyricSizeDrag.scale(startScale: 1, start: CGPoint(x: -100, y: -50),
                                           translation: CGSize(width: -20, height: -10)), 1.2, accuracy: 0.001)
        // Held to the slider's range, and past the centre doesn't flip it.
        XCTAssertEqual(LyricSizeDrag.scale(startScale: 1, start: corner, translation: CGSize(width: 400, height: 200)), 1.5)
        XCTAssertEqual(LyricSizeDrag.scale(startScale: 1, start: corner, translation: CGSize(width: -300, height: -150)), 0.7)
        XCTAssertEqual(LyricSizeDrag.scale(startScale: 1.2, start: .zero, translation: CGSize(width: 40, height: 0)), 1.2)
        XCTAssertEqual(LyricSizeDrag.range, 0.7...1.5)
    }

    func testTheLineBoxGrowsWithLyricSizeAndStaysCentredOnTheStage() {
        let picture = CGSize(width: 584, height: 380)
        let small = LyricSizeDrag.box(in: picture, scale: 0.7)
        let large = LyricSizeDrag.box(in: picture, scale: 1.3)
        XCTAssertLessThan(small.width, large.width)
        XCTAssertEqual(small.midX, picture.width / 2, accuracy: 0.5)
        XCTAssertEqual(large.midY, picture.height / 2, accuracy: 0.5)
        // Never past the lyric stage (80 % of the width).
        XCTAssertLessThanOrEqual(LyricSizeDrag.box(in: picture, scale: 1.5).width, picture.width * 0.8 + 0.5)
    }

    // MARK: Choices

    func testTheChoiceGridsHoldEveryOption() {
        XCTAssertEqual(Set(LookChoices.wallpaperTypes), Set(Settings.BackgroundMode.allCases))
        XCTAssertEqual(LookChoices.wallpaperTypes.count, 8)
        XCTAssertEqual(LookChoices.layouts.count, LyricComposition.allCases.count + 1)
        XCTAssertNil(LookChoices.layouts.first ?? .scattered, "Match the music first")
        XCTAssertEqual(Set(LookChoices.layouts.compactMap { $0 }), Set(LyricComposition.allCases))
        XCTAssertEqual(Set(LookChoices.typefaces), Set(Settings.FontChoice.allCases))
        XCTAssertEqual(LookChoices.typefaces.map(LookChoices.shortName),
                       ["Match", "Manrope", "SF Pro", "Avenir", "Charter", "Rockwell", "Rounded", "Other…"])
    }

    /// The spec's tile sizes at the default window: 139 x 70 types, 89 x 52
    /// layouts, 75 x 47 Looks.
    func testTileSizesMatchTheSpecAtTheDefaultWindow() {
        XCTAssertEqual(ChoiceGridMetrics.tile(count: 4, ratio: 0.5, width: 584), CGSize(width: 139, height: 70))
        XCTAssertEqual(ChoiceGridMetrics.tile(count: 6, ratio: 56.0 / 96.0, width: 584).width, 89)
        XCTAssertEqual(ChoiceGridMetrics.tile(count: 3, ratio: 0.625, width: 246, gap: 10), CGSize(width: 75, height: 47))
        XCTAssertEqual(ChoiceGridMetrics.rows(Array(0..<15), count: 6).map(\.count), [6, 6, 3])
    }

    func testChoosingAWallpaperTileSetsTheTypeOrAsksForAFile() {
        let settings = Settings(defaults: LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.LookCanvas"),
                                notifications: NotificationCenter())
        var asked = 0
        WallpaperTypeChoices.choose(.shader, settings: settings, chooseImport: { asked += 1 })
        XCTAssertEqual(settings.backgroundMode, .shader)
        settings.customWallpaperPath = ""
        WallpaperTypeChoices.choose(.custom, settings: settings, chooseImport: { asked += 1 })
        XCTAssertEqual(asked, 1, "nothing imported yet: ask for a file")
        XCTAssertEqual(settings.backgroundMode, .shader, "the type waits for the file")
    }

    // MARK: Looks

    func testSaveAsLookSuggestsAFreeNameAndChecksIt() {
        let looks = Look.builtIns
        XCTAssertEqual(LooksSection.defaultName(for: looks[0], in: looks), "Matched copy")
        XCTAssertEqual(LooksSection.defaultName(for: nil, in: looks), "My Look")
        var copy = looks[1]
        copy = Look(id: "x", name: "Matched copy", isBuiltIn: false, values: copy.values)
        XCTAssertEqual(LooksSection.defaultName(for: looks[0], in: looks + [copy]), "Matched copy 2")
        let taken = Set(looks.map { $0.name.lowercased() })
        XCTAssertNil(LookNameSheet.problem(with: "Late train", taken: taken))
        XCTAssertNotNil(LookNameSheet.problem(with: "  ", taken: taken))
        XCTAssertNotNil(LookNameSheet.problem(with: "quiet", taken: taken), "names are unique, any case")
        XCTAssertNotNil(LookNameSheet.problem(with: String(repeating: "a", count: 31), taken: taken))
        XCTAssertNil(LookNameSheet.problem(with: String(repeating: "a", count: 30), taken: taken))
    }

    // MARK: Pages without a picture

    func testAPageKeepsItsColumnFortyFromTheSidebarAndSixHundredWide() {
        let wide = SettingsDetail.margins(for: 914)
        XCTAssertEqual(wide.leading + SettingsDetail.formPadding, 40)
        XCTAssertEqual(914 - wide.leading - wide.trailing - 2 * SettingsDetail.formPadding, 600, accuracy: 0.5)
        // Narrower than the column: the same margin both sides.
        let narrow = SettingsDetail.margins(for: 500)
        XCTAssertEqual(narrow.leading, narrow.trailing)
    }
}
