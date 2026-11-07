import AppKit
import ImageIO
import SwiftUI
import UniformTypeIdentifiers

/// The shape of a lyric card. Every card is 1080 points on its short side.
enum CardAspect: String, CaseIterable, Identifiable {
    case portrait, square, story, landscape

    var id: String { rawValue }

    var title: String {
        switch self {
        case .portrait: return "4:5"
        case .square: return "1:1"
        case .story: return "9:16"
        case .landscape: return "16:9"
        }
    }

    var detail: String {
        switch self {
        case .portrait: return "Portrait"
        case .square: return "Square"
        case .story: return "Story"
        case .landscape: return "Wide"
        }
    }

    var size: CGSize {
        switch self {
        case .portrait: return CGSize(width: 1080, height: 1350)
        case .square: return CGSize(width: 1080, height: 1080)
        case .story: return CGSize(width: 1080, height: 1920)
        case .landscape: return CGSize(width: 1920, height: 1080)
        }
    }

    /// Wide cards put the cover and credits in a column beside the words.
    var hasSidebar: Bool { self == .landscape }
}

/// What a lyric card can be saved or copied as.
enum CardFormat: String, CaseIterable, Identifiable {
    case mp4, gif, png

    var id: String { rawValue }
    var title: String { rawValue.uppercased() }
    var detail: String {
        switch self {
        case .mp4: return "Smooth video, full colour"
        case .gif: return "Loops everywhere"
        case .png: return "Crisp still image"
        }
    }
}

/// A shareable card of a lyric excerpt, the cover and the song.
@MainActor
enum LyricCard {
    struct Page {
        /// Each glued run of syllables or characters is one entry.
        var words: [String]
        var animation: Animation?
        var rightToLeft: Bool { LyricScript.lineIsRightToLeft(words) }
    }

    struct Content {
        var pages: [Page]
        var title: String
        var artist: String
        var artwork: NSImage?
        var palette: AlbumWallpaperPalette
        var mode: LyricColourMode = .standard
        var custom: [OKLab] = []
        var speed = 1.0
        var strength = 1.0
        var highlight: LyricHighlightMode = .complement
        var highlightCustom: OKLab?
        var reduceMotion = false
        var increaseContrast = false
        var aspect: CardAspect = .portrait

        var size: CGSize { aspect.size }

        /// All the selected lines as one page, for a still image.
        var stillPage: Page {
            Page(words: pages.flatMap(\.words))
        }

        var hasLyrics: Bool { pages.contains { !$0.words.isEmpty || $0.animation != nil } }
        var hasTrackDetails: Bool { artwork != nil || !title.isEmpty || !artist.isEmpty }
        var lyricStage: CGRect {
            let size = aspect.size
            if aspect.hasSidebar && hasTrackDetails {
                let left = LyricCard.inset + LyricCard.sidebarWidth + 56
                return CGRect(x: left, y: LyricCard.inset, width: size.width - left - LyricCard.inset,
                              height: size.height - 2 * LyricCard.inset - LyricCard.signatureHeight - 20)
            }
            let top: CGFloat = aspect == .landscape ? LyricCard.inset : 84
            let footer = hasTrackDetails ? LyricCard.trackFooterHeight : LyricCard.signatureHeight
            return CGRect(x: LyricCard.inset, y: top, width: size.width - 2 * LyricCard.inset,
                          height: size.height - top - LyricCard.inset - footer - 28)
        }

        func inkInputs(vocal: Double) -> LyricInkDirector.Inputs {
            let backdrop = LyricLegibility.darkened(palette.gradientLuminance(vividness: 1), by: LyricCard.lightestScrim)
            return .init(mode: mode, speed: speed, strength: strength, custom: custom, album: palette.artworkStops,
                         backdrop: max(0.06, backdrop), vocal: vocal, reduceMotion: reduceMotion,
                         increaseContrast: increaseContrast, highlight: highlight, highlightCustom: highlightCustom)
        }
    }

    struct Animation {
        let line: LyricsFetcher.Line
        let composition: LyricComposition
        let mood: TrackMood
        let seed: Int
        let words: [LyricsFetcher.Word]
        let glue: [Bool]
        let readings: [TimedWordKey: String]
        let rolling: LyricLayerCache.RollingWindow?
        let cardTypeSize: CGFloat

