import SwiftUI
import Combine

extension PlayerKind {
    /// The colour each player is known by, so the switch reads at a glance.
    var tint: Color {
        switch self {
        case .appleMusic: return Color(red: 0.98, green: 0.26, blue: 0.40)
        case .spotify: return Color(red: 0.12, green: 0.84, blue: 0.38)
        case .vlc: return Color(red: 1.0, green: 0.53, blue: 0.0)
        case .swinsian: return Color(red: 0.35, green: 0.62, blue: 1.0)
        case .vox: return Color(red: 0.98, green: 0.36, blue: 0.22)
        case .safari, .chrome, .arc, .brave, .edge:
            return WebTabMemory.shared.entry(for: self)?.service.tint ?? Color(red: 0.62, green: 0.70, blue: 1.0)
        }
    }

    /// The switch's label: the web player last seen in a browser, else "Web".
    var shortName: String {
        switch self {
        case .appleMusic: return "Apple Music"
        case .safari, .chrome, .arc, .brave, .edge: return WebTabMemory.shared.entry(for: self)?.service.displayName ?? "Web"
        default: return displayName
        }
    }
}

extension WebService {
    var tint: Color {
        switch self {
        case .youTubeMusic: return Color(red: 1.0, green: 0.2, blue: 0.2)
        case .soundCloud: return Color(red: 1.0, green: 0.47, blue: 0.1)
        case .amazonMusic: return Color(red: 0.15, green: 0.82, blue: 0.85)
        }
    }
}

/// Set while the hit mask is drawn: scroll views come out blank in an image,
/// so the mask is told to paint plain blocks where they are.
private struct MaskRenderKey: EnvironmentKey { static let defaultValue = false }
extension EnvironmentValues {
    var playerMaskRender: Bool {
        get { self[MaskRenderKey.self] }
        set { self[MaskRenderKey.self] = newValue }
    }
}

/// What the player draws of the song: the monitor's, or a preview's.
struct PlayerNow: Equatable {
    var track: NowPlayingMonitor.Track?
    var isPlaying = false
    /// The player that is playing it.
    var player: PlayerKind?
    var artwork: NSImage?
    var canControl = true
    var idleTitle = "Nothing playing"
}

/// The player that lives on the desktop, "Sleeve": one card washed in the
/// album's own colours. On the left the cover, with the record peeking out
/// of it (it slides back in on pause); on the right the player, the song,
/// the seek bar and a row with play, search, the queue and the film button.
/// Search and the queue take over the right-hand column; the cover stays as
/// the anchor, with the song and the transport under it.
struct DesktopPlayerView: View {
    static let width: CGFloat = 640
    static let cover: CGFloat = 176
    static let column: CGFloat = 364
    static let artWidth: CGFloat = 216
    static let padding: CGFloat = 20
    static let radius: CGFloat = 28
    /// The card at rest.
    static let restHeight: CGFloat = cover + 2 * padding
    /// The card at its tallest (the queue open).
    static var tallestHeight: CGFloat { columnHeight(.queue) + 2 * padding }

    /// Where the card's top sits in the panel: just under the middle of the
    /// screen, so the lyric (in the middle) stays above it, and the card
    /// grows downwards; never past the panel's bottom.
    static func cardTop(panelHeight: CGFloat) -> CGFloat {
        max(0, min(panelHeight / 2 + 8, panelHeight - tallestHeight))
    }

    /// The right-hand column's height for each pane. A queue of a song or
    /// two (Spotify shares only the one playing) keeps to the height of the
    /// cover with the song and transport under it.
    static func columnHeight(_ pane: DesktopPlayerModel.Pane, queueRows: Int = .max) -> CGFloat {
        switch pane {
        case .song: return cover
        case .search: return 300
        case .queue: return queueRows <= 2 ? compactArtHeight : 334
        }
    }

    /// The cover with the song's title and the small transport under it.
    static let compactArtHeight: CGFloat = cover + 96

    /// The song's titles while the seek bar's capsules are up over them:
    /// all but gone, so the capsules read.
    static func titleOpacity(dimmed: Bool) -> Double { dimmed ? 0.08 : 1 }

    @ObservedObject var model: DesktopPlayerModel
    let displayID: CGDirectDisplayID
    @ObservedObject private var hover = HoverState.shared
    @EnvironmentObject private var nowPlaying: NowPlayingMonitor
    @ObservedObject private var lyrics = LyricsViewModel.shared
    @ObservedObject private var readings = RomanizationOptions.shared
    @Environment(\.playerMaskRender) private var maskRender
    @Environment(\.playerPreview) private var preview
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion
    @Environment(\.accessibilityReduceTransparency) private var systemReduceTransparency
    @Environment(\.colorSchemeContrast) private var contrast
    @Environment(\.playerLookOverride) private var lookOverride
    @FocusState private var searchFocused: Bool
    /// Not observed here: the bar redraws itself; the titles only follow
    /// `ScrubActivity`, which changes when hover or a drag starts and ends.
    @State private var scrub: ScrubController
    /// The rise on appearing is only set while hidden, so leaving is a plain fade.
    @State private var riseArmed = true
    @State private var noteTrigger = 0
    @State private var playToggle = OptimisticToggle()
    @State private var toggleRefresh = 0
    /// The queue row a Next press moved to before the player answered.
    @State private var optimisticCurrent: String?
    @State private var pendingCoverToggle: Task<Void, Never>?
    /// The music video is being lined up: its bar's capsule is up for good,
    /// so the titles make way meanwhile.
    @State private var aligningLive = false

