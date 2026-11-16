import XCTest
@testable import VersoCustom

final class TranslationPlacementTests: XCTestCase {
    func testStoredPlacementAndDefault() {
        let defaults = UserDefaults(suiteName: "TranslationPlacementTests")!
        defaults.removePersistentDomain(forName: "TranslationPlacementTests")
        XCTAssertEqual(TranslationPlacement.stored(in: defaults), .below)
        defaults.set("top", forKey: LunalyrExtras.Keys.translationPlacement)
        XCTAssertEqual(TranslationPlacement.stored(in: defaults), .top)
        defaults.set("nonsense", forKey: LunalyrExtras.Keys.translationPlacement)
        XCTAssertEqual(TranslationPlacement.stored(in: defaults), .below)
        defaults.removePersistentDomain(forName: "TranslationPlacementTests")
    }

    func testEdgesAndSides() {
        XCTAssertEqual(TranslationPlacement.allCases.filter(\.atEdge), [.bottom, .top])
        XCTAssertEqual(TranslationPlacement.allCases.filter(\.overWords), [.above, .top])
    }

    /// The place is part of a lyric style, so each style (and monitor) keeps its own.
    @MainActor
    func testThePlaceIsPartOfALyricStyle() {
        XCTAssertTrue(LyricStyleProfiles.keys.contains(LunalyrExtras.Keys.translationPlacement))
    }
}