        var rolls: Bool { line.wordTimed && composition.arrivalOnly }
        var end: Double { LyricHighlightTiming.lineEnd(line.end, words: words) }
        var firstInk: Double? {
            guard let first = words.first(where: { LyricRuns.isVisible($0.text) }) else { return nil }
            guard line.wordTimed else { return line.start + min(0.09, (end - line.start) / 2) }
            let following = words.first { $0.start > first.start && LyricRuns.isVisible($0.text) }?.start ?? end
            return first.start + min(0.12, max(0.02, (following - first.start) * 0.75))
        }

        @MainActor
        init(line: LyricsFetcher.Line, composition: LyricComposition, mood: TrackMood, seed: Int,
             romanized: [LyricsFetcher.Word]? = nil, display: RomanizationOptions.Display = .off) {
            self.line = line
            self.composition = composition
            self.mood = mood
            self.seed = seed
            let source = display == .replace ? romanized ?? line.words : line.words
            let joined = LyricRuns.glue(line: line, words: source)
            let glue = line.wordTimed && composition.hasConnections ? LyricRuns.splittingWords(source, glue: joined) : joined
            let rolling = line.wordTimed && composition.arrivalOnly
                ? LyricLayerCache.rollingWindow(around: 0, lines: [line], romanized: romanized.map { [0: $0] } ?? [:],
                                               time: line.start, lead: 0, display: display) : nil
            self.rolling = rolling
            let shown = rolling?.words ?? source
            self.words = shown
            let labels = LyricRuns.labels(shown, glue: rolling == nil ? glue : [])
            let weight = LyricCard.weight(labels)
            self.cardTypeSize = max(126, 224 * sqrt(5 / max(5, weight)))
            self.glue = rolling == nil ? glue : []
            self.readings = display == .below
                ? rolling?.readings ?? LyricReadingPairs.match(romanized, to: line.words, glue: glue) : [:]
        }
    }

    /// Portrait 4:5, the shape most apps show without cropping.
    static let size = CGSize(width: 1080, height: 1350)
    static let inset: CGFloat = 52
    static let trackFooterHeight: CGFloat = 224
    static let sidebarWidth: CGFloat = 440
    static let signatureHeight: CGFloat = 38
    static let lightestScrim = 0.48
    static let rollingHeightInTypeSizes: CGFloat = 2.35

    /// The line to share: the one being sung, or the last one sung during
    /// a break, or the start of unsynced lyrics; else just the song.
    static func currentContent(before: Int, after: Int, composition: LyricComposition?,
                               rows: [Int]? = nil, aspect: CardAspect = .portrait) -> Content? {
        let playing = NowPlayingMonitor.shared
        guard let track = playing.current else { return nil }
        let model = LyricsViewModel.shared
        let time = playing.playbackTime() + Settings.shared.lyricOffset + SongOffsets.shared.offset(for: model.currentQuery)
        let display = RomanizationOptions.shared.display
        let lyricRows = model.lines.indices.filter { !model.lines[$0].isInstrumental && !model.lines[$0].text.isEmpty }
        let indices = (rows ?? selectedRows(lyricRows, active: model.activeLineIndex(at: time), before: before, after: after))
            .filter { model.lines.indices.contains($0) }

        // This song's own cover, even while the next cover is resolving.
        let artwork = playing.currentTrackArtwork
        let grid = paletteGrid(for: artwork)
        let remembered = StyleMemory.shared.remembered(for: track)?.style
        let colours = LyricColourOptions.shared
        let highlights = LyricHighlightOptions.shared
        let trackSeed = TrackMood.seed(title: track.title, artist: track.artist)
        let saturation = grid.isEmpty ? 0.5 : grid.map(\.y).reduce(0, +) / Double(grid.count)
        let mood = TrackMood.derive(genre: track.genre, wordsPerSecond: model.wordsPerSecond,
                                    artworkSaturation: saturation, seed: trackSeed)
        var pages = indices.map { index -> Page in
            let line = model.lines[index]
            let readings = model.romanized[index]
            let words = Self.words(of: line, readings: display == .replace ? readings : nil)
            guard !line.words.isEmpty else { return Page(words: words) }
            let layout = composition ?? LyricComposition.selected(settings: .shared, remembered: remembered?.composition,
                                                                  mood: mood, seed: index,
                                                                  track: "\(track.artist)|\(track.title)")
            let animation = Animation(line: line, composition: layout, mood: mood,
                                      seed: line.wordTimed && layout.arrivalOnly ? trackSeed : index,
                                      romanized: readings, display: display)
            return Page(words: words, animation: animation)
        }
        if pages.isEmpty {
            let plain = model.plainLines.filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            let chosen = rows.map { $0.filter(plain.indices.contains) } ?? selectedRows(Array(plain.indices), active: nil, before: before, after: after)
            pages = chosen.map { Page(words: plain[$0].split(whereSeparator: \.isWhitespace).map(String.init)) }
        }
        if pages.isEmpty { pages = [Page(words: [])] }
        return Content(pages: pages, title: track.title, artist: track.artist, artwork: artwork,
                       palette: AlbumWallpaperPalette(grid: grid), mode: remembered?.colourMode ?? colours.mode,
                       custom: colours.customColours, speed: colours.speed, strength: colours.strength,
                       highlight: highlights.mode, highlightCustom: highlights.customColour,
                       reduceMotion: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion,
                       increaseContrast: NSWorkspace.shared.accessibilityDisplayShouldIncreaseContrast,
                       aspect: aspect)
    }