    init(model: DesktopPlayerModel, displayID: CGDirectDisplayID, scrub: ScrubController? = nil) {
        self.model = model
        self.displayID = displayID
        _scrub = State(initialValue: scrub ?? ScrubController(displayID: displayID))
    }

    private var look: PlayerLook {
        lookOverride ?? PlayerLook(reduceMotion: systemReduceMotion, reduceTransparency: systemReduceTransparency,
                                   increaseContrast: contrast == .increased)
    }

    /// The song as drawn: a preview's, else the monitor's.
    private var now: PlayerNow {
        if let fixture = preview?.now { return fixture }
        return PlayerNow(track: nowPlaying.current, isPlaying: nowPlaying.isPlaying, player: nowPlaying.activePlayer,
                         artwork: nowPlaying.currentTrackArtwork, canControl: nowPlaying.canControlPlayback,
                         idleTitle: nowPlaying.idleTitle)
    }

    private var playingKind: PlayerKind { now.player ?? model.player }
    private var aligning: Bool { preview.map { $0.align != nil } ?? aligningLive }

    private var pane: DesktopPlayerModel.Pane {
        model.showsSearch ? .search : model.showsQueue ? .queue : .song
    }

    var body: some View {
        let shown = preview != nil || hover.showsPlayer(on: displayID, searching: model.isPinned)
        let look = self.look
        let now = self.now
        let palette = ArtworkPalette.of(now.track == nil ? nil : now.artwork)
        GeometryReader { proxy in
            VStack(spacing: 0) {
                Color.clear.frame(height: Self.cardTop(panelHeight: proxy.size.height))
                card(now: now, palette: palette, shown: shown)
                Spacer(minLength: 0)
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
        }
        .foregroundStyle(.white)
        .environment(\.colorScheme, .dark)
        .environment(\.playerLook, look)
        .environment(\.playerPalette, palette)
        .opacity(shown ? 1 : 0)
        .offset(y: shown || !riseArmed || look.reduceMotion ? 0 : 8)
        .animation(PlayerMotion.reveal(shown: shown, reduceMotion: look.reduceMotion), value: shown)
        .animation(look.reduceMotion ? .easeInOut(duration: 0.15) : .smooth(duration: 0.32), value: pane)
        .onChange(of: shown) { _, now in shownChanged(now) }
        .onChange(of: searchFocused) { _, focused in
            guard !maskRender, preview == nil else { return }
            PlayerInteraction.shared.searchFocused = focused
            if !focused { ControlPanelController.shared.releaseKeyboard() }
        }
        .onChange(of: contextKey, initial: true) { _, _ in
            if !maskRender && preview == nil { scrub.update(scrubContext) }
        }
        .onChange(of: now.isPlaying) { _, playing in
            playToggle.reconcile(actual: playing, now: Self.uptime)
        }
        .onChange(of: model.playNoteToken) { _, _ in noteTrigger += 1 }
        .onChange(of: model.queue) { _, queue in
            if let optimisticCurrent, queue.first(where: \.isCurrent)?.id == optimisticCurrent { self.optimisticCurrent = nil }
        }
        .onReceive(alignPublisher) { aligningLive = $0 }
        .onAppear { if !maskRender && preview == nil { registerKeys() } }
        .onDisappear { if !maskRender && preview == nil { PlayerKeyboard.shared.unregister(displayID) } }
    }

    private var alignPublisher: AnyPublisher<Bool, Never> {
        guard preview == nil else { return Just(false).eraseToAnyPublisher() }
        return MusicVideoController.shared.$align.map { $0 != nil }.removeDuplicates().eraseToAnyPublisher()
    }

    private static var uptime: Double { ProcessInfo.processInfo.systemUptime }

    private func shownChanged(_ now: Bool) {
        guard !maskRender, preview == nil else { return }
        if now {
            riseArmed = false
            model.refreshQueue()
            registerKeys()
        } else {
            searchFocused = false
            scrub.pointerExited()
            // The queue closes with the player; a typed search stays.
            model.closePane()
            // Re-armed out of sight, after the fade.
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 260_000_000)
                guard !hover.showsPlayer(on: displayID, searching: model.isPinned) else { return }
                var transaction = Transaction()
                transaction.disablesAnimations = true
                withTransaction(transaction) { riseArmed = true }
            }
            ControlPanelController.shared.releaseKeyboard()
        }
    }

    // MARK: Seek context

    private var canSeek: Bool { now.player != nil && now.canControl }

    private var trackKey: String {
        guard let track = now.track else { return "" }
        return "\(track.title)|\(track.artist)|\(track.persistentID ?? "")|\(Int(track.duration))"
    }

    private var lyricOffset: Double {
        Settings.shared.lyricOffset + SongOffsets.shared.offset(for: lyrics.currentQuery)
    }

    /// Changes only when what the bar draws for the song does.
    private struct ContextKey: Equatable {
        var track: String
        var duration: Double
        var canSeek: Bool
        var lines: Int
        var first: Double
        var last: Double
        var offset: Double
        var display: RomanizationOptions.Display
        var readings: Int
    }

    private var contextKey: ContextKey {
        // A preview brings its own bar, and never reads the app's lyrics or settings.
        guard preview == nil else {
            return ContextKey(track: "", duration: 0, canSeek: false, lines: 0, first: 0, last: 0, offset: 0,
                              display: .off, readings: 0)
        }
        return ContextKey(track: trackKey, duration: now.track?.duration ?? 0, canSeek: canSeek,
                   lines: lyrics.lines.count, first: lyrics.lines.first?.start ?? 0, last: lyrics.lines.last?.start ?? 0,
                   offset: lyricOffset, display: readings.display, readings: lyrics.romanized.count)
    }

    private var scrubContext: ScrubController.Context {
        let track = now.track
        let readingsOn = readings.display != .off
        let romanized: [Int: String] = readingsOn ? lyrics.romanized.mapValues { words in
            words.map { $0.text.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }.joined(separator: " ")
        } : [:]
        let lines = ScrubLine.lines(from: lyrics.lines, offset: lyricOffset, romanized: romanized)
        return ScrubController.Context(duration: track?.duration ?? 0, trackKey: trackKey,
                                       lines: readingsOn ? lines : lines.map { var line = $0; line.romanized = nil; return line },
                                       canSeek: canSeek && !(track?.isStream ?? true) && !(track?.isAdvert ?? true),
                                       romanization: readings.display)
    }

    // MARK: Card

    private func card(now: PlayerNow, palette: ArtworkPalette, shown: Bool) -> some View {
        let pane = self.pane
        return HStack(alignment: .top, spacing: Self.padding) {
            artColumn(now: now, palette: palette, compact: pane != .song)
            Group {
                switch pane {
                case .song: songColumn(now: now, palette: palette, shown: shown)
                case .search: searchColumn
                case .queue: queueColumn(now: now)
                }
            }
            .frame(width: Self.column, height: Self.columnHeight(pane, queueRows: model.queue.count), alignment: .topLeading)
            .transition(.opacity)
        }
        .padding(Self.padding)
        .frame(width: Self.width, alignment: .topLeading)
        .background(SleeveCardBackground(palette: palette, hasSong: now.track != nil))
        .overlay(alignment: .top) {
            PlayerNoteToast(note: model.playNote, trigger: noteTrigger, alwaysShown: preview != nil)
                .alignmentGuide(.top) { $0[.bottom] + 10 }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Lunalyr player")
    }

    // MARK: Art column

    private func artColumn(now: PlayerNow, palette: ArtworkPalette, compact: Bool) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            ZStack(alignment: .leading) {
                if let track = now.track, !track.isAdvert {
                    // The record slides back into the sleeve on pause; under
                    // Reduce Motion it stays where it is.
                    let out = displayedPlaying || look.reduceMotion
                    SleeveRecord(size: Self.cover - 6, palette: palette)
                        .offset(x: out ? 40 : 14)
                        .animation(look.reduceMotion ? nil : .spring(duration: 0.55, bounce: 0.1), value: out)
                        .accessibilityHidden(true)
                }
                coverButton(now: now)
            }
            .frame(width: Self.artWidth, height: Self.cover, alignment: .leading)
            if compact {
                VStack(alignment: .leading, spacing: 2) {
                    Text(now.track?.title ?? now.idleTitle).font(.system(size: 13, weight: .bold)).lineLimit(1)
                    if let artist = now.track?.artist, !artist.isEmpty {
                        Text(artist).font(.system(size: 11)).foregroundStyle(.white.opacity(look.increaseContrast ? 0.9 : 0.7))
                            .lineLimit(1)
                    }
                }
                .frame(width: Self.cover, alignment: .leading)
                transport(.small)
            }
        }
        .frame(width: Self.artWidth, alignment: .topLeading)
    }

    private func coverButton(now: PlayerNow) -> some View {
        Button(action: coverClicked) {
            coverImage(now: now)
                .frame(width: Self.cover, height: Self.cover)
                .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .strokeBorder(.white.opacity(look.increaseContrast ? 0.5 : 0.12), lineWidth: 0.5))
                .shadow(color: .black.opacity(0.45), radius: 10, x: 4, y: 6)
                .contentShape(Rectangle())
        }
        .buttonStyle(CoverButtonStyle())
        .disabled(now.track == nil)
        .help(now.track == nil ? "" : "Click to play or pause, double-click to open \(playingKind.shortName)")
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(now.track.map { $0.artist.isEmpty ? "Cover, \($0.title)" : "Cover, \($0.title) by \($0.artist)" }
                            ?? now.idleTitle)
        .accessibilityHint(now.track == nil ? "" : "Plays or pauses. Double-click opens \(playingKind.shortName)")
        .accessibilityAddTraits(.isButton)
        .accessibilityAction(named: displayedPlaying ? "Pause" : "Play") { playPause() }
        .accessibilityAction(named: "Open \(playingKind.shortName)") { openPlayerApp() }
    }

    @ViewBuilder
    private func coverImage(now: PlayerNow) -> some View {
        if now.track != nil, let artwork = now.artwork {
            Image(nsImage: artwork).resizable().aspectRatio(contentMode: .fill)
        } else {
            ZStack {
                Color.white.opacity(0.1)
                Image(systemName: now.track?.isStream == true ? "dot.radiowaves.left.and.right" : "music.note")
                    .font(.system(size: 40)).foregroundStyle(.white.opacity(0.4))
            }
        }
    }

    /// A click on the cover plays or pauses; a double-click brings the
    /// player's app forward instead (Sleeve's convention). The single click
    /// waits out a short double-click window for that.
    private func coverClicked() {
        PlayerInteraction.shared.noteClick()
        if (NSApp.currentEvent?.clickCount ?? 1) >= 2 {
            pendingCoverToggle?.cancel()
            pendingCoverToggle = nil
            openPlayerApp()
            return
        }
        pendingCoverToggle?.cancel()
        let wait = min(NSEvent.doubleClickInterval, 0.3)
        pendingCoverToggle = Task { @MainActor in
            try? await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000))
            guard !Task.isCancelled else { return }
            playPause()
        }
    }

    private func openPlayerApp() {
        guard !AppRuntime.isUnitTesting, preview == nil,
              let kind = nowPlaying.activePlayer ?? nowPlaying.transportTarget else { return }
        NSRunningApplication.runningApplications(withBundleIdentifier: kind.bundleID).first?.activate()
    }

    // MARK: Song column

    private func songColumn(now: PlayerNow, palette: ArtworkPalette, shown: Bool) -> some View {
        let kind = ScrubRowKind.of(track: now.track, canSeek: canSeek)
        return VStack(alignment: .leading, spacing: 0) {
            playerLabel
            ScrubActivityReader(activity: scrub.activity) { activity in
                titles(now: now)
                    .opacity(Self.titleOpacity(dimmed: (activity.capsulesUp && kind == .seekable) || aligning))
                    .animation(activity.capsulesUp ? .easeIn(duration: 0.10) : .easeOut(duration: 0.14),
                               value: activity.capsulesUp)
            }
            Spacer(minLength: 0)
            if kind != .hidden || aligning {
                seekRow(kind: kind, shown: shown)
            }
            alignOrControls(kind: kind)
                .padding(.top, 8)
        }
    }

    /// The player that is playing, small, over the song. A click opens the
    /// search, where the switch is.
    private var playerLabel: some View {
        Button { openSearch() } label: {
            HStack(spacing: 5) {
                Circle().fill(playingKind.tint).frame(width: 6, height: 6)
                Text(playingKind.shortName.uppercased()).font(.system(size: 10, weight: .bold)).tracking(1.2)
                Image(systemName: "chevron.down").font(.system(size: 8, weight: .bold))
            }
            .foregroundStyle(.white.opacity(look.increaseContrast ? 0.9 : 0.7))
            .padding(.vertical, 2)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("Search \(model.player.shortName), or switch the player search uses")
        .accessibilityLabel("Playing in \(playingKind.shortName)")
        .accessibilityHint("Opens search, where you choose the player to search")
    }

    private func titles(now: PlayerNow) -> some View {
        let track = now.track
        let artist = track?.artist ?? "Search to start something"
        let album = track?.album.flatMap { $0.isEmpty || $0 == track?.title ? nil : $0 }
        return VStack(alignment: .leading, spacing: 0) {
            Text(track?.title ?? now.idleTitle)
                .font(.system(size: 25, weight: .bold))
                .lineLimit(1).minimumScaleFactor(0.75)
                .padding(.top, 8)
            if !artist.isEmpty {
                Text(artist).font(.system(size: 15, weight: .medium))
                    .foregroundStyle(.white.opacity(look.increaseContrast ? 0.95 : 0.8)).lineLimit(1)
                    .padding(.top, 1)
            }
            if let album {
                Text(album).font(.system(size: 12))
                    .foregroundStyle(.white.opacity(look.increaseContrast ? 0.85 : 0.55)).lineLimit(1)
                    .padding(.top, 1)
            }
        }
        .accessibilityElement(children: .combine)
    }

    @ViewBuilder
    private func seekRow(kind: ScrubRowKind, shown: Bool) -> some View {
        let accent = palette.fromArtwork ? palette.accentColor : playingKind.tint
        if let preview {
            if let align = preview.align {
                VideoAlignRow(display: align, tint: accent)
            } else {
                ScrubRow(controller: scrub, kind: kind, tint: accent, isPlaying: displayedPlaying, shown: shown,
                         showsRemaining: $model.showsRemaining)
            }
        } else {
            SeekOrAlignRow(musicVideo: MusicVideoController.shared, scrub: scrub, kind: kind, tint: accent,
                           isPlaying: displayedPlaying, shown: shown, showsRemaining: $model.showsRemaining)
        }
    }

    private var palette: ArtworkPalette { ArtworkPalette.of(now.track == nil ? nil : now.artwork) }

    /// The transport row, or Reset / Cancel / Done while the music video is lined up.
    @ViewBuilder
    private func alignOrControls(kind: ScrubRowKind) -> some View {
        let accent = palette.fromArtwork ? palette.accentColor : playingKind.tint
        if let preview {
            if preview.align != nil {
                VideoAlignControls(canReset: preview.align?.origin == .manual, tint: accent,
                                   onReset: {}, onCancel: {}, onDone: {})
            } else {
                controlsRow(kind: kind)
            }
        } else {
            ControlsOrAlign(musicVideo: MusicVideoController.shared, tint: accent) { controlsRow(kind: kind) }
        }
    }

    // MARK: Transport

    private var displayedPlaying: Bool {
        _ = toggleRefresh
        return playToggle.displayed(actual: now.isPlaying, now: Self.uptime)
    }

    /// The glyph flips now; the player's answer settles it, or after 1.5 s
    /// the glyph goes back to the truth.
    private func playPause() {
        guard preview == nil else { return }
        playToggle.press(actual: nowPlaying.isPlaying, now: Self.uptime)
        nowPlaying.playPause()
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: UInt64((OptimisticToggle.timeout + 0.05) * 1_000_000_000))
            playToggle.reconcile(actual: nowPlaying.isPlaying, now: Self.uptime)
            toggleRefresh += 1
        }
    }

    /// Next moves the queue's highlight at once when what comes next is
    /// known (Music, shuffle off), and the bar goes to 0:00. Otherwise the
    /// press shows and the real change moves things, so nothing shows a guess.
    private func skip(forward: Bool) {
        guard preview == nil, nowPlaying.current != nil else { return }
        if forward {
            if let next = DesktopPlayerModel.optimisticNext(queue: model.queue, shuffleOn: model.shuffleOn,
                                                             kind: nowPlaying.activePlayer) {
                optimisticCurrent = next
                scrub.showStart()
                Task { @MainActor in
                    try? await Task.sleep(nanoseconds: 3_000_000_000)
                    if optimisticCurrent == next { optimisticCurrent = nil }
                }
            }
            nowPlaying.nextTrack()
        } else {
            nowPlaying.previousTrack()
        }
    }

    private func transport(_ size: PlayerTransport.Size) -> some View {
        PlayerTransport(size: size, canControl: now.canControl, hasSong: now.track != nil, isPlaying: displayedPlaying,
                        onPrevious: { skip(forward: false) }, onPlayPause: playPause, onNext: { skip(forward: true) })
    }

    private func controlsRow(kind: ScrubRowKind) -> some View {
        PlayerControlsRow(searchOpen: pane == .search, queueOpen: pane == .queue,
                          onSearch: { openSearch(toggle: true) }, onQueue: { model.toggle(.queue) }) {
            transport(.large)
        } film: {
            filmButton(kind: kind)
        }
    }

    @ViewBuilder
    private func filmButton(kind: ScrubRowKind) -> some View {
        let accent = palette.fromArtwork ? palette.accentColor : playingKind.tint
        if let preview {
            if preview.musicVideo {
                VideoAlignButton(on: preview.align != nil, reason: { nil }, tint: accent) {}
            }
        } else {
            LiveAlignButton(musicVideo: MusicVideoController.shared, kind: kind, tint: accent)
        }
    }

    private func openSearch(toggle: Bool = false) {
        if toggle { model.toggle(.search) } else { model.pane = .search }
        if model.pane == .search { searchFocused = true }
    }

    private var switchKinds: [PlayerKind] {
        if let preview { return preview.kinds }
        return DesktopPlayerModel.switchKinds(
            installed: DesktopPlayerModel.installed, running: nowPlaying.runningPlayers,
            followed: nowPlaying.followedPlayers,
            webSeen: Set(PlayerKind.browsers.filter { WebTabMemory.shared.entry(for: $0) != nil }),
            active: nowPlaying.activePlayer, selected: model.player)
    }

    private func registerKeys() {
        let model = model, focus = $searchFocused
        PlayerKeyboard.shared.register(.init(
            scrub: scrub,
            playPause: { playPause() },
            track: { skip(forward: $0) },
            focusSearch: {
                model.pane = .search
                focus.wrappedValue = true
            },
            clearSearch: {
                if !model.query.isEmpty || focus.wrappedValue {
                    model.query = ""
                    focus.wrappedValue = false
                    model.pane = .song
                    return true
                }
                guard model.pane != .song else { return false }
                model.pane = .song
                return true
            },
            hasResults: { model.isSearching && !model.results.isEmpty },
            moveSelection: { model.moveSelection(by: $0) },
            playSelection: { model.playSelection() }), for: displayID)
    }

    // MARK: Search column

    private var searchColumn: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                PlayerPicker(kinds: switchKinds, selected: model.player) { model.player = $0 }
                Spacer(minLength: 0)
                PaneCloseButton(label: "Close search") {
                    model.query = ""
                    searchFocused = false
                    model.pane = .song
                }
            }
            PlayerSearchField(query: $model.query, focused: $searchFocused, player: model.player,
                              shownFocused: preview?.searchFocused ?? searchFocused) { model.playSelection() }
            PlayerResultsList(results: model.results, status: model.status, selection: model.selection,
                              player: model.player) { model.play($0) }
        }
        .onAppear { if !maskRender && preview == nil { searchFocused = true } }
    }

    // MARK: Queue column

    private func queueColumn(now: PlayerNow) -> some View {
        let items: [PlayerQueueItem]
        var footnote: String?
        if model.queue.isEmpty, let track = now.track {
            // A player that shares no queue (Spotify): just what is playing.
            items = [PlayerQueueItem(id: "now", title: track.title, artist: track.artist, index: 0, isCurrent: true)]
            footnote = DesktopPlayerModel.emptyQueueMessage(for: playingKind)
        } else {
            items = model.queue
            if items.isEmpty { footnote = DesktopPlayerModel.emptyQueueMessage(for: playingKind) }
        }
        return VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Text("QUEUE").font(.system(size: 10, weight: .bold)).tracking(1.2)
                Spacer(minLength: 0)
                if model.shuffleOn, !model.queue.isEmpty {
                    Label("Playlist order, shuffle is on", systemImage: "shuffle")
                        .font(.system(size: 10))
                        .help("Shuffle is on: this is the playlist\u{2019}s order, not the order songs will play in")
                }
                PaneCloseButton(label: "Close queue") { model.pane = .song }
            }
            .foregroundStyle(.white.opacity(look.increaseContrast ? 0.9 : 0.65))
            PlayerQueueList(items: items, optimisticCurrent: optimisticCurrent, isPlaying: displayedPlaying,
                            artwork: now.artwork, footnote: footnote, onTap: queueTapped)
        }
    }

    private func queueTapped(_ item: PlayerQueueItem) {
        PlayerInteraction.shared.noteClick()
        let isCurrent = optimisticCurrent.map { $0 == item.id } ?? item.isCurrent
        if isCurrent { playPause() } else { model.play(item) }
    }
}

