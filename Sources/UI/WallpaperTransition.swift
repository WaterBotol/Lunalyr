import Foundation

/// How the wallpaper's background goes from one layer to the next: a song
/// change (one video to the next, a video to its fallback, one cover to
/// another) or a mode change. The crossfade itself is `BackgroundCrossfade`;
/// this decides when it may start and how long it takes.
///
/// The outgoing layer stays fully drawn until the incoming one has a
/// picture. An outgoing music video is paused on its last frame for that
/// while (`MusicVideoHold.outgoing`: YouTube shows its frozen frame, a
/// paused AVPlayer keeps its picture), so only one video ever plays: the
/// fade goes from a still to the new video once the new player shows its
/// first frame, never through black or the stand-in. A player that has
/// shown nothing after `videoFirstFrameWait` is faded to anyway, onto its
/// stand-in, so a slow video never holds the old song for long.
///
/// While the next song's video is still being looked for, its stand-in
/// (`musicVideoPending`) is not faded to over what is on screen: the old
/// video's frame (or the fallback wallpaper) holds until the video is
/// found and showing, or the look-up gives up (`videoLookupWait`) and the
/// fallback is chosen instead.
///
/// Reduce Motion keeps the same holds but makes the fade a short dissolve;
/// nothing scales or moves either way.
enum WallpaperTransition {
    /// What an incoming layer needs before the fade into it may start.
    enum Gate: Equatable {
        /// Draws at once: a colour field, a gradient, a still cover, Your
        /// wallpaper.
        case none
        /// A GPU effect: holds until it has compiled on this display's GPU.
        case compile
        /// Holds until it draws its first frame (a visualiser, an imported
        /// picture), or `firstFrameWait` passes.
        case firstFrame
        /// A video player -- a music video, or animated cover art: holds
        /// until it shows a picture (a music video's still, under Reduce
        /// Motion), or `videoFirstFrameWait` passes. Over Your wallpaper it
        /// does not hold (see `canDraw`).
        case video
        /// The stand-in while the song's video is looked for: never faded to
        /// over something on screen, only shown as a surface's first layer.
        /// The look-up's own timeout (`videoLookupWait`) replaces it with the
        /// fallback; `firstFrameWait(for:)` is only a backstop.
        case videoLookup
    }

    /// How long a layer's first frame is waited for.
    static let firstFrameWait: TimeInterval = 3
    /// How long a music video's first picture is waited for: a YouTube page
    /// takes a moment to load and start.
    static let videoFirstFrameWait: TimeInterval = 4
    /// How long a song change in Music video waits for the song's video to
    /// be found before showing the fallback wallpaper.
    static let videoLookupWait: TimeInterval = 3

    /// The longest the outgoing wallpaper can be held on a song change in
    /// Music video: the look-up, then the new player's first picture.
    static var longestHold: TimeInterval { videoLookupWait + videoFirstFrameWait }

    /// The crossfade's length: the surface fade (a smoothstep, ease in and
    /// out), or a short plain dissolve under Reduce Motion.
    static func fadeDuration(reduceMotion: Bool) -> TimeInterval {
        reduceMotion ? WallpaperMotion.reducedDissolve : WallpaperMotion.surfaceFade
    }

    /// Whether a layer behind `gate` has something to draw, so the fade into
    /// it can run. `firstFrameDrawn`: its view reported a picture.
    /// `overdue`: its wait ran out. `firstLayer`: nothing is on screen to
    /// hold instead (the surface's first layer). `compiled`: a GPU effect is
    /// ready on this display. `overOpaque`: the layer on screen beneath
    /// covers the desktop. Leaving Your wallpaper, the wallpaper window
    /// turns opaque once the incoming layer should have faded in
    /// (`WallpaperOpacityPolicy`): a video or its stand-in is not held there,
    /// as before, or the screen would go black under it.
    static func canDraw(_ gate: Gate, firstFrameDrawn: Bool, overdue: Bool, firstLayer: Bool = false,
                        compiled: Bool = true, overOpaque: Bool = true) -> Bool {
        switch gate {
        case .none: return true
        case .compile: return compiled
        case .firstFrame: return firstFrameDrawn || overdue
        case .video: return firstFrameDrawn || overdue || !overOpaque
        case .videoLookup: return firstLayer || overdue || !overOpaque
        }
    }

    /// Whether the wallpaper's frame clock must keep running for its
    /// background. While the incoming layer is held, the layers beneath stay
    /// on screen and a clock-driven one (a visualiser, an effect, a
    /// pattern) must keep moving rather than freeze until the fade starts.
    /// `layers` bottom first, the last being the one faded to.
    static func backgroundAnimates(layers: [(animates: Bool, isReady: Bool)], held: Bool) -> Bool {
        guard let top = layers.last else { return false }
        if top.animates { return true }
        guard held else { return false }
        return layers.dropLast().contains { $0.animates && $0.isReady }
    }

    /// How long a held layer waits before it is shown anyway.
    static func firstFrameWait(for gate: Gate) -> TimeInterval {
        switch gate {
        case .none, .compile, .firstFrame: return firstFrameWait
        case .video: return videoFirstFrameWait
        // Longer than the look-up's own timeout, which decides first.
        case .videoLookup: return videoLookupWait + firstFrameWait
        }
    }

    /// A player in a held layer (drawn at no opacity yet) shows its first
    /// picture at once rather than easing it in: the wallpaper's crossfade
    /// is the fade, and an eased reveal inside it would show the stand-in
    /// through the start of the crossfade. Once shown, reveals ease again.
    static func revealsAtOnce(isCurrent: Bool, held: Bool) -> Bool { isCurrent && held }
}

extension NowPlayingView.BackgroundLayer {
    /// What the fade into this layer waits for.
    var transitionGate: WallpaperTransition.Gate {
        switch self {
        case .desktop, .albumGradient, .generated: return .none
        case .motion(_, let still, _): return still ? .none : .video
        case .custom, .visualizer: return .firstFrame
        case .shader: return .compile
        case .musicVideo: return .video
        case .musicVideoPending: return .videoLookup
        }
    }
}
