import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

final class RenderingPerformanceTests: XCTestCase {
    @MainActor
    func testEveryCompositionKeepsSmallWordsLegibleAtLargeScale() {
        for style in LyricComposition.allCases {
            let sizes: [CGFloat]
            if style.arrivalOnly {
                sizes = SpokenTypography.prepare(.init(words: words, baseSize: 220, mood: .neutral,
                    composition: style, fontIdentity: LyricFontIdentity(settings: .shared)), settings: .shared).map(\.pointSize)
            } else {
                sizes = ScatteredTypography.prepare(key(size: 220, style: style), settings: .shared).map { $0.metrics.size }
            }
            XCTAssertGreaterThan(sizes.min() ?? 0, 220 * 0.55, style.title)
            XCTAssertLessThan((sizes.max() ?? 1) / max(1, sizes.min() ?? 1), 1.9, style.title)
        }
    }
    @MainActor
    func testInlineReadingUsesTheSourceWordsFontRules() throws {
        let settings = Settings.shared
        let savedRandomize = settings.randomizeLyricFonts
        defer { settings.randomizeLyricFonts = savedRandomize }
        let word = LyricsFetcher.Word(text: "光", start: 0, end: 1)
        let readings = [word.presentationID: "hikari"]
        for randomize in [false, true] {
            settings.randomizeLyricFonts = randomize
            let identity = LyricFontIdentity(settings: settings)
            let scattered = try XCTUnwrap(ScatteredTypography.prepare(
                .init(words: [word], baseSize: 80, style: .forLine(0), mood: .neutral,
                    composition: .editorial, spread: 1, fontIdentity: identity, readings: readings), settings: settings).first)
            XCTAssertEqual(scattered.reading?.text, "hikari")
            // Same face, seed and alternate as the word; smaller and regular
            // weight, so the reading reads as secondary.
            XCTAssertEqual(scattered.reading?.font, settings.lyricFont(size: scattered.metrics.size * 0.56,
                hero: false, mood: .neutral, seed: word.presentationID.visualSeed,
                alternate: scattered.metrics.alternateFace))
            let spoken = try XCTUnwrap(SpokenTypography.prepare(
                .init(words: [word], baseSize: 80, mood: .neutral, composition: .spoken,
                    fontIdentity: identity, readings: readings), settings: settings).first)
            XCTAssertEqual(spoken.reading?.text, "hikari")
            XCTAssertNotEqual(spoken.reading?.font, spoken.font)
        }
    }
    @MainActor
    func testSwipeStylesDrawDistinctInk() throws {
        func image(_ style: WordArrival) throws -> Data {
            let renderer = ImageRenderer(content: Text("A lyric")
                .font(.system(size: 35, weight: .bold))
                .foregroundStyle(.white)
                .frame(width: 170, height: 58)
                .modifier(WordEntrance(progress: 0.51, style: style, size: 35, direction: 1, visibility: 1))
                .background(.black))
            return try XCTUnwrap(renderer.nsImage?.tiffRepresentation)
        }
        XCTAssertNotEqual(try image(.satinRibbon), try image(.splitInk))
    }

    func testInstrumentalDotsPulseAndLeaveRoomForTheNextWordPreview() throws {
        let gap = VCVocalGap(start: 10, end: 26, nextVocal: 3)
        XCTAssertNil(gap.instrumentalCue(at: 10, previewLead: 0.5))
        let cue = try XCTUnwrap(gap.instrumentalCue(at: 16, previewLead: 0.5))
        XCTAssertEqual(cue.opacity, 1)
        XCTAssertNotEqual(cue.bob(at: 0), cue.bob(at: 1))
        XCTAssertGreaterThan(cue.highlight(at: 0), cue.highlight(at: 2))
        let unknown = VCVocalGap(start: 10, end: -1, nextVocal: -1).instrumentalCue(at: 17, previewLead: 0.5)
        XCTAssertNil(unknown?.progress)
        XCTAssertNil(gap.instrumentalCue(at: 25.55, previewLead: 0.5))
    }

