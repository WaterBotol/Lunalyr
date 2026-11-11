import AppKit
import SwiftUI
@testable import VersoCustom

// The real desktop player (`DesktopPlayerView`, "Sleeve") drawn offscreen
// with made-up songs and covers over a made-up wallpaper, through its preview
// hooks (`playerPreview`, `playerLookOverride`). Nothing plays, nothing is
// fetched, no preference is read or written but a throwaway suite's.

/// What the player is showing.
enum SleeveState: String, CaseIterable {
    case rest, hover, scrub, search, queue, paused, nothing, radio, advert, noDuration, align, reduced, spotify,
         reduceMotion, playNote

    var title: String {
        switch self {
        case .rest: return "At rest"
        case .hover: return "Pointer on the seek bar"
        case .scrub: return "Scrubbing, snapped onto a line"
        case .search: return "Search open, second result picked with the arrow keys"
        case .queue: return "Queue open (shuffle on)"
        case .paused: return "Paused: the record slides back into the sleeve"
        case .nothing: return "Nothing playing (search still works)"
        case .radio: return "Radio / stream: LIVE"
        case .advert: return "Advert: read-only"
        case .noDuration: return "No known length"
        case .align: return "Lining up the music video"
        case .reduced: return "Reduce Transparency + Increase Contrast, hovering"
        case .spotify: return "Spotify queue: only the song playing"
        case .reduceMotion: return "Reduce Motion, paused: the record stays put"
        case .playNote: return "A picked song plays alone: the note over the card"
        }
    }
}

enum SleeveBackdrop: String, CaseIterable { case dark, light }

/// One state's model, bar and preview hooks.
@MainActor
struct SleeveFixture {
    static let duration = 213.0
    static let defaults = UserDefaults(suiteName: "lunalyr.tests.player-sleeve")!

    let state: SleeveState
    let model: DesktopPlayerModel
    let scrub: ScrubController
    var preview: PlayerPreview
    let look: PlayerLook

    var scrubbing: Bool { scrub.session != nil }

    init(state: SleeveState, backdrop: AnyView? = nil) {
        self.state = state
        let model = DesktopPlayerModel(defaults: Self.defaults, preview: true)
        let spotify = state == .spotify
        model.player = spotify ? .spotify : .appleMusic
        let queue = PlayerPreviewScene.queue
        switch state {
        case .search:
            model.loadPreview(queue: queue, shuffle: true, results: PlayerPreviewScene.results, selection: 1,
                              query: "evening", pane: .search)
        case .queue:
            model.loadPreview(queue: queue, shuffle: true, pane: .queue)
        case .spotify:
            model.loadPreview(queue: [], shuffle: false, pane: .queue)
        case .nothing:
            model.loadPreview(queue: [], shuffle: false)
        case .playNote:
            model.loadPreview(queue: queue, shuffle: true, playNote: QueueSolo.notInLibrary.note)
        default:
            model.loadPreview(queue: queue, shuffle: true)
        }
        model.showsRemaining = true
        self.model = model

        let track: NowPlayingMonitor.Track?
        var artwork: NSImage? = PlayerPreviewScene.covers["q3"]
        switch state {
        case .nothing:
            track = nil
        case .radio:
            track = .init(title: "Night Radio", artist: "Live", duration: 0, isStream: true)
            artwork = PlayerPreviewScene.covers["radio"]
        case .advert:
            track = .init(title: "Advertisement", artist: "Your music will resume shortly", duration: 30, isAdvert: true)
            artwork = nil
        case .noDuration:
            track = .init(title: "The Evening Knows", artist: "Lunar Tides", duration: 0, album: "Night Bloom")
        default:
            track = .init(title: "The Evening Knows", artist: "Lunar Tides", duration: Self.duration, album: "Night Bloom",
                          persistentID: "P3")
        }
        let paused = state == .paused || state == .reduceMotion
        let elapsed: Double = state == .advert ? 12 : paused ? 61 : 83
        var clock = 0.0
        let scrub = ScrubController(displayID: 1, seek: { _ in }, playback: { elapsed }, haptics: .silent,
                                    now: { clock }, reduceMotion: { true }, previewsOnWallpaper: false)
        scrub.update(.init(duration: track?.duration ?? 0, trackKey: "preview", lines: PlayerPreviewScene.lines,
                           canSeek: track.map { !$0.isStream && !$0.isAdvert && $0.duration > 0 } ?? false))
        func x(_ time: Double) -> Double { ScrubMath.x(time: time, duration: Self.duration) }
        switch state {
        case .hover, .reduced:
            scrub.pointerMoved(x: x(99), y: 0)
        case .scrub:
            scrub.mouseDown(x: x(120), y: 0)
            // A slow approach onto the line at 1:06, then down off the bar to half speed.
            var position = x(76)
            clock += 0.1
            scrub.mouseDragged(x: position, y: 0)
            while position > x(66) + 1 {
                clock += 0.1
                position -= 1.5
                scrub.mouseDragged(x: position, y: 0)
            }
            clock += 0.1
            scrub.mouseDragged(x: position, y: 50)
        default:
            break
        }
        self.scrub = scrub

        var preview = PlayerPreview(covers: PlayerPreviewScene.covers)
        preview.now = PlayerNow(track: track, isPlaying: !paused && track != nil, player: spotify ? .spotify : .appleMusic,
                                artwork: artwork)
        preview.musicVideo = true
        preview.searchFocused = state == .search
        preview.backdrop = backdrop
        if state == .align {
            preview.align = MusicVideoAlignDisplay(videoLength: 232, songDuration: Self.duration, offset: 15, songTime: 79,
                                                   origin: .estimated)
        }
        self.preview = preview
        switch state {
        case .reduced: look = PlayerLook(reduceTransparency: true, increaseContrast: true)
        case .reduceMotion: look = PlayerLook(reduceMotion: true)
        default: look = PlayerLook()
        }
    }

