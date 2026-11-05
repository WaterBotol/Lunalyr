import AppKit
import CoreText
import ImageIO
import SwiftUI
import UniformTypeIdentifiers
import XCTest
@testable import VersoCustom

/// Renders the real lyric stage -- `LyricLayer`, as `LyricStage` draws it on
/// every frame -- over the wallpapers it is drawn on, to PNG, for looking at
/// highlight colours and word transitions. Nothing is asserted about the
/// pictures; they are for a person to look at.
///
/// Skipped unless LUNALYR_LYRIC_RENDER_DIR names a folder. xcodebuild passes
/// the test process only variables prefixed with TEST_RUNNER_, so run it as
///
///     TEST_RUNNER_LUNALYR_LYRIC_RENDER_DIR=/path/to/folder xcodebuild test ... \
///         -only-testing:VersoCustomTests/LyricHighlightRenderTests
///
/// It writes, for every layout and for a Latin and a Japanese line (with its
/// romaji below): contact sheets of album palettes and colour modes over a
/// dark visualiser, a light imported picture, mid grey and the transparent
/// overlay on Your wallpaper; frame strips through two sung words, so a
/// transition can be followed frame by frame; every cell on its own; and a
/// log of the colours the stage was given. Each sequence is drawn by one
/// persistent renderer whose wall clock runs on, so the colours have eased
/// to where the stage settles, as they do on screen.
///
/// The stage reads the shared settings, as it does in the app. They are the
/// Debug build's own preferences domain, and every one changed here is put
/// back afterwards.
final class LyricHighlightRenderTests: XCTestCase {
    static let variable = "LUNALYR_LYRIC_RENDER_DIR"

    static func folder(in environment: [String: String]) -> URL? {
        guard let path = environment[variable] ?? environment["TEST_RUNNER_" + variable], !path.isEmpty else { return nil }
        return URL(fileURLWithPath: path, isDirectory: true)
    }

    // MARK: Scene

    /// The screen the stage is drawn for, and the scale cells are rendered at.
    static let screen = CGSize(width: 1280, height: 720)
    static let cellScale: CGFloat = 1

    /// The part of a cell close-ups show, as shares of the screen: around the
    /// column of a rolling stage, or across a whole line elsewhere.
    static func closeUp(_ layout: LyricComposition) -> CGRect {
        layout.arrivalOnly ? CGRect(x: 0.22, y: 0.2, width: 0.5, height: 0.6) : CGRect(x: 0.1, y: 0.2, width: 0.8, height: 0.6)
    }

    static func crop(_ image: CGImage, _ share: CGRect) -> CGImage {
        let rect = CGRect(x: share.minX * CGFloat(image.width), y: share.minY * CGFloat(image.height),
                          width: share.width * CGFloat(image.width), height: share.height * CGFloat(image.height)).integral
        return image.cropping(to: rect) ?? image
    }

    /// A cover's 3x3 grid as `ColorExtractor.extractPalette` reports it, in sRGB.
    struct Palette {
        let name: String
        let grid: [SIMD3<Double>]

        var hsb: [SIMD3<Double>] {
            grid.map { rgb in
                let colour = NSColor(srgbRed: rgb.x, green: rgb.y, blue: rgb.z, alpha: 1)
                var h: CGFloat = 0, s: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
                colour.getHue(&h, saturation: &s, brightness: &b, alpha: &a)
                return SIMD3(Double(h), Double(s), Double(b))
            }
        }
        var cover: AlbumWallpaperPalette { AlbumWallpaperPalette(grid: hsb) }
        /// The lyric accent exactly as `NowPlayingView.lyricAccent` takes it.
        var accent: Color {
            let cell = hsb[2]
            return Color(hue: cell.x, saturation: min(0.65, max(0.2, cell.y)), brightness: max(0.78, cell.z))
        }
        var swipe: LyricSwipePalette { LyricSwipePalette(hue: hsb[2].x, saturation: hsb[2].y) }
    }

