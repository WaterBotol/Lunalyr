import XCTest
@testable import VersoCustom

// Syncing a YouTube video from its captions: the timed-text formats, the
// matching of captions to lyric lines (with speech-recognition mistakes),
// when the offset is trusted, a video edited differently partway, which
// captured replies are believed, and the user's own timing winning. No
// player, no network, no real preferences.

private struct NoVideoLibrary: MusicVideoLibrary {
    func localVideo(for song: MusicVideoSong) async -> URL? { nil }
}

private struct AlwaysPlayable: MusicVideoPlayability {
    func check(_ url: URL) async -> MusicVideoPlayabilityResult { .playable }
}

private final class NoHTTP: MusicVideoHTTP, @unchecked Sendable {
    func get(_ url: URL) async throws -> (Data, HTTPURLResponse) {
        (Data(), HTTPURLResponse(url: url, statusCode: 404, httpVersion: nil, headerFields: [:])!)
    }
}

@MainActor
final class MusicVideoCaptionSyncTests: XCTestCase {
    typealias Sync = MusicVideoCaptionSync

    // MARK: Fixtures

    /// Eight distinct lines, four seconds apart from 20 s.
    private let lyrics: [MusicVideoLyricLine] = [
        "What's wrong, you've been askin'",
        "But I don't have an answer",
        "How come, I'm still thinkin'",
        "Let's pretend to fall asleep now",
        "When we get old, will we regret this?",
        "Too young to think about all that",
        "And stalling only goes so far",
        "When you've got a head start",
    ].enumerated().map { MusicVideoLyricLine(start: 20 + Double($0.offset) * 4, text: $0.element) }

    /// The lyric lines as recognition would time them, `offset` later,
    /// each word 0.3 s apart, with `jitter(i)` added to line i.
    private func asrCues(_ lines: [MusicVideoLyricLine], offset: Double,
                         jitter: (Int) -> Double = { _ in 0 },
                         text: (Int, String) -> String = { $1 }) -> [MusicVideoCaptionCue] {
        lines.enumerated().map { index, line in
            let start = line.start + offset + jitter(index)
            let words = text(index, line.text).split(separator: " ").enumerated().map {
                MusicVideoCaptionCue.Word(start: start + Double($0.offset) * 0.3, text: String($0.element))
            }
            return MusicVideoCaptionCue(start: start, end: start + 3, text: text(index, line.text), words: words)
        }
    }

    // MARK: Parsing

    func testJSON3GivesCuesWithWordTimesAndSkipsNewlineEvents() throws {
        let json = """
        {"wireMagic":"pb3","events":[
          {"tStartMs":0,"dDurationMs":204720,"id":1,"wpWinPosId":1},
          {"tStartMs":43580,"dDurationMs":5200,"wWinId":1,"segs":[{"utf8":"what's","acAsrConf":0},{"utf8":" wrong","tOffsetMs":1000}]},
          {"tStartMs":46069,"dDurationMs":2711,"wWinId":1,"aAppend":1,"segs":[{"utf8":"\\n"}]},
          {"tStartMs":69620,"dDurationMs":8160,"wWinId":1,"segs":[{"utf8":"[Music]"}]},
          {"tStartMs":98446,"dDurationMs":4000,"segs":[{"utf8":"So can I call you tonight?"}]}
        ]}
        """
        let cues = Sync.cues(from: Data(json.utf8))
        XCTAssertEqual(cues.count, 3)
        XCTAssertEqual(cues[0].start, 43.58, accuracy: 1e-9)
        XCTAssertEqual(cues[0].end, 48.78, accuracy: 1e-9)
        XCTAssertEqual(cues[0].text, "what's wrong")
        XCTAssertEqual(cues[0].words?.map(\.text), ["what's", "wrong"])
        XCTAssertEqual(try XCTUnwrap(cues[0].words?.last?.start), 44.58, accuracy: 1e-9)
        XCTAssertEqual(cues[1].text, "[Music]")
        XCTAssertNil(cues[2].words, "an uploader's cue times only its start")
    }

