import AppKit
import SwiftUI

/// The album palette the wallpaper last took from the cover, shared with the
/// floating lyrics so the cover is not read twice
/// (`DesktopWallpaperController.currentPalette` publishes it here).
@MainActor
final class FloatingLyricsPalette: ObservableObject {
    static let shared = FloatingLyricsPalette()
    @Published var grid: [SIMD3<Double>] = ColorExtractor.fallbackHSBPalette
}

/// What the floating window's content needs from its controller.
@MainActor
final class FloatingLyricsState: ObservableObject {
    /// The window is on screen: the lyric clock runs.
    @Published var active = false
    /// Edit position: draggable, with a resize grip and Done.
    @Published var editing = false
}

/// The floating lyrics: the wallpaper's own lyric stage (`LyricStage`, same
/// clock, layouts, colours and readings), on a soft shade instead of a
/// wallpaper, so it reads over any app. No background, video or visualiser.
struct FloatingLyricsView: View {
    @ObservedObject var state: FloatingLyricsState
    @ObservedObject var options: FloatingLyricsOptions = .shared
    /// A drag in edit mode: `resizing` for the corner grip; `ended` once let go.
    var onDrag: (_ resizing: Bool, _ ended: Bool) -> Void = { _, _ in }
    var onDone: () -> Void = {}
    @ObservedObject private var palette = FloatingLyricsPalette.shared
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var lyricsVM = LyricsViewModel.shared
    @EnvironmentObject private var nowPlaying: NowPlayingMonitor
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency

    var body: some View {
        GeometryReader { geometry in
            let look = FloatingLyricsLook(grid: palette.grid, track: nowPlaying.current,
                                          wordsPerSecond: lyricsVM.wordsPerSecond)
            ZStack {
                FloatingLyricsBackdrop(strength: options.backdrop, reduceTransparency: reduceTransparency)
                LyricStage(size: geometry.size, mood: look.mood, accent: look.accent, trackSeed: look.seed,
                           palette: look.cover.artworkStops, interval: settings.effectiveAnimationRate.interval,
                           paused: !state.active, textSize: CGFloat(options.textSize))
                    .equatable()
                    .frame(width: geometry.size.width, height: geometry.size.height)
                    .environment(\.lyricSwipePalette, LyricSwipePalette(hue: look.albumAccent.x, saturation: look.albumAccent.y))
                    .environment(\.lyricBackdropOverride,
                                 FloatingBackdrop.luminance(strength: options.backdrop, reduceTransparency: reduceTransparency))
                    // One soft shadow for the whole stage, so thin type
                    // still reads where the shade fades out.
                    .compositingGroup()
                    .shadow(color: .black.opacity(0.35 + 0.35 * options.backdrop), radius: 6, y: 1)
                    .accessibilityHidden(true)
                if state.editing {
                    FloatingEditChrome(placeholder: nowPlaying.current == nil || lyricsVM.lines.isEmpty
                                       && lyricsVM.plainLines.isEmpty,
                                       onDrag: onDrag, onDone: onDone)
                }
            }
        }
        .ignoresSafeArea()
    }
}

/// The colours and character the wallpaper derives from the song, worked out
/// the same way (`NowPlayingView`'s mood, seed and accent).
struct FloatingLyricsLook {
    let cover: AlbumWallpaperPalette
    let albumAccent: SIMD3<Double>
    let accent: Color
    let mood: TrackMood
    let seed: Int

    init(grid: [SIMD3<Double>], track: NowPlayingMonitor.Track?, wordsPerSecond: Double) {
        cover = AlbumWallpaperPalette(grid: grid)
        albumAccent = ColorExtractor.accent(in: grid)
        accent = Color(hue: albumAccent.x, saturation: min(0.65, max(0.2, albumAccent.y)),
                       brightness: max(0.78, albumAccent.z))
        seed = TrackMood.seed(title: track?.title ?? "", artist: track?.artist ?? "")
        let saturation = grid.isEmpty ? 0.5 : grid.map(\.y).reduce(0, +) / Double(grid.count)
        mood = TrackMood.derive(genre: track?.genre, wordsPerSecond: wordsPerSecond,
                                artworkSaturation: saturation, seed: seed)
    }
}

