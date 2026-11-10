import CoreGraphics
import XCTest
@testable import VersoCustom

// Lining a YouTube video's cuts up with the beat, the per-video memory of
// offsets, and the sync loop's handling of small offset changes and lasting
// drift. Synthetic frames and onsets only: no player, no audio, no network,
// no real preferences.

private struct CutSeeded: RandomNumberGenerator {
    var state: UInt64
    mutating func next() -> UInt64 {
        state = state &* 6364136223846793005 &+ 1442695040888963407
        return state
    }
}

private struct NoVideoLibrary: MusicVideoLibrary {
    func localVideo(for song: MusicVideoSong) async -> URL? { nil }
}

private struct AlwaysPlayable: MusicVideoPlayability {
    func check(_ url: URL) async -> MusicVideoPlayabilityResult { .playable }
}

/// Never reaches the network: every request is a 404 (a pasted link's
/// still check asks for its frames).
private final class NoHTTP: MusicVideoHTTP, @unchecked Sendable {
    func get(_ url: URL) async throws -> (Data, HTTPURLResponse) {
        (Data(), HTTPURLResponse(url: url, statusCode: 404, httpVersion: nil, headerFields: [:])!)
    }
}

@MainActor
private final class QuietListener: MusicVideoBriefListening {
    var mayListen = true
    private(set) var starts: [TimeInterval] = []
    private(set) var stops = 0
    func start(for seconds: TimeInterval) -> Bool {
        starts.append(seconds)
        return true
    }
    func stop() { stops += 1 }
}

/// A song's beats and a video's cuts, on one uptime clock.
private enum Synthetic {
    static let rate = MusicVideoAudioAlign.rate
    static let grid = MusicVideoCutAlign.gridWidth * MusicVideoCutAlign.gridHeight

    /// Energy frames at 50 a second from `start` to `end`: quiet, with a
    /// hit (loud, decaying) at each beat time, `accents` giving its level.
    static func audio(beats: [(time: Double, level: Float)], from start: Double = 0, to end: Double = 42,
                      seed: UInt64 = 3) -> [AudioEnvelopeRecorder.Frame] {
        var random = CutSeeded(state: seed)
        var frames: [AudioEnvelopeRecorder.Frame] = []
        var time = start
        while time < end {
            var energy: Float = 0.01 * Float.random(in: 0.8...1.2, using: &random)
            for beat in beats where time >= beat.time && time < beat.time + 0.3 {
                energy += beat.level * Float(exp(-(time - beat.time) / 0.08))
            }
            frames.append(.init(time: time + Double.random(in: -0.002...0.002, using: &random), energy: energy))
            time += 1 / rate
        }
        return frames
    }

    /// Frames at `fps` from `start` to `end`: one random picture per shot,
    /// a new shot at each cut, a little movement within a shot.
    static func frames(cuts: [Double], from start: Double = 10, to end: Double = 37, fps: Double = 4,
                       seed: UInt64 = 7) -> [MusicVideoCutAlign.Frame] {
        var random = CutSeeded(state: seed)
        let shots = (0...cuts.count).map { _ in (0..<grid).map { _ in Float.random(in: 0...1, using: &random) } }
        var frames: [MusicVideoCutAlign.Frame] = []
        var time = start
        while time <= end {
            let shot = cuts.filter { $0 <= time }.count
            let picture = shots[shot].map { min(1, max(0, $0 + Float.random(in: -0.02...0.02, using: &random))) }
            frames.append(.init(time: time + Double.random(in: -0.01...0.01, using: &random), signature: picture))
            time += 1 / fps
        }
        return frames
    }

    /// A beat every 0.47 s (128 bpm, off the frames' rhythm); about a
    /// third of them loud, at random, the rest soft.
    static func accentedBeats(seed: UInt64) -> [(time: Double, level: Float)] {
        var random = CutSeeded(state: seed)
        return stride(from: 0.3, to: 42, by: 0.47).map { time in
            (time, Double.random(in: 0...1, using: &random) < 0.3 ? Float(1) : Float(0.04))
        }
    }
}

