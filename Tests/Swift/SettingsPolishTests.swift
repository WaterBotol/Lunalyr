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
}
