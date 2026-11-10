import Foundation

// Search YouTube…: the user searches YouTube for the playing song and picks
// the upload to use (a lyric video, a live take, another upload), when the
// automatic lookup chose the wrong one. Thumbnails only: nothing plays here.
// A pick goes through the pasted-link path, so Clear, per-video offsets and
// the still-image note work as for a pasted link.

/// What a Search YouTube… search found.
enum MusicVideoSearchOutcome: Equatable, Sendable {
    /// Video results, in YouTube's order (possibly none).
    case results([YouTubeSearchPage.Result])
    /// Too many searches for now (`MusicVideoRateLimit`).
    case rateLimited
    /// Offline, or YouTube's page could not be read.
    case failed
    /// Replaced by a newer search, or the window closed.
    case cancelled
}

extension YouTubeSearchPage {
    /// YouTube's results page for exactly what the user typed, without the
    /// automatic lookup's "official music video", so lyric videos, live
    /// takes and covers show up too.
    static func requestURL(query: String) -> URL? {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        var components = URLComponents(string: "https://www.youtube.com/results")
        components?.queryItems = [URLQueryItem(name: "search_query", value: trimmed)]
        return components?.url
    }
}

/// One result as the picker shows it.
struct MusicVideoSearchRow: Identifiable, Equatable, Sendable {
    var id: String { videoID }
    let videoID: String
    let title: String
    let channel: String
    let verifiedArtist: Bool
    /// "3:45", for the badge on the thumbnail.
    let durationText: String?
    /// "12M views · 3 years ago", or whichever of the two YouTube gave.
    let details: String?
    /// Within `YouTubeSearchPage.lengthMatch` of the song's length.
    let sameLength: Bool
    /// The video on the wallpaper now.
    let playingNow: Bool

    static let sameLengthHint = "Same length as the song"
    static let playingNowLabel = "Playing now"
    static let verifiedLabel = "Verified artist"
    /// Results shown at most.
    static let limit = 20

    /// The thumbnail: YouTube's 320 × 180 still. An image, never the video.
    var thumbnailURL: URL? {
        YouTubeVideoID.isValid(videoID) ? URL(string: "https://i.ytimg.com/vi/\(videoID)/mqdefault.jpg") : nil
    }

    init(_ result: YouTubeSearchPage.Result, songDuration: Double?, playing: String?) {
        videoID = result.videoID
        title = result.title.isEmpty ? "Untitled video" : result.title
        channel = result.channel
        verifiedArtist = result.verifiedArtist
        durationText = Self.duration(result.duration)
        details = Self.details(views: result.views, uploaded: result.uploaded)
        sameLength = Self.sameLength(result.duration, song: songDuration)
        playingNow = playing == result.videoID
    }

    /// "0:07", "3:45", "1:02:03"; nil without a usable length.
    static func duration(_ seconds: Double?) -> String? {
        guard let seconds, seconds.isFinite, seconds >= 0 else { return nil }
        let total = Int(seconds.rounded())
        let hours = total / 3600, minutes = total / 60 % 60, rest = total % 60
        return hours > 0 ? String(format: "%d:%02d:%02d", hours, minutes, rest) : String(format: "%d:%02d", minutes, rest)
    }

