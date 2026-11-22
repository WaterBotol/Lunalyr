import AppKit
import Combine
import Foundation

/// A song in Apple Music's Up Next, as MediaRemote describes it.
///
/// Its duration is MediaRemote's, which can differ from what Music's
/// AppleScript reports for the same song by a second or two, so its
/// `recordingKey` may not be the one the song gets once it plays:
/// `matches` compares with that tolerance (`QueueLookAhead.adopt` carries
/// what was looked up across).
struct QueuedTrack: Equatable, Sendable {
    var title: String
    var artist: String
    var album: String
    var duration: Double
    var genre: String?
    /// MediaRemote's identifier for the queue item (not Music's persistent ID).
    var identifier: String?
    var isCurrent: Bool

    /// The lookup a lyric search makes for it.
    var query: LyricQuery {
        var query = LyricQuery(title: title, artist: artist, album: album.isEmpty ? nil : album, duration: duration)
        query.genre = genre
        return query
    }

    var recordingKey: String { query.recordingKey }

    var videoSong: MusicVideoSong {
        MusicVideoSong(title: title, artist: artist, album: album.isEmpty ? nil : album, duration: duration)
    }

    /// Something to look up: a titled song with a length (not a stream).
    var isLookable: Bool {
        !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && duration > 0
    }

    /// Durations from the two sources may differ by this much.
    static let durationTolerance: Double = 3

    /// The same song as `title`, `artist`, `album` and `duration` from the
    /// player: the same names (as lyric lookups compare them) and nearly
    /// the same length.
    func matches(title other: String, artist otherArtist: String, album otherAlbum: String?, duration otherDuration: Double) -> Bool {
        let n = LyricQuery.normalize
        guard n(title) == n(other), n(artist) == n(otherArtist), n(album) == n(otherAlbum ?? "") else { return false }
        guard duration > 0, otherDuration > 0 else { return true }
        return abs(duration - otherDuration) <= Self.durationTolerance
    }

    func matches(_ track: NowPlayingMonitor.Track) -> Bool {
        matches(title: track.title, artist: track.artist, album: track.album, duration: track.duration)
    }

    func matches(_ other: QueuedTrack) -> Bool {
        matches(title: other.title, artist: other.artist, album: other.album, duration: other.duration)
    }
}

/// One line from the queue helper (`Helpers/QueueReader/LunalyrQueue.m`).
enum MusicQueueMessage: Equatable, Sendable {
    case ready
    /// A whole snapshot: the playing song, then what follows it.
    case queue([QueuedTrack])
    case error(String)

    /// The message on one line of the helper's output; nil for anything
    /// that is not one (a partial line, another version, noise).
    static func parse(_ line: some StringProtocol) -> MusicQueueMessage? {
        let text = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard text.hasPrefix("{"), let data = text.data(using: .utf8),
              let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let type = object["type"] as? String else { return nil }
        switch type {
        case "ready":
            return .ready
        case "error":
            return .error((object["code"] as? String).map { String($0.prefix(40)) } ?? "unknown")
        case "queue":
            guard (object["v"] as? NSNumber)?.intValue == 1, let items = object["items"] as? [Any] else { return nil }
            var tracks: [QueuedTrack] = []
            for case let item as [String: Any] in items.prefix(50) {
                let duration = (item["duration"] as? NSNumber)?.doubleValue ?? 0
                tracks.append(QueuedTrack(
                    title: item["title"] as? String ?? "", artist: item["artist"] as? String ?? "",
                    album: item["album"] as? String ?? "",
                    duration: duration.isFinite && duration > 0 ? duration : 0,
                    genre: (item["genre"] as? String).flatMap { $0.isEmpty ? nil : $0 },
                    identifier: item["id"] as? String, isCurrent: (item["current"] as? NSNumber)?.boolValue ?? false))
            }
            return .queue(tracks)
        default:
            return nil
        }
    }