    func testSrv3AndTheOlderXMLFormatParse() {
        let srv3 = """
        <?xml version="1.0" encoding="utf-8" ?><timedtext format="3"><body>
        <p t="1200" d="2000"><s>don&#39;t</s><s t="400"> stop</s></p>
        <p t="5000" d="1500">Line two</p>
        </body></timedtext>
        """
        let cues = Sync.cues(from: Data(srv3.utf8))
        XCTAssertEqual(cues.count, 2)
        XCTAssertEqual(cues[0].text, "don't stop")
        XCTAssertEqual(cues[0].words?.map(\.start), [1.2, 1.6])
        XCTAssertEqual(cues[1].start, 5)
        let older = """
        <?xml version="1.0" encoding="utf-8" ?><transcript><text start="3.5" dur="2.25">Hello &amp; goodbye</text></transcript>
        """
        let old = Sync.cues(from: Data(older.utf8))
        XCTAssertEqual(old, [MusicVideoCaptionCue(start: 3.5, end: 5.75, text: "Hello & goodbye")])
        XCTAssertTrue(Sync.cues(from: Data("not captions".utf8)).isEmpty)
        XCTAssertTrue(Sync.cues(from: Data()).isEmpty)
    }

    // MARK: Words

    func testNormalisingIgnoresCasePunctuationAccentsAndAsides() {
        XCTAssertEqual(Sync.normalize("Café, CAFÉ! (oh-oh) [Music] ♪ Don’t"), "cafe cafe dont")
        XCTAssertTrue(Sync.sameWord("askin", "asking"))
        XCTAssertTrue(Sync.sameWord("thinking", "thinkin"))
        XCTAssertTrue(Sync.sameWord("stalling", "stallin"))
        XCTAssertTrue(Sync.sameWord("regret", "regrett"))
        XCTAssertFalse(Sync.sameWord("get", "got"), "short words must match exactly")
        XCTAssertEqual(Sync.tokens("夜に駆ける"), ["夜", "に", "駆", "け", "る"])
    }

    func testMusicAndOtherNoiseCuesAreNotWords() {
        let cues = [MusicVideoCaptionCue(start: 1, end: 9, text: "[Music]"),
                    MusicVideoCaptionCue(start: 9, end: 10, text: "♪"),
                    MusicVideoCaptionCue(start: 10, end: 12, text: "foreign"),
                    MusicVideoCaptionCue(start: 12, end: 14, text: "[Applause]"),
                    MusicVideoCaptionCue(start: 14, end: 16, text: "♪ hello there ♪")]
        XCTAssertEqual(Sync.captionWords(cues).map(\.token), ["hello", "there"])
    }

    func testAWordRecognitionCouldNotTimeIsNotExact() {
        let cue = MusicVideoCaptionCue(start: 49.46, end: 54, text: "how come I'm still", words: [
            .init(start: 49.46, text: "how"), .init(start: 50.46, text: "come"),
            .init(start: 51.2, text: "I'm"), .init(start: 52.2, text: "still")])
        XCTAssertEqual(Sync.captionWords([cue]).map(\.exact), [true, false, true, false],
                       "a word exactly 1.000 s after the last is a placeholder")
    }

    // MARK: Consensus

    func testCleanCaptionsGiveTheOffset() throws {
        let result = Sync.measure(cues: asrCues(lyrics, offset: 20.6), lines: lyrics)
        XCTAssertEqual(try XCTUnwrap(result.offset), 20.6, accuracy: 0.03)
        XCTAssertEqual(result.matches.count, 8)
        XCTAssertEqual(result.agreeing, 8)
        XCTAssertTrue(result.segments.isEmpty)
    }

    func testRecognitionNoiseIsTolerated() throws {
        // Misheard, dropped and extra words, as YouTube's recognition does.
        let heard: [Int: String] = [
            0: "what's wrong you've been asking",
            2: "how come I'm still thinking",
            4: "when we get over we would get this",
            5: "too young to think about all that install it",
            6: "only goes so far man",
        ]
        let cues = asrCues(lyrics, offset: 20.6, jitter: { [0.1, -0.2, 0.15, 0, -0.1, 0.2, 0, -0.15][$0] },
                           text: { heard[$0] ?? $1 })
        let result = Sync.measure(cues: cues, lines: lyrics)
        XCTAssertEqual(try XCTUnwrap(result.offset), 20.6, accuracy: 0.15)
        XCTAssertGreaterThanOrEqual(result.matches.count, 6)
        XCTAssertLessThan(result.spread, 0.2)
    }

    func testTooFewMatchesGiveNoOffset() {
        let result = Sync.measure(cues: asrCues(Array(lyrics.prefix(4)), offset: 12), lines: lyrics)
        XCTAssertEqual(result.matches.count, 4)
        XCTAssertNil(result.offset, "four lines are not enough to trust")
    }