/// Redraws its content only when hover over the bar, a drag or a key's
/// time capsule starts or ends.
private struct ScrubActivityReader<Content: View>: View {
    @ObservedObject var activity: ScrubActivity
    @ViewBuilder let content: (_ activity: ScrubActivity) -> Content

    var body: some View { content(activity) }
}

/// Reveal and dismiss: in on a soft spring with no bounce, out quicker as a
/// plain fade. Under Reduce Motion, short fades.
enum PlayerMotion {
    static func reveal(shown: Bool, reduceMotion: Bool) -> Animation {
        if reduceMotion { return shown ? .easeOut(duration: 0.15) : .easeIn(duration: 0.12) }
        return shown ? .spring(duration: 0.32, bounce: 0) : .spring(duration: 0.20, bounce: 0)
    }
}

/// Set by previews drawn offscreen: made-up songs, covers by id and the
/// scene behind the glass; lists draw as plain rows (scroll views come out
/// blank in an image), and AppKit-backed pieces (mouse tracking, the text
/// field) are left out. Never set in the app.
struct PlayerPreview: Equatable {
    var covers: [String: NSImage] = [:]
    /// The song, instead of the monitor's.
    var now: PlayerNow?
    /// Lining up the music video: the video's bar, instead of the music video's.
    var align: MusicVideoAlignDisplay?
    /// A music video is the wallpaper (the film button shows).
    var musicVideo = false
    /// The players the switch offers.
    var kinds: [PlayerKind] = [.appleMusic, .spotify]
    /// The search field drawn with the keyboard in it.
    var searchFocused = false
    /// What the glass shows through, placed in the `PlayerPreview.space`
    /// coordinate space: an image can't draw the system's materials.
    var backdrop: AnyView?

