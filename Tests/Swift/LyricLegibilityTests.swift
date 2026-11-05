import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

final class LyricLegibilityTests: XCTestCase {
    /// A palette whose every stop is one colour, as a plain sleeve gives.
    private func sleeve(_ rgb: SIMD3<Double>) -> AlbumWallpaperPalette {
        var h: CGFloat = 0, s: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
        NSColor(srgbRed: rgb.x, green: rgb.y, blue: rgb.z, alpha: 1).getHue(&h, saturation: &s, brightness: &b, alpha: &a)
        return AlbumWallpaperPalette(grid: Array(repeating: SIMD3(Double(h), Double(s), Double(b)), count: 9))
    }

    /// White type over a backdrop after the shared scrim and the backing.
    private func contrast(luminance: Double, existing: Double, backing: Double) -> Double {
        let after = 1 - (1 - existing) * (1 - backing)
        return LyricLegibility.whiteContrast(over: LyricLegibility.darkened(luminance, by: after))
    }

    func testContrastMathMatchesWCAG() {
        XCTAssertEqual(LyricLegibility.whiteContrast(over: 0), 21, accuracy: 0.001)
        XCTAssertEqual(LyricLegibility.whiteContrast(over: 1), 1, accuracy: 0.001)
        XCTAssertEqual(LyricLegibility.luminance(SIMD3(1, 1, 1)), 1, accuracy: 0.0001)
        XCTAssertEqual(LyricLegibility.luminance(SIMD3(0.5, 0.5, 0.5)), 0.2140, accuracy: 0.001)
        for luminance in [0.0, 0.05, 0.2, 0.6, 1.0] {
            XCTAssertEqual(LyricLegibility.linear(LyricLegibility.encoded(luminance)), luminance, accuracy: 0.0005)
        }
        XCTAssertEqual(LyricLegibility.targetContrast(baseSize: 70), 4.5)
        XCTAssertEqual(LyricLegibility.targetContrast(baseSize: 118), 3)
    }

    func testPaleCoversGetJustEnoughBackingForReadableWhiteType() {
        // The review's worst cases: a white sleeve, pale yellow and lime.
        let scrim = 0.12
        for rgb in [SIMD3(0.98, 0.98, 0.97), SIMD3(1.0, 0.96, 0.62), SIMD3(0.72, 0.95, 0.22)] {
            let palette = sleeve(rgb)
            let luminance = LyricLegibility.colourFieldLuminance(palette, patternOpacity: 0.85)
            XCTAssertLessThan(contrast(luminance: luminance, existing: scrim, backing: 0), 3, "unreadable before")
            let backing = LyricLegibility.backing(forLuminance: luminance, existingDarkening: scrim, target: 4.5)
            XCTAssertGreaterThan(backing, 0)
            XCTAssertLessThanOrEqual(backing, LyricLegibility.maximumBacking)
            XCTAssertGreaterThanOrEqual(contrast(luminance: luminance, existing: scrim, backing: backing), 4.5 - 0.01)
            // Never more than needed: a little less would miss the target.
            XCTAssertLessThan(contrast(luminance: luminance, existing: scrim, backing: max(0, backing - 0.03)), 4.5)
        }
    }

    func testDarkCoversAreLeftAlone() {
        for rgb in [SIMD3(0.08, 0.06, 0.2), SIMD3(0.25, 0.1, 0.3), SIMD3(0.1, 0.2, 0.25)] {
            let luminance = LyricLegibility.colourFieldLuminance(sleeve(rgb), patternOpacity: nil)
            XCTAssertEqual(LyricLegibility.backing(forLuminance: luminance, existingDarkening: 0.12, target: 4.5), 0)
        }
        // The fallback palette used before artwork arrives needs nothing either.
        let fallback = AlbumWallpaperPalette(grid: ColorExtractor.fallbackHSBPalette)
        XCTAssertEqual(LyricLegibility.backing(forLuminance: LyricLegibility.colourFieldLuminance(fallback, patternOpacity: 0.85),
                                               existingDarkening: 0.12, target: 4.5), 0)
    }

