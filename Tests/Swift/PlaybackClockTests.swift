import XCTest
@testable import VersoCustom

final class PlaybackClockTests: XCTestCase {
    func testSmallCorrectionsEaseInWithoutJumping() throws {
        var clock = PlaybackClock()
        clock.sync(to: 10, sampledAt: 0, running: true, now: 0, continuous: false)
        // Two seconds on, the player says 12.12: 120 ms ahead of the extrapolation.
        clock.sync(to: 12.12, sampledAt: 2, running: true, now: 2, continuous: true)
        XCTAssertEqual(try XCTUnwrap(clock.position(at: 2)), 12, accuracy: 1e-9, "No jump at the re-sync")
        XCTAssertTrue(clock.isSlewing(at: 2.2))
        let settled = 2 + PlaybackClock.slewDuration
        XCTAssertEqual(try XCTUnwrap(clock.position(at: settled)), 12.12 + PlaybackClock.slewDuration, accuracy: 1e-9)
        XCTAssertFalse(clock.isSlewing(at: settled + 0.001))
        XCTAssertEqual(try XCTUnwrap(clock.position(at: 5)), 15.12, accuracy: 1e-9)
    }

    func testCorrectionsNeverRunBackwardsAndDoNotDependOnFrameRate() throws {
        for error in [-0.24, -0.1, 0.05, 0.24] {
            var reference: [Double: Double] = [:]
            for rate in [30.0, 60.0, 120.0] {
                var clock = PlaybackClock()
                clock.sync(to: 50, sampledAt: 0, running: true, now: 0, continuous: false)
                clock.sync(to: 52 + error, sampledAt: 2, running: true, now: 2, continuous: true)
                var last = try XCTUnwrap(clock.position(at: 2))
                for frame in 1...Int(rate) {
                    let position = try XCTUnwrap(clock.position(at: 2 + Double(frame) / rate))
                    XCTAssertGreaterThan(position, last, "error \(error) at \(rate) Hz")
                    last = position
                }
                for instant in [2.1, 2.2, 2.3, 2.5] {
                    let position = try XCTUnwrap(clock.position(at: instant))
                    if let expected = reference[instant] {
                        XCTAssertEqual(position, expected, accuracy: 1e-12, "Same instant, same position at \(rate) Hz")
                    } else {
                        reference[instant] = position
                    }
                }
                XCTAssertEqual(try XCTUnwrap(clock.position(at: 3)), 53 + error, accuracy: 1e-9, "Lands on the sample")
            }
        }
    }

    func testSeeksNewTracksAndPausesMoveAtOnce() throws {
        var clock = PlaybackClock()
        clock.sync(to: 10, sampledAt: 0, running: true, now: 0, continuous: false)
        clock.sync(to: 80, sampledAt: 2, running: true, now: 2, continuous: true)
        XCTAssertEqual(try XCTUnwrap(clock.position(at: 2)), 80, accuracy: 1e-9, "A real seek jumps")
        clock.sync(to: 0.05, sampledAt: 3, running: true, now: 3, continuous: false)
        XCTAssertEqual(try XCTUnwrap(clock.position(at: 3)), 0.05, accuracy: 1e-9, "A new track jumps")
        clock.setRunning(false, now: 4)
        XCTAssertEqual(try XCTUnwrap(clock.position(at: 9)), 1.05, accuracy: 1e-9, "Paused holds")
        clock.setRunning(true, now: 9)
        XCTAssertEqual(try XCTUnwrap(clock.position(at: 10)), 2.05, accuracy: 1e-9, "Resumes where it held")
        clock.reset()
        XCTAssertNil(clock.position(at: 10))
    }

    func testTheSampleTimeIsHonoured() throws {
        var clock = PlaybackClock()
        // Sampled at 100, applied 80 ms later.
        clock.sync(to: 30, sampledAt: 100, running: true, now: 100.08, continuous: false)
        XCTAssertEqual(try XCTUnwrap(clock.position(at: 100.08)), 30.08, accuracy: 1e-9)
        clock.sync(to: 31, sampledAt: 101, running: false, now: 101.5, continuous: true)
        XCTAssertEqual(try XCTUnwrap(clock.position(at: 101.5)), 31, accuracy: 1e-9, "Paused samples do not advance")
    }
}