    @MainActor
    func testInstrumentalDotsRenderOverYourWallpaper() throws {
        let music = FakeMusicApp()
        music.queue = [.init(id: "00000000000000A1", name: "First Light", duration: 30)]
        music.position = 10
        let harness = PlayerHarness(music: music)
        harness.start()

        let model = LyricsViewModel.shared, settings = Settings.shared, extras = LunalyrExtras.shared
        let saved = (lines: model.lines, plain: model.plainLines, notFound: model.notFound, loading: model.isLoading,
                     mode: settings.backgroundMode, dots: settings.showInterludes, hidden: extras.lyricsHidden)
        defer {
            model.lines = saved.lines
            model.plainLines = saved.plain
            model.notFound = saved.notFound
            model.isLoading = saved.loading
            settings.backgroundMode = saved.mode
            settings.showInterludes = saved.dots
            extras.lyricsHidden = saved.hidden
        }
        model.lines = [
            LyricsFetcher.makeLine(text: "Before", start: 1, end: 3),
            LyricsFetcher.makeLine(text: "After", start: 20, end: 22),
        ]
        model.plainLines = []
        model.notFound = false
        model.isLoading = false
        extras.lyricsHidden = false
        settings.backgroundMode = .desktop

        func litPixels(show: Bool) throws -> Int {
            settings.showInterludes = show
            let renderer = ImageRenderer(content: LyricLayer(time: 10, baseSize: 60, maxWidth: 350, maxHeight: 180,
                mood: .neutral, accent: .orange, trackSeed: 0, animating: false)
                .environmentObject(harness.monitor)
                .frame(width: 400, height: 220))
            let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
            var lit = 0
            for y in 85..<135 {
                for x in 145..<255 where (bitmap.colorAt(x: x, y: y)?.alphaComponent ?? 0) > 0.1 {
                    lit += 1
                }
            }
            return lit
        }
        XCTAssertGreaterThan(try litPixels(show: true), 200)
        XCTAssertEqual(try litPixels(show: false), 0)
    }

    func testMotionCoverUsesTheAlbumHeaderAndMatchesTheRelease() throws {
        let wrong = MotionArtworkProvider.SearchResponse.Result(
            collectionViewUrl: "https://music.apple.com/au/album/1989/1", collectionName: "1989", artistName: "Taylor Swift")
        let correct = MotionArtworkProvider.SearchResponse.Result(
            collectionViewUrl: "https://music.apple.com/au/album/midnights/2", collectionName: "Midnights", artistName: "Taylor Swift")
        let pages = MotionArtworkProvider.matchingPages([wrong, correct], album: "Midnights", artist: "Taylor Swift", song: nil)
        XCTAssertEqual(pages.map(\.lastPathComponent), ["2"])

        let video = "https://mvod.itunes.apple.com/art/cover.m3u8"
        let artwork = "{\"videoArtwork\":{\"dictionary\":{\"motionDetailSquare\":{\"video\":\"\(video)\"}}}}"
        func page(_ first: String, _ second: String) -> String {
            """
            <script id="serialized-server-data" type="application/json">
            {"data":[{"data":{"sections":[{"items":[\(first)]},{"items":[\(second)]}]}}]}
            </script>
            """
        }
        XCTAssertNil(MotionArtworkProvider.parse(html: page("{}", artwork)))
        XCTAssertEqual(MotionArtworkProvider.parse(html: page(artwork, "{}"))?.square.absoluteString, video)
    }

