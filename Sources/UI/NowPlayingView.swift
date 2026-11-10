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
    /// The background layer on screen, the one it replaced, and the fade
    /// between them.
    @State private var crossfade = BackgroundCrossfade<BackgroundLayer>(.generated(pattern: nil, seed: 0))
    /// The last track's look, kept while nothing plays so a pause or a stop
    /// does not redraw the wallpaper as a different one.
    @State private var heldMood: TrackMood?
    @State private var heldSeed: Int?
    @State private var viewportSize: CGSize = .zero
    /// Renewed at natural breaks to keep the shader's clock small.
    @State private var shaderEpoch = 0
    @State private var shaderEpochStartedAt = Date()
    /// Bumped when a fade or notice ends, so a quiet frame clock can stop.
    @State private var clockCheck = 0

    private static let backgroundFadeDuration: TimeInterval = 0.75
    private static let paletteFadeDuration: TimeInterval = 0.9
    /// How long the "no lyrics" notice stays up, and how long it takes to go.
    private static let noticeHoldDuration: TimeInterval = 4.0
    private static let noticeFadeDuration: TimeInterval = 1.0
    private static let idleHoldDuration: TimeInterval = 5.0
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
        /// `still` shows the cover itself instead of its animation.
        case motion(URL, still: Bool)
        case custom(URL)
        /// The song's music video, kept on the song's position.
        case musicVideo(MusicVideoSource)
        /// A new epoch is the same effect restarted on a fresh clock.
        case shader(ShaderStyle, epoch: Int)
        case visualizer(AudioVisualizerStyle)
        case albumGradient
        /// `nil` pattern means colour field only. Each layer keeps its own
        /// seed, so the outgoing pattern keeps its geometry while it fades.
        case generated(pattern: BackgroundStyle?, seed: Int)

        /// Whether the wallpaper clock must redraw it every frame. Videos and
        /// still pictures draw themselves.
        var animatesWithClock: Bool {
            switch self {
            case .desktop, .motion, .custom, .musicVideo: return false
            case .shader, .visualizer, .generated, .albumGradient: return true
            }
        }
    }

    private var desiredLayer: BackgroundLayer {
        // Read once: on battery each read asks IOKit for the power source.
        let mode = settings.effectiveBackgroundMode
        if mode == .musicVideo {
            if let source = musicVideoHere { return .musicVideo(source) }
            // No video for this song, or kept off this display: whatever
            // was chosen before Music video, under the same power rules.
            return layer(for: Settings.effectiveMode(settings.musicVideoFallback, under: settings.powerConstraint))
        }
        return layer(for: mode)
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
        guard mode == .musicVideo, musicVideoHere == nil else { return mode }
        return Settings.effectiveMode(settings.musicVideoFallback, under: settings.powerConstraint)
    }

    private func layer(for mode: Settings.BackgroundMode) -> BackgroundLayer {
        if mode == .auto,
           let motion = nowPlaying.motionArtwork, settings.motionArtworkEnabled,
           !settings.excludesMotion(artist: nowPlaying.current?.artist,
                                    album: nowPlaying.current?.album) {
            // Portrait displays get the tall rendition when the album has a
            // dependable one.
            return .motion(motion.rendition(portrait: prefersTallArtwork), still: reduceMotion)
        }
        let mood = backgroundMood
        let seed = backgroundSeed
        if mode == .desktop { return .desktop }
        if mode == .custom, CustomWallpaperStore.isAvailable(settings.customWallpaperPath) {
            return .custom(URL(fileURLWithPath: settings.customWallpaperPath))
        }
        if mode == .visualizer { return .visualizer(settings.audioVisualizerStyle) }
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

    /// The background follows the last track until another one plays.
    private var backgroundMood: TrackMood { nowPlaying.current == nil ? heldMood ?? mood : mood }
    private var backgroundSeed: Int { nowPlaying.current == nil ? heldSeed ?? trackSeed : trackSeed }

    private var currentLayer: BackgroundLayer { crossfade.current }

    /// Whether `layer` has something to draw: anything but an effect still
    /// compiling on this surface's GPU. A crossfade into an effect that has
    /// never had anything to draw waits instead of fading towards an empty
    /// view (see `BackgroundCrossfade`).
    private func canDraw(_ layer: BackgroundLayer) -> Bool {
        guard case .shader(let style, _) = layer else { return true }
        return effects.isSettled(style, display: effectDisplay)
    }

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
            notFoundAt: lyricsVM.notFoundAt, noLyrics: settings.noLyrics, lyricsShown: extras.lyricsVisible),
            at: date, timing: .init(idleHold: Self.idleHoldDuration, noticeHold: Self.noticeHoldDuration,
                                    noticeFade: Self.noticeFadeDuration))
    }

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
        if crossfade.previous != nil { ends.append(crossfade.changedAt.addingTimeInterval(Self.backgroundFadeDuration)) }
        ends.append(paletteChangedAt.addingTimeInterval(Self.paletteFadeDuration))
        if nowPlaying.current == nil, let since = nowPlaying.idleSince {
            ends.append(since.addingTimeInterval(Self.idleHoldDuration + Self.noticeFadeDuration))
        }
        if lyricsVM.notFound, settings.noLyrics == .notice, let at = lyricsVM.notFoundAt {
            ends.append(at.addingTimeInterval(Self.noticeHoldDuration + Self.noticeFadeDuration))
        }
        let lyricsAdvance = nowPlaying.current != nil && nowPlaying.isPlaying
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
                let fade = crossfade.fade(at: Date(), duration: Self.backgroundFadeDuration)
                let outgoing = crossfade.outgoing(fade: fade)
                let layers = crossfade.layers(fade: fade)
                let phases = LayerPhases(colour: colourPhase, flow: flowPhase, pattern: patternPhase,
                                         visualizer: visualizerPhase, economical: economical)
                let stage = stagePresence(at: Date())

                WallpaperViewport(size: geometry.size) {
                  ZStack {
                    // Generated wallpapers sit on opaque black. Desktop
                    // overlay mode leaves the desktop showing, and the black
                    // fades with the layer moving to or from it.
                    let base = baseOpacity(outgoing: outgoing, fade: fade)
                    if base > 0 { Color.black.opacity(base) }
                    // Keyed by the layer itself, so the outgoing layer stays
                    // the same view -- and the same video or web page -- for
                    // the whole fade instead of being rebuilt in a new slot.
                    ForEach(layers, id: \.self) { layer in
                        crossfaded(layer, opacity: crossfade.opacity(of: layer, fade: fade),
                                   phases: phases, size: geometry.size, cover: cover, stage: stage)
                    }
                  }
                } lyrics: {
                    // The lyrics run on their own clock (see LyricStage), so
                    // this frame passes them by unless an input changed.
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
        .onChange(of: nowPlaying.artwork) { _, artwork in updatePalette(artwork) }
        .onChange(of: desiredLayer) { _, next in
            // Hold the old layer alive just long enough to fade it out. A
            // layer that never had anything to draw was never seen, so the
            // fade carries on from the one that is on screen.
            prepareEffect(for: next)
            crossfade.change(to: next, ready: canDraw(next), at: Date())
        }
        .onChange(of: canDraw(currentLayer)) { _, ready in
            // The effect is ready: start the fade into it now. Once shown, it
            // stays shown -- its surface keeps drawing it on the GPU it has
            // while the display's new GPU compiles it.
            if ready { crossfade.becameReady(at: Date()) }
        }
        .onChange(of: mood, initial: true) { _, mood in
            if nowPlaying.current != nil { heldMood = mood }
        }
        .onChange(of: trackSeed, initial: true) { _, seed in
            if nowPlaying.current != nil { heldSeed = seed }
        }
        .onChange(of: nowPlaying.current) { _, _ in renewShaderClock(.trackChange) }
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
            updatePalette(nowPlaying.artwork)
            let layer = desiredLayer
            prepareEffect(for: layer)
            crossfade = BackgroundCrossfade(layer, ready: canDraw(layer))
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

    /// Opaque black under generated wallpapers; none under the desktop
    /// overlay; fading with a crossfade to or from it.
    private func baseOpacity(outgoing: BackgroundLayer?, fade: Double) -> Double {
        if currentLayer == .desktop { return outgoing == nil ? 0 : 1 - fade }
        return outgoing == .desktop ? fade : 1
    }

    private func crossfaded(_ layer: BackgroundLayer, opacity: Double, phases: LayerPhases,
                            size: CGSize, cover: AlbumWallpaperPalette, stage: Double) -> some View {
        // `compositingGroup` flattens each layer with its own scrim before the
        // fade opacity is applied; without it the two scrims blend through
        // each other and the screen visibly lightens mid-transition. Videos,
        // web views and Metal views are included: their layers sit in one
        // group-opacity layer, so the fade applies to the whole layer rather
        // than to each part separately.
        backgroundLayer(layer, phases: phases, size: size, cover: cover, stage: stage)
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
                                    paused: renderingPaused || reduceMotion)
                if hasAlbumPalette { cover.gradient.opacity(0.18).ignoresSafeArea() }
                scrim
                textBacking(backing, presence: stage, size: size)
            }

        case .musicVideo(let source):
            ZStack {
                // Reduce Motion holds the video still, as it does an imported one.
                MusicVideoBackground(source: source, paused: renderingPaused || reduceMotion,
                                     dim: settings.musicVideoDim, blur: settings.musicVideoBlur,
                                     preview: isSettingsPreview, standIn: cover.gradient)
                scrim
                textBacking(backing, presence: stage, size: size)
            }

        case .motion(let url, let still):
            ZStack {
                cover.gradient.ignoresSafeArea()
                Group {
                    if still {
                        stillCover
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
                if hasAlbumPalette { cover.gradient.opacity(0.18).ignoresSafeArea() }
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

        case .visualizer(let style):
            ZStack {
                if let engine = style.webEngine {
                    let originalLight3D = style == .light3D && settings.light3DColours == .original
                    WebVisualizerView(engine: engine, preset: settings.wavefieldPreset, projection: settings.wavefieldProjection,
                        sensitivity: settings.audioSensitivity, motion: settings.effectSpeed, tick: phases.visualizer, paused: renderingPaused, preview: isSettingsPreview, palette: hasAlbumPalette && !originalLight3D ? cover : nil,
                        displayID: effectDisplay, preferences: visualizerPreferences)
                        .id(engine)
                        .ignoresSafeArea()
                        .saturation(originalLight3D ? 1 : settings.vividness)
                } else {
                    AudioVisualizerView(style: style, time: phases.visualizer, paused: renderingPaused, preview: isSettingsPreview,
                        palette: hasAlbumPalette ? cover : nil)
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
    /// Reduce Motion.
    @ViewBuilder
    private var stillCover: some View {
        if let artwork = nowPlaying.artwork {
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
        let hasLyrics = !lyricsVM.lines.isEmpty || !lyricsVM.plainLines.isEmpty
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
        case .musicVideo:
            // Footage is unmeasured; the dimming is laid over it before the scrim.
            luminance = LyricLegibility.unknownBackdropLuminance
            existing = 1 - (1 - min(1, max(0, settings.musicVideoDim))) * (1 - scrim)
        case .motion:
            luminance = LyricLegibility.coverLuminance(hsbGrid: palette)
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

    private func wallpaperPalette(at date: Date) -> AlbumWallpaperPalette {
        let progress = Easing.smoothstep(date.timeIntervalSince(paletteChangedAt) / 0.9)
        guard progress < 1, let previousWallpaperPalette else { return wallpaperPalette }
        return previousWallpaperPalette.interpolated(to: wallpaperPalette, fraction: progress)
    }

    private func applyPalette(_ extracted: [SIMD3<Double>], hasArtwork: Bool) {
        let now = Date()
        previousWallpaperPalette = wallpaperPalette(at: now)
        wallpaperPalette = AlbumWallpaperPalette(grid: extracted)
        paletteChangedAt = now
        hasAlbumPalette = hasArtwork
        palette = extracted
        DesktopWallpaperController.shared.currentPalette = extracted
    }

    private var albumAccent: SIMD3<Double> { ColorExtractor.accent(in: palette) }

    private var lyricAccent: Color {
        let hsb = albumAccent
        return Color(hue: hsb.x, saturation: min(0.65, max(0.2, hsb.y)), brightness: max(0.78, hsb.z))
    }

    private func updatePalette(_ artwork: NSImage?) {
        let token = UUID()
        paletteGeneration = token
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
