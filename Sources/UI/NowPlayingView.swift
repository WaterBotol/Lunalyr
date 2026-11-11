import SwiftUI
import CoreGraphics

@MainActor
struct NowPlayingView: View {
    let displayID: CGDirectDisplayID
    @ObservedObject var renderActivity: WindowRenderActivity
    var isSettingsPreview = false
    @EnvironmentObject private var nowPlaying: NowPlayingMonitor
    @ObservedObject private var lyricsVM = LyricsViewModel.shared
    @ObservedObject private var hover = HoverState.shared
    @ObservedObject var settings: Settings = .shared
    /// Passed to the web scenes, so a test can host this view with its own.
    /// Also decides where a music video plays ("Heavy scenes on main
    /// display only").
    @ObservedObject var visualizerPreferences: VisualizerPreferences = .shared
    @ObservedObject private var musicVideo = MusicVideoController.shared
    @ObservedObject private var mainDisplay = MainDisplayMonitor.shared
    // Read the current envelope on a scheduled frame. Subscribing to every
    // meter publication would trigger extra wallpaper redraws outside it.
    @ObservedObject private var readingOptions = RomanizationOptions.shared
    @ObservedObject private var songOffsets = SongOffsets.shared
    @ObservedObject private var desktopOptions = DesktopOptions.shared
    @ObservedObject private var floatingOptions = FloatingLyricsOptions.shared
    /// Published when an effect finishes compiling, so a crossfade into it
    /// can start.
    @ObservedObject private var effects = ShaderPipelineCache.shared
    @StateObject private var backdrop = BackdropLuminance()
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @ObservedObject private var extras = LunalyrExtras.shared
    @State private var paletteGeneration = UUID()
    @State private var palette = ColorExtractor.fallbackHSBPalette
    @State private var wallpaperPalette = AlbumWallpaperPalette(grid: ColorExtractor.fallbackHSBPalette)
    @State private var previousWallpaperPalette: AlbumWallpaperPalette?
    @State private var paletteChangedAt = Date.distantPast
    @State private var hasAlbumPalette = false
    /// Whether the palette fading out was the album's, so the album tint
    /// fades with the colours instead of popping in or out.
    @State private var hadAlbumPalette = false
    /// The cover the palette was last asked for, the track that cover is
    /// for, and whether its colours are still being taken.
    @State private var paletteRequest: (image: NSImage?, track: NowPlayingMonitor.Track?, inFlight: Bool)?
    /// The track whose cover's colours are applied: a song change's
    /// background waits until they are in.
    @State private var paletteTrack: NowPlayingMonitor.Track?
    /// The background layers on screen, bottom first, and their fades. It
    /// starts empty (an invisible placeholder) until the view appears.
    @State private var crossfade = BackgroundCrossfade<BackgroundLayer>(.generated(pattern: nil, seed: 0), ready: false,
                                                                        fadingInAt: .distantFuture)
    /// The mood and seed the background is chosen from: held across a song
    /// change until the new song's cover and lyrics are in, then kept for
    /// that recording, and kept while nothing plays (see
    /// `BackgroundChoiceHold`).
    @State private var choiceHold = BackgroundChoiceHold<NowPlayingMonitor.Track, BackgroundInputs?>(choice: nil)
    /// Layers whose view has drawn its first frame (visualisers, imported
    /// pictures): the crossfade into one waits for it.
    @State private var drawnLayers: Set<BackgroundLayer> = []
    /// A layer whose first frame took longer than `firstFrameWait`: shown
    /// anyway rather than holding the old one forever.
    @State private var firstFrameOverdue: BackgroundLayer?
    /// The song whose music video took longer than `musicVideoWait` to
    /// find: its surface falls back meanwhile.
    @State private var musicVideoOverdue: NowPlayingMonitor.Track?
    @State private var viewportSize: CGSize = .zero
    /// Renewed at natural breaks to keep the shader's clock small.
    @State private var shaderEpoch = 0
    @State private var shaderEpochStartedAt = Date()
    /// Bumped when a fade or notice ends, so a quiet frame clock can stop.
    @State private var clockCheck = 0

    private static let backgroundFadeDuration: TimeInterval = WallpaperMotion.surfaceFade
    private static let paletteFadeDuration: TimeInterval = WallpaperMotion.surfaceFade
    /// How long the "no lyrics" notice stays up, and how long it takes to go.
    private static let noticeHoldDuration: TimeInterval = WallpaperMotion.noticeHold
    private static let noticeFadeDuration: TimeInterval = WallpaperMotion.noticeFade
    private static let idleHoldDuration: TimeInterval = WallpaperMotion.idleNoticeHold
    /// How long a crossfade waits for a layer's first frame.
    static let firstFrameWait: TimeInterval = 3
    /// How long a song change in Music video waits on the cover stand-in
    /// for the song's video before showing the fallback wallpaper.
    static let musicVideoWait: TimeInterval = 3
    /// Ambient motion under Reduce Motion, matching the visualisers.
    private static let reducedMotionRate = 0.15
    /// How long the text backing takes to come or go when the lyric stage
    /// fills or empties at once.
    private static let backingFadeDuration: TimeInterval = 0.6

    /// A hidden or covered surface stops, and so do the desktop wallpapers
    /// while the user has paused them. The Settings preview keeps moving
    /// through that pause -- it is where changes are judged -- and stops
    /// only with its own window. System audio follows the same rule
    /// (`WindowRenderActivity.state`), so it runs exactly while a visualiser
    /// can be drawn. The Settings preview ignores the user's Pause wallpaper.
    private var renderingPaused: Bool {
        !renderActivity.shouldRender(pauseWhenCovered: settings.pauseWhenCovered,
                                     wallpaperPaused: !isSettingsPreview && extras.wallpaperPaused)
    }