    func testMatchesThatDisagreeGiveNoOffset() {
        // Each line lands somewhere else: a remix, a live cut, or chance.
        let cues = asrCues(lyrics, offset: 10, jitter: { Double($0) * 1.7 })
        let result = Sync.measure(cues: cues, lines: lyrics)
        XCTAssertGreaterThanOrEqual(result.matches.count, 5)
        XCTAssertNil(result.offset)
        XCTAssertLessThan(Double(result.agreeing), 0.6 * Double(result.matches.count))
    }

    func testAgreementJustAboveSixtyPercentIsTrusted() throws {
        // 5 of 8 agree (62.5 %); 3 are scattered seconds away.
        let cues = asrCues(lyrics, offset: 8, jitter: { [0, 0.1, -0.1, 0.05, 0, 3, -4, 6][$0] })
        let result = Sync.measure(cues: cues, lines: lyrics)
        XCTAssertEqual(result.agreeing, 5)
        XCTAssertEqual(try XCTUnwrap(result.offset), 8, accuracy: 0.1)
        // 4 of 8 do not.
        let fewer = Sync.measure(cues: asrCues(lyrics, offset: 8, jitter: { [0, 0.1, -0.1, 3.3, 5, 3, -4, 6][$0] }),
                                 lines: lyrics)
        XCTAssertNil(fewer.offset)
    }

    func testARepeatedChorusMatchesItsOwnRepeat() throws {
        // The same chorus three times in the song and the video.
        let chorus = ["So can I call you tonight", "I'm trying to make up my mind"]
        var lines: [MusicVideoLyricLine] = []
        for (n, base) in [30.0, 80.0, 130.0].enumerated() {
            lines.append(.init(start: base, text: chorus[0]))
            lines.append(.init(start: base + 4, text: chorus[1]))
            lines.append(.init(start: base + 10, text: "verse line number \(["one", "two", "three"][n]) here"))
        }
        let result = Sync.measure(cues: asrCues(lines, offset: 5.5), lines: lines)
        XCTAssertEqual(try XCTUnwrap(result.offset), 5.5, accuracy: 0.05)
        XCTAssertEqual(result.agreeing, 9, "every chorus line picks its own repeat, not another one")
    }

    func testChorusesOneRepeatApartAreAmbiguous() {
        // Three identical choruses; the captions hold only the second and
        // third (nothing else matched). Shifting by one chorus explains as
        // many lines as the truth: no offset.
        let chorus = ["So can I call you tonight", "I'm trying to make up my mind", "Could you tell me what's real"]
        var lines: [MusicVideoLyricLine] = []
        for base in [30.0, 80.0, 130.0] {
            for (i, text) in chorus.enumerated() { lines.append(.init(start: base + Double(i) * 4, text: text)) }
        }
        let cues = asrCues(Array(lines[3...]), offset: 5)
        let result = Sync.measure(cues: cues, lines: lines)
        XCTAssertNil(result.offset)
        guard case .ambiguous(let rival, let support) = result.verdict else {
            return XCTFail("\(result.verdict)")
        }
        XCTAssertGreaterThanOrEqual(Double(rival), Sync.rivalShare * Double(support))
        XCTAssertTrue(CaptionSyncLog.measuredLine(result, userOffset: nil).contains("ambiguous"))
    }

    func testThreeAgreeingOfFiveIsNotEnough() {
        let cues = asrCues(Array(lyrics.prefix(5)), offset: 8, jitter: { [0, 0.1, -0.1, 3, -4][$0] })
        let result = Sync.measure(cues: cues, lines: lyrics)
        XCTAssertEqual(result.matches.count, 5)
        XCTAssertEqual(result.agreeing, 3)
        XCTAssertNil(result.offset, "60 % of five, but only three lines")
    }

    func testAnUploadersLineCaptionsMatchByTheirStart() throws {
        let cues = lyrics.map { MusicVideoCaptionCue(start: $0.start + 3.2, end: $0.start + 6.5, text: $0.text) }
        let result = Sync.measure(cues: cues, lines: lyrics)
        XCTAssertEqual(try XCTUnwrap(result.offset), 3.2, accuracy: 1e-9)
        XCTAssertEqual(result.spread, 0, accuracy: 1e-9)
    }

    func testAnOffsetOutsideTheRangeIsNotUsed() {
        let result = Sync.measure(cues: asrCues(lyrics, offset: 400), lines: lyrics)
        XCTAssertNil(result.offset)
    }

    // MARK: Piecewise

