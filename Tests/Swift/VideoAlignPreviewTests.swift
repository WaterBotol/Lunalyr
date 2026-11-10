import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Mock-ups of lining up the music video in the desktop player, drawn
/// offscreen at 2x from the real parts over a made-up video frame. Nothing
/// plays. Written as PNGs only when LUNALYR_PLAYER_PREVIEW_DIR is set (as
/// `DesktopPlayerPreviewTests`); otherwise each state is checked to draw.
@MainActor
final class VideoAlignPreviewTests: XCTestCase {
    func testAlignStatesDraw() throws {
        let directory = ProcessInfo.processInfo.environment["LUNALYR_PLAYER_PREVIEW_DIR"].map { URL(fileURLWithPath: $0) }
        if let directory { try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true) }
        for state in AlignPreviewState.allCases {
            let renderer = ImageRenderer(content: AlignPreviewScene(state: state)
                .frame(width: PlayerPreviewScene.size.width, height: PlayerPreviewScene.size.height))
            renderer.scale = 2
            let image = try XCTUnwrap(renderer.cgImage, "\(state)")
            XCTAssertEqual(image.width, Int(PlayerPreviewScene.size.width * 2))
            if let directory {
                try DesktopPlayerPreviewTests.write(image, to: directory.appendingPathComponent("\(state.fileName).png"))
            }
        }
    }
}

enum AlignPreviewState: String, CaseIterable {
    case button, mode, dragging, fine, kept, unavailable

    var fileName: String {
        let order = AlignPreviewState.allCases.firstIndex(of: self).map { $0 + 1 } ?? 0
        return "20-align-\(order)-\(rawValue)"
    }

    var caption: String {
        switch self {
        case .button: return "Music video wallpaper: the film button beside the seek bar starts lining up the video"
        case .mode: return "Align mode: the video\u{2019}s own bar, the song\u{2019}s span in colour, the offset over the knob"
        case .dragging: return "Dragging: only the video moves (the song plays on); release keeps it for this video"
        case .fine: return "Dragging ~130 pt below the bar: fine steps, a tenth of a second at a time"
        case .kept: return "Released: kept as your own timing for this video. \u{2190} \u{2192} 0.1 s, \u{21E7} 1 s, Esc puts it back"
        case .unavailable: return "Song paused: the film button is dimmed; a click says \u{201C}\(MusicVideoAlignAvailability.notPlaying)\u{201D}"
        }
    }

    var aligning: Bool { self != .button && self != .unavailable }

    var display: MusicVideoAlignDisplay {
        // A 3:52 video whose song (3:20) starts 17 s in; 1:04 into the song.
        var display = MusicVideoAlignDisplay(videoLength: 232, songDuration: 200, offset: 15, songTime: 64,
                                             origin: .estimated)
        switch self {
        case .button, .unavailable, .mode:
            break
        case .dragging:
            display.offset = 17.3
            display.dragging = true
        case .fine:
            display.offset = 17.6
            display.dragging = true
            display.rateLabel = "Fine"
        case .kept:
            display.offset = 17.6
            display.origin = .manual
            display.touched = true
        }
        return display
    }
}

/// One state over a made-up video frame.
@MainActor
private struct AlignPreviewScene: View {
    let state: AlignPreviewState
    @FocusState private var focus: Bool
    private let scrub: ScrubController
    private let tint = PlayerKind.appleMusic.tint

    init(state: AlignPreviewState) {
        self.state = state
        let elapsed = 64.0
        let scrub = ScrubController(displayID: 1, seek: { _ in }, playback: { elapsed }, haptics: .silent,
                                    now: { 0 }, reduceMotion: { true }, previewsOnWallpaper: false)
        scrub.update(.init(duration: 200, trackKey: "preview", lines: PlayerPreviewScene.lines, canSeek: true))
        if state == .button { scrub.pointerMoved(x: ScrubMath.x(time: 64, duration: 200), y: 0) }
        self.scrub = scrub
    }

