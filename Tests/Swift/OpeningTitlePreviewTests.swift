import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Renders the song's opening title on the wallpaper, through the real lyric
/// layer, over a dark busy music-video frame and a bright one, at several
/// moments: entering, holding, leaving and once the first line has begun.
/// Preview only: nothing plays, nothing is fetched, no window opens.
/// Skipped unless LUNALYR_OPENING_PREVIEWS names a folder (pass it to
/// `xcodebuild test` as TEST_RUNNER_LUNALYR_OPENING_PREVIEWS=<dir>). Renders
/// go to `<dir>/<label>/`, the label from LUNALYR_OPENING_LABEL ("after" by
/// default).
@MainActor
final class OpeningTitlePreviewTests: XCTestCase {
    /// A 14-inch MacBook Pro's desktop, in points.
    static let screen = CGSize(width: 1512, height: 982)

    struct Song {
        let name: String
        let title: String
        let artist: String
        let album: String
        let lines: [LyricsFetcher.Line]
        /// The moments the old heading was drawn at (the "before" set).
        let moments: [(String, Double)]
        var reduceMotion = false
        /// The cover's two colours; nil for a song with no artwork.
        var cover: (Color, Color)? = (Color(red: 0.95, green: 0.5, blue: 0.35), Color(red: 0.25, green: 0.15, blue: 0.45))
        /// "Show the album cover" switched off.
        var coverOff = false

        /// The moments drawn: through the card's entrance, held, half-way
        /// out, and once the first line has begun.
        @MainActor var cardMoments: [(String, Double)] {
            let first = OpeningTitle.firstSung(lines)
            guard let card = OpeningTitle.schedule(words: .timed(firstLine: first),
                                                   previewLead: VersoCustom.Settings.shared.nextWordPreviewLead) else {
                return [("0-no-card", 1.2)] + (first.map { [("5-first-line", $0 + 0.6)] } ?? [])
            }
            let entrance = card.settled - card.start
            return [("1-entering", card.start + entrance * 0.2), ("2-entering", card.start + entrance * 0.5),
                    ("3-holding", (card.settled + card.leave) / 2), ("4-leaving", (card.leave + card.end) / 2)]
                + (first.map { [("5-first-line", $0 + 0.6)] } ?? [])
        }
    }

    static func spaced(_ text: String, start: Double, step: Double = 0.42) -> LyricsFetcher.Line {
        LyricHighlightRenderTests.spaced(text, start: start, step: step)
    }

    static func credit(_ text: String, _ start: Double) -> LyricsFetcher.Line {
        LyricsFetcher.Line(start: start, end: start + 0.01, text: text, words: [], isInstrumental: true, credit: true)
    }