    func testOnlyCompleteUnencryptedMP4RenditionsAreUsedDirectly() throws {
        let base = try XCTUnwrap(URL(string: "https://mvod.itunes.apple.com/art/cover.m3u8"))
        let playlist = """
        #EXTM3U
        #EXT-X-MAP:URI="cover.mp4",BYTERANGE="895@0"
        #EXTINF:3,
        #EXT-X-BYTERANGE:1000@895
        cover.mp4
        #EXTINF:3,
        #EXT-X-BYTERANGE:1000@1895
        cover.mp4
        #EXT-X-ENDLIST
        """
        XCTAssertEqual(MotionArtworkProvider.singleMP4(in: playlist, relativeTo: base)?.absoluteString,
            "https://mvod.itunes.apple.com/art/cover.mp4")
        XCTAssertNil(MotionArtworkProvider.singleMP4(in: playlist + "\n#EXT-X-KEY:METHOD=SAMPLE-AES", relativeTo: base))
        XCTAssertNil(MotionArtworkProvider.singleMP4(in: playlist + "\nother.mp4", relativeTo: base))
    }

    func testTallLinesAndReadingsFitShortOrPortraitDisplays() {
        for available in [CGSize(width: 900, height: 400), CGSize(width: 400, height: 900)] {
            let content = CGSize(width: 1400, height: 1700)
            let fit = LyricStageFit.scale(content: content, available: available)
            XCTAssertLessThanOrEqual(content.width * fit, available.width + 0.001)
            XCTAssertLessThanOrEqual(content.height * fit, available.height + 0.001)
        }
    }
    func testPreviewPreservesTheActualDisplayAspectAndLyricStage() {
        for size in [CGSize(width: 1512, height: 982), CGSize(width: 1080, height: 1920),
                     CGSize(width: 3440, height: 1440)] {
            let layout = LyricViewportMetrics(size: size)
            let scale = LyricViewportMetrics.previewScale(desktop: size, inside: CGSize(width: 256, height: 144))
            let preview = CGSize(width: size.width * scale, height: size.height * scale)
            XCTAssertEqual(preview.width / preview.height, size.width / size.height, accuracy: 0.0001)
            XCTAssertLessThanOrEqual(preview.width, 256.001)
            XCTAssertLessThanOrEqual(preview.height, 144.001)
            let stage = layout.stageSize
            let content = CGSize(width: stage.width * 2, height: stage.height * 3)
            let fit = LyricStageFit.presentationScale(content: content, available: stage, userScale: 1.5)
            XCTAssertLessThanOrEqual(content.width * fit, stage.width + 0.001)
            XCTAssertLessThanOrEqual(content.height * fit, stage.height + 0.001)
        }
    }

    @MainActor
    func testRenderedWideCutoutAndReadingFitDesktopAndScaledPreview() throws {
        let display = CGSize(width: 680, height: 420)
        let metrics = LyricViewportMetrics(size: display)
        let word = LyricsFetcher.Word(text: String(repeating: "W", count: 42), start: 0, end: 2)
        let stage = LyricLineView(words: [word], time: 0.6, stageSize: metrics.stageSize,
            baseSize: metrics.baseSize, mood: .neutral, composition: .cutout, readings: [
                word.presentationID: String(repeating: "romaji ", count: 12),
            ])
            .modifier(FittedLyricStage(available: metrics.stageSize, userScale: 1.5))
            .frame(maxWidth: metrics.stageSize.width)
        let canvas = WallpaperViewport(size: display) { Color.black } lyrics: { stage }

        let desktop = try renderedInkBounds(canvas)
        let preview = try renderedInkBounds(canvas.scaleEffect(0.5).frame(width: display.width / 2, height: display.height / 2))
        for frame in [desktop, preview] {
            XCTAssertGreaterThan(frame.minX, 0.09)
            XCTAssertLessThan(frame.maxX, 0.91)
            XCTAssertGreaterThan(frame.minY, 0.12)
            XCTAssertLessThan(frame.maxY, 0.88)
        }
        XCTAssertEqual(desktop.midX, preview.midX, accuracy: 0.02)
        XCTAssertEqual(desktop.midY, preview.midY, accuracy: 0.02)
    }