    /// Counts vocal lines on each side of the lyric currently on screen.
    /// During a break the previous lyric remains the reference point.
    static func selectedRows(_ vocalRows: [Int], active: Int?, before: Int, after: Int) -> [Int] {
        guard !vocalRows.isEmpty else { return [] }
        let pivot = active.flatMap { index in vocalRows.lastIndex { $0 <= index } } ?? 0
        let first = max(0, pivot - LunalyrExtras.clampedCardLineCount(before))
        let last = min(vocalRows.count - 1, pivot + LunalyrExtras.clampedCardLineCount(after))
        return Array(vocalRows[first...last])
    }

    /// The card's colours, taken from the cover it shows: neutral without
    /// one, never the last cover the wallpaper extracted.
    static func palette(for artwork: NSImage?) -> AlbumWallpaperPalette {
        AlbumWallpaperPalette(grid: paletteGrid(for: artwork))
    }

    private static func paletteGrid(for artwork: NSImage?) -> [SIMD3<Double>] {
        artwork.map { ColorExtractor.extractPalette(from: $0) } ?? ColorExtractor.fallbackHSBPalette
    }

    /// A line's words as the wallpaper sets them: syllables and characters
    /// that run together in the lyric are one word (沈むように, not
    /// 沈 む よ う に), and a Latin reading of each character stands apart.
    static func words(of line: LyricsFetcher.Line, readings: [LyricsFetcher.Word]?) -> [String] {
        let shown = readings ?? line.words
        let words = LyricRuns.labels(shown, glue: LyricRuns.glue(line: line, words: shown))
        return words.isEmpty ? line.text.split(separator: " ").map(String.init) : words
    }

    /// How much room a line takes, counted in words: a run written without
    /// spaces counts by its length, about three characters to a word.
    static func weight(_ words: [String]) -> Double {
        words.reduce(0) { total, word in
            let characters = word.unicodeScalars.filter(JapaneseReading.isJapanese).count
            return total + max(1, Double(characters) / 3)
        }
    }

    /// Settled colours for the card's dark backdrop.
    static func ink(for content: Content) -> LyricInk {
        let director = LyricInkDirector()
        var ink = LyricInk.white
        for step in 0...24 {
            ink = director.ink(content.inkInputs(vocal: 0.6), wall: Double(step) * 0.25)
        }
        return ink
    }

    static func image(_ content: Content, scale: CGFloat = 2) -> NSImage? {
        let renderer = ImageRenderer(content: LyricCardView(content: content, ink: ink(for: content)))
        renderer.scale = scale
        return renderer.nsImage
    }

    /// Full card resolution (1080 × 1350). Tests render at half size to stay fast.
    static let gifScale: CGFloat = 1

    private struct GIFFrame {
        let image: CGImage
        let delay: Double
    }