    /// What the background should be right now. Changing any input -- the
    /// album gaining motion art, a settings change, the track's mood -- makes
    /// this differ from `currentLayer` and starts a crossfade.
    enum BackgroundLayer: Hashable {
        case desktop
        /// `still` shows the cover itself instead of its animation (Reduce
        /// Motion); `cover` is that song's cover, so a new one crossfades in.
        case motion(URL, still: Bool, cover: NSImage? = nil)
        case custom(URL)
        /// The song's music video, kept on the song's position.
        case musicVideo(MusicVideoSource)
        /// The video's stand-in (the cover's colours, dimmed) while the new
        /// song's video is being looked for, so a song change goes from one
        /// video to the next without the fallback wallpaper in between.
        case musicVideoPending
        /// A new epoch is the same effect restarted on a fresh clock.
        case shader(ShaderStyle, epoch: Int)
        /// `web` is where a web scene is drawn (live, or the native stand-in
        /// on another display); nil for the native scenes.
        case visualizer(AudioVisualizerStyle, web: WebVisualizerPlacement? = nil)
        case albumGradient
        /// `nil` pattern means colour field only. Each layer keeps its own
        /// seed, so the outgoing pattern keeps its geometry while it fades.
        case generated(pattern: BackgroundStyle?, seed: Int)

        /// Whether the wallpaper clock must redraw it every frame. Videos and
        /// still pictures draw themselves.
        var animatesWithClock: Bool {
            switch self {
            case .desktop, .motion, .custom, .musicVideo, .musicVideoPending: return false
            case .shader, .visualizer, .generated, .albumGradient: return true
            }
        }

        /// Everything but Your wallpaper covers what is beneath it (each
        /// layer is drawn over its own black), so a crossfade between two of
        /// them leaves the outgoing one fully drawn under the incoming one.
        var isOpaque: Bool { self != .desktop }
    }

    /// What a song's background is chosen from (see `BackgroundChoiceHold`).
    struct BackgroundInputs: Equatable {
        var mood: TrackMood
        var seed: Int
    }

    private var desiredLayer: BackgroundLayer {
        // Read once: on battery each read asks IOKit for the power source.
        let mode = settings.effectiveBackgroundMode
        if mode == .musicVideo {
            if let source = musicVideoHere { return .musicVideo(source) }
            if awaitsMusicVideo { return .musicVideoPending }
            // No video for this song, or kept off this display: whatever
            // was chosen before Music video, under the same power rules.
            return layer(for: Settings.effectiveMode(settings.musicVideoFallback, under: settings.powerConstraint))
        }
        return layer(for: mode)
    }

    /// The playing song's video is still being looked for, for a surface
    /// that would show it (see `musicVideoWait`).
    private var awaitsMusicVideo: Bool {
        guard let track = nowPlaying.current, musicVideoOverdue != track else { return false }
        return musicVideoLookupPending && showsMusicVideoHere
    }

    /// The video controller is looking for the playing song's video, or
    /// has not caught up with the song change yet (it still shows the last
    /// song's video, which `source(for:)` does not return for this one).
    private var musicVideoLookupPending: Bool {
        guard nowPlaying.current != nil else { return false }
        switch musicVideo.status {
        case .searching, .showing: return true
        case .off, .idle, .missing: return false
        }
    }

    /// Whether this surface shows music videos at all (display choice).
    private var showsMusicVideoHere: Bool {
        // Placement does not depend on the video, so any source asks it.
        MusicVideoController.placement(source: .youTube(""), preview: isSettingsPreview,
                                       heavyScenesOnMainDisplayOnly: visualizerPreferences.heavyScenesOnMainDisplayOnly,
                                       choice: settings.musicVideoDisplays, displays: mainDisplay.displays,
                                       display: effectDisplay, mainDisplay: mainDisplay.id) != nil
    }

    /// The song's video, when this surface shows it.
    private var musicVideoHere: MusicVideoSource? {
        MusicVideoController.placement(source: musicVideo.source(for: nowPlaying.current), preview: isSettingsPreview,
                                       heavyScenesOnMainDisplayOnly: visualizerPreferences.heavyScenesOnMainDisplayOnly,
                                       choice: settings.musicVideoDisplays, displays: mainDisplay.displays,
                                       display: effectDisplay, mainDisplay: mainDisplay.id)
    }

    /// The type drawn, with Music video resolved to the video or its fallback.
    private var drawnMode: Settings.BackgroundMode {
        let mode = settings.effectiveBackgroundMode
        guard mode == .musicVideo, musicVideoHere == nil, !awaitsMusicVideo else { return mode }
        return Settings.effectiveMode(settings.musicVideoFallback, under: settings.powerConstraint)
    }

