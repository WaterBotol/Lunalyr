import SwiftUI
import CoreGraphics

@MainActor
struct NowPlayingView: View {
    let displayID: CGDirectDisplayID
    @ObservedObject var renderActivity: WindowRenderActivity
    @EnvironmentObject private var nowPlaying: NowPlayingMonitor
    @ObservedObject private var lyricsVM = LyricsViewModel.shared
    @ObservedObject private var hover = HoverState.shared
    @ObservedObject private var settings = Settings.shared
    // Read the current envelope on a scheduled frame. Subscribing to every
    // meter publication would trigger extra wallpaper redraws outside it.
    @ObservedObject private var readingOptions = RomanizationOptions.shared
    @ObservedObject private var songOffsets = SongOffsets.shared
    @State private var paletteGeneration = UUID()
    @State private var palette: [SIMD3<Double>] = ColorExtractor.fallbackHSBPalette
    @State private var currentLayer: BackgroundLayer = .generated(pattern: nil)
    @State private var previousLayer: BackgroundLayer?
    @State private var layerChangedAt = Date.distantPast

    private static let backgroundFadeDuration: TimeInterval = 0.75
    /// How long the "no lyrics" notice stays up, and how long it takes to go.
    private static let noticeHoldDuration: TimeInterval = 4.0
    private static let noticeFadeDuration: TimeInterval = 1.0
    private static let idleHoldDuration: TimeInterval = 5.0

    private var renderingPaused: Bool {
        !renderActivity.shouldRender(pauseWhenCovered: settings.pauseWhenCovered)
    }

    /// What the background should be right now. Changing any input -- the
    /// album gaining motion art, a settings change, the track's mood -- makes
    /// this differ from `currentLayer` and starts a crossfade.
    enum BackgroundLayer: Equatable {
        case motion(URL)
        case custom(URL)
        case shader(ShaderStyle)
        case visualizer(AudioVisualizerStyle)
        /// `nil` pattern means colour field only.
        case generated(pattern: BackgroundStyle?)
    }

    private var desiredLayer: BackgroundLayer {
        if settings.effectiveBackgroundMode == .auto,
           let motion = nowPlaying.motionArtwork, settings.motionArtworkEnabled,
           !settings.excludesMotion(artist: nowPlaying.current?.artist,
                                    album: nowPlaying.current?.album) {
            return .motion(motion.square)
        }
        let mood = self.mood
        let mode = settings.effectiveBackgroundMode
        if mode == .custom, !settings.customWallpaperPath.isEmpty {
            return .custom(URL(fileURLWithPath: settings.customWallpaperPath))
        }
        if mode == .visualizer { return .visualizer(settings.audioVisualizerStyle) }
        if mode == .shader || (mode == .auto && mood.energy > 0.72) {
            return .shader(settings.resolvedShader(for: mood, seed: trackSeed))
        }
        if mode == .colourOnly { return .generated(pattern: nil) }
        return .generated(pattern: settings.resolvedPattern(for: mood))
    }

