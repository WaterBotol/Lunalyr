import AppKit
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

    /// The audio alignment never finishes by itself: only the time limit ends it.
    var audioHangs = false

    func audioOffset(url: URL, song: MusicVideoSong) async -> MusicVideoAutoResult {
        audioCalls.append(url)
        if audioHangs {
            while !Task.isCancelled { await Task.yield() }
            return .unavailable
        }
        return audioResults.isEmpty ? .unavailable : audioResults.removeFirst()
    }
}

/// Brief listening without any audio: records what the controller asked for.
@MainActor
private final class FakeListener: MusicVideoBriefListening {
    /// Music playing, power not spared, permission already granted.
    var mayListen = true
    private(set) var starts: [TimeInterval] = []
    private(set) var stops = 0
    /// A listen is running (started and not stopped).
    var running: Bool { starts.count > stops }

    func start(for seconds: TimeInterval) -> Bool {
        XCTAssertTrue(mayListen, "never started without permission")
        starts.append(seconds)
        return true
    }

    func stop() { stops += 1 }
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
                            offsets: MusicVideoOffsets? = nil, listener: FakeListener? = nil,
                            songClock: @escaping @MainActor () -> (time: Double, playing: Bool) = { (0, true) },
                            lyrics: @escaping @MainActor (MusicVideoSong) -> [MusicVideoLyricLine]? = { _ in nil },
                            uptime: @escaping () -> TimeInterval = { 0 },
                            sleep: @escaping (TimeInterval) async -> Void = { _ in }) -> MusicVideoController {
        let controller = MusicVideoController(resolver: resolver, offsets: offsets ?? MusicVideoOffsets(defaults: defaults),
                                              autoSync: autoSync, listener: listener, songClock: songClock,
                                              lyrics: lyrics, uptime: uptime, now: { [start] in start }, sleep: sleep)
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

    // MARK: Brief listening

    private let libraryURL = URL(fileURLWithPath: "/Music/Media/Music Videos/Blinding Lights.m4v")

    func testALibraryVideoWithoutAnOffsetListensBrieflyOnceAndStops() async {
        let autoSync = FakeAutoSync()
        autoSync.audioResults = [.found(10, .audio)]
        let listener = FakeListener()
        let controller = controller(resolver(library: NoLibrary(video: libraryURL)), autoSync, listener: listener)
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        XCTAssertEqual(listener.starts, [MusicVideoController.listenLimit], "up to about 25 s")
        XCTAssertEqual(listener.stops, 1, "stopped again on success")
        XCTAssertFalse(controller.listening)
        XCTAssertEqual(autoSync.audioCalls, [libraryURL], "no refine pass after a brief listen")
        XCTAssertEqual(controller.currentOffset, 10)
        XCTAssertEqual(controller.offsetOrigin, .audio)
    }

    func testABriefListenStopsAtTheTimeLimit() async {
        let autoSync = FakeAutoSync()
        autoSync.audioHangs = true
        let listener = FakeListener()
        var slept: [TimeInterval] = []
        let controller = controller(resolver(library: NoLibrary(video: libraryURL)), autoSync, listener: listener,
                                    sleep: { slept.append($0) })
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        XCTAssertTrue(slept.contains(MusicVideoController.listenLimit))
        XCTAssertEqual(listener.starts.count, 1)
        XCTAssertFalse(listener.running, "capture is stopped at the time limit")
        XCTAssertFalse(controller.listening)
        XCTAssertFalse(controller.syncing)
        XCTAssertEqual(controller.offsetOrigin, .none)
    }

    func testABriefListenHappensOncePerSongAndVideo() async {
        let autoSync = FakeAutoSync()
        let listener = FakeListener()
        let controller = controller(resolver(library: NoLibrary(video: libraryURL)), autoSync, listener: listener)
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        XCTAssertEqual(listener.starts.count, 1)
        // Nothing usable was found; the song plays again.
        controller.update(track: nil, enabled: true, online: false)
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        XCTAssertEqual(listener.starts.count, 1, "not again for the same song and video")
        XCTAssertFalse(listener.running)
        // Re-sync is the user asking: it may listen again.
        controller.resync()
        await controller.settle()
        XCTAssertEqual(listener.starts.count, 2)
        XCTAssertFalse(listener.running)
    }

    func testNoBriefListenWithoutPermissionOrWithAnOffset() async {
        let autoSync = FakeAutoSync()
        let listener = FakeListener()
        listener.mayListen = false
        let controller = controller(resolver(library: NoLibrary(video: libraryURL)), autoSync, listener: listener)
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        XCTAssertTrue(listener.starts.isEmpty, "no permission (or nothing playing): no capture")
        XCTAssertFalse(controller.listening)

        let suite = "LunalyrTests.MusicVideoSync.\(UUID().uuidString)"
        addTeardownBlock { UserDefaults(suiteName: suite)?.removePersistentDomain(forName: suite) }
        let offsets = MusicVideoOffsets(defaults: UserDefaults(suiteName: suite)!)
        offsets.set(4, for: song.key)
        let allowed = FakeListener()
        let manual = self.controller(resolver(library: NoLibrary(video: libraryURL)), FakeAutoSync(), offsets: offsets,
                                     listener: allowed)
        manual.update(track: track, enabled: true, online: false)
        await manual.settle()
        XCTAssertTrue(allowed.starts.isEmpty, "the user's offset needs no listening")

        let youTube = resolver()
        youTube.links.set("dQw4w9WgXcQ", for: song.key)
        let other = FakeListener()
        let video = self.controller(youTube, FakeAutoSync(), listener: other)
        video.update(track: track, enabled: true, online: true)
        await video.settle()
        XCTAssertTrue(other.starts.isEmpty, "YouTube videos are never listened to")
    }

    func testTimingLabelStates() {
        XCTAssertEqual(MusicVideoController.timingLabel(origin: .none, syncing: true, listening: true),
                       "Listening to line up the video…")
        XCTAssertEqual(MusicVideoController.timingLabel(origin: .estimated, syncing: true, listening: true),
                       "Listening to line up the video…")
        XCTAssertEqual(MusicVideoController.timingLabel(origin: .estimated, syncing: true), "Estimated; syncing…")
        XCTAssertEqual(MusicVideoController.timingLabel(origin: .none, syncing: true), "Syncing…")
        XCTAssertEqual(MusicVideoController.timingLabel(origin: .audio, syncing: false), "Synced automatically")
        XCTAssertEqual(MusicVideoController.timingLabel(origin: .manual, syncing: false, listening: true), "Offset set by you")
        XCTAssertNil(MusicVideoController.timingLabel(origin: .none, syncing: false))
    }

    // MARK: Tap to sync

    private let sungLines = [MusicVideoLyricLine(start: 15, text: "I've been tryna call"),
                             MusicVideoLyricLine(start: 20, text: "I've been on my own for long enough"),
                             MusicVideoLyricLine(start: 58, text: "I said, ooh, I'm blinded by the lights"),
                             MusicVideoLyricLine(start: 62.5, text: "No, I can't sleep until I feel your touch"),
                             MusicVideoLyricLine(start: 67, text: "I said, ooh, I'm drowning in the night")]

    func testTapLineChoiceAndMaths() {
        XCTAssertEqual(MusicVideoTapSync.reactionTime, 0.2)
        XCTAssertEqual(MusicVideoTapSync.line(in: sungLines, reference: 30, firstLine: true)?.start, 15,
                       "armed before the singing: the first line, wherever the video is")
        XCTAssertEqual(MusicVideoTapSync.line(in: sungLines, reference: 3, firstLine: false)?.start, 15)
        XCTAssertEqual(MusicVideoTapSync.line(in: sungLines, reference: 62.7, firstLine: false)?.start, 62.5, "just passed")
        XCTAssertEqual(MusicVideoTapSync.line(in: sungLines, reference: 61, firstLine: false)?.start, 62.5, "upcoming")
        XCTAssertEqual(MusicVideoTapSync.line(in: sungLines, reference: 59, firstLine: false)?.start, 58)
        XCTAssertEqual(MusicVideoTapSync.line(in: sungLines, reference: 300, firstLine: false)?.start, 67)
        XCTAssertNil(MusicVideoTapSync.line(in: [], reference: 3, firstLine: true))
        XCTAssertTrue(MusicVideoTapSync.armedBeforeFirstLine(songTime: 2, lines: sungLines))
        XCTAssertFalse(MusicVideoTapSync.armedBeforeFirstLine(songTime: 16, lines: sungLines))
        XCTAssertEqual(MusicVideoTapSync.offset(videoTimeAtTap: 23.6, lineStart: 15), 8.4, accuracy: 1e-9,
                       "the reaction time is taken off the tap")
        XCTAssertEqual(MusicVideoTapSync.averaged([8.4, 8.6]), 8.5)
        XCTAssertNil(MusicVideoTapSync.averaged([]))
        XCTAssertEqual(MusicVideoTapSync.describe(8.4), "Video starts 8.4 s ahead")
        XCTAssertEqual(MusicVideoTapSync.describe(-2), "Video starts 2.0 s behind")
        XCTAssertEqual(MusicVideoTapSync.describe(0.01), "Video starts with the song")
    }

    func testTapToSyncSetsTheUsersOffsetAndASecondTapRefinesIt() async {
        let resolver = resolver()
        resolver.links.set("dQw4w9WgXcQ", for: song.key)
        var songTime = 2.0, clock: TimeInterval = 100
        let lines = sungLines
        let controller = controller(resolver, FakeAutoSync(), songClock: { (songTime, true) },
                                    lyrics: { _ in lines }, uptime: { clock })
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        XCTAssertTrue(controller.canTapSync)
        XCTAssertEqual(MusicVideoTapSync.buttonTitle(controller.tapState), "Tap to sync")

        // Armed during the intro: the tap marks where the singing starts.
        controller.tapSyncButton()
        XCTAssertEqual(controller.tapState, .armed(firstLine: true))
        XCTAssertEqual(MusicVideoTapSync.buttonTitle(controller.tapState), "Tap now")
        XCTAssertEqual(MusicVideoTapSync.note(controller.tapState),
                       "Watch the video. Press any key, or Tap now, the moment the singing starts in the video. Esc cancels.")
        // The singing starts 23.4 s into the video; the tap lands 0.2 s later.
        songTime = 23.6
        controller.noteVideoSample(time: 23.5, playing: true, of: .youTube("dQw4w9WgXcQ"))
        clock += 0.1
        controller.tapSyncButton()
        XCTAssertEqual(controller.currentOffset, 8.4, accuracy: 1e-9)
        XCTAssertEqual(controller.offsetOrigin, .manual, "the tap beats anything automatic")
        XCTAssertEqual(MusicVideoController.timingLabel(origin: controller.offsetOrigin, syncing: false), "Offset set by you")
        XCTAssertEqual(controller.tapState, .done(offset: 8.4, taps: 1, firstLine: true))
        XCTAssertEqual(MusicVideoTapSync.note(controller.tapState),
                       "Video starts 8.4 s ahead, from where the singing starts. Tap again to refine.")
        XCTAssertEqual(MusicVideoTapSync.buttonTitle(controller.tapState), "Tap again")

        // Later in the song: the next line sung in the video. The video now
        // runs 8.4 s ahead; that line really starts 8.6 s in.
        songTime = 60
        controller.tapSyncButton()
        XCTAssertEqual(controller.tapState, .armed(firstLine: false))
        XCTAssertEqual(MusicVideoTapSync.note(controller.tapState),
                       "Watch the video. Press any key, or Tap now, when the next line is sung in the video. Esc cancels.")
        clock += 10
        songTime = 62.9
        controller.noteVideoSample(time: 71.3, playing: true, of: .youTube("dQw4w9WgXcQ"))
        controller.tap()
        XCTAssertEqual(controller.currentOffset, 8.5, accuracy: 1e-9, "the two taps are averaged")
        XCTAssertEqual(controller.tapState, .done(offset: 8.5, taps: 2, firstLine: false))
        XCTAssertEqual(MusicVideoTapSync.note(controller.tapState),
                       "Video starts 8.5 s ahead, from the next line sung in the video. Averaged over 2 taps.")

        // A nudge starts the taps over.
        controller.nudgeOffset(by: 0.5)
        XCTAssertEqual(controller.tapState, .idle)
        XCTAssertEqual(controller.currentOffset, 9.0, accuracy: 1e-9)
    }

    func testTapWithoutAVideoSampleUsesTheSyncedPosition() async {
        let resolver = resolver()
        resolver.links.set("dQw4w9WgXcQ", for: song.key)
        var songTime = 1.0
        let lines = sungLines
        let controller = controller(resolver, FakeAutoSync(), songClock: { (songTime, true) }, lyrics: { _ in lines })
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        controller.beginTapSync()
        // With no offset yet the video is kept at the song's own time.
        songTime = 23.6
        controller.tap()
        XCTAssertEqual(controller.currentOffset, 8.4, accuracy: 1e-9)
    }

    func testTapToSyncNeedsLyricsPlaybackAndAVideo() async {
        let resolver = resolver()
        resolver.links.set("dQw4w9WgXcQ", for: song.key)
        var lines: [MusicVideoLyricLine]? = nil
        var playing = true
        let controller = controller(resolver, FakeAutoSync(), songClock: { (5, playing) }, lyrics: { _ in lines })
        XCTAssertFalse(controller.canTapSync, "nothing showing")
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        controller.beginTapSync()
        XCTAssertEqual(controller.tapState, .failed(.noLyrics))
        XCTAssertEqual(MusicVideoTapSync.note(controller.tapState),
                       "Tap to sync needs this song’s timed lyrics, and they haven’t loaded.")
        lines = sungLines
        playing = false
        controller.beginTapSync()
        XCTAssertEqual(controller.tapState, .failed(.notPlaying))
        playing = true
        controller.beginTapSync()
        controller.cancelTapSync()
        XCTAssertEqual(controller.tapState, .idle)
        XCTAssertFalse(controller.offsets.hasManual(song.key), "cancelled: nothing set")
        controller.beginTapSync()
        controller.update(track: nil, enabled: true, online: false)
        XCTAssertEqual(controller.tapState, .idle, "a song change ends it")
        XCTAssertFalse(controller.canTapSync)
    }

    func testOnlyPlainKeysOutsideTextInputCountAsTheTap() {
        func action(_ code: UInt16, _ modifiers: NSEvent.ModifierFlags = [], repeat isRepeat: Bool = false,
                    typing: Bool = false) -> MusicVideoTapSync.KeyAction {
            MusicVideoTapSync.keyAction(keyCode: code, modifiers: modifiers, isRepeat: isRepeat, textInputFocused: typing)
        }
        XCTAssertEqual(action(49), .tap, "space")
        XCTAssertEqual(action(0, .shift), .tap, "a shifted letter is still a plain key")
        XCTAssertEqual(action(126, .function), .tap, "an arrow key")
        XCTAssertEqual(action(53), .cancel, "Esc")
        XCTAssertEqual(action(49, typing: true), .pass, "typing in a text field")
        XCTAssertEqual(action(53, typing: true), .pass)
        XCTAssertEqual(action(9, .command), .pass, "Command-V")
        XCTAssertEqual(action(8, .control), .pass)
        XCTAssertEqual(action(0, .option), .pass)
        XCTAssertEqual(action(55, .command), .pass, "Command alone")
        XCTAssertEqual(action(56, .shift), .pass, "Shift alone")
        XCTAssertEqual(action(49, repeat: true), .pass, "a held key")
    }

    func testAMissedBriefListenIsTriedOnceWhenPlaybackResumes() async {
        let autoSync = FakeAutoSync()
        let listener = FakeListener()
        listener.mayListen = false
        let controller = controller(resolver(library: NoLibrary(video: libraryURL)), autoSync, listener: listener)
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        XCTAssertTrue(listener.starts.isEmpty, "paused when the video started")
        controller.playbackChanged(playing: false)
        listener.mayListen = true
        controller.playbackChanged(playing: true)
        await controller.settle()
        XCTAssertEqual(listener.starts.count, 1, "tried on resume")
        XCTAssertFalse(listener.running)
        controller.playbackChanged(playing: false)
        controller.playbackChanged(playing: true)
        await controller.settle()
        XCTAssertEqual(listener.starts.count, 1, "once")

        // Still not allowed on resume: not tried on every later resume.
        let blocked = FakeListener()
        blocked.mayListen = false
        let other = self.controller(resolver(library: NoLibrary(video: libraryURL)), FakeAutoSync(), listener: blocked)
        other.update(track: track, enabled: true, online: false)
        await other.settle()
        other.playbackChanged(playing: true)
        await other.settle()
        blocked.mayListen = true
        other.playbackChanged(playing: true)
        await other.settle()
        XCTAssertTrue(blocked.starts.isEmpty, "the resume try is spent")

        // An offset found meanwhile needs no listening.
        let found = FakeListener()
        found.mayListen = false
        let suite = "LunalyrTests.MusicVideoSync.\(UUID().uuidString)"
        addTeardownBlock { UserDefaults(suiteName: suite)?.removePersistentDomain(forName: suite) }
        let offsets = MusicVideoOffsets(defaults: UserDefaults(suiteName: suite)!)
        let third = self.controller(resolver(library: NoLibrary(video: libraryURL)), FakeAutoSync(), offsets: offsets,
                                    listener: found)
        third.update(track: track, enabled: true, online: false)
        await third.settle()
        third.nudgeOffset(by: 2)
        found.mayListen = true
        third.playbackChanged(playing: true)
        await third.settle()
        XCTAssertTrue(found.starts.isEmpty)
    }
}