    @MainActor
    func testRenderedConstellationWithWideWordsStaysOnPortraitDesktop() throws {
        let display = CGSize(width: 420, height: 680)
        let metrics = LyricViewportMetrics(size: display)
        let settings = Settings.shared
        let saved = settings.constellationSpread
        defer { settings.constellationSpread = saved }
        settings.constellationSpread = 1.8
        let words = (0..<10).map { index in
            LyricsFetcher.Word(text: String(repeating: "W", count: 9 + index * 2),
                start: Double(index) * 0.2, end: Double(index) * 0.2 + 0.8)
        }
        for style in [LyricComposition.constellation, .constellationEditorial] {
            let stage = LyricLineView(words: words, time: 2.4, stageSize: metrics.stageSize,
                baseSize: metrics.baseSize, mood: .neutral, composition: style, seed: 2)
                .modifier(FittedLyricStage(available: metrics.stageSize, userScale: 1.5))
                .frame(maxWidth: metrics.stageSize.width)
            let canvas = WallpaperViewport(size: display) { Color.black } lyrics: { stage }
            let ink = try renderedInkBounds(canvas)
            XCTAssertGreaterThan(ink.minX, 0.06, style.title)
            XCTAssertLessThan(ink.maxX, 0.94, style.title)
            XCTAssertGreaterThan(ink.minY, 0.09, style.title)
            XCTAssertLessThan(ink.maxY, 0.91, style.title)
        }
    }

    @MainActor
    private func renderedInkBounds(_ content: some View) throws -> CGRect {
        let renderer = ImageRenderer(content: content)
        renderer.scale = 1
        let image = try XCTUnwrap(renderer.cgImage)
        let bitmap = NSBitmapImageRep(cgImage: image)
        var ink = CGRect.null
        for y in stride(from: 0, to: bitmap.pixelsHigh, by: 2) {
            for x in stride(from: 0, to: bitmap.pixelsWide, by: 2) {
                guard let pixel = bitmap.colorAt(x: x, y: y), pixel.numberOfComponents >= 3,
                      max(pixel.redComponent, pixel.greenComponent, pixel.blueComponent) > 0.45 else { continue }
                ink = ink.union(CGRect(x: CGFloat(x) / CGFloat(bitmap.pixelsWide),
                    y: CGFloat(y) / CGFloat(bitmap.pixelsHigh), width: 0, height: 0))
            }
        }
        XCTAssertFalse(ink.isNull)
        return ink
    }

    func testMixedTimingFadesTheWholePhraseAndKeepsRollingRunsSeparate() {
        let lines = [true, true, false, true].enumerated().map { index, timed in
            let start = Double(index * 2)
            let word = LyricsFetcher.Word(text: "phrase", start: start + 0.8, end: start + 1.5)
            return LyricsFetcher.Line(start: start, end: start + 2, text: "whole phrase",
                words: [word], isInstrumental: false, wordTimed: timed)
        }
        let phrase = lines[2]
        XCTAssertEqual(LyricPhraseTiming(line: phrase, nextStart: 6, time: 3.9).presence, 0)
        XCTAssertEqual(LyricPhraseTiming(line: phrase, nextStart: 6, time: 4.35).presence, 1)
        XCTAssertGreaterThan(LyricPhraseTiming(line: phrase, nextStart: 6, time: 6.1).presence, 0)
        XCTAssertEqual(LyricPhraseTiming.rollingRange(around: 1, lines: lines), 0...1)
        XCTAssertEqual(LyricPhraseTiming.rollingRange(around: 3, lines: lines), 3...3)
    }