@MainActor
final class MusicVideoCutSyncTests: XCTestCase {
    private var defaults: UserDefaults!

    override func setUp() async throws {
        let name = "LunalyrTests.MusicVideoCutSync.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: name)!
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
    }

    private func found(_ verdict: MusicVideoCutAlign.Verdict, file: StaticString = #filePath, line: UInt = #line)
        -> MusicVideoCutAlign.Result? {
        if case .found(let result) = verdict { return result }
        XCTFail("expected a clear peak, got \(verdict)", file: file, line: line)
        return nil
    }

    // MARK: Cuts

    func testCutsAreSuddenChangesNotMovement() {
        let frames = Synthetic.frames(cuts: [12.1, 15.6, 21.3])
        let cuts = MusicVideoCutAlign.cuts(in: frames)
        XCTAssertEqual(cuts.count, 3)
        for (cut, time) in zip(cuts, [12.1, 15.6, 21.3]) {
            XCTAssertLessThanOrEqual(cut.from, time + 0.02)
            XCTAssertGreaterThanOrEqual(cut.to, time - 0.02)
        }
        XCTAssertTrue(MusicVideoCutAlign.cuts(in: Synthetic.frames(cuts: [])).isEmpty, "one long take has no cuts")
        // Frames too far apart place nothing.
        let sparse = Synthetic.frames(cuts: [12.1, 15.6], fps: 1)
        XCTAssertTrue(MusicVideoCutAlign.cuts(in: sparse).isEmpty)
    }

    func testSignatureOfASnapshot() throws {
        func image(_ draw: (CGContext) -> Void) -> CGImage {
            let context = CGContext(data: nil, width: 64, height: 36, bitsPerComponent: 8, bytesPerRow: 0,
                                    space: CGColorSpaceCreateDeviceRGB(),
                                    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
            draw(context)
            return context.makeImage()!
        }
        let black = image { $0.setFillColor(gray: 0, alpha: 1); $0.fill(CGRect(x: 0, y: 0, width: 64, height: 36)) }
        XCTAssertNil(MusicVideoCutAlign.signature(of: black), "a blank snapshot caught no video")
        let left = image {
            $0.setFillColor(gray: 0, alpha: 1); $0.fill(CGRect(x: 0, y: 0, width: 64, height: 36))
            $0.setFillColor(gray: 1, alpha: 1); $0.fill(CGRect(x: 0, y: 0, width: 32, height: 36))
        }
        let right = image {
            $0.setFillColor(gray: 0, alpha: 1); $0.fill(CGRect(x: 0, y: 0, width: 64, height: 36))
            $0.setFillColor(gray: 1, alpha: 1); $0.fill(CGRect(x: 32, y: 0, width: 32, height: 36))
        }
        let a = try XCTUnwrap(MusicVideoCutAlign.signature(of: left))
        let b = try XCTUnwrap(MusicVideoCutAlign.signature(of: right))
        XCTAssertEqual(a.count, MusicVideoCutAlign.gridWidth * MusicVideoCutAlign.gridHeight)
        XCTAssertGreaterThan(try XCTUnwrap(MusicVideoCutAlign.difference(a, b)), 0.5, "a cut")
        XCTAssertEqual(try XCTUnwrap(MusicVideoCutAlign.difference(a, a)), 0)
    }

    // MARK: Alignment

    /// Cuts on the loud beats, shown `lag` late.
    private func cutsOnAccents(_ beats: [(time: Double, level: Float)], lag: Double) -> [Double] {
        beats.filter { $0.level > 0.5 && $0.time > 10.5 && $0.time < 36.5 }.map { $0.time + lag }
    }

    func testFindsHowLateTheCutsFallBehindTheBeat() throws {
        for (seed, lag) in [(UInt64(6), 0.4), (7, -0.45), (8, 0.15), (9, 0.0), (10, -0.62), (11, 0.66)] {
            let beats = Synthetic.accentedBeats(seed: seed)
            let verdict = MusicVideoCutAlign.align(frames: Synthetic.frames(cuts: cutsOnAccents(beats, lag: lag), seed: seed),
                                                   audio: Synthetic.audio(beats: beats, seed: seed))
            let result = try XCTUnwrap(found(verdict), "lag \(lag)")
            XCTAssertEqual(result.lag, lag, accuracy: 0.06, "lag \(lag)")
            XCTAssertGreaterThanOrEqual(result.peak, MusicVideoCutAlign.peakRatio * result.runnerUp)
            XCTAssertGreaterThanOrEqual(result.cuts, MusicVideoCutAlign.minimumCuts)
        }
    }

    func testSurvivesNoiseMissedAndExtraCuts() throws {
        let beats = Synthetic.accentedBeats(seed: 30)
        var cuts = cutsOnAccents(beats, lag: 0.3)
        cuts.remove(at: 2)
        cuts.append(contentsOf: [13.37, 27.81])   // two cuts off the beat
        cuts.sort()
        // Loud noise in the music between the beats.
        var audio = Synthetic.audio(beats: beats, seed: 30)
        var random = CutSeeded(state: 77)
        for index in audio.indices where Double.random(in: 0...1, using: &random) < 0.03 {
            audio[index].energy += Float.random(in: 0...0.05, using: &random)
        }
        let result = try XCTUnwrap(found(MusicVideoCutAlign.align(frames: Synthetic.frames(cuts: cuts, seed: 30),
                                                                  audio: audio)))
        XCTAssertEqual(result.lag, 0.3, accuracy: 0.08)
    }

    func testAnEvenBeatCutEvenlyIsRefused() {
        // Every beat equally loud, a cut every second beat: one beat
        // earlier or later fits as well, so no lag can be trusted.
        let beats = stride(from: 0.3, to: 42, by: 0.47).map { ($0, Float(1)) }
        let cuts = beats.enumerated().filter { $0.offset % 2 == 0 && $0.element.0 > 10.5 && $0.element.0 < 36.5 }
            .map { $0.element.0 + 0.2 }
        let verdict = MusicVideoCutAlign.align(frames: Synthetic.frames(cuts: cuts), audio: Synthetic.audio(beats: beats))
        XCTAssertEqual(verdict, .unclear)
        // Every beat, cut every beat: the same.
        let everyBeat = beats.map(\.0).filter { $0 > 10.5 && $0 < 36.5 }.map { $0 + 0.2 }
        XCTAssertEqual(MusicVideoCutAlign.align(frames: Synthetic.frames(cuts: everyBeat), audio: Synthetic.audio(beats: beats)),
                       .unclear)
    }

    func testCutsUnrelatedToTheMusicAreRefused() {
        for seed in UInt64(40)..<UInt64(80) {
            var random = CutSeeded(state: seed)
            let cuts = (0..<13).map { _ in Double.random(in: 10.5...36.5, using: &random) }.sorted()
            let beats = Synthetic.accentedBeats(seed: seed + 100)
            let verdict = MusicVideoCutAlign.align(frames: Synthetic.frames(cuts: cuts, seed: seed),
                                                   audio: Synthetic.audio(beats: beats, seed: seed))
            if case .found(let result) = verdict { XCTFail("seed \(seed): believed a chance fit at \(result.lag)") }
        }
    }

    func testTooFewCutsOrNoAudioGiveNoVerdictYet() {
        let beats = Synthetic.accentedBeats(seed: 21)
        let cuts = cutsOnAccents(beats, lag: 0.4)
        XCTAssertEqual(MusicVideoCutAlign.align(frames: Synthetic.frames(cuts: cuts), audio: []), .tooLittle,
                       "capture not running yet")
        XCTAssertEqual(MusicVideoCutAlign.align(frames: Synthetic.frames(cuts: Array(cuts.prefix(3)), to: 18),
                                                audio: Synthetic.audio(beats: beats)), .tooLittle, "too short")
        // Audio only from 31 s: most cuts have no music around them.
        XCTAssertEqual(MusicVideoCutAlign.align(frames: Synthetic.frames(cuts: cuts),
                                                audio: Synthetic.audio(beats: beats, from: 31)), .tooLittle)
        XCTAssertEqual(MusicVideoCutAlign.align(frames: Synthetic.frames(cuts: [15, 25]),
                                                audio: Synthetic.audio(beats: beats)), .unclear,
                       "a long take with hardly a cut is not worth trying again")
    }

    // MARK: Collector

    func testCollectorStartsOverAfterASeekPauseOrNewOffset() {
        var collector = MusicVideoCutCollector()
        let frame = { (time: Double) in
            MusicVideoCutAlign.Frame(time: time, signature: [Float](repeating: 0.5, count: Synthetic.grid))
        }
        XCTAssertFalse(collector.shouldCapture(now: 10, offset: 4, steady: true, lastSeek: 9.5), "settling after a seek")
        XCTAssertTrue(collector.shouldCapture(now: 11, offset: 4, steady: true, lastSeek: 9.5))
        collector.add(frame(11), drift: 0.1)
        XCTAssertTrue(collector.shouldCapture(now: 11.25, offset: 4, steady: true, lastSeek: 9.5))
        collector.add(frame(11.25), drift: 0.3)
        XCTAssertEqual(collector.frames.count, 2)
        XCTAssertEqual(collector.meanDrift, 0.2, accuracy: 1e-9)
        XCTAssertEqual(collector.offsetUsed, 4)
        XCTAssertFalse(collector.shouldCapture(now: 11.5, offset: 4.25, steady: true, lastSeek: 9.5), "a nudge")
        XCTAssertTrue(collector.frames.isEmpty)
        XCTAssertTrue(collector.shouldCapture(now: 11.75, offset: 4.25, steady: true, lastSeek: 9.5))
        collector.add(frame(11.75), drift: 0)
        XCTAssertFalse(collector.shouldCapture(now: 12, offset: 4.25, steady: false, lastSeek: 9.5), "paused")
        XCTAssertTrue(collector.frames.isEmpty)
        XCTAssertFalse(collector.shouldCapture(now: 13, offset: 4.25, steady: true, lastSeek: 12.8), "sought")
        // Frames older than `keep` are dropped; checks are spaced.
        for step in 0...120 { collector.add(frame(20 + Double(step) * 0.25), drift: 0) }
        XCTAssertLessThanOrEqual(collector.span, MusicVideoCutCollector.keep + 0.001)
        XCTAssertTrue(collector.checkDue(now: 50))
        XCTAssertFalse(collector.checkDue(now: 52))
        XCTAssertTrue(collector.checkDue(now: 54))
        XCTAssertFalse(collector.expired(now: 10 + MusicVideoCutCollector.limit - 1))
        XCTAssertTrue(collector.expired(now: 10 + MusicVideoCutCollector.limit))
    }

    // MARK: Controller

    private let song = MusicVideoSong(title: "Blinding Lights", artist: "The Weeknd", album: "After Hours", duration: 200)
    private var track: NowPlayingMonitor.Track {
        NowPlayingMonitor.Track(title: "Blinding Lights", artist: "The Weeknd", duration: 200, album: "After Hours")
    }
    private let start = Date(timeIntervalSince1970: 1_800_000_000)
    private let videoID = "dQw4w9WgXcQ"

    private func showing(captureActive: Bool = true, listener: QuietListener? = nil,
                         audio: [AudioEnvelopeRecorder.Frame] = []) async -> MusicVideoController {
        let resolver = MusicVideoResolver(library: NoVideoLibrary(), playability: AlwaysPlayable(), http: NoHTTP(),
                                          cache: MusicVideoCache(defaults: defaults),
                                          links: MusicVideoLinks(defaults: defaults), now: { [start] in start })
        resolver.links.set(videoID, for: song.key)
        let controller = MusicVideoController(resolver: resolver, offsets: MusicVideoOffsets(defaults: defaults),
                                              listener: listener, cutSync: true, captureActive: { captureActive },
                                              audioFrames: { audio }, now: { [start] in start })
        controller.update(track: track, enabled: true, online: false)
        await controller.settle()
        return controller
    }

    func testAClearFitIsStoredForTheVideoAndRemembered() async throws {
        let beats = Synthetic.accentedBeats(seed: 6)
        let controller = await showing(audio: Synthetic.audio(beats: beats, seed: 6))
        let source = MusicVideoSource.youTube(videoID)
        controller.noteVideoDuration(215, of: source)
        XCTAssertEqual(controller.currentOffset, 15, "the length estimate")
        XCTAssertTrue(controller.cutSyncWanted(source))
        XCTAssertTrue(controller.beginCutSync(source))
        XCTAssertTrue(controller.cutSyncing)
        XCTAssertEqual(MusicVideoController.timingLabel(origin: controller.offsetOrigin, syncing: false,
                                                        watchingCuts: controller.cutSyncing),
                       MusicVideoController.watchingCutsLabel)
        // Under the estimate the video showed its cuts 0.4 s late, while
        // the loop held it 0.1 s ahead of where it wanted it.
        let frames = Synthetic.frames(cuts: cutsOnAccents(beats, lag: 0.4), seed: 6)
        XCTAssertTrue(controller.checkCutSync(source, frames: frames, offsetUsed: 15, drift: 0.1, final: false))
        XCTAssertEqual(controller.currentOffset, 15.5, accuracy: 0.08)
        XCTAssertEqual(controller.offsetOrigin, .cuts)
        XCTAssertFalse(controller.cutSyncing)
        XCTAssertEqual(MusicVideoController.timingLabel(origin: controller.offsetOrigin, syncing: false), "Synced automatically")
        XCTAssertFalse(controller.cutSyncWanted(source), "once found, not again")
        let reloaded = MusicVideoOffsets(defaults: defaults)
        XCTAssertEqual(reloaded.origin(for: song.key, video: "yt:\(videoID)", now: start), .cuts, "kept on this Mac")
        XCTAssertEqual(reloaded.offset(for: song.key, video: "yt:\(videoID)", now: start), 15.5, accuracy: 0.08)
    }

    func testAnUnclearFitChangesNothingAndIsNotRetriedSoon() async {
        let beats = stride(from: 0.3, to: 42, by: 0.47).map { ($0, Float(1)) }
        let controller = await showing(audio: Synthetic.audio(beats: beats))
        let source = MusicVideoSource.youTube(videoID)
        controller.noteVideoDuration(215, of: source)
        XCTAssertTrue(controller.beginCutSync(source))
        let frames = Synthetic.frames(cuts: beats.enumerated().filter { $0.offset % 2 == 0 && $0.element.0 > 10.5 }
            .map { $0.element.0 + 0.2 }.filter { $0 < 36.5 })
        XCTAssertFalse(controller.checkCutSync(source, frames: frames, offsetUsed: 15, drift: 0, final: false),
                       "keeps watching")
        XCTAssertEqual(controller.currentOffset, 15)
        XCTAssertTrue(controller.checkCutSync(source, frames: frames, offsetUsed: 15, drift: 0, final: true))
        XCTAssertEqual(controller.currentOffset, 15, "a wrong guess is never stored")
        XCTAssertEqual(controller.offsetOrigin, .estimated)
        XCTAssertFalse(controller.cutSyncWanted(source))
        XCTAssertNotNil(MusicVideoOffsets(defaults: defaults).automaticEntry(for: song.key, video: "yt:\(videoID)", now: start),
                        "remembered as tried")
    }

    func testCutSyncNeedsTheMusicAndNeverOverridesTheUser() async {
        let source = MusicVideoSource.youTube(videoID)
        let deaf = await showing(captureActive: false)
        XCTAssertFalse(deaf.cutSyncWanted(source), "no capture and no brief listen allowed")

        let listener = QuietListener()
        let briefly = await showing(captureActive: false, listener: listener)
        XCTAssertTrue(briefly.beginCutSync(source))
        XCTAssertEqual(listener.starts, [MusicVideoController.cutListenLimit], "a brief listen, bounded")
        briefly.videoEarlier()
        XCTAssertFalse(briefly.cutSyncContinues(source), "the user's nudge ends it")
        XCTAssertEqual(listener.stops, 1, "and the listen")
        XCTAssertFalse(briefly.cutSyncWanted(source), "and it is not started over the user's offset")
        XCTAssertFalse(briefly.cutSyncWanted(.youTube("BBBBBBBBBBB")), "only for the video on screen")

        // Re-sync forgets the nudge and may try again.
        briefly.resync()
        XCTAssertTrue(briefly.cutSyncWanted(source))
        XCTAssertTrue(briefly.beginCutSync(source))
        // A song change ends it, listen and all.
        briefly.update(track: nil, enabled: true, online: false)
        XCTAssertFalse(briefly.cutSyncing)
        XCTAssertEqual(listener.stops, 2)
    }

    func testVideoEarlierAndLaterMoveAQuarterSecondForThisVideo() async {
        let controller = await showing()
        controller.noteVideoDuration(215, of: .youTube(videoID))
        controller.videoEarlier()
        XCTAssertEqual(controller.currentOffset, 15.25, accuracy: 1e-9)
        controller.videoEarlier()
        XCTAssertEqual(controller.currentOffset, 15.5, accuracy: 1e-9, "quarter steps add up exactly")
        controller.videoLater()
        controller.videoLater()
        controller.videoLater()
        XCTAssertEqual(controller.currentOffset, 14.75, accuracy: 1e-9)
        XCTAssertEqual(controller.offsetOrigin, .manual)
        XCTAssertEqual(MusicVideoOffsets(defaults: defaults).offset(for: song.key, video: "yt:\(videoID)", now: start),
                       14.75, accuracy: 1e-9, "kept")
    }

    // MARK: Offsets per video

    func testTheUsersOffsetBelongsToTheVideoItWasSetOn() {
        let offsets = MusicVideoOffsets(defaults: defaults)
        offsets.setEstimate(12, for: song.key, video: "yt:BBBBBBBBBBB")
        offsets.set(8.4, for: song.key, video: "yt:AAAAAAAAAAA")
        XCTAssertEqual(offsets.offset(for: song.key, video: "yt:AAAAAAAAAAA", now: start), 8.4)
        XCTAssertTrue(offsets.hasManual(song.key, video: "yt:AAAAAAAAAAA"))
        XCTAssertFalse(offsets.hasManual(song.key, video: "yt:BBBBBBBBBBB"))
        XCTAssertEqual(offsets.offset(for: song.key, video: "yt:BBBBBBBBBBB", now: start), 12,
                       "another upload of the song has its own intro")
        XCTAssertEqual(offsets.origin(for: song.key, video: "yt:BBBBBBBBBBB", now: start), .estimated)
        let reloaded = MusicVideoOffsets(defaults: defaults)
        XCTAssertEqual(reloaded.offset(for: song.key, video: "yt:AAAAAAAAAAA", now: start), 8.4, "kept with its video")
        XCTAssertFalse(reloaded.hasManual(song.key, video: "yt:BBBBBBBBBBB"))
        reloaded.clearManual(song.key)
        XCTAssertFalse(MusicVideoOffsets(defaults: defaults).hasManual(song.key))
        // One set before videos were told apart applies to any.
        defaults.set([song.key: 3.0], forKey: MusicVideoOffsets.key)
        let legacy = MusicVideoOffsets(defaults: defaults)
        XCTAssertEqual(legacy.offset(for: song.key, video: "yt:BBBBBBBBBBB", now: start), 3)
    }

    func testOldAudioOffsetsAreMovedOffTheLyricsLead() {
        defaults.set([song.key: ["video": "file:/a.m4v", "at": start.timeIntervalSince1970, "kind": "audio", "offset": 8.0]],
                     forKey: MusicVideoOffsets.automaticKey)
        let offsets = MusicVideoOffsets(defaults: defaults)
        XCTAssertEqual(offsets.offset(for: song.key, video: "file:/a.m4v", now: start), 8 + NowPlayingMonitor.syncLead,
                       accuracy: 1e-9, "measured against the lead, which the video's clock no longer has")
        offsets.storeAutomatic(9, origin: .cuts, for: "other", video: "yt:\(videoID)", now: start)
        let reloaded = MusicVideoOffsets(defaults: defaults)
        XCTAssertEqual(reloaded.offset(for: song.key, video: "file:/a.m4v", now: start), 8 + NowPlayingMonitor.syncLead,
                       accuracy: 1e-9, "moved once, not on every launch")
        XCTAssertEqual(reloaded.origin(for: "other", video: "yt:\(videoID)", now: start), .cuts)
    }

    // MARK: Correction

    func testASmallChangeOfOffsetIsAppliedOnYouTube() {
        var corrector = MusicVideoCorrector(style: .ledSeek)
        func playing(_ drift: Double) -> MusicVideoDrift.Sample { .init(videoTime: 100 + drift, videoPlaying: true) }
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(0.25), shouldPlay: true, now: 10, fixedLead: 0), .none,
                       "drift alone under a second is left")
        corrector.targetMoved()
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(0.25), shouldPlay: true, now: 10.25, fixedLead: 0),
                       .seek(to: 100 + MusicVideoSeekLatency.initial, thenPlay: true), "a quarter-second nudge shows")
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(0.05), shouldPlay: true, now: 12, fixedLead: 0), .none)
        corrector.targetMoved()
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(0.08), shouldPlay: true, now: 14, fixedLead: 0), .none,
                       "already within a tenth")
    }

    func testDriftThatLingersIsSoughtButNotOverAndOver() {
        var corrector = MusicVideoCorrector(style: .ledSeek)
        func playing(_ drift: Double) -> MusicVideoDrift.Sample { .init(videoTime: 100 + drift, videoPlaying: true) }
        var now = 10.0
        func check(_ drift: Double) -> MusicVideoCorrector.Action {
            now += 0.25
            return corrector.decide(target: 100, sample: playing(drift), shouldPlay: true, now: now, fixedLead: 0)
        }
        var seeks = 0
        for _ in 0..<(MusicVideoSeekHysteresis.lingerChecks - 1) { if check(0.6) != .none { seeks += 1 } }
        XCTAssertEqual(seeks, 0, "a lasting 0.6 s is not sought at once")
        if case .seek = check(0.6) {} else { XCTFail("but is once it has lasted three seconds") }
        // It keeps landing 0.6 s off: the retries, then a doubled wait.
        var actions: [MusicVideoCorrector.Action] = []
        for _ in 0..<60 { actions.append(check(0.6)) }
        let more = actions.filter { if case .seek = $0 { return true } else { return false } }.count
        XCTAssertLessThanOrEqual(more, 6, "backs off: \(more) seeks in 15 s")
        XCTAssertGreaterThanOrEqual(more, 2)
    }

    func testAVPlayerSeeksForAChangedOffsetAndEasesTheRest() {
        var corrector = MusicVideoCorrector(style: .rate)
        let sample = MusicVideoDrift.Sample(videoTime: 50.5, videoPlaying: true)
        corrector.targetMoved()
        XCTAssertEqual(corrector.decide(target: 50, sample: sample, shouldPlay: true, now: 10, fixedLead: 0.05),
                       .seek(to: 50.05, thenPlay: true))
        corrector.targetMoved()
        guard case .rate = corrector.decide(target: 50, sample: .init(videoTime: 50.25, videoPlaying: true), shouldPlay: true,
                                            now: 12, fixedLead: 0.05) else { return XCTFail("a quarter second is eased") }
    }

    func testTheLoopComparesWithTheSongHalfwayThroughTheAnswerAndRetargets() {
        final class SlowSurface: MusicVideoPlaybackSurface {
            var pending: ((MusicVideoDrift.Sample?, Double?) -> Void)?
            var seeks: [Double] = []
            var seekLead: Double { 0 }
            var correctionStyle: MusicVideoCorrectionStyle { .ledSeek }
            func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) { pending = done }
            func playVideo() {}
            func pauseVideo() {}
            func seekVideo(to seconds: Double) { seeks.append(seconds) }
        }
        var songTime = 40.0, clock = 100.0, offset = 0.0
        var drifts: [Double?] = [], readAt: [TimeInterval] = []
        let surface = SlowSurface()
        let loop = MusicVideoSyncLoop(songTime: { songTime }, songPlaying: { true }, offset: { offset }, uptime: { clock })
        loop.surface = surface
        loop.onSample = { _, drift, at in drifts.append(drift); readAt.append(at) }
        loop.tick()
        // The answer takes 0.2 s; the player read 40.1 halfway.
        songTime += 0.2
        clock += 0.2
        surface.pending?(.init(videoTime: 40.1, videoPlaying: true), 215)
        XCTAssertEqual(try XCTUnwrap(drifts.last ?? nil), 0, accuracy: 1e-9, "in step, not 0.1 s behind")
        XCTAssertEqual(readAt.last ?? 0, 100.1, accuracy: 1e-9)
        XCTAssertEqual(MusicVideoSyncLoop.midpoint(10, 30, playing: true), 30, "a jump keeps the later read")
        XCTAssertEqual(MusicVideoSyncLoop.midpoint(10, 10.2, playing: false), 10.2)
        // A quarter-second nudge is sought at once, led by the seek latency;
        // a drift that small alone never would be.
        offset = 0.25
        songTime += 0.25
        clock += 0.25
        loop.tick()
        surface.pending?(.init(videoTime: songTime, videoPlaying: true), 215)
        XCTAssertEqual(surface.seeks, [40.45 + 0.25 + MusicVideoSeekLatency.initial])
    }

    // MARK: Search

    func testLengthsOnResultsAreRead() {
        XCTAssertEqual(YouTubeSearchPage.lengthSeconds("3:45"), 225)
        XCTAssertEqual(YouTubeSearchPage.lengthSeconds("1:02:03"), 3723)
        XCTAssertEqual(YouTubeSearchPage.lengthSeconds("0:59"), 59)
        XCTAssertNil(YouTubeSearchPage.lengthSeconds("LIVE"))
        XCTAssertNil(YouTubeSearchPage.lengthSeconds("3:4x"))
        XCTAssertNil(YouTubeSearchPage.lengthSeconds("12"))
        let html = #"<script>var ytInitialData = {"contents":[{"videoRenderer":{"videoId":"dQw4w9WgXcQ","title":{"runs":[{"text":"The Weeknd - Blinding Lights (Official Video)"}]},"ownerText":{"runs":[{"text":"TheWeekndVEVO"}]},"lengthText":{"accessibility":{"accessibilityData":{"label":"4 minutes, 22 seconds"}},"simpleText":"4:22"}}}]};</script>"#
        XCTAssertEqual(YouTubeSearchPage.results(fromHTML: html).first?.duration, 262)
    }

    func testAnOfficialVideoAsLongAsTheSongComesFirst() {
        let results = [
            YouTubeSearchPage.Result(videoID: "AAAAAAAAAAA", title: "The Weeknd - Blinding Lights (Official Music Video)",
                                     channel: "TheWeekndVEVO", duration: 262),
            YouTubeSearchPage.Result(videoID: "BBBBBBBBBBB", title: "The Weeknd - Blinding Lights (Official Video)",
                                     channel: "The Weeknd", duration: 201),
            YouTubeSearchPage.Result(videoID: "CCCCCCCCCCC", title: "The Weeknd - Blinding Lights",
                                     channel: "The Weeknd", duration: 200),
        ]
        let ranked = YouTubeSearchPage.officialVideos(in: results, title: "Blinding Lights", artist: "The Weeknd",
                                                      songDuration: 200)
        XCTAssertEqual(ranked.map(\.videoID), ["BBBBBBBBBBB", "AAAAAAAAAAA", "CCCCCCCCCCC"],
                       "the same length first; a bare title (maybe the audio) gains nothing by its length")
        XCTAssertEqual(YouTubeSearchPage.officialVideos(in: results, title: "Blinding Lights", artist: "The Weeknd")
            .map(\.videoID), ["AAAAAAAAAAA", "BBBBBBBBBBB", "CCCCCCCCCCC"], "without the song's length, as before")
        XCTAssertEqual(YouTubeSearchPage.officialVideos(in: results, title: "Blinding Lights", artist: "The Weeknd",
                                                        songDuration: 230).first?.videoID, "AAAAAAAAAAA",
                       "none as long as the song: as before")
    }
}
