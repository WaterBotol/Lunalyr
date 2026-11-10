import AppKit
import ImageIO
import SwiftUI
import UniformTypeIdentifiers
import XCTest
@testable import VersoCustom

/// Mock-ups of the desktop player in its main states, drawn offscreen at 2x
/// from the real parts (strip, seek row, controls) with made-up covers and
/// lyrics. Nothing is fetched, nothing plays. The PNGs are written only
/// when LUNALYR_PLAYER_PREVIEW_DIR is set (pass it to `xcodebuild test` as
/// TEST_RUNNER_LUNALYR_PLAYER_PREVIEW_DIR); otherwise each state is just
/// checked to draw something.
@MainActor
final class DesktopPlayerPreviewTests: XCTestCase {
    func testPlayerStatesDraw() throws {
        let directory = ProcessInfo.processInfo.environment["LUNALYR_PLAYER_PREVIEW_DIR"].map { URL(fileURLWithPath: $0) }
        if let directory { try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true) }
        defer { PlayerInteraction.shared.dragging = false; PlayerInteraction.shared.lastClick = nil }
        for state in PlayerPreviewState.allCases {
            let scene = PlayerPreviewScene(state: state)
            let renderer = ImageRenderer(content: scene.frame(width: PlayerPreviewScene.size.width,
                                                              height: PlayerPreviewScene.size.height))
            renderer.scale = 2
            let image = try XCTUnwrap(renderer.cgImage, "\(state)")
            XCTAssertEqual(image.width, Int(PlayerPreviewScene.size.width * 2))
            if let directory {
                try Self.write(image, to: directory.appendingPathComponent("\(state.fileName).png"))
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

enum PlayerPreviewState: String, CaseIterable {
    case rest, hoverLyric, scrubSnap, fineScrub, searching, paused, noSong, radio, noDuration, advert,
         reduceMotion, reduceTransparencyContrast, romanized

    var fileName: String {
        let order = PlayerPreviewState.allCases.firstIndex(of: self).map { String(format: "%02d", $0 + 1) } ?? "00"
        let name = rawValue.replacingOccurrences(of: "([a-z])([A-Z])", with: "$1-$2", options: .regularExpression).lowercased()
        return "\(order)-\(name)"
    }

    var caption: String {
        switch self {
        case .rest: return "Rest: hairline bar, elapsed and remaining"
        case .hoverLyric: return "Hover: knob, line ticks, section gaps, lighter repeated lines, time and lyric at the pointer"
        case .scrubSnap: return "Scrubbing, snapped onto a line start (tick lit, player colour, side covers dimmed, wallpaper follows)"
        case .fineScrub: return "Fine scrub: pointer ~90 pt below the bar, quarter speed"
        case .searching: return "Searching: results replace the strip, the seek row folds away"
        case .paused: return "Paused: play glyph, cover sits back, fill dimmed"
        case .noSong: return "Nothing playing"
        case .radio: return "Radio / stream: LIVE"
        case .noDuration: return "No known length: elapsed only"
        case .advert: return "Advert: read-only progress"
        case .reduceMotion: return "Reduce Motion: no bend or depth on the strip"
        case .reduceTransparencyContrast: return "Reduce Transparency + Increase Contrast, hovering"
        case .romanized: return "Hover with romanisation Below"
        }
    }
}

/// One state, on a made-up wallpaper.
@MainActor
struct PlayerPreviewScene: View {
    static let size = CGSize(width: 900, height: 560)
    let state: PlayerPreviewState
    @FocusState private var focus: Bool
    private let scrub: ScrubController
    private let elapsed: Double

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

    init(state: PlayerPreviewState) {
        self.state = state
        var clock = 0.0
        let elapsed: Double = state == .paused ? 61 : 83
        self.elapsed = elapsed
        let scrub = ScrubController(displayID: 1, seek: { _ in }, playback: { elapsed }, haptics: .silent,
                                    now: { clock }, reduceMotion: { true }, previewsOnWallpaper: false)
        let lines = state == .romanized ? Self.romanizedLines : Self.lines
        scrub.update(.init(duration: Self.duration, trackKey: "preview", lines: lines, canSeek: true,
                           romanization: state == .romanized ? .below : .off))
        func x(_ time: Double) -> Double { ScrubMath.x(time: time, duration: Self.duration) }
        switch state {
        case .hoverLyric, .reduceTransparencyContrast:
            scrub.pointerMoved(x: x(99), y: 0)
        case .romanized:
            scrub.pointerMoved(x: x(14), y: 0)
        case .scrubSnap:
            scrub.mouseDown(x: x(120), y: 0)
            // A slow approach onto the line at 1:06.
            var position = x(76)
            clock += 0.1
            scrub.mouseDragged(x: position, y: 0)
            while position > x(66) + 1 {
                clock += 0.1
                position -= 2
                scrub.mouseDragged(x: position, y: 0)
            }
        case .fineScrub:
            scrub.mouseDown(x: x(140), y: 0)
            clock += 0.1
            scrub.mouseDragged(x: x(140), y: 92)
            clock += 0.1
            scrub.mouseDragged(x: x(150), y: 92)
        default:
            break
        }
        self.scrub = scrub
    }

    private var look: PlayerLook {
        switch state {
        case .reduceMotion: return PlayerLook(reduceMotion: true)
        case .reduceTransparencyContrast: return PlayerLook(reduceTransparency: true, increaseContrast: true)
        default: return PlayerLook()
        }
    }

    private var rowKind: ScrubRowKind {
        switch state {
        case .searching, .noSong: return .hidden
        case .radio: return .live
        case .noDuration: return .elapsedOnly
        case .advert: return .advert
        default: return .seekable
        }
    }

    private var playing: Bool { state != .paused && state != .noSong }
    private let tint = PlayerKind.appleMusic.tint

    var body: some View {
        ZStack {
            Wallpaper()
            if let previewed = scrub.session?.targetTime, state == .scrubSnap {
                // The wallpaper's lyric following the hand (P2).
                if case .line(let index) = ScrubMath.lyric(at: previewed, lines: Self.lines) {
                    Text(Self.lines[index].text)
                        .font(.system(size: 54, weight: .bold, design: .rounded))
                        .foregroundStyle(.white)
                        .shadow(color: .black.opacity(0.4), radius: 10)
                        .modifier(PreviewFocus(look: PlayerHoverFocus.look(hovered: true, previewing: true)))
                        .offset(y: -40)
                }
            } else if state != .noSong {
                Text("Oh, the evening knows my name")
                    .font(.system(size: 54, weight: .bold, design: .rounded))
                    .foregroundStyle(.white)
                    .modifier(PreviewFocus(look: PlayerHoverFocus.look(hovered: true, previewing: false)))
                    .offset(y: -40)
            }
            PlayerColumn(scrubKind: rowKind) {
                top
            } scrub: {
                ScrubRow(controller: scrub, kind: rowKind, tint: tint, isPlaying: playing, shown: true,
                         showsRemaining: .constant(true))
            } controls: {
                PlayerControlsRow(kinds: [.appleMusic, .spotify], selected: .appleMusic, onSelect: { _ in },
                                  canControl: true, hasSong: state != .noSong, isPlaying: playing,
                                  onPrevious: {}, onPlayPause: {}, onNext: {},
                                  query: .constant(state == .searching ? "evening" : ""), searchFocused: $focus,
                                  onSubmit: {})
            }
            .foregroundStyle(.white)
            VStack {
                Spacer()
                Text(state.caption).font(.system(size: 12, weight: .medium)).foregroundStyle(.white.opacity(0.9))
                    .padding(.horizontal, 10).padding(.vertical, 4)
                    .background(Color.black.opacity(0.55), in: Capsule())
                    .padding(.bottom, 12)
            }
        }
        .environment(\.colorScheme, .dark)
        .environment(\.playerLook, look)
        .environment(\.playerPreview, PlayerPreview(covers: Self.covers))
    }

    @ViewBuilder
    private var top: some View {
        switch state {
        case .searching:
            PlayerResultsList(results: Self.results, status: .idle, selection: 1, player: .appleMusic) { _ in }
                .frame(width: DesktopPlayerView.width, height: DesktopPlayerView.resultsHeight)
                .background(Color(white: 0.1).opacity(0.75), in: RoundedRectangle(cornerRadius: 26, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 26, style: .continuous).strokeBorder(.white.opacity(0.14), lineWidth: 0.5))
        case .noSong:
            EmptyPlayerTile(title: "Nothing playing").frame(width: DesktopPlayerView.width, height: DesktopPlayerView.stripHeight)
        case .radio:
            CoverStrip(items: [PlayerQueueItem(id: "radio", title: "Night Radio", artist: "Live", index: 0, isCurrent: true)],
                       tint: tint, onTap: { _ in })
                .frame(width: DesktopPlayerView.width, height: DesktopPlayerView.stripHeight)
        default:
            CoverStrip(items: Self.queue, tint: tint, paused: state == .paused,
                       labelsDimmed: scrub.hover != nil || scrub.session != nil, sidesDimmed: scrub.session != nil,
                       onTap: { _ in })
                .frame(width: DesktopPlayerView.width, height: DesktopPlayerView.stripHeight)
        }
    }

    // MARK: Made-up content

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

/// The wallpaper's lyric as it sits under the player.
private struct PreviewFocus: ViewModifier {
    let look: (blur: CGFloat, opacity: Double)
    func body(content: Content) -> some View { content.blur(radius: look.blur).opacity(look.opacity) }
}

/// A stand-in wallpaper: deep colour with soft light.
private struct Wallpaper: View {
    var body: some View {
        ZStack {
            LinearGradient(colors: [Color(red: 0.10, green: 0.08, blue: 0.22), Color(red: 0.30, green: 0.12, blue: 0.28),
                                    Color(red: 0.06, green: 0.16, blue: 0.24)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
            Circle().fill(Color(red: 0.95, green: 0.45, blue: 0.40).opacity(0.35)).frame(width: 420).blur(radius: 90)
                .offset(x: -240, y: -120)
            Circle().fill(Color(red: 0.35, green: 0.55, blue: 1).opacity(0.3)).frame(width: 380).blur(radius: 90)
                .offset(x: 260, y: 140)
        }
    }
}