    static func gif(_ content: Content, scale: CGFloat = gifScale) async -> Data? {
        var collected: [GIFFrame] = []
        let ok = await renderFrames(content, scale: scale, minimumCadence: 0.08, budget: content.pages.count == 1 ? 90 : 120) { image, delay in
            collected.append(GIFFrame(image: image, delay: delay))
            return true
        }
        guard ok, !Task.isCancelled else { return nil }
        let frames = collected
        let encoder = Task.detached(priority: .userInitiated) {
            encodeGIF(frames)
        }
        return await withTaskCancellationHandler {
            await encoder.value
        } onCancel: {
            encoder.cancel()
        }
    }

    /// Renders the card's animation one frame at a time, handing each to `sink`
    /// (which says whether to go on), so a long clip never holds every frame
    /// at once. `budget` caps the frame count by stretching the cadence (the
    /// GIF's way); nil takes a frame every `minimumCadence`, whatever the length.
    @discardableResult
    static func renderFrames(_ content: Content, scale gifScale: CGFloat, minimumCadence: Double, budget: Int?,
                             progress: ((Double) -> Void)? = nil,
                             sink: (CGImage, Double) async -> Bool) async -> Bool {
        let stillInk = ink(for: content)
        let stage = content.lyricStage
        let clips = content.pages.enumerated().map { index, page -> (page: Page, start: Double, firstInk: Double?, duration: Double) in
            let tail = index == content.pages.count - 1 ? 0.32 : 0
            if let animation = page.animation, let firstInk = animation.firstInk {
                let start = min(firstInk, max(animation.line.start, firstInk - 0.12))
                let span = animation.end - start
                if span.isFinite, span > 0 { return (page, start, firstInk, span + tail) }
            }
            return (page, 0, nil, content.pages.count == 1 ? 0.08 : 1.8)
        }
        guard !clips.isEmpty else { return false }
        let timed = clips.filter { $0.firstInk != nil }
        let total = timed.reduce(0) { $0 + $1.duration }
        // Reserve frames for every clip, even very short lines. Share the
        // remaining budget across the timed clips in proportion to duration.
        let cadence = budget.map { max(minimumCadence, total / Double(max(1, $0 - 3 * clips.count))) } ?? minimumCadence
        let expected = max(1, Int((total / cadence).rounded(.up)) + clips.count)
        var produced = 0
        let backdrop: CGImage?
        if !timed.isEmpty {
            let renderer = ImageRenderer(content: LyricCardView(content: content, ink: stillInk, hideLyrics: true))
            renderer.scale = gifScale
            guard let rendered = renderer.cgImage else { return false }
            backdrop = rendered
        } else {
            backdrop = nil
        }
        let director = LyricInkDirector()
        for step in 0..<24 {
            _ = director.ink(content.inkInputs(vocal: 0), wall: Double(step) * 0.25)
        }
        var elapsed = 0.0
        for clip in clips {
            guard !Task.isCancelled else { return false }
            if let animation = clip.page.animation, let firstInk = clip.firstInk, let backdrop {
                let count = max(3, Int((clip.duration / cadence - 1e-9).rounded(.up)))
                let step = clip.duration / Double(count)
                // Begin after the first word or phrase has started to appear.
                // Keep the final rolling word in view through any trailing rest.
                let lastInk = animation.rolls
                    ? max(firstInk, (animation.words.map(\.end).max() ?? animation.end) + 0.44) : .infinity
                for index in 0..<count {
                    guard !Task.isCancelled else { return false }
                    let offset = Double(index) * step
                    let time = min(lastInk, max(firstInk, clip.start + offset))
                    let vocal = animation.line.wordTimed ? OrganicMotion.vocalIntensity(animation.words, at: time) : 0
                    let ink = director.ink(content.inkInputs(vocal: vocal), wall: 6 + elapsed + offset)
                    let frame = LyricCardFrame(size: content.size, animation: animation, backdrop: backdrop, stage: stage,
                                               time: time, ink: ink, reduceMotion: content.reduceMotion)
                    let renderer = ImageRenderer(content: frame)
                    renderer.scale = gifScale
                    guard let image = renderer.cgImage else { return false }
                    // Round successive boundaries so errors do not accumulate.
                    let delay = ((offset + step) * 100).rounded() - (offset * 100).rounded()
                    guard await sink(image, max(0.02, delay / 100)) else { return false }
                    produced += 1
                    progress?(min(1, Double(produced) / Double(expected)))
                    if produced.isMultiple(of: 4) { await Task.yield() }
                }
            } else {
                let renderer = ImageRenderer(content: LyricCardView(content: content, ink: stillInk, page: clip.page))
                renderer.scale = gifScale
                guard let image = renderer.cgImage else { return false }
                guard await sink(image, clip.duration) else { return false }
                produced += 1
                progress?(min(1, Double(produced) / Double(expected)))
                await Task.yield()
            }
            elapsed += clip.duration
        }
        return true
    }

