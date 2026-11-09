import XCTest
@testable import VersoCustom

// Drift correction: rate nudges on AVPlayer, led and rare seeks on YouTube.
// Pure decisions and a fake surface; nothing plays.

@MainActor
final class MusicVideoCorrectionTests: XCTestCase {
    // MARK: Rate nudges (AVPlayer)

    func testRateNudgeEasesSmallDriftWithHysteresis() {
        var nudge = MusicVideoRateNudge()
        XCTAssertEqual(nudge.rate(forDrift: 0.08), 1, "within step")
        XCTAssertEqual(nudge.rate(forDrift: 0.3), 0.97, accuracy: 0.0001)
        XCTAssertLessThan(nudge.rate(forDrift: 0.3), 1, "ahead: slower")
        XCTAssertEqual(nudge.rate(forDrift: 1.2), 0.97, accuracy: 0.0001, "capped")
        XCTAssertEqual(nudge.rate(forDrift: -1.2), 1.03, accuracy: 0.0001, "behind: faster, capped")
        XCTAssertLessThan(nudge.rate(forDrift: 0.08), 1, "keeps easing until well within step")
        XCTAssertEqual(nudge.rate(forDrift: 0.02), 1)
        XCTAssertEqual(nudge.rate(forDrift: 0.1), 1, "and does not start again for a little drift")
    }

    func testAVPlayerDriftIsEasedAndOnlyJumpsAreSought() {
        var corrector = MusicVideoCorrector(style: .rate)
        let playing = MusicVideoDrift.Sample(videoTime: 50.5, videoPlaying: true)
        let eased = corrector.decide(target: 50, sample: playing, shouldPlay: true, now: 10, fixedLead: 0.05)
        guard case .rate(let rate) = eased else { return XCTFail("eased, not sought: \(eased)") }
        XCTAssertLessThan(rate, 1)
        XCTAssertGreaterThanOrEqual(rate, 0.97)
        XCTAssertEqual(corrector.decide(target: 50, sample: playing, shouldPlay: true, now: 10.25, fixedLead: 0.05), .none,
                       "the same rate is not set again")
        XCTAssertEqual(corrector.decide(target: 50, sample: .init(videoTime: 50.01, videoPlaying: true), shouldPlay: true,
                                        now: 10.5, fixedLead: 0.05), .rate(1))
        XCTAssertEqual(corrector.decide(target: 50, sample: .init(videoTime: 51.4, videoPlaying: true), shouldPlay: true,
                                        now: 10.75, fixedLead: 0.05), .rate(0.97), "1.4 s is still eased")
        XCTAssertEqual(corrector.decide(target: 80, sample: .init(videoTime: 51.4, videoPlaying: true), shouldPlay: true,
                                        now: 11, fixedLead: 0.05), .seek(to: 80.05, thenPlay: true), "a jump is sought")
        XCTAssertEqual(corrector.rate, 1)
        XCTAssertEqual(corrector.decide(target: 80, sample: .init(videoTime: 80, videoPlaying: true), shouldPlay: false,
                                        now: 13, fixedLead: 0.05), .pause)
    }

    func testSyncLoopSetsTheRateOnAVPlayer() {
        final class RateSurface: MusicVideoPlaybackSurface {
            var time = 30.6, playing = true
            var rates: [Double] = [], seeks: [Double] = []
            var seekLead: Double { 0.05 }
            var correctionStyle: MusicVideoCorrectionStyle { .rate }
            func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) {
                done(.init(videoTime: time, videoPlaying: playing), 215)
            }
            func playVideo() { playing = true }
            func pauseVideo() { playing = false }
            func seekVideo(to seconds: Double) { seeks.append(seconds); time = seconds }
            func setVideoRate(_ rate: Double) { rates.append(rate) }
        }
        let surface = RateSurface()
        var durations: [Double] = []
        let loop = MusicVideoSyncLoop(songTime: { 30 }, songPlaying: { true }, offset: { 0 }, uptime: { 100 })
        loop.surface = surface
        loop.onDuration = { durations.append($0) }
        loop.tick()
        XCTAssertTrue(surface.seeks.isEmpty)
        XCTAssertEqual(surface.rates.count, 1)
        XCTAssertLessThan(surface.rates[0], 1)
        XCTAssertEqual(durations, [215])
    }

    // MARK: YouTube: led seeks with hysteresis

    func testYouTubeSeeksRarelyWithHysteresis() {
        var corrector = MusicVideoCorrector(style: .ledSeek)
        func playing(_ drift: Double) -> MusicVideoDrift.Sample { .init(videoTime: 100 + drift, videoPlaying: true) }
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(0.8), shouldPlay: true, now: 10, fixedLead: 0), .none,
                       "under a second is left alone")
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(1.2), shouldPlay: true, now: 10.25, fixedLead: 0), .none,
                       "one check over is not enough")
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(1.2), shouldPlay: true, now: 10.5, fixedLead: 0),
                       .seek(to: 100.25, thenPlay: true), "two in a row: seek, led by the initial latency")
        // It lands 0.35 s behind: the latency was 0.6 s. Not settled yet.
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(-0.35), shouldPlay: true, now: 11, fixedLead: 0), .none)
        XCTAssertEqual(corrector.latency.lead, 0.6, accuracy: 0.0001)
        // Settled and still 0.35 off: past the release band, so once more.
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(-0.35), shouldPlay: true, now: 12, fixedLead: 0),
                       .seek(to: 100.6, thenPlay: true), "led by the measured latency")
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(0.1), shouldPlay: true, now: 14, fixedLead: 0), .none)
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(0.5), shouldPlay: true, now: 15, fixedLead: 0), .none,
                       "released: half a second is left alone again")
        XCTAssertEqual(corrector.decide(target: 100, sample: playing(4), shouldPlay: true, now: 16, fixedLead: 0),
                       .seek(to: 100 + corrector.latency.lead, thenPlay: true), "a scrub is sought at once")
    }

    func testSeekLatencyIsAMedianOfLandings() {
        var latency = MusicVideoSeekLatency()
        XCTAssertEqual(latency.lead, MusicVideoSeekLatency.initial)
        latency.record(lead: 0.25, residual: -0.25)
        latency.record(lead: 0.5, residual: 0.1)
        latency.record(lead: 0.4, residual: -0.1)
        XCTAssertEqual(latency.lead, 0.5, accuracy: 0.0001)
        latency.record(lead: 0.5, residual: -30)
        XCTAssertEqual(latency.samples.count, 3, "a scrub during the seek is not latency")
        for _ in 0..<10 { latency.record(lead: 0.3, residual: 0) }
        XCTAssertEqual(latency.samples.count, MusicVideoSeekLatency.kept)
        XCTAssertEqual(latency.lead, 0.3, accuracy: 0.0001)
    }
}
