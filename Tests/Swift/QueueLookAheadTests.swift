import XCTest
@testable import VersoCustom

// The queue look-ahead: the helper's JSON lines, which songs are looked up
// ahead, cancelling on a queue change, and that what was found is used when
// the song starts. Every seam is faked: nothing here starts perl, reaches
// the network or reads the real preferences.

private func queued(_ title: String, duration: Double = 200, album: String = "Album", current: Bool = false) -> QueuedTrack {
    QueuedTrack(title: title, artist: "Our Artist", album: album, duration: duration, genre: nil, identifier: nil,
                isCurrent: current)
}

private func playing(_ title: String, duration: Double = 200, album: String? = "Album") -> NowPlayingMonitor.Track {
    NowPlayingMonitor.Track(title: title, artist: "Our Artist", duration: duration, genre: nil, album: album)
}

final class MusicQueueMessageTests: XCTestCase {
    func testQueueLineParses() throws {
        let line = #"{"type":"queue","v":1,"items":[{"title":"Now","artist":"A","album":"B","duration":191.471,"id":"1::2","genre":"Pop","current":true},{"title":"Next","artist":"A","album":"","duration":180.4,"current":false}]}"#
        guard case .queue(let items)? = MusicQueueMessage.parse(line) else { return XCTFail("not a queue") }
        XCTAssertEqual(items.count, 2)
        XCTAssertEqual(items[0].title, "Now")
        XCTAssertEqual(items[0].duration, 191.471, accuracy: 0.0001)
        XCTAssertEqual(items[0].identifier, "1::2")
        XCTAssertEqual(items[0].genre, "Pop")
        XCTAssertTrue(items[0].isCurrent)
        XCTAssertFalse(items[1].isCurrent)
        XCTAssertNil(items[1].query.album, "an empty album is no album")
    }