    @MainActor
    func testMonochromeArtworkRetainsItsHueAcrossGradientStops() {
        let image = NSImage(size: NSSize(width: 90, height: 90))
        image.lockFocus()
        NSColor(srgbRed: 0.1, green: 0.2, blue: 0.7, alpha: 1).setFill()
        NSRect(x: 0, y: 0, width: 90, height: 90).fill()
        image.unlockFocus()
        let palette = ColorExtractor.extractPalette(from: image)
        XCTAssertEqual(palette.count, 9)
        let saturation = palette.map(\.y)
        XCTAssertTrue(saturation.allSatisfy { $0 > 0.42 })
        XCTAssertTrue(palette.allSatisfy { abs($0.x - palette[0].x) < 0.01 })
        XCTAssertGreaterThan(palette[4].z - palette[0].z, 0.15)
        XCTAssertEqual(AlbumWallpaperPalette(grid: palette).stops.count, 5)
    }
    @MainActor
    func testArtworkContrastsReachBothTheWallpaperAndLyricPalette() {
        let image = NSImage(size: NSSize(width: 96, height: 96))
        image.lockFocus()
        for x in stride(from: 0, to: 96, by: 8) {
            NSColor(srgbRed: 0.92, green: 0.04, blue: 0.03, alpha: 1).setFill()
            NSRect(x: x, y: 0, width: 4, height: 96).fill()
            NSColor(srgbRed: 0.03, green: 0.90, blue: 0.92, alpha: 1).setFill()
            NSRect(x: x + 4, y: 0, width: 4, height: 96).fill()
        }
        image.unlockFocus()
        let grid = ColorExtractor.extractPalette(from: image)
        let cover = AlbumWallpaperPalette(grid: grid)
        func red(_ rgb: SIMD3<Float>) -> Bool { rgb.x > 0.65 && rgb.y < 0.2 && rgb.z < 0.2 }
        func cyan(_ rgb: SIMD3<Float>) -> Bool { rgb.x < 0.2 && rgb.y > 0.65 && rgb.z > 0.65 }
        XCTAssertTrue(cover.stops.contains(where: red))
        XCTAssertTrue(cover.stops.contains(where: cyan))
        XCTAssertEqual(grid, ColorExtractor.extractPalette(from: image))

        let director = LyricInkDirector()
        var ink = LyricInk.white
        for step in 0...30 {
            ink = director.ink(.init(mode: .albumDrift, album: cover.artworkStops), wall: Double(step) * 0.2)
        }
        let hues = [ink.stops.0.hue, ink.stops.1.hue, ink.stops.2.hue, ink.stops.3.hue]
        func coverRed(_ hue: Double) -> Bool { (0.3...0.7).contains(hue) }
        func coverCyan(_ hue: Double) -> Bool { abs(hue) > 2.8 }
        XCTAssertTrue(hues.contains(where: coverRed), "cover red")
        XCTAssertTrue(hues.contains(where: coverCyan), "cover cyan")
        XCTAssertTrue(hues.allSatisfy { coverRed($0) || coverCyan($0) }, "all stops use a hue sampled from the cover")
    }

    @MainActor
    func testWallpaperGradientKeepsColourFromAcrossTheCover() {
        let image = NSImage(size: NSSize(width: 96, height: 96))
        image.lockFocus()
        for index in 0..<8 {
            NSColor(hue: CGFloat(index) / 8, saturation: 0.92, brightness: 0.82, alpha: 1).setFill()
            NSRect(x: index * 12, y: 0, width: 12, height: 96).fill()
        }
        image.unlockFocus()
        let cover = AlbumWallpaperPalette(grid: ColorExtractor.extractPalette(from: image))
        XCTAssertEqual(cover.stops.count, 5)
        XCTAssertEqual(cover.wallpaperStops.count, 9)
        let hues = Set(cover.wallpaperStops.map { rgb -> Int in
            let colour = NSColor(srgbRed: CGFloat(rgb.x), green: CGFloat(rgb.y), blue: CGFloat(rgb.z), alpha: 1)
            return Int((colour.hueComponent * 8).rounded()) % 8
        })
        XCTAssertGreaterThanOrEqual(hues.count, 7)
        XCTAssertEqual(cover.gradientStops(vividness: 1), cover.wallpaperStops)
        let measured = cover.gradientLuminance(vividness: 1)
        XCTAssertEqual(LyricBackdrop.beforeBacking(mode: .colourOnly, motion: false, palette: cover.stops,
                                                   scrim: 0, measured: measured), measured, accuracy: 0.0001)
    }