/// A soft dark shade behind the floating lyrics: a stretched radial fade,
/// darkest where the words sit, gone at the window's edge, so there is no
/// box. Reduce Transparency draws a plain, nearly opaque panel instead.
struct FloatingLyricsBackdrop: View {
    let strength: Double
    let reduceTransparency: Bool

    var body: some View {
        GeometryReader { geometry in
            let size = geometry.size
            if reduceTransparency {
                RoundedRectangle(cornerRadius: min(22, size.height * 0.2), style: .continuous)
                    .fill(Color.black.opacity(0.86))
                    .padding(4)
            } else if strength > 0.01 {
                let amount = FloatingBackdrop.maximumShade * min(1, strength)
                let side = max(1, size.height)
                RadialGradient(stops: [.init(color: .black.opacity(amount), location: 0),
                                       .init(color: .black.opacity(amount * 0.8), location: 0.55),
                                       .init(color: .black.opacity(0), location: 1)],
                               center: .center, startRadius: 0, endRadius: side / 2)
                    .frame(width: side, height: side)
                    .scaleEffect(x: max(1, size.width / side), y: 1)
                    .frame(width: size.width, height: size.height)
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

/// Edit position: an outline, a hint, Done, and a grip in the corner. The
/// whole window moves with a drag; the grip resizes it.
struct FloatingEditChrome: View {
    /// Nothing playing: say where the lyrics will be.
    let placeholder: Bool
    let onDrag: (_ resizing: Bool, _ ended: Bool) -> Void
    let onDone: () -> Void

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(Color.black.opacity(0.18))
                .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .strokeBorder(Color.lunalyrAccent, style: StrokeStyle(lineWidth: 2, dash: [7, 5])))
                .contentShape(Rectangle())
                .gesture(drag(resizing: false))
            if placeholder {
                Text("Lyrics appear here")
                    .font(.system(size: 22, weight: .semibold))
                    .foregroundStyle(.white.opacity(0.85))
                    .shadow(color: .black.opacity(0.6), radius: 4)
                    .allowsHitTesting(false)
            }
            VStack {
                HStack(spacing: 8) {
                    Label("Drag to move, corner to resize", systemImage: "arrow.up.and.down.and.arrow.left.and.right")
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(.white)
                        .padding(.horizontal, 10)
                        .padding(.vertical, 5)
                        .background(Capsule().fill(Color.black.opacity(0.6)))
                        .allowsHitTesting(false)
                    Spacer(minLength: 0)
                    Button("Done", action: onDone)
                        .buttonStyle(.borderedProminent)
                        .controlSize(.small)
                        .help("Locks the floating lyrics in place; clicks pass through them again")
                }
                Spacer(minLength: 0)
                HStack {
                    Spacer(minLength: 0)
                    Image(systemName: "arrow.up.left.and.arrow.down.right")
                        .font(.system(size: 12, weight: .bold))
                        .foregroundStyle(.white)
                        .frame(width: 26, height: 26)
                        .background(Circle().fill(Color.lunalyrAccent))
                        .contentShape(Rectangle().inset(by: -6))
                        .gesture(drag(resizing: true))
                        .accessibilityLabel("Resize")
                }
            }
            .padding(8)
        }
    }

    private func drag(resizing: Bool) -> some Gesture {
        // The window moves under the pointer, so the controller follows the
        // pointer on screen rather than this gesture's own translation.
        DragGesture(minimumDistance: 1, coordinateSpace: .global)
            .onChanged { _ in onDrag(resizing, false) }
            .onEnded { _ in onDrag(resizing, true) }
    }
}
