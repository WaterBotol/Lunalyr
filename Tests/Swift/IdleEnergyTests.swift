import AppKit
import Combine
import XCTest
@testable import VersoCustom

/// Lunalyr runs all day: session caches must stay bounded, covers must be
/// decoded no larger than they are shown, and a long pause must not keep
/// waking the player every two seconds.
final class IdleEnergyTests: XCTestCase {
    // MARK: - BoundedCache

    func testBoundedCacheForgetsTheLeastRecentlyUsedPastItsCapacity() {
        var cache = BoundedCache<String, Int>(capacity: 3)
        cache.insert(1, for: "a")
        cache.insert(2, for: "b")
        cache.insert(3, for: "c")
        XCTAssertEqual(cache.value(for: "a"), 1, "Touching a keeps it")
        cache.insert(4, for: "d")
        XCTAssertEqual(cache.count, 3)
        XCTAssertNil(cache.value(for: "b"), "The least recently used goes first")
        XCTAssertEqual(cache.value(for: "a"), 1)
        XCTAssertEqual(cache.value(for: "c"), 3)
        XCTAssertEqual(cache.value(for: "d"), 4)
    }

    func testBoundedCacheKeepsItsTotalCostUnderTheLimit() {
        var cache = BoundedCache<String, Data>(capacity: 100, maxCost: 1_000, cost: { $0.count })
        for index in 0..<50 { cache.insert(Data(count: 300), for: "\(index)") }
        XCTAssertLessThanOrEqual(cache.totalCost, 1_000)
        XCTAssertEqual(cache.count, 3)
        XCTAssertNotNil(cache.value(for: "49"), "The newest stays")
        // One entry larger than the limit is still kept on its own, rather
        // than the cache refusing the cover being shown.
        cache.insert(Data(count: 5_000), for: "huge")
        XCTAssertEqual(cache.count, 1)
        XCTAssertNotNil(cache.value(for: "huge"))
        cache.insert(Data(count: 10), for: "huge")
        XCTAssertEqual(cache.totalCost, 10, "Replacing an entry replaces its cost")
        cache.remove("huge")
        XCTAssertEqual(cache.totalCost, 0)
        XCTAssertEqual(cache.count, 0)
    }

    func testArtworkCachesAreBounded() {
        XCTAssertLessThanOrEqual(ArtworkProvider.cacheCapacity, 128)
        XCTAssertLessThanOrEqual(ArtworkProvider.cacheBytes, 32_000_000)
        XCTAssertLessThanOrEqual(MotionArtworkProvider.cacheCapacity, 512)
    }

    // MARK: - Cover decoding

    private func png(side: Int) -> Data {
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: side, pixelsHigh: side, bitsPerSample: 8,
                                   samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                   colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        return rep.representation(using: .png, properties: [:])!
    }

    func testALargeCoverIsDecodedNoLargerThanItIsShown() {
        let image = ArtworkImage.decode(png(side: 3000))
        XCTAssertEqual(image?.width, ArtworkImage.maxPixelSize)
        XCTAssertEqual(image?.height, ArtworkImage.maxPixelSize)
        XCTAssertEqual(ArtworkImage.decode(png(side: 3000), maxPixelSize: 400)?.width, 400)
    }

    func testASmallCoverKeepsItsSizeAndJunkIsNotACover() {
        let image = ArtworkImage.decode(png(side: 600))
        XCTAssertEqual(image?.width, 600, "Never scaled up")
        XCTAssertEqual(image.map(ArtworkImage.image)?.size, NSSize(width: 600, height: 600))
        XCTAssertNil(ArtworkImage.decode(Data("not an image".utf8)))
        XCTAssertNil(ArtworkImage.decode(Data()))
    }

    func testDecodingOffTheMainThreadGivesTheSameCover() async {
        let image = await ArtworkImage.decodeDetached(png(side: 64))
        XCTAssertEqual(image?.width, 64)
        let none = await ArtworkImage.decodeDetached(nil)
        XCTAssertNil(none)
    }

    // MARK: - Hover poll

    func testTheHoverPollSlowsOnlyOnceThePointerHasRestedWithNothingShown() {
        let fast = HoverState.pollInterval(stillFor: 0, active: false)
        XCTAssertLessThan(fast, 0.1, "A moving pointer is followed closely")
        XCTAssertEqual(HoverState.pollInterval(stillFor: HoverState.restAfter - 0.01, active: false), fast)
        XCTAssertEqual(HoverState.pollInterval(stillFor: HoverState.restAfter, active: false), HoverState.restingInterval)
        XCTAssertEqual(HoverState.pollInterval(stillFor: 600, active: true), fast, "Controls up: always quick")
        XCTAssertLessThanOrEqual(HoverState.restingInterval, 0.3, "A first movement is still noticed promptly")
    }