    /// The real player, in its 780-pt panel.
    var player: some View {
        DesktopPlayerView(model: model, displayID: 1, scrub: scrub)
            .frame(width: SleeveScene.panel.width, height: SleeveScene.panel.height)
            .environment(\.playerPreview, preview)
            .environment(\.playerLookOverride, look)
            .environmentObject(NowPlayingMonitor.shared)
    }
}

/// One state of the player over a whole made-up screen: the wallpaper, its
/// lyric (blurred behind the card as the app does), and the player's panel
/// in the middle.
@MainActor
struct SleeveScene: View {
    static let size = CGSize(width: 1440, height: 900)
    static let panel = CGSize(width: 780, height: 780)
    let state: SleeveState
    let backdrop: SleeveBackdrop
    var caption: String?

    var body: some View {
        let probe = SleeveFixture(state: state)
        let previewing = probe.scrubbing || state == .align
        let focus = PlayerHoverFocus.look(hovered: true, previewing: previewing, reduceMotion: probe.look.reduceMotion)
        let wallpaper = SleeveWallpaper(kind: backdrop, lyric: state != .nothing,
                                        line: probe.scrubbing ? "Count the windows going gold" : "Oh, the evening knows my name",
                                        blur: focus.blur, opacity: focus.opacity)
        let fixture = SleeveFixture(state: state, backdrop: AnyView(wallpaper))
        ZStack(alignment: .topLeading) {
            wallpaper
            fixture.player
                .position(x: Self.size.width / 2, y: Self.size.height / 2)
            if let caption {
                Text(caption)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 12).padding(.vertical, 6)
                    .background(Color.black.opacity(0.6), in: Capsule())
                    .padding(20)
            }
        }
        .frame(width: Self.size.width, height: Self.size.height)
        .clipped()
        .coordinateSpace(name: PlayerPreview.space)
        .environment(\.colorScheme, .dark)
    }
}

/// A busy, music-video-like frame with the lyric on top, as the wallpaper
/// draws it under the player.
struct SleeveWallpaper: View {
    let kind: SleeveBackdrop
    var lyric = true
    var line = "Oh, the evening knows my name"
    var blur: CGFloat = 0
    var opacity: Double = 1

    var body: some View {
        ZStack {
            SleeveBusyArt(kind: kind)
            if lyric {
                Text(line)
                    .font(.system(size: 66, weight: .heavy, design: .rounded))
                    .foregroundStyle(.white)
                    .shadow(color: .black.opacity(0.35), radius: 12, y: 2)
                    .blur(radius: blur)
                    .opacity(opacity)
                    .offset(y: -40)
            }
        }
        .frame(width: SleeveScene.size.width, height: SleeveScene.size.height)
        .clipped()
    }
}

/// Seeded, so every render matches.
private struct SleeveRandom {
    var state: UInt64
    mutating func next() -> Double {
        state = state &* 6364136223846793005 &+ 1442695040888963407
        return Double((state >> 33) & 0xFFFFFF) / Double(0xFFFFFF)
    }
    mutating func range(_ low: Double, _ high: Double) -> Double { low + (high - low) * next() }
}

/// Glows, light beams, a lit skyline and bokeh: dark (night) or light (a
/// bright, busy day, the hardest case for white type).
struct SleeveBusyArt: View {
    let kind: SleeveBackdrop