    private func layer(for mode: Settings.BackgroundMode) -> BackgroundLayer {
        if mode == .auto,
           let motion = nowPlaying.motionArtwork, settings.motionArtworkEnabled,
           !settings.excludesMotion(artist: nowPlaying.current?.artist,
                                    album: nowPlaying.current?.album) {
            // Portrait displays get the tall rendition when the album has a
            // dependable one.
            return .motion(motion.rendition(portrait: prefersTallArtwork), still: reduceMotion,
                           cover: reduceMotion ? nowPlaying.currentTrackArtwork : nil)
        }
        let inputs = backgroundInputs
        let mood = inputs.mood
        let seed = inputs.seed
        if mode == .desktop { return .desktop }
        if mode == .custom, CustomWallpaperStore.isAvailable(settings.customWallpaperPath) {
            return .custom(URL(fileURLWithPath: settings.customWallpaperPath))
        }
        if mode == .visualizer {
            let style = settings.audioVisualizerStyle
            guard style.webEngine != nil else { return .visualizer(style) }
            return .visualizer(style, web: WebVisualizerView.placement(
                preview: isSettingsPreview, heavyScenesOnMainDisplayOnly: visualizerPreferences.heavyScenesOnMainDisplayOnly,
                display: effectDisplay, mainDisplay: mainDisplay.id))
        }
        if mode == .shader || (mode == .auto && mood.energy > 0.72) {
            return .shader(settings.resolvedShader(for: mood, seed: seed), epoch: shaderEpoch)
        }
        if mode == .colourOnly {
            return settings.albumGradientWallpaper ? .albumGradient : .generated(pattern: nil, seed: 0)
        }
        return .generated(pattern: settings.resolvedPattern(for: mood), seed: seed)
    }

    private var trackSeed: Int {
        TrackMood.seed(title: nowPlaying.current?.title ?? "", artist: nowPlaying.current?.artist ?? "")
    }

    private var artworkSaturation: Double {
        guard !palette.isEmpty else { return 0.5 }
        return palette.map(\.y).reduce(0, +) / Double(palette.count)
    }

    private var mood: TrackMood {
        TrackMood.derive(
            genre: nowPlaying.current?.genre,
            wordsPerSecond: lyricsVM.wordsPerSecond,
            artworkSaturation: artworkSaturation,
            seed: trackSeed
        )
    }

    /// The playing song's inputs as they stand now.
    private var liveInputs: BackgroundInputs { BackgroundInputs(mood: mood, seed: trackSeed) }

    /// What the background is chosen from: the held choice, once there is
    /// one (see `choiceHold`).
    private var backgroundInputs: BackgroundInputs { choiceHold.choice ?? liveInputs }

    /// The playing song's cover colours are applied and its lyrics are no
    /// longer loading: its background can be chosen.
    private var backgroundInputsSettled: Bool {
        guard let track = nowPlaying.current, let paletteTrack, paletteTrack.isSameTrack(as: track) else { return false }
        return !lyricsVM.isLoading
    }

    private struct SettleKey: Equatable {
        let track: NowPlayingMonitor.Track?
        let settled: Bool
    }

    /// Takes the playing song's background choice once its inputs settle
    /// (or `settleLimit` passes). A new song's effect clock is renewed in
    /// the same step, so a song change is one crossfade.
    private func settleBackgroundChoice() {
        var hold = choiceHold
        if hold.update(track: nowPlaying.current, choice: liveInputs, settled: backgroundInputsSettled, now: Date()) {
            renewShaderClock(.trackChange)
        }
        if hold != choiceHold { choiceHold = hold }
    }

    private var currentLayer: BackgroundLayer { crossfade.current }

    /// Whether `layer` has something to draw: anything but an effect still
    /// compiling on this surface's GPU. A crossfade into an effect that has
    /// never had anything to draw waits instead of fading towards an empty
    /// view (see `BackgroundCrossfade`).
    private func canDraw(_ layer: BackgroundLayer) -> Bool {
        switch layer {
        case .shader(let style, _):
            return effects.isSettled(style, display: effectDisplay)
        case .visualizer, .custom:
            // A visualiser compiles on first use, a web scene loads, and a
            // picture decodes: each says when its first frame is up.
            return drawnLayers.contains(layer) || firstFrameOverdue == layer
        default:
            return true
        }
    }

    /// Records that `layer`'s view has drawn, outside the view update that
    /// drew it.
    private func firstFrameDrawn(_ layer: BackgroundLayer) -> () -> Void {
        { if !drawnLayers.contains(layer) { drawnLayers.insert(layer) } }
    }

    /// A date for a fade that starts now: while the surface is paused,
    /// changes apply at once instead, so the still wallpaper shows the new
    /// layer and the new colours together, and resuming does not jump the
    /// last of a frozen fade.
    private var fadeStart: Date { renderingPaused ? .distantPast : Date() }

    private func prepareEffect(for layer: BackgroundLayer) {
        if case .shader(let style, _) = layer { effects.prepare(style, display: effectDisplay) }
    }

    /// The display whose GPU draws this surface's effects; the Settings
    /// preview takes the GPU the visualisers prefer.
    private var effectDisplay: CGDirectDisplayID? { isSettingsPreview ? nil : displayID }

    /// How much of the lyric stage is in use, following `lyricsSection`, so
    /// the text backing is drawn only behind something. Hidden lyrics leave
    /// nothing: the boolean, not the dissolve, so the backing eases out with
    /// them even while this frame clock is idle.
    private func stagePresence(at date: Date) -> Double {
        LyricStagePresence.presence(.init(
            hasTrack: nowPlaying.current != nil, idleSince: nowPlaying.idleSince,
            isLoading: lyricsVM.isLoading, hasTimedLines: !lyricsVM.lines.isEmpty,
            hasPlainLines: !lyricsVM.plainLines.isEmpty, notFound: lyricsVM.notFound,
            notFoundAt: lyricsVM.notFoundAt, noLyrics: settings.noLyrics, lyricsShown: extras.lyricsVisible && drawsLyrics),
            at: date, timing: .init(idleHold: Self.idleHoldDuration, noticeHold: Self.noticeHoldDuration,
                                    noticeFade: Self.noticeFadeDuration))
    }

    /// Whether this surface draws the lyrics: not on the desktop while
    /// they float over the apps (`LyricsSurface`).
    private var drawsLyrics: Bool { isSettingsPreview || !floatingOptions.isFloating }

