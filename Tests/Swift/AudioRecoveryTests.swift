import AudioToolbox
import Darwin
import XCTest
@testable import VersoCustom

final class AudioRecoveryTests: XCTestCase {
    func testAutomaticRecoversMissingOrStalledCallbacksAfterGracePeriod() {
        func recover(_ age: Double, _ packets: Int, _ packetAge: Double) -> Bool {
            AudioCaptureRecovery.shouldUseCompatibility(method: .automatic, usingTap: true,
                elapsed: age, packets: packets, packetAge: packetAge, quietFor: 0, playing: true)
        }
        XCTAssertFalse(recover(3.9, 0, 0))
        XCTAssertTrue(recover(4, 0, 0))
        XCTAssertTrue(recover(10, 120, 3))
        XCTAssertFalse(recover(10, 120, 0.1))
    }
    func testSilenceRecoveryRequiresPlaybackAndRespectsExplicitMethod() {
        for method in AudioCaptureMethod.allCases {
            let value = AudioCaptureRecovery.shouldUseCompatibility(method: method, usingTap: true,
                elapsed: 15, packets: 300, packetAge: 0, quietFor: 8, playing: true)
            XCTAssertEqual(value, method == .automatic)
        }
        XCTAssertFalse(AudioCaptureRecovery.shouldUseCompatibility(method: .automatic, usingTap: true,
            elapsed: 15, packets: 300, packetAge: 0, quietFor: 8, playing: false))
    }
    func testCompatibilityDoesNotEnterRecoveryLoop() {
        XCTAssertFalse(AudioCaptureRecovery.shouldUseCompatibility(method: .automatic, usingTap: false,
            elapsed: 60, packets: 0, packetAge: 60, quietFor: 60, playing: true))
    }
    func testTapTailExcludesPhysicalInputBuffers() {
        var physical = [Float](repeating: 0.5, count: 960)
        var tap = [Float](repeating: 0, count: 960)
        let format = AudioStreamBasicDescription(mSampleRate: 48000, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
            mBytesPerPacket: 8, mFramesPerPacket: 1, mBytesPerFrame: 8,
            mChannelsPerFrame: 2, mBitsPerChannel: 32, mReserved: 0)
        var delivered: AudioPCMProcessor.Report?
        let processor = AudioPCMProcessor(target: VisualizerAudio(), deliveryQueue: nil) { delivered = $0 }
        physical.withUnsafeMutableBytes { input in
            tap.withUnsafeMutableBytes { output in
                let list = AudioBufferList.allocate(maximumBuffers: 2)
                defer { free(list.unsafeMutablePointer) }
                list.unsafeMutablePointer.pointee.mNumberBuffers = 2
                list[0] = AudioBuffer(mNumberChannels: 2, mDataByteSize: UInt32(input.count), mData: input.baseAddress)
                list[1] = AudioBuffer(mNumberChannels: 2, mDataByteSize: UInt32(output.count), mData: output.baseAddress)
                processor.process(list.unsafePointer, format: format, lastChannelsOnly: true)
            }
        }
        processor.drain(now: 1)
        XCTAssertNotNil(delivered)
        XCTAssertNil(delivered?.problem)
        XCTAssertEqual(delivered?.levels.rms, 0)
        // The loud physical input never reached the analysers.
        XCTAssertNil(delivered?.audibleAt)
    }
    func testTapTailReadsTrailingChannelsInAnInterleavedBuffer() {
        var samples = (0..<1920).map { Float($0 % 4 < 2 ? 0 : 0.25) }
        let format = AudioStreamBasicDescription(mSampleRate: 48000, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
            mBytesPerPacket: 8, mFramesPerPacket: 1, mBytesPerFrame: 8,
            mChannelsPerFrame: 2, mBitsPerChannel: 32, mReserved: 0)
        var delivered: AudioPCMProcessor.Report?
        let processor = AudioPCMProcessor(target: VisualizerAudio(), deliveryQueue: nil) { delivered = $0 }
        samples.withUnsafeMutableBytes { bytes in
            var list = AudioBufferList(mNumberBuffers: 1,
                mBuffers: AudioBuffer(mNumberChannels: 4, mDataByteSize: UInt32(bytes.count), mData: bytes.baseAddress))
            processor.process(&list, format: format, lastChannelsOnly: true)
        }
        processor.drain(now: 1)
        XCTAssertNil(delivered?.problem)
        XCTAssertGreaterThan(delivered?.levels.rms ?? 0, 0.03)
        XCTAssertEqual(delivered?.formatLabel, "48000 Hz · 2ch · 32bit float")
    }
    func testNoAutomaticFallbackWhileAUserStartMayAwaitAPermissionPrompt() {
        func recover(sinceUserStart: Double?, packets: Int = 300, quietFor: Double = 9) -> Bool {
            AudioCaptureRecovery.shouldUseCompatibility(method: .automatic, usingTap: true, elapsed: 20,
                packets: packets, packetAge: 0, quietFor: quietFor, playing: true, sinceUserStart: sinceUserStart)
        }
        // Silence is what a tap delivers while its permission prompt is open.
        XCTAssertFalse(recover(sinceUserStart: 8))
        XCTAssertFalse(recover(sinceUserStart: 59.9, packets: 0))
        XCTAssertTrue(recover(sinceUserStart: 60))
        XCTAssertTrue(recover(sinceUserStart: nil))
    }
    func testAutomaticFallbackNeverAsksForANewPermission() {
        var asked = 0
        func allowed(_ value: Bool) -> Bool { asked += 1; return value }
        XCTAssertFalse(AudioCaptureRecovery.shouldUseCompatibility(method: .automatic, usingTap: true,
            elapsed: 20, packets: 0, packetAge: 0, quietFor: 0, playing: true, screenCaptureAllowed: allowed(false)))
        XCTAssertEqual(asked, 1)
        XCTAssertTrue(AudioCaptureRecovery.shouldUseCompatibility(method: .automatic, usingTap: true,
            elapsed: 20, packets: 0, packetAge: 0, quietFor: 0, playing: true, screenCaptureAllowed: allowed(true)))
        // Healthy capture, explicit methods and the grace period do not even
        // check Screen Recording access.
        _ = AudioCaptureRecovery.shouldUseCompatibility(method: .automatic, usingTap: true,
            elapsed: 20, packets: 300, packetAge: 0, quietFor: 0, playing: true, screenCaptureAllowed: allowed(true))
        _ = AudioCaptureRecovery.shouldUseCompatibility(method: .systemAudio, usingTap: true,
            elapsed: 20, packets: 0, packetAge: 0, quietFor: 0, playing: true, screenCaptureAllowed: allowed(true))
        _ = AudioCaptureRecovery.shouldUseCompatibility(method: .automatic, usingTap: true, elapsed: 20,
            packets: 0, packetAge: 0, quietFor: 0, playing: true, sinceUserStart: 5, screenCaptureAllowed: allowed(true))
        XCTAssertEqual(asked, 2)
    }
    func testHealthReportsMissingAudioOnlyWhenItShouldArrive() {
        func health(elapsed: Double = 20, packets: Int = 300, packetAge: Double = 0, silentFor: Double = 0,
                    quiet: Bool = false, playing: Bool = true, output: Bool = true) -> AudioCaptureHealth {
            AudioCaptureHealth.evaluate(elapsed: elapsed, packets: packets, packetAge: packetAge,
                digitalSilenceFor: silentFor, quiet: quiet, musicPlaying: playing, outputActive: output)
        }
        XCTAssertEqual(health(elapsed: 2, packets: 0), .waiting)
        XCTAssertEqual(health(elapsed: 4, packets: 0), .noSamples)
        XCTAssertEqual(health(packetAge: 2), .stalled)
        // With nothing playing, an absent or paused stream is not a fault.
        XCTAssertEqual(health(elapsed: 30, packets: 0, playing: false, output: false), .waiting)
        XCTAssertEqual(health(packetAge: 5, quiet: true, playing: false, output: false), .quiet)
        XCTAssertEqual(health(), .receiving)
        // Exact digital silence while Music plays: permission withdrawn.
        XCTAssertEqual(health(silentFor: 8, quiet: true), .notReceiving)
        // A short rest, or nothing playing, is simply quiet.
        XCTAssertEqual(health(silentFor: 5, quiet: true), .quiet)
        XCTAssertEqual(health(silentFor: 60, quiet: true, playing: false), .quiet)
        XCTAssertTrue(AudioCaptureHealth.notReceiving.needsAttention)
        XCTAssertTrue(AudioCaptureHealth.noSamples.needsAttention)
        XCTAssertFalse(AudioCaptureHealth.quiet.needsAttention)
    }
}
