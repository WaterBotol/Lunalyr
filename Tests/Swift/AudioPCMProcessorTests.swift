import AudioToolbox
import CoreAudio
import XCTest
@testable import VersoCustom

/// The capture callback only fills the ring; analysis and reports come from
/// the worker. These tests drive both sides directly, without audio devices.
final class AudioPCMProcessorTests: XCTestCase {
    private let stereo = AudioStreamBasicDescription(mSampleRate: 48000, mFormatID: kAudioFormatLinearPCM,
        mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked, mBytesPerPacket: 8, mFramesPerPacket: 1,
        mBytesPerFrame: 8, mChannelsPerFrame: 2, mBitsPerChannel: 32, mReserved: 0)

    private func tone(frames: Int, start: Int = 0, amplitude: Float = 0.25) -> [Float] {
        var samples = [Float](repeating: 0, count: frames * 2)
        for frame in 0..<frames {
            let value = amplitude * Float(sin(2 * Double.pi * 440 * Double(start + frame) / 48000))
            samples[frame * 2] = value; samples[frame * 2 + 1] = -value
        }
        return samples
    }
    private func feed(_ processor: AudioPCMProcessor, _ samples: [Float], format: AudioStreamBasicDescription? = nil) {
        var samples = samples
        let format = format ?? stereo
        samples.withUnsafeMutableBytes { bytes in
            var list = AudioBufferList(mNumberBuffers: 1, mBuffers: AudioBuffer(mNumberChannels: format.mChannelsPerFrame,
                mDataByteSize: UInt32(bytes.count), mData: bytes.baseAddress))
            processor.process(&list, format: format)
        }
    }

    func testCaptureCallbackOnlyQueuesAudioForTheWorker() {
        var reports: [AudioPCMProcessor.Report] = []
        let processor = AudioPCMProcessor(target: VisualizerAudio(), deliveryQueue: nil) { reports.append($0) }
        feed(processor, tone(frames: 512))
        XCTAssertTrue(reports.isEmpty, "Analysis and reporting must not run on the capture thread.")
        XCTAssertEqual(processor.drain(now: 10), 512)
        XCTAssertEqual(reports.count, 1)
        XCTAssertEqual(reports.first?.packets, 1)
        XCTAssertEqual(reports.first?.droppedFrames, 0)
        XCTAssertEqual(reports.first?.deliveredAt, 10)
        XCTAssertEqual(reports.first?.audibleAt, 10)
        XCTAssertEqual(reports.first?.formatLabel, "48000 Hz · 2ch · 32bit float")
        XCTAssertGreaterThan(reports.first?.levels.rms ?? 0, 0.01)
        // Nothing new: no report and no work.
        XCTAssertEqual(processor.drain(now: 11), 0)
        XCTAssertEqual(reports.count, 1)
    }

    func testReportsToTheMainThreadAreThrottled() {
        var reports = 0
        let processor = AudioPCMProcessor(target: VisualizerAudio(), deliveryQueue: nil) { _ in reports += 1 }
        feed(processor, tone(frames: 256)); processor.drain(now: 10)
        feed(processor, tone(frames: 256)); processor.drain(now: 10.02)
        XCTAssertEqual(reports, 1)
        feed(processor, tone(frames: 256)); processor.drain(now: 10.1)
        XCTAssertEqual(reports, 2)
    }

    func testDigitalSilenceIsNotMistakenForAudio() {
        var report: AudioPCMProcessor.Report?
        let processor = AudioPCMProcessor(target: VisualizerAudio(), deliveryQueue: nil) { report = $0 }
        feed(processor, [Float](repeating: 0, count: 1024))
        processor.drain(now: 3)
        XCTAssertEqual(report?.packets, 1)
        XCTAssertNil(report?.audibleAt)
        // A quiet but real passage still counts as audio.
        feed(processor, tone(frames: 512, amplitude: 0.0001))
        processor.drain(now: 4)
        XCTAssertEqual(report?.audibleAt, 4)
    }

    func testRingWrapsAroundWithoutLosingAudio() {
        var last: AudioPCMProcessor.Report?
        let processor = AudioPCMProcessor(target: VisualizerAudio(), deliveryQueue: nil) { last = $0 }
        var analysed = 0, now = 0.0
        // Well over twice the ring's capacity, drained as the worker would.
        for block in 0..<300 {
            feed(processor, tone(frames: 512, start: block * 512))
            now += 1
            analysed += processor.drain(now: now)
        }
        XCTAssertEqual(analysed, 300 * 512)
        XCTAssertEqual(last?.packets, 300)
        XCTAssertEqual(last?.droppedFrames, 0)
    }

    func testAStalledWorkerDropsAndCountsInsteadOfBlockingCapture() {
        var last: AudioPCMProcessor.Report?
        let processor = AudioPCMProcessor(target: VisualizerAudio(), deliveryQueue: nil) { last = $0 }
        // The worker is stalled while the callback keeps delivering.
        for block in 0..<200 { feed(processor, tone(frames: 512, start: block * 512)) }
        let fed = 200 * 512, capacity = AudioPCMProcessor.ringCapacity
        // Only the newest backlog is analysed once the worker resumes.
        XCTAssertEqual(processor.drain(now: 1), AudioPCMProcessor.maximumBacklog)
        XCTAssertEqual(last?.packets, 200)
        XCTAssertEqual(last?.droppedFrames, fed - AudioPCMProcessor.maximumBacklog)
        XCTAssertEqual(fed - capacity + (capacity - AudioPCMProcessor.maximumBacklog), last?.droppedFrames)
        // Normal flow resumes afterwards.
        feed(processor, tone(frames: 512))
        XCTAssertEqual(processor.drain(now: 2), 512)
    }

