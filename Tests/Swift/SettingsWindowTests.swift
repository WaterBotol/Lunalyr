import AppKit
import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import VersoCustom

/// The Settings window must fit small screens, open beside the lyrics, and
/// put a usable picture behind the Your wallpaper preview.
@MainActor
final class SettingsWindowTests: XCTestCase {
    func testFirstSettingsFrameTakesMostOfTheScreenAtItsTrailingEdge() {
        let screens: [(visible: NSRect, size: CGSize)] = [
            (NSRect(x: 0, y: 66, width: 1470, height: 853), CGSize(width: 1102, height: 780)),     // 13-inch Air, Dock showing
            (NSRect(x: 0, y: 66, width: 1024, height: 550), CGSize(width: 984, height: 510)),      // 13-inch Air at Larger Text
            (NSRect(x: 0, y: 70, width: 1440, height: 806), CGSize(width: 1080, height: 766)),     // 14-inch with Dock
            (NSRect(x: 0, y: 0, width: 2560, height: 1415), CGSize(width: 1200, height: 780)),     // 27-inch
            (NSRect(x: 1440, y: -200, width: 1920, height: 1055), CGSize(width: 1200, height: 780)), // secondary display
            (NSRect(x: 0, y: 0, width: 1279, height: 760), CGSize(width: 1200, height: 720)),      // never past the maximum
        ]
        for (visible, size) in screens {
            let frame = SettingsWindowController.initialFrame(in: visible)
            XCTAssertEqual(frame.size, size, "on \(visible)")
            XCTAssertTrue(visible.contains(frame), "\(frame) spills out of \(visible)")
            XCTAssertEqual(frame.maxX, visible.maxX - 20, accuracy: 1, "at the trailing edge, near the menu bar extra")
            XCTAssertEqual(frame.maxY, visible.maxY - 20, accuracy: 1)
            XCTAssertLessThanOrEqual(frame.width, LunalyrDesign.Layout.settingsMaximum.width)
            XCTAssertLessThanOrEqual(frame.height, LunalyrDesign.Layout.settingsMaximum.height)
            if visible.width >= 1024 {
                XCTAssertGreaterThanOrEqual(frame.width, SettingsWindowController.minimumWidth)
            }
        }
        // The minimum fits the smallest of those screens with room to spare.
        XCTAssertLessThanOrEqual(SettingsWindowController.minimumHeight, 550 - 40)
    }

    /// The 13-inch Air's default window: the detail beside a 210-point
    /// sidebar, the 380-point inspector, a 52-point toolbar and the stage's
    /// 20-point margins.
    private let airStage = CGSize(width: 1102 - 210 - 380 - 1 - 40, height: 780 - 52 - 40)
    private let airDesktop = CGSize(width: 1470, height: 956)

    func testThePreviewTakesTheWholeColumnBesideTheInspector() {
        let aspect = airDesktop.width / airDesktop.height
        let plan = SettingsStagePlan.make(room: airStage, aspect: aspect, stacked: nil,
                                          chip: nil, strip: { _ in 40 }, banner: nil)
        XCTAssertEqual(plan.preview.width, airStage.width, accuracy: 1, "as wide as the column")
        XCTAssertEqual(plan.preview.width / plan.preview.height, aspect, accuracy: 0.02, "the desktop's shape")
        XCTAssertLessThanOrEqual(plan.height, airStage.height)
        XCTAssertFalse(plan.beside)
        // Larger Preview narrows the inspector to 320 points: a larger picture.
        let larger = SettingsStagePlan.make(room: CGSize(width: airStage.width + 60, height: airStage.height), aspect: aspect,
                                            stacked: nil, chip: nil, strip: { _ in 40 }, banner: nil)
        XCTAssertGreaterThan(larger.preview.width, plan.preview.width)
    }

    func testABannerAndChipShrinkThePreviewRatherThanSpillOut() {
        let aspect = airDesktop.width / airDesktop.height
        // Larger Text: a short window, with the wallpaper paused and Music refused.
        let room = CGSize(width: 984 - 210 - 380 - 1 - 40, height: 510 - 52 - 40)
        // The banner wraps taller as it narrows; it keeps the column's width.
        let banner: (CGFloat) -> CGFloat = { width in 60 + 12_000 / width }
        let plan = SettingsStagePlan.make(room: room, aspect: aspect, stacked: nil,
                                          chip: { _ in 26 }, strip: { _ in 40 }, banner: banner)
        XCTAssertLessThanOrEqual(plan.height, room.height + 1, "everything stays in the column")
        XCTAssertLessThan(plan.preview.width, room.width, "the preview gives way")
        XCTAssertGreaterThanOrEqual(plan.preview.width, SettingsStagePlan.minimumPreviewWidth)
        let expected = 26 + SettingsStagePlan.chipSpacing + plan.preview.height + SettingsStagePlan.spacing * 2 + 40
            + banner(room.width)
        XCTAssertEqual(plan.height, expected, accuracy: 0.5, "chip, preview, strip and banner")
    }

