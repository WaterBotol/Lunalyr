import AppKit
import Combine
import SwiftUI

/// State behind the desktop player: which player searches and plays, what
/// was typed, and what came back.
@MainActor
final class DesktopPlayerModel: ObservableObject {
    static let shared = DesktopPlayerModel()
    static let playerKey = "custom.desktopPlayer.player"
    /// "remaining" or "total": what the seek bar's right-hand label shows.
    static let timeModeKey = "custom.desktopPlayer.timeMode"
    /// Players installed on this Mac, looked up once.
    static let installed: Set<PlayerKind> = PlayerOptions.installedPlayers

    enum Status: Equatable {
        case idle, searching
        case message(String)
    }

    /// What the card's right-hand column shows: the song, the search, or the queue.
    enum Pane: Equatable {
        case song, search, queue
    }

    /// The player the search box and the queue talk to.
    @Published var player: PlayerKind {
        didSet {
            guard player != oldValue else { return }
            defaults.set(player.rawValue, forKey: Self.playerKey)
            results = []
            status = .idle
            runSearch()
        }
    }
    @Published var query = ""
    /// The search or the queue opened over the song's column. Shared by the
    /// panel and its hit mask (a separate render), so both see the same card.
    @Published var pane: Pane = .song
    @Published private(set) var results: [PlayerSearchResult] = [] {
        didSet { if results != oldValue { selection = 0 } }
    }
    /// The result Return plays; ↑ and ↓ move it.
    @Published private(set) var selection = 0
    /// The seek bar's right-hand label: time left (true) or the song's length.
    @Published var showsRemaining: Bool {
        didSet { if showsRemaining != oldValue { defaults.set(showsRemaining ? "remaining" : "total", forKey: Self.timeModeKey) } }
    }
    /// Moves on each time shuffle is switched or the playlist changes, so the
    /// shuffle note shows on the next reveal only, not on every one.
    @Published private(set) var shuffleNoteToken = 0
    private var shuffleNoteShown = 0
    /// What the strip shows: songs that played, the one playing, what is next.
    @Published private(set) var queue: [PlayerQueueItem] = []
    /// Shuffle is on, so the order of what comes next is not known.
    @Published private(set) var shuffleOn = false
    private var history: [PlayerQueueItem] = []
    private var fetched: [PlayerQueueItem] = []
    private var lastTrack: NowPlayingMonitor.Track?
    private var historyCount = 0
    @Published private(set) var status: Status = .idle
    /// A short note over the strip about the song just picked: that it
    /// plays alone and why, or that it could not be played. Shown once per
    /// `playNoteToken`; the status line under a search is not on screen
    /// once a song is picked.
    @Published private(set) var playNote: String?
    @Published private(set) var playNoteToken = 0
    private let defaults: UserDefaults
    private var searchTask: Task<Void, Never>?
    private var queueTask: Task<Void, Never>?
    private var cancellables: Set<AnyCancellable> = []
    /// A preview's model: holds made-up songs, never searches, reads the
    /// queue or plays anything.
    let isPreview: Bool

    init(defaults: UserDefaults = .standard, preview: Bool = false) {
        self.defaults = defaults
        isPreview = preview
        player = PlayerKind(rawValue: defaults.string(forKey: Self.playerKey) ?? "") ?? .appleMusic
        showsRemaining = defaults.string(forKey: Self.timeModeKey) != "total"
        guard !preview else { return }
        $query.removeDuplicates().debounce(for: .milliseconds(300), scheduler: DispatchQueue.main)
            .sink { [weak self] _ in self?.runSearch() }.store(in: &cancellables)
        // Songs that finish are remembered, in the order they played.
        NowPlayingMonitor.shared.$current.sink { [weak self] track in self?.noteTrack(track) }
            .store(in: &cancellables)
        // The strip follows the song.
        NowPlayingMonitor.shared.$current.map { $0?.persistentID ?? $0.map { "\($0.title)|\($0.artist)" } }
            .removeDuplicates().dropFirst()
            .debounce(for: .milliseconds(500), scheduler: DispatchQueue.main)
            .sink { [weak self] _ in
                self?.refreshQueue()
            }.store(in: &cancellables)
    }

    var searcher: any PlayerSearching { player.searcher }

    /// The player that is actually playing: the strip shows its songs, whatever
    /// the search is pointed at. Falls back to the search's player when none is.
    var playingKind: PlayerKind { NowPlayingMonitor.shared.activePlayer ?? player }
    private var queueKind: PlayerKind?

    /// Whether the player should stay on screen without the pointer: a search
    /// is typed.
    var isPinned: Bool { isSearching }

    var isSearching: Bool { !query.trimmingCharacters(in: .whitespaces).isEmpty }

    /// The column shows the search: opened, or something is typed.
    var showsSearch: Bool { pane == .search || isSearching }
    var showsQueue: Bool { pane == .queue && !isSearching }

    /// Search or queue, from the transport's buttons: a second press closes it.
    func toggle(_ wanted: Pane) {
        if wanted == .search, isSearching { pane = .search; return }
        pane = pane == wanted ? .song : wanted
    }

