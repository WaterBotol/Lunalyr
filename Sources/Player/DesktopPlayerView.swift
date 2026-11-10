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

/// The player that lives on the desktop: a strip of covers with what played,
/// what plays now (in the middle) and what plays next; under it the seek bar,
/// then one small line to pick the player, play, pause, skip and search.
/// Searching swaps the strip for the results.
struct DesktopPlayerView: View {
    static let width: CGFloat = 720
    static let stripHeight: CGFloat = 214
    static let resultsHeight: CGFloat = 372
    static let cover: CGFloat = 150

    @ObservedObject var model: DesktopPlayerModel
    let displayID: CGDirectDisplayID
    @ObservedObject private var hover = HoverState.shared
    @EnvironmentObject private var nowPlaying: NowPlayingMonitor
    @ObservedObject private var lyrics = LyricsViewModel.shared
    @ObservedObject private var readings = RomanizationOptions.shared
    @Environment(\.playerMaskRender) private var maskRender
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion
    @Environment(\.accessibilityReduceTransparency) private var systemReduceTransparency
    @Environment(\.colorSchemeContrast) private var contrast
    @Environment(\.playerLookOverride) private var lookOverride
    @FocusState private var searchFocused: Bool
    /// Not observed here: the bar redraws itself; the strip only follows
    /// `ScrubActivity`, which changes when hover or a drag starts and ends.
    @State private var scrub: ScrubController
    /// Bumped whenever the player appears or goes away, so the strip returns to
    /// the song that is playing.
    @State private var recenter = 0
    /// The appearing scale (0.97) is only set while hidden, so leaving is a
    /// plain fade.
    @State private var scaleArmed = true
    @State private var noteTrigger = 0
    @State private var playToggle = OptimisticToggle()
    @State private var toggleRefresh = 0
    /// The strip item a Next press slid to before the player answered.
    @State private var optimisticCurrent: String?
    @State private var pendingCoverToggle: Task<Void, Never>?
    /// The music video is being lined up: its bar's capsule is up for good,
    /// so the strip's titles and side covers make way meanwhile.
    @State private var aligning = false

    init(model: DesktopPlayerModel, displayID: CGDirectDisplayID) {
        self.model = model
        self.displayID = displayID
        _scrub = State(initialValue: ScrubController(displayID: displayID))
    }

    private var look: PlayerLook {
        lookOverride ?? PlayerLook(reduceMotion: systemReduceMotion, reduceTransparency: systemReduceTransparency,
                                   increaseContrast: contrast == .increased)
    }

    var body: some View {
        let shown = hover.showsPlayer(on: displayID, searching: model.isPinned)
        let look = self.look
        let rowKind = model.isSearching ? .hidden : ScrubRowKind.of(track: nowPlaying.current, canSeek: canSeek)
        PlayerColumn(scrubKind: rowKind) {
            top(shown: shown)
        } scrub: {
            SeekOrAlignRow(musicVideo: MusicVideoController.shared, scrub: scrub, kind: rowKind,
                           tint: model.playingKind.tint, isPlaying: displayedPlaying, shown: shown,
                           showsRemaining: $model.showsRemaining)
        } controls: {
            ControlsOrAlign(musicVideo: MusicVideoController.shared, tint: model.playingKind.tint) { controls }
        }
        .foregroundStyle(.white)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .environment(\.colorScheme, .dark)
        .environment(\.playerLook, look)
        .opacity(shown ? 1 : 0)
        .scaleEffect(shown || !scaleArmed || look.reduceMotion ? 1 : 0.97)
        .animation(PlayerMotion.reveal(shown: shown, reduceMotion: look.reduceMotion), value: shown)
        .animation(look.reduceMotion ? .easeInOut(duration: 0.15) : .smooth(duration: 0.34), value: model.isSearching)
        .onChange(of: shown) { _, now in shownChanged(now) }
        .onChange(of: searchFocused) { _, focused in
            guard !maskRender else { return }
            PlayerInteraction.shared.searchFocused = focused
            if !focused { ControlPanelController.shared.releaseKeyboard() }
        }
        .onChange(of: contextKey, initial: true) { _, _ in
            if !maskRender { scrub.update(scrubContext) }
        }
        .onChange(of: nowPlaying.isPlaying) { _, playing in
            playToggle.reconcile(actual: playing, now: Self.uptime)
        }
        .onChange(of: model.queue) { _, queue in
            if let optimisticCurrent, queue.first(where: \.isCurrent)?.id == optimisticCurrent { self.optimisticCurrent = nil }
        }
        .onReceive(MusicVideoController.shared.$align.map { $0 != nil }.removeDuplicates()) { aligning = $0 }
        .onAppear { if !maskRender { registerKeys() } }
        .onDisappear { if !maskRender { PlayerKeyboard.shared.unregister(displayID) } }
    }