    private var prefersTallArtwork: Bool {
        let size = viewportSize.width > 0 ? viewportSize
            : NSScreen.screens.first { $0.versoDisplayID == displayID }?.frame.size ?? .zero
        return size.height > size.width
    }

    /// The Settings preview follows the desktop's frame rate, except while
    /// power is being saved.
    private var frameInterval: Double {
        isSettingsPreview && settings.sparesGPU ? 1.0 / 15 : settings.effectiveAnimationRate.interval
    }

    /// When the frame clock may stop: every visible layer draws itself or
    /// holds still, no lyric is advancing, and fades and notices are done.
    private var quietAfter: Date? {
        var ends: [Date] = []
        if let end = crossfade.fadesEnd(duration: Self.backgroundFadeDuration) { ends.append(end) }
        ends.append(paletteChangedAt.addingTimeInterval(Self.paletteFadeDuration))
        if nowPlaying.current == nil, let since = nowPlaying.idleSince {
            ends.append(since.addingTimeInterval(Self.idleHoldDuration + Self.noticeFadeDuration))
        }
        if lyricsVM.notFound, settings.noLyrics == .notice, let at = lyricsVM.notFoundAt {
            ends.append(at.addingTimeInterval(Self.noticeHoldDuration + Self.noticeFadeDuration))
        }
        let lyricsAdvance = drawsLyrics && nowPlaying.current != nil && nowPlaying.isPlaying
            && !lyricsVM.lines.isEmpty && lyricsVM.plainLines.isEmpty
        // An outgoing layer only needs the clock until its fade ends, which
        // is already one of the transition deadlines.
        return FrameClockPolicy.quietAfter(.init(
            backgroundAnimates: currentLayer.animatesWithClock && !(reduceMotion && currentLayer == .albumGradient),
            lyricsAdvance: lyricsAdvance, transitionsEnd: ends))
    }

