import AppKit
import Combine
import XCTest
@testable import VersoCustom

// Review round 2: local players and onboarding (R2-7, R2-15). Fakes and
// UUID defaults suites only: no Apple events, no real preferences.

// MARK: - R2-15: local-file names

final class TrackNumberTests: XCTestCase {
    func testOnlyNumbersThatLookLikeTrackNumbersAreDropped() {
        for (name, expected) in [
            ("01 Song", "Song"), ("01. Song", "Song"), ("1-03 Song", "Song"), ("01 - Song", "Song"),
            ("7. Intro", "Intro"), ("12 - Song", "Song"), ("003 Song", "Song"),
            ("99 Luftballons", "99 Luftballons"), ("21 Guns", "21 Guns"), ("7 Seconds", "7 Seconds"),
            ("7-Eleven", "7-Eleven"), ("10.000 Maniacs", "10.000 Maniacs"), ("1999", "1999"),
        ] {
            XCTAssertEqual(VLCBackend.stripTrackNumber(name), expected, name)
        }
        XCTAssertTrue(VLCBackend.titleAndArtist(name: nil, path: "/Music/99 Luftballons.mp3") == ("99 Luftballons", ""))
        XCTAssertTrue(VLCBackend.titleAndArtist(name: nil, path: "/Music/02 Nena - 99 Luftballons.flac")
                      == ("99 Luftballons", "Nena"))
    }
}

// MARK: - R2-15: installed players, looked up once in a while

final class InstalledPlayersTests: XCTestCase {
    func testLookupsAreRememberedForAWhile() {
        final class Counter: @unchecked Sendable {
            var lookups = 0
            var now: TimeInterval = 100
            var installed: Set<PlayerKind> = [.appleMusic]
        }
        let counter = Counter()
        let cache = InstalledPlayers(lifetime: 60, now: { counter.now }, isInstalled: { kind in
            counter.lookups += 1
            return counter.installed.contains(kind)
        })
        XCTAssertEqual(cache.current, [.appleMusic])
        XCTAssertEqual(counter.lookups, PlayerKind.allCases.count)
        counter.installed = [.appleMusic, .vlc]
        for _ in 0..<50 { _ = cache.current }
        XCTAssertEqual(counter.lookups, PlayerKind.allCases.count, "redraws ask nothing more")
        XCTAssertEqual(cache.current, [.appleMusic])
        counter.now += 61
        XCTAssertEqual(cache.current, [.appleMusic, .vlc], "a player installed meanwhile shows up a minute later")
        XCTAssertEqual(counter.lookups, 2 * PlayerKind.allCases.count)
    }
}

// MARK: - R2-7: VLC, Swinsian and VOX are opt-in

@MainActor
final class LocalPlayersOptInTests: XCTestCase {
    private func isolated() -> (UserDefaults, String) {
        let name = "LunalyrTests.\(UUID().uuidString)"
        return (UserDefaults(suiteName: name)!, name)
    }

    func testAutomaticFollowsLocalPlayersOnlyOnceTurnedOn() {
        let (defaults, name) = isolated()
        defer { defaults.removePersistentDomain(forName: name) }
        // Someone who used Automatic, with web players on, before this setting existed.
        defaults.set("automatic", forKey: PlayerOptions.key)
        defaults.set(true, forKey: PlayerOptions.webKey)
        let options = PlayerOptions(defaults: defaults)
        XCTAssertFalse(options.followsLocalPlayers)
        XCTAssertFalse(options.followedPlayers.contains { $0.family == .localFiles })
        XCTAssertTrue(options.followedPlayers.contains(.safari))

        var received: [[PlayerKind]] = []
        let watch = options.playersPublisher.sink { received.append($0) }
        defer { watch.cancel() }
        options.followsLocalPlayers = true
        XCTAssertEqual(defaults.object(forKey: PlayerOptions.localKey) as? Bool, true)
        XCTAssertEqual(PlayerOptions(defaults: defaults).followsLocalPlayers, true)
        XCTAssertTrue(received.last?.contains(.vlc) ?? false)
        options.followsLocalPlayers = false
        XCTAssertEqual(received.last?.contains(.vlc), false)
    }

    func testAnOpenVLCIsNeverAddressedUntilTurnedOn() {
        let (defaults, name) = isolated()
        defer { defaults.removePersistentDomain(forName: name) }
        let options = PlayerOptions(defaults: defaults)
        let runner = RoutingRunner()
        let vlc = FakeLocalPlayer(.vlc)
        runner.routes["vlc."] = vlc
        let scheduler = ManualScheduler()
        let clock = TestClock()
        var services = NowPlayingServices.offline
        services.followedPlayers = options.playersPublisher
        let monitor = NowPlayingMonitor(
            runner: runner, environment: FakePlayerEnvironment(running: ["org.videolan.vlc"], installed: ["org.videolan.vlc"]),
            scheduler: scheduler, permission: { _, _ in 0 }, services: services, uptime: { clock.now }, pollInterval: nil)
        monitor.start()
        scheduler.drain()
        monitor.tick()
        scheduler.drain()
        XCTAssertEqual(vlc.log, [], "an open VLC is left alone")
        XCTAssertNil(monitor.current)

        options.followsLocalPlayers = true
        scheduler.drain()
        XCTAssertEqual(monitor.activePlayer, .vlc)
        XCTAssertEqual(monitor.current?.title, "Song")
    }

    func testAccessRowsAndOnboardingNameOnlyWhatIsThere() {
        XCTAssertEqual(PlayerAccessList.shown(installed: [.appleMusic, .vlc, .safari], followed: [.appleMusic, .spotify]),
                       [.appleMusic], "no row offering to ask for control of an unfollowed VLC")

        // The line under the title names the apps; the caption under the
        // list, what else can be followed once turned on.
        let musicOnly = OnboardingCopy.permissionNote(installed: [.appleMusic])
            + OnboardingCopy.permissionDetail(installed: [.appleMusic])
        for absent in ["Spotify", "VLC", "Swinsian", "VOX"] {
            XCTAssertFalse(musicOnly.contains(absent), absent)
        }
        XCTAssertTrue(musicOnly.contains("Music"))
        XCTAssertTrue(musicOnly.contains("YouTube Music, SoundCloud and Amazon Music"))

        XCTAssertTrue(OnboardingCopy.permissionNote(installed: [.appleMusic, .spotify, .vox]).contains("Music and Spotify"))
        let withVox = OnboardingCopy.permissionDetail(installed: [.appleMusic, .spotify, .vox])
        XCTAssertTrue(withVox.contains("Once you turn them on in Settings, it also follows VOX"))
        XCTAssertFalse(withVox.contains("VLC"))
        XCTAssertFalse(OnboardingCopy.permissionNote(installed: []).isEmpty)
        XCTAssertEqual(OnboardingCopy.list(["A", "B", "C"]), "A, B and C")
        XCTAssertEqual(MusicPlayersPage.localTitle([.vlc, .vox]), "Follow VLC and VOX")
    }
}
