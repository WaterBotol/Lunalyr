import AppKit
import AVFoundation
import CoreVideo
import SwiftUI
import XCTest
@testable import VersoCustom

/// PREVIEW renders of the lively action words, written only when
/// LUNALYR_ACTION_PREVIEW is set (pass TEST_RUNNER_LUNALYR_ACTION_PREVIEW=1);
/// skipped otherwise.
///
/// For each action: `action-<name>.mp4`, a Cutout line (the user's layout,
/// drawn through `LyricLineView`) with the action word held 1.2 s, today's
/// effect on the left and the lively one at the default amount on the
/// right; `intensity.mp4` (Subtle / Middle / Wild for a few actions);
/// `all-actions.mp4` (every kept action back to back); a keyframe PNG per
/// action; dropped brainstorm actions under `dropped/`. 1280×720, 60 fps
/// (the app draws at the display's rate), H.264, no audio track. Nothing is
/// played.
///
/// Optional: LUNALYR_ACTION_PREVIEW_DIR (output folder),
/// LUNALYR_ACTION_PREVIEW_ONLY ("hop,spin"), LUNALYR_ACTION_PREVIEW_SHEET=1
/// (contact sheets only, no video: quick to iterate on),
/// LUNALYR_ACTION_PREVIEW_KEEP_DROPPED=1 (leave `dropped/` as it is).
@MainActor
final class ActionWordsPreviewTests: XCTestCase {
    static let canvas = CGSize(width: 1280, height: 720)
    static let fps = 60.0
    static let hold = 1.2

    struct Clip {
        let action: WordAction
        let texts: [String]
        let hue: Double
    }

    static let clips: [Clip] = [
        Clip(action: .spin, texts: ["round", "and", "round", "we", "spin", "tonight"], hue: 0.72),
        Clip(action: .float, texts: ["watch", "me", "float", "above", "the", "city"], hue: 0.6),
        Clip(action: .dip, texts: ["and", "I", "fall", "right", "into", "you"], hue: 0.55),
        Clip(action: .shake, texts: ["my", "hands", "still", "shake", "for", "you"], hue: 0.02),
        Clip(action: .hop, texts: ["we", "jump", "into", "the", "summer"], hue: 0.12),
        Clip(action: .grow, texts: ["feel", "it", "grow", "inside", "me"], hue: 0.36),
        Clip(action: .shrink, texts: ["the", "walls", "begin", "to", "shrink"], hue: 0.8),
        Clip(action: .dissolve, texts: ["watch", "it", "all", "fade", "away"], hue: 0.66),
        Clip(action: .flicker, texts: ["let", "it", "burn", "all", "night"], hue: 0.04),
        Clip(action: .frost, texts: ["my", "heart", "is", "frozen", "still"], hue: 0.56),
        Clip(action: .bleed, texts: ["I", "would", "bleed", "for", "you"], hue: 0.95),
        Clip(action: .dark, texts: ["lost", "inside", "the", "dark", "again"], hue: 0.7),
        Clip(action: .shatter, texts: ["this", "heart", "is", "broken", "now"], hue: 0.88),
        Clip(action: .melt, texts: ["I", "melt", "when", "you're", "near"], hue: 0.08),
        Clip(action: .echo, texts: ["your", "name", "will", "echo", "on"], hue: 0.62),
        Clip(action: .pulse, texts: ["feel", "my", "heartbeat", "race"], hue: 0.97),
        Clip(action: .wave, texts: ["we", "ride", "the", "waves", "tonight"], hue: 0.53),
        Clip(action: .sink, texts: ["I", "sink", "beneath", "the", "tide"], hue: 0.58),
        Clip(action: .explode, texts: ["watch", "it", "all", "explode"], hue: 0.06),
        Clip(action: .scream, texts: ["I", "scream", "your", "name"], hue: 0.0),
        Clip(action: .whisper, texts: ["you", "whisper", "in", "my", "ear"], hue: 0.75),
        Clip(action: .ghost, texts: ["a", "ghost", "inside", "my", "room"], hue: 0.68),
        Clip(action: .glow, texts: ["you", "glow", "like", "summer"], hue: 0.14),
        Clip(action: .electric, texts: ["this", "electric", "feeling"], hue: 0.6),
    ]