    static let palettes: [Palette] = [
        Palette(name: "mono-blue", grid: [
            SIMD3(0.10, 0.18, 0.42), SIMD3(0.16, 0.30, 0.62), SIMD3(0.30, 0.48, 0.82),
            SIMD3(0.06, 0.10, 0.26), SIMD3(0.22, 0.38, 0.72), SIMD3(0.42, 0.58, 0.88),
            SIMD3(0.08, 0.14, 0.34), SIMD3(0.18, 0.32, 0.66), SIMD3(0.12, 0.22, 0.50)]),
        Palette(name: "brown-olive", grid: [
            SIMD3(0.36, 0.26, 0.16), SIMD3(0.52, 0.42, 0.24), SIMD3(0.62, 0.58, 0.34),
            SIMD3(0.28, 0.22, 0.14), SIMD3(0.46, 0.40, 0.22), SIMD3(0.70, 0.62, 0.42),
            SIMD3(0.22, 0.18, 0.10), SIMD3(0.40, 0.36, 0.20), SIMD3(0.56, 0.46, 0.30)]),
        Palette(name: "multicolour", grid: [
            SIMD3(0.90, 0.12, 0.20), SIMD3(0.10, 0.35, 0.95), SIMD3(0.98, 0.78, 0.08),
            SIMD3(0.10, 0.75, 0.40), SIMD3(0.85, 0.20, 0.75), SIMD3(0.05, 0.75, 0.85),
            SIMD3(0.98, 0.45, 0.08), SIMD3(0.45, 0.15, 0.85), SIMD3(0.15, 0.15, 0.18)]),
        Palette(name: "pastel", grid: [
            SIMD3(0.98, 0.85, 0.88), SIMD3(0.85, 0.82, 0.98), SIMD3(0.80, 0.95, 0.90),
            SIMD3(0.99, 0.90, 0.80), SIMD3(0.90, 0.86, 0.98), SIMD3(0.84, 0.92, 0.99),
            SIMD3(0.96, 0.88, 0.94), SIMD3(0.88, 0.96, 0.84), SIMD3(0.99, 0.95, 0.85)]),
        Palette(name: "black-white", grid: [
            SIMD3(0.05, 0.05, 0.05), SIMD3(0.85, 0.85, 0.85), SIMD3(0.50, 0.50, 0.50),
            SIMD3(0.95, 0.95, 0.95), SIMD3(0.12, 0.12, 0.12), SIMD3(0.70, 0.70, 0.70),
            SIMD3(0.30, 0.30, 0.30), SIMD3(0.98, 0.98, 0.98), SIMD3(0.20, 0.20, 0.20)]),
    ]
    static func palette(_ name: String) -> Palette { palettes.first { $0.name == name }! }

    /// One row of a sheet: a cover and the text colour mode.
    struct Look {
        let palette: Palette
        let mode: LyricColourMode
        var label: String { "\(palette.name) · \(mode.title)" }
        var file: String { "\(palette.name)-\(mode.rawValue)" }
    }

    static let looks: [Look] = [
        Look(palette: palette("mono-blue"), mode: .standard),
        Look(palette: palette("brown-olive"), mode: .standard),
        Look(palette: palette("multicolour"), mode: .standard),
        Look(palette: palette("pastel"), mode: .standard),
        Look(palette: palette("black-white"), mode: .standard),
        Look(palette: palette("brown-olive"), mode: .albumDrift),
        Look(palette: palette("multicolour"), mode: .albumDrift),
        // The user's own: their colours drifting at speed 2 (the default colours here).
        Look(palette: palette("multicolour"), mode: .customGradient),
    ]

    enum Backdrop: String, CaseIterable {
        case visualiser, picture, grey, overlay

        var title: String {
            switch self {
            case .visualiser: return "Dark visualiser"
            case .picture: return "Light imported picture"
            case .grey: return "Mid grey picture"
            case .overlay: return "Overlay on Your wallpaper"
            }
        }

        var mode: VersoCustom.Settings.BackgroundMode {
            switch self {
            case .visualiser: return .visualizer
            case .picture, .grey: return .custom
            case .overlay: return .desktop
            }
        }
    }

    /// A word-timed line: tokens with their onsets and ends.
    static func line(_ tokens: [(String, Double, Double)], text: String) -> LyricsFetcher.Line {
        let words = tokens.map { LyricsFetcher.Word(text: $0.0, start: $0.1, end: $0.2) }
        return LyricsFetcher.Line(start: words.first?.start ?? 0, end: words.last?.end ?? 0, text: text, words: words,
                                  isInstrumental: false, wordTimed: true)
    }

    static func spaced(_ text: String, start: Double, step: Double, held: [String: Double] = [:]) -> LyricsFetcher.Line {
        var time = start
        var tokens: [(String, Double, Double)] = []
        for word in text.split(separator: " ").map(String.init) {
            tokens.append((word, time, time + (held[word] ?? step * 0.85)))
            time += held[word].map { max(step, $0 + 0.1) } ?? step
        }
        return line(tokens, text: text)
    }