    func testAnExtraSceneMidSongIsReportedAsTwoSegments() throws {
        // 12 lines; from the seventh on the video runs 9 s later.
        let lines = (0..<12).map {
            MusicVideoLyricLine(start: 15 + Double($0) * 5, text: "distinct lyric line number \($0) " + ["alpha", "bravo",
                "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima"][$0])
        }
        let cues = asrCues(lines, offset: 4, jitter: { $0 >= 6 ? 9 : 0 })
        let result = Sync.measure(cues: cues, lines: lines)
        XCTAssertNil(result.offset, "half and half: no single offset to trust")
        XCTAssertEqual(result.segments.count, 2)
        XCTAssertEqual(result.segments[0].offset, 4, accuracy: 0.05)
        XCTAssertEqual(result.segments[1].offset, 13, accuracy: 0.05)
        XCTAssertEqual(result.segments[0].lines, 6)
        XCTAssertEqual(result.segments[1].lines, 6)
        XCTAssertLessThan(result.segments[0].songEnd, result.segments[1].songStart)
        // Interleaved disagreement is not a second segment.
        let mixed = Sync.measure(cues: asrCues(lines, offset: 4, jitter: { $0 % 2 == 0 ? 9 : 0 }), lines: lines)
        XCTAssertTrue(mixed.segments.isEmpty)
    }

    // MARK: Capture

    func testOnlyYouTubesOwnTimedTextForThisVideoIsBelieved() throws {
        let url = "https://www.youtube-nocookie.com/api/timedtext?v=wIgmyE5Juzw&caps=asr&kind=asr&lang=en&fmt=json3"
        let capture = try XCTUnwrap(Sync.capture(frameHost: "www.youtube-nocookie.com", frameScheme: "https",
                                                 url: url, videoID: "wIgmyE5Juzw"))
        XCTAssertEqual(capture, Sync.Capture(automatic: true, language: "en"))
        XCTAssertEqual(Sync.capture(frameHost: "www.youtube.com", frameScheme: "https",
                                    url: "https://www.youtube.com/api/timedtext?v=wIgmyE5Juzw&lang=en-US&fmt=json3",
                                    videoID: "wIgmyE5Juzw")?.automatic, false)
        XCTAssertNil(Sync.capture(frameHost: "www.youtube-nocookie.com", frameScheme: "https", url: url,
                                  videoID: "dQw4w9WgXcQ"), "another video's captions (an advert's, the last video's)")
        XCTAssertNil(Sync.capture(frameHost: "evil.example", frameScheme: "https", url: url, videoID: "wIgmyE5Juzw"))
        XCTAssertNil(Sync.capture(frameHost: "www.youtube-nocookie.com", frameScheme: "http", url: url,
                                  videoID: "wIgmyE5Juzw"))
        XCTAssertNil(Sync.capture(frameHost: "www.youtube-nocookie.com", frameScheme: "https",
                                  url: "https://evil.example/api/timedtext?v=wIgmyE5Juzw", videoID: "wIgmyE5Juzw"))
        XCTAssertNil(Sync.capture(frameHost: "www.youtube-nocookie.com", frameScheme: "https",
                                  url: "https://www.youtube-nocookie.com/api/other?v=wIgmyE5Juzw", videoID: "wIgmyE5Juzw"))
        XCTAssertNil(Sync.capture(frameHost: "www.youtube-nocookie.com", frameScheme: "https",
                                  url: url + "&tlang=fr", videoID: "wIgmyE5Juzw"), "a machine translation")
    }

    func testTheCaptureScriptOnlyWorksInYouTubesFrameAndTheEmbedAsksForCaptionsOnlyWhenWanted() {
        XCTAssertTrue(Sync.captureScript.contains("www.youtube-nocookie.com"))
        XCTAssertTrue(Sync.captureScript.contains("/api/timedtext"))
        XCTAssertTrue(Sync.captureScript.contains("display:none"), "the player never shows the captions")
        let plain = YouTubeEmbed.html(videoID: "wIgmyE5Juzw", start: 0)
        XCTAssertTrue(plain.contains("cc_load_policy: 0"))
        XCTAssertTrue(plain.contains("var captionsWanted = false"))
        let wanted = YouTubeEmbed.html(videoID: "wIgmyE5Juzw", start: 0, captions: "en-GB")
        XCTAssertTrue(wanted.contains("cc_load_policy: 1"))
        XCTAssertTrue(wanted.contains("cc_lang_pref: 'en'"))
        XCTAssertTrue(wanted.contains("var captionsWanted = true"))
        XCTAssertTrue(wanted.contains("mute: 1"), "still muted")
        XCTAssertNil(YouTubeEmbed.captionLanguage("en'); alert(1"))
    }

