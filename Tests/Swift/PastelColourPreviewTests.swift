import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Lyric colours with and without the pastel easing, over a few album
/// palettes and custom gradients and three backdrops, for judging whether
/// unpastelled colours read well. Preview only: skipped unless
/// LUNALYR_PASTEL_PREVIEWS names a folder (pass it to `xcodebuild test` as
/// TEST_RUNNER_LUNALYR_PASTEL_PREVIEWS=<dir>).
@MainActor
final class PastelColourPreviewTests: XCTestCase {
    static let palettes: [(String, [SIMD3<Float>])] = [
        ("warm", [SIMD3(0.95, 0.55, 0.42), SIMD3(0.35, 0.25, 0.55), SIMD3(0.15, 0.12, 0.3)]),
        ("navy-maroon", [SIMD3(0.08, 0.12, 0.35), SIMD3(0.4, 0.06, 0.1), SIMD3(0.1, 0.1, 0.12)]),
        ("neon", [SIMD3(1, 0.05, 0.6), SIMD3(0.1, 1, 0.3), SIMD3(0.1, 0.4, 1)]),
        ("near-grey", [SIMD3(0.55, 0.53, 0.5), SIMD3(0.3, 0.31, 0.33), SIMD3(0.7, 0.68, 0.66)]),
        ("earthy", [SIMD3(0.45, 0.32, 0.18), SIMD3(0.3, 0.38, 0.2), SIMD3(0.65, 0.5, 0.3)]),
    ]
    static let customs: [(String, [String])] = [
        ("custom-default", LyricColourOptions.defaultCustom),
        ("custom-deep", ["#1E3A8A", "#B91C1C", "#047857"]),
        ("custom-neon", ["#FF00FF", "#00FFFF", "#FFFF00"]),
    ]
    static let backdrops: [(String, Double, Color)] = [
        ("dark", 0.03, Color(white: 0.12)),
        ("video", 0.15, Color(white: 0.38)),
        ("light", 0.7, Color(white: 0.86)),
    ]

    func testRenderPastelComparison() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let path = environment["LUNALYR_PASTEL_PREVIEWS"] ?? environment["TEST_RUNNER_LUNALYR_PASTEL_PREVIEWS"],
              !path.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_PASTEL_PREVIEWS=<dir> to render the pastel comparison")
        }
        let folder = URL(fileURLWithPath: path)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var rows: [(String, LyricColourMode, [SIMD3<Float>], [OKLab])] = Self.palettes.map { ($0.0, .albumDrift, $0.1, []) }
        rows += Self.customs.map { ($0.0, .customGradient, Self.palettes[0].1, $0.1.compactMap(LyricColourOptions.oklab(hex:))) }
        for (backdropName, luminance, fill) in Self.backdrops {
            let view = VStack(alignment: .leading, spacing: 6) {
                ForEach(Array(rows.enumerated()), id: \.offset) { _, row in
                    HStack(spacing: 24) {
                        Text(row.0).font(.system(size: 13, weight: .semibold)).frame(width: 130, alignment: .leading)
                            .foregroundStyle(luminance > 0.4 ? Color.black : .white)
                        ForEach([true, false], id: \.self) { pastel in
                            Self.sample(mode: row.1, album: row.2, custom: row.3, backdrop: luminance, pastel: pastel)
                                .frame(width: 520, alignment: .leading)
                        }
                    }
                }
            }
            .padding(24)
            .background(fill)
            let renderer = ImageRenderer(content: view)
            renderer.scale = 1
            let image = try XCTUnwrap(renderer.cgImage)
            try DesktopPlayerPreviewTests.write(image, to: folder.appendingPathComponent("pastel-\(backdropName).png"))
        }
    }

    /// A line in the ink's colours, settled.
    static func sample(mode: LyricColourMode, album: [SIMD3<Float>], custom: [OKLab], backdrop: Double,
                       pastel: Bool) -> some View {
        let director = LyricInkDirector()
        var inputs = LyricInkDirector.Inputs(mode: mode, custom: custom, album: album, backdrop: backdrop)
        inputs.pastel = pastel
        var ink = director.ink(inputs, wall: 0)
        for step in 1...80 { ink = director.ink(inputs, wall: Double(step) * 0.25) }
        let words = ["Golden", "hour", "static", "on", "the", "radio"]
        return HStack(spacing: 8) {
            ForEach(Array(words.enumerated()), id: \.offset) { index, word in
                let sample = ink.sample(slot: Double(index) / Double(words.count))
                Text(word).font(.system(size: 30, weight: .heavy)).foregroundStyle(sample.rest)
                    .shadow(color: sample.glow.opacity(0.5), radius: 6)
            }
        }
    }
}