    /// An invented song: three Latin lines, then three Japanese ones timed by
    /// word with nothing between them, as Music's TTML gives them.
    static let lines: [LyricsFetcher.Line] = [
        spaced("We were dancing in the city lights", start: 14, step: 0.45, held: ["dancing": 0.7, "lights": 0.85]),
        spaced("Holding on to every golden night", start: 18, step: 0.5, held: ["golden": 0.9, "night": 0.8]),
        spaced("And we will never say goodbye", start: 22.4, step: 0.45, held: ["goodbye": 0.9]),
        line([("夜空", 26.0, 26.4), ("に", 26.4, 26.6), ("光る", 26.6, 27.1), ("星", 27.1, 27.4), ("を", 27.4, 27.6),
              ("数えて", 27.6, 28.6)], text: "夜空に光る星を数えて"),
        line([("君", 30.0, 30.3), ("と", 30.3, 30.5), ("歩いた", 30.5, 31.0), ("あの", 31.0, 31.4), ("道", 31.4, 31.7),
              ("を", 31.7, 31.9), ("今も", 31.9, 32.4), ("覚えてる", 32.4, 33.6)], text: "君と歩いたあの道を今も覚えてる"),
        line([("ずっと", 34.2, 34.7), ("ここ", 34.7, 35.0), ("に", 35.0, 35.2), ("いる", 35.2, 36.0)], text: "ずっとここにいる"),
    ]

    /// Which line a sheet shows, when it is caught, and the two onsets a
    /// strip runs through.
    struct Sung {
        let name: String
        /// Mid-line, while a word is held and the one before has faded.
        let still: Double
        let onsets: (Double, Double)
    }
    static let sung = [
        Sung(name: "latin", still: 20.5, onsets: (19.5, 20.0)),
        Sung(name: "japanese", still: 31.78, onsets: (31.0, 31.4)),
    ]

    /// Before the first onset, through its press, held, the next onset and
    /// the first one's fade, and the second held.
    static func stripTimes(_ onsets: (Double, Double)) -> [Double] {
        let (first, second) = onsets
        return [first - 0.08, first, first + 0.04, first + 0.08, first + 0.12, first + 0.25,
                second, second + 0.08, second + 0.16, second + 0.30, second + 0.45, second + 0.7]
    }

    // MARK: Drawing

    /// The wallpaper as `NowPlayingView` lays it under the lyrics, with the
    /// stage on top.
    struct Frame: View {
        let backdrop: Backdrop
        let look: Look
        let picture: CGImage?
        let backing: Double
        let measured: Double?
        let tinted: Bool
        let time: Double
        let wall: Double
        let monitor: NowPlayingMonitor
        var reduceMotion = false

        var body: some View {
            let metrics = LyricViewportMetrics(size: LyricHighlightRenderTests.screen)
            let cover = look.palette.cover
            ZStack {
                switch backdrop {
                case .visualiser:
                    VisualiserStandIn(cover: cover)
                    Scrim()
                case .picture, .grey:
                    if let picture { Image(decorative: picture, scale: 1).resizable() }
                    if tinted { cover.gradient.opacity(0.18) }
                    Scrim()
                case .overlay:
                    Color.clear
                }
                if backing > WallpaperShade.minimumBacking {
                    TextBackingShade(amount: backing, size: LyricHighlightRenderTests.screen)
                }
                LyricLayer(time: time, baseSize: metrics.baseSize, maxWidth: metrics.stageSize.width,
                           maxHeight: metrics.stageSize.height, mood: .neutral, accent: look.palette.accent,
                           trackSeed: 4242, wall: wall, palette: cover.stops, measuredBackdrop: measured)
                    .environmentObject(monitor)
                    .environment(\.lyricSwipePalette, look.palette.swipe)
                    .environment(\._accessibilityReduceMotion, reduceMotion)
            }
            .frame(width: LyricHighlightRenderTests.screen.width, height: LyricHighlightRenderTests.screen.height)
        }
    }

    /// A dark visualiser: soft blooms of the cover's colours and a few lit
    /// ribbons on black, about as bright behind the stage as Silk Ribbons.
    struct VisualiserStandIn: View {
        let cover: AlbumWallpaperPalette
        var body: some View {
            Canvas { context, size in
                context.fill(Path(CGRect(origin: .zero, size: size)), with: .color(.black))
                let places: [(CGFloat, CGFloat, CGFloat)] = [(0.18, 0.30, 0.42), (0.78, 0.25, 0.36), (0.52, 0.72, 0.5),
                                                             (0.10, 0.85, 0.3), (0.92, 0.80, 0.34)]
                for (index, place) in places.enumerated() {
                    let rgb = cover.stops[index % cover.stops.count]
                    let colour = Color(.sRGB, red: Double(rgb.x), green: Double(rgb.y), blue: Double(rgb.z))
                    let centre = CGPoint(x: size.width * place.0, y: size.height * place.1)
                    let radius = size.width * place.2
                    context.fill(Path(ellipseIn: CGRect(x: centre.x - radius, y: centre.y - radius,
                                                        width: radius * 2, height: radius * 2)),
                                 with: .radialGradient(Gradient(colors: [colour.opacity(0.55), colour.opacity(0)]),
                                                       center: centre, startRadius: 0, endRadius: radius))
                }
                for ribbon in 0..<4 {
                    var path = Path()
                    let base = size.height * (0.32 + 0.12 * CGFloat(ribbon))
                    path.move(to: CGPoint(x: 0, y: base))
                    for step in 1...48 {
                        let x = size.width * CGFloat(step) / 48
                        let y = base + sin(CGFloat(step) * 0.22 + CGFloat(ribbon) * 1.3) * size.height * 0.06
                        path.addLine(to: CGPoint(x: x, y: y))
                    }
                    let rgb = cover.stops[(ribbon + 1) % cover.stops.count]
                    let colour = Color(.sRGB, red: Double(rgb.x), green: Double(rgb.y), blue: Double(rgb.z))
                    context.stroke(path, with: .color(colour.opacity(0.6)), lineWidth: 3)
                }
            }
        }
    }