    static let space = "playerPreviewScene"

    static func == (lhs: PlayerPreview, rhs: PlayerPreview) -> Bool {
        lhs.covers == rhs.covers && lhs.now == rhs.now && lhs.align == rhs.align && lhs.musicVideo == rhs.musicVideo
            && lhs.kinds == rhs.kinds && lhs.searchFocused == rhs.searchFocused
            && (lhs.backdrop == nil) == (rhs.backdrop == nil)
    }
}

private struct PlayerPreviewKey: EnvironmentKey { static let defaultValue: PlayerPreview? = nil }
extension EnvironmentValues {
    var playerPreview: PlayerPreview? {
        get { self[PlayerPreviewKey.self] }
        set { self[PlayerPreviewKey.self] = newValue }
    }
}

/// A cover gives a little on mouse-down, so the press is felt at once even
/// when the click waits to see whether it is a double-click.
struct CoverButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.97 : 1)
            .animation(configuration.isPressed ? .spring(duration: 0.10, bounce: 0) : .spring(duration: 0.30, bounce: 0.15),
                       value: configuration.isPressed)
    }
}

/// A song's own cover, straight from the Music library: the exact artwork of
/// that track, not a guess by name. Covers asked for together (a strip
/// coming into view) are read in one script; a cover scrolled away before its
/// turn is not read at all.
enum LibraryArtwork {
    private static let cache: NSCache<NSString, NSImage> = {
        let cache = NSCache<NSString, NSImage>()
        cache.countLimit = 160
        return cache
    }()
    private static let batcher = ArtworkBatcher(desk: .shared)
    /// Covers are drawn at `DesktopPlayerView.cover` points; a library
    /// cover can be thousands of pixels, decoded on the main thread.
    static let maxPixelSize = 400

