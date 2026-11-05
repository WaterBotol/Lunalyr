import AppKit
import AVFoundation
import SwiftUI
import XCTest
@testable import VersoCustom

@MainActor
final class DesktopSurfaceTests: XCTestCase {
    // MARK: Shared video players

    private final class FakeVideo: LoopingVideo {
        let player = AVPlayer()
        var naturalSize: CGSize?
        var onNaturalSize: ((CGSize) -> Void)?
        var plays = 0, pauses = 0, stops = 0
        func play() { plays += 1 }
        func pause() { pauses += 1 }
        func stop() { stops += 1 }
    }

    func testDisplaysShareOneDecoderPerVideo() throws {
        var made: [URL: FakeVideo] = [:]
        let players = SharedVideoPlayers(linger: 0) { url in
            let video = FakeVideo()
            made[url] = video
            return video
        }
        let cover = URL(fileURLWithPath: "/tmp/lunalyr-cover.mp4")
        let laptop = players.acquire(cover, paused: false) { _ in }
        let external = players.acquire(cover, paused: false) { _ in }
        XCTAssertEqual(players.videoCount, 1)
        XCTAssertEqual(players.leaseCount(for: cover), 2)
        XCTAssertTrue(players.player(for: laptop) === players.player(for: external))
        let video = try XCTUnwrap(made[cover])
        XCTAssertEqual(video.plays, 1)
        XCTAssertTrue(players.isPlaying(cover))

        // One display covered: the other still watches, so it keeps playing.
        players.setPaused(true, for: laptop)
        XCTAssertTrue(players.isPlaying(cover))
        players.setPaused(true, for: external)
        XCTAssertFalse(players.isPlaying(cover))
        XCTAssertEqual(video.pauses, 1)
        players.setPaused(false, for: laptop)
        XCTAssertTrue(players.isPlaying(cover))

        players.release(laptop)
        XCTAssertEqual(players.leaseCount(for: cover), 1)
        XCTAssertEqual(video.stops, 0)
        XCTAssertFalse(players.isPlaying(cover), "the remaining holder is paused")
        players.release(external)
        players.release(external) // releasing twice is harmless
        XCTAssertEqual(players.videoCount, 0)
        XCTAssertEqual(video.stops, 1)
        XCTAssertNil(players.player(for: external))
    }

    func testSizeReachesEveryHolderAndDifferentVideosStaySeparate() {
        var made: [FakeVideo] = []
        let players = SharedVideoPlayers(linger: 0) { _ in
            let video = FakeVideo()
            made.append(video)
            return video
        }
        let square = URL(fileURLWithPath: "/tmp/square.mp4"), tall = URL(fileURLWithPath: "/tmp/tall.m3u8")
        var sizes: [CGSize] = []
        let a = players.acquire(square, paused: false) { sizes.append($0) }
        let b = players.acquire(square, paused: true) { sizes.append($0) }
        let c = players.acquire(tall, paused: false) { _ in }
        XCTAssertEqual(players.videoCount, 2)
        made[0].onNaturalSize?(CGSize(width: 1080, height: 1080))
        XCTAssertEqual(sizes, [CGSize(width: 1080, height: 1080), CGSize(width: 1080, height: 1080)])
        [a, b, c].forEach(players.release)
        XCTAssertEqual(players.videoCount, 0)
    }

    func testReleasedVideoLingersSoACrossfadeCanReclaimIt() {
        var created = 0
        let players = SharedVideoPlayers(linger: 30) { _ in created += 1; return FakeVideo() }
        let url = URL(fileURLWithPath: "/tmp/cover.mp4")
        let outgoing = players.acquire(url, paused: false) { _ in }
        players.release(outgoing)
        XCTAssertEqual(players.videoCount, 1, "kept briefly")
        let incoming = players.acquire(url, paused: false) { _ in }
        XCTAssertEqual(created, 1, "the same decoder carries on")
        XCTAssertTrue(players.isPlaying(url))
        players.release(incoming)
    }

