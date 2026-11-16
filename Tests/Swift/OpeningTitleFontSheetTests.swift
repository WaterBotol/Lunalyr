import AppKit
import CoreText
import SwiftUI
import XCTest
@testable import VersoCustom

/// The opening title card in many faces, side by side on one contact
/// sheet, for judging how it holds up with a font it was not drawn for.
/// Preview only. Skipped unless LUNALYR_TITLE_FONT_SHEET names a PNG path
/// (pass it as TEST_RUNNER_LUNALYR_TITLE_FONT_SHEET=<file.png>). Faces
/// downloaded or installed on this Mac (Poppins, Bebas Neue, Monoton,
/// Minecrafter) are used when present.
@MainActor
final class OpeningTitleFontSheetTests: XCTestCase {
    struct Face {
        let name: String
        var choice: VersoCustom.Settings.FontChoice = .installed
        var family = ""
        /// A font file to register for this process first, if it is there.
        var file: String? = nil
    }

    static let fontFolders = [NSHomeDirectory() + "/Library/Application Support/Verso/Fonts", NSHomeDirectory() + "/Library/Fonts"]

    static let faces: [Face] = [
        Face(name: "Poppins", choice: .google, family: "Poppins"),
        Face(name: "System (SF Pro)", choice: .appleMusic),
        Face(name: "Pixel (Minecrafter)", family: "minecrafter", file: "Minecrafter-MA3Dw.ttf"),
        Face(name: "Serif (Charter)", family: "Charter"),
        Face(name: "Condensed (Bebas Neue)", choice: .google, family: "Bebas Neue"),
        Face(name: "Condensed (Impact)", family: "Impact"),
        Face(name: "Wide (Monoton)", choice: .google, family: "Monoton"),
        Face(name: "Script (Snell Roundhand)", family: "Snell Roundhand"),
    ]

    struct Card {
        let name: String
        let title: String
        var artist = "WALLOWS"
        var album: String? = "TELL ME THAT IT'S OVER"
        var reading: String? = nil
    }

    static let cards: [Card] = [
        Card(name: "one line", title: "MARVELOUS"),
        Card(name: "two lines", title: "Everything You Said Before the Summer", artist: "Clara Vey & The Lanterns",
             album: "Paper Moons"),
        Card(name: "long details", title: "Marvelous", artist: "Clara Vey, Theo Marsh & The Midnight Lanterns",
             album: "Everything We Never Said (Deluxe Anniversary Edition)"),
        Card(name: "Japanese", title: "夜に駆ける", artist: "YOASOBI", album: "THE BOOK", reading: "Yoru ni Kakeru"),
        Card(name: "Korean", title: "사랑으로", artist: "wave to earth", album: "0.1 flaws and all."),
    ]

    /// Registers the face's files for this process, from a copy: fonts in
    /// Lunalyr's own download folder are not offered as installed families.
    static func register(_ face: Face) {
        guard let file = face.file else { return }
        let copies = FileManager.default.temporaryDirectory.appendingPathComponent("LunalyrTitleFonts", isDirectory: true)
        try? FileManager.default.createDirectory(at: copies, withIntermediateDirectories: true)
        for name in Set([file, file.replacingOccurrences(of: "-1.", with: "-0.")]) {
            for folder in fontFolders {
                let url = URL(fileURLWithPath: folder).appendingPathComponent(name)
                guard FileManager.default.fileExists(atPath: url.path) else { continue }
                let copy = copies.appendingPathComponent(name)
                if !FileManager.default.fileExists(atPath: copy.path) { try? FileManager.default.copyItem(at: url, to: copy) }
                CTFontManagerRegisterFontsForURL(copy as CFURL, .process, nil)
                break
            }
        }
    }