    // MARK: - Re-sync of a paused player

    private static let song = FakeMusicApp.Song(id: "00000000000000C1", name: "Long Pause", album: "Rest")

    @MainActor
    private func pausedHarness() -> PlayerHarness {
        let music = FakeMusicApp()
        music.queue = [Self.song]
        music.state = .paused
        music.position = 42
        return PlayerHarness(music: music)
    }

    @MainActor
    func testAPausedPlayerIsReadEveryTickAtFirstThenEveryFifthAfterAMinute() {
        let h = pausedHarness()
        h.start()
        XCTAssertFalse(h.monitor.isPlaying)
        h.music.clearLog()
        for _ in 0..<5 {
            h.clock.now += 2
            h.monitor.tick()
            h.scheduler.drain()
        }
        XCTAssertEqual(h.music.count("music.position"), 5, "A fresh pause still catches a scrub every tick")

        h.clock.now += NowPlayingMonitor.restingAfter
        h.music.clearLog()
        for _ in 0..<10 {
            h.clock.now += 2
            h.monitor.tick()
            h.scheduler.drain()
        }
        XCTAssertEqual(h.music.count("music.position"), 2, "A long pause is re-read like an idle player")

        // A scrub during the long pause is still found, just later.
        h.music.position = 120
        for _ in 0..<5 {
            h.clock.now += 2
            h.monitor.tick()
            h.scheduler.drain()
        }
        XCTAssertEqual(h.monitor.playbackTime(), 120, accuracy: 0.001)
    }

    @MainActor
    func testPlayingAgainRestoresTheFullRateAndANewPauseStartsAfresh() {
        let h = pausedHarness()
        h.start()
        h.clock.now += NowPlayingMonitor.restingAfter + 1
        h.music.state = .playing
        h.postMusic(Self.song, "Playing")
        h.scheduler.drain()
        XCTAssertTrue(h.monitor.isPlaying)
        h.music.clearLog()
        for _ in 0..<4 {
            h.clock.now += 2
            h.monitor.tick()
            h.scheduler.drain()
        }
        XCTAssertEqual(h.music.count("music.position"), 4, "Playing is read every tick")

        h.music.state = .paused
        h.postMusic(Self.song, "Paused")
        h.scheduler.drain()
        h.music.clearLog()
        for _ in 0..<4 {
            h.clock.now += 2
            h.monitor.tick()
            h.scheduler.drain()
        }
        XCTAssertEqual(h.music.count("music.position"), 4, "The minute counts from this pause, not the last one")
    }

    /// Asleep or locked displays show no lyrics: the re-sync slows down, and
    /// waking reads the player at once instead of at the next slow tick.
    @MainActor
    func testTheReSyncBacksOffWhileTheScreensAreHiddenAndCatchesUpOnWake() {
        let music = FakeMusicApp()
        music.queue = [Self.song]
        music.state = .playing
        let hidden = CurrentValueSubject<Bool, Never>(false)
        let followed = CurrentValueSubject<[PlayerKind], Never>([.appleMusic])
        var services = NowPlayingServices.offline
        services.screensHidden = hidden.eraseToAnyPublisher()
        services.followedPlayers = followed.eraseToAnyPublisher()
        let scheduler = ManualScheduler(), clock = TestClock()
        let monitor = NowPlayingMonitor(
            runner: FakePlayers(music: music, spotify: FakeSpotifyApp(), consent: FakeAutomationPrompt(clock: clock)),
            environment: FakePlayerEnvironment(running: ["com.apple.Music"]), scheduler: scheduler,
            permission: { _, _ in 0 }, services: services, uptime: { clock.now }, pollInterval: 2)
        // Stop the real timer however the test ends.
        defer { followed.send([]); XCTAssertNil(monitor.resyncInterval) }
        monitor.start()
        scheduler.drain()
        XCTAssertTrue(monitor.isPolling)
        XCTAssertEqual(monitor.resyncInterval, 2)

        music.clearLog()
        hidden.send(true)
        scheduler.drain()
        XCTAssertEqual(monitor.resyncInterval, 2 * NowPlayingMonitor.hiddenPollFactor)
        XCTAssertEqual(music.log, [], "Going to sleep sends nothing")
        hidden.send(true)
        XCTAssertEqual(monitor.resyncInterval, 2 * NowPlayingMonitor.hiddenPollFactor)

        hidden.send(false)
        scheduler.drain()
        XCTAssertEqual(monitor.resyncInterval, 2)
        XCTAssertEqual(music.count("music.read"), 1, "Read again the moment the screens wake")
    }
}
