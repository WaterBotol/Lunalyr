import Metal
import XCTest
@testable import VersoCustom

/// The lyrics over a GPU effect follow how bright the effect really is
/// behind them (`StageMeter`), not a guess from the cover: with a pale
/// cover the guess was about 0.8, but most effects draw 0.02 to 0.2 there,
/// so the lyrics turned dark and muddy over a dark wallpaper.
@MainActor
final class StageMeterTests: XCTestCase {
    static func palePalette() -> AlbumWallpaperPalette {
        AlbumWallpaperPalette(grid: (0..<9).map { SIMD3(Double($0) / 9, 0.18, 0.95) })
    }

    private func gpu() throws -> ShaderGPU {
        _ = try GPUTestSupport.device()
        return try XCTUnwrap(VisualizerGPU.shared.context(for: nil).map(ShaderGPU.init))
    }

    /// A frame of `style` drawn as the wallpaper draws it, into a texture
    /// the meter can copy (as the effect's drawable now is).
    private func draw(_ style: ShaderStyle, cover: AlbumWallpaperPalette, at time: Double, on gpu: ShaderGPU,
                      into target: MTLTexture, command: MTLCommandBuffer) throws {
        let pipeline = try gpu.device.makeRenderPipelineState(descriptor:
            try XCTUnwrap(ShaderBackgroundRenderer.descriptor(for: style, library: gpu.library)))
        let frame = ShaderBackgroundRenderer.Frame(style: style, time: time, flow: time * 0.7, palette: cover,
                                                   vividness: 1, budget: 1)
        let pass = MTLRenderPassDescriptor()
        pass.colorAttachments[0].texture = target
        pass.colorAttachments[0].loadAction = .clear
        pass.colorAttachments[0].storeAction = .store
        XCTAssertTrue(ShaderBackgroundRenderer.encode(pipeline, frame: frame, clock: SIMD2(Float(time), Float(time * 0.7)),
            points: CGSize(width: 1280, height: 720), pixels: CGSize(width: target.width, height: target.height),
            pass: pass, command: command))
    }

    /// The stage's mean luminance read straight from every pixel.
    private func exact(_ texture: MTLTexture) -> Double {
        var bytes = [UInt8](repeating: 0, count: texture.width * texture.height * 4)
        texture.getBytes(&bytes, bytesPerRow: texture.width * 4,
                         from: MTLRegionMake2D(0, 0, texture.width, texture.height), mipmapLevel: 0)
        return bytes.withUnsafeBytes {
            StageMeter.stageLuminance(bgra: $0, width: texture.width, height: texture.height,
                                      bytesPerRow: texture.width * 4) ?? -1
        }
    }

    func testTheMeterReadsWhatEachEffectDrew() throws {
        let gpu = try gpu()
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: 640, height: 360,
                                                                  mipmapped: false)
        descriptor.usage = [.renderTarget, .shaderRead]
        descriptor.storageMode = .shared
        let target = try XCTUnwrap(gpu.device.makeTexture(descriptor: descriptor))
        let cover = Self.palePalette()
        let guess = LyricBackdrop.beforeBacking(mode: .shader, motion: false, palette: cover.stops, scrim: 1)
        var darkerThanGuessed = 0
        for style in ShaderStyle.allCases {
            let meter = StageMeter()
            var reading: Double?
            let read = expectation(description: style.rawValue)
            meter.onReading = { reading = $0; read.fulfill() }
            let command = try XCTUnwrap(gpu.queue.makeCommandBuffer())
            try draw(style, cover: cover, at: 35, on: gpu, into: target, command: command)
            XCTAssertTrue(meter.measure(target, on: command, at: 100), style.rawValue)
            command.commit()
            wait(for: [read], timeout: 10)
            let truth = exact(target)
            let value = try XCTUnwrap(reading, style.rawValue)
            // Averaged through the mipmaps in encoded values, then rounded:
            // close to the exact mean, never far on the bright side.
            XCTAssertEqual(value, truth, accuracy: max(0.04, truth * 0.35), style.rawValue)
            // The lyric colours take it, under the scrim.
            let behind = LyricBackdrop.beforeBacking(mode: .shader, motion: false, palette: cover.stops, scrim: 1,
                                                     drawn: value)
            XCTAssertEqual(behind, LyricLegibility.darkened(value, by: WallpaperShade.scrimAtStage(strength: 1)),
                           accuracy: 1e-9)
            if behind < LyricInkDirector.lightBelow { darkerThanGuessed += 1 }
            // Not due again until the interval has passed.
            XCTAssertFalse(meter.isDue(at: 100 + StageMeter.interval / 2))
            XCTAssertTrue(meter.isDue(at: 100 + StageMeter.interval))
        }
        // The guess called every effect light enough for dark lyrics; most are not.
        XCTAssertGreaterThan(guess, LyricInkDirector.darkAbove)
        XCTAssertGreaterThan(darkerThanGuessed, ShaderStyle.allCases.count / 2)
    }

    func testADrawnReadingWinsWhateverTheMode() {
        let pale = Self.palePalette().stops
        for mode in Settings.BackgroundMode.allCases {
            let value = LyricBackdrop.beforeBacking(mode: mode, motion: false, palette: pale, scrim: 0, drawn: 0.08)
            XCTAssertEqual(value, 0.08, accuracy: 1e-9, mode.rawValue)
        }
        // Without one, nothing changes.
        XCTAssertEqual(LyricBackdrop.beforeBacking(mode: .shader, motion: false, palette: pale, scrim: 1),
                       LyricBackdrop.beforeBacking(mode: .shader, motion: false, palette: pale, scrim: 1, drawn: nil))
    }

    func testReadingsAreRoundedAndClamped() {
        XCTAssertEqual(StageMeter.rounded(0.333), 0.34, accuracy: 1e-9)
        XCTAssertEqual(StageMeter.rounded(-1), 0)
        XCTAssertEqual(StageMeter.rounded(4), 1)
    }

    func testTheReadLevelIsAboutFortyEightPixelsWide() {
        XCTAssertEqual(StageMeter.level(forWidth: 40), 0)
        XCTAssertEqual(StageMeter.level(forWidth: 1920), 5) // 60 px
        XCTAssertEqual(StageMeter.level(forWidth: 2880), 5) // 90 px
    }

    func testASnapshotIsMeasuredLikeAFrame() throws {
        // Dark all round, white in the middle 70% x 50%.
        let width = 200, height = 100
        let space = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        let context = try XCTUnwrap(CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                              bytesPerRow: 0, space: space,
                                              bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        context.setFillColor(CGColor(gray: 0, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        context.setFillColor(CGColor(gray: 1, alpha: 1))
        context.fill(CGRect(x: 30, y: 25, width: 140, height: 50))
        let image = try XCTUnwrap(context.makeImage())
        XCTAssertEqual(try XCTUnwrap(StageMeter.stageLuminance(of: image)), 1, accuracy: 0.08)
    }
}
