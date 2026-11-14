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

    private let airDesktop = CGSize(width: 1470, height: 956)

    /// The 13-inch Air's default window (1100 x 720): the canvas between the
    /// 186-point sidebar and the 286-point inspector draws the picture
    /// 584 x 380, the desktop's shape, as the spec has it.
    func testThePictureFillsTheCanvasAtTheDesktopsShape() {
        let aspect = airDesktop.width / airDesktop.height
        let canvas = CGSize(width: 1100 - 186 - 286, height: 720)
        let picture = LookCanvas.pictureSize(canvas: canvas, aspect: aspect, choices: 160)
        XCTAssertEqual(picture.width, 584, accuracy: 1)
        XCTAssertEqual(picture.width / picture.height, aspect, accuracy: 0.02)
    }

    /// The smallest window (860 x 420): the canvas narrows, and the picture
    /// shrinks to leave the choices their room rather than spill out.
    func testTheSmallestWindowShrinksThePictureNotTheChoices() {
        let aspect = airDesktop.width / airDesktop.height
        let canvas = CGSize(width: SettingsWindowController.minimumWidth - 186 - 286,
                            height: SettingsWindowController.minimumHeight)
        let choices: CGFloat = 150
        let picture = LookCanvas.pictureSize(canvas: canvas, aspect: aspect, choices: choices)
        XCTAssertLessThanOrEqual(picture.width, canvas.width - 44 + 0.5)
        let used = LookCanvas.switchBand + picture.height + LookCanvas.stripGap + LookCanvas.stripHeight
            + 16 + choices + LookCanvas.padding
        XCTAssertLessThanOrEqual(used, canvas.height + 0.5, "everything stays in the window")
        XCTAssertEqual(picture.width / picture.height, aspect, accuracy: 0.02)
        // A portrait display is height-bound too.
        let portrait = LookCanvas.pictureSize(canvas: CGSize(width: 628, height: 720), aspect: 1080.0 / 1920.0, choices: 160)
        XCTAssertLessThan(portrait.width, 584)
    }

    /// Pages remembered by earlier versions still open, and every place
    /// another surface links to lands on a page.
    func testStoredPagesAndDestinationsStillOpen() {
        for name in ["wallpaper", "typography", "lyrics", "readings", "covers", "performance", "extras", "share", "players", "about"] {
            XCTAssertNotNil(SettingsPage(rawValue: name), "a page remembered as \(name) no longer opens")
        }
        XCTAssertEqual(SettingsPage(.musicResponse), .wallpaper)
        XCTAssertEqual(SettingsAnchor(.musicResponse), .musicResponse)
        XCTAssertEqual(SettingsPage(.players), .players)
        XCTAssertEqual(SettingsAnchor(.players), .players)
        XCTAssertEqual(SettingsPage(.timing), .lyrics)
        XCTAssertEqual(SettingsPage(.menuBarAndShortcuts), .extras)
        XCTAssertNil(SettingsAnchor(.wallpaper))
        // Every page is in exactly one sidebar section, About last.
        let listed = SettingsPage.Group.allCases.flatMap(\.pages)
        XCTAssertEqual(Set(listed), Set(SettingsPage.allCases))
        XCTAssertEqual(listed.count, SettingsPage.allCases.count)
        XCTAssertEqual(listed.last, .about)
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