    private nonisolated static func encodeGIF(_ frames: [GIFFrame]) -> Data? {
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.gif.identifier as CFString,
                                                                 frames.count, nil) else { return nil }
        CGImageDestinationSetProperties(destination, [kCGImagePropertyGIFDictionary: [
            kCGImagePropertyGIFLoopCount: 0,
        ]] as CFDictionary)
        for frame in frames {
            guard !Task.isCancelled else { return nil }
            let properties = [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFDelayTime: min(655.35, frame.delay)]] as CFDictionary
            CGImageDestinationAddImage(destination, frame.image, properties)
        }
        return CGImageDestinationFinalize(destination) ? data as Data : nil
    }

    /// Publish a file attachment. A receiver that reads a generic image can
    /// flatten raw GIF pasteboard data to PNG before the paste event.
    static func copy(_ gif: Data, to pasteboard: NSPasteboard,
                     in directory: URL = FileManager.default.temporaryDirectory
                        .appendingPathComponent("Lunalyr Lyric Cards", isDirectory: true)) -> Bool {
        let files = FileManager.default
        let export = directory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        let file = export.appendingPathComponent("Lunalyr Lyric Card.gif")
        var copied = false
        defer {
            if !copied { try? files.removeItem(at: export) }
        }
        do {
            try files.createDirectory(at: export, withIntermediateDirectories: true,
                                      attributes: [.posixPermissions: 0o700])
            try gif.write(to: file, options: .atomic)
        } catch {
            return false
        }

        let item = NSPasteboardItem()
        guard item.setString(file.absoluteString, forType: .fileURL) else { return false }
        pasteboard.clearContents()
        copied = pasteboard.writeObjects([item])
        if copied { pruneExpiredExports(in: directory, keeping: export) }
        return copied
    }

    /// Keep recent exports available for pasted attachments and drafts.
    static func pruneExpiredExports(in directory: URL, keeping current: URL) {
        let files = FileManager.default
        let keys: Set<URLResourceKey> = [.isDirectoryKey, .contentModificationDateKey]
        let exports = try? files.contentsOfDirectory(at: directory, includingPropertiesForKeys: Array(keys))
        let cutoff = Date().addingTimeInterval(-7 * 24 * 60 * 60)
        for export in exports ?? [] where export != current && UUID(uuidString: export.lastPathComponent) != nil {
            guard let values = try? export.resourceValues(forKeys: keys), values.isDirectory == true,
                  let modified = values.contentModificationDate, modified < cutoff else { continue }
            try? files.removeItem(at: export)
        }
    }
}

private struct LyricCardFrame: View {
    let size: CGSize
    let animation: LyricCard.Animation
    let backdrop: CGImage
    let stage: CGRect
    let time: Double
    let ink: LyricInk
    let reduceMotion: Bool

    var body: some View {
        Image(decorative: backdrop, scale: 1)
            .resizable()
            .frame(width: size.width, height: size.height)
            .overlay {
                lyrics(in: stage.size)
                    .frame(width: stage.width, height: stage.height)
                    .clipped()
                    .position(x: stage.midX, y: stage.midY)
            }
            .environment(\.colorScheme, .dark)
            .environment(\.lyricReduceMotion, reduceMotion)
            .environment(\.lyricInk, ink)
    }