    func testBackingIsCappedAndCreditsExistingDarkening() {
        let white = 1.0
        XCTAssertEqual(LyricLegibility.backing(forLuminance: white, existingDarkening: 0, target: 15), LyricLegibility.maximumBacking)
        XCTAssertEqual(LyricLegibility.backing(forLuminance: white, existingDarkening: 0, target: 30), LyricLegibility.maximumBacking)
        let light = LyricLegibility.backing(forLuminance: white, existingDarkening: 0, target: 4.5)
        let darker = LyricLegibility.backing(forLuminance: white, existingDarkening: 0.3, target: 4.5)
        XCTAssertLessThan(darker, light)
        // An animated cover's own heavy darkening already suffices.
        XCTAssertEqual(LyricLegibility.backing(forLuminance: white, existingDarkening: 0.7, target: 4.5), 0)
        // Large type needs less.
        XCTAssertLessThan(LyricLegibility.backing(forLuminance: white, existingDarkening: 0, target: 3), light)
        XCTAssertEqual(LyricLegibility.backing(forLuminance: .nan, existingDarkening: 0, target: 4.5), 0)
    }

    func testShaderAndCoverEstimatesFollowTheirSources() {
        let pale = sleeve(SIMD3(0.95, 0.95, 0.9))
        XCTAssertGreaterThan(LyricLegibility.shaderLuminance(pale), 0.7)
        XCTAssertGreaterThan(LyricLegibility.visualizerLuminance(pale), 0.6)
        let grid = Array(repeating: SIMD3(0.15, 0.1, 0.95), count: 9)
        XCTAssertGreaterThan(LyricLegibility.coverLuminance(hsbGrid: grid), 0.7)
        XCTAssertEqual(LyricLegibility.rgb(hsb: SIMD3(0, 1, 1)).x, 1, accuracy: 0.0001)
        XCTAssertEqual(LyricLegibility.rgb(hsb: SIMD3(1.0 / 3, 1, 1)).y, 1, accuracy: 0.0001)
    }

    func testImageLuminanceReadsTheRegionBehindTheLyrics() throws {
        // White in the middle third of a dark picture: what matters is the
        // middle, where the lyric stage sits.
        let image = try XCTUnwrap(picture(width: 300, height: 200) { context in
            context.setFillColor(CGColor(gray: 0.05, alpha: 1))
            context.fill(CGRect(x: 0, y: 0, width: 300, height: 200))
            context.setFillColor(CGColor(gray: 1, alpha: 1))
            context.fill(CGRect(x: 60, y: 40, width: 180, height: 120))
        })
        let screen = CGSize(width: 1512, height: 982)
        let filled = try XCTUnwrap(LyricLegibility.stageLuminance(of: image, screen: screen, placement: .fill))
        XCTAssertGreaterThan(filled, 0.8)
        let dark = try XCTUnwrap(picture(width: 300, height: 200) { context in
            context.setFillColor(CGColor(gray: 0.1, alpha: 1))
            context.fill(CGRect(x: 0, y: 0, width: 300, height: 200))
        })
        XCTAssertLessThan(try XCTUnwrap(LyricLegibility.stageLuminance(of: dark, screen: screen, placement: .fit)), 0.02)
        XCTAssertNil(LyricLegibility.stageLuminance(of: dark, screen: .zero, placement: .fill))
    }