    static func image(persistentID id: String) async -> NSImage? {
        if let hit = cache.object(forKey: id as NSString) { return hit }
        guard let data = await batcher.data(for: id), data.count > 100,
              let decoded = await ArtworkImage.decodeDetached(data, maxPixelSize: maxPixelSize) else { return nil }
        let image = ArtworkImage.image(decoded)
        cache.setObject(image, forKey: id as NSString)
        return image
    }

    /// Covers read at most per script, so one reply stays a sensible size.
    static let batchSize = 8

    /// Reads the artwork of each track by persistent ID. A track that is
    /// gone or has no artwork is left out; anything worse (a timeout) ends
    /// the script, rather than waiting for every cover in turn.
    static func script(for ids: [String]) -> PlayerScript {
        let list = ids.map(ScriptText.quoted).joined(separator: ", ")
        let source = PlayerScriptSource.query(AppleMusicSearch.bundleID, """
        set found to {}
        repeat with idItem in {\(list)}
        set wantedID to contents of idItem
        try
        set t to first track of library playlist 1 whose persistent ID is wantedID
        if (count of artworks of t) > 0 then set end of found to {wantedID, data of artwork 1 of t}
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        end try
        end repeat
        return {"ok", found}
        """)
        return PlayerScript(name: "desktop-player.artwork", source: source, cacheable: false)
    }

