import AVFoundation
import XCTest
@testable import VersoCustom

/// The welcome's liquid-glass opening: when it plays, and that the bundled
/// video is silent and short. Nothing is played here.
@MainActor
final class WelcomeGlassTests: XCTestCase {
    func testPlaysOncePerLaunchWithMotionAllowedOutsideTests() {
        XCTAssertTrue(WelcomeGlass.shouldPlay(reduceMotion: false, alreadyPlayed: false, isUnitTesting: false, hasVideo: true))
        XCTAssertFalse(WelcomeGlass.shouldPlay(reduceMotion: true, alreadyPlayed: false, isUnitTesting: false, hasVideo: true),
                       "Reduce Motion goes straight to the preview")
        XCTAssertFalse(WelcomeGlass.shouldPlay(reduceMotion: false, alreadyPlayed: true, isUnitTesting: false, hasVideo: true),
                       "Back to the welcome does not replay it")
        XCTAssertFalse(WelcomeGlass.shouldPlay(reduceMotion: false, alreadyPlayed: false, isUnitTesting: true, hasVideo: true))
        XCTAssertFalse(WelcomeGlass.shouldPlay(reduceMotion: false, alreadyPlayed: false, isUnitTesting: false, hasVideo: false))
        XCTAssertFalse(WelcomeGlass.shouldPlay(reduceMotion: false, alreadyPlayed: false),
                       "never inside the unit-test host")
    }

    func testTheBundledVideoIsSilentSquareAndShort() async throws {
        let url = try XCTUnwrap(WelcomeGlass.url, "WelcomeGlass.mp4 is in the app bundle")
        let asset = AVURLAsset(url: url)
        let audio = try await asset.loadTracks(withMediaType: .audio)
        XCTAssertTrue(audio.isEmpty, "no sound track at all")
        let video = try await asset.loadTracks(withMediaType: .video)
        let track = try XCTUnwrap(video.first)
        let size = try await track.load(.naturalSize)
        XCTAssertEqual(size.width, size.height, "square")
        let duration = try await asset.load(.duration).seconds
        XCTAssertLessThan(duration, 5, "an opening, not a wait")
        let bytes = try XCTUnwrap(try FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int)
        XCTAssertLessThan(bytes, 2_000_000, "small enough to ship")
    }
}
