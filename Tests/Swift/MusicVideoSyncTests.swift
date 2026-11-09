import XCTest
@testable import VersoCustom

// The music video's automatic offset and drift correction. Every seam is
// faked: no network, no audio, no player, no real preferences.

private final class SyncHTTP: MusicVideoHTTP, @unchecked Sendable {
    struct Reply { var status: Int; var body: Data }
    /// Replies by URL path ("/watch", "/api/timedtext").
    var byPath: [String: Reply] = [:]
    private(set) var urls: [URL] = []

    func get(_ url: URL) async throws -> (Data, HTTPURLResponse) {
        urls.append(url)
        let reply = byPath[url.path] ?? Reply(status: 404, body: Data())
        return (reply.body, HTTPURLResponse(url: url, statusCode: reply.status, httpVersion: nil, headerFields: [:])!)
    }
}

private struct NoLibrary: MusicVideoLibrary {
    var video: URL?
    func localVideo(for song: MusicVideoSong) async -> URL? { video }
}

private struct Playable: MusicVideoPlayability {
    func check(_ url: URL) async -> MusicVideoPlayabilityResult { .playable }
}

@MainActor
private final class FakeAutoSync: MusicVideoAutoSyncing {
    var captionResults: [MusicVideoAutoResult] = []
    var audioResults: [MusicVideoAutoResult] = []
    private(set) var captionCalls: [String] = []
    private(set) var audioCalls: [URL] = []

    func captionOffset(videoID: String, song: MusicVideoSong) async -> MusicVideoAutoResult {
        captionCalls.append(videoID)
        return captionResults.isEmpty ? .unavailable : captionResults.removeFirst()
    }

    func audioOffset(url: URL, song: MusicVideoSong) async -> MusicVideoAutoResult {
        audioCalls.append(url)
        return audioResults.isEmpty ? .unavailable : audioResults.removeFirst()
    }
}

/// A small deterministic generator, so the synthetic signals repeat.
private struct Seeded: RandomNumberGenerator {
    var state: UInt64
    mutating func next() -> UInt64 {
        state = state &* 6364136223846793005 &+ 1442695040888963407
        return state
    }
}

