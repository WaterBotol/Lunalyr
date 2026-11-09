import AppKit
import Combine
import SwiftUI

/// State behind the desktop player: which player searches and plays, what
/// was typed, and what came back.
@MainActor
final class DesktopPlayerModel: ObservableObject {
    static let shared = DesktopPlayerModel()
    static let playerKey = "custom.desktopPlayer.player"

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
            queue = []
            status = .idle
            runSearch()
            refreshQueue()
        }
    }
    @Published var query = ""
    @Published private(set) var results: [PlayerSearchResult] = []
    @Published private(set) var queue: [PlayerQueueItem] = []
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
        // The strip follows the song.
        NowPlayingMonitor.shared.$current.map { $0?.persistentID ?? $0.map { "\($0.title)|\($0.artist)" } }
            .removeDuplicates().dropFirst()
            .debounce(for: .milliseconds(500), scheduler: DispatchQueue.main)
            .sink { [weak self] _ in
                self?.refreshQueue()
            }.store(in: &cancellables)
    }

    var searcher: any PlayerSearching { player.searcher }

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

    /// Reloads the strip: the songs before this one, this one, the songs after.
    func refreshQueue() {
        queueTask?.cancel()
        let searcher = self.searcher
        queueTask = Task { [weak self] in
            do {
                let items = try await searcher.queue()
                guard !Task.isCancelled else { return }
                self?.queue = items
                self?.status = items.isEmpty && !(self?.isSearching ?? false)
                    ? .message(searcher.kind == .spotify
                        ? "Spotify doesn\u{2019}t share what plays next."
                        : "Nothing is playing.")
                    : .idle
            } catch {
                guard !Task.isCancelled else { return }
                self?.queue = []
                self?.status = .message(Self.words(for: error, player: searcher.kind))
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
        let searcher = self.searcher
        perform(searcher) { try await searcher.playQueueItem(item) }
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

    nonisolated static func words(for error: Error, player: PlayerKind) -> String {
        switch error as? PlayerSearchError {
        case .notRunning: return "Open \(player.displayName) first, then try again."
        case .blocked: return "Allow Lunalyr to control \(player.displayName) in System Settings \u{203A} Privacy & Security \u{203A} Automation."
        default: return "\(player.displayName) did not answer. Try again."
        }
    }
}