    func testPortraitDisplaysTakeTheTallCoverOnlyAsAProgressiveFile() throws {
        let square = try XCTUnwrap(URL(string: "https://mvod.itunes.apple.com/a/square.mp4"))
        let rawTall = try XCTUnwrap(URL(string: "https://mvod.itunes.apple.com/a/tall/master.m3u8"))
        let progressiveTall = try XCTUnwrap(URL(string: "https://mvod.itunes.apple.com/a/tall.MP4"))
        let raw = MotionArtworkProvider.Motion(square: square, tall: rawTall)
        XCTAssertEqual(raw.rendition(portrait: true), square, "a raw HLS playlist may never start or loop cleanly")
        XCTAssertEqual(raw.rendition(portrait: false), square)
        let ready = MotionArtworkProvider.Motion(square: square, tall: progressiveTall)
        XCTAssertEqual(ready.rendition(portrait: true), progressiveTall)
        XCTAssertEqual(ready.rendition(portrait: false), square)
        XCTAssertEqual(MotionArtworkProvider.Motion(square: square, tall: nil).rendition(portrait: true), square)
    }

    // MARK: Controls panel hit mask

    func testControlsTakeClicksOnlyWhereTheyAreDrawn() throws {
        let size = CGSize(width: 400, height: 330)
        let panel = VStack {
            Spacer()
            Circle().fill(.white).frame(width: 48, height: 48)
                .shadow(color: .black.opacity(0.5), radius: 10, y: 2)
                .padding(.bottom, 40)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        let mask = try XCTUnwrap(ControlHitMask.render(panel, size: size))
        XCTAssertFalse(mask.isEmpty)
        // AppKit orientation: the button sits 40-88 pt above the bottom.
        XCTAssertTrue(mask.contains(CGPoint(x: 200, y: 64)))
        XCTAssertTrue(mask.contains(CGPoint(x: 200 + 26, y: 64)), "edge and soft shadow still hit")
        // Empty corners and the empty top half let the desktop through.
        XCTAssertFalse(mask.contains(CGPoint(x: 10, y: 10)))
        XCTAssertFalse(mask.contains(CGPoint(x: 390, y: 320)))
        XCTAssertFalse(mask.contains(CGPoint(x: 200, y: 250)))
        XCTAssertFalse(mask.contains(CGPoint(x: -5, y: 64)))
        XCTAssertFalse(mask.contains(CGPoint(x: 200, y: 400)))
    }

    func testAnUnusableMaskIsRetriedAtMostOnceASecond() {
        // Never rendered: render now.
        XCTAssertTrue(ControlHitMask.needsRender(usable: nil, stale: false, age: 0))
        // A good mask follows content changes and refreshes every 2 s.
        XCTAssertTrue(ControlHitMask.needsRender(usable: true, stale: true, age: 0.1))
        XCTAssertFalse(ControlHitMask.needsRender(usable: true, stale: false, age: 1.5))
        XCTAssertTrue(ControlHitMask.needsRender(usable: true, stale: false, age: 2.5))
        // An empty or failed one is not re-rendered on every 90 ms poll,
        // even while the content keeps changing.
        XCTAssertFalse(ControlHitMask.needsRender(usable: false, stale: false, age: 0.09))
        XCTAssertFalse(ControlHitMask.needsRender(usable: false, stale: true, age: 0.5))
        XCTAssertTrue(ControlHitMask.needsRender(usable: false, stale: false, age: 1.1))
    }

    func testAHeldButtonNeverHandsThePointerToOrFromTheControls() {
        // Over the controls with no button: the panel takes the click.
        XCTAssertTrue(ControlHitMask.acceptsMouse(active: true, overContent: true, buttonsHeld: false, previously: false))
        // Elsewhere, or with hover inactive, the desktop does.
        XCTAssertFalse(ControlHitMask.acceptsMouse(active: true, overContent: false, buttonsHeld: false, previously: true))
        XCTAssertFalse(ControlHitMask.acceptsMouse(active: false, overContent: true, buttonsHeld: false, previously: true))
        // A rubber band or file drag that began on the desktop stays there...
        XCTAssertFalse(ControlHitMask.acceptsMouse(active: true, overContent: true, buttonsHeld: true, previously: false))
        // ...and a scrub that began on the controls keeps them.
        XCTAssertTrue(ControlHitMask.acceptsMouse(active: true, overContent: false, buttonsHeld: true, previously: true))
    }

    // MARK: Options

    func testDesktopOptionsDefaultOnAndPersistInTheirOwnKeys() throws {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let options = DesktopOptions(defaults: defaults)
        XCTAssertTrue(options.showsControlsOnHover)
        XCTAssertTrue(options.automaticTextBacking)
        XCTAssertNil(options.manualTextBacking)
        options.showsControlsOnHover = false
        options.automaticTextBacking = false
        options.textBacking = 0.6
        let reloaded = DesktopOptions(defaults: defaults)
        XCTAssertFalse(reloaded.showsControlsOnHover)
        XCTAssertEqual(reloaded.manualTextBacking, 0.6)
        defaults.set(Double.nan, forKey: DesktopOptions.Keys.textBacking)
        defaults.set(7.0, forKey: "unrelated")
        XCTAssertEqual(DesktopOptions(defaults: defaults).textBacking, 0.35)
        defaults.set(3.0, forKey: DesktopOptions.Keys.textBacking)
        XCTAssertEqual(DesktopOptions(defaults: defaults).textBacking, 1)
        XCTAssertTrue([DesktopOptions.Keys.controlsOnHover, DesktopOptions.Keys.automaticTextBacking,
                       DesktopOptions.Keys.textBacking].allSatisfy { $0.hasPrefix("custom.") })
    }

    // MARK: Hover polling

    func testHoverPollStopsWhenNothingCanBeRevealed() {
        let displays: Set<CGDirectDisplayID> = [1, 2]
        XCTAssertTrue(HoverState.shouldPoll(enabled: true, systemHidden: false, hidden: [], displays: displays))
        XCTAssertTrue(HoverState.shouldPoll(enabled: true, systemHidden: false, hidden: [1], displays: displays))
        XCTAssertFalse(HoverState.shouldPoll(enabled: true, systemHidden: false, hidden: [1, 2], displays: displays))
        XCTAssertFalse(HoverState.shouldPoll(enabled: true, systemHidden: true, hidden: [], displays: displays))
        XCTAssertFalse(HoverState.shouldPoll(enabled: false, systemHidden: false, hidden: [], displays: displays))
    }

    // MARK: Window opacity

    func testOnlyYourWallpaperNeedsATransparentWindow() {
        for mode in Settings.BackgroundMode.allCases {
            XCTAssertEqual(DesktopWallpaperController.needsTransparency(mode), mode == .desktop, mode.title)
        }
    }

    /// Leaving Your wallpaper, a window that turns opaque paints black under
    /// the incoming wallpaper's fade. A fixed 1.2 s cut the desktop to black
    /// whenever an effect took longer to compile, since the fade waits for it.
    func testWindowsTurnOpaqueOnlyOnceTheIncomingWallpaperHasFadedIn() {
        typealias Policy = WallpaperOpacityPolicy
        func step(at now: Double, compiling: Bool = false, settledAt: Double? = nil, paused: Bool = false,
                  resumedAt: Double? = nil) -> Policy.Step {
            Policy.step(now: 100 + now, changedAt: 100, compiling: compiling, settledAt: settledAt.map { 100 + $0 },
                        paused: paused, resumedAt: resumedAt.map { 100 + $0 })
        }
        func wait(_ step: Policy.Step, line: UInt = #line) -> Double {
            guard case .wait(let seconds) = step else {
                XCTFail("expected a wait, got \(step)", line: line)
                return .nan
            }
            return seconds
        }
        // Nothing compiling: the fade's own time.
        XCTAssertEqual(wait(step(at: 0.5)), Policy.settleTime - 0.5, accuracy: 1e-9)
        XCTAssertEqual(step(at: Policy.settleTime), .opaque)
        // A compile in flight holds the switch, looked at often...
        XCTAssertEqual(step(at: 1.2, compiling: true), .wait(Policy.pollInterval))
        XCTAssertEqual(step(at: 9.9, compiling: true), .wait(Policy.pollInterval))
        // ...and the fade runs from when it finished.
        XCTAssertEqual(wait(step(at: 3.1, settledAt: 3)), Policy.settleTime - 0.1, accuracy: 1e-9)
        XCTAssertEqual(step(at: 3 + Policy.settleTime, settledAt: 3), .opaque)
        // A compile finished before the change delays nothing; one that never
        // finishes is not waited for beyond the limit.
        XCTAssertEqual(step(at: Policy.settleTime, settledAt: -5), .opaque)
        XCTAssertEqual(step(at: Policy.compileWait, compiling: true), .opaque)
        // A paused wallpaper's fade cannot run: its windows stay clear until
        // it resumes and the fade has had its time.
        XCTAssertEqual(step(at: 30, paused: true), .hold)
        XCTAssertEqual(wait(step(at: 30.1, resumedAt: 30)), Policy.settleTime - 0.1, accuracy: 1e-9)
        XCTAssertEqual(step(at: 30 + Policy.settleTime, resumedAt: 30), .opaque)
    }
}
