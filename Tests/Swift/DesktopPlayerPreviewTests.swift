import AppKit
import ImageIO
import SwiftUI
import UniformTypeIdentifiers
import XCTest
@testable import VersoCustom

/// The real desktop player in its main states (`SleeveState`), drawn
/// offscreen at 2x over a made-up wallpaper with made-up songs and covers.
/// Nothing is fetched, nothing plays. The PNGs are written only when
/// LUNALYR_PLAYER_PREVIEW_DIR is set (pass it to `xcodebuild test` as
/// TEST_RUNNER_LUNALYR_PLAYER_PREVIEW_DIR); otherwise each state is just
/// checked to draw something.
@MainActor
final class DesktopPlayerPreviewTests: XCTestCase {
    func testPlayerStatesDraw() throws {
        let directory = ProcessInfo.processInfo.environment["LUNALYR_PLAYER_PREVIEW_DIR"].map { URL(fileURLWithPath: $0) }
        if let directory { try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true) }
        defer { PlayerInteraction.shared.dragging = false; PlayerInteraction.shared.lastClick = nil }
        for (index, state) in SleeveState.allCases.enumerated() {
            let renderer = ImageRenderer(content: SleeveScene(state: state, backdrop: .dark, caption: state.title))
            renderer.scale = directory == nil ? 0.5 : 2
            let image = try XCTUnwrap(renderer.cgImage, "\(state)")
            XCTAssertEqual(image.width, Int(SleeveScene.size.width * renderer.scale))
            if let directory {
                try Self.write(image, to: directory.appendingPathComponent(String(format: "%02d-%@.png", index + 1, state.rawValue)))
            }
        }
    }

    static func write(_ image: CGImage, to url: URL) throws {
        guard let destination = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) else {
            throw CocoaError(.fileWriteUnknown)
        }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { throw CocoaError(.fileWriteUnknown) }
    }
}

/// Made-up songs, results, covers and lyrics for the player's previews.
@MainActor
enum PlayerPreviewScene {
    static let duration: Double = 213
    static let lines: [ScrubLine] = {
        let words = [
            (8.0, "Streetlights hum a borrowed tune"), (12.5, "Paper moons above the station"),
            (17.0, "Every train is leaving soon"), (21.5, "Hold the platform, hold the patience"),
            (34.0, "Oh, the evening knows my name"), (38.0, "Calls it softly down the river"),
            (42.0, "Says I'll never be the same"), (46.5, "Then forgets it, like a shiver"),
            (66.0, "Count the windows going gold"), (70.5, "One by one the city settles"),
            (75.0, "Every story that I told"), (79.5, "Rings like rain on tin and kettles"),
            (96.0, "Oh, the evening knows my name"), (100.0, "Calls it softly down the river"),
            (104.0, "Says I'll never be the same"), (108.5, "Then forgets it, like a shiver"),
            (140.0, "Hum it low and let it go"), (145.0, "Hum it low and let it go"),
            (170.0, "Oh, the evening knows my name"), (174.0, "Calls it softly down the river"),
        ]
        var lines: [ScrubLine] = []
        for (index, word) in words.enumerated() {
            let next = index + 1 < words.count ? words[index + 1].0 : word.0 + 4
            lines.append(ScrubLine(start: word.0, end: min(word.0 + 3.8, next - 0.2), text: word.1,
                                   romanized: nil, isInstrumental: false))
            if word.0 == 108.5 {
                lines.append(ScrubLine(start: 113, end: 139, text: "", romanized: nil, isInstrumental: true))
            }
        }
        return lines
    }()

    static let romanizedLines: [ScrubLine] = [
        ScrubLine(start: 8, end: 12, text: "夜明けの駅で待ってる", romanized: "yoake no eki de matteru", isInstrumental: false),
        ScrubLine(start: 12.5, end: 16.5, text: "君の声がまだ聞こえる", romanized: "kimi no koe ga mada kikoeru", isInstrumental: false),
        ScrubLine(start: 34, end: 38, text: "遠くの光を数えて", romanized: "tooku no hikari o kazoete", isInstrumental: false),
        ScrubLine(start: 66, end: 70, text: "もう一度だけ", romanized: "mou ichido dake", isInstrumental: false),
    ]

    static let queue: [PlayerQueueItem] = [
        ("Copper Lanterns", "Halcyon Bay"), ("Quiet Machines", "Moth Parade"), ("Glass Orchard", "Saltwater Choir"),
        ("The Evening Knows", "Lunar Tides"), ("Low Tide Radio", "Fen & Fable"), ("Paper Satellites", "The Wrens"),
        ("Velvet Hour", "Northern Arcade"),
    ].enumerated().map { index, song in
        PlayerQueueItem(id: "q\(index)", title: song.0, artist: song.1, index: index + 1, isCurrent: index == 3,
                        persistentID: "P\(index)")
    }

    static let results: [PlayerSearchResult] = [
        ("The Evening Knows", "Lunar Tides", "Night Bloom"), ("Evening Train", "Moth Parade", "Platforms"),
        ("An Evening Apart", "Saltwater Choir", "Tidelines"), ("Evenings (Live)", "The Wrens", "Live at the Hall"),
    ].enumerated().map { index, song in
        PlayerSearchResult(id: "r\(index)", kind: .song, title: song.0, artist: song.1, album: song.2, artworkURL: nil,
                           target: .libraryTrack(persistentID: "R\(index)"), inLibrary: index < 2)
    }

    static let covers: [String: NSImage] = {
        var covers: [String: NSImage] = [:]
        let palettes: [(NSColor, NSColor)] = [
            (.systemOrange, .systemPink), (.systemTeal, .systemIndigo), (.systemGreen, .systemBlue),
            (.systemPink, .systemPurple), (.systemYellow, .systemRed), (.systemBlue, .systemCyan), (.systemPurple, .systemOrange),
        ]
        for index in 0..<7 {
            let image = cover(palettes[index], seed: index)
            covers["q\(index)"] = image
            covers["r\(index % 4)"] = covers["r\(index % 4)"] ?? image
        }
        covers["radio"] = cover((.systemRed, .black), seed: 9)
        return covers
    }()

    /// A gradient square with a few circles: a stand-in album cover.
    static func cover(_ colours: (NSColor, NSColor), seed: Int) -> NSImage {
        let side = 300
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        let context = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        let gradient = CGGradient(colorsSpace: space, colors: [colours.0.cgColor, colours.1.cgColor] as CFArray,
                                  locations: [0, 1])!
        context.drawLinearGradient(gradient, start: .zero, end: CGPoint(x: side, y: side), options: [])
        for ring in 0..<3 {
            let radius = CGFloat(40 + ((seed * 37 + ring * 53) % 90))
            let x = CGFloat((seed * 71 + ring * 97) % side), y = CGFloat((seed * 43 + ring * 61) % side)
            context.setFillColor(NSColor.white.withAlphaComponent(0.12 + 0.06 * CGFloat(ring)).cgColor)
            context.fillEllipse(in: CGRect(x: x - radius, y: y - radius, width: radius * 2, height: radius * 2))
        }
        return NSImage(cgImage: context.makeImage()!, size: NSSize(width: side, height: side))
    }
}