    func testAStackedStageKeepsThePreviewToItsShareOfTheWindow() {
        let aspect = airDesktop.width / airDesktop.height
        // A 700 x 700 window: the detail is narrower than the stage and the inspector need side by side.
        let detail = CGSize(width: 700 - 210, height: 700 - 52)
        for large in [false, true] {
            let limits = SettingsStageLimits(detailHeight: detail.height, largePreview: large)
            XCTAssertEqual(limits.preview, detail.height * (large ? 0.6 : 0.4), accuracy: 0.01)
            let plan = SettingsStagePlan.make(room: CGSize(width: detail.width - 40, height: 0), aspect: aspect,
                                              stacked: limits, chip: nil, strip: { _ in 40 }, banner: { _ in 120 })
            XCTAssertLessThanOrEqual(plan.preview.height, limits.preview + 0.5)
            XCTAssertLessThanOrEqual(plan.preview.width, detail.width - 40 + 0.5)
            XCTAssertLessThanOrEqual(plan.height + 40, limits.stage + 0.5, "the inspector keeps a quarter of the window")
        }
        // The smallest window puts the song beside the preview.
        let small = SettingsStagePlan.make(room: CGSize(width: 490 - 40, height: 0), aspect: aspect,
                                           stacked: SettingsStageLimits(detailHeight: 420, largePreview: false),
                                           chip: nil, strip: { _ in 72 }, banner: nil)
        XCTAssertTrue(small.beside)
        XCTAssertLessThanOrEqual(small.preview.width + SettingsStagePlan.spacing + SettingsStagePlan.besideColumnMinimum, 450)
        XCTAssertEqual(small.height, small.preview.height, accuracy: 0.5, "the song fits beside the preview's height")
        // A portrait display is height-bound and centred.
        let portrait = SettingsStagePlan.make(room: airStage, aspect: 1080.0 / 1920.0, stacked: nil,
                                              chip: nil, strip: { _ in 40 }, banner: nil)
        XCTAssertLessThan(portrait.preview.width, airStage.width)
        XCTAssertLessThanOrEqual(portrait.height, airStage.height + 0.5)
    }

    /// Pages remembered by earlier versions still open, and every place
    /// another surface links to lands on a page.
    func testStoredPagesAndDestinationsStillOpen() {
        for name in ["wallpaper", "typography", "lyrics", "readings", "covers", "performance", "extras", "share", "about"] {
            XCTAssertNotNil(SettingsPage(rawValue: name), "a page remembered as \(name) no longer opens")
        }
        XCTAssertEqual(SettingsPage(.musicResponse), .wallpaper)
        XCTAssertEqual(SettingsAnchor(.musicResponse), .musicResponse)
        XCTAssertEqual(SettingsPage(.players), .general)
        XCTAssertEqual(SettingsAnchor(.players), .players)
        XCTAssertEqual(SettingsPage(.timing), .lyrics)
        XCTAssertEqual(SettingsPage(.menuBarAndShortcuts), .extras)
        XCTAssertNil(SettingsAnchor(.wallpaper))
        // Every page is in exactly one sidebar section, About last.
        let listed = SettingsPage.Group.allCases.flatMap(\.pages)
        XCTAssertEqual(Set(listed), Set(SettingsPage.allCases))
        XCTAssertEqual(listed.count, SettingsPage.allCases.count)
        XCTAssertEqual(listed.last, .about)
        XCTAssertFalse(SettingsPage.about.showsStage)
    }

    /// Opening Settings at a destination (the menu panel's Options…, a
    /// status's fix) shows that page, and the request is used once.
    func testSettingsShowsThePageAnotherSurfaceAsksFor() throws {
        let scene = UISnapshotScene(player: .playing)
        defer { scene.tearDown() }
        scene.showSettingsPage("about")
        scene.navigation.openSettings(.timing)
        XCTAssertEqual(scene.navigated, ["settings:timing"])
        _ = try UISnapshotRenderer.render(
            UISnapshotCatalogue.settingsWindow(scene).lunalyrModels(scene.models).defaultAppStorage(scene.defaults),
            chrome: UISnapshotCatalogue.settingsWindowChrome, size: .frame(CGSize(width: 1102, height: 780)),
            look: .light, pump: scene.pump)
        XCTAssertEqual(scene.defaults.string(forKey: "custom.settingsPage"), "lyrics")
        XCTAssertNil(scene.navigation.settingsRequest, "the request is used once")
    }

