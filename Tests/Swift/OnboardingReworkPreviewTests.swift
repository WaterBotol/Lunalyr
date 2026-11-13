import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Every onboarding step, in its window, light and dark, written as PNGs
/// only when LUNALYR_ONBOARDING_PREVIEW_DIR is set (pass it to
/// `xcodebuild test` as TEST_RUNNER_LUNALYR_ONBOARDING_PREVIEW_DIR).
/// Isolated stores, fake players, nothing fetched or played. Also writes a
/// contact sheet per look (`sheet-light.png`, `sheet-dark.png`).
@MainActor
final class OnboardingReworkPreviewTests: XCTestCase {
    func testWriteEveryStepLightAndDark() throws {
        guard let path = ProcessInfo.processInfo.environment["LUNALYR_ONBOARDING_PREVIEW_DIR"] else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_ONBOARDING_PREVIEW_DIR to write the onboarding previews")
        }
        let directory = URL(fileURLWithPath: path)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        for look in [UISnapshotRenderer.Look.light, .dark] {
            var images: [CGImage] = []
            var pages: [(OnboardingFlow.Step, OnboardingFlow.LookPart)] = OnboardingFlow.Step.allCases.map { ($0, .wallpaper) }
            if let look = pages.firstIndex(where: { $0.0 == .look }) { pages.insert((.look, .lyrics), at: look + 1) }
            for (step, part) in pages {
                let scene = UISnapshotScene(player: .playing)
                defer { scene.tearDown() }
                let image = try UISnapshotRenderer.render(
                    UISnapshotCatalogue.onboardingView(scene, step: step, lookPart: part).lunalyrModels(scene.models)
                        .defaultAppStorage(scene.defaults),
                    chrome: UISnapshotCatalogue.onboardingWindowChrome, size: .frame(LunalyrDesign.Layout.onboarding),
                    look: look, pump: scene.pump)
                let suffix = part == .lyrics ? "-lyrics" : ""
                let name = String(format: "%02d-%@%@-%@.png", step.rawValue + 1, Self.slug(step.title), suffix, look.rawValue)
                try UISnapshotRenderer.writePNG(image, to: directory.appendingPathComponent(name))
                images.append(image)
            }
            try UISnapshotRenderer.writePNG(try Self.sheet(images), to: directory.appendingPathComponent("sheet-\(look.rawValue).png"))
        }
    }

    private static func slug(_ title: String) -> String {
        let kept = title.lowercased().map { $0.isLetter || $0.isNumber ? $0 : "-" }
        return String(kept).split(separator: "-").joined(separator: "-")
    }

    /// The steps side by side, three to a row, on a neutral ground.
    private static func sheet(_ images: [CGImage]) throws -> CGImage {
        guard let first = images.first else { throw UISnapshotRenderer.SnapshotError.noContent }
        let columns = 3, gap = 24
        let rows = (images.count + columns - 1) / columns
        let width = columns * first.width + (columns + 1) * gap
        let height = rows * first.height + (rows + 1) * gap
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { throw UISnapshotRenderer.SnapshotError.noContext }
        context.setFillColor(NSColor(white: 0.5, alpha: 1).cgColor)
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        for (index, image) in images.enumerated() {
            let column = index % columns, row = index / columns
            let x = gap + column * (first.width + gap)
            let y = height - (gap + row * (first.height + gap)) - image.height
            context.draw(image, in: CGRect(x: x, y: y, width: image.width, height: image.height))
        }
        guard let image = context.makeImage() else { throw UISnapshotRenderer.SnapshotError.noContext }
        return image
    }
}
