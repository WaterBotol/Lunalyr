import XCTest
@testable import VersoCustom

/// The desktop player's queue lists Music's own Up Next, and a click on one
/// of its songs skips forward to it rather than starting it afresh.
@MainActor
final class PlayerUpNextTests: XCTestCase {
    private func queued(_ title: String, _ artist: String = "Our Artist", album: String = "Our Album",
                        duration: Double = 200, id: String? = nil) -> QueuedTrack {
        QueuedTrack(title: title, artist: artist, album: album, duration: duration, genre: nil, identifier: id,
                    isCurrent: false)
    }

    func testUpNextRowsCountTheirStepsFromThePlayingSong() {
        let rows = DesktopPlayerModel.upNextItems([queued("One", id: "a"), queued("Two", id: "b"), queued("Three")],
                                                  playlist: [])
        XCTAssertEqual(rows.map(\.upNextStep), [1, 2, 3])
        XCTAssertEqual(rows.map(\.title), ["One", "Two", "Three"])
        XCTAssertFalse(rows.contains(where: \.isCurrent))
        XCTAssertEqual(Set(rows.map(\.id)).count, 3)
    }

    func testTheSameSongTwiceInUpNextGetsTwoRows() {
        let rows = DesktopPlayerModel.upNextItems([queued("Again", id: "x"), queued("Again", id: "x")], playlist: [])
        XCTAssertEqual(Set(rows.map(\.id)).count, 2)
    }

    func testAnUpNextSongCarriesTheLibraryIDOfTheSameSongInThePlaylist() {
        let playlist = [
            PlayerQueueItem(id: "q-1", title: "Two", artist: "our artist", index: 4, persistentID: "ABC"),
            PlayerQueueItem(id: "q-2", title: "Stream", artist: "Radio", index: 5),
        ]
        let rows = DesktopPlayerModel.upNextItems([queued("One"), queued("TWO", "Our Artist")], playlist: playlist)
        XCTAssertNil(rows[0].persistentID)
        XCTAssertEqual(rows[1].persistentID, "ABC")
    }

    func testAnUpNextSongWithoutALibraryIDCanStillBeClicked() {
        let row = DesktopPlayerModel.upNextItems([queued("From the catalogue")], playlist: [])[0]
        XCTAssertNil(row.persistentID)
        XCTAssertTrue(row.isPlayable)
        XCTAssertFalse(PlayerQueueItem(id: "s", title: "Stream", artist: "", index: 0).isPlayable)
    }

    func testAFreshReadIsUsedAsItIs() {
        let read = [queued("Next")]
        let playing = NowPlayingMonitor.Track(title: "Other", artist: "Someone", duration: 100, genre: nil, album: nil)
        XCTAssertEqual(DesktopPlayerModel.upNextNow(read: read, last: [queued("Old")], playing: playing), read)
    }

    func testWhileTheNewSongsReadIsOnItsWayTheLastOneMovesOnByOne() {
        let last = [queued("Started", duration: 180), queued("After it"), queued("Then this")]
        let playing = NowPlayingMonitor.Track(title: "Started", artist: "Our Artist", duration: 181, genre: nil,
                                              album: "Our Album")
        XCTAssertEqual(DesktopPlayerModel.upNextNow(read: [], last: last, playing: playing).map(\.title),
                       ["After it", "Then this"])
    }

    func testAfterSkippingSeveralTheLastReadPicksUpAfterTheSongSkippedTo() {
        let last = [queued("One"), queued("Two"), queued("Three"), queued("Four")]
        let playing = NowPlayingMonitor.Track(title: "Three", artist: "Our Artist", duration: 200, genre: nil,
                                              album: "Our Album")
        XCTAssertEqual(DesktopPlayerModel.upNextNow(read: [], last: last, playing: playing).map(\.title), ["Four"])
    }

    func testRowsKeepTheirIdsAsSongsMoveUp() {
        let before = DesktopPlayerModel.upNextItems([queued("One", id: "a"), queued("Two", id: "b"), queued("Three")],
                                                    playlist: [])
        let after = DesktopPlayerModel.upNextItems([queued("Two", id: "b"), queued("Three")], playlist: [])
        XCTAssertEqual(Array(before.dropFirst()).map(\.id), after.map(\.id))
        XCTAssertEqual(after.map(\.upNextStep), [1, 2])
    }

    func testTheLastReadIsDroppedWhenSomethingElseStarted() {
        let last = [queued("Expected"), queued("After it")]
        let playing = NowPlayingMonitor.Track(title: "Picked by hand", artist: "Someone", duration: 150, genre: nil,
                                              album: nil)
        XCTAssertEqual(DesktopPlayerModel.upNextNow(read: [], last: last, playing: playing), [])
        XCTAssertEqual(DesktopPlayerModel.upNextNow(read: [], last: last, playing: nil), [])
    }

    func testClickingAnUpNextSongSkipsForwardThenPlays() {
        let source = AppleMusicSearch.skipSource(steps: 3)
        XCTAssertTrue(source.contains("repeat 3 times"))
        XCTAssertTrue(source.contains("next track"))
        XCTAssertTrue(source.hasSuffix("play"))
        XCTAssertFalse(source.contains("play track"), "starting a song afresh would replace Up Next")
    }

    func testSkipsStayWithinWhatTheQueueLists() {
        XCTAssertTrue(AppleMusicSearch.skipSource(steps: 0).contains("repeat 1 times"))
        XCTAssertTrue(AppleMusicSearch.skipSource(steps: 500).contains("repeat \(MusicQueueReader.upNextLimit) times"))
    }

    func testTheQueueListsMoreOfUpNextThanTheLookAheadLooksUp() {
        var snapshot = [QueuedTrack(title: "Now", artist: "A", album: "", duration: 100, genre: nil, identifier: nil,
                                    isCurrent: true)]
        snapshot += (1...30).map { queued("Song \($0)") }
        let current = NowPlayingMonitor.Track(title: "Now", artist: "A", duration: 100, genre: nil, album: nil)
        let listed = MusicQueueMessage.upcoming(in: snapshot, current: current, limit: MusicQueueReader.upNextLimit)
        XCTAssertEqual(listed.count, 24)
        XCTAssertEqual(listed.first?.title, "Song 1")
        XCTAssertLessThan(MusicQueueReader.upcomingLimit, MusicQueueReader.upNextLimit)
    }
}