    static func details(views: String?, uploaded: String?) -> String? {
        let parts = [views, uploaded].compactMap { $0?.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    static func sameLength(_ video: Double?, song: Double?) -> Bool {
        guard let video, let song, song > 0, video.isFinite else { return false }
        return abs(video - song) <= YouTubeSearchPage.lengthMatch
    }

    /// What VoiceOver reads for the row.
    var accessibilityLabel: String {
        var parts = [title]
        if !channel.isEmpty { parts.append(verifiedArtist ? "\(channel), \(Self.verifiedLabel)" : channel) }
        if let durationText { parts.append(durationText) }
        if let details { parts.append(details) }
        if sameLength { parts.append(Self.sameLengthHint) }
        if playingNow { parts.append(Self.playingNowLabel) }
        return parts.joined(separator: ", ")
    }

    /// The rows for `results`: YouTube's order, each video once, the one
    /// playing now moved to the top, at most `limit`.
    static func rows(_ results: [YouTubeSearchPage.Result], songDuration: Double?, playing: String?,
                     limit: Int = limit) -> [MusicVideoSearchRow] {
        var seen: Set<String> = []
        var rows = results.filter { seen.insert($0.videoID).inserted }
            .map { MusicVideoSearchRow($0, songDuration: songDuration, playing: playing) }
        if let index = rows.firstIndex(where: \.playingNow), index > 0 {
            rows.insert(rows.remove(at: index), at: 0)
        }
        return Array(rows.prefix(max(0, limit)))
    }
}

/// What the picker needs from the music video controller; a seam for tests.
@MainActor
protocol MusicVideoSearching: AnyObject {
    var searchSong: MusicVideoSong? { get }
    var showingYouTubeID: String? { get }
    var pickedVideoID: String? { get }
    func searchYouTube(_ query: String) async -> MusicVideoSearchOutcome
    @discardableResult func usePickedVideo(_ videoID: String, forSong songKey: String) -> Bool
    func clearPastedLink()
}

extension MusicVideoController: MusicVideoSearching {}

/// The Search YouTube… picker's state: the song it is for, the query, the
/// results and the keyboard selection.
@MainActor
final class MusicVideoSearchModel: ObservableObject {
    enum Phase: Equatable {
        /// Nothing searched yet (or the song changed).
        case idle
        case loading
        case results
        case empty
        case rateLimited
        case failed
        /// No song is playing.
        case noSong
    }

    @Published var query = ""
    @Published private(set) var phase: Phase = .idle
    @Published private(set) var rows: [MusicVideoSearchRow] = []
    @Published var selection: String?
    /// A short note above the results (the song changed under a pick).
    @Published private(set) var notice: String?
    /// "“Blinding Lights” by The Weeknd".
    @Published private(set) var songLabel: String?
    private(set) var songKey: String?
    private var songDuration: Double?
    private var results: [YouTubeSearchPage.Result] = []
    private let source: MusicVideoSearching
    private var searchTask: Task<Void, Never>?
    /// Closes the window: after a pick, Back to automatic, or Close.
    var onClose: () -> Void = {}

    static let songChangedNote = "The song changed. Search again for the one playing now."

    init(source: MusicVideoSearching) {
        self.source = source
    }

    /// "<artist> <title>": what the search field starts with.
    nonisolated static func prefill(for song: MusicVideoSong) -> String {
        [song.artist, song.title].map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            .joined(separator: " ")
    }

    nonisolated static func label(for song: MusicVideoSong) -> String {
        let artist = song.artist.trimmingCharacters(in: .whitespaces)
        return artist.isEmpty ? "“\(song.title)”" : "“\(song.title)” by \(artist)"
    }

    /// Whether the playing song uses a pick (or pasted link) to go back from.
    var canGoBackToAutomatic: Bool {
        songKey != nil && source.searchSong?.key == songKey && source.pickedVideoID != nil
    }

    /// Called each time the window is shown: a new song (or one that
    /// failed or was never searched) is searched afresh; the same song
    /// keeps its results, with "Playing now" brought up to date.
    func prepare() {
        notice = nil
        let song = source.searchSong
        if song?.key != songKey || ![.results, .empty, .loading].contains(phase) {
            reset(to: song)
            if song != nil { search() }
        } else {
            rebuildRows()
        }
    }

    private func reset(to song: MusicVideoSong?) {
        searchTask?.cancel()
        songKey = song?.key
        songDuration = song?.duration
        songLabel = song.map(Self.label)
        query = song.map(Self.prefill) ?? ""
        results = []
        rows = []
        selection = nil
        phase = song == nil ? .noSong : .idle
    }

    /// Searches YouTube for the query (Return in the field).
    func search() {
        guard songKey != nil else { phase = .noSong; return }
        let text = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        searchTask?.cancel()
        notice = nil
        results = []
        rows = []
        selection = nil
        phase = .loading
        searchTask = Task { [weak self] in
            guard let self else { return }
            let outcome = await self.source.searchYouTube(text)
            guard !Task.isCancelled else { return }
            self.show(outcome)
        }
    }

    private func show(_ outcome: MusicVideoSearchOutcome) {
        switch outcome {
        case .results(let found):
            results = found
            rebuildRows()
            selection = rows.first?.id
            phase = rows.isEmpty ? .empty : .results
        case .rateLimited: phase = .rateLimited
        case .failed: phase = .failed
        case .cancelled: break
        }
    }

    private func rebuildRows() {
        rows = MusicVideoSearchRow.rows(results, songDuration: songDuration, playing: source.showingYouTubeID)
        if let selection, !rows.contains(where: { $0.id == selection }) { self.selection = rows.first?.id }
    }

    /// Uses the video for the song and closes. When another song has
    /// started since the search, nothing is used: the picker moves to the
    /// new song and says so.
    func pick(_ videoID: String) {
        guard let songKey else { return }
        if source.usePickedVideo(videoID, forSong: songKey) {
            close()
        } else if source.searchSong?.key != songKey {
            reset(to: source.searchSong)
            notice = Self.songChangedNote
        }
    }

    func pickSelection() {
        if let selection { pick(selection) }
    }

    /// Up and down through the rows (keyboard), staying at either end.
    func moveSelection(by delta: Int) {
        guard !rows.isEmpty else { return }
        let current = selection.flatMap { id in rows.firstIndex { $0.id == id } }
        let next = current.map { min(rows.count - 1, max(0, $0 + delta)) } ?? (delta < 0 ? rows.count - 1 : 0)
        selection = rows[next].id
    }

    /// Stops using the pick for the song and lets Lunalyr choose again.
    func backToAutomatic() {
        guard canGoBackToAutomatic else { return }
        source.clearPastedLink()
        close()
    }

    func close() {
        stop()
        onClose()
    }

    /// The window closed: any search in flight is dropped (sends nothing
    /// more if it was still waiting its turn).
    func stop() {
        searchTask?.cancel()
        if phase == .loading { phase = .idle }
    }

    /// Waits for the search in flight; for tests.
    func settle() async {
        await searchTask?.value
    }
}