    func testDecodesASmallCopyOfAPictureOnDisk() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let url = folder.appendingPathComponent("wallpaper.png")
        let image = try XCTUnwrap(picture(width: 1200, height: 800) { context in
            context.setFillColor(CGColor(gray: 0.9, alpha: 1))
            context.fill(CGRect(x: 0, y: 0, width: 1200, height: 800))
        })
        let rep = NSBitmapImageRep(cgImage: image)
        try XCTUnwrap(rep.representation(using: .png, properties: [:])).write(to: url)
        let thumbnail = try XCTUnwrap(LyricLegibility.thumbnail(at: url, maxPixels: 128))
        XCTAssertLessThanOrEqual(max(thumbnail.width, thumbnail.height), 128)
        XCTAssertNil(LyricLegibility.thumbnail(at: folder.appendingPathComponent("missing.png")))
    }

    // MARK: Only behind something

    private let timing = LyricStagePresence.Timing(idleHold: 5, noticeHold: 4, noticeFade: 1)

    private func inputs(hasTrack: Bool = true, idleSince: Date? = nil, loading: Bool = false, timed: Bool = false,
                        plain: Bool = false, notFound: Bool = false, notFoundAt: Date? = nil,
                        noLyrics: VersoCustom.Settings.NoLyricsBehaviour = .notice, shown: Bool = true) -> LyricStagePresence.Inputs {
        .init(hasTrack: hasTrack, idleSince: idleSince, isLoading: loading, hasTimedLines: timed,
              hasPlainLines: plain, notFound: notFound, notFoundAt: notFoundAt, noLyrics: noLyrics, lyricsShown: shown)
    }

    func testHiddenLyricsTakeTheBackingWithThem() {
        let now = Date()
        // Hidden by the user or while a display is mirrored: the stage is
        // empty, so no shade is left over the picture.
        let states = [inputs(timed: true), inputs(plain: true), inputs(loading: true),
                      inputs(notFound: true, noLyrics: .trackName),
                      inputs(notFound: true, notFoundAt: now), inputs(hasTrack: false, idleSince: now)]
        for state in states {
            XCTAssertEqual(LyricStagePresence.presence(state, at: now, timing: timing), 1, "shown: unchanged")
            var hidden = state
            hidden.lyricsShown = false
            XCTAssertEqual(LyricStagePresence.presence(hidden, at: now, timing: timing), 0)
        }
        // Existing callers that never mention it keep the lyrics shown.
        let unspecified = LyricStagePresence.Inputs(hasTrack: true, idleSince: nil, isLoading: false, hasTimedLines: true,
                                                    hasPlainLines: false, notFound: false, notFoundAt: nil, noLyrics: .notice)
        XCTAssertTrue(unspecified.lyricsShown)
    }

    // MARK: Across the stage

    /// White type at a point given in stage sizes from its centre:
    /// (0.5, 0) is the middle of the stage's right edge.
    private func contrastOnStage(x: Double, y: Double, luminance: Double, scrim: Double, amount: Double,
                                 opacity: Double = 1) -> Double {
        let half = LyricLegibility.stageHalfExtent
        let strength = LyricLegibility.backingStrength(x: x * 2 * half.x, y: y * 2 * half.y)
        let after = 1 - (1 - scrim) * (1 - amount * strength)
        return LyricLegibility.contrast(ofWhiteAt: opacity, over: LyricLegibility.darkened(luminance, by: after))
    }

    func testTheBackingReachesItsTargetAcrossTheWholeStage() {
        // A white sleeve under the lightest part of the shared scrim.
        let amount = LyricLegibility.stageBacking(forLuminance: 1, existingDarkening: 0.12, target: 4.5)
        XCTAssertLessThanOrEqual(amount, LyricLegibility.maximumBacking)
        // The ends of a full-width line, the top and bottom of a block of
        // plain lyrics, the corners and everywhere between.
        for x in stride(from: -0.5, through: 0.5, by: 0.05) {
            for y in stride(from: -0.5, through: 0.5, by: 0.05) {
                XCTAssertGreaterThanOrEqual(contrastOnStage(x: x, y: y, luminance: 1, scrim: 0.12, amount: amount),
                                            4.5 - 0.01, "at (\(x), \(y)) of the stage")
            }
        }
    }

    func testTheBackingEasesOutByTheScreenEdgesInsteadOfSpillingPastThem() {
        let strength = LyricLegibility.backingStrength(x:y:)
        // Nothing at all along the screen's edges, corners included, so they
        // read as the picture itself rather than a bright inverted vignette.
        for step in 0...20 {
            let along = Double(step) / 20 - 0.5
            for point in [(along, -0.5), (along, 0.5), (-0.5, along), (0.5, along)] {
                XCTAssertEqual(strength(point.0, point.1), 0, "at \(point) of the screen")
            }
        }
        // Halfway between the stage and the screen's edge, about half.
        let half = LyricLegibility.stageHalfExtent
        XCTAssertEqual(strength((half.x + 0.5) / 2, 0), 0.5, accuracy: 1e-9)
        XCTAssertEqual(strength(0, -(half.y + 0.5) / 2), 0.5, accuracy: 1e-9)
        // Never stronger further out, along the axes and the diagonal.
        for direction in [(1.0, 0.0), (0.0, 1.0), (1.0, 1.0), (-0.8, 0.6)] {
            var last = 1.0
            for step in 0...100 {
                let t = Double(step) / 200
                let value = strength(direction.0 * t, direction.1 * t)
                XCTAssertLessThanOrEqual(value, last + 1e-12)
                last = value
            }
        }
        // Darkened as much as the stage needs and little more: the 1.8x
        // ellipse put 88% of the screen at 0.75 or more, its middle edges
        // at about 0.85 and its corners at none.
        var total = 0.0, strong = 0
        let samples = 200
        for column in 0..<samples {
            for row in 0..<samples {
                let value = strength((Double(column) + 0.5) / Double(samples) - 0.5, (Double(row) + 0.5) / Double(samples) - 0.5)
                total += value
                if value >= 0.75 { strong += 1 }
            }
        }
        let stageShare = 4 * half.x * half.y
        let strongShare = Double(strong) / Double(samples * samples)
        XCTAssertGreaterThanOrEqual(strongShare, stageShare - 0.01, "the whole stage")
        XCTAssertLessThan(strongShare, stageShare + 0.15)
        XCTAssertLessThan(total / Double(samples * samples), 0.8)
    }

    func testTheShadeImageIsThatShape() throws {
        let shade = try XCTUnwrap(LyricLegibility.backingShade)
        let columns = shade.width, rows = shade.height
        XCTAssertEqual(shade.alphaInfo, .premultipliedLast)
        let data = try XCTUnwrap(shade.dataProvider?.data) as Data
        for (column, row) in [(0, 0), (columns / 2, rows / 2), (columns / 10, rows / 2), (columns / 20, rows / 2),
                              (columns - 1, rows / 2), (columns / 2, rows / 14), (columns / 10, rows / 7), (columns - 1, rows - 1)] {
            let offset = row * shade.bytesPerRow + column * 4
            let x = (Double(column) + 0.5) / Double(columns) - 0.5, y = (Double(row) + 0.5) / Double(rows) - 0.5
            XCTAssertEqual(Double(data[offset + 3]) / 255, LyricLegibility.backingStrength(x: x, y: y), accuracy: 0.5 / 255 + 1e-9)
            XCTAssertEqual(data[offset], 0, "black: premultiplied colour stays 0")
        }
        XCTAssertNil(LyricLegibility.shadeImage(columns: 0, rows: 4))
    }

    /// The backing as the wallpaper draws it over white, then how dark it
    /// left each point given in shares of the view from its top-left.
    @MainActor
    private func renderedDarkening(manual: Bool) throws -> (Double, Double) -> Double {
        let size = CGSize(width: 320, height: 180)
        let renderer = ImageRenderer(content: ZStack {
            Color.white
            TextBackingShade(amount: 0.5, manual: manual, size: size)
        }.frame(width: size.width, height: size.height))
        renderer.scale = 1
        let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
        return { x, y in
            let column = min(bitmap.pixelsWide - 1, max(0, Int(x * Double(bitmap.pixelsWide))))
            let row = min(bitmap.pixelsHigh - 1, max(0, Int(y * Double(bitmap.pixelsHigh))))
            let colour = bitmap.colorAt(x: column, y: row)?.usingColorSpace(.sRGB)
            return 1 - Double(colour?.redComponent ?? 1)
        }
    }

    @MainActor
    func testTheDrawnBackingFollowsTheShapeAndAManualAmountKeepsItsEllipse() throws {
        let half = LyricLegibility.stageHalfExtent
        let left = 0.5 - half.x, top = 0.5 - half.y
        let automatic = try renderedDarkening(manual: false)
        let centre = automatic(0.5, 0.5)
        XCTAssertGreaterThan(centre, 0.2)
        // As dark at the stage's edges and corners as in its middle...
        for (x, y) in [(left, 0.5), (1 - left, 0.5), (0.5, top), (0.5, 1 - top), (left, top), (1 - left, 1 - top)] {
            XCTAssertEqual(automatic(x, y), centre, accuracy: 0.03, "at (\(x), \(y))")
        }
        // ...easing out across the margin, to nothing at the screen's edge.
        let margin = automatic(left / 2, 0.5)
        XCTAssertGreaterThan(margin, 0.05)
        XCTAssertLessThan(margin, centre - 0.05)
        for (x, y) in [(0.0, 0.5), (0.999, 0.5), (0.5, 0.0), (0.5, 0.999), (0.0, 0.0), (0.999, 0.999)] {
            XCTAssertLessThan(automatic(x, y), 0.03, "at (\(x), \(y))")
        }

        // The user's own amount keeps its old look: lighter at the stage's
        // ends than its middle, and nothing at its corners.
        let ellipse = try renderedDarkening(manual: true)
        let middle = ellipse(0.5, 0.5)
        XCTAssertEqual(middle, centre, accuracy: 0.03, "the same amount at the centre")
        XCTAssertLessThan(ellipse(left, 0.5), middle * 0.75)
        XCTAssertLessThan(ellipse(left, top), 0.03)
    }

    func testReadingsAndTranslationsAreCountedToo() {
        // The contrast model agrees with plain white.
        for luminance in [0.0, 0.1, 0.4, 1.0] {
            XCTAssertEqual(LyricLegibility.contrast(ofWhiteAt: 1, over: luminance),
                           LyricLegibility.whiteContrast(over: luminance), accuracy: 1e-9)
        }
        XCTAssertLessThan(LyricLegibility.contrast(ofWhiteAt: 0.7, over: 0.2), LyricLegibility.whiteContrast(over: 0.2))
        let ceiling = LyricLegibility.secondaryTextCeiling
        XCTAssertGreaterThan(ceiling, 0)
        XCTAssertEqual(LyricLegibility.contrast(ofWhiteAt: 0.7, over: ceiling), 3, accuracy: 0.001)

        let pale = 1.0, scrim = 0.12
        // Ordinary sizes: the lyric's 4.5:1 already keeps 70% white at 3:1.
        let ordinary = LyricLegibility.stageBacking(forLuminance: pale, existingDarkening: scrim, target: 4.5)
        XCTAssertEqual(ordinary, LyricLegibility.backing(forLuminance: pale, existingDarkening: scrim, target: 4.5), accuracy: 1e-9)
        XCTAssertGreaterThanOrEqual(contrastOnStage(x: 0.5, y: 0, luminance: pale, scrim: scrim, amount: ordinary, opacity: 0.7),
                                    3 - 0.01)
        // Very large type is held to 3:1 itself, which would leave its
        // readings near 2.3:1: they decide the amount there.
        let large = LyricLegibility.stageBacking(forLuminance: pale, existingDarkening: scrim, target: 3)
        let whiteOnly = LyricLegibility.backing(forLuminance: pale, existingDarkening: scrim, target: 3)
        XCTAssertLessThan(contrastOnStage(x: 0, y: 0, luminance: pale, scrim: scrim, amount: whiteOnly, opacity: 0.7), 2.5)
        XCTAssertGreaterThan(large, whiteOnly)
        XCTAssertLessThanOrEqual(large, LyricLegibility.maximumBacking)
        XCTAssertGreaterThanOrEqual(contrastOnStage(x: 0, y: 0.5, luminance: pale, scrim: scrim, amount: large, opacity: 0.7),
                                    3 - 0.01)
        // Dark covers still need nothing.
        XCTAssertEqual(LyricLegibility.stageBacking(forLuminance: 0.02, existingDarkening: scrim, target: 3), 0)
        XCTAssertEqual(LyricLegibility.stageBacking(forLuminance: .nan, existingDarkening: scrim, target: 4.5), 0)
    }

    func testBackingIsDrawnOnlyWhileTheStageShowsSomething() {
        let now = Date()
        // Lyrics, plain lines, a loading spinner or a track name: in use.
        XCTAssertEqual(LyricStagePresence.presence(inputs(timed: true), at: now, timing: timing), 1)
        XCTAssertEqual(LyricStagePresence.presence(inputs(plain: true), at: now, timing: timing), 1)
        XCTAssertEqual(LyricStagePresence.presence(inputs(loading: true), at: now, timing: timing), 1)
        XCTAssertEqual(LyricStagePresence.presence(inputs(notFound: true, noLyrics: .trackName), at: now, timing: timing), 1)
        // Missing lyrics set to show nothing: an empty stage.
        XCTAssertEqual(LyricStagePresence.presence(inputs(notFound: true, noLyrics: .nothing), at: now, timing: timing), 0)
        // Nothing has played since launch.
        XCTAssertEqual(LyricStagePresence.presence(inputs(hasTrack: false), at: now, timing: timing), 0)
    }

    func testBackingFadesAwayWithTheNoticesSoAStoppedPlayerLeavesTheDesktopAlone() {
        let stopped = Date()
        let idle = inputs(hasTrack: false, idleSince: stopped)
        // "Nothing playing" holds for five seconds, then fades over one.
        XCTAssertEqual(LyricStagePresence.presence(idle, at: stopped.addingTimeInterval(4.9), timing: timing), 1)
        let midway = LyricStagePresence.presence(idle, at: stopped.addingTimeInterval(5.5), timing: timing)
        XCTAssertGreaterThan(midway, 0.2)
        XCTAssertLessThan(midway, 0.8)
        XCTAssertEqual(LyricStagePresence.presence(idle, at: stopped.addingTimeInterval(6), timing: timing), 0)
        XCTAssertEqual(LyricStagePresence.presence(idle, at: stopped.addingTimeInterval(3600), timing: timing), 0)
        // "Lyrics unavailable" holds for four.
        let missing = inputs(notFound: true, notFoundAt: stopped)
        XCTAssertEqual(LyricStagePresence.presence(missing, at: stopped.addingTimeInterval(3), timing: timing), 1)
        XCTAssertEqual(LyricStagePresence.presence(missing, at: stopped.addingTimeInterval(5), timing: timing), 0)
        XCTAssertEqual(LyricStagePresence.presence(inputs(notFound: true), at: stopped, timing: timing), 0,
                       "no notice up, nothing to back")
    }

    @MainActor
    func testNotificationObservationIsRemovedWithItsOwner() {
        let center = NotificationCenter()
        let name = Notification.Name("LunalyrTests.observation")
        var calls = 0
        var observation: NotificationObservation? = NotificationObservation(center, name) { calls += 1 }
        // Delivered on the main queue.
        let settle = { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
        center.post(name: name, object: nil)
        settle()
        XCTAssertEqual(calls, 1)
        XCTAssertNotNil(observation)
        observation = nil
        center.post(name: name, object: nil)
        settle()
        XCTAssertEqual(calls, 1, "a released owner no longer hears the notification")
    }

    private func picture(width: Int, height: Int, draw: (CGContext) -> Void) -> CGImage? {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        draw(context)
        return context.makeImage()
    }
}