    var body: some View {
        Canvas { context, size in
            var random = SleeveRandom(state: kind == .dark ? 7 : 11)
            let rect = CGRect(origin: .zero, size: size)
            let dark = kind == .dark
            let base: [Color] = dark
                ? [Color(red: 0.05, green: 0.04, blue: 0.13), Color(red: 0.20, green: 0.05, blue: 0.22), Color(red: 0.02, green: 0.11, blue: 0.17)]
                : [Color(red: 0.66, green: 0.82, blue: 0.98), Color(red: 0.99, green: 0.90, blue: 0.76), Color(red: 1.0, green: 0.78, blue: 0.70)]
            context.fill(Path(rect), with: .linearGradient(Gradient(colors: base), startPoint: .zero,
                                                            endPoint: CGPoint(x: size.width, y: size.height)))
            let glows: [Color] = dark
                ? [Color(red: 0.95, green: 0.2, blue: 0.6), Color(red: 0.1, green: 0.8, blue: 0.95), Color(red: 1, green: 0.62, blue: 0.15), Color(red: 0.5, green: 0.3, blue: 1)]
                : [.white, Color(red: 1, green: 0.6, blue: 0.75), Color(red: 1, green: 0.95, blue: 0.5), Color(red: 0.5, green: 0.95, blue: 0.9)]
            context.drawLayer { layer in
                layer.addFilter(.blur(radius: 70))
                for (index, colour) in glows.enumerated() {
                    let radius = random.range(180, 320)
                    let centre = CGPoint(x: random.range(0, size.width), y: random.range(0, size.height * 0.8))
                    let circle = CGRect(x: centre.x - radius, y: centre.y - radius, width: radius * 2, height: radius * 2)
                    layer.fill(Path(ellipseIn: circle), with: .color(colour.opacity(dark ? 0.5 : (index == 0 ? 0.95 : 0.6))))
                }
            }
            for _ in 0..<8 {
                var beam = context
                beam.translateBy(x: random.range(0, size.width), y: -40)
                beam.rotate(by: .degrees(random.range(-28, 28)))
                let width = random.range(18, 70)
                beam.fill(Path(CGRect(x: -width / 2, y: 0, width: width, height: size.height * 1.1)),
                          with: .linearGradient(Gradient(colors: [.white.opacity(dark ? 0.22 : 0.5), .white.opacity(0)]),
                                                startPoint: .zero, endPoint: CGPoint(x: 0, y: size.height * 0.9)))
            }
            var x = -20.0
            while x < size.width {
                let width = random.range(50, 140)
                let height = random.range(90, 300)
                let block = CGRect(x: x, y: size.height - height, width: width, height: height)
                context.fill(Path(block), with: .color(dark ? Color(red: 0.02, green: 0.02, blue: 0.06).opacity(0.92)
                                                            : Color(red: 0.2, green: 0.45, blue: 0.5).opacity(0.55)))
                for row in stride(from: block.minY + 12, to: block.maxY - 10, by: 18) {
                    for column in stride(from: block.minX + 8, to: block.maxX - 10, by: 16) where random.next() < 0.35 {
                        context.fill(Path(CGRect(x: column, y: row, width: 8, height: 10)),
                                     with: .color(dark ? Color(red: 1, green: 0.8, blue: 0.4).opacity(0.75) : .white.opacity(0.8)))
                    }
                }
                x += width + random.range(-10, 20)
            }
            let dots: [Color] = dark ? [.pink, .cyan, .orange, .purple, .white, .yellow] : [.white, .yellow, .pink, .mint, .orange]
            context.drawLayer { layer in
                layer.addFilter(.blur(radius: 5))
                for _ in 0..<55 {
                    let radius = random.range(6, 42)
                    let point = CGPoint(x: random.range(0, size.width), y: random.range(0, size.height))
                    let colour = dots[Int(random.range(0, Double(dots.count) - 0.01))]
                    layer.fill(Path(ellipseIn: CGRect(x: point.x - radius, y: point.y - radius, width: radius * 2, height: radius * 2)),
                               with: .color(colour.opacity(random.range(0.15, dark ? 0.55 : 0.75))))
                }
            }
            for _ in 0..<40 {
                let radius = random.range(2, 7)
                let point = CGPoint(x: random.range(0, size.width), y: random.range(0, size.height))
                context.fill(Path(ellipseIn: CGRect(x: point.x - radius, y: point.y - radius, width: radius * 2, height: radius * 2)),
                             with: .color(.white.opacity(random.range(0.4, 0.9))))
            }
        }
    }
}
