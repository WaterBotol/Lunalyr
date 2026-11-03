import CoreMedia
import Foundation
import ScreenCaptureKit

/// Compatibility path for macOS 14.0/14.1 or devices that do not expose taps.
final class ScreenAudioCapture: NSObject, AudioCaptureSession, SCStreamOutput, SCStreamDelegate {
    private var stream: SCStream?
    private let processor: AudioPCMProcessor
    private let onError: (Error) -> Void
    init(processor: AudioPCMProcessor, onError: @escaping (Error) -> Void) {
        self.processor = processor; self.onError = onError
    }
    func start() async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else { throw LyricHTTP.Failure(message: "No display is available for compatibility capture.") }
        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
        let config = SCStreamConfiguration()
        config.capturesAudio = true; config.excludesCurrentProcessAudio = true
        config.sampleRate = 48000; config.channelCount = 2
        config.showsCursor = false
        if #available(macOS 15.0, *) { config.captureMicrophone = false }
        config.width = 16; config.height = 16
        config.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        config.queueDepth = 3
        let capture = SCStream(filter: filter, configuration: config, delegate: self)
        // Consume/discard the tiny 1 Hz display output as well. Some macOS
        // versions do not reliably start an audio-only display stream.
        try capture.addStreamOutput(self, type: .screen, sampleHandlerQueue: processor.queue)
        try capture.addStreamOutput(self, type: .audio, sampleHandlerQueue: processor.queue)
        stream = capture
        try await capture.startCapture()
    }
    func stream(_ stream: SCStream, didStopWithError error: Error) { onError(error) }
    func stream(_ stream: SCStream, didOutputSampleBuffer buffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, buffer.isValid,
              let description = CMSampleBufferGetFormatDescription(buffer),
              let format = CMAudioFormatDescriptionGetStreamBasicDescription(description) else { return }
        try? buffer.withAudioBufferList { list, _ in processor.process(list.unsafePointer, format: format.pointee) }
    }
    func stop() {
        let old = stream; stream = nil
        Task { try? await old?.stopCapture() }
    }
}
