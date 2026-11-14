import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// The redesign's form language (SPEC.md §2, §4, §8): density, captions with
/// "More", the window styles, and the pages' renamed labels.
@MainActor
final class FormLanguageTests: XCTestCase {
    // MARK: Density

    func testPagesAndTheInspectorPackToTheSpec() {
        XCTAssertEqual(FormDensity.page.sectionSpacing, 28)
        XCTAssertEqual(FormDensity.page.rowSpacing, 8)
        XCTAssertEqual(FormDensity.inspector.sectionSpacing, 22)
        XCTAssertEqual(FormDensity.inspector.rowSpacing, 9)
    }

    func testFormsArePagesUnlessTheInspectorSaysSo() {
        XCTAssertEqual(EnvironmentValues().formDensity, .page)
        var values = EnvironmentValues()
        values.formDensity = .inspector
        XCTAssertEqual(values.formDensity, .inspector)
    }

    func testPageColumnMeasurements() {
        XCTAssertEqual(ThemedPageMetrics.column, 600)
        XCTAssertEqual(ThemedPageMetrics.inset, 40)
        XCTAssertEqual(ThemedPageMetrics.titleBand, 56)
    }

    // MARK: Captions

    func testACaptionWithALongerTextEndsInMore() {
        let content = InspectorCaption.content("Needs the Discord desktop app.", more: "The whole story.", compact: false)
        XCTAssertEqual(content.shown, "Needs the Discord desktop app.")
        XCTAssertEqual(content.more, "The whole story.")
        let attributed = InspectorCaption.attributed(content.shown)
        let text = String(attributed.characters)
        XCTAssertTrue(text.hasSuffix(InspectorCaption.moreTitle), text)
        XCTAssertEqual(InspectorCaption.moreTitle, "More")
        let link = attributed.runs.compactMap(\.link).first
        XCTAssertEqual(link, InspectorCaption.moreURL)
    }

    func testAShortCaptionHasNoMore() {
        let content = InspectorCaption.content("Caps Line patterns at 30 fps.", more: nil, compact: true)
        XCTAssertNil(content.more)
    }

    func testALongCaptionInAFormFoldsBehindMore() {
        let long = "Each song picks a look to suit its energy and feel. Animated covers play where Apple Music has them, and the look changes when the song does."
        let folded = InspectorCaption.content(long, more: nil, compact: true)
        XCTAssertEqual(folded.shown, "Each song picks a look to suit its energy and feel.")
        XCTAssertEqual(folded.more, long)
        // Outside a form it shows whole.
        XCTAssertNil(InspectorCaption.content(long, more: nil, compact: false).more)
    }

    // MARK: Window style

    func testWindowStyleIsStandardOrBoldUnderTheSameKey() {
        XCTAssertEqual(SettingsVisualStyle.key, "custom.settingsStyle")
        XCTAssertEqual(SettingsVisualStyle.calm.title, "Standard")
        XCTAssertEqual(SettingsVisualStyle.bold.title, "Bold")
        XCTAssertEqual(SettingsVisualStyle(stored: "calm"), .calm)
        XCTAssertEqual(SettingsVisualStyle(stored: "soft"), .calm)
        XCTAssertEqual(SettingsVisualStyle(stored: "clay"), .bold)
    }

    func testStandardIsTheStudioTokens() {
        for dark in [false, true] {
            let tokens = SettingsVisualStyle.calm.tokens(dark: dark)
            XCTAssertEqual(tokens.canvas, LunalyrDesign.Studio.color(.window, dark: dark))
            XCTAssertEqual(tokens.face, LunalyrDesign.Studio.color(.control, dark: dark))
            XCTAssertEqual(tokens.ink, LunalyrDesign.Studio.color(.ink, dark: dark))
            XCTAssertEqual(tokens.secondary, LunalyrDesign.Studio.color(.ink2, dark: dark))
            XCTAssertNil(tokens.shadow, "Standard draws no shadows")
        }
        // Increase Contrast darkens secondary text.
        let contrast = SettingsVisualStyle.calm.tokens(dark: false, increaseContrast: true)
        XCTAssertEqual(contrast.secondary, LunalyrDesign.Studio.color(.ink2, dark: false, increaseContrast: true))
    }

    func testBoldKeepsItsOutlinesAndHardShadows() {
        let bold = SettingsVisualStyle.bold.tokens(dark: false)
        XCTAssertEqual(bold.outline, 1.5)
        XCTAssertNotNil(bold.shadow)
        XCTAssertGreaterThan(bold.shadowOffset, 0)
    }

    // MARK: Controls

    func testControlSizesFollowTheSpec() {
        XCTAssertEqual(ThemedSwitchTrack.size, CGSize(width: 29, height: 17))
        XCTAssertEqual(ThemedCheckboxBox.size, 15)
        XCTAssertEqual(ThemedSliderTrack.thumb, 15)
    }

    // MARK: Pages

    func testShareLabelsAreTheDecidedNames() {
        XCTAssertEqual(ShareInspector.relayLabel, "Relay")
        XCTAssertEqual(ShareInspector.cardSection, "Lyric card")
        XCTAssertEqual(ShareInspector.openCardTitle, "Open Lyric Card\u{2026}")
    }

    func testRomanisationSaysWhichStyleItEdits() {
        XCTAssertTrue(ReadingsPage.styleNote(floating: false).hasPrefix("Changes the Desktop style"))
        XCTAssertTrue(ReadingsPage.styleNote(floating: true).hasPrefix("Changes the Floating style"))
    }

    func testThisSongNamesArtistAndAlbum() {
        let track = NowPlayingMonitor.Track(title: "Song", artist: "Artist", duration: 200, genre: nil, album: "Album")
        XCTAssertEqual(TimingPage.byline(track), "Artist · Album")
        let single = NowPlayingMonitor.Track(title: "Song", artist: "Artist", duration: 200, genre: nil, album: nil)
        XCTAssertEqual(TimingPage.byline(single), "Artist")
    }
}