    private func lyrics(in stage: CGSize) -> some View {
        let line = animation.line
        let style = animation.composition
        let settings = Settings.shared
        let rolls = animation.rolls
        let pose = LyricPhraseTiming(line: line, nextStart: .infinity, time: time)
        let density = line.words.reduce(0) { $0 + $1.text.count + 3 }
        // Lay the graph out in fewer rows, then fit the result to the card.
        let densityScale = style.hasConnections ? 0.68 * min(1, sqrt(110 / Double(max(density, 110)))) : 1
        let base = rolls ? max(290, animation.cardTypeSize * 1.62) : animation.cardTypeSize
        let rollingHeight = LyricCard.rollingHeightInTypeSizes
        return Group {
            if line.wordTimed {
                LyricLineView(words: animation.words, time: time, stageSize: stage,
                    baseSize: rolls ? min(base, stage.height / rollingHeight) : base * densityScale, mood: animation.mood,
                    composition: style, seed: animation.seed, presence: pose.progress, readings: animation.readings,
                    lineEnd: animation.end, glue: animation.glue,
                    rollingHeightInTypeSizes: rollingHeight, ordinals: animation.rolling?.ordinals ?? [:], parts: animation.rolling?.parts ?? [:])
                    .opacity(rolls ? 1 : pose.progress)
            } else {
                let phrase = LyricsFetcher.Word(text: line.text, start: line.start, end: line.end)
                let entrance = settings.arrival(in: style, for: phrase).motion(in: style)
                let point = base * (style == .spoken ? 1.26 : 1.06)
                LyricLineView(words: animation.words, time: time, stageSize: stage, baseSize: point,
                    mood: animation.mood, composition: style.arrivalOnly ? .scattered : style, seed: animation.seed, readings: animation.readings, wordTimed: false,
                    phraseSweep: LyricPhraseTiming.sweep(of: line, at: time), glue: animation.glue)
                    .modifier(WordEntrance(progress: pose.progress, style: entrance, size: point * 0.68,
                        direction: animation.seed.isMultiple(of: 2) ? 1 : -1, arrival: pose.arrival))
            }
        }
        .modifier(FittedLyricStage(available: stage, neutral: 1, maximumNaturalScale: rolls ? 1 : 2.6))
    }
}

/// The still card and the artwork and credits shared by all animated frames.
struct LyricCardView: View {
    let content: LyricCard.Content
    let ink: LyricInk
    var page: LyricCard.Page?
    var hideLyrics = false

    private var shown: LyricCard.Page { page ?? content.pages.first ?? LyricCard.Page(words: []) }

    /// The line as one shaped paragraph, each word in its own colour. One
    /// `Text` per word could not break a run of Chinese or Japanese between
    /// its characters (a long one ran off the card), and its rows of a
    /// right-to-left line were ragged on the right.
    private var line: Text {
        let count = shown.words.count
        var text = Text(verbatim: "")
        for (index, word) in shown.words.enumerated() {
            let slot = count > 1 ? Double(index) / Double(count - 1) : 0.5
            if index > 0 { text = text + Text(verbatim: " ") }
            text = text + Text(verbatim: word).foregroundStyle(ink.sample(slot: slot, seed: UInt64(index)).sung)
        }
        return text
    }

    var body: some View {
        let size = content.size
        let stage = content.lyricStage
        let sidebar = content.aspect.hasSidebar && content.hasLyrics && content.hasTrackDetails
        ZStack {
            backdrop
            if sidebar {
                VStack(alignment: .leading, spacing: 36) {
                    Spacer(minLength: 0)
                    if let artwork = content.artwork { cover(artwork, size: LyricCard.sidebarWidth) }
                    credits(titleSize: 56, artistSize: 36, alignment: .leading)
                    Spacer(minLength: 0)
                    signature
                }
                .frame(width: LyricCard.sidebarWidth, alignment: .leading)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
                .padding(LyricCard.inset)
            } else {
                VStack(spacing: 0) {
                    if !content.hasLyrics {
                        Spacer(minLength: 0)
                        trackPortrait
                    }
                    Spacer(minLength: 0)
                    if content.hasLyrics && content.hasTrackDetails {
                        trackFooter
                    } else {
                        signature.frame(maxWidth: .infinity, alignment: .trailing)
                    }
                }
                .padding(LyricCard.inset)
            }
            if !hideLyrics && !shown.words.isEmpty {
                lyrics(in: stage.size)
                    .frame(width: stage.width, height: stage.height)
                    .clipped()
                    .position(x: stage.midX, y: stage.midY)
            }
        }
        .frame(width: size.width, height: size.height)
        .clipped()
        .foregroundStyle(.white)
        .environment(\.colorScheme, .dark)
    }