    struct Scrim: View {
        var body: some View {
            LinearGradient(colors: WallpaperShade.scrim.map { .black.opacity($0 * VersoCustom.Settings.shared.scrimStrength) },
                           startPoint: .top, endPoint: .bottom)
        }
    }

    /// A light photograph: a pale sky over warm sand, with soft clouds.
    @MainActor
    static func lightPicture() -> CGImage? {
        let view = ZStack {
            LinearGradient(colors: [Color(.sRGB, red: 0.74, green: 0.85, blue: 0.96),
                                    Color(.sRGB, red: 0.93, green: 0.94, blue: 0.92),
                                    Color(.sRGB, red: 0.95, green: 0.88, blue: 0.76)],
                           startPoint: .top, endPoint: .bottom)
            ForEach(0..<6, id: \.self) { index in
                Ellipse().fill(.white.opacity(0.7))
                    .frame(width: 360 + CGFloat(index) * 40, height: 120)
                    .blur(radius: 40)
                    .offset(x: CGFloat(index * 210 - 520), y: CGFloat(index % 3) * 120 - 200)
            }
        }
        .frame(width: screen.width, height: screen.height)
        let renderer = ImageRenderer(content: view)
        renderer.scale = 1
        return renderer.cgImage
    }

    @MainActor
    static func greyPicture() -> CGImage? {
        let renderer = ImageRenderer(content: Color(.sRGB, red: 0.5, green: 0.5, blue: 0.5)
            .frame(width: screen.width, height: screen.height))
        renderer.scale = 1
        return renderer.cgImage
    }

    // MARK: The test