    private static var uptime: Double { ProcessInfo.processInfo.systemUptime }

    private func shownChanged(_ now: Bool) {
        recenter += 1
        if now {
            scaleArmed = false
            if model.takeShuffleNote() { noteTrigger += 1 }
            guard !maskRender else { return }
            model.refreshQueue()
            registerKeys()
        } else {
            searchFocused = false
            scrub.pointerExited()
            // Re-armed out of sight, after the fade.
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 260_000_000)
                guard !hover.showsPlayer(on: displayID, searching: model.isPinned) else { return }
                var transaction = Transaction()
                transaction.disablesAnimations = true
                withTransaction(transaction) { scaleArmed = true }
            }
            if !maskRender { ControlPanelController.shared.releaseKeyboard() }
        }
    }

    // MARK: Seek context

    private var canSeek: Bool { nowPlaying.activePlayer != nil && nowPlaying.canControlPlayback }

    private var trackKey: String {
        guard let track = nowPlaying.current else { return "" }
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
        ContextKey(track: trackKey, duration: nowPlaying.current?.duration ?? 0, canSeek: canSeek,
                   lines: lyrics.lines.count, first: lyrics.lines.first?.start ?? 0, last: lyrics.lines.last?.start ?? 0,
                   offset: lyricOffset, display: readings.display, readings: lyrics.romanized.count)
    }

    private var scrubContext: ScrubController.Context {
        let track = nowPlaying.current
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

    // MARK: Strip

    @ViewBuilder
    private func top(shown: Bool) -> some View {
        if model.isSearching {
            resultsList
                .frame(width: Self.width, height: Self.resultsHeight)
                .glass(radius: 26, tint: model.player.tint, look: look)
        } else {
            ScrubActivityReader(activity: scrub.activity) { activity in
                // The bar's capsules rise over the titles under the covers:
                // the titles make way while they are up.
                strip(shown: shown, labelsDimmed: activity.capsulesUp || aligning,
                      sidesDimmed: activity.dragging || aligning)
            }
            .frame(width: Self.width, height: Self.stripHeight)
        }
    }

    @ViewBuilder
    private func strip(shown: Bool, labelsDimmed: Bool, sidesDimmed: Bool) -> some View {
        let paused = nowPlaying.current != nil && !displayedPlaying
        if maskRender {
            Color.black.opacity(0.3)
        } else if model.queue.isEmpty, let now = nowPlaying.current {
            // A player that shares no queue (Spotify): just what is playing.
            CoverStrip(items: [PlayerQueueItem(id: "now", title: now.title, artist: now.artist, index: 0, isCurrent: true)],
                       tint: model.playingKind.tint, artwork: nowPlaying.currentTrackArtwork, recenter: recenter,
                       shown: shown, paused: paused, labelsDimmed: labelsDimmed, sidesDimmed: sidesDimmed,
                       onTap: coverTapped)
        } else if model.queue.isEmpty {
            EmptyPlayerTile(title: nowPlaying.idleTitle)
        } else {
            CoverStrip(items: model.queue, tint: model.playingKind.tint, artwork: nowPlaying.currentTrackArtwork,
                       recenter: recenter, note: model.shuffleOn ? "Shuffle is on: this is the playlist\u{2019}s order" : nil,
                       noteTrigger: noteTrigger, shown: shown, paused: paused, labelsDimmed: labelsDimmed,
                       sidesDimmed: sidesDimmed, optimisticCurrent: optimisticCurrent, onTap: coverTapped)
        }
    }

    /// A click on the playing cover plays or pauses; a double-click brings
    /// the player's app forward instead (Sleeve's convention). The single
    /// click waits out a short double-click window for that.
    private func coverTapped(_ entry: PlayerQueueItem) {
        PlayerInteraction.shared.noteClick()
        let isCurrent = optimisticCurrent.map { $0 == entry.id } ?? entry.isCurrent
        guard isCurrent else { model.play(entry); return }
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
        guard !AppRuntime.isUnitTesting, let kind = nowPlaying.activePlayer ?? nowPlaying.transportTarget else { return }
        NSRunningApplication.runningApplications(withBundleIdentifier: kind.bundleID).first?.activate()
    }

    // MARK: Transport

    private var displayedPlaying: Bool {
        _ = toggleRefresh
        return playToggle.displayed(actual: nowPlaying.isPlaying, now: Self.uptime)
    }

    /// The glyph flips now; the player's answer settles it, or after 1.5 s
    /// the glyph goes back to the truth.
    private func playPause() {
        playToggle.press(actual: nowPlaying.isPlaying, now: Self.uptime)
        nowPlaying.playPause()
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: UInt64((OptimisticToggle.timeout + 0.05) * 1_000_000_000))
            playToggle.reconcile(actual: nowPlaying.isPlaying, now: Self.uptime)
            toggleRefresh += 1
        }
    }

    /// Next slides the strip at once when what comes next is known (Music,
    /// shuffle off), and the bar goes to 0:00. Otherwise the press shows and
    /// the real change moves things, so the strip never shows a guess.
    private func skip(forward: Bool) {
        guard nowPlaying.current != nil else { return }
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

    private var controls: some View {
        PlayerControlsRow(
            kinds: switchKinds, selected: model.player, onSelect: { model.player = $0 },
            canControl: nowPlaying.canControlPlayback, hasSong: nowPlaying.current != nil,
            isPlaying: displayedPlaying,
            onPrevious: { skip(forward: false) }, onPlayPause: playPause, onNext: { skip(forward: true) },
            query: $model.query, searchFocused: $searchFocused, onSubmit: { model.playSelection() })
    }

    private var switchKinds: [PlayerKind] {
        DesktopPlayerModel.switchKinds(
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
            focusSearch: { focus.wrappedValue = true },
            clearSearch: {
                guard !model.query.isEmpty || focus.wrappedValue else { return false }
                model.query = ""
                focus.wrappedValue = false
                return true
            },
            hasResults: { model.isSearching && !model.results.isEmpty },
            moveSelection: { model.moveSelection(by: $0) },
            playSelection: { model.playSelection() }), for: displayID)
    }

    // MARK: Results

    @ViewBuilder
    private var resultsList: some View {
        if maskRender { Color.black.opacity(0.3) } else { resultsScroll }
    }

    private var resultsScroll: some View {
        PlayerResultsList(results: model.results, status: model.status, selection: model.selection,
                          player: model.player) { model.play($0) }
    }
}