    var body: some View {
        let isHovered = !isSettingsPreview && hover.isActive(on: displayID)
        let quietAfter = self.quietAfter
        // Read so that a passing quiet deadline re-evaluates this body.
        let _ = clockCheck
        let clockIdle = FrameClockPolicy.isIdle(quietAfter: quietAfter, now: Date())
        // Power state is read once per update, never once per frame.
        let economical = settings.sparesGPU
        GeometryReader { geometry in
            let mood = self.mood

            // One timeline drives the gradient and the pattern so they share a
            // frame clock; the lyrics keep their own (see LyricStage). It stops
            // while the surface is hidden, and while nothing on it can change.
            TimelineView(.animation(minimumInterval: frameInterval, paused: renderingPaused || clockIdle)) { timeline in
                let now = timeline.date.timeIntervalSinceReferenceDate

                let speed = settings.effectSpeed
                // Reduce Motion slows the ambient wallpaper, as the
                // visualisers already slow themselves.
                let ambientSpeed = speed * (reduceMotion ? Self.reducedMotionRate : 1)
                // Kept steady: the shaders take this as `time`, where it drives
                // the slow breathing that must not lurch with the music.
                let colourPhase = VisualClock.gradient.advance(to: now, rate: ambientSpeed)
                // Ambient wallpapers and lyric gestures follow their own
                // continuous clocks. Only the dedicated visualiser consumes
                // audio; no global deformation or beat overlay remains.
                let flowPhase = VisualClock.ambientFlow.advance(to: now, rate: ambientSpeed * 0.70)
                let visualizerPhase = VisualClock.flow.advance(to: now, rate: speed * VisualClock.visualizerRate)
                let patternPhase = flowPhase * 0.5
                let cover = wallpaperPalette(at: renderingPaused ? .distantFuture : Date(timeIntervalSinceReferenceDate: now))

                // Background switches are crossfaded from a timestamp
                // rather than a SwiftUI transition: this view is rebuilt
                // every frame by TimelineView, which discards transition
                // state, so a `.transition` here would simply cut. An effect
                // that has not yet had anything to draw holds the fade at its
                // start.
                let layers = crossfade.layers(at: Date(), duration: Self.backgroundFadeDuration, isOpaque: \.isOpaque)
                let phases = LayerPhases(colour: colourPhase, flow: flowPhase, pattern: patternPhase,
                                         visualizer: visualizerPhase, economical: economical)
                let stage = stagePresence(at: Date())

                WallpaperViewport(size: geometry.size) {
                  ZStack {
                    // Keyed by the layer itself, so the outgoing layer stays
                    // the same view -- and the same video or web page -- for
                    // the whole fade instead of being rebuilt in a new slot.
                    ForEach(layers, id: \.layer) { drawn in
                        crossfaded(drawn.layer, opacity: drawn.opacity,
                                   phases: phases, size: geometry.size, cover: cover, stage: stage)
                    }
                  }
                } lyrics: {
                  ZStack {
                    // The lyrics run on their own clock (see LyricStage), so
                    // this frame passes them by unless an input changed.
                    // With Floating lyrics chosen they are drawn over the
                    // apps instead, never here too (the Settings preview
                    // still shows the style being edited).
                    if drawsLyrics {
                    LyricStage(
                        size: geometry.size,
                        mood: mood,
                        accent: lyricAccent,
                        trackSeed: trackSeed,
                        palette: cover.artworkStops,
                        interval: frameInterval,
                        paused: renderingPaused,
                        // The measured brightness behind the lyrics, so text
                        // colours over Your wallpaper or an imported picture
                        // are judged against what is really there.
                        backdrop: lyricBackdrop(for: cover),
                        displayID: effectDisplay
                    )
                    .equatable()
                    .frame(width: geometry.size.width, height: geometry.size.height)
                    .environment(\.lyricSwipePalette, LyricSwipePalette(hue: albumAccent.x, saturation: albumAccent.y))
                    // Under the player; clearer while its seek bar is dragged.
                    .modifier(PlayerHoverFocus(hovered: isHovered, displayID: effectDisplay))
                    }
                    // Shortcut confirmations stay sharp over that blur.
                    WallpaperNoteLayer(size: geometry.size, mood: mood, interval: frameInterval)
                        .equatable()
                  }
                }
                // The words, copies and echoes on screen are decoration for
                // assistive technology; the element below speaks for them.
                .accessibilityHidden(true)
            }
            // One element per surface for assistive technology: the line being
            // sung. It keeps its own slow clock, because the background clock
            // above idles while the lyrics run on theirs.
            .overlay {
                TimelineView(.animation(minimumInterval: 1, paused: renderingPaused)) { _ in
                    Color.clear
                        .accessibilityElement()
                        .accessibilityLabel("Lyrics")
                        .accessibilityValue(accessibleLyric(at: accessiblePlaybackTime))
                        .accessibilityAddTraits(.updatesFrequently)
                }
                .allowsHitTesting(false)
            }
            .onChange(of: geometry.size, initial: true) { _, size in
                if viewportSize != size { viewportSize = size }
            }
        }
        .ignoresSafeArea(edges: isSettingsPreview ? [] : .all)
        .onChange(of: ArtworkKey(image: nowPlaying.artwork, track: nowPlaying.artworkTrack)) {
            updatePalette(nowPlaying.artwork, for: nowPlaying.artworkTrack)
        }
        .onChange(of: desiredLayer) { _, next in
            // Hold the old layers alive just long enough to fade them out.
            // A layer that never had anything to draw was never seen, so the
            // fade carries on from the one that is on screen.
            prepareEffect(for: next)
            crossfade.change(to: next, ready: canDraw(next), at: fadeStart, duration: Self.backgroundFadeDuration)
            let kept = Set(crossfade.entries.map(\.layer))
            if !drawnLayers.isSubset(of: kept) { drawnLayers.formIntersection(kept) }
            if let overdue = firstFrameOverdue, !kept.contains(overdue) { firstFrameOverdue = nil }
        }
        .onChange(of: canDraw(currentLayer)) { _, ready in
            // The effect is ready: start the fade into it now. Once shown, it
            // stays shown -- its surface keeps drawing it on the GPU it has
            // while the display's new GPU compiles it.
            if ready { crossfade.becameReady(at: fadeStart) }
        }
        // A layer that never reports a first frame is shown anyway.
        .task(id: crossfade.isHeld ? currentLayer : nil) {
            guard crossfade.isHeld else { return }
            let held = currentLayer
            try? await Task.sleep(for: .seconds(Self.firstFrameWait))
            if !Task.isCancelled, crossfade.isHeld, currentLayer == held { firstFrameOverdue = held }
        }
        // A song change holds the background until the song's cover and
        // lyrics are in, then crossfades once.
        .onChange(of: SettleKey(track: nowPlaying.current, settled: backgroundInputsSettled), initial: true) {
            settleBackgroundChoice()
        }
        .task(id: choiceHold.deadline) {
            guard let deadline = choiceHold.deadline else { return }
            try? await Task.sleep(for: .seconds(max(0, deadline.timeIntervalSinceNow) + 0.01))
            if !Task.isCancelled { settleBackgroundChoice() }
        }
        // A video still being looked for after `musicVideoWait` gives way to
        // the fallback wallpaper.
        .task(id: musicVideoLookupPending ? nowPlaying.current : nil) {
            guard musicVideoLookupPending, let track = nowPlaying.current else { return }
            try? await Task.sleep(for: .seconds(Self.musicVideoWait))
            if !Task.isCancelled, nowPlaying.current == track { musicVideoOverdue = track }
        }
        .onChange(of: renderActivity.screensAsleep) { _, asleep in
            if !asleep { renewShaderClock(.wake) }
        }
        .onChange(of: backdropSource, initial: true) { _, source in
            backdrop.track(mode: source.mode, customPath: source.path, placement: source.placement,
                           displayID: displayID, screen: source.screen)
        }
        // Stop the frame clock the moment the last fade or notice ends.
        .task(id: quietAfter) {
            guard let quietAfter, quietAfter > Date() else { return }
            try? await Task.sleep(for: .seconds(quietAfter.timeIntervalSinceNow + 0.05))
            if !Task.isCancelled { clockCheck &+= 1 }
        }
        // A long-running effect is renewed even without a track change.
        .task(id: shaderEpoch) {
            try? await Task.sleep(for: .seconds(ShaderEpochPolicy.maximumAge))
            if !Task.isCancelled { renewShaderClock(.elapsed) }
        }
        .onAppear {
            updatePalette(nowPlaying.artwork, for: nowPlaying.artworkTrack)
            let layer = desiredLayer
            prepareEffect(for: layer)
            // A desktop surface fades in from its black window once its
            // first layer can draw, rather than popping in; the Settings
            // preview shows at once.
            crossfade = isSettingsPreview ? BackgroundCrossfade(layer, ready: canDraw(layer))
                : BackgroundCrossfade(layer, ready: canDraw(layer), fadingInAt: fadeStart)
        }
    }

    private struct LayerPhases {
        let colour: Double, flow: Double, pattern: Double, visualizer: Double
        let economical: Bool
    }

    private struct BackdropSource: Equatable {
        let mode: Settings.BackgroundMode
        let path: String
        let placement: Settings.MotionPlacement
        let screen: CGSize
    }

