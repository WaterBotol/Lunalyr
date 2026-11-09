import AppKit
import Combine
import SwiftUI

/// State behind the desktop player: which player searches and plays, what
/// was typed, and what came back.
@MainActor
final class DesktopPlayerModel: ObservableObject {
    static let shared = DesktopPlayerModel()
    static let playerKey = "custom.desktopPlayer.player"
    /// Players installed on this Mac, looked up once.
    static let installed: Set<PlayerKind> = PlayerOptions.installedPlayers

    enum Status: Equatable {
        case idle, searching
        case message(String)
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
    @Published private(set) var results: [PlayerSearchResult] = []
    /// What the strip shows: songs that played, the one playing, what is next.
    @Published private(set) var queue: [PlayerQueueItem] = []
    /// Shuffle is on, so the order of what comes next is not known.
    @Published private(set) var shuffleOn = false
    private var history: [PlayerQueueItem] = []
    private var fetched: [PlayerQueueItem] = []
    private var lastTrack: NowPlayingMonitor.Track?
    private var historyCount = 0
    @Published private(set) var status: Status = .idle
    private let defaults: UserDefaults
    private var searchTask: Task<Void, Never>?
    private var queueTask: Task<Void, Never>?
    private var cancellables: Set<AnyCancellable> = []

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        player = PlayerKind(rawValue: defaults.string(forKey: Self.playerKey) ?? "") ?? .appleMusic
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

    /// The player that is playing now, when it is one the search can drive.
    func followNowPlaying() {
        if let active = NowPlayingMonitor.shared.activePlayer, active != player { player = active }
    }

    func runSearch() {
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
        defer { lastTrack = track }
        guard let previous = lastTrack, let track, !previous.title.isEmpty,
              !(previous.title == track.title && previous.artist == track.artist),
              NowPlayingMonitor.shared.activePlayer == .appleMusic || NowPlayingMonitor.shared.activePlayer == nil else { return }
        historyCount += 1
        history.append(PlayerQueueItem(id: "h-\(historyCount)", title: previous.title, artist: previous.artist,
                                       index: 0, persistentID: previous.persistentID))
        if history.count > 24 { history.removeFirst(history.count - 24) }
        recompose()
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

    func play(_ result: PlayerSearchResult) {
        let searcher = self.searcher
        perform(searcher) { try await searcher.play(result) }
        if case .spotifySearch = result.target { return }
        collapseAfterPlaying()
    }

    func play(_ item: PlayerQueueItem) {
        guard item.isPlayable else { return }
        let searcher = playingKind.searcher
        // The strip moves to the song at once; the player catches up after.
        // It plays inside the playlist that is playing, which carries on after it.
        if fetched.contains(where: { $0.id == item.id }) {
            fetched = fetched.map { var copy = $0; copy.isCurrent = $0.id == item.id; return copy }
            recompose()
        }
        perform(searcher) { try await searcher.playQueueItem(item) }
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
    }

    private func perform(_ searcher: any PlayerSearching, _ work: @escaping @Sendable () async throws -> Void) {
        Task { [weak self] in
            do { try await work() } catch {
                self?.status = .message(Self.words(for: error, player: searcher.kind))
            }
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