    // MARK: In the app

    private var defaults: UserDefaults!
    private let song = MusicVideoSong(title: "Are You Bored Yet?", artist: "Wallows", album: "Nothing Happens", duration: 178)
    private let videoID = "wIgmyE5Juzw"

    override func setUp() async throws {
        let name = "LunalyrTests.MusicVideoCaptionSync.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: name)!
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
    }

    private func showing(lyrics: [MusicVideoLyricLine]?) async -> MusicVideoController {
        let resolver = MusicVideoResolver(library: NoVideoLibrary(), playability: AlwaysPlayable(), http: NoHTTP(),
                                          cache: MusicVideoCache(defaults: defaults),
                                          links: MusicVideoLinks(defaults: defaults))
        resolver.links.set(videoID, for: song.key)
        let controller = MusicVideoController(resolver: resolver, offsets: MusicVideoOffsets(defaults: defaults),
                                              songClock: { (30, true) }, lyrics: { _ in lyrics })
        let track = NowPlayingMonitor.Track(title: song.title, artist: song.artist, duration: song.duration,
                                            album: song.album)
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        return controller
    }

    private func json3(_ cues: [MusicVideoCaptionCue]) -> Data {
        let events: [[String: Any]] = cues.map { cue in
            let start = Int((cue.start * 1000).rounded())
            let segs: [[String: Any]] = (cue.words ?? [.init(start: cue.start, text: cue.text)]).enumerated().map {
                var seg: [String: Any] = ["utf8": ($0.offset == 0 ? "" : " ") + $0.element.text]
                if $0.offset > 0 { seg["tOffsetMs"] = Int(($0.element.start * 1000).rounded()) - start }
                return seg
            }
            return ["tStartMs": start, "dDurationMs": 3000, "segs": segs]
        }
        return try! JSONSerialization.data(withJSONObject: ["events": events])
    }

    func testCaptionsSetTheTimingFromCaptionsOverTheEstimate() async {
        let controller = await showing(lyrics: lyrics)
        controller.noteVideoDuration(204.7, of: .youTube(videoID))
        XCTAssertEqual(controller.offsetOrigin, .estimated)
        XCTAssertTrue(controller.wantsCaptions(for: .youTube(videoID)))
        controller.noteCaptions(json3(asrCues(lyrics, offset: 20.6)), of: .youTube(videoID))
        XCTAssertEqual(controller.offsetOrigin, .captions)
        XCTAssertEqual(controller.currentOffset, 20.6, accuracy: 0.03)
        XCTAssertEqual(MusicVideoController.timingLabel(origin: .captions), "From captions")
        XCTAssertTrue(controller.wantsCaptions(for: .youTube(videoID)), "always loaded")
    }

    func testCaptionsBeforeTheLyricsAreMeasuredWhenTheLyricsArrive() async {
        var lines: [MusicVideoLyricLine]?
        let resolver = MusicVideoResolver(library: NoVideoLibrary(), playability: AlwaysPlayable(), http: NoHTTP(),
                                          cache: MusicVideoCache(defaults: defaults),
                                          links: MusicVideoLinks(defaults: defaults))
        resolver.links.set(videoID, for: song.key)
        let controller = MusicVideoController(resolver: resolver, offsets: MusicVideoOffsets(defaults: defaults),
                                              songClock: { (30, true) }, lyrics: { _ in lines })
        controller.update(track: NowPlayingMonitor.Track(title: song.title, artist: song.artist, duration: song.duration,
                                                         album: song.album), enabled: true, online: false)
        await controller.settle()
        controller.noteCaptions(json3(asrCues(lyrics, offset: 7)), of: .youTube(videoID))
        XCTAssertEqual(controller.offsetOrigin, .none)
        lines = lyrics
        controller.noteVideoSample(time: 37, playing: true, of: .youTube(videoID))
        XCTAssertEqual(controller.offsetOrigin, .captions)
        XCTAssertEqual(controller.currentOffset, 7, accuracy: 0.03)
    }

    private var track: NowPlayingMonitor.Track {
        NowPlayingMonitor.Track(title: song.title, artist: song.artist, duration: song.duration, album: song.album)
    }
    private var video: String { "yt:\(videoID)" }