    /// Esc, and the player going away with nothing typed: back to the song.
    func closePane() {
        guard !isSearching else { return }
        pane = .song
    }

    /// Previews only: made-up songs and results, set straight in.
    func loadPreview(queue: [PlayerQueueItem], shuffle: Bool, results: [PlayerSearchResult] = [], selection: Int = 0,
                     status: Status = .idle, query: String = "", pane: Pane = .song, playNote: String? = nil) {
        guard isPreview else { return }
        fetched = queue
        self.queue = queue
        shuffleOn = shuffle
        self.query = query
        self.results = results
        self.selection = selection
        self.status = status
        self.pane = pane
        self.playNote = playNote
    }

    /// The player that is playing now, when it is one the search can drive.
    func followNowPlaying() {
        if let active = NowPlayingMonitor.shared.activePlayer, active != player { player = active }
    }

    func runSearch() {
        guard !isPreview else { return }
        searchTask?.cancel()
        let text = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard text.count >= 2 else {
            results = []
            status = .idle
            return
        }
        status = .searching
        let searcher = self.searcher
        searchTask = Task { [weak self] in
            do {
                let found = try await searcher.search(text)
                guard !Task.isCancelled else { return }
                self?.results = found
                self?.status = found.isEmpty ? .message("Nothing found for \u{201C}\(text)\u{201D}") : .idle
            } catch {
                guard !Task.isCancelled else { return }
                self?.results = []
                self?.status = .message(Self.words(for: error, player: searcher.kind))
            }
        }
    }

    private func noteTrack(_ track: NowPlayingMonitor.Track?) {
        let kind = NowPlayingMonitor.shared.activePlayer
        defer { lastTrack = track; lastKind = kind }
        guard let previous = lastTrack, let track,
              Self.belongsInHistory(previous: previous, from: lastKind, next: track, in: kind) else { return }
        historyCount += 1
        history.append(PlayerQueueItem(id: "h-\(historyCount)", title: previous.title, artist: previous.artist,
                                       index: 0, persistentID: previous.persistentID))
        if history.count > 24 { history.removeFirst(history.count - 24) }
        recompose()
    }

    /// The player `lastTrack` came from.
    private var lastKind: PlayerKind?

    /// Only Music's own songs go into the strip's history, and only when the
    /// next song is Music's too: a Spotify or web song's id means nothing to
    /// Music, and clicking it would only fail.
    nonisolated static func belongsInHistory(previous: NowPlayingMonitor.Track, from previousKind: PlayerKind?,
                                             next: NowPlayingMonitor.Track, in kind: PlayerKind?) -> Bool {
        guard !previous.title.isEmpty, !(previous.title == next.title && previous.artist == next.artist),
              previousKind == .appleMusic, kind == .appleMusic || kind == nil else { return false }
        return true
    }

    /// History first (what really played, in order), then the song playing,
    /// then what the playlist says comes next.
    private func recompose() {
        guard let nowIndex = fetched.firstIndex(where: \.isCurrent) else { queue = fetched; return }
        let now = fetched[nowIndex]
        let after = Array(fetched[(nowIndex + 1)...])
        var before = Array(history.suffix(24)).filter { $0.persistentID == nil || $0.persistentID != now.persistentID }
        // As many before as after: where less has played than is queued, the
        // playlist's own earlier songs fill in the older end.
        let missing = after.count - before.count
        if missing > 0 {
            let known = Set(before.compactMap(\.persistentID))
            let earlier = fetched[..<nowIndex].filter { $0.persistentID.map { !known.contains($0) } ?? true }
            before = Array(earlier.suffix(missing)) + before
        }
        queue = before + [now] + after
    }

    /// Reloads the strip: the songs before this one, this one, the songs after.
    func refreshQueue() {
        guard !isPreview else { return }
        queueTask?.cancel()
        let searcher = playingKind.searcher
        if queueKind != searcher.kind { queue = []; fetched = []; queueKind = searcher.kind }
        queueTask = Task { [weak self] in
            do {
                let snapshot = try await searcher.queue()
                let items = snapshot.items
                guard !Task.isCancelled, let self else { return }
                // A player between two songs answers with nothing; keep what
                // the strip already shows rather than emptying it.
                if items.isEmpty, !self.fetched.isEmpty { return }
                if snapshot.shuffle != self.shuffleOn || Self.playlistChanged(from: self.fetched, to: items) {
                    self.shuffleNoteToken += 1
                }
                self.shuffleOn = snapshot.shuffle
                self.fetched = items
                self.recompose()
                self.status = items.isEmpty && !self.isSearching
                    ? .message(Self.emptyQueueMessage(for: searcher.kind))
                    : .idle
            } catch {
                guard !Task.isCancelled, let self else { return }
                if self.queue.isEmpty { self.status = .message(Self.words(for: error, player: searcher.kind)) }
            }
        }
    }

    /// ↑ / ↓ in the results.
    func moveSelection(by step: Int) {
        guard !results.isEmpty else { return }
        selection = min(max(selection + step, 0), results.count - 1)
    }

    /// Return in the search: the selected result.
    func playSelection() {
        guard results.indices.contains(selection) else { return }
        play(results[selection])
    }

