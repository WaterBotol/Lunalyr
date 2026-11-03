import AudioToolbox
import XCTest
@testable import VersoCustom

final class AudioInputFormatTests: XCTestCase {
    private func format(bits: UInt32, frameBytes: UInt32, flags: AudioFormatFlags) -> AudioStreamBasicDescription {
        AudioStreamBasicDescription(mSampleRate: 48000, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: flags, mBytesPerPacket: frameBytes, mFramesPerPacket: 1,
            mBytesPerFrame: frameBytes, mChannelsPerFrame: 2, mBitsPerChannel: bits, mReserved: 0)
    }
    func testFloat32PlanarAndFloat64InterleavedAreRecognised() {
        let planar = format(bits: 32, frameBytes: 4, flags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved)
        let interleaved = format(bits: 64, frameBytes: 16, flags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked)
        XCTAssertEqual(AudioPCMProcessor.sampleFormat(planar)?.rawValue, VC_PCM_FLOAT32.rawValue)
        XCTAssertEqual(AudioPCMProcessor.sampleFormat(interleaved)?.rawValue, VC_PCM_FLOAT64.rawValue)
    }
    func testSignedIntegerPCMIsRecognised() {
        let value = format(bits: 16, frameBytes: 4, flags: kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked)
        XCTAssertEqual(AudioPCMProcessor.sampleFormat(value)?.rawValue, VC_PCM_INT16.rawValue)
    }
    func testUnsupportedOrPaddedFormatsAreNotSilentlyDecodedAsFloat32() {
        XCTAssertNil(AudioPCMProcessor.sampleFormat(format(bits: 16, frameBytes: 4, flags: 0)))
        XCTAssertNil(AudioPCMProcessor.sampleFormat(format(bits: 32, frameBytes: 8, flags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsBigEndian)))
        XCTAssertNil(AudioPCMProcessor.sampleFormat(format(bits: 24, frameBytes: 8, flags: kAudioFormatFlagIsSignedInteger)))
        XCTAssertNil(AudioPCMProcessor.sampleFormat(format(bits: 32, frameBytes: 0, flags: kAudioFormatFlagIsFloat)))
    }
}
