import AppKit
import XCTest
@testable import VersoCustom

// Stand-ins for the relay, song.link and the follower's player. Nothing here
// opens a socket, reaches the network or sends an Apple event.

/// A clock the test moves by hand.
final class ListenTestClock: @unchecked Sendable {
    var now: TimeInterval = 1000
    func advance(_ seconds: TimeInterval) { now += seconds }
}

/// HTTP answered from a table; records every request.
final class FakeListenHTTP: ListenHTTP, @unchecked Sendable {
    private let lock = NSLock()
    private var replies: [String: (Data, Int)] = [:]
    private var _requests: [String] = []
    var fails = false

    var requests: [String] { lock.withLock { _requests } }

    func reply(_ url: String, _ body: String, status: Int = 200) {
        lock.withLock { replies[url] = (Data(body.utf8), status) }
    }

    private func answer(_ method: String, _ url: URL) throws -> (Data, Int) {
        try lock.withLock {
            _requests.append("\(method) \(url.absoluteString)")
            if fails { throw URLError(.notConnectedToInternet) }
            return replies[url.absoluteString] ?? (Data(), 404)
        }
    }

    func get(_ url: URL) async throws -> (Data, Int) { try answer("GET", url) }
    func post(_ url: URL) async throws -> (Data, Int) { try answer("POST", url) }
}

/// A socket whose incoming messages the test pushes and whose sent ones it reads.
final class FakeListenSocket: ListenSocket, @unchecked Sendable {
    let url: URL
    private let lock = NSLock()
    private var _sent: [String] = []
    private var inbox: [String] = []
    private var waiting: CheckedContinuation<String, Error>?
    private var closed = false
    var isUnknownRoom = false
    var closeCode: Int?

    init(url: URL) { self.url = url }

    var sent: [String] { lock.withLock { _sent } }
    var isClosed: Bool { lock.withLock { closed } }

    func send(_ data: Data) async throws {
        try lock.withLock {
            if closed { throw URLError(.networkConnectionLost) }
            _sent.append(String(decoding: data, as: UTF8.self))
        }
    }

    func receive() async throws -> String {
        try await withCheckedThrowingContinuation { continuation in
            lock.lock()
            if closed {
                lock.unlock()
                continuation.resume(throwing: URLError(.networkConnectionLost))
            } else if !inbox.isEmpty {
                let next = inbox.removeFirst()
                lock.unlock()
                continuation.resume(returning: next)
            } else {
                waiting = continuation
                lock.unlock()
            }
        }
    }

    /// Delivers a message from the relay.
    func push(_ text: String) {
        lock.lock()
        if let waiting {
            self.waiting = nil
            lock.unlock()
            waiting.resume(returning: text)
        } else {
            inbox.append(text)
            lock.unlock()
        }
    }

    /// The relay closes the socket.
    func drop(unknownRoom: Bool = false, closeCode: Int? = nil) {
        lock.lock()
        isUnknownRoom = unknownRoom
        self.closeCode = closeCode
        closed = true
        let waiting = self.waiting
        self.waiting = nil
        lock.unlock()
        waiting?.resume(throwing: URLError(.networkConnectionLost))
    }

    func close() { drop() }
}

final class FakeListenSockets: ListenSocketOpening, @unchecked Sendable {
    private let lock = NSLock()
    private var _opened: [FakeListenSocket] = []
    var opened: [FakeListenSocket] { lock.withLock { _opened } }

    func open(_ url: URL) -> any ListenSocket {
        let socket = FakeListenSocket(url: url)
        lock.withLock { _opened.append(socket) }
        return socket
    }
}

/// The follower's player, as plain state.
@MainActor
final class FakeListenPlayer: ListenPlayerControl {
    var kind: PlayerKind? = .spotify
    var current: ListenPlayerReading?
    var shuffleOn = true
    var library: [LibraryCandidate] = []
    var playable = true
    private(set) var log: [String] = []

    func reading() -> ListenPlayerReading? { current }

    func play(_ target: ListenPlayTarget, at position: Double) async -> Bool {
        log.append("play \(target) at \(String(format: "%.1f", position))")
        guard playable, let kind else { return false }
        let id: String
        switch target {
        case .spotify(let spotifyID): id = "spotify:track:\(spotifyID)"
        case .library(let persistentID): id = persistentID
        }
        current = ListenPlayerReading(kind: target.kind, title: "", artist: "", trackID: id, position: position, playing: true)
        return true
    }

    func seek(to position: Double) async {
        log.append("seek \(String(format: "%.1f", position))")
        current?.position = position
    }

    func setPlaying(_ playing: Bool, on kind: PlayerKind) async {
        log.append("\(playing ? "play" : "pause") \(kind.rawValue)")
        if current?.kind == kind { current?.playing = playing }
    }

    /// Whether anything was asked to make sound: a song started or resumed.
    var everPlayed: Bool { log.contains { $0.hasPrefix("play") } }

    func shuffle(_ kind: PlayerKind) async -> Bool? { shuffleOn }

    func setShuffle(_ on: Bool, _ kind: PlayerKind) async {
        log.append("shuffle \(on ? "on" : "off")")
        shuffleOn = on
    }

    /// While set, library searches wait until the test releases them, in order.
    var holdsSearches = false
    private var heldSearches: [CheckedContinuation<Void, Never>] = []
    var pendingSearches: Int { heldSearches.count }

    func releaseSearch() {
        guard !heldSearches.isEmpty else { return }
        heldSearches.removeFirst().resume()
    }

    func searchLibrary(title: String) async -> [LibraryCandidate] {
        log.append("search \(title)")
        if holdsSearches { await withCheckedContinuation { heldSearches.append($0) } }
        return library
    }
}

extension XCTestCase {
    /// Lets queued tasks run until `condition` holds, for up to two seconds.
    @MainActor
    func waitUntil(_ message: String = "condition never held", file: StaticString = #filePath, line: UInt = #line,
                    _ condition: () -> Bool) async {
        for _ in 0..<400 {
            if condition() { return }
            try? await Task.sleep(nanoseconds: 5_000_000)
        }
        XCTFail(message, file: file, line: line)
    }
}

/// Answers "Listen along with …?" when the test says so, and records it.
@MainActor
final class FakeListenConfirmer: ListenJoinConfirming {
    private(set) var asked: [ListenInvite] = []
    private var pending: CheckedContinuation<Bool, Never>?

    func confirm(_ invite: ListenInvite) async -> Bool {
        asked.append(invite)
        return await withCheckedContinuation { pending = $0 }
    }

    var isAsking: Bool { pending != nil }

    func answer(_ accepted: Bool) {
        let pending = pending
        self.pending = nil
        pending?.resume(returning: accepted)
    }
}