    static func parse(_ reply: NSAppleEventDescriptor) -> [String: Data] {
        let items = reply.scriptItems
        guard items.first?.scriptText == "ok", items.count >= 2 else { return [:] }
        var covers: [String: Data] = [:]
        for row in items[1].scriptItems {
            let fields = row.scriptItems
            guard fields.count >= 2, let id = fields[0].scriptText else { continue }
            covers[id] = fields[1].data
        }
        return covers
    }
}

/// Gathers cover requests into batches for `ScriptDesk`.
final class ArtworkBatcher: @unchecked Sendable {
    private let desk: ScriptDesk
    private let lock = NSLock()
    /// Who waits for each track's cover.
    private var waiters: [String: [UUID: CheckedContinuation<Data?, Never>]] = [:]
    /// Tracks asked for, oldest first, not yet in a script.
    private var order: [String] = []
    private var inFlight: [String] = []
    private var scheduled = false

    init(desk: ScriptDesk) {
        self.desk = desk
    }

    func data(for id: String) async -> Data? {
        let token = UUID()
        return await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<Data?, Never>) in
                add(id, token, continuation)
            }
        } onCancel: {
            remove(id, token)
        }
    }

    private func add(_ id: String, _ token: UUID, _ continuation: CheckedContinuation<Data?, Never>) {
        if Task.isCancelled { continuation.resume(returning: nil); return }
        lock.lock()
        if waiters[id] == nil, !inFlight.contains(id) { order.append(id) }
        waiters[id, default: [:]][token] = continuation
        let start = !scheduled
        scheduled = true
        lock.unlock()
        if start { Task { await flush() } }
    }

    private func remove(_ id: String, _ token: UUID) {
        lock.lock()
        let continuation = waiters[id]?.removeValue(forKey: token)
        if waiters[id]?.isEmpty == true { waiters[id] = nil }
        lock.unlock()
        continuation?.resume(returning: nil)
    }

    /// The tracks still wanted, oldest first, for the script about to run.
    private func takeBatch() -> PlayerScript? {
        lock.lock(); defer { lock.unlock() }
        order.removeAll { waiters[$0] == nil }
        let ids = Array(order.prefix(LibraryArtwork.batchSize))
        order.removeFirst(ids.count)
        inFlight = ids
        return ids.isEmpty ? nil : LibraryArtwork.script(for: ids)
    }

    /// Hands each waiter its cover (nil when there was none); true while more are wanted.
    private func settle(_ covers: [String: Data]) -> Bool {
        lock.lock()
        var done: [(CheckedContinuation<Data?, Never>, Data?)] = []
        for id in inFlight {
            for continuation in (waiters.removeValue(forKey: id) ?? [:]).values { done.append((continuation, covers[id])) }
        }
        inFlight = []
        order.removeAll { waiters[$0] == nil }
        let more = !order.isEmpty
        if !more { scheduled = false }
        lock.unlock()
        for (continuation, data) in done { continuation.resume(returning: data) }
        return more
    }

    private func flush() async {
        // A strip comes into view a few covers at a time; let them gather.
        try? await Task.sleep(nanoseconds: 40_000_000)
        while true {
            let covers: [String: Data]
            if let reply = try? await desk.run(for: .appleMusic, priority: .artwork, polling: true, make: { [self] in takeBatch() }) {
                covers = LibraryArtwork.parse(reply)
            } else {
                covers = [:]
            }
            let more = settle(covers)
            guard more else { return }
        }
    }
}