    private var trackSeed: Int {
        let key = (nowPlaying.current?.title ?? "") + (nowPlaying.current?.artist ?? "")
        var value: UInt64 = 1469598103934665603
        for byte in key.utf8 {
            value = (value ^ UInt64(byte)) &* 1099511628211
        }
        return Int(value % 100_000)
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

    var body: some View {
        let isHovered = hover.isActive(on: displayID)
        GeometryReader { geometry in
            let baseSize = min(max(geometry.size.width * 0.046, 38), 124)
            let mood = self.mood

            // One timeline drives the gradient, the pattern and the lyrics so
            // they share a frame clock instead of each running their own.
            TimelineView(.animation(minimumInterval: settings.effectiveAnimationRate.interval, paused: renderingPaused)) { timeline in
                let now = timeline.date.timeIntervalSinceReferenceDate
                let playbackTime = nowPlaying.playbackTime() + settings.lyricOffset + songOffsets.offset(for: lyricsVM.currentQuery)

                let speed = settings.effectSpeed
                // Kept steady: the shaders take this as `time`, where it drives
                // the slow breathing that must not lurch with the music.
                let colourPhase = VisualClock.gradient.advance(to: now, rate: speed)
                // Ambient wallpapers and lyric gestures follow their own
                // continuous clocks. Only the dedicated visualiser consumes
                // audio; no global deformation or beat overlay remains.
                let flowPhase = VisualClock.flow.advance(to: now, rate: speed * 0.70)
                let patternPhase = flowPhase * 0.5

                // Background switches are crossfaded from a timestamp
                // rather than a SwiftUI transition: this view is rebuilt
                // every frame by TimelineView, which discards transition
                // state, so a `.transition` here would simply cut.
                let fade = Easing.smoothstep(
                    Date().timeIntervalSince(layerChangedAt) / Self.backgroundFadeDuration
                )

                WallpaperViewport(size: geometry.size) {
                  ZStack {
                    // `compositingGroup` flattens each layer with its own
                    // scrim before the fade opacity is applied; without it
                    // the two scrims blend through each other and the screen
                    // visibly lightens mid-transition.
                    if let outgoing = previousLayer, fade < 1 {
                        backgroundLayer(
                            outgoing, colourPhase: colourPhase, patternPhase: patternPhase,
                            flowPhase: flowPhase, size: geometry.size
                        )
                        .compositingGroup()
                        .opacity(1 - fade)
                    }

                    backgroundLayer(
                        currentLayer, colourPhase: colourPhase, patternPhase: patternPhase,
                        flowPhase: flowPhase, size: geometry.size
                    )
                    .compositingGroup()
                    .opacity(previousLayer == nil ? 1 : fade)
                  }
                } lyrics: {
                    lyricsSection(
                        time: playbackTime,
                        baseSize: baseSize,
                        maxWidth: geometry.size.width * 0.80,
                        maxHeight: geometry.size.height * 0.72,
                        mood: mood
                    )
                    .padding(.horizontal, 40)
                    .frame(width: geometry.size.width, height: geometry.size.height)
                    .blur(radius: isHovered ? 22 : 0)
                    .opacity(isHovered ? 0.3 : 1)
                    .animation(.easeOut(duration: 0.3), value: isHovered)
                }
            }
        }
        .onChange(of: nowPlaying.artwork) { _, artwork in updatePalette(artwork) }
        .onChange(of: desiredLayer) { previous, next in
            // Hold the old layer alive just long enough to fade it out.
            previousLayer = previous
            currentLayer = next
            layerChangedAt = Date()
        }
        .onAppear {
            updatePalette(nowPlaying.artwork)
            currentLayer = desiredLayer
        }
    }

    /// Renders one background layer. Each carries its own scrim so the two
    /// sides of a crossfade stay legible throughout the blend.
    @ViewBuilder
    private func backgroundLayer(
        _ layer: BackgroundLayer, colourPhase: Double, patternPhase: Double,
        flowPhase: Double, size: CGSize
    ) -> some View {
        // The colour field drifted on a metronome and answered the music only
        // by leaning its blobs 5% wider -- effectively deaf, which made
        // "Colour only" the one mode that ignored what was playing. It now
        // orbits on the same clock as everything else, at a third the rate:
        // the blobs are large and slow, so they need far less of it than a
        // shader does to read as moving with the track.
        let colourFlow = flowPhase * 0.34
        switch layer {
        case .custom(let url):
            ZStack {
                CustomWallpaperView(url: url, placement: settings.customWallpaperPlacement, paused: renderingPaused)
                scrim
            }

        case .motion(let url):
            ZStack {
                // The album brought its own art direction, so nothing of ours
                // is drawn over it -- just enough softening and shade that
                // the words stay readable on a bright frame.
                MotionArtworkView(url: url, placement: settings.motionPlacement, isPaused: renderingPaused)
                    .ignoresSafeArea()
                    // Only softened enough to stop fine detail fighting the
                    // type -- the footage should still be recognisable.
                    .blur(radius: settings.motionBlur)
                    // A fixed overscan, purely to keep the blur from showing
                    // the frame edge. It used to breathe with the beat; that
                    // twitched the whole picture on every hit.
                    .scaleEffect(1.04)
                Color.black.opacity(0.40 * settings.scrimStrength).ignoresSafeArea()
                RadialGradient(
                    colors: [.black.opacity(0.5), .black.opacity(0.16), .clear],
                    center: .center,
                    startRadius: 0,
                    endRadius: max(size.width, size.height) * 0.55
                )
                .ignoresSafeArea()
            }

        case .shader(let style):
            ZStack {
                ShaderBackgroundView(
                    style: style,
                    time: colourPhase,
                    flow: flowPhase,
                    tintA: paletteColour(0),
                    tintB: paletteColour(4)
                )
                scrim
            }

        case .visualizer(let style):
            ZStack {
                AudioVisualizerView(style: style, time: flowPhase, paused: renderingPaused)
                    .ignoresSafeArea()
                scrim
            }

        case .generated(let pattern):
            ZStack {
                AnimatedGradientView(
                    palette: palette,
                    time: colourFlow
                ).saturation(settings.vividness)
                if let pattern {
                    FractalBackgroundView(
                        seed: trackSeed,
                        phase: patternPhase,
                        style: pattern
                    )
                    .opacity(settings.patternOpacity)
                }
                scrim
            }
        }
    }

    private var scrim: some View {
        let strength = settings.scrimStrength
        return LinearGradient(
            colors: [
                .black.opacity(0.34 * strength),
                .black.opacity(0.12 * strength),
                .black.opacity(0.50 * strength),
            ],
            startPoint: .top, endPoint: .bottom
        )
        .ignoresSafeArea()
    }

    private func paletteColour(_ index: Int) -> Color {
        guard palette.indices.contains(index) else { return .purple }
        let hsb = palette[index]
        return Color(
            hue: hsb.x,
            saturation: min(hsb.y * 1.2 * settings.vividness, 1),
            brightness: min(hsb.z * 1.15 + 0.1, 1)
        )
    }

    private var lyricAccent: Color {
        guard palette.indices.contains(2) else { return .mint }
        let hsb = palette[2]
        return Color(hue: hsb.x, saturation: min(0.65, max(0.2, hsb.y)), brightness: max(0.78, hsb.z))
    }

    private func updatePalette(_ artwork: NSImage?) {
        let token = UUID()
        paletteGeneration = token
        guard let artwork else {
            withAnimation(.easeInOut(duration: 0.9)) {
                palette = ColorExtractor.fallbackHSBPalette
            }
            return
        }
        Task.detached(priority: .userInitiated) {
            let extracted = ColorExtractor.extractPalette(from: artwork)
            await MainActor.run {
                guard self.paletteGeneration == token else { return }
                withAnimation(.easeInOut(duration: 0.9)) {
                    self.palette = extracted
                }
                DesktopWallpaperController.shared.currentPalette = extracted
            }
        }
    }

    @ViewBuilder
    private func lyricsSection(time: TimeInterval, baseSize: CGFloat, maxWidth: CGFloat, maxHeight: CGFloat, mood: TrackMood) -> some View {
        if nowPlaying.current == nil {
            // Say so, then leave the background to itself rather than
            // parking a notice on the desktop for hours.
            if let since = nowPlaying.idleSince {
                let age = Date().timeIntervalSince(since)
                let presence = 1 - Easing.smoothstep(
                    (age - Self.idleHoldDuration) / Self.noticeFadeDuration
                )
                if presence > 0.01 {
                    placeholder("Nothing playing", size: baseSize * 0.42)
                        .opacity(presence)
                }
            }
        } else if lyricsVM.isLoading && lyricsVM.lines.isEmpty && lyricsVM.plainLines.isEmpty {
            ProgressView().controlSize(.large).tint(.white)
        } else if !lyricsVM.plainLines.isEmpty {
            // No timed source has this track, so the words are shown as a
            // static block instead of nothing at all.
            plainLyrics(baseSize: baseSize, maxWidth: maxWidth, mood: mood)
                .modifier(FittedLyricStage(available: CGSize(width: maxWidth, height: maxHeight), userScale: CGFloat(settings.lyricScale)))
        } else if lyricsVM.notFound || lyricsVM.lines.isEmpty {
            // Say so briefly, then get out of the way -- an instrumental
            // should not carry a permanent error notice on the wallpaper.
            switch settings.noLyrics {
            case .nothing:
                Color.clear
            case .trackName:
                VStack(spacing: baseSize * 0.12) {
                    Text(nowPlaying.current?.title ?? "")
                        .font(settings.lyricFont(size: baseSize * 0.5, hero: true, mood: mood))
                    Text(nowPlaying.current?.artist ?? "")
                        .font(settings.uiFont(size: baseSize * 0.26, mood: mood))
                        .foregroundStyle(.white.opacity(0.6))
                }
                .foregroundStyle(.white.opacity(0.85))
                .shadow(color: .black.opacity(0.5), radius: 10, y: 2)
            case .notice:
                if let reportedAt = lyricsVM.notFoundAt {
                    let age = Date().timeIntervalSince(reportedAt)
                    let presence = 1 - Easing.smoothstep(
                        (age - Self.noticeHoldDuration) / Self.noticeFadeDuration
                    )
                    ZStack {
                        if presence > 0.01 {
                            placeholder("Lyrics unavailable", size: baseSize * 0.34).opacity(presence)
                        }
                        if settings.showInterludes {
                            let gap = VCVocalGap(start: time - max(0, age - Self.noticeHoldDuration),
                                                  end: nowPlaying.current?.duration ?? -1, nextVocal: -1)
                            InstrumentalInterludeView(time: time, gap: gap, baseSize: baseSize, accent: lyricAccent).opacity(1 - presence)
                        }
                    }
                }
            }
        } else {
            let gap = settings.showInterludes ? instrumentalGap(at: time) : nil
            ZStack {
                currentLine(time: time, baseSize: baseSize, maxWidth: maxWidth, maxHeight: maxHeight, mood: mood)
                    .opacity(1 - (gap?.presence(at: time) ?? 0))
                if settings.showInterludes, let gap {
                    InstrumentalInterludeView(time: time, gap: gap, baseSize: baseSize, accent: lyricAccent, nextWord: interludeNextWord(gap: gap, mood: mood))
                }
            }
        }
    }

    private func instrumentalGap(at time: Double) -> VCVocalGap? {
        var gap = VCVocalGap()
        let found = lyricsVM.vocalWindows.withUnsafeBufferPointer {
            vc_vocal_gap($0.baseAddress, Int32($0.count), time, nowPlaying.current?.duration ?? 0, 2.4, &gap)
        }
        return found != 0 ? gap : nil
    }

    private func interludeNextWord(gap: VCVocalGap, mood: TrackMood) -> String? {
        guard gap.nextVocal >= 0,
              let index = lyricsVM.activeLineIndex(at: gap.end + 0.02),
              lyricsVM.lines.indices.contains(index) else { return nil }
        let composition = settings.resolvedComposition(for: mood, seed: index)
        guard settings.previewsNextWord(in: composition) else { return nil }
        let source = readingOptions.display == .replace ? lyricsVM.romanized[index] ?? lyricsVM.lines[index].words : lyricsVM.lines[index].words
        return source.first(where: { $0.start >= gap.end - 0.02 && $0.text.contains(where: \.isLetter) })?.text
    }

    private func plainLyrics(baseSize: CGFloat, maxWidth: CGFloat, mood: TrackMood) -> some View {
        VStack(spacing: baseSize * 0.10) {
            ForEach(Array(lyricsVM.plainLines.prefix(14).enumerated()), id: \.offset) { _, line in
                Text(line)
                    .font(settings.lyricFont(size: baseSize * 0.26, hero: false, mood: mood))
                    .foregroundStyle(.white.opacity(line.isEmpty ? 0 : 0.7))
                    .multilineTextAlignment(.center)
                    .lineLimit(1)
            }
        }
        .frame(maxWidth: maxWidth)
        .shadow(color: .black.opacity(0.4), radius: 8, y: 2)
    }

    /// Outgoing ink can finish its exit after a line boundary. Rolling styles
    /// retain one word stage instead of drawing two overlapping timelines.
    @ViewBuilder
    private func currentLine(time: TimeInterval, baseSize: CGFloat, maxWidth: CGFloat, maxHeight: CGFloat, mood: TrackMood) -> some View {
        let index = lyricsVM.activeLineIndex(at: time) ?? 0
        let composition = settings.resolvedComposition(for: mood, seed: index)
        ZStack {
            if !composition.arrivalOnly, index > 0,
               !settings.resolvedComposition(for: mood, seed: index - 1).arrivalOnly {
                renderLyricLine(index: index - 1, time: time, baseSize: baseSize,
                    maxWidth: maxWidth, maxHeight: maxHeight, mood: mood)
            }
            renderLyricLine(index: index, time: time, baseSize: baseSize,
                maxWidth: maxWidth, maxHeight: maxHeight, mood: mood)
        }
    }

    @ViewBuilder
    private func renderLyricLine(index: Int, time: TimeInterval, baseSize: CGFloat, maxWidth: CGFloat, maxHeight: CGFloat,
                                 mood: TrackMood) -> some View {
        let lines = lyricsVM.lines

        if lines.indices.contains(index), !lines[index].words.isEmpty, time >= lines[index].start - 1 {
            // The fade is computed from playback time rather than a SwiftUI
            // transition: this view is rebuilt every frame by TimelineView,
            // which discards transition state and made line changes snap.
            let line = lines[index]
            let appear = Easing.smoothstep((time - line.start) / 0.22)
            let nextStart = index + 1 < lines.count ? lines[index + 1].start : line.end + 0.8
            let exit = Easing.smoothstep((time - min(line.end + 0.12, nextStart - 0.08)) / 0.46)
            let presence = appear * (1 - exit)

            let composition = line.wordTimed ? settings.resolvedComposition(for: mood, seed: index) : .editorial
            let density = line.words.reduce(0) { $0 + $1.text.count + 3 }
            let densityScale = composition.hasConnections
                ? min(1.0, max(0.52, sqrt(110.0 / Double(max(density, 110))))) : 1.0
            let readings = lyricsVM.romanized[index]
            let displayed = line.wordTimed
                ? (composition.arrivalOnly ? rollingWords(around: index, time: time)
                   : (readingOptions.display == .replace ? readings ?? line.words : line.words))
                : [LyricsFetcher.Word(text: readingOptions.display == .replace ? readings?.map(\.text).joined(separator: " ") ?? line.text : line.text, start: line.start, end: line.end)]
            let stagePresence = composition.arrivalOnly ? 1 : presence
            if stagePresence > 0.001 {
              VStack(spacing: baseSize * 0.28) {
                if !line.wordTimed {
                    Text(displayed.map(\.text).joined(separator: " "))
                        .font(settings.lyricFont(size: baseSize * 0.72, hero: false, mood: mood))
                        .foregroundStyle(.white).multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                LyricLineView(
                    words: displayed,
                    time: time,
                    baseSize: baseSize * densityScale,
                    mood: mood,
                    composition: composition,
                    seed: composition.arrivalOnly ? trackSeed : index,
                    presence: stagePresence,
                    accent: lyricAccent
                )
                }
                if readingOptions.display == .below, let readings {
                    if line.wordTimed {
                        readingLine(readings, time: time, baseSize: baseSize, composition: composition)
                    } else {
                        Text(readings.map(\.text).joined(separator: " "))
                            .font(settings.uiFont(size: max(13, baseSize * 0.3), mood: mood))
                            .foregroundStyle(.white.opacity(0.72))
                            .multilineTextAlignment(.center)
                    }
                }
            }
            .modifier(FittedLyricStage(available: CGSize(width: maxWidth, height: maxHeight), userScale: CGFloat(settings.lyricScale)))
            .frame(maxWidth: maxWidth)
            .opacity(stagePresence)
              .scaleEffect(composition.arrivalOnly ? 1 : 0.96 + 0.04 * appear - 0.025 * exit)
              .offset(y: composition.arrivalOnly ? 0 : CGFloat((1 - appear) * 0.22 - exit * 0.36) * baseSize)
              .blur(radius: composition.arrivalOnly ? 0 : exit * 3.5)
            }
        } else {
            Color.clear
        }
    }

    private func rollingWords(around index: Int, time: Double) -> [LyricsFetcher.Word] {
        let lines = lyricsVM.lines
        var result: [LyricsFetcher.Word] = [], seen = Set<TimedWordKey>()
        for row in max(0, index - 2)...min(lines.count - 1, index + 1) {
            guard lines[row].wordTimed else { continue }
            let words = readingOptions.display == .replace ? lyricsVM.romanized[row] ?? lines[row].words : lines[row].words
            for word in words where word.end >= time - 12 && (row <= index || word.start <= time + 1.2)
                && word.text.rangeOfCharacter(from: .alphanumerics) != nil {
                if seen.insert(word.presentationID).inserted { result.append(word) }
            }
        }
        return result.sorted { $0.start < $1.start }
    }

    private func readingLine(_ words: [LyricsFetcher.Word], time: TimeInterval,
                             baseSize: CGFloat, composition: LyricComposition) -> some View {
        let next = words.firstIndex { $0.start > time }
        return FlowLayout(spacing: baseSize * 0.07, lineSpacing: 5, rowAlignment: .center) {
            ForEach(Array(words.enumerated()), id: \.offset) { index, word in
                let state = WordPresentation.make(word: word, index: index, nextIndex: next, time: time, composition: composition)
                Text(word.text)
                    .font(settings.uiFont(size: max(13, baseSize * 0.24), mood: mood))
                    .foregroundStyle(.white.opacity(0.72))
                    .modifier(WordEntrance(progress: state.progress, style: settings.arrival(in: composition).resolved(for: composition),
                                           size: baseSize * 0.24, direction: index.isMultiple(of: 2) ? 1 : -1, visibility: state.opacity))
            }
        }
        .shadow(color: .black.opacity(0.5), radius: 6, y: 2)
    }

    private func placeholder(_ text: String, size: CGFloat) -> some View {
        Text(text)
            .font(settings.uiFont(size: size, mood: mood))
            .foregroundStyle(.white.opacity(0.5))
    }
}