    var body: some View {
        ZStack {
            VideoFrame(shift: state.display.offset)
            // The lyric stays readable while lining up, to judge it by.
            Text("I said, ooh, I\u{2019}m blinded by the lights")
                .font(.system(size: 46, weight: .bold, design: .rounded))
                .foregroundStyle(.white)
                .shadow(color: .black.opacity(0.4), radius: 10)
                .blur(radius: state.aligning ? 4 : 22)
                .opacity(state.aligning ? 0.8 : 0.3)
                .offset(y: -150)
            PlayerColumn(scrubKind: .seekable) {
                CoverStrip(items: PlayerPreviewScene.queue, tint: tint, paused: state == .unavailable,
                           labelsDimmed: true, sidesDimmed: state.aligning, onTap: { _ in })
                    .frame(width: DesktopPlayerView.width, height: DesktopPlayerView.stripHeight)
            } scrub: {
                Group {
                    if state.aligning {
                        VideoAlignRow(display: state.display, tint: tint)
                    } else {
                        ScrubRow(controller: scrub, kind: .seekable, tint: tint, isPlaying: state != .unavailable,
                                 shown: true, showsRemaining: .constant(true))
                    }
                }
                .overlay(alignment: .trailing) {
                    VideoAlignButton(on: state.aligning,
                                     reason: { state == .unavailable ? MusicVideoAlignAvailability.notPlaying : nil },
                                     tint: tint) {}
                        .offset(x: VideoAlignButton.outset)
                }
            } controls: {
                if state.aligning {
                    VideoAlignControls(canReset: state == .kept, tint: tint, onReset: {}, onCancel: {}, onDone: {})
                } else {
                    PlayerControlsRow(kinds: [.appleMusic, .spotify], selected: .appleMusic, onSelect: { _ in },
                                      canControl: true, hasSong: true, isPlaying: state != .unavailable,
                                      onPrevious: {}, onPlayPause: {}, onNext: {}, query: .constant(""),
                                      searchFocused: $focus, onSubmit: {})
                }
            }
            .foregroundStyle(.white)
            .offset(y: 60)
            VStack {
                Spacer()
                Text(state.caption).font(.system(size: 12, weight: .medium)).foregroundStyle(.white.opacity(0.9))
                    .padding(.horizontal, 10).padding(.vertical, 4)
                    .background(Color.black.opacity(0.55), in: Capsule())
                    .padding(.bottom, 12)
            }
        }
        .environment(\.colorScheme, .dark)
        .environment(\.playerPreview, PlayerPreview(covers: PlayerPreviewScene.covers))
    }
}

/// A stand-in music video frame: a dusky street, a singer in silhouette.
/// `shift` moves the scene a little, as a different moment of the video.
private struct VideoFrame: View {
    let shift: Double

    var body: some View {
        ZStack {
            LinearGradient(colors: [Color(red: 0.05, green: 0.10, blue: 0.16), Color(red: 0.22, green: 0.10, blue: 0.18),
                                    Color(red: 0.55, green: 0.25, blue: 0.16)],
                           startPoint: .top, endPoint: .bottom)
            Circle().fill(Color(red: 1, green: 0.55, blue: 0.3).opacity(0.5)).frame(width: 300).blur(radius: 80)
                .offset(x: 210 - shift * 4, y: 40)
            ForEach(0..<7, id: \.self) { index in
                RoundedRectangle(cornerRadius: 2)
                    .fill(Color(red: 1, green: 0.85, blue: 0.55).opacity(0.25 + 0.08 * Double(index % 3)))
                    .frame(width: 4, height: 90 + CGFloat(index * 17 % 60))
                    .blur(radius: 1.5)
                    .offset(x: CGFloat(-360 + index * 120) - shift * 2, y: 20)
            }
            VStack(spacing: 0) {
                Circle().fill(Color.black.opacity(0.85)).frame(width: 74)
                RoundedRectangle(cornerRadius: 40).fill(Color.black.opacity(0.85)).frame(width: 170, height: 260)
                    .offset(y: -8)
            }
            .offset(x: -170 + shift, y: 170)
            // Letterbox, as music videos often are.
            VStack {
                Color.black.frame(height: 34)
                Spacer()
                Color.black.frame(height: 34)
            }
        }
        .clipped()
    }
}