/// Covers found by name, once each, for songs that carry neither a cover
/// address nor a library id. Only a result whose title and artist match
/// exactly is accepted; otherwise the tile stays plain rather than show the
/// wrong album. Only the song's own title and artist are sent.
enum CoverLookup {
    private static let cache = NSCache<NSString, NSURL>()

    /// Case, accents, spaces and punctuation ignored.
    static func normal(_ text: String) -> String {
        text.folding(options: [.diacriticInsensitive, .caseInsensitive], locale: nil)
            .components(separatedBy: CharacterSet.alphanumerics.inverted).joined()
    }

    static func url(title: String, artist: String) async -> URL? {
        let key = "\(title)|\(artist)".lowercased() as NSString
        if let hit = cache.object(forKey: key) { return hit as URL }
        guard let url = AppleMusicSearch.searchURL("\(title) \(artist)", entity: "song", limit: 8),
              let (data, _) = try? await URLSession.shared.data(from: url) else { return nil }
        let wantTitle = normal(title), wantArtist = normal(artist)
        let match = AppleMusicSearch.parseCatalogue(data).first {
            $0.kind == .song && normal($0.title) == wantTitle
                && (wantArtist.isEmpty || normal($0.artist).contains(wantArtist) || wantArtist.contains(normal($0.artist)))
        }
        guard let art = match?.artworkURL else { return nil }
        let large = URL(string: art.absoluteString.replacingOccurrences(of: "200x200", with: "400x400")) ?? art
        cache.setObject(large as NSURL, forKey: key)
        return large
    }
}

