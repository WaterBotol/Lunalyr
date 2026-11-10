import AppKit
import SwiftUI

/// Everything the wallpaper draws above its background: the synced line and
/// its neighbours, unsynced or missing-lyrics states and instrumental cues.
///
/// Kept apart from `NowPlayingView` so lyric rendering has one owner and the
/// background code does not have to be read to follow a lyric change.
///
/// Nothing here cuts. A seek, a new song, lyrics arriving or a change of
/// lyric source fades the previous scene out (still moving, at its own
/// time) while the new one fades in.
@MainActor
struct LyricLayer: View {
    let time: TimeInterval
    let baseSize: CGFloat
    let maxWidth: CGFloat
    let maxHeight: CGFloat
    let mood: TrackMood
    let accent: Color
    let trackSeed: Int
    /// Wall-clock seconds for this frame. Cross-fades run on it, so they
    /// finish even while playback is paused.
    var wall: TimeInterval = Date().timeIntervalSinceReferenceDate
    /// The album colours behind the lyrics (sRGB), for text colour and its
    /// contrast against the wallpaper.
    var palette: [SIMD3<Float>] = []
    /// The luminance the wallpaper measured behind the stage, when it shows
    /// a picture (Your wallpaper, an imported image or video).
    var measuredBackdrop: Double?
    /// False while the wallpaper is paused: no further frames will come, so
    /// fades jump to where they are heading instead of stopping part-way.
    var animating = true
    /// Told how much motion this frame has, so the stage can slow its clock
    /// while nothing moves.
    var pace: LyricPace?
    @EnvironmentObject private var nowPlaying: NowPlayingMonitor
    @ObservedObject private var lyricsVM = LyricsViewModel.shared
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var readingOptions = RomanizationOptions.shared
    @ObservedObject private var colours = LyricColourOptions.shared
    @ObservedObject private var highlights = LyricHighlightOptions.shared
    @ObservedObject private var extras = LunalyrExtras.shared
    @ObservedObject private var memory = StyleMemory.shared
    @StateObject private var scenes = LyricSceneMemory()
    @StateObject private var cache = LyricLayerCache()
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.colorSchemeContrast) private var contrast

    /// How long the "no lyrics" notice stays up, and how long it takes to go.
    private static let noticeHoldDuration: TimeInterval = 4.0
    private static let noticeFadeDuration: TimeInterval = 1.0
    private static let idleHoldDuration: TimeInterval = 5.0
    static let noticeTiming = LyricStagePresence.Timing(idleHold: idleHoldDuration, noticeHold: noticeHoldDuration,
                                                        noticeFade: noticeFadeDuration)

    private var sceneKey: LyricSceneKey {
        let kind: LyricSceneKey.Kind
        if nowPlaying.current == nil { kind = .idle }
        else if lyricsVM.isLoading && lyricsVM.lines.isEmpty && lyricsVM.plainLines.isEmpty { kind = .loading }
        else if !lyricsVM.plainLines.isEmpty { kind = .plain }
        else if lyricsVM.notFound || lyricsVM.lines.isEmpty { kind = .missing }
        else { kind = .synced }
        var track = Hasher()
        track.combine(nowPlaying.current?.title)
        track.combine(nowPlaying.current?.artist)
        return LyricSceneKey(kind: kind, track: track.finalize(),
            fingerprint: LyricSceneKey.fingerprint(lyricsVM.lines, plain: lyricsVM.plainLines))
    }

    private var sceneContent: LyricSceneContent {
        LyricSceneContent(lines: lyricsVM.lines, romanized: lyricsVM.romanized,
            machineTranslations: lyricsVM.machineTranslations, plainLines: lyricsVM.plainLines,
            title: nowPlaying.current?.title ?? "", artist: nowPlaying.current?.artist ?? "",
            notFoundAt: lyricsVM.notFoundAt, idleSince: nowPlaying.idleSince,
            duration: nowPlaying.current?.duration ?? 0)
    }

    /// The player macOS will not let Lunalyr read, while that is why the
    /// wallpaper shows nothing playing.
    private var refusedPlayer: PlayerKind? { nowPlaying.deniedPlayers.first }

    /// When the idle notice went up: when playback stopped, or when a
    /// running player turned out to be refused -- which, at launch, leaves
    /// no stop to count from.
    private func idleNoticeSince(_ idleSince: Date?) -> Date? {
        let refused = cache.refusedSince(refusedPlayer, now: Date())
        return [idleSince, refused].compactMap { $0 }.max()
    }

    /// The text colours for this frame, eased and made readable.
    private func ink(energy: Double, wall: Double) -> LyricInk {
        let audio = AudioReactiveController.shared
        let active = lyricsVM.activeLineIndex(at: time).flatMap { lyricsVM.lines.indices.contains($0) ? $0 : nil }
        let mode = settings.effectiveBackgroundMode
        let chosenColour = memory.remembered(for: nowPlaying.current)?.style.colourMode ?? colours.mode
        // Without any timestamps there is nothing to sweep along. Keep the
        // text coloured with an ambient drift until a timed source is available.
        let colour: LyricColourMode = chosenColour == .sungSweep && !lyricsVM.plainLines.isEmpty ? .albumDrift : chosenColour
        // The same backdrop the wallpaper's text backing is worked out for:
        // the picture's measured luminance, the album tint and scrim laid
        // over it, and that backing.
        let backdrop = LyricBackdrop.luminance(mode: mode,
            motion: mode == .auto && settings.motionArtworkEnabled && nowPlaying.motionArtwork != nil,
            palette: palette, scrim: settings.scrimStrength, measured: measuredBackdrop,
            backing: DesktopOptions.shared.manualTextBacking,
            target: LyricLegibility.targetContrast(baseSize: baseSize), tinted: nowPlaying.artwork != nil,
            pastelText: colour != .standard, increaseContrast: contrast == .increased)
        return cache.ink(.init(mode: colour, speed: colours.speed, strength: colours.strength, custom: colours.customColours,
            album: palette, backdrop: backdrop, audio: audio.running ? audio.levels : nil,
            vocal: active.map { OrganicMotion.vocalIntensity(lyricsVM.lines[$0].words, at: time) } ?? energy,
            reduceMotion: reduceMotion, increaseContrast: contrast == .increased, accent: cache.oklab(accent),
            highlight: highlights.mode, highlightCustom: highlights.customColour),
            wall: wall)
    }

    var body: some View {
        // A resting clock's timeline hands a rebuild the date of its last
        // tick; anything this frame starts must start from now.
        let wall = pace?.wall(self.wall) ?? self.wall
        let key = sceneKey
        let transition = scenes.observe(key: key, content: sceneContent, time: time, wall: wall,
                                        jump: LyricClock.shared.lastJump)
        let energy = cache.liveEnergy(wall: wall)
        let ink = ink(energy: energy, wall: wall)
        let shown = animating ? extras.visibility.presence(at: wall) : (extras.lyricsVisible ? 1 : 0)
        let _ = pace?.report(level(key: key, transition: transition, shown: shown,
                                   target: animating ? extras.visibility.target : shown, ink: ink, wall: wall))
        ZStack {
            if shown > 0.001 {
                ZStack {
                    ForEach(transition.leaving, id: \.scene.id) { leaving in
                        if leaving.presence > 0.001 {
                            // A song change dissolves softly; a seek or a
                            // source upgrade redraws the same place and
                            // only fades.
                            let dissolve = reduceMotion || !leaving.scene.blurs ? 0 : 1 - leaving.presence
                            scene(leaving.scene.content, key: leaving.scene.key,
                                  time: leaving.scene.renderTime(at: wall), live: false, energy: energy, ink: ink)
                                .opacity(leaving.presence)
                                .blur(radius: dissolve * 5)
                                .scaleEffect(1 - 0.012 * dissolve)
                        }
                    }
                    scene(sceneContent, key: key, time: time, live: true, energy: energy, ink: ink)
                        .opacity(transition.incomingPresence)
                }
                // Hiding the lyrics dissolves them softly rather than cutting.
                .opacity(shown)
                .blur(radius: reduceMotion ? 0 : (1 - shown) * 9)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .overlay(alignment: .bottom) { noteView(wall: wall) }
        .environment(\.lyricInk, ink)
    }

    /// How much moves on this frame: anything in its entrance, sweep, swell,
    /// release or exit needs every frame; a break with only the slow float,
    /// drifting colour or wandering dots needs far fewer; a stage with
    /// nothing left to change needs none (`LyricPace.quietLevel`).
    private func level(key: LyricSceneKey, transition: LyricSceneMemory.Transition, shown: Double,
                       target: Double, ink: LyricInk, wall: Double) -> LyricPace.Level {
        if let note = extras.note, wall - note.at < 2.7 { return .active }
        if shown > 0.001, !transition.leaving.isEmpty || transition.incomingPresence < 1 { return .active }
        let drifting = ink.mode.drifts || ink.mode == .musicReactive || !ink.settled
        let noticeAge: TimeInterval?
        switch key.kind {
        case .idle: noticeAge = idleNoticeSince(nowPlaying.idleSince).map { Date().timeIntervalSince($0) }
        case .missing: noticeAge = lyricsVM.notFoundAt.map { Date().timeIntervalSince($0) }
        default: noticeAge = nil
        }
        if let quiet = LyricPace.quietLevel(.init(kind: key.kind, shown: shown, target: target,
                                                  playing: nowPlaying.isPlaying, colourMoves: drifting,
                                                  noLyrics: settings.noLyrics, noticeAge: noticeAge),
                                            timing: Self.noticeTiming) {
            return quiet
        }
        switch key.kind {
        case .idle, .missing, .loading, .plain:
            return .active
        case .synced:
            let lines = lyricsVM.lines
            let index = lyricsVM.activeLineIndex(at: time) ?? 0
            let lead = max(0.9, settings.nextWordPreviewLead + 0.15)
            for row in max(0, index - 2)...min(lines.count - 1, index + 1) where lines.indices.contains(row) {
                let line = lines[row]
                if time >= line.start - lead, time <= line.start + 0.8 { return .active }
                // Where the renderer starts the exit, which for most layouts
                // is earlier than the phrase's: measured from the phrase's,
                // those exits played at 12 or 24 fps.
                let exit = renderedExitStart(row, in: lines)
                if time >= exit - 0.2, time <= exit + LyricPhraseTiming.exitSpan(for: composition(at: row)) + 0.1 {
                    return .active
                }
                for word in line.words where time >= word.start - lead && time <= word.end + 1.2 {
                    return .active
                }
            }
            if settings.showInterludes && instrumentalGap(at: time) != nil { return .ambient }
            return drifting ? .ambient : .still
        }
    }

    /// A brief confirmation of what a shortcut just did: rises in, holds,
    /// fades. Drawn whether or not the lyrics are showing.
    @ViewBuilder
    private func noteView(wall: Double) -> some View {
        if animating, let note = extras.note {
            let age = wall - note.at
            let shown = vc_settle(age, 0.32) * (1 - vc_settle(age - 1.9, 0.55))
            if age >= 0, age < 2.6, shown > 0.001 {
                Label(note.text, systemImage: note.symbol)
                    .font(settings.uiFont(size: max(13, baseSize * 0.26), bold: true, mood: mood))
                    .foregroundStyle(.white)
                    .padding(.horizontal, baseSize * 0.34)
                    .padding(.vertical, baseSize * 0.16)
                    .background(Capsule().fill(.black.opacity(0.42)))
                    .shadow(color: .black.opacity(0.3), radius: 10, y: 3)
                    .opacity(shown)
                    .offset(y: reduceMotion ? 0 : (1 - shown) * baseSize * 0.25)
                    .padding(.bottom, max(24, maxHeight * 0.09))
                    .accessibilityLabel(note.text)
            }
        }
    }

    /// One scene: whatever the lyric layer shows for a given state.
    @ViewBuilder
    private func scene(_ content: LyricSceneContent, key: LyricSceneKey, time: TimeInterval,
                       live: Bool, energy: Double, ink: LyricInk) -> some View {
        let fallback = ink.base
        switch key.kind {
        case .idle:
            // Say so, then leave the background to itself rather than
            // parking a notice on the desktop for hours. When macOS refuses
            // Lunalyr the player, say that instead: the music may well be
            // playing. The menu bar and Settings keep saying it after this
            // has gone.
            if let since = idleNoticeSince(content.idleSince) {
                let age = Date().timeIntervalSince(since)
                let presence = (1 - Easing.smoothstep((age - Self.idleHoldDuration) / Self.noticeFadeDuration))
                    * vc_settle(age, 0.6)
                if presence > 0.01 {
                    if let refused = refusedPlayer {
                        VStack(spacing: baseSize * 0.14) {
                            placeholder(LyricLayer.refusedTitle(refused), size: baseSize * 0.42, colour: fallback.rest)
                            placeholder(LyricLayer.refusedDetail, size: baseSize * 0.24, colour: fallback.rest)
                        }
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: maxWidth)
                        .opacity(presence)
                    } else {
                        placeholder("Nothing playing", size: baseSize * 0.42, colour: fallback.rest)
                            .opacity(presence)
                    }
                }
            }
        case .loading:
            ProgressView().controlSize(.large).tint(.white)
        case .plain:
            // No timed source has this track, so the words are shown as a
            // static block instead of nothing at all.
            plainLyrics(content.plainLines, baseSize: baseSize, maxWidth: maxWidth, mood: mood, colour: fallback.rest,
                        progress: PlainLyricsScroll.progress(time: time, duration: content.duration,
                                                             reduceMotion: reduceMotion))
        case .missing:
            // Say so briefly, then get out of the way -- an instrumental
            // should not carry a permanent error notice on the wallpaper.
            switch settings.noLyrics {
            case .nothing:
                Color.clear
            case .trackName:
                VStack(spacing: baseSize * 0.12) {
                    Text(content.title)
                        .font(settings.lyricFont(size: baseSize * 0.5, hero: true, mood: mood))
                    Text(content.artist)
                        .font(settings.uiFont(size: baseSize * 0.26, mood: mood))
                        .foregroundStyle(fallback.rest.opacity(0.6))
                }
                .foregroundStyle(fallback.rest.opacity(0.85))
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .modifier(FittedLyricStage(available: CGSize(width: maxWidth, height: maxHeight),
                    userScale: CGFloat(settings.lyricScale)))
                .frame(maxWidth: maxWidth)
                .shadow(color: .black.opacity(0.5), radius: 10, y: 2)
            case .notice:
                if let reportedAt = content.notFoundAt {
                    let age = Date().timeIntervalSince(reportedAt)
                    let presence = 1 - Easing.smoothstep(
                        (age - Self.noticeHoldDuration) / Self.noticeFadeDuration
                    )
                    if presence > 0.01 {
                        placeholder("Lyrics unavailable", size: baseSize * 0.34, colour: fallback.rest).opacity(presence)
                    }
                }
            }
        case .synced:
            let lines = SceneLines(lines: content.lines, romanized: content.romanized, fingerprint: key.fingerprint,
                                   machineTranslations: content.machineTranslations)
            let gap = live && settings.showInterludes ? instrumentalGap(at: time) : nil
            currentLine(lines, time: time, live: live, energy: energy,
                lyricOpacity: 1 - (gap?.presence(at: time) ?? 0))
                .overlay {
                    creditsOverlay(lines, time: time)
                }
                .overlay {
                    if let cue = gap?.instrumentalCue(at: time, previewLead: settings.nextWordPreviewLead, energy: energy) {
                        InstrumentalInterludeView(cue: cue, accent: accent, baseSize: baseSize)
                    }
                }
        }
    }

    @ViewBuilder
    private func creditsOverlay(_ scene: SceneLines, time: Double) -> some View {
        if LyricCreditCues.isOn {
            let cues = LyricCreditCues.cues(in: scene.lines, title: nowPlaying.current?.title ?? "",
                                            artist: nowPlaying.current?.artist ?? "")
            if !cues.isEmpty {
                let first = scene.lines.firstIndex { !$0.isInstrumental } ?? 0
                CreditsLayer(cues: cues, time: time, stageSize: CGSize(width: maxWidth, height: maxHeight),
                             baseSize: baseSize, seed: trackSeed,
                             composition: composition(at: first), mood: mood)
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

    /// Unsynced lyrics, all of them: the block scrolls slowly through the
    /// song in step with its progress, with soft edges, instead of stopping
    /// after fourteen lines.
    /// With Reduce Motion (`progress` nil) the whole block is fitted to the
    /// stage and stays still.
    @ViewBuilder
    private func plainLyrics(_ plainLines: [String], baseSize: CGFloat, maxWidth: CGFloat, mood: TrackMood,
                             colour: Color, progress: Double?) -> some View {
        if let progress {
            scrollingPlainLyrics(plainLines, baseSize: baseSize, maxWidth: maxWidth, mood: mood, colour: colour,
                                 progress: progress)
        } else {
            plainBlock(plainLines, size: baseSize * CGFloat(min(1.5, max(0.7, settings.lyricScale))) * 0.3,
                       maxWidth: maxWidth, mood: mood, colour: colour)
                .modifier(FittedLyricStage(available: CGSize(width: maxWidth, height: maxHeight), userScale: 1, neutral: 1))
                .shadow(color: .black.opacity(0.4), radius: 8, y: 2)
        }
    }

    private func plainBlock(_ plainLines: [String], size: CGFloat, maxWidth: CGFloat, mood: TrackMood,
                            colour: Color) -> some View {
        VStack(spacing: size * 0.42) {
            ForEach(Array(plainLines.enumerated()), id: \.offset) { _, line in
                Text(line)
                    .font(settings.lyricFont(size: size, hero: false, mood: mood))
                    .foregroundStyle(colour.opacity(line.isEmpty ? 0 : 0.78))
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: maxWidth)
    }

    private func scrollingPlainLyrics(_ plainLines: [String], baseSize: CGFloat, maxWidth: CGFloat, mood: TrackMood,
                                      colour: Color, progress: Double) -> some View {
        let visible = maxHeight * 0.92
        let size = baseSize * CGFloat(min(1.5, max(0.7, settings.lyricScale))) * 0.3
        return plainBlock(plainLines, size: size, maxWidth: maxWidth, mood: mood, colour: colour)
        .visualEffect { block, geometry in
            // A short block sits centred; a long one scrolls through.
            let overflow = geometry.size.height - visible
            return block.offset(y: overflow <= 0 ? -overflow / 2 : -CGFloat(progress) * overflow)
        }
        .frame(height: visible, alignment: .top)
        .clipped()
        .mask {
            LinearGradient(stops: [.init(color: .clear, location: 0), .init(color: .black, location: 0.1),
                                   .init(color: .black, location: 0.9), .init(color: .clear, location: 1)],
                           startPoint: .top, endPoint: .bottom)
        }
        .shadow(color: .black.opacity(0.4), radius: 8, y: 2)
    }

    /// The lines one scene draws from.
    struct SceneLines {
        let lines: [LyricsFetcher.Line]
        let romanized: [Int: [LyricsFetcher.Word]]
        let fingerprint: Int
        /// Translations made on this Mac; a line's own translation wins.
        var machineTranslations: [Int: String] = [:]
    }

    /// Outgoing ink finishes its exit after a line boundary: every word of
    /// the previous line (and, after a very short line, the one before) is
    /// drawn until its staggered exit completes. Rolling styles retain one
    /// word stage instead of drawing two overlapping timelines.
    @ViewBuilder
    private func currentLine(_ scene: SceneLines, time: TimeInterval, live: Bool, energy: Double,
                             lyricOpacity: Double) -> some View {
        let lines = scene.lines
        let activeIndex = live ? lyricsVM.activeLineIndex(at: time)
            : cache.lineIndex(for: lines, fingerprint: scene.fingerprint).line(at: time)
        let index = activeIndex ?? 0
        let stageSize = CGSize(width: maxWidth, height: maxHeight)
        LyricLineStack(stageSize: stageSize, gap: baseSize * 0.3) {
            earlierLine(index - 2, current: index, scene: scene, time: time, stageSize: stageSize, energy: energy,
                        live: live, lyricOpacity: lyricOpacity)
            earlierLine(index - 1, current: index, scene: scene, time: time, stageSize: stageSize, energy: energy,
                        live: live, lyricOpacity: lyricOpacity)
            let next = live && activeIndex != nil ? nextLinePreview(after: index, in: scene, time: time) : nil
            let since = previewAllowed(after: index, in: lines)
            renderLyricLine(index: index, scene: scene, time: time, stageSize: stageSize, energy: energy, live: live,
                yield: next.map { previewLevel(of: lines[$0], time: time, since: since) } ?? 0)
                .opacity(lyricOpacity)
            if let next {
                renderLyricLine(index: next, scene: scene, time: time, stageSize: stageSize, energy: energy,
                    live: live, previewing: true, previewSince: since)
            }
        }
    }

    /// An earlier line still being sung or finishing its exit. A held one
    /// (`heldShift`) moves aside above the lines in the middle of the stage
    /// (`LyricLineStack`); with Reduce Motion it does not travel there but
    /// fades out where it was as it fades in where it goes.
    @ViewBuilder
    private func earlierLine(_ row: Int, current: Int, scene: SceneLines, time: TimeInterval, stageSize: CGSize,
                             energy: Double, live: Bool, lyricOpacity: Double) -> some View {
        if leaving(row, current: current, in: scene.lines, time: time) {
            let shift = heldShift(row, in: scene.lines, time: time)
            let crossFades = reduceMotion && (shift ?? 0) > 0 && (shift ?? 0) < 1
            let travel = shift.map { reduceMotion ? ($0 >= 1 ? 1 : 0) : $0 }
            let yield = carriedYield(row, scene: scene, time: time, live: live)
            renderLyricLine(index: row, scene: scene, time: time, stageSize: stageSize,
                energy: energy, live: live, outgoing: true, yield: yield)
                .modifier(HeldLineShift(travel: travel))
                .opacity(lyricOpacity * (crossFades ? 1 - (shift ?? 0) : 1))
            if crossFades, let shift {
                renderLyricLine(index: row, scene: scene, time: time, stageSize: stageSize,
                    energy: energy, live: live, outgoing: true, yield: yield)
                    .modifier(HeldLineShift(travel: 1))
                    .opacity(lyricOpacity * shift)
            }
        }
    }

    /// The dimming a line-timed line had reached when the next line's
    /// preview handed over to that line, kept while it leaves: dropped at
    /// the onset, the leaving line brightened from about a quarter to half
    /// strength just as the new line appeared.
    private func carriedYield(_ row: Int, scene: SceneLines, time: Double, live: Bool) -> Double {
        let lines = scene.lines
        guard live, lines.indices.contains(row + 1), !lines[row].wordTimed else { return 0 }
        let handover = lines[row + 1].start.nextDown
        guard time > handover,
              nextLinePreview(after: row, in: scene, time: handover) == row + 1 else { return 0 }
        return previewLevel(of: lines[row + 1], time: handover, since: previewAllowed(after: row, in: lines))
    }

    /// 0...1: how far the next line's preview has ramped in.
    private func previewLevel(of line: LyricsFetcher.Line, time: Double, since: Double) -> Double {
        guard let onset = LyricPhraseTiming.firstWordOnset(of: line) else { return 0 }
        return vc_word_visibility(time, onset, onset + 1, 1, 1, settings.nextWordPreviewLead) / 0.25
            * Easing.smoothstep((time - since) / 0.16)
    }

    /// Whether an earlier line is still being sung or finishing its exit.
    private func leaving(_ line: Int, current: Int, in lines: [LyricsFetcher.Line], time: Double) -> Bool {
        Self.isLeaving(line, current: current, lines: lines, time: time,
                       span: { LyricPhraseTiming.exitSpan(for: composition(at: $0)) }) {
            lines[$0].wordTimed && composition(at: $0).arrivalOnly
        }
    }

    /// `leaving`, given which lines roll (`rolls`) and how long each takes
    /// to leave (`span`).
    static func isLeaving(_ line: Int, current: Int, lines: [LyricsFetcher.Line], time: Double,
                          span: (Int) -> Double = { _ in LyricPhraseTiming.exitSpan },
                          rolls: (Int) -> Bool) -> Bool {
        guard line >= 0, line < current, lines.indices.contains(current) else { return false }
        // An earlier renderer cannot reappear after a short intervening line.
        guard !changesTiming(lines[line], lines[current]),
              !(line..<current).contains(where: { changesTiming(lines[$0], lines[$0 + 1]) }) else { return false }
        let handOver = Self.handOver(after: line, in: lines, rolls: rolls)
        guard LyricPhraseTiming.stillLeaving(lines[line], nextStart: handOver, time: time, span: span(line)) else {
            return false
        }
        // A rolling line followed by another rolling line has no exit of its
        // own: its words carry on in that line's stage and fade with it.
        if rolls(line) && rolls(line + 1) { return false }
        return !(rolls(current) && rolls(line))
    }

    private static func changesTiming(_ from: LyricsFetcher.Line, _ to: LyricsFetcher.Line) -> Bool {
        !from.isInstrumental && !to.isInstrumental && from.wordTimed != to.wordTimed
    }

    /// Ease the whole outgoing stage away before its replacement's onset.
    /// Both styles can then enter at their own timestamps without sharing the centre.
    static func timingHandoffPresence(after row: Int, in lines: [LyricsFetcher.Line], time: Double) -> Double {
        guard lines.indices.contains(row + 1), changesTiming(lines[row], lines[row + 1]) else { return 1 }
        let duration = 0.18
        return 1 - Easing.smoothstep((time - (lines[row + 1].start - duration)) / duration)
    }

    /// A rolling stage's presence. It carries on at full strength into the
    /// next rolling line; otherwise -- and always once it is an outgoing
    /// line -- it fades on its own exit, so the value is the same before and
    /// after the hand-over and an older stage can never reappear.
    static func rollingPresence(nextRolls: Bool, outgoing: Bool, exit: Double) -> Double {
        nextRolls && !outgoing ? 1 : 1 - exit
    }

    /// When line `row` hands the stage on (`LyricPhraseTiming.handOver`).
    /// A rolling stage carries its words on into the next line by itself,
    /// so it hands over as the next line starts, as before.
    private func handOver(after row: Int, in lines: [LyricsFetcher.Line]) -> Double {
        Self.handOver(after: row, in: lines) { lines[$0].wordTimed && composition(at: $0).arrivalOnly }
    }

    /// `handOver`, given which lines roll (`rolls`).
    static func handOver(after row: Int, in lines: [LyricsFetcher.Line], rolls: (Int) -> Bool) -> Double {
        if lines.indices.contains(row + 1), rolls(row) { return lines[row + 1].start }
        return LyricPhraseTiming.handOver(after: row, in: lines)
    }

    /// 0...1: how far a held line -- a duet part or ad-lib still being sung
    /// after the next line started -- has moved aside for that line, from
    /// that line's start; nil for a line that is not held. Stage-filling
    /// layouts set every line in the middle of the stage, so the held line
    /// shrinks and rises above the lines there rather than sit on top of
    /// them -- for as long as it is drawn, however many lines start under
    /// it. Rolling stages already stack their words.
    private func heldShift(_ row: Int, in lines: [LyricsFetcher.Line], time: Double) -> Double? {
        guard LyricPhraseTiming.isHeld(row, in: lines), !composition(at: row).arrivalOnly, !composition(at: row).fillsStage else { return nil }
        return vc_settle(time - lines[row + 1].start, 0.45)
    }

    /// When line `row` starts to leave as drawn (`renderedExitStart(_:in:handOver:rolls:fillsStage:)`).
    private func renderedExitStart(_ row: Int, in lines: [LyricsFetcher.Line]) -> Double {
        let style = composition(at: row)
        return Self.renderedExitStart(row, in: lines, handOver: handOver(after: row, in: lines),
                                      rolls: lines[row].wordTimed && style.arrivalOnly, fillsStage: style.fillsStage)
    }

    /// The exit the renderer actually draws: word-timed lines that do not
    /// roll, and line-timed lines in stage layouts, leave a moment after
    /// their last word (`stageExitStart`); everything else at the phrase's
    /// own exit.
    static func renderedExitStart(_ row: Int, in lines: [LyricsFetcher.Line], handOver: Double, rolls: Bool,
                                  fillsStage: Bool) -> Double {
        let line = lines[row]
        let phrase = LyricPhraseTiming.exitStart(of: line, nextStart: handOver)
        guard !rolls, line.wordTimed || fillsStage else { return phrase }
        return min(phrase, stageHeldUntil(line))
    }

    /// A layout remembered for this song or artist wins; "automatic" there
    /// means Match the music even if a fixed layout is set globally.
    private func composition(at index: Int) -> LyricComposition {
        LyricComposition.selected(settings: settings,
            remembered: memory.remembered(for: nowPlaying.current)?.style.composition, mood: mood, seed: index,
            track: nowPlaying.current.map { "\($0.artist)|\($0.title)" })
    }

    /// The next line may be previewed once the current line's last word
    /// has begun; its preview ramps in from that moment.
    private func previewAllowed(after index: Int, in lines: [LyricsFetcher.Line]) -> Double {
        guard lines.indices.contains(index) else { return -.infinity }
        let line = lines[index]
        guard line.wordTimed else { return line.start }
        return line.words.last { LyricRuns.isVisible($0.text) }?.start ?? line.start
    }

    private func nextLinePreview(after index: Int, in scene: SceneLines, time: Double) -> Int? {
        let lines = scene.lines
        let next = index + 1
        guard lines.indices.contains(index), lines.indices.contains(next), !lines[next].isInstrumental,
              !Self.changesTiming(lines[index], lines[next]), time < lines[next].start else { return nil }
        let style = composition(at: next)
        // A stage layout draws nothing for a line-timed preview, so the
        // line being sung would only dim for nothing.
        guard lines[next].wordTimed || !style.fillsStage else { return nil }
        guard settings.previewsNextWord(in: style),
              !lines[next].wordTimed || settings.revealsWords(in: style),
              !(lines[index].wordTimed && lines[next].wordTimed && style.arrivalOnly
                && composition(at: index).arrivalOnly) else { return nil }
        // One preview at a time, even if two lines overlap in the source.
        if lines[index].wordTimed, lines[index].words.contains(where: { $0.start > time && LyricRuns.isVisible($0.text) }) {
            return nil
        }
        guard let onset = LyricPhraseTiming.firstWordOnset(of: lines[next]),
              onset > time, onset - time <= settings.nextWordPreviewLead else { return nil }
        let line = lines[next]
        let words = readingOptions.display == .replace ? scene.romanized[next] ?? line.words : line.words
        if line.wordTimed {
            guard let first = words.first(where: { LyricRuns.isVisible($0.text) }),
                  LyricRuns.isPreviewWord(first.text) else { return nil }
        } else {
            let glue = glue(next, words: words, scene: scene, live: true)
            guard LyricRuns.canPreviewFirstRun(words, glue: glue) else { return nil }
        }
        return next
    }

    /// `yield` (0...1) dims a line-timed line while the next line's first
    /// word previews in its place, so the two never sit at full strength on
    /// top of each other.
    @ViewBuilder
    private func renderLyricLine(index: Int, scene: SceneLines, time: TimeInterval, stageSize: CGSize,
                                 energy: Double, live: Bool, outgoing: Bool = false, previewing: Bool = false,
                                 previewSince: Double = -.infinity, yield: Double = 0) -> some View {
        let lines = scene.lines
        if lines.indices.contains(index), !lines[index].words.isEmpty, previewing || time >= lines[index].start {
            let line = lines[index]
            let pose = LyricPhraseTiming(line: line, nextStart: handOver(after: index, in: lines), time: time)
            let composition = composition(at: index)
            let rolls = line.wordTimed && composition.arrivalOnly
            let handsOffPreview = line.wordTimed && !outgoing && settings.revealsWords(in: composition)
                && settings.previewsNextWord(in: composition)
            // Word-timed lines leave word by word (see `exitStart` below), so
            // only their entrance is applied to the line as a whole. A
            // rolling stage carries on into the next rolling line; when the
            // next line will not roll, it fades on its own exit -- the same
            // value before and after it becomes the outgoing line, so the
            // hand-over is a fade rather than a drop.
            let nextRolls = index + 1 < lines.count && lines[index + 1].wordTimed
                && self.composition(at: index + 1).arrivalOnly
            let stagePresence: Double = previewing ? 1
                : rolls ? Self.rollingPresence(nextRolls: nextRolls, outgoing: outgoing, exit: pose.exit)
                : line.wordTimed ? (handsOffPreview ? 1 : pose.progress)
                : pose.presence
            let readings = scene.romanized[index]
            // A reading that cannot be split by word goes once under the
            // whole line, arriving and leaving with it as a translation does
            // (a rolling stage puts it under the line's first word instead).
            // A preview lays it out too, unseen, so the line's first word
            // previews exactly where it will sit.
            let lineReading = rolls ? nil
                : LyricReadingPairs.line(readings, for: line, display: readingOptions.display)
            if stagePresence > 0.001 {
                // Rolling stages fill the stage by design, so the size slider
                // scales their words directly; fitting the whole stage capped
                // the slider's top fifth at "already full".
                let userScale = CGFloat(min(1.5, max(0.7, settings.lyricScale)))
                // Laid out in a preview as well (it is unseen until the line
                // starts): the line is centred with it, so leaving it out
                // made the previewed word jump up when the line arrived.
                let translation = extras.showsTranslations
                    ? (line.translation ?? scene.machineTranslations[index])?.trimmingCharacters(in: .whitespacesAndNewlines)
                    : nil
                // Dense web layouts set long lines smaller.
                let density = line.words.reduce(0) { $0 + $1.text.count + 3 }
                let densityScale = line.wordTimed && composition.hasConnections
                    ? min(1.0, max(0.52, sqrt(110.0 / Double(max(density, 110))))) : 1.0
                let translationSize = baseSize * 0.30 * userScale
                // A rolling stage keeps a band for translations clear of its
                // words whenever the song has any, so its size never changes
                // from line to line.
                let band = rolls && extras.showsTranslations
                    && (cache.hasTranslations(lines, fingerprint: scene.fingerprint) || !scene.machineTranslations.isEmpty)
                    ? translationSize * 2.7 + baseSize * 0.3 : 0
                let fit = LyricLineStack.Fit(available: composition.hasConnections
                    ? CGSize(width: stageSize.width * 0.92, height: stageSize.height * 0.86)
                    : stageSize, userScale: rolls || composition.fillsStage ? 1 : userScale,
                    neutral: rolls || composition.fillsStage ? 1 : 0.75)
                VStack(spacing: baseSize * 0.2) {
                Group {
                    if line.wordTimed {
                        let rolling = rolls ? rollingWords(around: index, scene: scene, time: time, live: live) : nil
                        let words = rolling?.words ?? (readingOptions.display == .replace ? readings ?? line.words : line.words)
                        // A constellation is a web of fragments: an unspaced
                        // line is parted into its words there, or it is one
                        // fragment with nothing to link.
                        let glue = rolls ? [] : glue(index, words: words, scene: scene, live: live,
                                                     splitsWords: composition.hasConnections)
                        let annotations = readingOptions.display == .below
                            ? rolling?.readings ?? readingPairs(index, readings: readings, words: line.words, glue: glue,
                                                                 scene: scene, live: live) : [:]
                        // Reduce Motion: the line fades; it does not slide or swell.
                        let still = rolls || reduceMotion
                        let lineBase: CGFloat = rolls ? baseSize * 0.75 * userScale
                            : composition.fillsStage ? baseSize * 0.8 * userScale : baseSize * densityScale
                        let lineSeed = rolls ? trackSeed : index
                        let lineExit: Double = rolls || previewing ? .infinity : Self.stageExitStart(line, pose: pose)
                        let lineView = LyricLineView(words: words, time: time, stageSize: stageSize,
                            baseSize: lineBase,
                            mood: mood, composition: composition,
                            seed: lineSeed, presence: stagePresence * (1 - pose.exit), accent: accent,
                            energy: energy, readings: annotations, lineEnd: line.end,
                            exitStart: lineExit,
                            previewSince: previewSince,
                            glue: glue,
                            bottomInset: band, following: rolling?.following, ordinals: rolling?.ordinals ?? [:],
                            rollingLineEnds: rolling?.lineEnds ?? [:], parts: rolling?.parts ?? [:])
                        let swell: CGFloat = still ? 1 : 0.965 + 0.035 * pose.arrival - 0.012 * pose.exit
                        let slide: CGFloat = still ? 0 : CGFloat((1 - pose.arrival) * 0.20 - pose.exit * 0.10) * baseSize
                        lineView
                            .opacity(stagePresence)
                            .scaleEffect(swell)
                            .offset(y: slide)
                    } else {
                        let words = readingOptions.display == .replace ? readings ?? line.words : line.words
                        let glue = glue(index, words: words, scene: scene, live: live)
                        lineTimedLyric(line, words: words, time: time, pose: pose,
                            stageSize: stageSize, composition: composition, seed: index, previewing: previewing,
                            previewSince: previewSince, glue: glue,
                            annotations: readingOptions.display == .below
                                ? readingPairs(index, readings: readings, words: line.words, glue: glue,
                                               scene: scene, live: live) : [:],
                            yield: yield,
                            heldPreview: !previewing && live && index > 0 && time - line.start < 1
                                && nextLinePreview(after: index - 1, in: scene, time: line.start.nextDown) == index)
                    }
                }
                if let lineReading {
                    // Sized with the line's words, as their own readings are.
                    TranslationLine(text: lineReading,
                                    font: settings.lyricFont(size: baseSize * 0.38 * densityScale, hero: false, mood: mood),
                                    width: stageSize.width * 0.8)
                        .opacity(pose.progress * (1 - pose.exit))
                }
                // A translation from the lyric source sits under its line as
                // a quieter second voice, arriving and leaving with it. A
                // rolling stage keeps one size across lines, so there it
                // rests over the stage's foot instead of resizing the stage.
                if !rolls, let translation, !translation.isEmpty {
                    TranslationLine(text: translation,
                                    font: settings.lyricFont(size: baseSize * 0.34 * userScale, hero: false, mood: mood),
                                    width: stageSize.width * 0.8)
                        .opacity(pose.progress * (1 - pose.exit))
                }
                }
                .overlay(alignment: .bottom) {
                    if rolls, let translation, !translation.isEmpty {
                        TranslationLine(text: translation,
                                        font: settings.lyricFont(size: translationSize, hero: false, mood: mood),
                                        width: stageSize.width * 0.8, lines: 2)
                            .opacity(pose.progress * (1 - pose.exit))
                            .padding(.bottom, baseSize * 0.2)
                    }
                }
                .opacity(Self.timingHandoffPresence(after: index, in: lines, time: time))
                .modifier(FittedLyricStage(available: fit.available, userScale: fit.userScale, neutral: fit.neutral))
                // Set no wider than the stage, and placed, by `LyricLineStack`.
                .layoutValue(key: LyricLineStack.LineKey.self, value: .init(fit: fit,
                    presence: previewing ? nil : pose.progress * (1 - pose.exit)))
            }
        } else {
            Color.clear
        }
    }

    /// A line with only a line timestamp, set as one level phrase of words.
    /// Its preview is the same layout with only the first word showing, so
    /// that word appears exactly where it will be rather than mid-screen
    /// over the line still being sung.
    /// When a stage layout starts clearing its line. These layouts show every
    /// word sung so far at once, so a line that lingers (a long gap before the
    /// next one, or a source whose line end is the next line's start) would
    /// sit there fully built; leave a moment after the last word instead.
    ///
    /// The cap on a long held note counts from the last word's onset, not
    /// the line's start: counted from the start, a word sung more than about
    /// nine seconds into a line was already gone at its onset and never
    /// appeared, and a held last word faded while still being sung.
    static func stageExitStart(_ line: LyricsFetcher.Line, pose: LyricPhraseTiming) -> Double {
        min(pose.exitStart, stageHeldUntil(line))
    }

    /// When a stage layout's line has been sung and held long enough.
    static func stageHeldUntil(_ line: LyricsFetcher.Line) -> Double {
        let sung = line.words.map(\.end).max() ?? line.end
        let lastOnset = line.words.map(\.start).max() ?? line.start
        let sungEnd = min(sung, lastOnset + 8)
        return max(line.start + 1.5, sungEnd) + WallpaperMotion.holdAfterSung
    }

    private func lineTimedLyric(_ line: LyricsFetcher.Line, words: [LyricsFetcher.Word], time: Double,
                                pose: LyricPhraseTiming, stageSize: CGSize, composition: LyricComposition,
                                seed: Int, previewing: Bool,
                                previewSince: Double, glue: [Bool], annotations: [TimedWordKey: String],
                                yield: Double, heldPreview: Bool = false) -> some View {
        let phrase = LyricsFetcher.Word(text: line.text, start: line.start, end: line.end)
        // A line sung as one has no words to highlight one by one; it
        // enters as its layout has it enter.
        let entrance = settings.arrival(in: composition, for: phrase).motion(in: composition)
        // The phrase's words are set at 0.68 of `base`: this matches the
        // single-text sizes used before (0.72, or 0.86 for Spoken).
        let base = baseSize * (composition == .spoken ? 1.26 : 1.06)
        let pointSize = base * 0.68
        let previewOpacity = vc_word_visibility(time, line.start, line.end, 1, 1, settings.nextWordPreviewLead)
            * Easing.smoothstep((time - previewSince) / 0.16)
        let stage = composition.fillsStage
        let visibility = pose.presence * (1 - 0.5 * yield)
        // The previewed first word stays where the preview showed it, at
        // least as strong, while the rest of the line makes its entrance:
        // restarting it with the line blinked it out for a frame or two
        // and dropped it back to the entrance's starting pose.
        let holdsFirstRun = !previewing && !stage && heldPreview
            && (pose.progress < 1 || pose.arrival != 1) && pose.exit <= 0
        let lineView = { (firstRun: Double?, held: Bool, hides: Bool) in
            LyricLineView(words: words, time: time, stageSize: stageSize,
                baseSize: stage ? baseSize * 0.8 * CGFloat(min(1.5, max(0.7, self.settings.lyricScale))) : base,
                mood: self.mood, composition: stage ? composition : .scattered, seed: seed, presence: pose.presence,
                readings: annotations, wordTimed: false, phraseSweep: LyricPhraseTiming.sweep(of: line, at: time),
                exitStart: stage && !previewing ? Self.stageExitStart(line, pose: pose) : .infinity,
                glue: glue, firstRunOnly: firstRun, heldFirstRun: held, hidesFirstRun: hides)
        }
        return ZStack {
            lineView(previewing ? previewOpacity : nil, false, holdsFirstRun)
                .modifier(WordEntrance(progress: previewing ? 1 : pose.progress, style: entrance, size: pointSize,
                    direction: seed.isMultiple(of: 2) ? 1 : -1, visibility: previewing ? 1 : visibility,
                    arrival: previewing ? 1 : pose.arrival))
            if holdsFirstRun {
                lineView(Self.heldFirstRunOpacity(presence: pose.presence) * (1 - 0.5 * yield), true, false)
            }
        }
    }

    /// The previewed first word of a line-timed line as the line arrives:
    /// never fainter than the preview it takes over from.
    static func heldFirstRunOpacity(presence: Double) -> Double {
        max(0.25, presence)
    }

    /// Which words of a line run together, worked out once per line and
    /// the words shown for it. `splitsWords` parts unspaced runs into their
    /// words (`LyricRuns.splittingWords`).
    private func glue(_ index: Int, words: [LyricsFetcher.Word], scene: SceneLines, live: Bool,
                      splitsWords: Bool = false) -> [Bool] {
        guard scene.lines.indices.contains(index) else { return [] }
        let line = scene.lines[index]
        let build = {
            let glue = LyricRuns.glue(line: line, words: words)
            return splitsWords ? LyricRuns.splittingWords(words, glue: glue) : glue
        }
        guard live else { return build() }
        return cache.glue(line: index, fingerprint: scene.fingerprint, words: words, splitsWords: splitsWords, build: build)
    }

    /// Reading pairs for a line set in runs, built once per line rather
    /// than per frame.
    private func readingPairs(_ index: Int, readings: [LyricsFetcher.Word]?, words: [LyricsFetcher.Word],
                              glue: [Bool], scene: SceneLines, live: Bool) -> [TimedWordKey: String] {
        guard live else { return LyricReadingPairs.match(readings, to: words, glue: glue) }
        return cache.readingPairs(line: index, fingerprint: scene.fingerprint, readings: readings, words: words,
                                  glue: glue)
    }

    /// The words a rolling stage holds. Rebuilt only when a word actually
    /// joins or leaves the window, not every frame.
    private func rollingWords(around index: Int, scene: SceneLines, time: Double,
                              live: Bool) -> LyricLayerCache.RollingWindow {
        let lead = live ? max(1.2, settings.nextWordPreviewLead) : 0
        let display = readingOptions.display
        let offsets = cache.wordOffsets(fingerprint: scene.fingerprint, lines: scene.lines, romanized: scene.romanized,
                                        display: display)
        let build = { LyricLayerCache.rollingWindow(around: index, lines: scene.lines, romanized: scene.romanized,
                                                    time: time, lead: lead, display: display, offsets: offsets) }
        guard live else { return build() }
        let key = LyricLayerCache.RollingKey(index: index, fingerprint: scene.fingerprint, display: display,
                                             lead: lead, readings: LyricLayerCache.signature(scene.romanized))
        return cache.rolling(key, time: time, build: build)
    }

    private func placeholder(_ text: String, size: CGFloat, colour: Color) -> some View {
        Text(text)
            .font(settings.uiFont(size: size, mood: mood))
            .foregroundStyle(colour.opacity(0.5))
    }

    /// The idle notice when macOS refuses Lunalyr a running player, worded
    /// as the hover controls and the menu-bar banner word it.
    static func refusedTitle(_ player: PlayerKind) -> String { "Lunalyr can’t read \(player.displayName)" }
    static let refusedDetail = "Allow it in System Settings › Privacy & Security › Automation"
}

/// Per-layer memo for work that would otherwise repeat every frame. Never
/// publishes; consulted while the frame is built, like `RenderMemo`.
@MainActor
final class LyricLayerCache: ObservableObject {
    struct RollingKey: Equatable {
        let index: Int
        let fingerprint: Int
        let display: RomanizationOptions.Display
        let lead: Double
        let readings: Int
    }
    struct RollingWindow {
        var words: [LyricsFetcher.Word]
        var readings: [TimedWordKey: String]
        /// The window is exactly this set of words while `from <= time < until`.
        var from: Double
        var until: Double
        /// The onset of the first word after the window, if any.
        var following: Double? = nil
        /// Each word's place in the whole song, which its colour is keyed
        /// by: its place in the window shifts whenever a line or a word
        /// leaves it.
        var ordinals: [TimedWordKey: Int] = [:]
        /// The tokens each glued run of the window is made of.
        var parts: [TimedWordKey: [LyricsFetcher.Word]] = [:]
        var lineEnds: [TimedWordKey: Double] = [:]
    }
    private var rollingKey: RollingKey?
    private var rollingWindow = RollingWindow(words: [], readings: [:], from: .infinity, until: -.infinity)
    private(set) var rollingBuilds = 0

    func rolling(_ key: RollingKey, time: Double, build: () -> RollingWindow) -> RollingWindow {
        if key != rollingKey || time < rollingWindow.from || time >= rollingWindow.until {
            rollingWindow = build()
            rollingKey = key
            rollingBuilds += 1
        }
        return rollingWindow
    }

    private var offsets: [Int: [Int]] = [:]

    /// `wordOffsets` for a scene's lines, worked out once per song and
    /// display, not at every change of window. Two scenes cross-fading each
    /// keep theirs.
    func wordOffsets(fingerprint: Int, lines: [LyricsFetcher.Line], romanized: [Int: [LyricsFetcher.Word]],
                     display: RomanizationOptions.Display) -> [Int] {
        var hasher = Hasher()
        hasher.combine(fingerprint)
        hasher.combine(display)
        hasher.combine(Self.signature(romanized))
        let key = hasher.finalize()
        if let hit = offsets[key], hit.count == lines.count + 1 { return hit }
        let value = Self.wordOffsets(lines: lines, romanized: romanized, display: display)
        if offsets.count > 4 { offsets.removeAll() }
        offsets[key] = value
        return value
    }

    /// Where each line's words begin among the words of the whole song, as
    /// a rolling stage sets them: a glued run is one word, punctuation on
    /// its own none. One entry per line, then the total.
    static func wordOffsets(lines: [LyricsFetcher.Line], romanized: [Int: [LyricsFetcher.Word]],
                            display: RomanizationOptions.Display) -> [Int] {
        var offsets: [Int] = [], total = 0
        offsets.reserveCapacity(lines.count + 1)
        for (row, line) in lines.enumerated() {
            offsets.append(total)
            let source = display == .replace ? romanized[row] ?? line.words : line.words
            total += LyricRuns.merged(source, glue: LyricRuns.rollingGlue(line: line, words: source)).words
                .filter { LyricRuns.isVisible($0.text) }.count
        }
        offsets.append(total)
        return offsets
    }

    /// How long a rolling word takes to hand the stage to the next one:
    /// `vc_rolling_handoff` has landed exactly by then.
    static let rollingHandover: Double = 3

    /// The words of a rolling stage around one line, and the span of time
    /// over which that set does not change. `offsets` is `wordOffsets` for
    /// these lines, worked out here when not given.
    static func rollingWindow(around index: Int, lines: [LyricsFetcher.Line], romanized: [Int: [LyricsFetcher.Word]],
                              time: Double, lead: Double, display: RomanizationOptions.Display,
                              offsets: [Int]? = nil) -> RollingWindow {
        var result: [LyricsFetcher.Word] = [], seen = Set<TimedWordKey>()
        var readings: [TimedWordKey: String] = [:]
        var ordinals: [TimedWordKey: Int] = [:]
        var parts: [TimedWordKey: [LyricsFetcher.Word]] = [:]
        var from = -Double.infinity, until = Double.infinity
        var candidates: [(row: Int, word: LyricsFetcher.Word)] = []
        var lineEnds: [TimedWordKey: Double] = [:]
        let rows = LyricPhraseTiming.rollingRange(around: index, lines: lines)
        let ends = rows.map { LyricHighlightTiming.lineEnd(lines[$0].end, words: lines[$0].words) }
        // A place for each word that no change of window can move, counted
        // in the words the stage shows: every earlier line's, then the
        // word's place in its own. Counting earlier lines' tokens instead
        // put a glued line's last word and the next line's first nearly a
        // whole line of characters apart, which can land on almost the
        // same colour.
        let offsets = offsets.flatMap { $0.count == lines.count + 1 ? $0 : nil }
            ?? wordOffsets(lines: lines, romanized: romanized, display: display)
        for row in rows {
            let source = display == .replace ? romanized[row] ?? lines[row].words : lines[row].words
            let glue = LyricRuns.rollingGlue(line: lines[row], words: source)
            let pairs = display == .below ? LyricReadingPairs.rolling(romanized[row], for: lines[row], glue: glue) : [:]
            // Glued syllables and characters roll as the word they form.
            let runs = LyricRuns.merged(source, glue: glue, readings: pairs)
            readings.merge(runs.readings) { first, _ in first }
            parts.merge(runs.tokens) { first, _ in first }
            var place = offsets[row]
            for word in runs.words where LyricRuns.isVisible(word.text) {
                if ordinals[word.presentationID] == nil { ordinals[word.presentationID] = place }
                place += 1
                candidates.append((row, word))
            }
        }
        let onsets = candidates.map(\.word.start).sorted()
        for (row, word) in candidates {
            // Old words leave twelve seconds after they end, and never before
            // the word after them has taken their place: the stack is
            // anchored on the last word sung, so a preview waiting through a
            // long break keeps its place when that word goes. (It faded out
            // long before; see `vc_rolling_pose`.)
            var low = 0, high = onsets.count
            while low < high {
                let middle = (low + high) / 2
                if onsets[middle] <= word.start { low = middle + 1 } else { high = middle }
            }
            let successor = low < onsets.count ? onsets[low] : .infinity
            let lineEnd = max(ends[row - rows.lowerBound], word.end)
            let leaves = max(word.end + 12, successor + rollingHandover, lineEnd + LyricHighlightTiming.fade)
            if time > leaves { from = max(from, leaves); continue }
            until = min(until, leaves)
            // Words of the following line join a little before they are sung.
            if row > index {
                let joins = word.start - lead
                if time < joins { until = min(until, joins); continue }
                from = max(from, joins)
            }
            if seen.insert(word.presentationID).inserted {
                result.append(word)
                lineEnds[word.presentationID] = lineEnd
            }
        }
        let words = result.sorted { $0.start < $1.start }
        // The next word to come after the window, looked up in the lines
        // themselves: the window's last word keeps this pace before and
        // after that word joins, so the stage cannot lurch at the join.
        var following: Double?
        if let last = words.last {
            let rows = LyricPhraseTiming.rollingRange(around: index, lines: lines)
            for row in rows.lowerBound..<min(lines.count, rows.upperBound + 3) where following == nil {
                let source = display == .replace ? romanized[row] ?? lines[row].words : lines[row].words
                let runs = LyricRuns.merged(source, glue: LyricRuns.rollingGlue(line: lines[row], words: source))
                following = runs.words.first {
                    $0.start > last.start && LyricRuns.isVisible($0.text) && !seen.contains($0.presentationID)
                }?.start
            }
        }
        return RollingWindow(words: words, readings: readings, from: from, until: until, following: following,
                             ordinals: ordinals.filter { seen.contains($0.key) },
                             parts: parts.filter { seen.contains($0.key) }, lineEnds: lineEnds)
    }

    /// Cheap identity for a readings table: changes when readings arrive or
    /// are regenerated in another language.
    static func signature(_ romanized: [Int: [LyricsFetcher.Word]]) -> Int {
        var hasher = Hasher()
        hasher.combine(romanized.count)
        if let first = romanized.keys.min(), let words = romanized[first] {
            hasher.combine(first); hasher.combine(words.first?.text); hasher.combine(words.count)
        }
        return hasher.finalize()
    }

    private var pairsFingerprint = 0
    private var pairs: [Int: (signature: Int, value: [TimedWordKey: String])] = [:]

    func readingPairs(line: Int, fingerprint: Int, readings: [LyricsFetcher.Word]?,
                      words: [LyricsFetcher.Word], glue: [Bool] = []) -> [TimedWordKey: String] {
        if fingerprint != pairsFingerprint { pairs.removeAll(); pairsFingerprint = fingerprint }
        var hasher = Hasher()
        hasher.combine(Self.signature(words))
        hasher.combine(readings?.count)
        for reading in readings ?? [] {
            hasher.combine(reading.text); hasher.combine(reading.start); hasher.combine(reading.end)
        }
        hasher.combine(glue)
        let signature = hasher.finalize()
        if let hit = pairs[line], hit.signature == signature { return hit.value }
        let value = LyricReadingPairs.match(readings, to: words, glue: glue)
        if pairs.count > 48 { pairs.removeAll() }
        pairs[line] = (signature, value)
        return value
    }

    /// Identity of the words a line is shown with: its own, or the
    /// readings replacing them.
    static func signature(_ words: [LyricsFetcher.Word]) -> Int {
        var hasher = Hasher()
        hasher.combine(words.count)
        for word in words { hasher.combine(word.text) }
        return hasher.finalize()
    }

    private var translationsFingerprint: Int?
    private var translations = false

    /// Whether any line of the song carries a translation; once per song.
    func hasTranslations(_ lines: [LyricsFetcher.Line], fingerprint: Int) -> Bool {
        if translationsFingerprint != fingerprint {
            translations = lines.contains { !($0.translation?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true) }
            translationsFingerprint = fingerprint
        }
        return translations
    }

    private var glueFingerprint = 0
    private var glues: [Int: (words: Int, value: [Bool])] = [:]

    /// Keyed by the words themselves: comparing only the first word reused
    /// one display mode's grouping in the other whenever a line began with
    /// a word that has no reading ("Baby 夢ならば").
    func glue(line: Int, fingerprint: Int, words: [LyricsFetcher.Word], splitsWords: Bool = false,
              build: () -> [Bool]) -> [Bool] {
        if fingerprint != glueFingerprint { glues.removeAll(); glueFingerprint = fingerprint }
        var hasher = Hasher()
        hasher.combine(Self.signature(words))
        hasher.combine(splitsWords)
        let signature = hasher.finalize()
        if let hit = glues[line], hit.words == signature { return hit.value }
        let value = build()
        if glues.count > 48 { glues.removeAll() }
        glues[line] = (signature, value)
        return value
    }

    private var indexFingerprint: Int?
    private var index = LyricLineIndex(lines: [])

    /// A line index for a captured scene, built once per scene.
    func lineIndex(for lines: [LyricsFetcher.Line], fingerprint: Int) -> LyricLineIndex {
        if indexFingerprint != fingerprint {
            index = LyricLineIndex(lines: lines)
            indexFingerprint = fingerprint
        }
        return index
    }

    let director = LyricInkDirector()

    func ink(_ inputs: LyricInkDirector.Inputs, wall: Double) -> LyricInk {
        director.ink(inputs, wall: wall)
    }

    private var accentKey: Color?
    private var accentLab: OKLab?

    /// The album accent in OKLab, converted once per accent.
    func oklab(_ colour: Color) -> OKLab? {
        if colour != accentKey {
            accentKey = colour
            accentLab = NSColor(colour).usingColorSpace(.sRGB).map {
                Perceptual.oklab(sRGB: SIMD3(Double($0.redComponent), Double($0.greenComponent), Double($0.blueComponent)))
            }
        }
        return accentLab
    }

    private var refused: PlayerKind?
    private var refusedAt: Date?

    /// When `player` was first seen refused (nil while none is), so the
    /// notice saying so holds and fades like the others.
    func refusedSince(_ player: PlayerKind?, now: Date) -> Date? {
        if player != refused {
            refused = player
            refusedAt = player == nil ? nil : now
        }
        return refusedAt
    }

    private var energy = VCFollower()
    private var energyWall: Double?

    /// Live loudness when the visualiser is capturing audio, smoothed so it
    /// can only ever ease the motion rather than jolt it. Zero otherwise.
    func liveEnergy(wall: Double) -> Double {
        let audio = AudioReactiveController.shared
        let target = audio.running ? min(1, max(0, audio.levels.rms * 2.6 + audio.levels.bass * 0.8)) : 0
        let dt = energyWall.map { wall - $0 } ?? 0
        // Later than the last frame, or the system clock stepped back.
        if dt > 0 || dt < -1 || energyWall == nil {
            energyWall = wall
            return min(1, max(0, vc_follow(&energy, target, max(0, dt), 0.18, 2.5)))
        }
        return min(1, max(0, energy.value))
    }
}

/// How far through unsynced lyrics the scroll is. They carry no timing, so
/// the block moves evenly with the song: holding for an intro, reaching the
/// end shortly before the song does. Continuous, so it never jumps.
enum PlainLyricsScroll {
    /// Nil with Reduce Motion: the block is fitted and held still instead.
    static func progress(time: Double, duration: Double, reduceMotion: Bool) -> Double? {
        reduceMotion ? nil : progress(time: time, duration: duration)
    }

    static func progress(time: Double, duration: Double) -> Double {
        guard duration > 30, time.isFinite else { return 0 }
        let lead = min(12, duration * 0.08), tail = min(20, duration * 0.12)
        return Easing.smoothstep((time - lead) / max(1, duration - lead - tail))
    }
}

/// How fast the lyric clock needs to run. Reported by the layer each frame;
/// published only when it changes, after the frame, so it never interrupts
/// the update that measured it.
@MainActor
final class LyricPace: ObservableObject {
    enum Level: Equatable {
        /// Something is entering, being sung, swelling or leaving.
        case active
        /// Only slow motion: wandering interlude dots, drifting colour,
        /// scrolling unsynced lyrics.
        case ambient
        /// Nothing visible changes beyond the slow float.
        case still
        /// Playback is paused: the words and their float follow the song's
        /// position, so nothing moves, but a seek in the player still
        /// changes the line. Looked at twice a second.
        case waiting
        /// Nothing on the stage can change until an input does: the
        /// notices have gone, the lyrics are hidden, or there is nothing to
        /// show. The clock stops; any input change rebuilds the layer, which
        /// reports a new level and starts it again.
        case resting
    }
    @Published private(set) var level: Level = .active
    private var pending: Level?
    private var lastWall = -Double.infinity

    /// The newest report wins: one waiting to be published is replaced, not
    /// kept. Holding on to the first of two reports in one turn (a scene
    /// changing as a notice finishes) could publish `.resting` after an
    /// `.active` -- and a resting clock draws no frame that would correct it.
    func report(_ next: Level) {
        guard next != (pending ?? level) else { return }
        let scheduled = pending != nil
        pending = next
        guard !scheduled else { return }
        DispatchQueue.main.async { [weak self] in
            guard let self, let pending = self.pending else { return }
            self.pending = nil
            if pending != self.level { self.level = pending }
        }
    }

    /// Whether the clock is stopped.
    var rests: Bool { level == .resting }

    /// The frame interval for `level`, never faster than `full`.
    func interval(full: Double) -> Double {
        switch level {
        case .active: return full
        case .ambient: return max(full, 1.0 / 24)
        case .still: return max(full, 1.0 / 12)
        case .waiting: return max(full, 0.5)
        case .resting: return max(full, 1)
        }
    }

    /// How far behind the real time a frame may be drawn.
    static let staleness = 0.05

    /// The wall time to draw a frame at. A tick is drawn at its own date,
    /// so frames step evenly. But a rebuild between ticks -- any input
    /// changing -- is handed the date of the last tick: up to half a second
    /// old while paused, hours while resting. A cross-fade or a hide it
    /// started would begin in the past and be mostly over by the next
    /// frame, so such a frame is drawn at the real time, give or take
    /// `staleness`.
    ///
    /// Never earlier than a frame already drawn, as a rebuild and the next
    /// tick can arrive slightly out of order -- unless the system clock
    /// stepped back (set by hand, or corrected): then the frames follow it,
    /// rather than every fade and note holding still until the old time
    /// comes round again.
    func wall(_ date: Double) -> Double {
        let now = max(date, Date().timeIntervalSinceReferenceDate - Self.staleness)
        if now < lastWall - 1 { lastWall = now }
        lastWall = max(lastWall, now)
        return lastWall
    }

    /// What the clock needs on a frame where no word is being sung.
    struct Quiet: Equatable {
        var kind: LyricSceneKey.Kind
        /// How shown the lyrics are (hiding fades them out).
        var shown: Double
        /// Where that is heading: 1 while the lyrics are shown or coming
        /// back, 0 while they are hidden or going.
        var target: Double
        var playing: Bool
        /// Colour that changes by itself: drifting, music reactive, or
        /// still easing towards a new colour.
        var colourMoves: Bool
        var noLyrics: Settings.NoLyricsBehaviour
        /// Seconds since the idle or "lyrics unavailable" notice went up.
        var noticeAge: TimeInterval?
    }

    /// The level for a quiet frame, or nil while a song is being sung: its
    /// words decide then. Notices hold still, fade on every frame and then
    /// need nothing; hidden lyrics and an empty stage need nothing; paused
    /// lyrics only wait for a seek unless their colour moves.
    static func quietLevel(_ quiet: Quiet, timing: LyricStagePresence.Timing) -> Level? {
        // Only hidden lyrics that have finished fading out rest. Shown again,
        // their first frame -- a millisecond into the fade, presence still
        // about a millionth -- must start the clock, or nothing would ever
        // draw the rest of the fade: Settings and mirroring show the lyrics
        // without anything else changing on the stage.
        if quiet.target <= 0, quiet.shown <= 0.001 { return .resting }
        if abs(quiet.shown - quiet.target) > 0.001 { return .active }
        switch quiet.kind {
        case .idle:
            return notice(age: quiet.noticeAge, hold: timing.idleHold, fade: timing.noticeFade, arrival: 0.6,
                          colourMoves: quiet.colourMoves)
        case .missing:
            switch quiet.noLyrics {
            case .nothing: return .resting
            // The title and artist are set in the text colour, which may
            // still be drifting or easing to a new cover.
            case .trackName: return quiet.colourMoves ? .ambient : .resting
            case .notice:
                return notice(age: quiet.noticeAge, hold: timing.noticeHold, fade: timing.noticeFade,
                              colourMoves: quiet.colourMoves)
            }
        case .loading:
            return .still
        case .plain:
            return quiet.playing || quiet.colourMoves ? .ambient : .waiting
        case .synced:
            guard !quiet.playing else { return nil }
            return quiet.colourMoves ? .ambient : .waiting
        }
    }

    /// A notice that arrives, holds for `hold` seconds and fades over
    /// `fade`: every frame while it moves, few while it holds (more if its
    /// colour moves), none once it has gone or if it never went up.
    static func notice(age: TimeInterval?, hold: TimeInterval, fade: TimeInterval,
                       arrival: TimeInterval = 0, colourMoves: Bool = false) -> Level {
        guard let age, age < hold + fade + 0.05 else { return .resting }
        if age < arrival || age >= hold - 0.1 { return .active }
        return colourMoves ? .ambient : .still
    }
}

/// The lyrics on their own frame clock.
///
/// They used to share the background's: every wallpaper frame rebuilt and
/// re-diffed the whole lyric tree on every display, whether or not a word
/// moved. Here the stage compares equal across background frames, so they
/// pass it by, and its own clock runs at the display rate only while words
/// are entering, being sung or leaving -- 24 fps in a break with only slow
/// drift, 12 when nothing changes but the float, twice a second while the
/// song is paused, and not at all once nothing on the stage can change
/// (nothing playing, no lyrics, hidden lyrics) until an input does.
struct LyricStage: View, Equatable {
    let size: CGSize
    let mood: TrackMood
    let accent: Color
    let trackSeed: Int
    let palette: [SIMD3<Float>]
    /// The wallpaper's frame interval: the fastest the lyrics will run.
    let interval: Double
    let paused: Bool
    /// What the wallpaper measured behind the stage, for a picture (see
    /// `LyricBackdrop`); nil for generated wallpapers or until it is known.
    var backdrop: Double? = nil
    @EnvironmentObject private var nowPlaying: NowPlayingMonitor
    @ObservedObject private var lyricsVM = LyricsViewModel.shared
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var songOffsets = SongOffsets.shared
    @StateObject private var pace = LyricPace()

    static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.size == rhs.size && lhs.mood == rhs.mood && lhs.accent == rhs.accent && lhs.trackSeed == rhs.trackSeed
            && lhs.palette == rhs.palette && lhs.interval == rhs.interval && lhs.paused == rhs.paused
            && lhs.backdrop == rhs.backdrop
    }

    var body: some View {
        let layout = LyricViewportMetrics(size: size)
        TimelineView(.animation(minimumInterval: pace.interval(full: interval), paused: paused || pace.rests)) { timeline in
            let now = pace.wall(timeline.date.timeIntervalSinceReferenceDate)
            // Smoothed so Music's periodic re-anchoring bends the pace
            // instead of jumping every word mid-animation.
            let time = LyricClock.shared.time(wall: now,
                raw: nowPlaying.playbackTime() + settings.lyricOffset + songOffsets.offset(for: lyricsVM.currentQuery),
                playing: nowPlaying.isPlaying)
            LyricLayer(time: time, baseSize: layout.baseSize, maxWidth: layout.stageSize.width,
                       maxHeight: layout.stageSize.height, mood: mood, accent: accent, trackSeed: trackSeed,
                       wall: now, palette: palette, measuredBackdrop: backdrop, animating: !paused, pace: pace)
        }
    }
}

/// A held line moving aside for the lines that started under it (see
/// `LyricLayer.heldShift`): smaller, and -- placed by `LyricLineStack` --
/// above them. `travel` is nil for a line that is not held.
struct HeldLineShift: ViewModifier {
    let travel: Double?

    func body(content: Content) -> some View {
        content
            .scaleEffect(1 - LyricLineStack.heldShrink * CGFloat(travel ?? 0))
            .layoutValue(key: LyricLineStack.HeldKey.self, value: travel)
    }
}

/// The lines on a stage, each centred on it as a `ZStack` would -- except
/// a held line (a duet part or ad-lib still being sung after the next line
/// started), which rises clear above the lines in the middle, by their
/// measured, fitted height, as far as the top of the stage allows. Lifted
/// by a fixed share of the stage, it sat over any line taller than that
/// share, as a constellation fitted to most of the stage can be.
struct LyricLineStack: Layout {
    /// How a line is fitted to the stage (`FittedLyricStage`).
    struct Fit: Equatable {
        var available: CGSize
        var userScale: CGFloat
        var neutral: CGFloat

        func scale(for size: CGSize) -> CGFloat {
            LyricStageFit.presentationScale(content: size, available: available, userScale: userScale, neutral: neutral)
        }
    }

    /// A drawn line: how it is fitted, and how present it is in the middle
    /// of the stage (its entrance and exit, 0...1). A preview, which shows
    /// only its first word, has none: it never pushes a held line about.
    struct Line: Equatable {
        var fit: Fit
        var presence: Double?
    }

    struct LineKey: LayoutValueKey {
        static let defaultValue: Line? = nil
    }

    /// 0...1: how far a held line has moved aside; nil for any other.
    struct HeldKey: LayoutValueKey {
        static let defaultValue: Double? = nil
    }

    /// How much smaller a held line is once it has moved aside.
    static let heldShrink: CGFloat = 0.3

    let stageSize: CGSize
    /// Between a held line and the lines below it.
    let gap: CGFloat

    /// One subview as the placement sees it.
    struct Item: Equatable {
        /// The line's size before it is fitted.
        var size: CGSize
        var line: Line?
        var held: Double?
    }

    /// Each subview's vertical offset from the middle of the stage: zero
    /// for all but held lines. Those stack upwards, the latest nearest the
    /// middle, above the tallest line there (weighted by how present it is,
    /// so the held line follows lines arriving and leaving instead of
    /// jumping between them), and never past the top of the stage.
    static func offsets(_ items: [Item], stageHeight: CGFloat, gap: CGFloat) -> [CGFloat] {
        func extent(_ item: Item) -> CGFloat {
            guard let line = item.line else { return 0 }
            return item.size.height * line.fit.scale(for: item.size)
        }
        var middle: CGFloat = 0
        for item in items where item.held == nil {
            guard let presence = item.line?.presence else { continue }
            middle = max(middle, extent(item) / 2 * CGFloat(min(1, max(0, presence))))
        }
        var result = [CGFloat](repeating: 0, count: items.count)
        var top = -middle
        for index in items.indices.reversed() {
            guard let travel = items[index].held, travel > 0, items[index].line != nil else { continue }
            let amount = CGFloat(min(1, travel))
            let half = extent(items[index]) * (1 - heldShrink * amount) / 2
            let target = max(-stageHeight / 2 + half, top - gap - half)
            result[index] = min(0, target) * amount
            top = result[index] - half
        }
        return result
    }

    private func lineProposal(_ proposal: ProposedViewSize) -> ProposedViewSize {
        ProposedViewSize(width: min(stageSize.width, proposal.width ?? stageSize.width), height: proposal.height)
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        var size = CGSize.zero
        for subview in subviews {
            let line = subview[LineKey.self] != nil
            let measured = subview.sizeThatFits(line ? lineProposal(proposal) : proposal)
            // A line takes the stage's width, as its frame did.
            let width = line ? max(measured.width, lineProposal(proposal).width ?? measured.width) : measured.width
            size = CGSize(width: max(size.width, width), height: max(size.height, measured.height))
        }
        return size
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let lines = lineProposal(proposal)
        let items = subviews.map { subview -> Item in
            let line = subview[LineKey.self]
            return Item(size: line == nil ? .zero : subview.sizeThatFits(lines), line: line, held: subview[HeldKey.self])
        }
        let offsets = Self.offsets(items, stageHeight: stageSize.height, gap: gap)
        for (index, subview) in subviews.enumerated() {
            subview.place(at: CGPoint(x: bounds.midX, y: bounds.midY + offsets[index]), anchor: .center,
                          proposal: items[index].line == nil ? proposal : lines)
        }
        Self.recorder?(items, offsets)
    }

    /// Tests watch what the real layout placed. Never set in the app.
    nonisolated(unsafe) static var recorder: (([Item], [CGFloat]) -> Void)?
}

/// A line's translation, or a reading of the whole line: the reading
/// colour, centred, under its line.
private struct TranslationLine: View {
    let text: String
    let font: Font
    let width: CGFloat
    /// Kept to the band reserved for it on rolling stages.
    var lines: Int?
    @Environment(\.lyricInk) private var ink

    var body: some View {
        Text(text)
            .font(font)
            .foregroundStyle(ink.base.reading)
            .lineLimit(lines)
            .multilineTextAlignment(.center)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: width)
            .shadow(color: .black.opacity(0.3), radius: 6, y: 2)
    }
}