    func testWideRotatedGlyphsStayInsideTheRollingStage() {
        // Use real Mac font metrics, including a deliberately wide word and
        // a Cutout-style label. Exercise centres near both stage edges.
        for font in [NSFont.systemFont(ofSize: 124, weight: .bold),
                     NSFont(name: "Georgia-Bold", size: 124) ?? NSFont.systemFont(ofSize: 124)] {
            let measured = ("WWWWWWWWWWWWWWWW" as NSString).size(withAttributes: [.font: font])
            let ink = CGSize(width: measured.width + 22, height: measured.height + 6)
            for centre: CGFloat in [150, 380, 640] {
                for angle in [-16.0, 0, 16] {
                    let fit = WordStageFit.scale(ink: ink, stage: CGSize(width: 800, height: 500),
                        centre: CGPoint(x: centre, y: 90), scale: 1.3, angle: angle, margin: 30)
                    let radians = angle * .pi / 180
                    let extent = (ink.width * CGFloat(abs(cos(radians)))
                        + ink.height * CGFloat(abs(sin(radians)))) * 1.3 * fit / 2
                    XCTAssertGreaterThan(fit, 0)
                    XCTAssertGreaterThanOrEqual(centre - extent, 30 - 0.001)
                    XCTAssertLessThanOrEqual(centre + extent, 770 + 0.001)
                    let verticalExtent = (ink.height * CGFloat(abs(cos(radians)))
                        + ink.width * CGFloat(abs(sin(radians)))) * 1.3 * fit / 2
                    XCTAssertLessThanOrEqual(verticalExtent, 60 + 0.001)
                }
            }
        }
    }
    func testStageFitDoesNotEnlargeShortWordsAndRejectsInvalidGeometry() {
        XCTAssertEqual(WordStageFit.scale(ink: .init(width: 80, height: 40), stage: CGSize(width: 800, height: 500),
            centre: CGPoint(x: 400, y: 250), scale: 1.2, angle: 5, margin: 30), 1)
        XCTAssertEqual(WordStageFit.scale(ink: .init(width: CGFloat.infinity, height: 40), stage: CGSize(width: 800, height: 500),
            centre: CGPoint(x: 400, y: 250), scale: 1.2, angle: 5, margin: 30), 0)
    }
    @MainActor
    func testConstellationLightingIsContinuousAtWordBoundaries() {
        let token = ScatteredTypography.prepare(key(), settings: .shared)[2]
        func heat(_ time: Double) -> Double {
            ScatteredWordFrame.make(token, nextIndex: nil, time: time,
                composition: .constellationEditorial).heat
        }
        for boundary in [token.source.start, token.source.end, token.source.end + 0.3] {
            XCTAssertEqual(heat(boundary - 0.0001), heat(boundary + 0.0001), accuracy: 0.001)
        }
        XCTAssertGreaterThan(heat(token.source.end + 0.1), heat(token.source.end + 0.2))
    }
    private let words: [LyricsFetcher.Word] = [
        .init(text: "Keep", start: 10, end: 10.3), .init(text: "the", start: 10.3, end: 10.6),
        .init(text: "light", start: 10.6, end: 13), .init(text: "♪", start: 13, end: 13.1),
    ]
    @MainActor
    private func key(size: CGFloat = 44, spread: Double = 1,
                     style: LyricComposition = .constellationEditorial) -> ScatteredTypography.Key {
        .init(words: words, baseSize: size, style: .forLine(0), mood: .neutral,
              composition: style, spread: spread, fontIdentity: LyricFontIdentity(settings: .shared))
    }
    @MainActor
    func testPlaybackAndSeekingReusePreparedTypography() {
        let memo = RenderMemo<ScatteredTypography.Key, [ScatteredTypography.Word]>()
        let source = key()
        for time in [0.0, 10.1, 11, 12, 20, 9, 11] {
            let prepared = memo.value(for: source) { ScatteredTypography.prepare(source, settings: .shared) }
            _ = ScatteredWordFrame.make(prepared[0], nextIndex: 0, time: time, composition: source.composition)
        }
        XCTAssertEqual(memo.builds, 1)
    }
    @MainActor
    func testResizingAndSpreadInvalidateWithoutChangingWordIdentity() {
        let memo = RenderMemo<ScatteredTypography.Key, [ScatteredTypography.Word]>()
        func value(_ input: ScatteredTypography.Key) -> [ScatteredTypography.Word] {
            memo.value(for: input) { ScatteredTypography.prepare(input, settings: .shared) }
        }
        let first = value(key()), doubled = value(key(size: 88))
        XCTAssertEqual(doubled[0].metrics.size, first[0].metrics.size * 2, accuracy: 0.0001)
        let wider = value(key(size: 88, spread: 1.5))
        XCTAssertNotEqual(wider[0].metrics.waveOffset, doubled[0].metrics.waveOffset)
        XCTAssertEqual(first.map(\.source.presentationID), wider.map(\.source.presentationID))
        XCTAssertEqual(memo.builds, 3)
    }
    @MainActor
    func testEditorialAmountInvalidatesPreparedTypeWithToggleOn() {
        let settings = Settings.shared
        let wasEnabled = settings.editorialTypography, savedAmount = settings.editorialAmount
        defer { settings.editorialTypography = wasEnabled; settings.editorialAmount = savedAmount }
        settings.editorialTypography = true
        let memo = RenderMemo<ScatteredTypography.Key, [ScatteredTypography.Word]>()
        settings.editorialAmount = 0
        let lowKey = key()
        let low = memo.value(for: lowKey) { ScatteredTypography.prepare(lowKey, settings: settings) }
        settings.editorialAmount = 1
        let highKey = key()
        let high = memo.value(for: highKey) { ScatteredTypography.prepare(highKey, settings: settings) }
        XCTAssertEqual(memo.builds, 2)
        XCTAssertNotEqual(low.map(\.metrics), high.map(\.metrics))
    }
    @MainActor
    func testFontSwitchInvalidatesCachedGlyphs() {
        let settings = Settings.shared
        let saved = settings.fontChoice, savedRandomize = settings.randomizeLyricFonts
        defer {
            settings.fontChoice = saved
            settings.randomizeLyricFonts = savedRandomize
        }
        settings.randomizeLyricFonts = false
        let memo = RenderMemo<ScatteredTypography.Key, [ScatteredTypography.Word]>()
        settings.fontChoice = .sans
        let firstKey = key(), first = memo.value(for: firstKey) { ScatteredTypography.prepare(firstKey, settings: settings) }
        settings.fontChoice = .serif
        let secondKey = key(), second = memo.value(for: secondKey) { ScatteredTypography.prepare(secondKey, settings: settings) }
        XCTAssertNotEqual(firstKey, secondKey)
        // Index 0 uses Editorial's alternate face, which intentionally stays
        // independent of the selected regular lyric font.
        XCTAssertNotEqual(first[1].font, second[1].font)
        XCTAssertEqual(memo.builds, 2)
    }
    @MainActor
    func testLateFontIdentityAndNewReadingInvalidatePreparation() {
        var input = key()
        let loaded = LyricFontIdentity(choice: input.fontIdentity.choice, customRegular: nil,
            customBold: nil, appRegular: "LoadedRegular", appBold: "LoadedBold")
        let withFont = ScatteredTypography.Key(words: input.words, baseSize: input.baseSize, style: input.style,
            mood: input.mood, composition: input.composition, spread: input.spread, fontIdentity: loaded)
        XCTAssertNotEqual(input, withFont)
        input = .init(words: [.init(text: "Hikari", start: 10, end: 13)], baseSize: input.baseSize,
            style: input.style, mood: input.mood, composition: input.composition, spread: input.spread,
            fontIdentity: input.fontIdentity)
        XCTAssertNotEqual(input, key())
        XCTAssertEqual(ScatteredTypography.prepare(input, settings: .shared).first?.source.text, "Hikari")
    }
    @MainActor
    func testStableInkFramesStillChangeAtArrivalAndOnBackwardsSeek() {
        let settings = Settings.shared
        let saved = settings.wordRevealByStyle
        defer { settings.wordRevealByStyle = saved }
        settings.wordRevealByStyle[LyricComposition.scattered.rawValue] = true
        let token = ScatteredTypography.prepare(key(style: .scattered), settings: settings)[0]
        func frame(_ time: Double) -> ScatteredWordFrame {
            .make(token, nextIndex: 0, time: time, composition: .scattered)
        }
        XCTAssertEqual(frame(0), frame(8))
        XCTAssertEqual(frame(20), frame(30))
        XCTAssertNotEqual(frame(10.01), frame(10.08))
        XCTAssertNotEqual(frame(20), frame(8))
    }
    @MainActor
    func testPreparationKeepsPunctuationFilteringAndHeldWordGlyphs() {
        let prepared = ScatteredTypography.prepare(key(), settings: .shared)
        XCTAssertEqual(prepared.count, 3)
        XCTAssertEqual(prepared[2].label, "LIGHT")
        XCTAssertEqual(prepared[2].characters.joined(), "LIGHT")
        XCTAssertGreaterThan(prepared[2].sustain, 0.9)
        XCTAssertTrue(prepared[2].metrics.isHero)
    }
    @MainActor
    func testRollingStylesPreserveTimedWordIdentityAcrossWindows() {
        let settings = Settings.shared
        let saved = settings.editorialTypography
        defer { settings.editorialTypography = saved }
        for editorial in [false, true] {
        settings.editorialTypography = editorial
        for style in [LyricComposition.spoken, .cascade, .cutout] {
            let first = SpokenTypography.prepare(.init(words: words, baseSize: 44, mood: .neutral,
                composition: style, fontIdentity: LyricFontIdentity(settings: settings)), settings: settings)
            let shifted = SpokenTypography.prepare(.init(words: Array(words.dropFirst()), baseSize: 44, mood: .neutral,
                composition: style, fontIdentity: LyricFontIdentity(settings: settings)), settings: settings)
            XCTAssertEqual(first[1].id, shifted[0].id)
            XCTAssertEqual(first[1].hash, shifted[0].hash)
            XCTAssertEqual(first[1].font, shifted[0].font)
        }
        }
    }
    func testLineIndexMatchesForwardRuleAtBoundariesAndAfterSeeks() {
        let lines = [2.0, 5, 5, 8].map { LyricsFetcher.Line(start: $0, end: $0 + 2, text: "x", words: [], isInstrumental: false) }
        let index = LyricLineIndex(lines: lines)
        XCTAssertNil(index.line(at: 1.99))
        XCTAssertEqual(index.line(at: 2), 0)
        XCTAssertEqual(index.line(at: 5), 2)
        XCTAssertEqual(index.line(at: 100), 3)
        XCTAssertEqual(index.line(at: 3), 0)
        XCTAssertNil(index.line(at: .nan))
    }
    func testMalformedLineIndexKeepsEarlyStopAndEmptyHandling() {
        let lines = [2.0, 8, 5].map { LyricsFetcher.Line(start: $0, end: $0 + 1, text: "x", words: [], isInstrumental: false) }
        XCTAssertEqual(LyricLineIndex(lines: lines).line(at: 6), 0)
        XCTAssertNil(LyricLineIndex(lines: []).line(at: 6))
    }
}