    func testWorkerSkipsStaleAudioOnResumeThenReportsOnMain() {
        var reports = 0
        let processor = AudioPCMProcessor(target: VisualizerAudio(), deliveryQueue: .main) { _ in
            XCTAssertTrue(Thread.isMainThread)
            reports += 1
        }
        // Audio left from before a suspension is stale, not replayed.
        feed(processor, tone(frames: 512))
        processor.startAnalysis()
        defer { processor.stopAnalysis() }
        let settled = expectation(description: "worker settled")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { settled.fulfill() }
        wait(for: [settled], timeout: 2)
        XCTAssertEqual(reports, 0)
        // New audio is drained by the worker's own cadence.
        feed(processor, tone(frames: 512))
        let deadline = Date().addingTimeInterval(3)
        while reports == 0 && Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.02)) }
        XCTAssertEqual(reports, 1)
    }

    func testMonoAndUnsupportedInput() {
        var report: AudioPCMProcessor.Report?
        let processor = AudioPCMProcessor(target: VisualizerAudio(), deliveryQueue: nil) { report = $0 }
        var mono = stereo
        mono.mChannelsPerFrame = 1; mono.mBytesPerFrame = 4; mono.mBytesPerPacket = 4
        feed(processor, Array(tone(frames: 256).prefix(256)), format: mono)
        processor.drain(now: 1)
        XCTAssertEqual(report?.formatLabel, "48000 Hz · 1ch · 32bit float")
        XCTAssertNil(report?.problem)
        var bigEndian = stereo
        bigEndian.mFormatFlags |= kAudioFormatFlagIsBigEndian
        feed(processor, tone(frames: 256), format: bigEndian)
        processor.drain(now: 2)
        XCTAssertEqual(report?.problem, "This audio format isn’t supported. Try the other capture method.")
        // Reported once, not on every callback.
        feed(processor, tone(frames: 256), format: bigEndian)
        processor.drain(now: 3)
        XCTAssertEqual(report?.deliveredAt, 2)
    }

    func testVisualiserReceivesTheDrainedAudio() {
        let target = VisualizerAudio()
        let processor = AudioPCMProcessor(target: target, deliveryQueue: nil) { _ in }
        for block in 0..<16 {
            feed(processor, tone(frames: 512, start: block * 512))
            processor.drain(now: Double(block))
        }
        let snapshot = target.latest()
        XCTAssertGreaterThan(snapshot.level, 0)
        XCTAssertGreaterThan(snapshot.bands.max() ?? 0, 0)
    }

    func testTapIOProcTurnsEveryOutputStreamOff() throws {
        guard #available(macOS 14.2, *) else { throw XCTSkip("Process taps need macOS 14.2.") }
        XCTAssertEqual(MemoryLayout<AudioHardwareIOProcStreamUsage>.offset(of: \.mStreamIsOn),
                       SystemAudioCapture.streamUsageHeader)
        let bytes = SystemAudioCapture.streamUsageHeader + 3 * 4
        let usage = UnsafeMutableRawPointer.allocate(byteCount: bytes, alignment: 8)
        defer { usage.deallocate() }
        let proc = UnsafeMutableRawPointer(bitPattern: 0xA1B2C3)!
        usage.storeBytes(of: proc, as: UnsafeMutableRawPointer.self)
        usage.storeBytes(of: UInt32(3), toByteOffset: 8, as: UInt32.self)
        for stream in 0..<3 {
            usage.storeBytes(of: UInt32(1), toByteOffset: SystemAudioCapture.streamUsageHeader + stream * 4, as: UInt32.self)
        }
        XCTAssertTrue(SystemAudioCapture.disableAllStreams(in: usage, byteCount: bytes))
        XCTAssertEqual(usage.load(as: UnsafeMutableRawPointer.self), proc)
        XCTAssertEqual(usage.load(fromByteOffset: 8, as: UInt32.self), 3)
        for stream in 0..<3 {
            XCTAssertEqual(usage.load(fromByteOffset: SystemAudioCapture.streamUsageHeader + stream * 4, as: UInt32.self), 0)
        }
        // A stream count that does not fit the record is left alone.
        usage.storeBytes(of: UInt32(9), toByteOffset: 8, as: UInt32.self)
        XCTAssertFalse(SystemAudioCapture.disableAllStreams(in: usage, byteCount: bytes))
        XCTAssertFalse(SystemAudioCapture.disableAllStreams(in: usage, byteCount: 4))
    }

    func testTapFormatChangesAreDetected() throws {
        guard #available(macOS 14.2, *) else { throw XCTSkip("Process taps need macOS 14.2.") }
        XCTAssertFalse(SystemAudioCapture.formatChanged(stereo, stereo))
        var faster = stereo; faster.mSampleRate = 96000
        XCTAssertTrue(SystemAudioCapture.formatChanged(stereo, faster))
        var wider = stereo; wider.mChannelsPerFrame = 4; wider.mBytesPerFrame = 16
        XCTAssertTrue(SystemAudioCapture.formatChanged(stereo, wider))
        var integer = stereo; integer.mFormatFlags = kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked
        XCTAssertTrue(SystemAudioCapture.formatChanged(stereo, integer))
    }
}