    private var backdrop: some View {
        let size = content.size
        return ZStack {
            content.palette.gradient
            if let artwork = content.artwork {
                Image(nsImage: artwork).resizable().scaledToFill()
                    .frame(width: size.width, height: size.height)
                    .clipped()
                    .scaleEffect(1.22)
                    .blur(radius: 108)
                    .opacity(0.62)
            }
            LinearGradient(colors: [.black.opacity(LyricCard.lightestScrim), .black.opacity(0.64), .black.opacity(0.86)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
        }
    }

    private var trackFooter: some View {
        HStack(spacing: 28) {
            if let artwork = content.artwork { cover(artwork, size: 190) }
            credits(titleSize: 54, artistSize: 36, alignment: .leading)
                .frame(maxWidth: .infinity, alignment: .leading)
            signature.frame(maxHeight: .infinity, alignment: .bottom)
        }
        .frame(height: LyricCard.trackFooterHeight)
    }

    private var trackPortrait: some View {
        VStack(spacing: 38) {
            if let artwork = content.artwork { cover(artwork, size: 420) }
            credits(titleSize: 74, artistSize: 42, alignment: .center)
        }
        .frame(maxWidth: .infinity)
    }

    private func cover(_ artwork: NSImage, size: CGFloat) -> some View {
        let shape = RoundedRectangle(cornerRadius: size * 0.15, style: .continuous)
        return Image(nsImage: artwork).resizable().interpolation(.high).scaledToFit()
            .frame(width: size, height: size)
            .background(.black.opacity(0.40))
            .clipShape(shape)
            .overlay { shape.strokeBorder(.white.opacity(0.18), lineWidth: 2) }
            .shadow(color: .black.opacity(0.36), radius: 22, y: 9)
    }

    private func credits(titleSize: CGFloat, artistSize: CGFloat, alignment: HorizontalAlignment) -> some View {
        VStack(alignment: alignment, spacing: 9) {
            if !content.title.isEmpty {
                Text(verbatim: content.title)
                    .font(.system(size: titleSize, weight: .semibold))
                    .tracking(-titleSize * 0.025)
                    .lineLimit(2)
            }
            if !content.artist.isEmpty {
                Text(verbatim: content.artist)
                    .font(.system(size: artistSize, weight: .medium))
                    .foregroundStyle(.white.opacity(0.80))
                    .lineLimit(2)
            }
        }
        .minimumScaleFactor(0.68)
        .multilineTextAlignment(alignment == .center ? .center : .leading)
    }

    private var signature: some View {
        Text("Lunalyr")
            .font(.system(size: 28, weight: .semibold))
            .foregroundStyle(.white.opacity(0.68))
            .fixedSize()
            .frame(height: LyricCard.signatureHeight)
    }

    private func lyrics(in stage: CGSize) -> some View {
        let count = max(1, LyricCard.weight(shown.words))
        let nominal = 146 + 24 * max(0, 1 - count / 8)
        let point = nominal * min(1, (9 / count).squareRoot() * 1.15)
        let font = Settings.shared.lyricFont(size: point, hero: true, mood: .neutral)
        let rightToLeft = shown.rightToLeft
        let available = CGSize(width: stage.width * (count < 4 ? 0.82 : 1), height: stage.height)
        return line.font(font)
            .lineSpacing(point * 0.12)
            .multilineTextAlignment(rightToLeft ? .trailing : .leading)
            .fixedSize(horizontal: false, vertical: true)
            .visualEffect { text, geometry in
                text.scaleEffect(LyricStageFit.scale(content: geometry.size, available: available,
                    maximum: count < 4 ? 2.6 : 1), anchor: rightToLeft ? .trailing : .leading)
            }
            .shadow(color: .black.opacity(0.35), radius: 12, y: 3)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: rightToLeft ? .trailing : .leading)
    }
}