    /// Another song, then this one again: a new playback of the same video.
    private func replay(_ controller: MusicVideoController) async {
        controller.update(track: NowPlayingMonitor.Track(title: "Other", artist: "Someone", duration: 100, album: "X"),
                          enabled: true, online: false)
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
    }

    /// The desktop player's film button: Align, ← (or →) `steps` times, Done.
    private func alignInDesktopPlayer(_ controller: MusicVideoController, by delta: Double, steps: Int = 1) {
        controller.reduceMotion = { false }
        controller.noteVideoSample(time: 50, playing: true, of: .youTube(videoID))
        XCTAssertTrue(controller.beginAlign(), "\(controller.alignAvailability)")
        for _ in 0..<steps { controller.alignNudge(by: delta) }
        controller.alignDone()
        XCTAssertFalse(controller.isAligning)
    }

    func testTimingSavedBeforeCaptionsStillWinsAndResetHandsBackToThem() async {
        // An offset saved before caption sync existed.
        defaults.set([song.key: 3.0], forKey: MusicVideoOffsets.key)
        defaults.set([song.key: video], forKey: MusicVideoOffsets.manualVideoKey)
        let controller = await showing(lyrics: lyrics)
        XCTAssertEqual(controller.offsetOrigin, .manual)
        XCTAssertTrue(controller.wantsCaptions(for: .youTube(videoID)), "loaded even for a video the user timed")
        controller.noteCaptions(json3(asrCues(lyrics, offset: 20.6)), of: .youTube(videoID))
        XCTAssertEqual(controller.offsetOrigin, .manual, "the user's saved timing wins over captions")
        XCTAssertEqual(controller.currentOffset, 3)
        XCTAssertTrue(controller.captionLog.last?.hasSuffix("skipped: the user's timing +3.00 s wins for this video") == true,
                      "\(controller.captionLog)")
        controller.resetOffset()
        XCTAssertEqual(controller.offsetOrigin, .captions, "Reset hands the video back to its captions")
        XCTAssertEqual(controller.currentOffset, 20.6, accuracy: 0.03)
        XCTAssertNil(MusicVideoOffsets(defaults: defaults).values[song.key], "and forgets the older timing")
    }

    // The bug: a nudge made while captions set the timing was kept in memory
    // for this playback only, so it was lost at the next song or replay and
    // dropped whenever the captions were measured again.

    func testANudgeFromTheDesktopPlayerOverCaptionsIsSavedAndWins() async {
        let controller = await showing(lyrics: lyrics)
        controller.noteCaptions(json3(asrCues(lyrics, offset: 20.6)), of: .youTube(videoID))
        XCTAssertEqual(controller.offsetOrigin, .captions)

        alignInDesktopPlayer(controller, by: MusicVideoAlignKey.step, steps: 3)
        XCTAssertEqual(controller.offsetOrigin, .manual)
        XCTAssertEqual(controller.currentOffset, 20.9, accuracy: 0.001, "moved from the captions' offset, once per press")
        let saved = MusicVideoOffsets(defaults: defaults)
        XCTAssertEqual(saved.offset(for: song.key, video: video), 20.9, accuracy: 0.001, "saved")
        XCTAssertEqual(saved.manualEntry(song.key), .init(value: 20.9, video: video))
        XCTAssertTrue(controller.captionLog.last?.contains("the user set +20.90 s over the captions' +20.60 s") == true,
                      "\(controller.captionLog)")

        // The player loads the captions again (a reload, or a track with
        // word timing this time) and they measure a little differently.
        controller.noteCaptions(json3(asrCues(lyrics, offset: 20.75)), of: .youTube(videoID))
        XCTAssertEqual(controller.offsets.captionedOffset(song.key, video: video) ?? 0, 20.75, accuracy: 0.03)
        XCTAssertEqual(controller.offsetOrigin, .manual, "captions measured again never undo the user's timing")
        XCTAssertEqual(controller.currentOffset, 20.9, accuracy: 0.001)
        XCTAssertTrue(controller.captionLog.last?.hasSuffix("skipped: the user's timing +20.90 s wins for this video") == true,
                      "\(controller.captionLog)")

        await replay(controller)
        XCTAssertEqual(controller.offsetOrigin, .manual, "kept for the next playback")
        XCTAssertEqual(controller.currentOffset, 20.9, accuracy: 0.001)
        controller.noteCaptions(json3(asrCues(lyrics, offset: 20.6)), of: .youTube(videoID))
        XCTAssertEqual(controller.currentOffset, 20.9, accuracy: 0.001, "and its captions don't undo it")
    }