/// The search results that replace the strip; the selected one (↑ ↓) is lit
/// and plays on Return.
struct PlayerResultsList: View {
    let results: [PlayerSearchResult]
    let status: DesktopPlayerModel.Status
    let selection: Int
    let player: PlayerKind
    let onPlay: (PlayerSearchResult) -> Void
    @Environment(\.playerPreview) private var preview

    var body: some View {
        if preview != nil {
            VStack(alignment: .leading, spacing: 2) { rows }.padding(10)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        } else {
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 2) { rows }.padding(10)
                }
                .onChange(of: selection) { _, index in
                    guard results.indices.contains(index) else { return }
                    proxy.scrollTo(results[index].id)
                }
            }
        }
    }

    @ViewBuilder
    private var rows: some View {
        statusLine
        ForEach(Array(results.enumerated()), id: \.element.id) { index, result in
            row(result, selected: index == selection).id(result.id)
        }
    }

    @ViewBuilder
    private var statusLine: some View {
        switch status {
        case .searching: Text("Searching\u{2026}").font(.system(size: 12)).foregroundStyle(.white.opacity(0.6)).padding(8)
        case .message(let text): Text(text).font(.system(size: 12)).foregroundStyle(.white.opacity(0.7)).padding(8)
        case .idle: EmptyView()
        }
    }

    private func row(_ result: PlayerSearchResult, selected: Bool) -> some View {
        Button { onPlay(result) } label: {
            HStack(spacing: 10) {
                Group {
                    if let image = preview?.covers[result.id] {
                        Image(nsImage: image).resizable().aspectRatio(contentMode: .fill)
                            .clipShape(RoundedRectangle(cornerRadius: 6))
                    } else if preview != nil {
                        RoundedRectangle(cornerRadius: 6).fill(.white.opacity(0.1))
                    } else {
                        RemoteCover(url: result.artworkURL, symbol: result.kind == .album ? "square.stack" : "music.note",
                                    lookup: result.artworkURL == nil && player == .appleMusic ? (result.title, result.artist) : nil,
                                    persistentID: { if case .libraryTrack(let id) = result.target { return id } else { return nil } }())
                    }
                }
                .frame(width: 40, height: 40)
                VStack(alignment: .leading, spacing: 1) {
                    Text(result.title).font(.system(size: 13, weight: .medium)).lineLimit(1)
                    Text(subtitle(result)).font(.system(size: 11)).foregroundStyle(.white.opacity(0.6)).lineLimit(1)
                }
                Spacer()
                if result.inLibrary {
                    Text("Library").font(.system(size: 10, weight: .semibold)).foregroundStyle(.white.opacity(0.5))
                } else if result.kind == .album {
                    Text("Album").font(.system(size: 10, weight: .semibold)).foregroundStyle(.white.opacity(0.5))
                }
            }
            .padding(6).contentShape(Rectangle())
            .background(selected ? Color.white.opacity(0.08) : Color.clear, in: RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(RowButtonStyle())
    }

    private func subtitle(_ result: PlayerSearchResult) -> String {
        if result.kind == .album || result.album.isEmpty || result.album == result.title { return result.artist }
        return "\(result.artist) \u{00B7} \(result.album)"
    }
}

/// Reveal and dismiss: in on a soft spring with no bounce, out quicker as a
/// plain fade. Under Reduce Motion, short fades.
enum PlayerMotion {
    static func reveal(shown: Bool, reduceMotion: Bool) -> Animation {
        if reduceMotion { return shown ? .easeOut(duration: 0.15) : .easeIn(duration: 0.12) }
        return shown ? .spring(duration: 0.32, bounce: 0) : .spring(duration: 0.20, bounce: 0)
    }
}

/// The player's three rows: the strip (or results), the seek bar, the
/// controls. The seek row folds away while searching or with no song.
struct PlayerColumn<Top: View, Scrub: View, Controls: View>: View {
    let scrubKind: ScrubRowKind
    @ViewBuilder let top: () -> Top
    @ViewBuilder let scrub: () -> Scrub
    @ViewBuilder let controls: () -> Controls

    var body: some View {
        let folded = scrubKind == .hidden
        VStack(spacing: 0) {
            top()
            scrub()
                .frame(height: folded ? 0 : ScrubRow.height)
                .opacity(folded ? 0 : 1)
                .padding(.top, folded ? 10 : 8)
            controls()
                .padding(.top, folded ? 0 : 6)
        }
    }
}

/// Nothing playing: one plain tile where the cover would be.
struct EmptyPlayerTile: View {
    let title: String

    var body: some View {
        VStack(spacing: 8) {
            RoundedRectangle(cornerRadius: 12).fill(.white.opacity(0.1))
                .overlay(Image(systemName: "music.note").font(.system(size: 34)).foregroundStyle(.white.opacity(0.4)))
                .frame(width: DesktopPlayerView.cover, height: DesktopPlayerView.cover)
            Text(title).font(.system(size: 12, weight: .medium)).foregroundStyle(.white.opacity(0.8))
        }
        .shadow(color: .black.opacity(0.6), radius: 4)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityElement(children: .combine)
    }
}

/// Redraws its content only when hover over the bar, a drag or a key's
/// time capsule starts or ends.
private struct ScrubActivityReader<Content: View>: View {
    @ObservedObject var activity: ScrubActivity
    @ViewBuilder let content: (_ activity: ScrubActivity) -> Content

    var body: some View { content(activity) }
}

private struct StripOffsetKey: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = nextValue() }
}