    /// Whether the shuffle note should show on this reveal: once per change.
    func takeShuffleNote() -> Bool {
        guard shuffleOn, shuffleNoteToken != shuffleNoteShown else { return false }
        shuffleNoteShown = shuffleNoteToken
        return true
    }

    /// Whether a fresh read of the playlist window is a different playlist:
    /// a position both reads cover holds a different song, or the two
    /// windows share no song at all.
    nonisolated static func playlistChanged(from old: [PlayerQueueItem], to new: [PlayerQueueItem]) -> Bool {
        guard !old.isEmpty, !new.isEmpty else { return old.isEmpty != new.isEmpty }
        let before = Dictionary(old.map { ($0.index, $0.persistentID ?? "\($0.title)|\($0.artist)") },
                                uniquingKeysWith: { first, _ in first })
        var overlap = false
        for item in new {
            guard let was = before[item.index] else { continue }
            overlap = true
            if was != (item.persistentID ?? "\(item.title)|\(item.artist)") { return true }
        }
        return !overlap
    }

    /// The strip item a press on Next can slide to before the player
    /// answers: only for Music with shuffle off, where what comes next is
    /// known; never a guess.
    nonisolated static func optimisticNext(queue: [PlayerQueueItem], shuffleOn: Bool, kind: PlayerKind?) -> String? {
        guard kind == .appleMusic, !shuffleOn, let now = queue.firstIndex(where: \.isCurrent),
              now + 1 < queue.count, queue[now + 1].isPlayable else { return nil }
        return queue[now + 1].id
    }

    func play(_ result: PlayerSearchResult) {
        guard !isPreview else { return }
        let searcher = self.searcher
        perform(searcher) { try await searcher.playNoting(result) }
        if case .spotifySearch = result.target { return }
        collapseAfterPlaying()
    }

    func play(_ item: PlayerQueueItem) {
        guard item.isPlayable, !isPreview else { return }
        let searcher = playingKind.searcher
        // The strip moves to the song at once; the player catches up after.
        // It plays inside the playlist that is playing, which carries on after it.
        if fetched.contains(where: { $0.id == item.id }) {
            fetched = fetched.map { var copy = $0; copy.isCurrent = $0.id == item.id; return copy }
            recompose()
        }
        perform(searcher) { try await searcher.playQueueItemNoting(item) }
        Task { [weak self] in
            for delay in [900, 2500, 5000] as [UInt64] {
                try? await Task.sleep(nanoseconds: delay * 1_000_000)
                self?.refreshQueue()
            }
        }
    }

    private func collapseAfterPlaying() {
        query = ""
        results = []
        pane = .song
    }

    private func perform(_ searcher: any PlayerSearching, _ work: @escaping @Sendable () async throws -> String?) {
        Task { [weak self] in
            do {
                self?.showPlayNote(try await work())
            } catch {
                let words = Self.words(for: error, player: searcher.kind)
                self?.status = .message(words)
                self?.showPlayNote(words)
            }
        }
    }

    /// Seconds a play note stays on record: longer than the strip shows it.
    static let playNoteLife: UInt64 = 9

    private func showPlayNote(_ text: String?) {
        guard let text else { return }
        playNote = text
        playNoteToken += 1
        let token = playNoteToken
        Task { [weak self] in
            try? await Task.sleep(nanoseconds: Self.playNoteLife * 1_000_000_000)
            if self?.playNoteToken == token { self?.playNote = nil }
        }
    }

    /// Under an empty strip. Only Music shares what plays next.
    nonisolated static func emptyQueueMessage(for kind: PlayerKind) -> String {
        kind == .appleMusic ? "Nothing is playing." : "\(kind.shortName) doesn\u{2019}t share what plays next."
    }

    /// The players the switch offers: Music and Spotify where installed, a
    /// local-file player while it runs, a browser once a web player was seen
    /// in it -- and always the one playing and the one selected. Only
    /// followed players, which are the only ones Lunalyr may talk to.
    nonisolated static func switchKinds(installed: Set<PlayerKind>, running: Set<PlayerKind>, followed: [PlayerKind],
                                        webSeen: Set<PlayerKind>, active: PlayerKind?, selected: PlayerKind) -> [PlayerKind] {
        PlayerKind.allCases.filter { kind in
            if kind == active || kind == selected { return true }
            guard followed.contains(kind) else { return false }
            switch kind.family {
            case .musicApp: return installed.contains(kind)
            case .localFiles: return installed.contains(kind) && running.contains(kind)
            case .browser: return running.contains(kind) && webSeen.contains(kind)
            }
        }
    }

    nonisolated static func words(for error: Error, player: PlayerKind) -> String {
        switch error as? PlayerSearchError {
        case .unsupported: return "\(player.shortName) has no search Lunalyr can use."
        case .notRunning: return "Open \(player.displayName) first, then try again."
        case .blocked: return "Allow Lunalyr to control \(player.displayName) in System Settings \u{203A} Privacy & Security \u{203A} Automation."
        default: return "\(player.displayName) did not answer. Try again."
        }
    }
}