    /// A symbol on each action word, so the reel shows the symbol's
    /// entrance (today a fade; lively, a pop echoing the action).
    static let symbols: [String: String] = [
        "spin": "tornado", "float": "cloud.fill", "fall": "arrow.down", "shake": "hand.raised.fill",
        "jump": "hare.fill", "grow": "leaf.fill", "shrink": "arrow.down.right.and.arrow.up.left",
        "fade": "sparkles", "burn": "flame.fill", "frozen": "snowflake", "bleed": "drop.fill",
        "dark": "moon.fill", "broken": "bolt.fill", "melt": "flame", "echo": "speaker.wave.2.fill",
        "heartbeat": "heart.fill", "waves": "water.waves", "sink": "arrow.down.to.line",
    ]

    static var actionList: [String: WordAction] {
        Dictionary((WordAction.defaultList + WordAction.droppedList).map { ($0.word, $0.action) },
                   uniquingKeysWith: { first, _ in first })
    }

    /// The line: quick words, the action word held `hold`, then the rest.
    static func words(_ clip: Clip) -> (words: [LyricsFetcher.Word], onset: Double) {
        var result: [LyricsFetcher.Word] = []
        var time = 10.0
        var onset = 0.0
        let actions = actionList
        var found = false
        for text in clip.texts {
            let isAction = !found && LyricWordIcons.match(text, in: actions)?.value == clip.action
            if isAction { found = true; onset = time }
            let length = isAction ? hold : 0.3
            result.append(LyricsFetcher.Word(text: text, start: time, end: time + length))
            time += isAction ? hold + 0.05 : 0.34
        }
        precondition(found, "no \(clip.action) word in \(clip.texts)")
        return (result, onset)
    }

    static func span(_ clip: Clip) -> ClosedRange<Double> {
        let (words, onset) = words(clip)
        let start = max((words.first?.start ?? onset) - 0.25, onset - 1.0)
        return start...(onset + hold + 1.5)
    }

    // MARK: Drawing

    static func stage(_ clip: Clip, style: WordActionStyle, time: Double, size: CGSize) -> some View {
        let (words, _) = words(clip)
        let plan = WordAccentPlan.make(words: words, glue: [Bool](repeating: false, count: words.count),
                                       symbols: symbols, actions: actionList)
        return ZStack {
            StageLayoutVideoTests.backdrop(hue: clip.hue)
            LyricLineView(words: words, time: time, stageSize: size, baseSize: 72, mood: .neutral, composition: .cutout,
                          seed: 3, accent: Color(hue: (clip.hue + 0.5).truncatingRemainder(dividingBy: 1), saturation: 0.55,
                                                 brightness: 0.75),
                          lineEnd: (words.last?.end ?? time) + 0.3, accents: plan)
                .frame(width: size.width, height: size.height)
        }
        .frame(width: size.width, height: size.height)
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        .environment(\.lyricWordActionStyle, style)
    }

    static func label(_ text: String, emphasis: Bool = false) -> some View {
        Text(text)
            .font(.system(size: 17, weight: emphasis ? .semibold : .medium, design: .rounded))
            .foregroundStyle(.white.opacity(emphasis ? 0.92 : 0.6))
    }

    static func panes(_ clip: Clip, styles: [(String, WordActionStyle)], time: Double, onset: Double) -> some View {
        let gap: CGFloat = 14
        let width = (canvas.width - gap * CGFloat(styles.count + 1)) / CGFloat(styles.count)
        let size = CGSize(width: width.rounded(.down), height: 590)
        let word = words(clip).words.first { $0.start == onset }?.text ?? clip.action.rawValue
        return ZStack(alignment: .topLeading) {
            StageLayoutVideoTests.backdrop(hue: clip.hue).opacity(0.55)
            Color.black.opacity(0.55)
            VStack(alignment: .leading, spacing: 12) {
                HStack(alignment: .firstTextBaseline) {
                    Text(clip.action.title).font(.system(size: 28, weight: .bold, design: .rounded)).foregroundStyle(.white)
                    label("“\(word)” held \(String(format: "%.1f", hold)) s  ·  Cutout")
                    Spacer()
                    label(String(format: "%.2f s", max(0, time - onset)))
                        .monospacedDigit()
                }
                .padding(.horizontal, gap + 4)
                HStack(spacing: gap) {
                    ForEach(styles.indices, id: \.self) { index in
                        VStack(spacing: 8) {
                            stage(clip, style: styles[index].1, time: time, size: size)
                            label(styles[index].0, emphasis: index > 0)
                        }
                    }
                }
                .padding(.horizontal, gap)
            }
            .padding(.top, 16)
        }
        .frame(width: canvas.width, height: canvas.height)
        .environment(\.lyricInk, LyricInk())
        .environment(\.lyricReduceMotion, false)
        .environment(\.colorScheme, .dark)
    }

