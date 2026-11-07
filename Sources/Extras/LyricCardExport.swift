import AppKit
import AVFoundation
import CoreVideo
import ImageIO
import SwiftUI
import UniformTypeIdentifiers

// MARK: - Lyric cards as MP4, GIF and PNG
//
//     let files = try await LyricCard.export(content, formats: [.mp4, .png], to: folder) { progress in … }
//     LyricCard.publish(files, to: .general)       // pastes as attachments
//
// MP4 streams its frames to the encoder as they are drawn, 30 a second, so a
// whole song costs no more memory than one line. PNG is one crisp still of
// all the selected lines, drawn at twice the card's size.

extension LyricCard {
    static let videoFramesPerSecond = 30.0

    /// One crisp PNG of the selected lines together.
    static func stillPNG(_ content: Content, scale: CGFloat = 2) -> Data? {
        var still = content
        still.pages = [content.stillPage]
        let renderer = ImageRenderer(content: LyricCardView(content: still, ink: ink(for: still)))
        renderer.scale = scale
        guard let image = renderer.cgImage else { return nil }
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, image, nil)
        return CGImageDestinationFinalize(destination) ? data as Data : nil
    }

    /// An H.264 video of the card's animation at its full size.
    static func video(_ content: Content, to url: URL, progress: ((Double) -> Void)? = nil) async -> Bool {
        let pixels = CGSize(width: content.size.width, height: content.size.height)
        guard let writer = try? AVAssetWriter(outputURL: url, fileType: .mp4) else { return false }
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: Int(pixels.width),
            AVVideoHeightKey: Int(pixels.height),
            AVVideoColorPropertiesKey: [
                AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2,
                AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2,
                AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2,
            ],
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: 9_000_000,
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
                AVVideoMaxKeyFrameIntervalKey: 60,
            ],
        ])
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: Int(pixels.width),
            kCVPixelBufferHeightKey as String: Int(pixels.height),
        ])
        guard writer.canAdd(input) else { print("LYRIC-MP4 cannot add input"); return false }
        writer.add(input)
        guard writer.startWriting() else { print("LYRIC-MP4 start failed: \(String(describing: writer.error))"); return false }
        writer.startSession(atSourceTime: .zero)

        var time = 0.0
        let ok = await renderFrames(content, scale: 1, minimumCadence: 1 / videoFramesPerSecond, budget: nil,
                                    progress: progress) { image, delay in
            guard let buffer = pixelBuffer(from: image, size: pixels, pool: adaptor.pixelBufferPool) else {
                print("LYRIC-MP4 no pixel buffer")
                return false
            }
            while !input.isReadyForMoreMediaData {
                if Task.isCancelled { return false }
                try? await Task.sleep(nanoseconds: 4_000_000)
            }
            guard adaptor.append(buffer, withPresentationTime: CMTime(seconds: time, preferredTimescale: 600)) else {
                print("LYRIC-MP4 append failed at \(time): \(String(describing: writer.error))")
                return false
            }
            time += delay
            return true
        }
        guard ok, !Task.isCancelled else {
            print("LYRIC-MP4 frames ok=\(ok) cancelled=\(Task.isCancelled) status=\(writer.status.rawValue) error=\(String(describing: writer.error))")
            writer.cancelWriting()
            try? FileManager.default.removeItem(at: url)
            return false
        }
        input.markAsFinished()
        writer.endSession(atSourceTime: CMTime(seconds: time, preferredTimescale: 600))
        await writer.finishWriting()
        if writer.status != .completed { print("LYRIC-MP4 finish: \(writer.status.rawValue) \(String(describing: writer.error))") }
        return writer.status == .completed
    }

    private static func pixelBuffer(from image: CGImage, size: CGSize, pool: CVPixelBufferPool?) -> CVPixelBuffer? {
        var buffer: CVPixelBuffer?
        if let pool {
            CVPixelBufferPoolCreatePixelBuffer(nil, pool, &buffer)
        } else {
            CVPixelBufferCreate(nil, Int(size.width), Int(size.height), kCVPixelFormatType_32BGRA, nil, &buffer)
        }
        guard let buffer else { return nil }
        CVPixelBufferLockBaseAddress(buffer, [])
        defer { CVPixelBufferUnlockBaseAddress(buffer, []) }
        guard let context = CGContext(data: CVPixelBufferGetBaseAddress(buffer), width: Int(size.width), height: Int(size.height),
                                      bitsPerComponent: 8, bytesPerRow: CVPixelBufferGetBytesPerRow(buffer),
                                      space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        else { return nil }
        context.draw(image, in: CGRect(origin: .zero, size: size))
        return buffer
    }

    /// Makes each chosen format, writing them to `directory`. Returns the files made.
    static func export(_ content: Content, formats: Set<CardFormat>, to directory: URL,
                       progress: @escaping (Double) -> Void = { _ in }) async -> [(CardFormat, URL)] {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
        let order = CardFormat.allCases.filter(formats.contains)
        var made: [(CardFormat, URL)] = []
        for (index, format) in order.enumerated() {
            guard !Task.isCancelled else { break }
            let file = directory.appendingPathComponent("Lunalyr Lyric Card.\(format.rawValue)")
            let share: (Double) -> Void = { progress((Double(index) + $0) / Double(order.count)) }
            switch format {
            case .png:
                if let data = stillPNG(content), (try? data.write(to: file, options: .atomic)) != nil { made.append((format, file)) }
            case .gif:
                if let data = await gif(content), (try? data.write(to: file, options: .atomic)) != nil { made.append((format, file)) }
            case .mp4:
                if await video(content, to: file, progress: share) { made.append((format, file)) }
            }
            share(1)
        }
        return made
    }

    /// Puts the files on the pasteboard as separate attachments (a PNG also
    /// as image data, so apps that paste pictures take it too).
    @discardableResult
    static func publish(_ files: [(CardFormat, URL)], to pasteboard: NSPasteboard) -> Bool {
        guard !files.isEmpty else { return false }
        let items = files.compactMap { format, url -> NSPasteboardItem? in
            let item = NSPasteboardItem()
            guard item.setString(url.absoluteString, forType: .fileURL) else { return nil }
            if format == .png, let data = try? Data(contentsOf: url) { item.setData(data, forType: .png) }
            return item
        }
        guard !items.isEmpty else { return false }
        pasteboard.clearContents()
        return pasteboard.writeObjects(items)
    }

    static var exportDirectory: URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("Lunalyr Lyric Cards", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
    }
}