    /// The songs after the playing one, at most `limit`, when the snapshot
    /// is about the song `current` (the player and MediaRemote agree on
    /// what plays); otherwise none, rather than another moment's queue.
    static func upcoming(in snapshot: [QueuedTrack], current: NowPlayingMonitor.Track?, limit: Int = 5) -> [QueuedTrack] {
        guard let current, let index = snapshot.firstIndex(where: \.isCurrent) ?? (snapshot.isEmpty ? nil : 0),
              snapshot[index].matches(current) else { return [] }
        return Array(snapshot.dropFirst(index + 1).prefix(limit))
    }
}

/// Splits a byte stream into lines.
struct LineBuffer {
    private var pending = Data()
    /// Longer than any snapshot: a line this long without an end is dropped.
    static let longest = 1_000_000

    mutating func append(_ data: Data) -> [String] {
        pending.append(data)
        var lines: [String] = []
        while let end = pending.firstIndex(of: 0x0A) {
            let line = pending[pending.startIndex..<end]
            pending.removeSubrange(pending.startIndex...end)
            if let text = String(data: line, encoding: .utf8) { lines.append(text) }
        }
        if pending.count > Self.longest { pending.removeAll() }
        return lines
    }
}

/// `LineBuffer` for the helper's output handler, which runs on a
/// background queue one call at a time.
private final class LineReader: @unchecked Sendable {
    private let lock = NSLock()
    private var buffer = LineBuffer()

    func append(_ data: Data) -> [String] { lock.withLock { buffer.append(data) } }
}

/// Reads Apple Music's Up Next while Music is the player Lunalyr follows,
/// so `QueueLookAhead` can look the next songs up before they play.
///
/// macOS lets only entitled processes read MediaRemote, but a small library
/// loaded into `/usr/bin/perl` may. The library ships in Lunalyr's
/// Frameworks folder; perl runs it in watch mode, which prints the queue
/// as JSON lines whenever it changes and exits when its stdin closes, so it
/// never outlives Lunalyr. Each song change writes "refresh" to it. When
/// watch mode keeps failing, a one-shot run per song change takes over.
///
/// Every failure (no helper, perl refusing, a crash, no answer) leaves
/// `upcoming` empty and is retried later with a growing pause; nothing
/// else depends on it. Nothing here plays, pauses or skips anything.
@MainActor
final class MusicQueueReader: ObservableObject {
    static let shared = MusicQueueReader(command: .perl)

    /// The songs after the playing one, at most `upcomingLimit`.
    @Published private(set) var upcoming: [QueuedTrack] = []
    static let upcomingLimit = 5
    /// The songs after the playing one as the desktop player's queue lists
    /// them, at most `upNextLimit`, in the order Music will play them
    /// (shuffle and Play Next included).
    @Published private(set) var upNext: [QueuedTrack] = []
    nonisolated static let upNextLimit = 24
    /// The desktop player's queue is open, so the queue is read even with
    /// look-ahead off (`QueueLookAhead.refresh` decides). Nothing is looked
    /// up for it; it only lists the songs.
    @Published var wantedByPlayer = false

    /// The library perl loads, in the app's Frameworks folder.
    nonisolated static var helperURL: URL? {
        Bundle.main.privateFrameworksURL?.appendingPathComponent("LunalyrQueue.dylib")
    }

    /// Installs the library's entry point as a Perl sub and calls it.
    nonisolated static let loader = """
    use DynaLoader; my $l = DynaLoader::dl_load_file($ARGV[0], 0) or exit 3; \
    my $s = DynaLoader::dl_find_symbol($l, $ARGV[1]) or exit 4; \
    DynaLoader::dl_install_xsub("main::lunalyr_queue", $s); lunalyr_queue(); exit 0;
    """

    enum Mode: Equatable, Sendable { case watch, once }