    static let songs: [Song] = [
        Song(name: "a-standard", title: "Human Nature", artist: "Yot Club", album: "Rare Hymns",
             lines: [credit("Human Nature - Yot Club", 0), credit("Produced by: Mara Lind", 0.5),
                     spaced("Always felt like you were gonna stick around", start: 11.0),
                     spaced("Now the summer's over and I'm watching you go", start: 14.6)],
             moments: [("1-entering", 1.1), ("2-entering", 1.7), ("3-holding", 5.0), ("4-leaving", 9.3),
                       ("5-first-line", 11.6)]),
        Song(name: "b-long", title: "Everything You Said Before the Summer Ended (Midnight Version)",
             artist: "Clara Vey & The Lanterns", album: "Paper Moons",
             lines: [spaced("Tell me what you said before the light went out", start: 13.0)],
             moments: [("1-entering", 1.4), ("3-holding", 6.0), ("4-leaving", 11.3)]),
        Song(name: "c-japanese", title: "夜に駆ける", artist: "YOASOBI", album: "THE BOOK",
             lines: [LyricHighlightRenderTests.line([("沈む", 9.0, 9.5), ("ように", 9.5, 10.0), ("溶けてゆく", 10.0, 11.0),
                                                     ("ように", 11.0, 11.6)], text: "沈むように溶けてゆくように")],
             moments: [("1-entering", 1.4), ("3-holding", 4.5), ("4-leaving", 7.4)]),
        Song(name: "d-first-line-soon", title: "Fast Start", artist: "Kid Mono", album: "Fast Start - Single",
             lines: [spaced("Straight in, no warning, we're already gone", start: 3.9)],
             moments: [("1-entering", 0.8), ("3-holding", 1.6), ("4-leaving", 2.5), ("5-first-line", 4.4)]),
        Song(name: "e-reduce-motion", title: "Human Nature", artist: "Yot Club", album: "Rare Hymns",
             lines: [spaced("Always felt like you were gonna stick around", start: 11.0)],
             moments: [("1-entering", 0.9), ("3-holding", 5.0), ("4-leaving", 9.4)], reduceMotion: true),
        Song(name: "g-cover-off", title: "Human Nature", artist: "Yot Club", album: "Rare Hymns",
             lines: [spaced("Always felt like you were gonna stick around", start: 11.0)],
             moments: [], coverOff: true),
        Song(name: "h-no-artwork", title: "Human Nature", artist: "Yot Club", album: "Rare Hymns",
             lines: [spaced("Always felt like you were gonna stick around", start: 11.0)],
             moments: [], cover: nil),
    ]

