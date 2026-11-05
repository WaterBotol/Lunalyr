import Combine
import XCTest
@testable import VersoCustom

/// The permission banner in the menu and the Settings preview: what it
/// says about a player macOS won't let Lunalyr read, and the ways out.
@MainActor
final class AutomationStatusBannerTests: XCTestCase {
    private let musicID = "com.apple.Music"
    private let spotifyID = "com.spotify.client"

    /// Music stopped and readable, Spotify open and refused.
    private func refusedSpotify(followed: [PlayerKind] = PlayerKind.allCases) -> PlayerHarness {
        var services = NowPlayingServices.offline
        services.followedPlayers = Just(followed).eraseToAnyPublisher()
        let music = FakeMusicApp()
        music.queue = [FakeMusicApp.Song(id: "00000000000000A1", name: "First Light")]
        music.state = .stopped
        let spotify = FakeSpotifyApp()
        spotify.queue = [FakeSpotifyApp.Song(id: "spotify:track:1", name: "Elsewhere")]
        let h = PlayerHarness(music: music, spotify: spotify, running: [musicID, spotifyID], services: services)
        h.permission.statuses[spotifyID] = OSStatus(AppleEventCode.notPermitted)
        return h
    }

    func testARefusedPlayerTheUserMayNotUseOffersFollowingTheOtherOnly() {
        let h = refusedSpotify()
        h.start()
        XCTAssertNil(h.monitor.current)
        XCTAssertEqual(AutomationStatusBanner.content(for: h.monitor),
                       .init(player: .spotify, undecided: false, alternative: .appleMusic))
        XCTAssertEqual(PlayerOptions.Choice.only(.appleMusic), .appleMusic)
        XCTAssertEqual(PlayerOptions.Choice.only(.spotify), .spotify)
        for kind in PlayerKind.allCases {
            XCTAssertEqual(PlayerOptions.Choice.only(kind).players, [kind])
        }
    }

    func testTheOnlyPlayerFollowedHasNoAlternative() {
        let h = refusedSpotify(followed: [.spotify])
        h.start()
        XCTAssertEqual(AutomationStatusBanner.content(for: h.monitor),
                       .init(player: .spotify, undecided: false, alternative: nil))
    }

    func testAPlayerThatIsNotInstalledIsNeverOfferedInstead() {
        let music = FakeMusicApp()
        music.queue = [FakeMusicApp.Song(id: "00000000000000A1", name: "First Light")]
        let h = PlayerHarness(music: music, running: [musicID])
        h.environment.installed = [musicID]
        h.permission.statuses[musicID] = OSStatus(AppleEventCode.notPermitted)
        h.start()
        XCTAssertEqual(AutomationStatusBanner.content(for: h.monitor),
                       .init(player: .appleMusic, undecided: false, alternative: nil),
                       "Following Spotify alone would follow nothing")
    }

    /// Following the other player alone would only move the warning to it.
    func testWithBothPlayersRefusedNeitherIsOfferedInstead() {
        let h = refusedSpotify()
        h.permission.statuses[musicID] = OSStatus(AppleEventCode.notPermitted)
        h.start()
        XCTAssertEqual(h.monitor.deniedPlayers, [.appleMusic, .spotify])
        XCTAssertNil(h.monitor.playerToFollowInstead(of: .appleMusic))
        XCTAssertNil(h.monitor.playerToFollowInstead(of: .spotify))
        XCTAssertEqual(AutomationStatusBanner.content(for: h.monitor),
                       .init(player: .appleMusic, undecided: false, alternative: nil))

        // The banner names the player the wallpaper names: the one playing.
        h.postSpotify(h.spotify.queue[0], "Playing", at: 0)
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.idleRefusal, .spotify)
        XCTAssertEqual(AutomationStatusBanner.content(for: h.monitor),
                       .init(player: .spotify, undecided: false, alternative: nil))
    }

    func testAnUndecidedPlayerIsAnInvitationWithTheSameWayOut() {
        let h = refusedSpotify()
        h.permission.statuses[spotifyID] = OSStatus(AppleEventCode.wouldRequireConsent)
        h.start()
        XCTAssertEqual(AutomationStatusBanner.content(for: h.monitor),
                       .init(player: .spotify, undecided: true, alternative: .appleMusic))
    }

    func testNothingShowsWhileAnotherPlayersSongIsOnScreen() {
        let h = refusedSpotify()
        h.music.state = .playing
        h.start()
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertEqual(h.monitor.deniedPlayers, [.spotify])
        XCTAssertNil(AutomationStatusBanner.content(for: h.monitor))
    }
}