    /// Every symbol the Settings window names exists on this Mac.
    func testEverySymbolSettingsNamesExists() {
        let symbols = SettingsPage.allCases.map(\.symbol) + [
            "battery.50percent", "leaf.fill", "play.rectangle", "play.rectangle.fill", "music.note", "pin.fill", "waveform",
            "chevron.up", "chevron.down", "minus.circle", "bookmark.fill", "exclamationmark.triangle.fill",
            "questionmark.circle", "character.book.closed", "eye", "eye.slash", "arrow.up.left.and.arrow.down.right",
            "arrow.down.right.and.arrow.up.left", "pause.rectangle.fill", "rectangle.on.rectangle",
        ]
        for symbol in symbols {
            XCTAssertNotNil(NSImage(systemSymbolName: symbol, accessibilityDescription: nil), "\(symbol) is not an SF Symbol")
        }
    }

    /// The workspace accent fills controls under white and, as a second
    /// shade, colours text and symbols on the window.
    func testAccentShadesKeepTheirContrastInEveryAppearance() throws {
        func luminance(_ color: NSColor) throws -> Double {
            let c = try XCTUnwrap(color.usingColorSpace(.sRGB))
            func channel(_ value: CGFloat) -> Double {
                let v = Double(value)
                return v <= 0.04045 ? v / 12.92 : pow((v + 0.055) / 1.055, 2.4)
            }
            return 0.2126 * channel(c.redComponent) + 0.7152 * channel(c.greenComponent) + 0.0722 * channel(c.blueComponent)
        }
        func contrast(_ a: NSColor, _ b: NSColor) throws -> Double {
            let (x, y) = (try luminance(a), try luminance(b))
            return (max(x, y) + 0.05) / (min(x, y) + 0.05)
        }
        func grey(_ level: Int) -> NSColor { NSColor(srgbRed: CGFloat(level) / 255, green: CGFloat(level) / 255, blue: CGFloat(level) / 255, alpha: 1) }

        for dark in [false, true] {
            let appearance = try XCTUnwrap(NSAppearance(named: dark ? .darkAqua : .aqua))
            var window = NSColor.windowBackgroundColor
            appearance.performAsCurrentDrawingAppearance {
                window = NSColor.windowBackgroundColor.usingColorSpace(.sRGB) ?? window
            }
            // Across supported releases, windows and grouped rows run from
            // #1E1E1E to #323232 in dark mode and from white to #ECECEC in
            // light mode; this Mac's own window is checked as well.
            let backgrounds = [window] + (dark ? [grey(0x1E), grey(0x32)] : [grey(0xFF), grey(0xEC)])
            for increaseContrast in [false, true] {
                let fill = LunalyrAccent.fill(dark: dark, increaseContrast: increaseContrast)
                let text = LunalyrAccent.text(dark: dark, increaseContrast: increaseContrast)
                let label = "dark: \(dark), increase contrast: \(increaseContrast)"
                XCTAssertGreaterThanOrEqual(try contrast(.white, fill), 4.5, "white on the fill, \(label)")
                for background in backgrounds {
                    XCTAssertGreaterThanOrEqual(try contrast(text, background), 4.5, "text on \(background), \(label)")
                    // In dark mode a fill white reads on can't also reach
                    // 3:1 against #323232; in light mode it does both.
                    if !dark {
                        XCTAssertGreaterThanOrEqual(try contrast(fill, background), 3, "the fill on \(background), \(label)")
                    }
                }
            }
            XCTAssertGreaterThan(try contrast(LunalyrAccent.text(dark: dark, increaseContrast: true), window),
                                 try contrast(LunalyrAccent.text(dark: dark, increaseContrast: false), window),
                                 "Increase Contrast must strengthen the text, dark: \(dark)")
        }
    }

    func testDesktopPictureThumbnailIsPreviewSized() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("LunalyrTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let url = folder.appendingPathComponent("desktop.png")
        let context = try XCTUnwrap(CGContext(data: nil, width: 640, height: 400, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(CGColor(red: 0.2, green: 0.4, blue: 0.6, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 640, height: 400))
        let image = try XCTUnwrap(context.makeImage())
        let destination = try XCTUnwrap(CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, image, nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination))

        let thumbnail = try XCTUnwrap(SettingsPreviewDisplay.thumbnail(of: url, maxPixelSize: 160))
        XCTAssertEqual(max(thumbnail.width, thumbnail.height), 160)
        let text = folder.appendingPathComponent("not-an-image.txt")
        try Data("plain".utf8).write(to: text)
        XCTAssertNil(SettingsPreviewDisplay.thumbnail(of: text, maxPixelSize: 160))
    }
}