    /// A made-up album cover: a two-colour gradient with a ring, as PNG.
    static func coverPNG(_ colours: (Color, Color)) -> Data? {
        let view = ZStack {
            LinearGradient(colors: [colours.0, colours.1], startPoint: .topLeading, endPoint: .bottomTrailing)
            Circle().stroke(Color.white.opacity(0.85), lineWidth: 14).frame(width: 170, height: 170)
            Circle().fill(colours.0.opacity(0.9)).frame(width: 60, height: 60).offset(x: 60, y: 70)
        }
        .frame(width: 300, height: 300)
        let renderer = ImageRenderer(content: view)
        renderer.scale = 1
        guard let image = renderer.cgImage else { return nil }
        return NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])
    }

    func testRenderOpeningTitlePreviews() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let path = environment["LUNALYR_OPENING_PREVIEWS"] ?? environment["TEST_RUNNER_LUNALYR_OPENING_PREVIEWS"],
              !path.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_OPENING_PREVIEWS=<dir> to render the opening title previews")
        }
        let label = environment["LUNALYR_OPENING_LABEL"] ?? environment["TEST_RUNNER_LUNALYR_OPENING_LABEL"] ?? "after"
        let folder = URL(fileURLWithPath: path).appendingPathComponent(label)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        let model = LyricsViewModel.shared, settings = VersoCustom.Settings.shared
        let extras = LunalyrExtras.shared, readings = RomanizationOptions.shared
        let saved = (lines: model.lines, plain: model.plainLines, notFound: model.notFound, loading: model.isLoading,
                     composition: settings.lyricComposition, hidden: extras.lyricsHidden, display: readings.display)
        defer {
            model.lines = saved.lines
            model.plainLines = saved.plain
            model.notFound = saved.notFound
            model.isLoading = saved.loading
            settings.lyricComposition = saved.composition
            extras.lyricsHidden = saved.hidden
            if readings.display != saved.display { readings.display = saved.display }
        }
        model.plainLines = []
        model.notFound = false
        model.isLoading = false
        settings.lyricComposition = .portal
        extras.lyricsHidden = false
        if readings.display != .below { readings.display = .below }

        let coverSetting = UserDefaults.standard.object(forKey: OpeningTitle.coverKey)
        defer {
            if let coverSetting { UserDefaults.standard.set(coverSetting, forKey: OpeningTitle.coverKey) }
            else { UserDefaults.standard.removeObject(forKey: OpeningTitle.coverKey) }
        }
        for song in Self.songs where label == "after" || !song.moments.isEmpty {
            UserDefaults.standard.set(!song.coverOff, forKey: OpeningTitle.coverKey)
            let music = FakeMusicApp()
            music.queue = [FakeMusicApp.Song(id: "00000000000000A1", name: song.title, artist: song.artist, album: song.album,
                                             artwork: song.cover.flatMap(Self.coverPNG))]
            music.position = 0
            let harness = PlayerHarness(music: music)
            harness.start()
            model.lines = song.lines
            // Let the cover arrive, as the wallpaper's does.
            let artDeadline = Date().addingTimeInterval(3)
            while song.cover != nil, harness.monitor.currentTrackArtwork == nil, Date() < artDeadline {
                harness.scheduler.drain()
                RunLoop.main.run(until: Date().addingTimeInterval(0.05))
            }
            let deadline = Date().addingTimeInterval(5)
            while Date() < deadline, song.lines.contains(where: { LyricRomanizer.containsNonLatin($0.text) }),
                  model.romanized.isEmpty {
                RunLoop.main.run(until: Date().addingTimeInterval(0.05))
            }
            for scene in [Backdrop.dark, .bright] {
                for (moment, time) in label == "before" ? song.moments : song.cardMoments {
                    var wall = Date().timeIntervalSinceReferenceDate + 60
                    let start = max(0, time - 1.6)
                    // Seen from the song's start, then sought to the moment.
                    let renderer = ImageRenderer(content: Frame(scene: scene, song: song, time: 0.1, wall: wall,
                                                                monitor: harness.monitor))
                    renderer.scale = 1
                    renderer.render { _, _ in }
                    // The clock runs up to the moment, as on screen.
                    var now = start
                    while now < time {
                        now = min(time, now + 1.0 / 30)
                        wall += 1.0 / 30
                        renderer.content = Frame(scene: scene, song: song, time: now, wall: wall, monitor: harness.monitor)
                        renderer.render { _, _ in }
                    }
                    let image = try XCTUnwrap(renderer.cgImage, "\(song.name) \(moment)")
                    try DesktopPlayerPreviewTests.write(image, to: folder.appendingPathComponent(
                        "\(song.name)-\(scene.rawValue)-\(moment).png"))
                }
            }
            // The floating lyrics draw a compact card, centred in the window.
            if label != "before", ["a-standard", "c-japanese", "h-no-artwork"].contains(song.name) {
                let textSize = CGFloat(FloatingLyricsOptions.SizePreset.medium.rawValue)
                let window = FloatingFrame.defaultSize(textSize: textSize,
                    in: CGRect(origin: .zero, size: Self.screen).insetBy(dx: 0, dy: 60))
                for (moment, time) in song.cardMoments where moment.hasPrefix("2") || moment.hasPrefix("3") {
                    var wall = Date().timeIntervalSinceReferenceDate + 60
                    let renderer = ImageRenderer(content: FloatingFrameShot(size: window, textSize: textSize, time: 0.1,
                                                                            wall: wall, monitor: harness.monitor))
                    renderer.scale = 2
                    renderer.render { _, _ in }
                    var now = max(0, time - 1.6)
                    while now < time {
                        now = min(time, now + 1.0 / 30)
                        wall += 1.0 / 30
                        renderer.content = FloatingFrameShot(size: window, textSize: textSize, time: now, wall: wall,
                                                             monitor: harness.monitor)
                        renderer.render { _, _ in }
                    }
                    let image = try XCTUnwrap(renderer.cgImage)
                    try DesktopPlayerPreviewTests.write(image, to: folder.appendingPathComponent(
                        "f-floating-\(song.name)-\(moment).png"))
                }
            }
        }
    }

    /// The floating lyrics window over a dark app, at its default size.
    struct FloatingFrameShot: View {
        let size: CGSize
        let textSize: CGFloat
        let time: Double
        let wall: Double
        let monitor: NowPlayingMonitor

        var body: some View {
            let metrics = LyricViewportMetrics(size: size, textSize: textSize)
            ZStack {
                Color(white: 0.16)
                FloatingLyricsFrame(backdrop: FloatingLyricsOptions.defaultBackdrop, reduceTransparency: false,
                                    albumAccent: SIMD3(0.05, 0.6, 0.9)) {
                    LyricLayer(time: time, baseSize: metrics.baseSize, maxWidth: metrics.stageSize.width,
                               maxHeight: metrics.stageSize.height, mood: .neutral,
                               accent: Color(.sRGB, red: 0.95, green: 0.55, blue: 0.42), trackSeed: 4242, wall: wall)
                        .frame(width: size.width, height: size.height)
                        .environmentObject(monitor)
                }
            }
            .frame(width: size.width, height: size.height)
            .clipped()
        }
    }

    enum Backdrop: String {
        case dark, bright

        /// What the wallpaper would measure behind the stage.
        var luminance: Double { self == .dark ? 0.07 : 0.72 }
    }

    struct Frame: View {
        let scene: Backdrop
        let song: Song
        let time: Double
        let wall: Double
        let monitor: NowPlayingMonitor

        var body: some View {
            let size = OpeningTitlePreviewTests.screen
            let metrics = LyricViewportMetrics(size: size)
            ZStack {
                if scene == .dark { NightStreet() } else { BrightBeach() }
                LyricHighlightRenderTests.Scrim()
                LyricLayer(time: time, baseSize: metrics.baseSize, maxWidth: metrics.stageSize.width,
                           maxHeight: metrics.stageSize.height, mood: .neutral,
                           accent: Color(.sRGB, red: 0.95, green: 0.55, blue: 0.42),
                           trackSeed: 4242, wall: wall,
                           palette: [SIMD3(0.95, 0.55, 0.42), SIMD3(0.35, 0.25, 0.55), SIMD3(0.15, 0.12, 0.3)],
                           measuredBackdrop: scene.luminance)
                    .environmentObject(monitor)
                    .environment(\._accessibilityReduceMotion, song.reduceMotion)
                Text(String(format: "%@ · %@ · %.1f s%@", song.name, scene.rawValue, time,
                            song.reduceMotion ? " · Reduce Motion" : ""))
                    .font(.system(size: 12, weight: .semibold).monospacedDigit())
                    .foregroundStyle(.white)
                    .padding(.horizontal, 9).padding(.vertical, 4)
                    .background(Capsule().fill(Color.black.opacity(0.65)))
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing)
                    .padding(.top, 40).padding(.trailing, 16)
            }
            .frame(width: size.width, height: size.height)
            .clipped()
        }
    }

    /// A dark, busy music-video frame: a street at night, neon bokeh, a lit
    /// shop front and passing headlights near the bottom left.
    struct NightStreet: View {
        var body: some View {
            Canvas { context, size in
                context.fill(Path(CGRect(origin: .zero, size: size)),
                             with: .linearGradient(Gradient(colors: [Color(red: 0.05, green: 0.04, blue: 0.12),
                                                                     Color(red: 0.12, green: 0.05, blue: 0.16),
                                                                     Color(red: 0.03, green: 0.03, blue: 0.05)]),
                                                   startPoint: .zero, endPoint: CGPoint(x: 0, y: size.height)))
                // A lit shop window on the right, a sign on the left.
                context.fill(Path(CGRect(x: size.width * 0.66, y: size.height * 0.34, width: size.width * 0.22,
                                         height: size.height * 0.36)),
                             with: .color(Color(red: 0.95, green: 0.72, blue: 0.42).opacity(0.55)))
                context.fill(Path(CGRect(x: size.width * 0.08, y: size.height * 0.18, width: size.width * 0.16,
                                         height: size.height * 0.05)),
                             with: .color(Color(red: 0.2, green: 0.9, blue: 0.95).opacity(0.7)))
                var random = SystemRandomNumberGeneratorStandIn(seed: 7)
                let colours: [Color] = [Color(red: 1, green: 0.45, blue: 0.3), Color(red: 1, green: 0.25, blue: 0.6),
                                        Color(red: 0.3, green: 0.8, blue: 1), Color(red: 1, green: 0.85, blue: 0.4),
                                        Color(red: 0.6, green: 0.4, blue: 1)]
                for index in 0..<70 {
                    let x = random.next() * size.width, y = random.next() * size.height
                    let radius = 12 + random.next() * 70
                    let colour = colours[index % colours.count]
                    context.fill(Path(ellipseIn: CGRect(x: x - radius, y: y - radius, width: radius * 2, height: radius * 2)),
                                 with: .radialGradient(Gradient(colors: [colour.opacity(0.75), colour.opacity(0)]),
                                                       center: CGPoint(x: x, y: y), startRadius: 0, endRadius: radius))
                }
                // Headlights streaking across the lower left.
                for lane in 0..<3 {
                    let y = size.height * (0.80 + 0.05 * CGFloat(lane))
                    context.fill(Path(roundedRect: CGRect(x: -20, y: y, width: size.width * 0.55, height: 5 + CGFloat(lane) * 3),
                                      cornerRadius: 4),
                                 with: .linearGradient(Gradient(colors: [.white.opacity(0.9), Color(red: 1, green: 0.8, blue: 0.5).opacity(0)]),
                                                       startPoint: CGPoint(x: 0, y: y), endPoint: CGPoint(x: size.width * 0.55, y: y)))
                }
            }
        }
    }

    /// A bright music-video frame: noon on a beach, a white sky and sun
    /// glare over pale sand, brightest at the bottom left.
    struct BrightBeach: View {
        var body: some View {
            Canvas { context, size in
                context.fill(Path(CGRect(origin: .zero, size: size)),
                             with: .linearGradient(Gradient(colors: [Color(red: 0.62, green: 0.80, blue: 0.95),
                                                                     Color(red: 0.93, green: 0.95, blue: 0.96),
                                                                     Color(red: 0.97, green: 0.90, blue: 0.78)]),
                                                   startPoint: .zero, endPoint: CGPoint(x: 0, y: size.height)))
                let glare = CGPoint(x: size.width * 0.18, y: size.height * 0.78)
                context.fill(Path(ellipseIn: CGRect(x: glare.x - 520, y: glare.y - 420, width: 1040, height: 840)),
                             with: .radialGradient(Gradient(colors: [.white, .white.opacity(0)]), center: glare,
                                                   startRadius: 0, endRadius: 520))
                // Sea, a couple of figures, a towel.
                context.fill(Path(CGRect(x: 0, y: size.height * 0.52, width: size.width, height: size.height * 0.08)),
                             with: .color(Color(red: 0.35, green: 0.62, blue: 0.78).opacity(0.7)))
                context.fill(Path(roundedRect: CGRect(x: size.width * 0.6, y: size.height * 0.62, width: 60, height: 170),
                                  cornerRadius: 26), with: .color(Color(red: 0.42, green: 0.28, blue: 0.22)))
                context.fill(Path(roundedRect: CGRect(x: size.width * 0.66, y: size.height * 0.64, width: 54, height: 160),
                                  cornerRadius: 24), with: .color(Color(red: 0.75, green: 0.35, blue: 0.3)))
                context.fill(Path(CGRect(x: size.width * 0.3, y: size.height * 0.86, width: 260, height: 60)),
                             with: .color(Color(red: 0.95, green: 0.55, blue: 0.2).opacity(0.8)))
            }
        }
    }

    /// A tiny deterministic generator, so the scenes are the same every run.
    struct SystemRandomNumberGeneratorStandIn {
        var state: UInt64
        init(seed: UInt64) { state = seed &* 0x9E37_79B9_7F4A_7C15 | 1 }
        mutating func next() -> CGFloat {
            state ^= state << 13
            state ^= state >> 7
            state ^= state << 17
            return CGFloat(state % 10_000) / 10_000
        }
    }
}
