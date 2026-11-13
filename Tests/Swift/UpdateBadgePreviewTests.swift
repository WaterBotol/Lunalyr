import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Renders the update badge to PNGs: the menu-bar icon plain, with an
/// update waiting (`MenuBarView.updateIconStyle`), with that and the pause
/// badge, and paused alone, on a light and a dark
/// menu bar at 2x and 6x (a template coloured as macOS colours it there); the
/// panel's update row alone; the whole panel with it, light and dark; and
/// Settings › General with an update waiting. Preview only: no updater
/// starts, nothing is fetched, no window opens on screen.
/// Skipped unless LUNALYR_UPDATE_PREVIEWS names a folder (pass it to
/// `xcodebuild test` as TEST_RUNNER_LUNALYR_UPDATE_PREVIEWS=<dir>).
@MainActor
final class UpdateBadgePreviewTests: XCTestCase {
    func testRenderUpdateBadgePreviews() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let path = environment["LUNALYR_UPDATE_PREVIEWS"] ?? environment["TEST_RUNNER_LUNALYR_UPDATE_PREVIEWS"],
              !path.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_UPDATE_PREVIEWS=<dir> to render the update badge previews")
        }
        let folder = URL(fileURLWithPath: path)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        let icon = try XCTUnwrap(NSImage(named: "MenuBarIconTemplate"))
        icon.size = NSSize(width: 18, height: 18)
        icon.isTemplate = true
        // At 2x, as on a Retina menu bar, and at 6x to see the detail.
        for scale in [2, 6] as [CGFloat] {
            let variants: [(String, NSImage)] = [
                ("plain", icon),
                ("update", MenuBarView.badgedIcon(from: icon, paused: false, updateAvailable: true, scales: [scale])),
                ("update-paused", MenuBarView.badgedIcon(from: icon, paused: true, updateAvailable: true, scales: [scale])),
                ("paused", MenuBarView.badgedIcon(from: icon, paused: true, updateAvailable: false, scales: [scale])),
            ]
            for dark in [false, true] {
                let bar = dark ? "dark" : "light", suffix = "@\(Int(scale))x"
                for (name, image) in variants.prefix(3) {
                    try DesktopPlayerPreviewTests.write(try menuBar([image], dark: dark, scale: scale),
                                                        to: folder.appendingPathComponent("icon-\(name)-\(bar)\(suffix).png"))
                }
                try DesktopPlayerPreviewTests.write(try menuBar(variants.map(\.1), dark: dark, scale: scale),
                                                    to: folder.appendingPathComponent("icons-side-by-side-\(bar)\(suffix).png"))
            }
        }

        for look in [UISnapshotRenderer.Look.light, .dark] {
            let scene = UISnapshotScene(player: .playing)
            defer { scene.tearDown() }
            scene.lyricNow = UISnapshotCatalogue.menuBarSinging
            scene.updates.receive(.found(version: "2.1.0", userInitiated: false, sparkleShowsIt: false))
            let row = MenuBarUpdateBanner()
                .padding(LunalyrDesign.Spacing.panel)
                .frame(width: LunalyrDesign.Layout.menuPanelWidth)
                .styledCanvas()
                .lunalyrTint()
                .lunalyrModels(scene.models).defaultAppStorage(scene.defaults)
            try DesktopPlayerPreviewTests.write(
                try UISnapshotRenderer.render(row, chrome: .none, size: .fitting(width: 0, maxHeight: 400), look: look,
                                              pump: scene.pump),
                to: folder.appendingPathComponent("panel-update-row-\(look.rawValue).png"))
            let panel = MenuBarView().lunalyrModels(scene.models).defaultAppStorage(scene.defaults)
            try DesktopPlayerPreviewTests.write(
                try UISnapshotRenderer.render(panel, chrome: .none, size: .fitting(width: 0, maxHeight: 2000), look: look,
                                              pump: scene.pump),
                to: folder.appendingPathComponent("panel-whole-\(look.rawValue).png"))
            let general = GeneralPage().lunalyrModels(scene.models).defaultAppStorage(scene.defaults)
            try DesktopPlayerPreviewTests.write(
                try UISnapshotRenderer.render(general, chrome: .none, size: .fitting(width: 620, maxHeight: 2000), look: look,
                                              pump: scene.pump),
                to: folder.appendingPathComponent("settings-general-\(look.rawValue).png"))
        }
    }

    /// A strip of menu bar at 2x with the icons on it, each drawn the way
    /// macOS draws a template image there: its alpha, in the bar's text
    /// colour.
    private func menuBar(_ icons: [NSImage], dark: Bool, scale: CGFloat) throws -> CGImage {
        let point = CGSize(width: 18, height: 18), gap: CGFloat = 16
        let size = CGSize(width: gap + CGFloat(icons.count) * (point.width + gap), height: 24)
        let context = try XCTUnwrap(CGContext(data: nil, width: Int(size.width * scale), height: Int(size.height * scale),
                                              bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.scaleBy(x: scale, y: scale)
        context.setFillColor(dark ? CGColor(srgbRed: 0.16, green: 0.16, blue: 0.18, alpha: 1)
                                  : CGColor(srgbRed: 0.93, green: 0.93, blue: 0.94, alpha: 1))
        context.fill(CGRect(origin: .zero, size: size))
        let ink = dark ? CGColor(gray: 1, alpha: 0.92) : CGColor(gray: 0, alpha: 0.85)
        for (index, icon) in icons.enumerated() {
            var rect = CGRect(origin: CGPoint(x: gap + CGFloat(index) * (point.width + gap), y: (size.height - point.height) / 2),
                              size: point)
            let mask = try XCTUnwrap(icon.cgImage(forProposedRect: &rect, context: nil,
                                                  hints: [.ctm: NSAffineTransform(transform: AffineTransform(scale: scale))]))
            context.saveGState()
            guard icon.isTemplate else {
                // In colour: drawn as it is.
                context.draw(mask, in: rect)
                context.restoreGState()
                continue
            }
            context.clip(to: rect, mask: try alphaMask(mask))
            context.setFillColor(ink)
            context.fill(rect)
            context.restoreGState()
        }
        return try XCTUnwrap(context.makeImage())
    }

    /// The image's alpha as a greyscale mask.
    private func alphaMask(_ image: CGImage) throws -> CGImage {
        let context = try XCTUnwrap(CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8,
                                              bytesPerRow: 0, space: CGColorSpaceCreateDeviceGray(),
                                              bitmapInfo: CGImageAlphaInfo.alphaOnly.rawValue))
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        let alpha = try XCTUnwrap(context.data).assumingMemoryBound(to: UInt8.self)
        let grey = try XCTUnwrap(CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8,
                                           bytesPerRow: image.width, space: CGColorSpaceCreateDeviceGray(),
                                           bitmapInfo: CGImageAlphaInfo.none.rawValue))
        let out = try XCTUnwrap(grey.data).assumingMemoryBound(to: UInt8.self)
        for row in 0..<image.height {
            for column in 0..<image.width {
                out[row * image.width + column] = alpha[row * context.bytesPerRow + column]
            }
        }
        return try XCTUnwrap(grey.makeImage())
    }
}