    func testTheUsersTimingOverCaptionsSurvivesARelaunch() async {
        let first = await showing(lyrics: lyrics)
        first.noteCaptions(json3(asrCues(lyrics, offset: 20.6)), of: .youTube(videoID))
        first.videoLater()
        XCTAssertEqual(first.currentOffset, 20.35, accuracy: 0.001)

        // A new run: nothing in memory, the same preferences, captions again.
        let second = await showing(lyrics: lyrics)
        XCTAssertEqual(second.offsetOrigin, .manual)
        XCTAssertEqual(second.currentOffset, 20.35, accuracy: 0.001)
        second.noteCaptions(json3(asrCues(lyrics, offset: 20.6)), of: .youTube(videoID))
        XCTAssertEqual(second.offsetOrigin, .manual)
        XCTAssertEqual(second.currentOffset, 20.35, accuracy: 0.001)
    }

    func testANudgeMadeBeforeTheCaptionsArriveStillWins() async {
        let controller = await showing(lyrics: lyrics)
        controller.noteVideoDuration(204.7, of: .youTube(videoID))
        XCTAssertEqual(controller.offsetOrigin, .estimated)
        controller.videoEarlier()
        let mine = controller.currentOffset
        controller.noteCaptions(json3(asrCues(lyrics, offset: 20.6)), of: .youTube(videoID))
        XCTAssertEqual(controller.offsetOrigin, .manual, "the captions came after the user's nudge")
        XCTAssertEqual(controller.currentOffset, mine, accuracy: 0.001)
    }

    func testSettingsAndMenuNudgesTakeTheSamePathAndResetGoesBackToCaptions() async {
        let controller = await showing(lyrics: lyrics)
        controller.noteCaptions(json3(asrCues(lyrics, offset: 20.6)), of: .youTube(videoID))
        controller.nudgeOffset(by: 0.5)   // Settings' Video timing
        controller.videoLater()           // the menu bar's Later
        XCTAssertEqual(controller.currentOffset, 20.85, accuracy: 0.001)
        XCTAssertEqual(MusicVideoOffsets(defaults: defaults).offset(for: song.key, video: video), 20.85, accuracy: 0.001)
        controller.resetOffset()
        XCTAssertEqual(controller.offsetOrigin, .captions, "Reset goes back to the timing from captions")
        XCTAssertEqual(controller.currentOffset, 20.6, accuracy: 0.03)
        XCTAssertFalse(MusicVideoOffsets(defaults: defaults).hasManual(song.key), "and forgets the user's timing")
    }

    func testAlignCancelOverCaptionsPutsBackWhatWasThere() async {
        defaults.set([song.key: 3.0], forKey: MusicVideoOffsets.key)
        defaults.set([song.key: video], forKey: MusicVideoOffsets.manualVideoKey)
        let controller = await showing(lyrics: lyrics)
        controller.noteCaptions(json3(asrCues(lyrics, offset: 20.6)), of: .youTube(videoID))
        controller.reduceMotion = { false }
        controller.noteVideoSample(time: 50, playing: true, of: .youTube(videoID))
        XCTAssertTrue(controller.beginAlign())
        controller.alignNudge(by: 1)
        XCTAssertEqual(controller.currentOffset, 4, accuracy: 0.001)
        controller.alignCancel()
        XCTAssertEqual(controller.offsetOrigin, .manual)
        XCTAssertEqual(controller.currentOffset, 3)
        XCTAssertEqual(MusicVideoOffsets(defaults: defaults).manualEntry(song.key), .init(value: 3, video: video))

        // Nothing saved before: Esc forgets the nudge, and captions apply.
        controller.resetOffset()
        XCTAssertEqual(controller.offsetOrigin, .captions)
        XCTAssertTrue(controller.beginAlign())
        controller.alignNudge(by: 1)
        controller.alignCancel()
        XCTAssertEqual(controller.offsetOrigin, .captions)
        XCTAssertEqual(controller.currentOffset, 20.6, accuracy: 0.03)
        XCTAssertFalse(MusicVideoOffsets(defaults: defaults).hasManual(song.key))
    }

