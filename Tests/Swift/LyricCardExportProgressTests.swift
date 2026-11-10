import AVFoundation
import XCTest
@testable import VersoCustom

/// The card export's progress and phases, and the sound mix running off the
/// main thread. Files are written and decoded, never played.
@MainActor
final class LyricCardExportProgressTests: XCTestCase {
    private let folder = FileManager.default.temporaryDirectory.appendingPathComponent("card-progress-\(UUID().uuidString)")

    override func setUpWithError() throws {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: folder)
    }

    /// Set from any thread.
    private final class Probe: @unchecked Sendable {
        private let lock = NSLock()
        private var renderedOnMain: [Bool] = []
        func record(onMain: Bool) { lock.lock(); renderedOnMain.append(onMain); lock.unlock() }
        var calls: [Bool] { lock.lock(); defer { lock.unlock() }; return renderedOnMain }
    }

    // MARK: C4: the mix stays off the main thread and can be cancelled

    func testTheSoundIsMixedOffTheMainThread() async throws {
        let video = try await MixProbe.video(folder.appendingPathComponent("v.mp4"), seconds: 1)
        let probe = Probe()
        let samples = MixProbe.stereoSine(frequency: 440, amplitude: 0.2, seconds: 1)
        let layer = LyricCard.SoundLayer(volume: 1) { _ in
            probe.record(onMain: pthread_main_np() != 0)
            return .init(samples)
        }
        var reported: [Double] = []
        let ok = await LyricCard.mux(soundOnto: video, layers: [layer]) { reported.append($0) }
        XCTAssertTrue(ok)
        XCTAssertEqual(probe.calls, [false], "the strand is rendered and mixed off the main thread")
        XCTAssertEqual(reported, reported.sorted(), "progress only moves forward")
        XCTAssertEqual(reported.last, 1)
        let tracks = try await AVURLAsset(url: video).loadTracks(withMediaType: .audio)
        XCTAssertEqual(tracks.count, 1)
    }

    func testCancellingTheMixLeavesTheVideoUntouched() async throws {
        let video = try await MixProbe.video(folder.appendingPathComponent("v.mp4"), seconds: 1)
        let before = try Data(contentsOf: video)
        let started = Probe()
        let layer = LyricCard.SoundLayer(volume: 1) { _ in
            started.record(onMain: false)
            // A slow strand: waits until the export is cancelled.
            while !Task.isCancelled { try? await Task.sleep(nanoseconds: 5_000_000) }
            return .init([Float](repeating: 0.1, count: 96_000))
        }
        let task = Task { await LyricCard.mux(soundOnto: video, layers: [layer]) }
        for _ in 0..<200 where started.calls.isEmpty { try await Task.sleep(nanoseconds: 5_000_000) }
        task.cancel()
        let ok = await task.value
        XCTAssertFalse(ok)
        XCTAssertEqual(try Data(contentsOf: video), before)
        let leftovers = try FileManager.default.contentsOfDirectory(atPath: folder.path)
        XCTAssertEqual(leftovers, ["v.mp4"], "no half-written mix is left behind")
    }

    // MARK: C5: an honest bar

    func testFormatsShareTheBarByHowLongTheyTake() throws {
        let shares = LyricCard.progressShares([.mp4, .gif, .png], duration: 20, pages: 4, hasSound: true)
        let mp4 = try XCTUnwrap(shares[.mp4]), gif = try XCTUnwrap(shares[.gif]), png = try XCTUnwrap(shares[.png])
        XCTAssertEqual(mp4.lowerBound, 0)
        XCTAssertEqual(mp4.upperBound, gif.lowerBound)
        XCTAssertEqual(gif.upperBound, png.lowerBound)
        XCTAssertEqual(png.upperBound, 1)
        XCTAssertGreaterThan(mp4.upperBound - mp4.lowerBound, 0.5, "20 s of video is most of the work")
        XCTAssertLessThan(png.upperBound - png.lowerBound, 0.05, "one still is a sliver")
        XCTAssertEqual(LyricCard.progressShares([.png], duration: 3, pages: 1, hasSound: false)[.png], 0...1)
        XCTAssertTrue(LyricCard.progressShares([], duration: 3, pages: 1, hasSound: false).isEmpty)
    }

    func testTheExportSaysWhatItIsDoingAndOnlyMovesForward() async throws {
        var phases: [LyricCard.ExportPhase] = []
        var progress: [Double] = []
        var content = MixProbe.content()
        content.extraSounds = [LyricCard.SoundTrack(url: try MixProbe.tone(folder.appendingPathComponent("t.wav"),
                                                                            frequency: 330, amplitude: 0.3, seconds: 2))]
        let files = await LyricCard.export(content, formats: [.mp4, .gif, .png], to: folder.appendingPathComponent("out"),
                                           phase: { phases.append($0) }, progress: { progress.append($0) })
        XCTAssertEqual(files.map(\.0), [.mp4, .gif, .png])
        XCTAssertEqual(phases, [.drawing(.mp4), .mixingSound, .drawing(.gif), .encodingGIF, .drawing(.png)])
        XCTAssertEqual(progress, progress.sorted(), "the bar never goes back")
        XCTAssertEqual(progress.last ?? 0, 1, accuracy: 1e-9)
        XCTAssertGreaterThan(Set(progress.map { ($0 * 20).rounded() }).count, 8, "the bar moves through the GIF too")
    }
}