    private var backdropSource: BackdropSource {
        BackdropSource(mode: drawnMode, path: settings.customWallpaperPath,
                       placement: settings.customWallpaperPlacement, screen: viewportSize)
    }

    /// The cover and the track it belongs to. Compared by identity: holding
    /// the image keeps a new cover from reusing an old one's address.
    private struct ArtworkKey: Equatable {
        let image: NSImage?
        let track: NowPlayingMonitor.Track?

        static func == (lhs: Self, rhs: Self) -> Bool {
            lhs.image === rhs.image && lhs.track == rhs.track && lhs.track?.persistentID == rhs.track?.persistentID
        }
    }

    private func crossfaded(_ layer: BackgroundLayer, opacity: Double, phases: LayerPhases,
                            size: CGSize, cover: AlbumWallpaperPalette, stage: Double) -> some View {
        // `compositingGroup` flattens each layer with its own scrim before the
        // fade opacity is applied; without it the two scrims blend through
        // each other and the screen visibly lightens mid-transition. Videos,
        // web views and Metal views are included: their layers sit in one
        // group-opacity layer, so the fade applies to the whole layer rather
        // than to each part separately.
        //
        // Every layer but Your wallpaper sits on its own opaque black, so it
        // covers what is beneath it as it fades in (see `isOpaque`).
        backgroundLayer(layer, phases: phases, size: size, cover: cover, stage: stage)
            .background { if layer.isOpaque { Color.black.ignoresSafeArea() } }
            .compositingGroup()
            .opacity(opacity)
    }

    private func renewShaderClock(_ trigger: ShaderEpochPolicy.Trigger) {
        guard ShaderEpochPolicy.shouldRenew(age: Date().timeIntervalSince(shaderEpochStartedAt), trigger: trigger) else { return }
        shaderEpoch &+= 1
        shaderEpochStartedAt = Date()
    }

    /// Renders one background layer. Each carries its own scrim so the two
    /// sides of a crossfade stay legible throughout the blend.
    @ViewBuilder
    private func backgroundLayer(_ layer: BackgroundLayer, phases: LayerPhases, size: CGSize,
                                 cover: AlbumWallpaperPalette, stage: Double) -> some View {
        // The colour field drifted on a metronome and answered the music only
        // by leaning its blobs 5% wider -- effectively deaf, which made
        // "Colour only" the one mode that ignored what was playing. It now
        // orbits on the same clock as everything else, at a third the rate:
        // the blobs are large and slow, so they need far less of it than a
        // shader does to read as moving with the track.
        let colourFlow = phases.flow * 0.34
        let backing = textBackingAmount(for: layer, cover: cover, size: size)
        // The album tint follows the palette's fade.
        let tint = albumTint(at: renderingPaused ? .distantFuture : Date())
        switch layer {
        case .desktop:
            ZStack {
                Color.clear
                textBacking(backing, presence: stage, size: size)
            }

        case .custom(let url):
            ZStack {
                // Reduce Motion holds an imported video on a still frame.
                CustomWallpaperView(url: url, placement: settings.customWallpaperPlacement,
                                    paused: renderingPaused || reduceMotion, onFirstFrame: firstFrameDrawn(layer))
                if tint > 0 { cover.gradient.opacity(0.18 * tint).ignoresSafeArea() }
                scrim
                textBacking(backing, presence: stage, size: size)
            }

        case .musicVideo(let source):
            ZStack {
                // Reduce Motion shows a still: a library video's paused
                // frame, or a YouTube video's own still (no player at all).
                // A paused or covered wallpaper holds the video's frame.
                MusicVideoBackground(source: source, paused: renderingPaused,
                                     dim: settings.musicVideoDim, blur: settings.musicVideoBlur,
                                     preview: isSettingsPreview, still: reduceMotion, standIn: cover.gradient)
                scrim
                textBacking(backing, presence: stage, size: size)
            }

        case .musicVideoPending:
            ZStack {
                // MusicVideoBackground's stand-in, as it looks before a
                // video's first frame.
                cover.gradient.ignoresSafeArea()
                Color.black.opacity(min(Settings.musicVideoDimRange.upperBound, max(0, settings.musicVideoDim)))
                    .ignoresSafeArea()
                scrim
                textBacking(backing, presence: stage, size: size)
            }

        case .motion(let url, let still, let artwork):
            ZStack {
                cover.gradient.ignoresSafeArea()
                Group {
                    if still {
                        stillCover(artwork)
                    } else {
                        MotionArtworkView(url: url, placement: settings.motionPlacement, isPaused: renderingPaused)
                    }
                }
                .ignoresSafeArea()
                // Only softened enough to stop fine detail fighting the
                // type -- the footage should still be recognisable.
                .blur(radius: settings.motionBlur)
                // A fixed overscan, purely to keep the blur from showing
                // the frame edge. It used to breathe with the beat; that
                // twitched the whole picture on every hit.
                .scaleEffect(1.04)
                if tint > 0 { cover.gradient.opacity(0.18 * tint).ignoresSafeArea() }
                Color.black.opacity(0.40 * settings.scrimStrength).ignoresSafeArea()
                RadialGradient(
                    colors: [.black.opacity(0.5), .black.opacity(0.16), .clear],
                    center: .center,
                    startRadius: 0,
                    endRadius: max(size.width, size.height) * 0.55
                )
                .ignoresSafeArea()
                textBacking(backing, presence: stage, size: size)
            }

        case .shader(let style, _):
            ZStack {
                // The album tint and colour richness are applied inside the
                // effect's own pass, at a bounded resolution.
                ShaderBackgroundView(style: style, time: phases.colour, flow: phases.flow, palette: cover,
                                     vividness: settings.vividness, paused: renderingPaused,
                                     preview: isSettingsPreview, economical: phases.economical,
                                     displayID: effectDisplay)
                    .ignoresSafeArea()
                scrim
                textBacking(backing, presence: stage, size: size)
            }

        case .visualizer(let style, let web):
            ZStack {
                if let engine = style.webEngine {
                    let originalLight3D = style == .light3D && settings.light3DColours == .original
                    WebVisualizerView(engine: engine, preset: settings.wavefieldPreset, projection: settings.wavefieldProjection,
                        sensitivity: settings.audioSensitivity, motion: settings.effectSpeed, tick: phases.visualizer, paused: renderingPaused, preview: isSettingsPreview, palette: hasAlbumPalette && !originalLight3D ? cover : nil,
                        displayID: effectDisplay, placement: web, onFirstFrame: firstFrameDrawn(layer),
                        preferences: visualizerPreferences)
                        .id(engine)
                        .ignoresSafeArea()
                        .saturation(originalLight3D ? 1 : settings.vividness)
                } else {
                    AudioVisualizerView(style: style, time: phases.visualizer, paused: renderingPaused, preview: isSettingsPreview,
                        palette: hasAlbumPalette ? cover : nil, onFirstFrame: firstFrameDrawn(layer))
                        .ignoresSafeArea()
                }
                scrim
                textBacking(backing, presence: stage, size: size)
            }

        case .albumGradient:
            let turn = reduceMotion ? 0 : sin(phases.flow * 0.16) * 0.2
            ZStack {
                LinearGradient(colors: cover.gradientStops(vividness: settings.vividness).map(AlbumWallpaperPalette.color),
                    startPoint: UnitPoint(x: 0, y: 0.22 + turn), endPoint: UnitPoint(x: 1, y: 0.78 - turn))
                    .ignoresSafeArea()
                scrim
                textBacking(backing, presence: stage, size: size)
            }

        case .generated(let pattern, let seed):
            ZStack {
                // Drawn at a bounded pixel budget, richness included.
                AnimatedGradientView(
                    palette: cover,
                    time: colourFlow,
                    vividness: settings.vividness,
                    economical: phases.economical
                )
                if let pattern {
                    FractalBackgroundView(
                        seed: seed,
                        phase: phases.pattern,
                        style: pattern
                    )
                    .opacity(settings.patternOpacity)
                }
                scrim
                textBacking(backing, presence: stage, size: size)
            }
        }
    }