    static func sideBySide(_ clip: Clip, time: Double, onset: Double) -> some View {
        let left = WordAction.today.contains(clip.action) ? "Today" : "Today: none"
        let right = LivelyAction.defaultAmount >= LivelyAction.wild ? "New · Wild"
            : String(format: "New · amount %.2f", LivelyAction.defaultAmount)
        return panes(clip, styles: [(left, .today), (right, .lively(amount: LivelyAction.defaultAmount))],
                     time: time, onset: onset)
    }

    static func intensity(_ clip: Clip, time: Double, onset: Double) -> some View {
        panes(clip, styles: [("Subtle", .lively(amount: LivelyAction.subtle)),
                             ("Middle", .lively(amount: LivelyAction.middle)),
                             ("Wild", .lively(amount: LivelyAction.wild))], time: time, onset: onset)
    }

    // MARK: Video

    final class Movie {
        let writer: AVAssetWriter
        let input: AVAssetWriterInput
        let adaptor: AVAssetWriterInputPixelBufferAdaptor
        private(set) var frames = 0

        init(path: String, size: CGSize) throws {
            try? FileManager.default.removeItem(atPath: path)
            writer = try AVAssetWriter(outputURL: URL(fileURLWithPath: path), fileType: .mp4)
            input = AVAssetWriterInput(mediaType: .video, outputSettings: [
                AVVideoCodecKey: AVVideoCodecType.h264,
                AVVideoWidthKey: Int(size.width), AVVideoHeightKey: Int(size.height),
                AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: 9_000_000,
                                                  AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel],
                AVVideoColorPropertiesKey: [AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2,
                                            AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2,
                                            AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2],
            ])
            input.expectsMediaDataInRealTime = false
            adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
                kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
                kCVPixelBufferWidthKey as String: Int(size.width), kCVPixelBufferHeightKey as String: Int(size.height),
            ])
            writer.add(input)
            guard writer.startWriting() else { throw writer.error ?? CocoaError(.fileWriteUnknown) }
            writer.startSession(atSourceTime: .zero)
        }

        func append(_ image: CGImage) throws {
            while !input.isReadyForMoreMediaData { RunLoop.current.run(until: Date().addingTimeInterval(0.005)) }
            let pool = try XCTUnwrap(adaptor.pixelBufferPool)
            var buffer: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, pool, &buffer)
            let pixels = try XCTUnwrap(buffer)
            CVPixelBufferLockBaseAddress(pixels, [])
            let context = try XCTUnwrap(CGContext(
                data: CVPixelBufferGetBaseAddress(pixels), width: image.width, height: image.height, bitsPerComponent: 8,
                bytesPerRow: CVPixelBufferGetBytesPerRow(pixels), space: CGColorSpace(name: CGColorSpace.sRGB)!,
                bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue))
            context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
            CVPixelBufferUnlockBaseAddress(pixels, [])
            guard adaptor.append(pixels, withPresentationTime: CMTime(value: CMTimeValue(frames), timescale: CMTimeScale(ActionWordsPreviewTests.fps))) else {
                throw writer.error ?? CocoaError(.fileWriteUnknown)
            }
            frames += 1
        }

        func finish() {
            input.markAsFinished()
            var done = false
            writer.finishWriting { done = true }
            while !done { RunLoop.current.run(until: Date().addingTimeInterval(0.01)) }
        }
    }

    // MARK: The test

    func testWriteActionWordPreview() throws {
        let environment = ProcessInfo.processInfo.environment
        guard environment["LUNALYR_ACTION_PREVIEW"].map({ !$0.isEmpty && $0 != "0" }) == true else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_ACTION_PREVIEW=1 to write the action-word preview")
        }
        let dir = environment["LUNALYR_ACTION_PREVIEW_DIR"].flatMap { $0.isEmpty ? nil : $0 }
            ?? "/Users/tommy/Documents/Codex/2026-11-04/take-over-development-of-lunalyr-https/work/action-words-preview"
        let only = environment["LUNALYR_ACTION_PREVIEW_ONLY"].map { Set($0.lowercased().split(separator: ",").map(String.init)) }
            .flatMap { $0.isEmpty ? nil : $0 }
        let sheetsOnly = environment["LUNALYR_ACTION_PREVIEW_SHEET"] == "1"
        let keepDropped = environment["LUNALYR_ACTION_PREVIEW_KEEP_DROPPED"] == "1"
        let fileManager = FileManager.default
        try fileManager.createDirectory(atPath: dir + "/keyframes", withIntermediateDirectories: true)
        try fileManager.createDirectory(atPath: dir + "/dropped", withIntermediateDirectories: true)
        try fileManager.createDirectory(atPath: dir + "/sheets", withIntermediateDirectories: true)
        // ImageRenderer, not a window snapshot: the snapshot (cacheDisplay)
        // missed a word's draw scale until its entrance mask came off, so
        // every word seemed to zoom ~1.25× in one frame about half a second
        // in (on screen it is smooth), and it dropped 3D turns.
        let painter = StageLayoutVideoTests.Painter(size: Self.canvas, renderer: .image)
        defer { painter.close() }
        func image(_ view: some View) throws -> (NSBitmapImageRep, CGImage) {
            let rep = try painter.draw(view)
            return (rep, try XCTUnwrap(rep.cgImage))
        }
        let chosen = Self.clips.filter {
            (only == nil || only!.contains($0.action.rawValue)) && !(keepDropped && WordAction.dropped.contains($0.action))
        }
        XCTAssertFalse(chosen.isEmpty)

        if sheetsOnly {
            for clip in chosen {
                let onset = Self.words(clip).onset
                let moments = [0.05, 0.2, 0.35, 0.55, 0.8, 1.1, 1.4, 1.8]
                var rows: [[NSBitmapImageRep]] = []
                for moment in moments {
                    rows.append([try image(Self.sideBySide(clip, time: onset + moment, onset: onset)).0])
                }
                let cell = CGSize(width: 640, height: 360)
                let pairs = stride(from: 0, to: rows.count, by: 2).map { [rows[$0][0], rows[min($0 + 1, rows.count - 1)][0]] }
                try StageLayoutVideoTests.writePNG(try StageLayoutVideoTests.sheet(pairs, cell: cell),
                                                   to: "\(dir)/sheets/sheet-\(clip.action.rawValue).png")
            }
            return
        }

        let kept = chosen.filter { !WordAction.dropped.contains($0.action) }
        let reel = only == nil ? try Movie(path: dir + "/all-actions.mp4", size: Self.canvas) : nil
        for clip in chosen {
            let dropped = WordAction.dropped.contains(clip.action)
            let path = dropped ? "\(dir)/dropped/action-\(clip.action.rawValue).mp4" : "\(dir)/action-\(clip.action.rawValue).mp4"
            let movie = try Movie(path: path, size: Self.canvas)
            let span = Self.span(clip)
            let onset = Self.words(clip).onset
            let count = Int(((span.upperBound - span.lowerBound) * Self.fps).rounded(.up))
            let keyIndex = Int(((onset + 0.45 - span.lowerBound) * Self.fps).rounded())
            for index in 0...count {
                try autoreleasepool {
                    let time = span.lowerBound + Double(index) / Self.fps
                    let (rep, cg) = try image(Self.sideBySide(clip, time: time, onset: onset))
                    try movie.append(cg)
                    if !dropped, kept.contains(where: { $0.action == clip.action }) { try reel?.append(cg) }
                    if index == keyIndex {
                        try StageLayoutVideoTests.writePNG(rep, to: "\(dir)/keyframes/\(clip.action.rawValue).png")
                    }
                }
            }
            movie.finish()
        }
        reel?.finish()

        // Subtle / Middle / Wild.
        let intensityActions: [WordAction] = [.hop, .spin, .dissolve, .shatter]
        let intensityClips = Self.clips.filter { intensityActions.contains($0.action) }
        if only == nil || !intensityClips.filter({ only!.contains($0.action.rawValue) }).isEmpty {
            let movie = try Movie(path: dir + "/intensity.mp4", size: Self.canvas)
            for clip in intensityClips {
                let span = Self.span(clip)
                let onset = Self.words(clip).onset
                let count = Int(((span.upperBound - span.lowerBound) * Self.fps).rounded(.up))
                for index in 0...count {
                    try autoreleasepool {
                        let time = span.lowerBound + Double(index) / Self.fps
                        let (rep, cg) = try image(Self.intensity(clip, time: time, onset: onset))
                        try movie.append(cg)
                        if index == Int(((onset + 0.4 - span.lowerBound) * Self.fps).rounded()) {
                            try StageLayoutVideoTests.writePNG(rep, to: "\(dir)/keyframes/intensity-\(clip.action.rawValue).png")
                        }
                    }
                }
            }
            movie.finish()
        }
    }
}