    func testTheCaptionLogSaysWhatWasDecidedWithoutLyricText() async {
        var lines: [MusicVideoLyricLine]?
        let resolver = MusicVideoResolver(library: NoVideoLibrary(), playability: AlwaysPlayable(), http: NoHTTP(),
                                          cache: MusicVideoCache(defaults: defaults),
                                          links: MusicVideoLinks(defaults: defaults))
        resolver.links.set(videoID, for: song.key)
        let controller = MusicVideoController(resolver: resolver, offsets: MusicVideoOffsets(defaults: defaults),
                                              songClock: { (30, true) }, lyrics: { _ in lines })
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        controller.noteCaptions(json3(asrCues(lyrics, offset: 20.6)), of: .youTube(videoID))
        controller.noteVideoSample(time: 40, playing: true, of: .youTube(videoID))
        controller.noteVideoSample(time: 41, playing: true, of: .youTube(videoID))
        XCTAssertEqual(controller.captionLog, [CaptionSyncLog.waitingLine], "once, not every sample")
        lines = lyrics
        controller.noteVideoSample(time: 42, playing: true, of: .youTube(videoID))
        XCTAssertEqual(controller.captionLog.last, "captions: 8 lines matched, 8 agree (spread 0.00 s), offset +20.60 s, applied")

        controller.noteCaptions(json3(asrCues(Array(lyrics.prefix(3)), offset: 5)), of: .youTube(videoID))
        XCTAssertTrue(controller.captionLog.last?.hasSuffix(Sync.Verdict.tooFew.summary) == true, "\(controller.captionLog)")
        for line in controller.captionLog {
            for lyric in lyrics { XCTAssertFalse(line.contains(lyric.text)) }
            XCTAssertFalse(line.contains(videoID))
        }

        XCTAssertEqual(CaptionSyncLog.emptyLine(status: 429, bytes: 0),
                       "captions: reply with no captions (HTTP 429, 0 bytes): blocked or refused")
        XCTAssertEqual(CaptionSyncLog.emptyLine(status: 200, bytes: 0),
                       "captions: reply with no captions (HTTP 200, 0 bytes): empty (no track, or refused)")
        XCTAssertEqual(CaptionSyncLog.emptyLine(status: 200, bytes: 0, type: "arraybuffer"),
                       "captions: reply with no captions (HTTP 200, 0 bytes): unreadable response type arraybuffer")
        XCTAssertEqual(CaptionSyncLog.fetchedLine(automatic: true, language: "en", cues: 42, wordTimed: true),
                       "captions: fetched YouTube's automatic (auto-only) track, language en, 42 cues, word timing yes")
        XCTAssertEqual(CaptionSyncLog.askedLine(language: "ko"), "captions: asked the player for them (language ko)")
        XCTAssertEqual(CaptionSyncLog.noneLine(after: 72),
                       "captions: none arrived within 72 s (the video has none, or the player never loaded them)")
    }

    func testRefusalsSayWhy() {
        XCTAssertEqual(Sync.measure(cues: [], lines: lyrics).verdict, .noMatches)
        XCTAssertEqual(Sync.measure(cues: asrCues(lyrics, offset: 20.6), lines: lyrics).verdict, .trusted)
        let three = Sync.measure(cues: asrCues(Array(lyrics.prefix(3)), offset: 20.6), lines: lyrics)
        XCTAssertNil(three.offset)
        XCTAssertEqual(three.verdict, .tooFew)
        let line = CaptionSyncLog.measuredLine(three, userOffset: nil)
        XCTAssertTrue(line.contains("3 lines matched") && line.contains("best +20.60 s") && line.contains("refused"), line)
    }

    func testWithoutTrustedCaptionsTheSavedTimingStillApplies() async {
        let controller = await showing(lyrics: lyrics)
        controller.noteVideoDuration(204.7, of: .youTube(videoID))
        controller.offsets.set(3, for: song.key, video: "yt:\(videoID)")
        controller.noteCaptions(json3(asrCues(Array(lyrics.prefix(3)), offset: 20.6)), of: .youTube(videoID))
        XCTAssertEqual(controller.offsetOrigin, .manual)
        XCTAssertEqual(controller.currentOffset, 3)
        controller.videoEarlier()
        XCTAssertEqual(MusicVideoOffsets(defaults: defaults).offset(for: song.key, video: "yt:\(videoID)"), 3.25,
                       "without captions a nudge is saved as before")
    }

    func testUntrustworthyCaptionsLeaveTheEstimate() async {
        let controller = await showing(lyrics: lyrics)
        controller.noteVideoDuration(204.7, of: .youTube(videoID))
        controller.noteCaptions(json3(asrCues(Array(lyrics.prefix(3)), offset: 20.6)), of: .youTube(videoID))
        XCTAssertEqual(controller.offsetOrigin, .estimated)
        XCTAssertNil(controller.lastCaptionResult?.offset)
    }
}