    func testOtherLinesParseOrAreIgnored() {
        XCTAssertEqual(MusicQueueMessage.parse(#"{"type":"ready","v":1}"#), .ready)
        XCTAssertEqual(MusicQueueMessage.parse(#"{"type":"error","code":"timeout"}"#), .error("timeout"))
        XCTAssertNil(MusicQueueMessage.parse(""))
        XCTAssertNil(MusicQueueMessage.parse("Use of uninitialized value at -e line 1."))
        XCTAssertNil(MusicQueueMessage.parse(#"{"type":"queue","v":2,"items":[]}"#), "another version is not guessed at")
        XCTAssertNil(MusicQueueMessage.parse(#"{"type":"queue","v":1,"items":[{"title":"Cut"#))
        XCTAssertNil(MusicQueueMessage.parse(#"{"type":"something"}"#))
    }

    func testOddValuesBecomeSafeOnes() {
        let line = #"{"type":"queue","v":1,"items":[{"title":7,"duration":-3},{"duration":"long"},"junk"]}"#
        guard case .queue(let items)? = MusicQueueMessage.parse(line) else { return XCTFail("not a queue") }
        XCTAssertEqual(items.count, 2)
        XCTAssertEqual(items[0].title, "")
        XCTAssertEqual(items[0].duration, 0)
        XCTAssertFalse(items[0].isLookable)
    }

    func testLinesArriveInPieces() {
        var buffer = LineBuffer()
        XCTAssertEqual(buffer.append(Data(#"{"type":"rea"#.utf8)), [])
        XCTAssertEqual(buffer.append(Data("dy\",\"v\":1}\n{\"type\":\"error\",\"code\":\"x\"}\n{".utf8)),
                       [#"{"type":"ready","v":1}"#, #"{"type":"error","code":"x"}"#])
        XCTAssertEqual(buffer.append(Data("}\n".utf8)), ["{}"])
    }

    func testUpcomingFollowsTheSongPlaying() {
        let snapshot = [queued("Now", duration: 191.5, current: true), queued("Next"), queued("After")]
        // MediaRemote's duration is a second or two off AppleScript's.
        let upcoming = MusicQueueMessage.upcoming(in: snapshot, current: playing("Now", duration: 189.7))
        XCTAssertEqual(upcoming.map(\.title), ["Next", "After"])
        XCTAssertEqual(MusicQueueMessage.upcoming(in: snapshot, current: playing("Next")), [],
                       "a snapshot about another song is not used")
        XCTAssertEqual(MusicQueueMessage.upcoming(in: snapshot, current: nil), [])
        XCTAssertEqual(MusicQueueMessage.upcoming(in: [], current: playing("Now")), [])
        XCTAssertEqual(MusicQueueMessage.upcoming(in: snapshot, current: playing("Now", duration: 191.5), limit: 1).map(\.title), ["Next"])
    }

    func testMatchingAllowsOnlyASmallDurationDifference() {
        let track = queued("Song", duration: 191.471)
        XCTAssertTrue(track.matches(playing("song", duration: 189.737)))
        XCTAssertFalse(track.matches(playing("Song", duration: 200)))
        XCTAssertFalse(track.matches(playing("Song", album: "Other")))
        XCTAssertNotEqual(track.recordingKey, LyricQuery(title: "Song", artist: "Our Artist", album: "Album", duration: 189.737).recordingKey,
                          "why what was looked up ahead has to be carried across")
    }

    func testBackoffGrowsAndIsCapped() {
        XCTAssertEqual(MusicQueueReader.pause(after: 0), 0)
        XCTAssertEqual(MusicQueueReader.pause(after: 1), MusicQueueReader.firstPause)
        XCTAssertEqual(MusicQueueReader.pause(after: 2), MusicQueueReader.firstPause * 2)
        XCTAssertEqual(MusicQueueReader.pause(after: 40), MusicQueueReader.longestPause)
    }

    @MainActor
    func testReaderStartsNothingInTestsAndStaysEmpty() {
        let reader = MusicQueueReader.shared
        reader.setActive(true)
        reader.songChanged(to: playing("Now"))
        XCTAssertFalse(reader.isRunning, "the test host never starts perl")
        XCTAssertEqual(reader.upcoming, [])
        reader.setActive(false)
    }
}

/// The reader's process handling, with /bin/sh standing in for perl.
@MainActor
final class MusicQueueReaderProcessTests: XCTestCase {
    private func reader(_ script: String) -> MusicQueueReader {
        let command = MusicQueueReader.Command(executable: URL(fileURLWithPath: "/bin/sh")) { mode in
            ["-c", script, "sh", mode == .watch ? "watch" : "once"]
        }
        let reader = MusicQueueReader(command: command, runsInTests: true)
        reader.answerTimeout = 0.5
        reader.onceTimeout = 0.5
        reader.firstPause = 0.1
        return reader
    }

    private func until(_ condition: () -> Bool, seconds: Double = 5, file: StaticString = #filePath, line: UInt = #line) async {
        for _ in 0..<Int(seconds * 50) where !condition() { try? await Task.sleep(for: .milliseconds(20)) }
        XCTAssertTrue(condition(), "timed out", file: file, line: line)
    }

    private static let queueLine = #"{"type":"queue","v":1,"items":[{"title":"Now","artist":"Our Artist","album":"Album","duration":201.5,"current":true},{"title":"Next","artist":"Our Artist","album":"Album","duration":180}]}"#

    func testAWatcherThatAnswersFillsUpcomingAndStopsWithIt() async {
        // Prints the queue, then waits on stdin as the helper does.
        let reader = reader("printf '%s\\n' '\(Self.queueLine)'; cat >/dev/null")
        reader.songChanged(to: playing("Now", duration: 200))
        reader.setActive(true)
        await until { reader.upcoming.map(\.title) == ["Next"] }
        XCTAssertTrue(reader.isRunning)
        XCTAssertEqual(reader.mode, .watch)
        XCTAssertEqual(reader.failureCount, 0)
        reader.songChanged(to: playing("Next", duration: 180))
        XCTAssertEqual(reader.upcoming, [], "another song's queue is not used until it is read again")
        reader.setActive(false)
        XCTAssertFalse(reader.isRunning)
    }

    func testAHelperThatKeepsFailingFallsBackAndLeavesUpcomingEmpty() async {
        let reader = reader("exit 3")
        reader.songChanged(to: playing("Now"))
        reader.setActive(true)
        await until { reader.mode == .once }
        XCTAssertGreaterThanOrEqual(reader.failureCount, MusicQueueReader.watchFailuresAllowed)
        XCTAssertEqual(reader.upcoming, [])
        reader.setActive(false)
        XCTAssertFalse(reader.isRunning)
    }

    func testASilentHelperIsStoppedAndRetried() async {
        let reader = reader("cat >/dev/null")
        reader.setActive(true)
        await until { reader.failureCount >= 1 }
        XCTAssertEqual(reader.upcoming, [])
        reader.setActive(false)
    }

    func testAHelperThatSaysTheQueueIsEmptyIsNotAFailure() async {
        let reader = reader(#"printf '%s\n' '{"type":"error","code":"empty"}'; cat >/dev/null"#)
        reader.songChanged(to: playing("Now"))
        reader.setActive(true)
        try? await Task.sleep(for: .milliseconds(900))
        XCTAssertEqual(reader.failureCount, 0)
        XCTAssertTrue(reader.isRunning)
        XCTAssertEqual(reader.upcoming, [])
        reader.setActive(false)
    }
}

final class LookAheadPlannerTests: XCTestCase {
    private let current = playing("Now")
    private let queue = [queued("A"), queued("B"), queued("C"), queued("D")]

    func testFirstThreeSongsAfterThePlayingOne() {
        let plan = LookAheadPlanner.plan(upcoming: queue, current: current, conditions: .init(videos: true), done: [:])
        XCTAssertEqual(plan.map(\.track.title), ["A", "B", "C"])
        XCTAssertEqual(plan.map(\.video), [true, true, false], "videos for the first two only")
        XCTAssertTrue(plan.allSatisfy { $0.lyrics && $0.cover })
    }

    func testSkipsThePlayingSongRepeatsUnlookablesAndDone() {
        let upcoming = [queued("Now", duration: 201), queued("A"), queued("A"), queued("", duration: 100),
                        queued("Stream", duration: 0), queued("B"), queued("C")]
        let plan = LookAheadPlanner.plan(upcoming: upcoming, current: current, conditions: .init(),
                                         done: [queued("B").recordingKey: LookAheadParts(lyrics: true, video: true, cover: true)])
        XCTAssertEqual(plan.map(\.track.title), ["A", "C"], "B is done; the window does not slide past it")
        XCTAssertTrue(plan.allSatisfy { !$0.video }, "no videos unless the music-video wallpaper searches online")
    }

    func testEasesOffOrStops() {
        let battery = LookAheadPlanner.plan(upcoming: queue, current: current, conditions: .init(onBattery: true, videos: true), done: [:])
        XCTAssertEqual(battery.map(\.track.title), ["A"])
        XCTAssertEqual(battery.first?.cover, false)
        for conditions in [LookAheadConditions(enabled: false), .init(followsMusic: false), .init(online: false),
                           .init(lowPowerMode: true)] {
            XCTAssertEqual(LookAheadPlanner.plan(upcoming: queue, current: current, conditions: conditions, done: [:]), [])
        }
        XCTAssertEqual(LookAheadPlanner.plan(upcoming: queue, current: nil, conditions: .init(), done: [:]), [])
    }

    func testASongMovingUpGetsWhatItsNewPlaceAdds() {
        // C was third (no video); the queue moves on and it is second.
        let done = [queued("B").recordingKey: LookAheadParts(lyrics: true, video: true, cover: true),
                    queued("C").recordingKey: LookAheadParts(lyrics: true, video: false, cover: true)]
        let plan = LookAheadPlanner.plan(upcoming: [queued("B"), queued("C"), queued("D")], current: playing("A"),
                                         conditions: .init(videos: true), done: done)
        XCTAssertEqual(plan, [LookAheadItem(track: queued("C"), lyrics: false, video: true, cover: false),
                              LookAheadItem(track: queued("D"), lyrics: true, video: false, cover: true)])
    }
}

@MainActor
final class QueueLookAheadTests: XCTestCase {
    private final class Log {
        var started: [String] = []
        var cancelled: [String] = []
        var finished: [String] = []
        var videos: [String] = []
        var covers: [String] = []
        /// Whether the video lookup finds room in the rate limit.
        var videoAnswers = true
    }

    private func until(_ condition: () -> Bool, file: StaticString = #filePath, line: UInt = #line) async {
        for _ in 0..<300 where !condition() { try? await Task.sleep(for: .milliseconds(10)) }
        XCTAssertTrue(condition(), "timed out", file: file, line: line)
    }

    private func lookAhead(_ log: Log, lyricsTake: Duration = .zero) -> QueueLookAhead {
        let work = QueueLookAhead.Work(
            lyrics: { track in
                log.started.append(track.title)
                if lyricsTake > .zero { try? await Task.sleep(for: lyricsTake) }
                if Task.isCancelled { log.cancelled.append(track.title) } else { log.finished.append(track.title) }
            },
            video: { track in log.videos.append(track.title); return log.videoAnswers },
            cover: { track in log.covers.append(track.title) })
        let ahead = QueueLookAhead(work: work)
        ahead.settleDelay = 0
        return ahead
    }

    func testLooksUpEachSongOnceAndRemembersIt() async {
        let log = Log(), ahead = lookAhead(log)
        let queue = [queued("A", duration: 191.5), queued("B"), queued("C")]
        ahead.update(upcoming: queue, current: playing("Now"), conditions: .init(videos: true))
        await until { log.finished.count == 3 }
        XCTAssertEqual(log.started, ["A", "B", "C"], "one at a time, in queue order")
        XCTAssertEqual(log.videos, ["A", "B"])
        XCTAssertEqual(log.covers, ["A", "B", "C"])
        // The same queue again asks nothing more.
        ahead.update(upcoming: queue, current: playing("Now"), conditions: .init(videos: true))
        try? await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(log.started.count, 3)
        // A starts: its details from the player differ a little.
        XCTAssertEqual(ahead.lookedUp(playing("A", duration: 189.7))?.duration, 191.5)
        XCTAssertNil(ahead.lookedUp(playing("Elsewhere")))
    }

    func testQueueChangeCancelsWorkForSongsNoLongerNext() async {
        let log = Log(), ahead = lookAhead(log, lyricsTake: .seconds(5))
        ahead.update(upcoming: [queued("A"), queued("B")], current: playing("Now"), conditions: .init())
        await until { log.started == ["A"] }
        XCTAssertEqual(ahead.working?.title, "A")
        ahead.update(upcoming: [queued("D"), queued("E")], current: playing("Now"), conditions: .init())
        await until { log.started.contains("D") }
        XCTAssertEqual(log.cancelled, ["A"])
        XCTAssertNil(ahead.lookedUp(playing("A")), "cancelled work is not taken as done")
        // Going offline stops it too.
        ahead.update(upcoming: [queued("D"), queued("E")], current: playing("Now"), conditions: .init(online: false))
        await until { log.cancelled == ["A", "D"] }
        XCTAssertEqual(ahead.plan, [])
    }

    func testTheSongUnderWayKeepsGoingWhenStillFirst() async {
        let log = Log(), ahead = lookAhead(log, lyricsTake: .milliseconds(300))
        ahead.update(upcoming: [queued("A"), queued("B")], current: playing("Now"), conditions: .init())
        await until { log.started == ["A"] }
        ahead.update(upcoming: [queued("A"), queued("C")], current: playing("Now"), conditions: .init())
        await until { log.finished.contains("C") }
        XCTAssertEqual(log.cancelled, [])
        XCTAssertEqual(log.started, ["A", "C"])
    }

    func testTheQueueMovingOnLooksUpTheNewSecondSongsVideo() async {
        let log = Log(), ahead = lookAhead(log)
        ahead.update(upcoming: [queued("A"), queued("B"), queued("C")], current: playing("Now"), conditions: .init(videos: true))
        await until { log.finished.count == 3 }
        XCTAssertEqual(log.videos, ["A", "B"])
        ahead.update(upcoming: [queued("B"), queued("C"), queued("D")], current: playing("A"), conditions: .init(videos: true))
        await until { log.finished.contains("D") && log.videos.contains("C") }
        XCTAssertEqual(log.videos, ["A", "B", "C"])
        XCTAssertEqual(log.started, ["A", "B", "C", "D"], "lyrics are not looked up twice")
    }

    func testAVideoWithoutRoomIsTriedAgainLaterWithoutHoldingUpTheRest() async {
        let log = Log(), ahead = lookAhead(log)
        log.videoAnswers = false
        let queue = [queued("A"), queued("B")]
        ahead.update(upcoming: queue, current: playing("Now"), conditions: .init(videos: true))
        await until { log.finished == ["A", "B"] && log.videos == ["A", "B"] }
        log.videoAnswers = true
        ahead.update(upcoming: queue, current: playing("Now"), conditions: .init(videos: true))
        await until { log.videos == ["A", "B", "A", "B"] }
        try? await Task.sleep(for: .milliseconds(50))
        ahead.update(upcoming: queue, current: playing("Now"), conditions: .init(videos: true))
        try? await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(log.videos.count, 4, "found videos are not looked up again")
        XCTAssertEqual(log.started, ["A", "B"])
    }

    func testPlansChangingDuringTheSettleStartEachSongOnce() async {
        let log = Log(), ahead = lookAhead(log)
        ahead.settleDelay = 0.3
        ahead.update(upcoming: [queued("A"), queued("B")], current: playing("Now"), conditions: .init())
        ahead.update(upcoming: [queued("C"), queued("A")], current: playing("Now"), conditions: .init())
        ahead.update(upcoming: [queued("C"), queued("A"), queued("B")], current: playing("Now"), conditions: .init())
        await until { log.finished.count == 3 }
        try? await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(log.started, ["C", "A", "B"])
        // And a run cancelled while waiting leaves its successor in charge.
        ahead.update(upcoming: [queued("D")], current: playing("C"), conditions: .init())
        ahead.update(upcoming: [], current: playing("C"), conditions: .init())
        ahead.update(upcoming: [queued("E")], current: playing("C"), conditions: .init())
        await until { log.finished.contains("E") }
        try? await Task.sleep(for: .milliseconds(400))
        XCTAssertEqual(log.started, ["C", "A", "B", "E"])
    }

    func testWaitsForThePlayingSongsOwnLookups() async {
        let log = Log(), ahead = lookAhead(log)
        ahead.settleDelay = 0.3
        ahead.update(upcoming: [queued("A")], current: playing("Now"), conditions: .init())
        try? await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(log.started, [])
        await until { log.finished == ["A"] }
    }

    func testNothingHappensWhenItCannot() async {
        let log = Log(), ahead = lookAhead(log)
        ahead.update(upcoming: [], current: playing("Now"), conditions: .init())
        ahead.update(upcoming: [queued("A")], current: playing("Now"), conditions: .init(lowPowerMode: true))
        try? await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(log.started, [])
        XCTAssertNil(ahead.lookedUp(playing("A")))
    }
}

// MARK: - The song start uses what was found

private final class AheadHTTP: MusicVideoHTTP, @unchecked Sendable {
    var page = Data()
    private(set) var urls: [URL] = []
    func get(_ url: URL) async throws -> (Data, HTTPURLResponse) {
        urls.append(url)
        let ok = url.host == "www.youtube.com"
        return (ok ? page : Data(), HTTPURLResponse(url: url, statusCode: ok ? 200 : 404, httpVersion: nil, headerFields: nil)!)
    }
}

private final class AheadLibrary: MusicVideoLibrary, @unchecked Sendable {
    var video: URL?
    func localVideo(for song: MusicVideoSong) async -> URL? { video }
}

private struct AheadPlayable: MusicVideoPlayability {
    func check(_ url: URL) async -> MusicVideoPlayabilityResult { .playable }
}

@MainActor
final class LookAheadWarmTests: XCTestCase {
    private var defaults: UserDefaults!
    private var folder: URL!

    override func setUp() async throws {
        let name = "LunalyrTests.LookAhead.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: name)!
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("LookAheadTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: folder)
    }

    private static let page = Data("""
    <html><script>var ytInitialData = {"contents":{"twoColumnSearchResultsRenderer":{"primaryContents":
    {"sectionListRenderer":{"contents":[{"itemSectionRenderer":{"contents":[{"videoRenderer":{"videoId":"4NRXx6U8ABQ",
    "title":{"runs":[{"text":"The Weeknd - Blinding Lights (Official Video)"}]},"ownerText":{"runs":[{"text":"The Weeknd"}]}}}]}}]}}}}};</script></html>
    """.utf8)

    private func resolver(_ http: AheadHTTP, library: AheadLibrary = AheadLibrary()) -> MusicVideoResolver {
        var time: TimeInterval = 1000
        return MusicVideoResolver(library: library, playability: AheadPlayable(), http: http,
                                  cache: MusicVideoCache(defaults: defaults), links: MusicVideoLinks(defaults: defaults),
                                  uptime: { time }, sleep: { time += $0 })
    }

    func testVideoFoundAheadIsUsedWithoutAnotherSearch() async {
        let http = AheadHTTP()
        http.page = Self.page
        let resolver = resolver(http)
        let ahead = MusicVideoSong(title: "Blinding Lights", artist: "The Weeknd", album: "After Hours", duration: 201.6)
        let found = await resolver.lookAhead(ahead)
        XCTAssertEqual(found, .found(.youTube("4NRXx6U8ABQ")))
        let sent = http.urls.count
        XCTAssertGreaterThan(sent, 0)
        // Music reports the song a little shorter once it plays.
        let song = MusicVideoSong(title: "Blinding Lights", artist: "The Weeknd", album: "After Hours", duration: 199.9)
        XCTAssertNotEqual(song.key, ahead.key)
        resolver.adopt(from: ahead, to: song)
        let result = await resolver.resolve(song, online: true)
        XCTAssertEqual(result, .found(.youTube("4NRXx6U8ABQ")))
        XCTAssertEqual(http.urls.count, sent, "the song start searched nothing")
        // Asked again ahead, nothing more is sent either.
        _ = await resolver.lookAhead(ahead)
        XCTAssertEqual(http.urls.count, sent)
    }

    func testLookAheadLeavesTheRateLimitRoomAndSkipsLibraryVideos() async {
        let http = AheadHTTP()
        http.page = Self.page
        let library = AheadLibrary()
        library.video = URL(fileURLWithPath: "/Music/Video.m4v")
        let song = MusicVideoSong(title: "Blinding Lights", artist: "The Weeknd", album: nil, duration: 200)
        let withLibrary = await resolver(http, library: library).lookAhead(song)
        XCTAssertEqual(withLibrary, .found(.local(URL(fileURLWithPath: "/Music/Video.m4v"))))
        XCTAssertTrue(http.urls.isEmpty, "a video in the library needs no search")
        let busy = MusicVideoResolver(library: AheadLibrary(), playability: AheadPlayable(), http: http,
                                      cache: MusicVideoCache(defaults: defaults), links: MusicVideoLinks(defaults: defaults),
                                      rateLimit: {
                                          var limit = MusicVideoRateLimit(minimumGap: 0)
                                          limit.record(at: 995)
                                          return limit
                                      }(), uptime: { 1000 })
        XCTAssertFalse(busy.hasLookAheadHeadroom)
        let skipped = await busy.lookAhead(song)
        XCTAssertNil(skipped)
        XCTAssertTrue(http.urls.isEmpty, "the playing song's slots are left alone")
    }

    func testAdoptingWithNothingFoundChangesNothing() async {
        let http = AheadHTTP()
        let resolver = resolver(http)
        let a = MusicVideoSong(title: "One", artist: "X", album: nil, duration: 201.6)
        let b = MusicVideoSong(title: "One", artist: "X", album: nil, duration: 199.9)
        resolver.adopt(from: a, to: b)
        XCTAssertEqual(resolver.cache.count, 0)
    }

    private let options = LyricSourceOptions.Snapshot(enabled: [.lrclib], mode: .smart, localFolder: "", localVersion: "")
    private let fast = LyricsFetcher.Timing(grace: 0.2, upgradeSettle: 0.1, appleRetry: 0.3, lrclibLimit: 3, sourceLimit: 3)

    private func candidate() -> LyricCandidate {
        let provenance = LyricsFetcher.Provenance(precision: "fixture", source: LyricSourceID.lrclib.title)
        let line = LyricsFetcher.makeLine(text: "Sing along", start: 1, end: 3)
        return .init(source: .lrclib, result: .synced([line], provenance), title: "Our Song", artist: "Our Artist",
                     duration: 190, score: 0)
    }

    func testLyricsFoundAheadAreUsedWithoutAskingAgain() async {
        final class Count: @unchecked Sendable { var calls = 0 }
        let count = Count()
        let found = candidate()
        let fetcher = LyricsFetcher(cacheFolder: folder, timing: fast, isOnline: { true }) { _, _, _ in
            count.calls += 1
            return found
        }
        let ahead = LyricQuery(title: "Our Song", artist: "Our Artist", album: "Album", duration: 191.5)
        let playing = LyricQuery(title: "Our Song", artist: "Our Artist", album: "Album", duration: 189.7)
        let first = await fetcher.lookup(ahead, options: options)
        XCTAssertNotNil(first.result)
        XCTAssertEqual(count.calls, 1)
        fetcher.adopt(from: ahead, to: playing, fingerprint: options.fingerprint, musixmatch: MusixmatchState(file: nil))
        let report = await fetcher.lookup(playing, options: options)
        XCTAssertNotNil(report.result)
        XCTAssertTrue(report.cached)
        XCTAssertEqual(count.calls, 1, "the song start asked no lyric source")
    }

    func testWithoutALookAheadTheSongIsLookedUpAsBefore() async {
        final class Count: @unchecked Sendable { var calls = 0 }
        let count = Count()
        let found = candidate()
        let fetcher = LyricsFetcher(cacheFolder: folder, timing: fast, isOnline: { true }) { _, _, _ in
            count.calls += 1
            return found
        }
        let ahead = LyricQuery(title: "Our Song", artist: "Our Artist", album: "Album", duration: 191.5)
        let playing = LyricQuery(title: "Our Song", artist: "Our Artist", album: "Album", duration: 189.7)
        fetcher.adopt(from: ahead, to: playing, fingerprint: options.fingerprint, musixmatch: MusixmatchState(file: nil))
        let report = await fetcher.lookup(playing, options: options)
        XCTAssertNotNil(report.result)
        XCTAssertFalse(report.cached)
        XCTAssertEqual(count.calls, 1)
    }
}