private enum CaptionFixtures {
    static let watchPage = #"""
    <html><script>var ytInitialPlayerResponse = {"videoDetails":{"videoId":"dQw4w9WgXcQ","lengthSeconds":"245","title":"A } tricky \" title {"},"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[{"baseUrl":"https://www.youtube.com/api/timedtext?v=dQw4w9WgXcQ&lang=fr","name":{"simpleText":"French"},"languageCode":"fr"},{"baseUrl":"https://www.youtube.com/api/timedtext?v=dQw4w9WgXcQ&kind=asr&lang=en","name":{"simpleText":"English (auto-generated)"},"languageCode":"en","kind":"asr"},{"baseUrl":"https://www.youtube.com/api/timedtext?v=dQw4w9WgXcQ&lang=en&fmt=srv3","name":{"simpleText":"English"},"languageCode":"en"},{"baseUrl":"https://evil.example/api/timedtext?lang=en","name":{"simpleText":"Elsewhere"},"languageCode":"en"}]}}};var meta = document.createElement('meta');</script></html>
    """#

    static let json3 = #"""
    {"wireMagic":"pb3","events":[
     {"tStartMs":0,"dDurationMs":12000,"id":1,"wpWinPosId":1,"wsWinStyleId":1},
     {"tStartMs":12300,"dDurationMs":2000,"segs":[{"utf8":"♪ Walking down the empty road ♪"}]},
     {"tStartMs":14500,"dDurationMs":10,"aAppend":1,"segs":[{"utf8":"\n"}]},
     {"tStartMs":15100,"dDurationMs":2500,"wWinId":1,"segs":[{"utf8":"lights"},{"utf8":" are","tOffsetMs":300},{"utf8":" burning","tOffsetMs":600}]}
    ]}
    """#

    static let manualVTT = """
    WEBVTT
    Kind: captions
    Language: en

    00:00:12.300 --> 00:00:14.500
    Walking down the empty road

    00:15.100 --> 00:17.600 align:start position:0%
    <i>Lights are burning</i>
    """

    /// YouTube's automatic WebVTT: each cue repeats the line before,
    /// untimed, above the new one with word timings.
    static let automaticVTT = """
    WEBVTT
    Kind: captions
    Language: en

    00:00:12.300 --> 00:00:15.090 align:start position:0%

    walking<00:00:12.700><c> down</c><00:00:13.000><c> the</c><00:00:13.200><c> empty</c><00:00:13.600><c> road</c>

    00:00:15.090 --> 00:00:15.100 align:start position:0%
    walking down the empty road


    00:00:15.100 --> 00:00:17.600 align:start position:0%
    walking down the empty road
    lights<00:00:15.400><c> are</c><00:00:15.700><c> burning</c>
    """
}

@MainActor
final class MusicVideoSyncTests: XCTestCase {
    private var defaults: UserDefaults!

    override func setUp() async throws {
        let name = "LunalyrTests.MusicVideoSync.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: name)!
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
    }

    private let song = MusicVideoSong(title: "Blinding Lights", artist: "The Weeknd", album: "After Hours", duration: 200)
    private var track: NowPlayingMonitor.Track {
        NowPlayingMonitor.Track(title: "Blinding Lights", artist: "The Weeknd", duration: 200, album: "After Hours")
    }
    private let start = Date(timeIntervalSince1970: 1_800_000_000)

    private func resolver(http: SyncHTTP = SyncHTTP(), library: NoLibrary = NoLibrary()) -> MusicVideoResolver {
        var time: TimeInterval = 0
        return MusicVideoResolver(library: library, playability: Playable(), http: http,
                                  cache: MusicVideoCache(defaults: defaults), links: MusicVideoLinks(defaults: defaults),
                                  now: { [start] in start }, uptime: { time }, sleep: { time += $0 })
    }

    // MARK: Caption tracks

    func testWatchPageListsTrustedCaptionTracksAndTheLength() throws {
        let page = try XCTUnwrap(YouTubeCaptions.page(fromHTML: CaptionFixtures.watchPage))
        XCTAssertEqual(page.duration, 245)
        XCTAssertEqual(page.tracks.map(\.languageCode), ["fr", "en", "en"], "a track hosted elsewhere is dropped")
        XCTAssertEqual(page.tracks.map(\.automatic), [false, true, false])
        let url = try XCTUnwrap(YouTubeCaptions.textURL(for: page.tracks[2]))
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        XCTAssertEqual(items.filter { $0.name == "fmt" }.map(\.value), ["json3"], "one fmt, json3")
        XCTAssertNil(YouTubeCaptions.page(fromHTML: "<html>no player</html>"))
        XCTAssertNil(YouTubeCaptions.trustedTimedTextURL("http://www.youtube.com/api/timedtext?v=x"))
        XCTAssertNil(YouTubeCaptions.trustedTimedTextURL("https://www.youtube.com/redirect?q=x"))
    }

    func testWrittenCaptionsInTheSungLanguageComeFirst() throws {
        let tracks = try XCTUnwrap(YouTubeCaptions.page(fromHTML: CaptionFixtures.watchPage)).tracks
        let english = YouTubeCaptions.preferred(tracks, lyricsLanguage: "en")
        XCTAssertEqual(english.map(\.automatic), [false, true], "written English, then automatic English")
        XCTAssertEqual(english.first?.languageCode, "en")
        let french = YouTubeCaptions.preferred(tracks, lyricsLanguage: "fr-FR")
        XCTAssertEqual(french.first?.languageCode, "fr")
        let automaticOnly = tracks.filter(\.automatic)
        XCTAssertEqual(YouTubeCaptions.preferred(automaticOnly, lyricsLanguage: "en").count, 1,
                       "automatic captions are used when they are all there is")
        XCTAssertEqual(YouTubeCaptions.preferred(tracks, lyricsLanguage: nil).first?.automatic, false)
    }

    func testJSON3CuesSkipEmptyAndNewlineEvents() {
        let cues = YouTubeCaptions.cues(from: Data(CaptionFixtures.json3.utf8))
        XCTAssertEqual(cues, [MusicVideoCaptionCue(start: 12.3, text: "♪ Walking down the empty road ♪"),
                              MusicVideoCaptionCue(start: 15.1, text: "lights are burning")])
    }

    func testVTTCuesFromWrittenAndAutomaticTracks() {
        XCTAssertEqual(YouTubeCaptions.cues(from: Data(CaptionFixtures.manualVTT.utf8)),
                       [MusicVideoCaptionCue(start: 12.3, text: "Walking down the empty road"),
                        MusicVideoCaptionCue(start: 15.1, text: "Lights are burning")])
        XCTAssertEqual(YouTubeCaptions.cues(fromVTT: CaptionFixtures.automaticVTT),
                       [MusicVideoCaptionCue(start: 12.3, text: "walking down the empty road"),
                        MusicVideoCaptionCue(start: 15.1, text: "lights are burning")],
                       "rolling repeats are kept once")
        XCTAssertEqual(YouTubeCaptions.vttTime("01:02:03.500"), 3723.5)
        XCTAssertEqual(YouTubeCaptions.vttTime("02:03,25 "), 123.25)
        XCTAssertNil(YouTubeCaptions.vttTime("soon"))
    }

    func testAnEmptyBodyGivesNoCues() {
        // What /api/timedtext answers today without a proof-of-origin token.
        XCTAssertTrue(YouTubeCaptions.cues(from: Data()).isEmpty)
        XCTAssertTrue(YouTubeCaptions.cues(from: Data("  \n".utf8)).isEmpty)
        XCTAssertTrue(YouTubeCaptions.cues(from: Data("<html>".utf8)).isEmpty)
    }

    // MARK: Caption alignment

    private let verses = [
        "Walking down the empty road tonight", "Every window shining like a star",
        "I can hear the city calling out my name", "Running till the morning finds us here",
        "Hold me close and never let me go", "Burning brighter than the sun above",
        "We were young and we were never afraid", "Dancing slowly in the falling rain",
        "Tell me now if you remember me", "Somewhere far beyond the silver sea",
    ]
    private let chorus = ["Oh the lights are burning in my heart", "Say my name and I will follow you"]

    /// Verse lines, the chorus three times, 4 s apart from 20 s.
    private var lyricLines: [MusicVideoLyricLine] {
        let texts = Array(verses[0..<4]) + chorus + Array(verses[4..<8]) + chorus + Array(verses[8...]) + chorus
        return texts.enumerated().map { MusicVideoLyricLine(start: 20 + Double($0.offset) * 4, text: $0.element) }
    }

    func testCaptionsAlignDespiteRepeatsOutliersAndNoise() throws {
        var random = Seeded(state: 7)
        var cues = lyricLines.map { line in
            MusicVideoCaptionCue(start: line.start + 12.3 + Double.random(in: -0.2...0.2, using: &random),
                                 text: "♪ " + line.text.uppercased() + "! ♪")
        }
        // Captions that are not lyrics, and two cues timed badly.
        cues.insert(MusicVideoCaptionCue(start: 2, text: "[Music]"), at: 0)
        cues.insert(MusicVideoCaptionCue(start: 5, text: "(door slams) Where were you last night?"), at: 1)
        cues[6].start += 9
        cues[9].start -= 14
        let result = try XCTUnwrap(MusicVideoCaptionAlign.offset(cues: cues, lines: lyricLines))
        XCTAssertEqual(result.offset, 12.3, accuracy: 0.15)
        XCTAssertGreaterThanOrEqual(result.matched, 14)
        XCTAssertLessThan(result.spread, 0.2)
    }

    func testCaptionAlignmentRefusesTooFewOrScatteredMatches() {
        let few = lyricLines.prefix(4).map { MusicVideoCaptionCue(start: $0.start + 3, text: $0.text) }
        XCTAssertNil(MusicVideoCaptionAlign.offset(cues: Array(few), lines: lyricLines), "too few matched lines")
        var random = Seeded(state: 3)
        let verseLines = lyricLines.filter { !chorus.contains($0.text) }
        let scattered = verseLines.map {
            MusicVideoCaptionCue(start: $0.start + 3 + Double.random(in: -6...6, using: &random), text: $0.text)
        }
        XCTAssertNil(MusicVideoCaptionAlign.offset(cues: scattered, lines: verseLines), "too much spread")
        let translated = lyricLines.map { MusicVideoCaptionCue(start: $0.start + 3, text: "Une autre chanson, vraiment différente") }
        XCTAssertNil(MusicVideoCaptionAlign.offset(cues: translated, lines: lyricLines), "captions that are not the lyrics")
    }

    func testDurationEstimateOnlyForAPlausibleIntro() {
        XCTAssertEqual(MusicVideoDurationEstimate.intro(videoDuration: 215.04, songDuration: 200), 15)
        XCTAssertEqual(MusicVideoDurationEstimate.intro(videoDuration: 202, songDuration: 200), 2)
        XCTAssertNil(MusicVideoDurationEstimate.intro(videoDuration: 201, songDuration: 200), "too close to say")
        XCTAssertNil(MusicVideoDurationEstimate.intro(videoDuration: 260, songDuration: 200), "a different edit")
        XCTAssertNil(MusicVideoDurationEstimate.intro(videoDuration: 190, songDuration: 200))
        XCTAssertNil(MusicVideoDurationEstimate.intro(videoDuration: nil, songDuration: 200))
    }

    // MARK: Captions through the resolver

    func testCaptionFetchSendsOnlyTheVideoToYouTubeThroughTheRateLimit() async throws {
        let http = SyncHTTP()
        http.byPath["/watch"] = .init(status: 200, body: Data(CaptionFixtures.watchPage.utf8))
        http.byPath["/api/timedtext"] = .init(status: 200, body: Data(CaptionFixtures.json3.utf8))
        let resolver = resolver(http: http)
        let fetched = await resolver.captionCues(videoID: "dQw4w9WgXcQ", lyricsLanguage: "en")
        guard case .tracks(let tracks, let duration) = fetched else { return XCTFail("tracks") }
        XCTAssertEqual(tracks.count, 2, "at most two tracks are fetched")
        XCTAssertEqual(duration, 245)
        XCTAssertEqual(http.urls.count, 3)
        XCTAssertTrue(http.urls.allSatisfy { $0.host == "www.youtube.com" })
        XCTAssertEqual(resolver.rateLimit.sent.count, 3, "each request counts against the limit")
        XCTAssertFalse(http.urls.contains { $0.absoluteString.contains("Blinding") }, "no song details sent")
    }

    func testEmptyCaptionBodiesAreNoCaptionsAndRateLimitsArePassing() async {
        let http = SyncHTTP()
        http.byPath["/watch"] = .init(status: 200, body: Data(CaptionFixtures.watchPage.utf8))
        http.byPath["/api/timedtext"] = .init(status: 200, body: Data())
        let fetched = await resolver(http: http).captionCues(videoID: "dQw4w9WgXcQ", lyricsLanguage: "en")
        XCTAssertEqual(fetched, .tracks([], duration: 245))
        let limited = SyncHTTP()
        limited.byPath["/watch"] = .init(status: 429, body: Data())
        let refused = await resolver(http: limited).captionCues(videoID: "dQw4w9WgXcQ", lyricsLanguage: "en")
        XCTAssertEqual(refused, .unavailable)
        let changed = SyncHTTP()
        changed.byPath["/watch"] = .init(status: 200, body: Data("<html>consent</html>".utf8))
        let unreadable = await resolver(http: changed).captionCues(videoID: "dQw4w9WgXcQ", lyricsLanguage: "en")
        XCTAssertEqual(unreadable, .unavailable)
    }

    // MARK: Audio alignment

    /// A song-like onset track: hits at random times and strengths.
    private func syntheticOnsets(seconds: Double, seed: UInt64) -> [Float] {
        var random = Seeded(state: seed)
        let count = Int(seconds * MusicVideoAudioAlign.rate)
        return (0..<count).map { _ in Double.random(in: 0...1, using: &random) > 0.9 ? Float.random(in: 0.2...1, using: &random) : 0 }
    }

    func testCrossCorrelationFindsAKnownLag() throws {
        let rate = MusicVideoAudioAlign.rate
        let video = syntheticOnsets(seconds: 240, seed: 11)
        // The song at 50…75 s is the video at 57.4…82.4 s, with noise.
        let offset = 7.4, songStart = 50.0
        var random = Seeded(state: 5)
        let begin = Int((songStart + offset) * rate)
        let live = video[begin..<(begin + Int(25 * rate))].map { $0 * 0.8 + Float.random(in: 0...0.15, using: &random) }
        let result = try XCTUnwrap(MusicVideoAudioAlign.align(live: Array(live), liveStart: songStart, video: video))
        XCTAssertEqual(result.offset, offset, accuracy: 0.03)
        XCTAssertGreaterThan(result.peak, 0.5)
        // A negative lag (the video starts later in the song) too.
        let earlier = Int((songStart - 20) * rate)
        let late = Array(video[earlier..<(earlier + Int(25 * rate))])
        XCTAssertEqual(try XCTUnwrap(MusicVideoAudioAlign.align(live: late, liveStart: songStart, video: video)).offset,
                       -20, accuracy: 0.03)
    }

    func testCrossCorrelationRefusesAnUnclearPeak() {
        let video = syntheticOnsets(seconds: 240, seed: 11)
        let unrelated = syntheticOnsets(seconds: 25, seed: 99)
        XCTAssertNil(MusicVideoAudioAlign.align(live: unrelated, liveStart: 50, video: video), "another song")
        // A bare metronome matches every beat equally well.
        let beat = (0..<Int(240 * MusicVideoAudioAlign.rate)).map { $0 % 25 == 0 ? Float(1) : 0 }
        XCTAssertNil(MusicVideoAudioAlign.align(live: Array(beat[2500..<3750]), liveStart: 50, video: beat))
        XCTAssertNil(MusicVideoAudioAlign.align(live: Array(video[0..<200]), liveStart: 50, video: video), "too short")
    }

    func testLiveWindowTakesTheUnbrokenNewestStretch() throws {
        let recorder = AudioEnvelopeRecorder()
        // 40 s of a 1 kHz-sampled signal at 30 s of song, in 20 ms passes.
        var samples = [Float](repeating: 0, count: 20)
        for pass in 0..<2000 {
            for i in samples.indices { samples[i] = pass % 10 == 0 ? 0.5 : 0.01 }
            samples.withUnsafeBufferPointer {
                recorder.append($0.baseAddress!, count: 20, sampleRate: 1000, now: 100 + Double(pass + 1) * 0.02)
            }
        }
        let frames = recorder.snapshot()
        XCTAssertEqual(frames.count, AudioEnvelopeRecorder.capacity, "bounded")
        XCTAssertEqual(frames.last?.time ?? 0, 140, accuracy: 0.001)
        // Now is 140, the song is at 70 s, so it started at 70.
        let window = try XCTUnwrap(MusicVideoAudioAlign.liveWindow(frames, now: 140, songTimeNow: 70, songStartedAt: 70))
        XCTAssertEqual(window.onsets.count, Int(30 * MusicVideoAudioAlign.rate))
        XCTAssertEqual(window.start, 40, accuracy: 0.03)
        // The song began 10 s ago: not enough heard yet.
        XCTAssertNil(MusicVideoAudioAlign.liveWindow(frames, now: 140, songTimeNow: 10, songStartedAt: 130))
        // A gap in capture keeps only what came after it.
        var gapped = frames
        gapped.removeSubrange(1000..<1100)
        XCTAssertNil(MusicVideoAudioAlign.liveWindow(gapped, now: 140, songTimeNow: 70, songStartedAt: 70),
                     "only 10 s after the gap")
    }

    // MARK: Offsets: manual beats automatic, cached per video

    func testManualOffsetAlwaysBeatsTheAutomaticOne() {
        let offsets = MusicVideoOffsets(defaults: defaults)
        let video = "yt:dQw4w9WgXcQ"
        offsets.setEstimate(15, for: song.key, video: video)
        XCTAssertEqual(offsets.offset(for: song.key, video: video, now: start), 15)
        XCTAssertEqual(offsets.origin(for: song.key, video: video, now: start), .estimated)
        offsets.storeAutomatic(12.34, origin: .captions, for: song.key, video: video, now: start)
        XCTAssertEqual(offsets.offset(for: song.key, video: video, now: start), 12.3, "found beats the estimate")
        XCTAssertEqual(offsets.origin(for: song.key, video: video, now: start), .captions)
        offsets.nudge(by: 0.5, for: song.key, video: video, now: start)
        XCTAssertEqual(offsets.offset(for: song.key, video: video, now: start), 12.8, "nudged from the automatic one")
        XCTAssertEqual(offsets.origin(for: song.key, video: video, now: start), .manual)
        offsets.storeAutomatic(30, origin: .captions, for: song.key, video: video, now: start)
        XCTAssertEqual(offsets.offset(for: song.key, video: video, now: start), 12.8, "a later automatic one never wins")
        offsets.set(0, for: song.key)
        XCTAssertEqual(offsets.origin(for: song.key, video: video, now: start), .manual, "a nudge to 0 is kept")
        offsets.clearManual(song.key)
        XCTAssertEqual(offsets.offset(for: song.key, video: video, now: start), 30)
    }

    func testAutomaticOffsetsAreCachedPerSongAndVideo() {
        let offsets = MusicVideoOffsets(defaults: defaults)
        offsets.storeAutomatic(8, origin: .audio, for: song.key, video: "file:/a.m4v", now: start)
        offsets.setEstimate(3, for: song.key, video: "yt:BBBBBBBBBBB")
        let reloaded = MusicVideoOffsets(defaults: defaults)
        XCTAssertEqual(reloaded.offset(for: song.key, video: "file:/a.m4v", now: start), 8, "kept on this Mac")
        XCTAssertEqual(reloaded.origin(for: song.key, video: "file:/a.m4v", now: start), .audio)
        XCTAssertEqual(reloaded.offset(for: song.key, video: "yt:BBBBBBBBBBB", now: start), 0,
                       "another video's offset is not used, and estimates are not stored")
        let later = start.addingTimeInterval(MusicVideoOffsets.automaticLifetime + 1)
        XCTAssertNil(reloaded.automaticEntry(for: song.key, video: "file:/a.m4v", now: later), "expires")
        reloaded.storeAutomatic(nil, origin: .captions, for: song.key, video: "yt:BBBBBBBBBBB", now: start)
        XCTAssertNotNil(reloaded.automaticEntry(for: song.key, video: "yt:BBBBBBBBBBB", now: start),
                        "nothing usable is remembered too")
        XCTAssertEqual(reloaded.origin(for: song.key, video: "yt:BBBBBBBBBBB", now: start), .none)
        XCTAssertNil(reloaded.automaticEntry(for: song.key, video: "yt:BBBBBBBBBBB",
                                             now: start.addingTimeInterval(MusicVideoOffsets.unusableLifetime + 1)))
    }

    // MARK: Controller

    private func controller(_ resolver: MusicVideoResolver, _ autoSync: FakeAutoSync,
                            offsets: MusicVideoOffsets? = nil) -> MusicVideoController {
        let controller = MusicVideoController(resolver: resolver, offsets: offsets ?? MusicVideoOffsets(defaults: defaults),
                                              autoSync: autoSync, now: { [start] in start }, sleep: { _ in })
        controller.refineDelay = 0
        return controller
    }

    func testControllerFindsTheOffsetOnceAndCachesIt() async {
        let resolver = resolver()
        resolver.links.set("dQw4w9WgXcQ", for: song.key)
        let autoSync = FakeAutoSync()
        autoSync.captionResults = [.found(12.3, .captions)]
        let first = controller(resolver, autoSync)
        first.update(track: track, enabled: true, online: true)
        await first.settle()
        XCTAssertEqual(autoSync.captionCalls, ["dQw4w9WgXcQ"])
        XCTAssertEqual(first.currentOffset, 12.3)
        XCTAssertEqual(first.offsetOrigin, .captions)
        XCTAssertEqual(MusicVideoController.timingLabel(origin: first.offsetOrigin, syncing: first.syncing),
                       "Synced automatically")
        let second = controller(resolver, autoSync)
        second.update(track: track, enabled: true, online: true)
        await second.settle()
        XCTAssertEqual(autoSync.captionCalls.count, 1, "found once per video")
        XCTAssertEqual(second.currentOffset, 12.3)
    }

    func testNudgeWinsAndResyncLooksAgain() async {
        let resolver = resolver()
        resolver.links.set("dQw4w9WgXcQ", for: song.key)
        let autoSync = FakeAutoSync()
        autoSync.captionResults = [.found(12.3, .captions), .found(11.9, .captions)]
        let controller = controller(resolver, autoSync)
        controller.update(track: track, enabled: true, online: true)
        await controller.settle()
        controller.nudgeOffset(by: -0.5)
        XCTAssertEqual(controller.currentOffset, 11.8)
        XCTAssertEqual(MusicVideoController.timingLabel(origin: controller.offsetOrigin, syncing: false), "Offset set by you")
        controller.resync()
        XCTAssertTrue(controller.syncing)
        await controller.settle()
        XCTAssertEqual(autoSync.captionCalls.count, 2)
        XCTAssertEqual(controller.currentOffset, 11.9)
        XCTAssertEqual(controller.offsetOrigin, .captions)
    }

    func testNoCaptionsFallsBackToTheEstimateAndIsRemembered() async {
        let resolver = resolver()
        resolver.links.set("dQw4w9WgXcQ", for: song.key)
        let autoSync = FakeAutoSync()
        autoSync.captionResults = [.unusable(.captions)]
        let controller = controller(resolver, autoSync)
        controller.update(track: track, enabled: true, online: true)
        await controller.settle()
        XCTAssertEqual(controller.currentOffset, 0)
        controller.noteVideoDuration(214, of: .youTube("dQw4w9WgXcQ"))
        XCTAssertEqual(controller.currentOffset, 14)
        XCTAssertEqual(MusicVideoController.timingLabel(origin: controller.offsetOrigin, syncing: false), "Estimated")
        controller.noteVideoDuration(500, of: .youTube("OTHEROTHER1"))
        XCTAssertEqual(controller.currentOffset, 14, "only the video on screen counts")
        let again = self.controller(resolver, autoSync)
        again.update(track: track, enabled: true, online: true)
        await again.settle()
        XCTAssertEqual(autoSync.captionCalls.count, 1, "a video without usable captions is not asked again soon")
    }

    func testCaptionsAreOnlyFetchedWithOnlineLookupsOn() async {
        let resolver = resolver()
        resolver.links.set("dQw4w9WgXcQ", for: song.key)
        let autoSync = FakeAutoSync()
        let controller = controller(resolver, autoSync)
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        XCTAssertNotNil(controller.source(for: track), "the pasted video plays")
        XCTAssertTrue(autoSync.captionCalls.isEmpty)
        XCTAssertFalse(controller.syncing)
    }

    func testLibraryVideoIsAlignedByAudioAndRefinedOnce() async {
        let url = URL(fileURLWithPath: "/Music/Media/Music Videos/Blinding Lights.m4v")
        let autoSync = FakeAutoSync()
        autoSync.audioResults = [.found(10, .audio), .found(10.4, .audio)]
        let controller = controller(resolver(library: NoLibrary(video: url)), autoSync)
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        XCTAssertEqual(autoSync.audioCalls, [url, url], "found, then refined once")
        XCTAssertEqual(controller.currentOffset, 10.4)
        XCTAssertEqual(controller.offsetOrigin, .audio)

        let suite = "LunalyrTests.MusicVideoSync.\(UUID().uuidString)"
        addTeardownBlock { UserDefaults(suiteName: suite)?.removePersistentDomain(forName: suite) }
        let far = FakeAutoSync()
        far.audioResults = [.found(10, .audio), .found(40, .audio)]
        let other = self.controller(resolver(library: NoLibrary(video: url)), far,
                                    offsets: MusicVideoOffsets(defaults: UserDefaults(suiteName: suite)!))
        other.update(track: track, enabled: true, online: false)
        await other.settle()
        XCTAssertEqual(other.currentOffset, 10, "a refinement somewhere else entirely is not taken")
    }

    func testAudioUnavailableIsNotRemembered() async {
        let url = URL(fileURLWithPath: "/Music/Media/Music Videos/Blinding Lights.m4v")
        let autoSync = FakeAutoSync()
        let controller = controller(resolver(library: NoLibrary(video: url)), autoSync)
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        XCTAssertEqual(controller.offsetOrigin, .none)
        XCTAssertNil(MusicVideoOffsets(defaults: defaults).automaticEntry(for: song.key, video: "file:\(url.path)", now: start))
    }
}