/// Turns the mouse wheel's up and down into sideways scrolling over the
/// panel's strip, which only listens to sideways movement on its own.
private final class WheelBridge {
    private var monitor: Any?

    func start() {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .scrollWheel) { event in
            guard let window = event.window, window.title == ControlPanelController.windowTitle,
                  event.scrollingDeltaX == 0, event.scrollingDeltaY != 0, !event.hasPreciseScrollingDeltas,
                  var view = window.contentView?.hitTest(event.locationInWindow) else { return event }
            while !(view is NSScrollView) {
                guard let up = view.superview else { return event }
                view = up
            }
            guard let scroll = view as? NSScrollView, scroll.documentView != nil else { return event }
            let clip = scroll.contentView
            // The clip view knows the strip's margins (insets) and its ends.
            var wanted = clip.bounds
            wanted.origin.x -= event.scrollingDeltaY * 24
            clip.setBoundsOrigin(clip.constrainBoundsRect(wanted).origin)
            scroll.reflectScrolledClipView(clip)
            return nil
        }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
    }

    deinit { stop() }
}

/// Set by previews drawn offscreen: the strip and results draw as plain
/// rows (scroll views come out blank in an image), with these covers by id,
/// and AppKit-backed pieces (mouse tracking, the text field) are left out.
/// Never set in the app.
struct PlayerPreview: Equatable {
    var covers: [String: NSImage] = [:]
}