    @MainActor
    func testRenderLyricHighlights() throws {
        guard let folder = Self.folder(in: ProcessInfo.processInfo.environment) else {
            throw XCTSkip("Renders only when \(Self.variable) (TEST_RUNNER_\(Self.variable) through xcodebuild) names a folder.")
        }
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("cells"), withIntermediateDirectories: true)
        let session = try Session(folder: folder)
        defer { session.restore() }
        // As the layouts enter their words, then with "Cutout · Highlight".
        try session.renderSheets()
        try session.renderStrips()
        try session.renderSheets(prefix: "hl-", entrance: .cutoutHighlight)
        try session.renderStrips(prefix: "hl-", entrance: .cutoutHighlight)
        try session.renderStrips(prefix: "hl-reduce-", entrance: .cutoutHighlight, reduceMotion: true,
                                 layouts: [.cutout, .scattered, .spoken])
        try session.renderModes()
        try session.log.joined(separator: "\n").write(to: folder.appendingPathComponent("log.txt"), atomically: true,
                                                      encoding: .utf8)
    }

    /// The shared state the stage reads, set for rendering and put back.
    @MainActor
    final class Session {
        let folder: URL
        let harness: PlayerHarness
        var log: [String] = []
        let pictures: [Backdrop: CGImage]
        private let restoreState: () -> Void
        private var wall = Date().timeIntervalSinceReferenceDate + 30
        /// The entrance every layout is set to (nil: Match the style), and Reduce Motion.
        var entrance: WordArrival?
        var reduceMotion = false

        init(folder: URL) throws {
            let music = FakeMusicApp()
            music.queue = [FakeMusicApp.Song(id: "00000000000000E1", name: "Highlight Probe", artist: "Render Harness",
                                             album: "Palettes")]
            music.position = 20
            let harness = PlayerHarness(music: music)
            harness.start()
            let model = LyricsViewModel.shared, settings = VersoCustom.Settings.shared
            let extras = LunalyrExtras.shared, colours = LyricColourOptions.shared, readings = RomanizationOptions.shared
            let highlights = LyricHighlightOptions.shared
            let saved = (lines: model.lines, plain: model.plainLines, notFound: model.notFound, loading: model.isLoading,
                         composition: settings.lyricComposition, background: settings.backgroundMode,
                         arrivals: settings.wordArrivalByStyle, hidden: extras.lyricsHidden, mode: colours.mode,
                         speed: colours.speed, display: readings.display, highlight: highlights.mode)
            self.folder = folder
            self.harness = harness
            restoreState = {
                model.lines = saved.lines
                model.plainLines = saved.plain
                model.notFound = saved.notFound
                model.isLoading = saved.loading
                settings.lyricComposition = saved.composition
                settings.backgroundMode = saved.background
                settings.wordArrivalByStyle = saved.arrivals
                extras.lyricsHidden = saved.hidden
                colours.mode = saved.mode
                colours.speed = saved.speed
                highlights.mode = saved.highlight
                if readings.display != saved.display { readings.display = saved.display }
            }
            highlights.mode = .complement
            if readings.display != .below { readings.display = .below }
            model.plainLines = []
            model.notFound = false
            model.isLoading = false
            model.lines = LyricHighlightRenderTests.lines
            extras.lyricsHidden = false
            colours.speed = 2
            settings.wordArrivalByStyle = [:]
            var pictures: [Backdrop: CGImage] = [:]
            pictures[.picture] = LyricHighlightRenderTests.lightPicture()
            pictures[.grey] = LyricHighlightRenderTests.greyPicture()
            self.pictures = pictures
            // The readings arrive from a background task, as in the app.
            let deadline = Date().addingTimeInterval(20)
            while model.romanized.count < 3, Date() < deadline {
                RunLoop.main.run(until: Date().addingTimeInterval(0.05))
            }
            log.append("readings for lines \(model.romanized.keys.sorted()): "
                + model.romanized.sorted { $0.key < $1.key }.map { $0.value.map(\.text).joined(separator: " ") }
                    .joined(separator: " | "))
            log.append("artwork shown: \(harness.monitor.artwork != nil); effective mode follows the backdrop")
        }

        func restore() { restoreState() }

        /// What the wallpaper measured behind the stage, the text backing it
        /// draws, and whether it lays the cover's tint over a picture.
        func shade(_ backdrop: Backdrop, _ look: Look) -> (measured: Double?, backing: Double, tinted: Bool) {
            let metrics = LyricViewportMetrics(size: LyricHighlightRenderTests.screen)
            let target = LyricLegibility.targetContrast(baseSize: metrics.baseSize)
            let scrim = WallpaperShade.scrimAtStage(strength: VersoCustom.Settings.shared.scrimStrength)
            let tinted = harness.monitor.artwork != nil
            switch backdrop {
            case .visualiser:
                return (nil, LyricLegibility.stageBacking(forLuminance: LyricLegibility.visualizerLuminance(look.palette.cover),
                                                          existingDarkening: scrim, target: target), tinted)
            case .picture, .grey:
                let measured = pictures[backdrop].flatMap {
                    LyricLegibility.stageLuminance(of: $0, screen: LyricHighlightRenderTests.screen, placement: .fill)
                }
                return (measured, LyricLegibility.stageBacking(forLuminance: measured ?? LyricLegibility.unknownBackdropLuminance,
                                                               existingDarkening: scrim, target: target), tinted)
            case .overlay:
                return (nil, LyricLegibility.stageBacking(forLuminance: LyricLegibility.unknownBackdropLuminance,
                                                          existingDarkening: 0, target: target), tinted)
            }
        }

        /// Sets the stage for one look over one backdrop.
        func apply(_ look: Look, _ backdrop: Backdrop) {
            let settings = VersoCustom.Settings.shared
            if settings.backgroundMode != backdrop.mode { settings.backgroundMode = backdrop.mode }
            let arrivals = entrance.map { arrival in
                Dictionary(uniqueKeysWithValues: LyricComposition.allCases.map { ($0.rawValue, arrival.rawValue) })
            } ?? [:]
            if settings.wordArrivalByStyle != arrivals { settings.wordArrivalByStyle = arrivals }
            let colours = LyricColourOptions.shared
            if colours.mode != look.mode { colours.mode = look.mode }
        }

        func frame(_ backdrop: Backdrop, _ look: Look, time: Double) -> Frame {
            let shade = shade(backdrop, look)
            return Frame(backdrop: backdrop, look: look, picture: pictures[backdrop], backing: shade.backing,
                         measured: shade.measured, tinted: shade.tinted, time: time, wall: wall, monitor: harness.monitor,
                         reduceMotion: reduceMotion)
        }

        /// Lets the stage's colours ease to where they settle: the ink moves
        /// by at most a quarter of a second of wall time per frame.
        func settle(_ renderer: ImageRenderer<Frame>, _ backdrop: Backdrop, _ look: Look, time: Double, steps: Int = 14) {
            for _ in 0..<steps {
                wall += 0.25
                renderer.content = frame(backdrop, look, time: time)
                renderer.render { _, _ in }
            }
        }

        func capture(_ renderer: ImageRenderer<Frame>, _ backdrop: Backdrop, _ look: Look, time: Double,
                     advance: Double = 1.0 / 60) throws -> CGImage {
            wall += advance
            renderer.content = frame(backdrop, look, time: time)
            let image = try XCTUnwrap(renderer.cgImage, "no image")
            return backdrop == .overlay ? Self.overChecks(image) : image
        }

        /// The overlay is transparent: shown over a mid-toned checkerboard.
        static func overChecks(_ image: CGImage) -> CGImage {
            let width = image.width, height = image.height
            guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                          space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return image }
            let check = max(8, width / 40)
            for row in 0...(height / check) {
                for column in 0...(width / check) {
                    let light = (row + column) % 2 == 0
                    context.setFillColor(CGColor(srgbRed: light ? 0.70 : 0.56, green: light ? 0.70 : 0.56,
                                                 blue: light ? 0.70 : 0.56, alpha: 1))
                    context.fill(CGRect(x: column * check, y: row * check, width: check, height: check))
                }
            }
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return context.makeImage() ?? image
        }

        func makeRenderer(_ backdrop: Backdrop, _ look: Look, time: Double) -> ImageRenderer<Frame> {
            let renderer = ImageRenderer(content: frame(backdrop, look, time: time))
            renderer.scale = LyricHighlightRenderTests.cellScale
            renderer.isOpaque = false
            return renderer
        }

        /// The colours the stage settles on for one look over one backdrop,
        /// worked out apart from the drawing, for the log.
        func describeInk(_ look: Look, _ backdrop: Backdrop) -> String {
            let shade = shade(backdrop, look)
            let estimate = LyricBackdrop.luminance(mode: backdrop.mode, motion: false, palette: look.palette.cover.stops,
                scrim: VersoCustom.Settings.shared.scrimStrength, measured: shade.measured, backing: nil,
                target: LyricLegibility.targetContrast(baseSize: LyricViewportMetrics(size: LyricHighlightRenderTests.screen).baseSize),
                tinted: shade.tinted)
            let director = LyricInkDirector()
            let accent = NSColor(look.palette.accent).usingColorSpace(.sRGB).map {
                Perceptual.oklab(sRGB: SIMD3(Double($0.redComponent), Double($0.greenComponent), Double($0.blueComponent)))
            }
            var ink = LyricInk.white
            for step in 0..<60 {
                ink = director.ink(.init(mode: look.mode, speed: 2, custom: LyricColourOptions.shared.customColours,
                                         album: look.palette.cover.stops, backdrop: estimate, accent: accent,
                                         highlight: LyricHighlightOptions.shared.mode,
                                         highlightCustom: LyricHighlightOptions.shared.customColour),
                                   wall: Double(step) * 0.25)
            }
            func describe(_ colour: Color?) -> String {
                guard let colour, let rgb = NSColor(colour).usingColorSpace(.sRGB) else { return "none" }
                let lab = Perceptual.oklab(sRGB: SIMD3(Double(rgb.redComponent), Double(rgb.greenComponent),
                                                       Double(rgb.blueComponent)))
                return String(format: "#%02X%02X%02X (L %.2f C %.3f h %.0f°)", Int(rgb.redComponent * 255),
                              Int(rgb.greenComponent * 255), Int(rgb.blueComponent * 255), lab.l, lab.chroma,
                              (lab.hue * 180 / .pi + 360).truncatingRemainder(dividingBy: 360))
            }
            func luminance(_ colour: Color?) -> Double? {
                guard let colour, let rgb = NSColor(colour).usingColorSpace(.sRGB) else { return nil }
                return Perceptual.luminance(sRGB: SIMD3(Double(rgb.redComponent), Double(rgb.greenComponent),
                                                        Double(rgb.blueComponent)))
            }
            var text = "\(look.label) over \(backdrop.title): backdrop \(String(format: "%.3f", estimate)), "
                + "backing \(String(format: "%.2f", shade.backing)), measured \(shade.measured.map { String(format: "%.3f", $0) } ?? "none")"
            text += "\n    tile \(describe(ink.tile)), text on tile \(describe(ink.onTile)), held words \(describe(ink.accent))"
            if let tile = luminance(ink.tile), let onTile = luminance(ink.onTile) {
                text += String(format: "\n    text on tile %.2f:1, tile against backdrop %.2f:1", Perceptual.contrast(tile, onTile),
                               Perceptual.contrast(tile, estimate))
            }
            text += "\n    lyric colour at mid-line \(describe(Perceptual.color(ink.mode == .standard ? .white : ink.gradient(slot: 0.5))))"
            if let tone = ink.highlight {
                func lab(_ colour: OKLab) -> String {
                    String(format: "L %.2f C %.3f h %.0f°", colour.l, colour.chroma, LyricHighlight.degrees(colour.hue))
                }
                text += "\n    highlight (\(LyricHighlightOptions.shared.mode.title)): tile \(lab(tone.tile))"
                    + (LyricHighlight.isMuddy(tone.tile) ? " MUDDY" : "") + ", text \(lab(tone.text)), held \(lab(tone.held))"
            }
            return text
        }

        // MARK: Sheets

        func renderSheets(prefix: String = "", entrance: WordArrival? = nil) throws {
            self.entrance = entrance
            defer { self.entrance = nil }
            let layouts = LyricComposition.allCases
            for sung in LyricHighlightRenderTests.sung {
                var cells: [LyricComposition: [[CGImage]]] = [:]
                for look in LyricHighlightRenderTests.looks {
                    var rows: [LyricComposition: [CGImage]] = [:]
                    for backdrop in Backdrop.allCases {
                        apply(look, backdrop)
                        VersoCustom.Settings.shared.lyricComposition = layouts[0]
                        let renderer = makeRenderer(backdrop, look, time: sung.still)
                        settle(renderer, backdrop, look, time: sung.still)
                        if sung.name == "latin", entrance == nil { log.append(describeInk(look, backdrop)) }
                        for layout in layouts {
                            VersoCustom.Settings.shared.lyricComposition = layout
                            settle(renderer, backdrop, look, time: sung.still, steps: 2)
                            let image = try capture(renderer, backdrop, look, time: sung.still)
                            rows[layout, default: []].append(image)
                            try BGRAImage.writePNG(image, to: folder.appendingPathComponent(
                                "cells/\(prefix)\(layout.rawValue)-\(sung.name)-\(look.file)-\(backdrop.rawValue).png"))
                        }
                    }
                    for layout in layouts { cells[layout, default: []].append(rows[layout] ?? []) }
                }
                for layout in layouts {
                    let rows = cells[layout] ?? []
                    let labels = LyricHighlightRenderTests.looks.map(\.label)
                    for (part, range) in [("a", 0..<4), ("b", 4..<rows.count)] where !range.isEmpty {
                        let sheet = Sheet(title: "\(prefix)\(layout.title) · \(sung.name) · t = \(sung.still)",
                                          columns: Backdrop.allCases.map(\.title),
                                          rows: Array(labels[range]), cells: Array(rows[range]))
                        if let image = sheet.image() {
                            try BGRAImage.writePNG(image, to: folder.appendingPathComponent(
                                "\(prefix)sheet-\(layout.rawValue)-\(sung.name)-\(part).png"))
                        }
                        let share = LyricHighlightRenderTests.closeUp(layout)
                        let close = Sheet(title: "\(prefix)\(layout.title) · \(sung.name) · t = \(sung.still) · close-up",
                                          columns: Backdrop.allCases.map(\.title), rows: Array(labels[range]),
                                          cells: rows[range].map { $0.map { LyricHighlightRenderTests.crop($0, share) } },
                                          cellWidth: 640)
                        if let image = close.image() {
                            try BGRAImage.writePNG(image, to: folder.appendingPathComponent(
                                "\(prefix)close-\(layout.rawValue)-\(sung.name)-\(part).png"))
                        }
                    }
                }
            }
        }

        // MARK: Strips

        func renderStrips(prefix: String = "", entrance: WordArrival? = nil, reduceMotion: Bool = false,
                          layouts: [LyricComposition] = LyricComposition.allCases) throws {
            self.entrance = entrance
            self.reduceMotion = reduceMotion
            defer { self.entrance = nil; self.reduceMotion = false }
            let look = LyricHighlightRenderTests.looks.last!
            for sung in LyricHighlightRenderTests.sung {
                for layout in layouts {
                    for backdrop in [Backdrop.visualiser, .picture] {
                        apply(look, backdrop)
                        VersoCustom.Settings.shared.lyricComposition = layout
                        let times = LyricHighlightRenderTests.stripTimes(sung.onsets)
                        let renderer = makeRenderer(backdrop, look, time: times[0])
                        settle(renderer, backdrop, look, time: times[0])
                        var previous = times[0]
                        var frames: [CGImage] = []
                        for time in times {
                            let image = try capture(renderer, backdrop, look, time: time,
                                                    advance: max(1.0 / 60, time - previous))
                            frames.append(LyricHighlightRenderTests.crop(image, LyricHighlightRenderTests.closeUp(layout)))
                            previous = time
                        }
                        let sheet = Sheet(title: "\(prefix)\(layout.title) · \(sung.name) · \(look.label) · \(backdrop.title) · "
                                            + "onsets \(sung.onsets.0), \(sung.onsets.1)",
                                          columns: times.map { String(format: "t = %.2f", $0) }, rows: [""], cells: [frames],
                                          wrap: 4, cellWidth: 640)
                        if let image = sheet.image() {
                            try BGRAImage.writePNG(image, to: folder.appendingPathComponent(
                                "\(prefix)strip-\(layout.rawValue)-\(sung.name)-\(backdrop.rawValue).png"))
                        }
                    }
                }
            }
        }

        // MARK: Highlight modes

        /// Cutout with "Cutout · Highlight", each look in the three
        /// highlight modes, side by side.
        func renderModes() throws {
            entrance = .cutoutHighlight
            let highlights = LyricHighlightOptions.shared
            defer { entrance = nil; highlights.mode = .complement }
            VersoCustom.Settings.shared.lyricComposition = .cutout
            for sung in LyricHighlightRenderTests.sung {
                for backdrop in [Backdrop.visualiser, .picture] {
                    var rows: [[CGImage]] = []
                    for look in LyricHighlightRenderTests.looks {
                        var row: [CGImage] = []
                        for mode in LyricHighlightMode.allCases {
                            highlights.mode = mode
                            apply(look, backdrop)
                            let renderer = makeRenderer(backdrop, look, time: sung.still)
                            settle(renderer, backdrop, look, time: sung.still)
                            let image = try capture(renderer, backdrop, look, time: sung.still)
                            row.append(LyricHighlightRenderTests.crop(image, LyricHighlightRenderTests.closeUp(.cutout)))
                        }
                        rows.append(row)
                    }
                    let labels = LyricHighlightRenderTests.looks.map(\.label)
                    for (part, range) in [("a", 0..<4), ("b", 4..<rows.count)] {
                        let sheet = Sheet(title: "Highlight modes · Cutout · \(sung.name) · \(backdrop.title)",
                                          columns: LyricHighlightMode.allCases.map(\.title), rows: Array(labels[range]),
                                          cells: Array(rows[range]), cellWidth: 640)
                        if let image = sheet.image() {
                            try BGRAImage.writePNG(image, to: folder.appendingPathComponent(
                                "modes-\(sung.name)-\(backdrop.rawValue)-\(part).png"))
                        }
                    }
                }
            }
        }
    }

    /// A labelled grid of rendered cells, each scaled to `cellWidth`.
    struct Sheet {
        let title: String
        let columns: [String]
        let rows: [String]
        let cells: [[CGImage]]
        /// Wraps each row after this many cells.
        var wrap: Int?
        var cellWidth = 480

        func image() -> CGImage? {
            guard let first = cells.first?.first else { return nil }
            let cellHeight = Int((Double(cellWidth) * Double(first.height) / Double(first.width)).rounded())
            let perRow = wrap ?? columns.count
            let bands = cells.map { Int((Double($0.count) / Double(perRow)).rounded(.up)) }
            let labelHeight = 22, titleHeight = 30
            let width = perRow * cellWidth
            let height = titleHeight + bands.reduce(0) { $0 + $1 * (cellHeight + labelHeight) }
            guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                          space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
            context.setFillColor(CGColor(srgbRed: 0.12, green: 0.12, blue: 0.13, alpha: 1))
            context.fill(CGRect(x: 0, y: 0, width: width, height: height))
            context.interpolationQuality = .high
            func text(_ string: String, x: Int, top: Int, size: CGFloat) {
                let font = CTFontCreateWithName("Helvetica-Bold" as CFString, size, nil)
                let attributed = NSAttributedString(string: string, attributes: [
                    NSAttributedString.Key(kCTFontAttributeName as String): font,
                    NSAttributedString.Key(kCTForegroundColorAttributeName as String):
                        CGColor(srgbRed: 0.92, green: 0.92, blue: 0.92, alpha: 1)])
                let line = CTLineCreateWithAttributedString(attributed)
                context.textPosition = CGPoint(x: CGFloat(x), y: CGFloat(height - top) - size)
                CTLineDraw(line, context)
            }
            text(title, x: 8, top: 6, size: 16)
            var top = titleHeight
            for (rowIndex, row) in cells.enumerated() {
                for (index, cell) in row.enumerated() {
                    let column = index % perRow, band = index / perRow
                    let y = top + band * (cellHeight + labelHeight)
                    let label = [rowIndex < rows.count ? rows[rowIndex] : "", index < columns.count ? columns[index] : ""]
                        .filter { !$0.isEmpty }.joined(separator: " · ")
                    text(label, x: column * cellWidth + 6, top: y + 4, size: 12)
                    context.draw(cell, in: CGRect(x: column * cellWidth, y: height - (y + labelHeight + cellHeight),
                                                  width: cellWidth, height: cellHeight))
                }
                top += (bands[rowIndex]) * (cellHeight + labelHeight)
            }
            return context.makeImage()
        }
    }
}