    /// What runs the helper: perl with the library in the app, or, in
    /// tests, a stand-in. Nil arguments: there is nothing to run.
    struct Command: Sendable {
        var executable: URL
        var arguments: @Sendable (Mode) -> [String]?

        static let perl = Command(executable: URL(fileURLWithPath: "/usr/bin/perl")) { mode in
            guard let helper = MusicQueueReader.helperURL, FileManager.default.fileExists(atPath: helper.path) else {
                return nil
            }
            return ["-e", MusicQueueReader.loader, helper.path, mode == .watch ? "lunalyr_queue_watch" : "lunalyr_queue_once"]
        }
    }

    /// Pauses after a failure: doubling from `firstPause` to `longestPause`.
    static let firstPause: TimeInterval = 5
    static let longestPause: TimeInterval = 300
    /// Watch mode gives way to one-shot runs after this many failures in a row.
    static let watchFailuresAllowed = 3
    /// A watcher that has said nothing useful by now is stopped.
    var answerTimeout: TimeInterval = 12
    /// A one-shot run that takes longer than this is stopped.
    var onceTimeout: TimeInterval = 10
    /// The first pause after a failure (tests shorten it).
    var firstPause: TimeInterval = MusicQueueReader.firstPause

    private var snapshot: [QueuedTrack] = []
    private var current: NowPlayingMonitor.Track?
    private var active = false
    private(set) var mode: Mode = .watch
    private var failures = 0
    private var process: Process?
    private var input: FileHandle?
    private var generation = 0
    private var restart: Task<Void, Never>?
    private var watchdog: Task<Void, Never>?
    private var heard = false
    private var quitObserver: NSObjectProtocol?

    private let command: Command
    /// Whether it may run inside the test host (only with a stand-in command).
    private let runsInTests: Bool

    /// The pause before the next try after `failures` failures in a row.
    nonisolated static func pause(after failures: Int, first: TimeInterval = firstPause) -> TimeInterval {
        guard failures > 0 else { return 0 }
        return min(longestPause, first * pow(2, Double(min(failures - 1, 16))))
    }

    /// Failures in a row so far, and the mode, for tests.
    var failureCount: Int { failures }
    var isRunning: Bool { process != nil }