private struct PlayerPreviewKey: EnvironmentKey { static let defaultValue: PlayerPreview? = nil }
extension EnvironmentValues {
    var playerPreview: PlayerPreview? {
        get { self[PlayerPreviewKey.self] }
        set { self[PlayerPreviewKey.self] = newValue }
    }
}

/// A row of covers that scrolls sideways with the trackpad or the wheel,
/// opening on the song that is playing, and settling on a cover after a
/// flick. Click a cover to play it. The faster it scrolls, the more the
/// covers bend, blur and brighten, like light passing through thick glass
/// (none of that under Reduce Motion). On reveal the side covers fan out
/// from behind the one in the middle.
struct CoverStrip: View {
    let items: [PlayerQueueItem]
    let tint: Color
    var artwork: NSImage?
    var recenter = 0
    var note: String?
    /// Bumped when the shuffle note should show (once per change).
    var noteTrigger = 0
    var shown = true
    var paused = false
    var labelsDimmed = false
    var sidesDimmed = false
    /// The item a Next press slid to, shown as current until the player answers.
    var optimisticCurrent: String?
    let onTap: (PlayerQueueItem) -> Void
    @Environment(\.playerLook) private var look
    @Environment(\.playerPreview) private var preview
    @State private var velocity: CGFloat = 0
    @State private var lastOffset: CGFloat?
    @State private var lastTime: TimeInterval = 0
    @State private var decay: Task<Void, Never>?
    @State private var wheel = WheelBridge()
    @State private var noteShown = false
    @State private var fanned = true

    private var currentID: String? {
        if let optimisticCurrent, items.contains(where: { $0.id == optimisticCurrent }) { return optimisticCurrent }
        return items.first(where: \.isCurrent)?.id ?? items.first?.id
    }

    private var currentIndex: Int { currentID.flatMap { id in items.firstIndex { $0.id == id } } ?? 0 }

    /// The titles under the covers while the seek bar's capsules are up:
    /// gone, since the capsules sit right over them (dimmed, they still
    /// showed through the time and lyric).
    static func labelOpacity(dimmed: Bool) -> Double { dimmed ? 0 : 1 }