/// A cover that downloads itself, with a plain tile while it does. Order of
/// trust: its own address, the library's artwork for that exact track, then
/// an exact-match lookup by name.
struct RemoteCover: View {
    let url: URL?
    let symbol: String
    var lookup: (String, String)?
    var persistentID: String?
    @State private var image: NSImage?

    var body: some View {
        ZStack {
            Rectangle().fill(.white.opacity(0.1))
            if let image {
                Image(nsImage: image).resizable().aspectRatio(contentMode: .fill).transition(.opacity)
            } else {
                Image(systemName: symbol).foregroundStyle(.white.opacity(0.4))
            }
        }
        .animation(.easeOut(duration: 0.25), value: image)
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .task(id: "\(url?.absoluteString ?? "")|\(persistentID ?? "")|\(lookup?.0 ?? "")|\(lookup?.1 ?? "")") {
            image = nil
            if url == nil, let persistentID, let art = await LibraryArtwork.image(persistentID: persistentID) {
                image = art
                return
            }
            guard !Task.isCancelled else { return }
            var address = url
            if address == nil, let lookup { address = await CoverLookup.url(title: lookup.0, artist: lookup.1) }
            guard let address, let (data, _) = try? await URLSession.shared.data(from: address) else { return }
            image = NSImage(data: data)
        }
    }
}

struct RowButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .background(configuration.isPressed ? Color.white.opacity(0.14) : Color.clear, in: RoundedRectangle(cornerRadius: 8))
    }
}