    /// Draws every card with `face` as the lyric font, cropped to the card.
    static func cells(_ face: Face, settings: VersoCustom.Settings) throws -> [CGImage] {
        register(face)
        settings.fontChoice = face.choice
        if face.choice == .google {
            // From Lunalyr's own cache of downloaded families.
            settings.applyGoogleFont(family: face.family)
            let wanted = face.family.replacingOccurrences(of: " ", with: "").lowercased()
            for _ in 0..<50 where !(settings.activeCustomFaces.regular ?? "").lowercased().contains(wanted) {
                RunLoop.current.run(until: Date().addingTimeInterval(0.1))
            }
            print("SHEET \(face.name): \(settings.activeCustomFaces.regular ?? "missing")")
        }
        if face.choice == .installed {
            // AppKit lists a font registered a moment ago once its
            // notification has gone round.
            for _ in 0..<20 where InstalledFontFamily.resolve(face.family) == nil {
                RunLoop.current.run(until: Date().addingTimeInterval(0.1))
            }
            settings.installedFontFamily = face.family
            settings.resolveInstalledFont()
            print("SHEET \(face.name): \(settings.activeCustomFaces.regular ?? "missing") / \(settings.activeCustomFaces.bold ?? "-")")
        }
        let screen = OpeningTitlePreviewTests.screen
        let baseSize = LyricViewportMetrics(size: screen).baseSize
        return try cards.map { card in
            let view = ZStack {
                Color(red: 0.13, green: 0.12, blue: 0.16)
                OpeningTitleCard(phase: OpeningTitle.Phase(age: 10, exit: 0, size: .full, moving: false),
                                 title: card.title, artist: card.artist, album: card.album, reading: card.reading,
                                 cover: OpeningTitleCardLayoutPreviewTests.cover, baseSize: baseSize,
                                 accent: Color(.sRGB, red: 0.95, green: 0.55, blue: 0.42), mood: .neutral)
                    .environment(\.lyricReduceMotion, true)
            }
            .frame(width: screen.width, height: screen.height)
            let renderer = ImageRenderer(content: view)
            renderer.scale = 1
            let full = try XCTUnwrap(renderer.cgImage)
            return try XCTUnwrap(full.cropping(to: CGRect(x: 0, y: screen.height * 0.6,
                                                          width: screen.width * 0.62, height: screen.height * 0.35)))
        }
    }

    func testRenderFontContactSheet() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let path = environment["LUNALYR_TITLE_FONT_SHEET"] ?? environment["TEST_RUNNER_LUNALYR_TITLE_FONT_SHEET"],
              !path.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_TITLE_FONT_SHEET=<file.png> to render the title card in many faces")
        }
        let settings = Settings.shared
        let saved = (settings.fontChoice, settings.installedFontFamily, settings.googleFontFamily)
        defer {
            settings.googleFontFamily = saved.2
            settings.fontChoice = saved.0
            settings.installedFontFamily = saved.1
            settings.resolveInstalledFont()
        }
        var rows: [(String, [CGImage])] = []
        for face in Self.faces { rows.append((face.name, try Self.cells(face, settings: settings))) }
        let cell = CGSize(width: 520, height: 191)
        let sheet = VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Text("").frame(width: 150)
                ForEach(Self.cards.indices, id: \.self) { index in
                    Text(Self.cards[index].name).font(.system(size: 15, weight: .semibold)).foregroundStyle(.white)
                        .frame(width: cell.width)
                }
            }
            ForEach(rows.indices, id: \.self) { row in
                HStack(spacing: 6) {
                    Text(rows[row].0).font(.system(size: 14, weight: .semibold)).foregroundStyle(.white)
                        .frame(width: 150, alignment: .leading)
                    ForEach(rows[row].1.indices, id: \.self) { index in
                        Image(decorative: rows[row].1[index], scale: 1).resizable().interpolation(.high)
                            .frame(width: cell.width, height: cell.height)
                    }
                }
            }
        }
        .padding(12)
        .background(Color.black)
        let renderer = ImageRenderer(content: sheet)
        renderer.scale = 1
        try DesktopPlayerPreviewTests.write(try XCTUnwrap(renderer.cgImage), to: URL(fileURLWithPath: path))
    }
}