    var body: some View {
        Group {
            if preview != nil { staticRow } else { scrollingRow }
        }
        .overlay(alignment: .top) {
            if let note, noteShown {
                Text(note).font(.system(size: 10)).foregroundStyle(.white.opacity(0.8))
                    .padding(.horizontal, 10).padding(.vertical, 3)
                    .playerCapsule(look)
                    .transition(.opacity)
            }
        }
        .animation(.easeOut(duration: 0.3), value: noteShown)
        .onChange(of: noteTrigger) { _, _ in
            noteShown = true
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 2_500_000_000)
                noteShown = false
            }
        }
        .onChange(of: shown) { _, now in
            guard now, !look.reduceMotion, preview == nil else { return }
            var transaction = Transaction()
            transaction.disablesAnimations = true
            withTransaction(transaction) { fanned = false }
            Task { @MainActor in fanned = true }
        }
    }

    // MARK: Scrolling

    private var scrollingRow: some View {
        let bend = max(-1, min(1, velocity / 1800))
        let strength = abs(bend)
        let still = look.reduceMotion
        return ScrollViewReader { proxy in
            ScrollView(.horizontal, showsIndicators: false) {
                LazyHStack(spacing: 14) {
                    ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                        tile(item, index: index)
                            .id(item.id)
                            .scrollTransition(axis: .horizontal) { content, phase in
                                let edge = abs(phase.value)
                                let moving = still ? 0 : strength
                                return content
                                    .scaleEffect((phase.isIdentity ? 1 : 0.84) - 0.1 * moving * edge)
                                    .opacity(phase.isIdentity ? 1 : 0.6)
                                    .rotation3DEffect(.degrees(still ? 0 : phase.value * (-24 - 16 * strength)),
                                                      axis: (0, 1, 0), perspective: 0.6)
                                    // Squeezed towards the middle, like a lens, and
                                    // blurred and lit by how fast it moves.
                                    .offset(x: -phase.value * moving * 34)
                                    .blur(radius: moving * (1 + 3 * edge))
                                    .brightness(moving * 0.12 * (1 - edge))
                                    .saturation(1 + moving * 0.25)
                            }
                    }
                }
                .scrollTargetLayout()
                .background(GeometryReader { proxy in
                    Color.clear.preference(key: StripOffsetKey.self, value: proxy.frame(in: .named("strip")).minX)
                })
            }
            // Margins rather than padding, so a flick settles with a cover in the middle.
            .contentMargins(.horizontal, (DesktopPlayerView.width - DesktopPlayerView.cover) / 2, for: .scrollContent)
            .scrollTargetBehavior(.viewAligned)
            .coordinateSpace(name: "strip")
            .onPreferenceChange(StripOffsetKey.self) { measured(offset: $0) }
            .task(id: "\(currentID ?? "")|\(items.count)") {
                // The row is laid out lazily; give it a moment before centring.
                try? await Task.sleep(nanoseconds: 80_000_000)
                guard !Task.isCancelled, let id = currentID else { return }
                withAnimation(look.reduceMotion ? nil : .smooth(duration: 0.4)) { proxy.scrollTo(id, anchor: .center) }
            }
            .onChange(of: recenter) { _, _ in
                // Back on the playing song every time, with no visible scroll.
                Task { @MainActor in
                    for delay in [30, 150] as [UInt64] {
                        try? await Task.sleep(nanoseconds: delay * 1_000_000)
                        if let id = currentID { proxy.scrollTo(id, anchor: .center) }
                    }
                }
            }
        }
        .onAppear { wheel.start() }
        .onDisappear { wheel.stop() }
    }

    /// For previews: the same tiles, laid out as the strip rests.
    private var staticRow: some View {
        let step = DesktopPlayerView.cover + 14
        return ZStack {
            ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                let distance = index - currentIndex
                if abs(distance) <= 3 {
                    tile(item, index: index)
                        .scaleEffect(distance == 0 ? 1 : 0.84)
                        .opacity(distance == 0 ? 1 : 0.6)
                        .rotation3DEffect(.degrees(look.reduceMotion || distance == 0 ? 0 : (distance > 0 ? -24 : 24)),
                                          axis: (0, 1, 0), perspective: 0.6)
                        .offset(x: CGFloat(distance) * step)
                }
            }
        }
        .frame(width: DesktopPlayerView.width, height: DesktopPlayerView.stripHeight)
        .clipped()
    }

    // MARK: Tile

    private func tile(_ item: PlayerQueueItem, index: Int) -> some View {
        let isCurrent = item.id == currentID
        let distance = index - currentIndex
        return Button { onTap(item) } label: {
            VStack(spacing: 6) {
                cover(item, isCurrent: isCurrent)
                    .frame(width: DesktopPlayerView.cover, height: DesktopPlayerView.cover)
                    .clipShape(RoundedRectangle(cornerRadius: 12))
                    .overlay(RoundedRectangle(cornerRadius: 12)
                        .strokeBorder(tint, lineWidth: isCurrent ? 2 : 0))
                    .shadow(color: .black.opacity(0.5), radius: 12, y: 6)
                    // A paused song's cover sits back a little.
                    .scaleEffect(isCurrent && paused ? 0.94 : 1)
                    .animation(look.reduceMotion ? nil : .smooth(duration: 0.35), value: paused)
                VStack(spacing: 1) {
                    Text(item.title).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                    Text(item.artist).font(.system(size: 10)).foregroundStyle(.white.opacity(0.65)).lineLimit(1)
                }
                .frame(width: DesktopPlayerView.cover)
                .shadow(color: .black.opacity(0.7), radius: 5)
                .opacity(Self.labelOpacity(dimmed: labelsDimmed))
                .animation(labelsDimmed ? .easeIn(duration: 0.10) : .easeOut(duration: 0.12), value: labelsDimmed)
            }
        }
        .buttonStyle(CoverButtonStyle())
        .disabled(!item.isPlayable)
        .opacity(sidesDimmed && !isCurrent ? 0.5 : 1)
        .animation(.easeOut(duration: 0.15), value: sidesDimmed)
        // The fan: side covers start tucked behind the middle one.
        .offset(x: fanned || isCurrent ? 0 : CGFloat(distance > 0 ? -40 : 40))
        .opacity(fanned || isCurrent ? 1 : 0)
        .animation(.spring(duration: 0.38, bounce: 0).delay(0.018 * Double(min(abs(distance), 8))), value: fanned)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(item.artist.isEmpty ? item.title : "\(item.title) by \(item.artist)")
        .accessibilityHint(isCurrent ? "Now playing" : distance < 0 ? "Played earlier" : "Up next")
        .accessibilityAddTraits(.isButton)
        .accessibilityAction(named: isCurrent ? (paused ? "Play" : "Pause") : "Play") { onTap(item) }
    }

    @ViewBuilder
    private func cover(_ item: PlayerQueueItem, isCurrent: Bool) -> some View {
        if let image = preview?.covers[item.id] {
            Image(nsImage: image).resizable().aspectRatio(contentMode: .fill)
        } else if preview != nil {
            Rectangle().fill(.white.opacity(0.1))
        } else if item.isCurrent, item.persistentID == nil, let artwork {
            Image(nsImage: artwork).resizable().aspectRatio(contentMode: .fill)
        } else {
            RemoteCover(url: nil, symbol: "music.note", lookup: (item.title, item.artist), persistentID: item.persistentID)
        }
    }

    private func measured(offset: CGFloat) {
        guard !look.reduceMotion else { return }
        let now = ProcessInfo.processInfo.systemUptime
        defer { lastOffset = offset; lastTime = now }
        guard let last = lastOffset, now > lastTime else { return }
        let instant = (offset - last) / CGFloat(now - lastTime)
        guard instant.isFinite else { return }
        let next = velocity * 0.6 + instant * 0.4
        if abs(next - velocity) > 30 { velocity = next }
        decay?.cancel()
        decay = Task { @MainActor in
            while abs(velocity) > 12 {
                try? await Task.sleep(nanoseconds: 33_000_000)
                guard !Task.isCancelled else { return }
                velocity *= 0.82
            }
            velocity = 0
        }
    }
}

/// A cover gives a little on mouse-down, so the press is felt at once even
/// when the click waits to see whether it is a double-click.
private struct CoverButtonStyle: ButtonStyle {
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

private struct RowButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .background(configuration.isPressed ? Color.white.opacity(0.14) : Color.clear, in: RoundedRectangle(cornerRadius: 8))
    }
}

private extension View {
    /// Frosted dark glass with a hairline edge; solid under Reduce Transparency.
    @ViewBuilder
    func glass(radius: CGFloat, tint: Color, look: PlayerLook = PlayerLook()) -> some View {
        let shape = RoundedRectangle(cornerRadius: radius, style: .continuous)
        if look.reduceTransparency {
            background(Color(white: 0.12).opacity(0.95), in: shape)
                .overlay(shape.strokeBorder(.white.opacity(look.increaseContrast ? 0.4 : 0.14), lineWidth: 0.5))
        } else {
            background(.ultraThinMaterial, in: shape)
                .background(Color.black.opacity(0.28), in: shape)
                .overlay(shape.strokeBorder(.white.opacity(look.increaseContrast ? 0.4 : 0.14), lineWidth: 0.5))
                .shadow(color: .black.opacity(0.35), radius: 24, y: 8)
        }
    }
}