    init(command: Command, runsInTests: Bool = false) {
        self.command = command
        self.runsInTests = runsInTests
        quitObserver = NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification,
                                                              object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.stopProcess() }
        }
    }

    /// Reads the queue while `active` (Music is the player followed and
    /// the look-ahead or the desktop player's queue wants it); stops the
    /// helper otherwise.
    func setActive(_ active: Bool) {
        guard !AppRuntime.isUnitTesting || runsInTests, active != self.active else { return }
        self.active = active
        if active {
            failures = 0
            mode = .watch
            launch()
        } else {
            restart?.cancel()
            stopProcess()
            snapshot = []
            publish()
        }
    }

    /// The playing song changed: `upcoming` follows it, and the helper is
    /// asked for the queue again.
    func songChanged(to track: NowPlayingMonitor.Track?) {
        current = track
        publish()
        guard active else { return }
        switch mode {
        case .watch:
            if let input { try? input.write(contentsOf: Data("refresh\n".utf8)) }
        case .once:
            if process == nil, restart == nil { launch() }
        }
    }

    private func publish() {
        let listed = MusicQueueMessage.upcoming(in: snapshot, current: current, limit: Self.upNextLimit)
        if listed != upNext { upNext = listed }
        let next = Array(listed.prefix(Self.upcomingLimit))
        if next != upcoming { upcoming = next }
    }

    // MARK: The helper process

    private func launch() {
        restart = nil
        guard active, process == nil else { return }
        let mode = self.mode
        guard FileManager.default.isExecutableFile(atPath: command.executable.path),
              let arguments = command.arguments(mode) else {
            LookAheadLog.failure("queue helper missing; look-ahead off")
            return
        }
        generation += 1
        let ticket = generation
        let process = Process()
        process.executableURL = command.executable
        process.arguments = arguments
        // Nothing from Lunalyr's environment (PERL5LIB and the like) reaches perl.
        process.environment = ["PATH": "/usr/bin:/bin", "LANG": "en_US.UTF-8"]
        let output = Pipe(), input = Pipe()
        process.standardOutput = output
        process.standardInput = input
        process.standardError = FileHandle.nullDevice
        let reader = LineReader()
        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            if data.isEmpty {
                handle.readabilityHandler = nil
                return
            }
            let messages = reader.append(data).compactMap(MusicQueueMessage.parse)
            guard !messages.isEmpty else { return }
            DispatchQueue.main.async { [weak self] in
                MainActor.assumeIsolated { self?.receive(messages, ticket: ticket) }
            }
        }
        process.terminationHandler = { [weak self] finished in
            let status = finished.terminationStatus
            // After its last lines, which arrive on another queue.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in
                MainActor.assumeIsolated { self?.ended(ticket: ticket, status: status) }
            }
        }
        self.process = process
        // Writing after the helper ended must fail, not raise SIGPIPE in Lunalyr.
        _ = fcntl(input.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1)
        self.input = input.fileHandleForWriting
        heard = false
        // Starting a process can take a moment; never on the main thread.
        DispatchQueue.global(qos: .utility).async { [weak self] in
            do {
                try process.run()
            } catch {
                DispatchQueue.main.async { [weak self] in
                    MainActor.assumeIsolated { self?.ended(ticket: ticket, status: -1) }
                }
            }
        }
        LookAheadLog.note("queue helper started (\(mode == .watch ? "watch" : "once"))")
        watchdog?.cancel()
        let timeout = mode == .watch ? answerTimeout : onceTimeout
        watchdog = Task { [weak self] in
            try? await Task.sleep(for: .seconds(timeout))
            guard let self, !Task.isCancelled, self.generation == ticket, self.process != nil else { return }
            // Watch mode only has to have answered once; a one-shot run has to be over.
            if mode == .once || !self.heard {
                LookAheadLog.failure("queue helper gave no answer in \(Int(timeout)) s; stopped")
                self.process?.terminate()
            }
        }
    }

    private func receive(_ messages: [MusicQueueMessage], ticket: Int) {
        guard ticket == generation else { return }
        for message in messages {
            switch message {
            case .ready:
                break
            case .queue(let tracks):
                if !heard { LookAheadLog.note("queue read: \(tracks.count) items") }
                heard = true
                failures = 0
                snapshot = tracks
                publish()
            case .error(let code):
                // Music not playing answers this way too: the helper works, the queue is just empty.
                LookAheadLog.note("queue helper: \(code)")
                if code == "symbols" {
                    heard = false
                } else {
                    heard = true
                    snapshot = []
                    publish()
                }
            }
        }
    }

    private func ended(ticket: Int, status: Int32) {
        guard ticket == generation else { return }
        process = nil
        try? input?.close()
        input = nil
        watchdog?.cancel()
        guard active else { return }
        if mode == .once && heard {
            // A one-shot run that answered: the next song change runs it again.
            return
        }
        failures += 1
        if mode == .watch && failures >= Self.watchFailuresAllowed {
            mode = .once
            LookAheadLog.failure("queue watcher failed \(failures) times (status \(status)); reading once per song instead")
        }
        let pause = Self.pause(after: failures, first: firstPause)
        LookAheadLog.failure("queue helper ended (status \(status)); trying again in \(Int(pause)) s")
        snapshot = []
        publish()
        restart?.cancel()
        restart = Task { [weak self] in
            try? await Task.sleep(for: .seconds(pause))
            guard let self, !Task.isCancelled else { return }
            self.launch()
        }
    }

    private func stopProcess() {
        generation += 1
        watchdog?.cancel()
        try? input?.close()
        input = nil
        if let process, process.isRunning { process.terminate() }
        process = nil
    }
}