    /// The album's own cover, softened and filled like its animation, for
    /// Reduce Motion. Each layer keeps its own song's cover, so a new one
    /// crossfades in rather than replacing the outgoing one at once.
    @ViewBuilder
    private func stillCover(_ artwork: NSImage?) -> some View {
        if let artwork {
            GeometryReader { geometry in
                Image(nsImage: artwork)
                    .resizable()
                    .aspectRatio(contentMode: settings.motionPlacement == .fit ? .fit : .fill)
                    .frame(width: geometry.size.width, height: geometry.size.height,
                           alignment: settings.motionPlacement == .fillTop ? .top
                               : settings.motionPlacement == .fillBottom ? .bottom : .center)
                    .clipped()
            }
        }
    }

    private var scrim: some View {
        let strength = settings.scrimStrength
        return LinearGradient(
            colors: WallpaperShade.scrim.map { .black.opacity($0 * strength) },
            startPoint: .top, endPoint: .bottom
        )
        .ignoresSafeArea()
    }

    /// Soft shade behind the lyric stage: as strong as this layer's backdrop
    /// needs for white type to reach its contrast target -- across the whole
    /// stage, where long lines and plain lyrics end, easing out by the
    /// screen's edges -- or the user's own amount in the ellipse it has always
    /// been; and only while the stage is in use. See `TextBackingShade`.
    @ViewBuilder
    private func textBacking(_ amount: Double, presence: Double, size: CGSize) -> some View {
        if amount > WallpaperShade.minimumBacking {
            TextBackingShade(amount: amount, manual: desktopOptions.manualTextBacking != nil, size: size)
            // Notices fade it frame by frame on the running clock; a stage
            // that fills or empties at once (a track starting, lyrics turning
            // out to be missing) eases it in or out.
            .opacity(presence)
            .animation(.easeInOut(duration: Self.backingFadeDuration), value: presence > 0.001)
            .allowsHitTesting(false)
        }
    }

    private func lyricBackdrop(for cover: AlbumWallpaperPalette) -> Double? {
        switch currentLayer {
        case .albumGradient: return cover.gradientLuminance(vividness: settings.vividness)
        case .generated(pattern: nil, seed: _): return nil
        default: return backdrop.luminance
        }
    }

