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
        var delivered: VCAudioLevels?
        let processor = AudioPCMProcessor(onLevels: { value, _ in delivered = value }, onProblem: { XCTFail($0) })
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
        XCTAssertNotNil(delivered)
        XCTAssertEqual(delivered?.rms, 0)
    }
    func testTapTailReadsTrailingChannelsInAnInterleavedBuffer() {
        var samples = (0..<1920).map { Float($0 % 4 < 2 ? 0 : 0.25) }
        let format = AudioStreamBasicDescription(mSampleRate: 48000, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
            mBytesPerPacket: 8, mFramesPerPacket: 1, mBytesPerFrame: 8,
            mChannelsPerFrame: 2, mBitsPerChannel: 32, mReserved: 0)
        var delivered: VCAudioLevels?
        let processor = AudioPCMProcessor(onLevels: { value, _ in delivered = value }, onProblem: { XCTFail($0) })
        samples.withUnsafeMutableBytes { bytes in
            var list = AudioBufferList(mNumberBuffers: 1,
                mBuffers: AudioBuffer(mNumberChannels: 4, mDataByteSize: UInt32(bytes.count), mData: bytes.baseAddress))
            processor.process(&list, format: format, lastChannelsOnly: true)
        }
        XCTAssertGreaterThan(delivered?.rms ?? 0, 0.03)
    }
}
