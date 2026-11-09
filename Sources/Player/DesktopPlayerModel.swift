import AppKit
import Combine
import SwiftUI

/// State behind the desktop player: which player searches and plays, what
/// was typed, and what came back.
@MainActor
final class DesktopPlayerModel: ObservableObject {
    static let shared = DesktopPlayerModel()
    static let playerKey = "custom.desktopPlayer.player"

    enum Drawer: String, CaseIterable, Identifiable {
        case queue, albums
        var id: String { rawValue }
        var title: String { self == .albums ? "Albums" : "Up next" }
    }

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
            albums = []
            status = .idle
            runSearch()
            refreshDrawer()
        }
    }
    @Published var query = ""
    @Published private(set) var results: [PlayerSearchResult] = []
    @Published private(set) var queue: [PlayerQueueItem] = []
    @Published private(set) var albums: [PlayerAlbum] = []
    @Published private(set) var status: Status = .idle
    /// Whether the panel is open above the bar.
    @Published var isExpanded = false {
        didSet { if isExpanded { refreshDrawer() } }
    }
    @Published var drawer: Drawer = .queue {
        didSet { refreshDrawer() }
    }
    @Published var selectedAlbum = 0

    private let defaults: UserDefaults
    private var searchTask: Task<Void, Never>?
    private var drawerTask: Task<Void, Never>?
    private var albumsArtist: String?
    private var cancellables: Set<AnyCancellable> = []

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        player = PlayerKind(rawValue: defaults.string(forKey: Self.playerKey) ?? "") ?? .appleMusic
        $query.removeDuplicates().debounce(for: .milliseconds(300), scheduler: DispatchQueue.main)
            .sink { [weak self] _ in self?.runSearch() }.store(in: &cancellables)
        // The queue and the albums follow the song.
        NowPlayingMonitor.shared.$current.map { $0?.persistentID ?? $0.map { "\($0.title)|\($0.artist)" } }
            .removeDuplicates().dropFirst()
            .debounce(for: .milliseconds(500), scheduler: DispatchQueue.main)
            .sink { [weak self] _ in
                guard let self, self.isExpanded else { return }
                self.refreshDrawer(force: true)
            }.store(in: &cancellables)
    }

    var searcher: any PlayerSearching { player.searcher }

    /// Whether the player should stay on screen without the pointer: it is
    /// open, or a search is typed.
    var isPinned: Bool { isExpanded || isSearching }

    /// Height of what opens above the bar: tall for search results, shorter
    /// for the cover strips.
    var drawerHeight: CGFloat {
        guard isExpanded else { return 0 }
        return isSearching ? DesktopPlayerView.resultsHeight : DesktopPlayerView.stripHeight
    }

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

    func refreshDrawer(force: Bool = false) {
        drawerTask?.cancel()
        let searcher = self.searcher
        switch drawer {
        case .queue:
            drawerTask = Task { [weak self] in
                do {
                    let items = try await searcher.queue()
                    guard !Task.isCancelled else { return }
                    self?.queue = items
                    self?.status = items.isEmpty && !(self?.isSearching ?? false)
                        ? .message(searcher.kind == .spotify
                            ? "Spotify doesn\u{2019}t share what plays next."
                            : "Nothing is queued after this song.")
                        : .idle
                } catch {
                    guard !Task.isCancelled else { return }
                    self?.queue = []
                    self?.status = .message(Self.words(for: error, player: searcher.kind))
                }
            }
        case .albums:
            guard let artist = NowPlayingMonitor.shared.current?.artist, !artist.isEmpty else {
                albums = []
                return
            }
            // Only Apple Music's own tracks are looked up in Apple's catalogue.
            guard searcher.kind == .appleMusic, NowPlayingMonitor.shared.activePlayer == .appleMusic else {
                albums = []
                return
            }
            if !force, albumsArtist == artist, !albums.isEmpty { return }
            albumsArtist = artist
            drawerTask = Task { [weak self] in
                let found = await searcher.albums(by: artist)
                guard !Task.isCancelled else { return }
                self?.albums = found
                self?.selectedAlbum = min(found.count / 2, max(found.count - 1, 0))
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

    func play(_ album: PlayerAlbum) {
        let searcher = self.searcher
        perform(searcher) { try await searcher.play(album) }
        collapseAfterPlaying()
    }

    private func collapseAfterPlaying() {
        query = ""
        results = []
        isExpanded = false
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