    private func textBackingAmount(for layer: BackgroundLayer, cover: AlbumWallpaperPalette, size: CGSize) -> Double {
        if let manual = desktopOptions.manualTextBacking { return manual }
        // The lyric target whenever a song plays, not only once its lyrics
        // are in: switching then stepped the backing darker mid-fade.
        let hasLyrics = nowPlaying.current != nil || !lyricsVM.lines.isEmpty || !lyricsVM.plainLines.isEmpty
        let target = hasLyrics ? LyricLegibility.pastelBackingTarget(increaseContrast: settings.increaseContrast)
            : LyricLegibility.targetContrast(baseSize: LyricViewportMetrics(size: size).baseSize)
        let strength = max(0, settings.scrimStrength)
        // The shared scrim is lightest exactly where the lyrics sit.
        let scrim = WallpaperShade.scrimAtStage(strength: strength)
        let luminance: Double, existing: Double
        switch layer {
        case .desktop:
            luminance = backdrop.luminance ?? LyricLegibility.unknownBackdropLuminance
            existing = 0
        case .custom:
            luminance = backdrop.luminance ?? LyricLegibility.unknownBackdropLuminance
            existing = scrim
        case .musicVideo, .musicVideoPending:
            // Footage is unmeasured; the dimming is laid over it before the scrim.
            luminance = LyricLegibility.unknownBackdropLuminance
            existing = 1 - (1 - min(1, max(0, settings.musicVideoDim))) * (1 - scrim)
        case .motion:
            // From the interpolated colours, so it follows the palette fade.
            luminance = Self.coverLuminance(cover)
            existing = 1 - (1 - min(1, 0.40 * strength)) * 0.5
        case .shader:
            luminance = LyricLegibility.shaderLuminance(cover)
            existing = scrim
        case .visualizer:
            luminance = LyricLegibility.visualizerLuminance(cover)
            existing = scrim
        case .albumGradient:
            luminance = cover.gradientLuminance(vividness: settings.vividness)
            existing = scrim
        case .generated(let pattern, _):
            luminance = LyricLegibility.colourFieldLuminance(cover, patternOpacity: pattern == nil ? nil : settings.patternOpacity)
            existing = scrim
        }
        return LyricLegibility.stageBacking(forLuminance: luminance, existingDarkening: existing, target: target)
    }

    /// The line being sung, for VoiceOver.
    /// Where the song is, with the same offsets the lyric stage applies.
    private var accessiblePlaybackTime: TimeInterval {
        nowPlaying.playbackTime() + settings.lyricOffset + songOffsets.offset(for: lyricsVM.currentQuery)
    }

    private func accessibleLyric(at time: TimeInterval) -> String {
        guard let track = nowPlaying.current else { return "Nothing playing" }
        if !lyricsVM.lines.isEmpty {
            guard let index = lyricsVM.activeLineIndex(at: time), lyricsVM.lines.indices.contains(index) else {
                return track.title
            }
            let text = lyricsVM.lines[index].text.trimmingCharacters(in: .whitespacesAndNewlines)
            return text.isEmpty ? track.title : text
        }
        if !lyricsVM.plainLines.isEmpty {
            return lyricsVM.plainLines.prefix(4).filter { !$0.isEmpty }.joined(separator: " ")
        }
        return lyricsVM.notFound ? "Lyrics unavailable" : track.title
    }

    private func paletteProgress(at date: Date) -> Double {
        Easing.smoothstep(date.timeIntervalSince(paletteChangedAt) / Self.paletteFadeDuration)
    }

    private func wallpaperPalette(at date: Date) -> AlbumWallpaperPalette {
        let progress = paletteProgress(at: date)
        guard progress < 1, let previousWallpaperPalette else { return wallpaperPalette }
        return previousWallpaperPalette.interpolated(to: wallpaperPalette, fraction: progress)
    }

    /// How much of the album tint is drawn, 0 to 1, following the palette
    /// fade between an album's colours and the fallback ones.
    private func albumTint(at date: Date) -> Double {
        let progress = paletteProgress(at: date)
        return (hadAlbumPalette ? 1 - progress : 0) + (hasAlbumPalette ? progress : 0)
    }

    /// `LyricLegibility.coverLuminance` of the colours on screen: the
    /// brightest cell of the cover's middle row.
    nonisolated static func coverLuminance(_ cover: AlbumWallpaperPalette) -> Double {
        let middle = [3, 4, 5].filter(cover.artworkStops.indices.contains).map { cover.artworkStops[$0] }
        return middle.map { LyricLegibility.luminance(SIMD3<Double>(Double($0.x), Double($0.y), Double($0.z))) }.max()
            ?? LyricLegibility.unknownBackdropLuminance
    }

    private func applyPalette(_ extracted: [SIMD3<Double>], hasArtwork: Bool) {
        let now = Date()
        // While paused the colours change at once, with the layers.
        let start = fadeStart
        hadAlbumPalette = albumTint(at: now) >= 0.5
        previousWallpaperPalette = wallpaperPalette(at: now)
        wallpaperPalette = AlbumWallpaperPalette(grid: extracted)
        paletteChangedAt = start
        hasAlbumPalette = hasArtwork
        palette = extracted
        if let request = paletteRequest {
            paletteRequest = (request.image, request.track, false)
            paletteTrack = request.track
        }
        DesktopWallpaperController.shared.currentPalette = extracted
    }

    private var albumAccent: SIMD3<Double> { ColorExtractor.accent(in: palette) }

    private var lyricAccent: Color {
        let hsb = albumAccent
        return Color(hue: hsb.x, saturation: min(0.65, max(0.2, hsb.y)), brightness: max(0.78, hsb.z))
    }

    /// Takes the colours from `artwork`, the cover of `track`. The same
    /// cover for another track (the next song on the album) needs no new
    /// colours, only the note that they are that track's now.
    private func updatePalette(_ artwork: NSImage?, for track: NowPlayingMonitor.Track?) {
        if let request = paletteRequest, request.image === artwork {
            paletteRequest = (artwork, track, request.inFlight)
            if !request.inFlight { paletteTrack = track }
            return
        }
        let token = UUID()
        paletteGeneration = token
        paletteRequest = (artwork, track, artwork != nil)
        guard let artwork else {
            applyPalette(ColorExtractor.fallbackHSBPalette, hasArtwork: false)
            return
        }
        Task.detached(priority: .userInitiated) {
            let extracted = ColorExtractor.extractPalette(from: artwork)
            await MainActor.run {
                guard self.paletteGeneration == token else { return }
                self.applyPalette(extracted, hasArtwork: true)
            }
        }
    }
}
