import AppKit
import XCTest
@testable import VersoCustom

// Stand-ins for everything NowPlayingMonitor talks to. Nothing here sends an
// Apple event, observes a real notification or touches the network.

/// Music's scripting interface, answered from plain state.
final class FakeMusicApp: AppleScriptRunning {
    struct Song: Equatable {
        var id: String
        var name: String
        var artist = "Artist"
        var album = "Album"
        var genre = "Pop"
        /// nil is a live stream: Music reports `missing value`.
        var duration: Double? = 200
        var streamTitle: String?
        var artwork: Data? = FakeImages.png
    }

    var running = true
    var state: PlayerSnapshot.State = .playing
    var queue: [Song] = []
    var index = 0
    var position: TimeInterval = 0
    /// When set, every script fails with this AppleScript error number.
    var failure: Int?
    /// Runs after a reply is prepared and before it is returned: what
    /// happens while the Apple event is "in flight".
    var onReply: ((String) -> Void)?
    private(set) var log: [String] = []
    private(set) var sources: [String] = []

    var song: Song? { queue.indices.contains(index) ? queue[index] : nil }

    func count(_ name: String) -> Int { log.filter { $0 == name }.count }

    func clearLog() { log.removeAll(); sources.removeAll() }

    func run(_ script: PlayerScript) -> Result<NSAppleEventDescriptor, ScriptFailure> {
        log.append(script.name)
        sources.append(script.source)
        if let failure { return .failure(ScriptFailure(code: failure)) }
        let reply = respond(to: script)
        onReply?(script.name)
        return .success(reply)
    }

    private func respond(to script: PlayerScript) -> NSAppleEventDescriptor {
        let isQuery = ["music.read", "music.position"].contains(script.name) || script.name.hasPrefix("music.artwork")
        guard running else { return isQuery ? .scriptList([text("notRunning")]) : text("notRunning") }
        switch script.name {
        case "music.read":
            guard state != .stopped, let song else { return .scriptList([text("stopped")]) }
            return .scriptList([
                text(state.rawValue), text(song.id), text(song.name), text(song.artist), text(song.album),
                text(song.genre), song.duration.map(number) ?? .missingValue,
                song.streamTitle.map(text) ?? .missingValue,
            ])
        case "music.position":
            guard state != .stopped else { return .scriptList([text("stopped")]) }
            return .scriptList([text(state.rawValue), song.map { text($0.id) } ?? .missingValue, number(position)])
        case "music.artwork.data", "music.artwork.raw data":
            guard let song else { return .scriptList([text("none"), .missingValue]) }
            guard let data = song.artwork else { return .scriptList([text("none"), text(song.id)]) }
            return .scriptList([text("artwork"), text(song.id),
                                NSAppleEventDescriptor(descriptorType: 0x7464_7461, data: data)!])
        case "music.playpause":
            state = state == .playing ? .paused : .playing
        case "music.next":
            index = min(index + 1, queue.count - 1)
            position = 0
        case "music.previous":
            index = max(index - 1, 0)
            position = 0
        case "music.seek":
            if let range = script.source.range(of: #"set player position to [0-9.]+"#, options: .regularExpression),
               let value = script.source[range].split(separator: " ").last.flatMap({ Double($0) }) {
                position = value
            }
        default:
            return .scriptList([text("unexpected")])
        }
        return text("ok")
    }

    /// Music's `com.apple.Music.playerInfo` userInfo for a song. Music
    /// states the persistent ID there as a signed 64-bit number.
    static func notification(_ song: Song, state: String) -> [AnyHashable: Any] {
        var info: [AnyHashable: Any] = [
            "Player State": state, "Name": song.name, "Artist": song.artist, "Album": song.album,
            "Genre": song.genre, "PersistentID": NSNumber(value: Int64(bitPattern: UInt64(song.id, radix: 16) ?? 0)),
        ]
        if let duration = song.duration { info["Total Time"] = NSNumber(value: Int(duration * 1000)) }
        if let title = song.streamTitle { info["Stream Title"] = title }
        return info
    }

    private func text(_ value: String) -> NSAppleEventDescriptor { NSAppleEventDescriptor(string: value) }
    private func number(_ value: Double) -> NSAppleEventDescriptor { NSAppleEventDescriptor(double: value) }
}

/// Spotify's scripting interface, answered from plain state.
final class FakeSpotifyApp: AppleScriptRunning {
    struct Song: Equatable {
        var id: String
        var name: String
        var artist = "Artist"
        var album = "Album"
        /// Spotify reports milliseconds.
        var milliseconds: Double = 215_373
        var artworkURL: String? = "https://i.scdn.co/image/ab67616d0000b273cover"
    }

    var running = true
    var state: PlayerSnapshot.State = .playing
    var queue: [Song] = []
    var index = 0
    var position: TimeInterval = 0
    var failure: Int?
    private(set) var log: [String] = []

    var song: Song? { queue.indices.contains(index) ? queue[index] : nil }

    func count(_ name: String) -> Int { log.filter { $0 == name }.count }

    func clearLog() { log.removeAll() }

    func run(_ script: PlayerScript) -> Result<NSAppleEventDescriptor, ScriptFailure> {
        log.append(script.name)
        if let failure { return .failure(ScriptFailure(code: failure)) }
        let isQuery = ["spotify.read", "spotify.position", "spotify.artwork"].contains(script.name)
        guard running else { return .success(isQuery ? .scriptList([text("notRunning")]) : text("notRunning")) }
        switch script.name {
        case "spotify.read":
            guard state != .stopped, let song else { return .success(.scriptList([text("stopped")])) }
            return .success(.scriptList([
                text(state.rawValue), text(song.id), text(song.name), text(song.artist), text(song.album),
                NSAppleEventDescriptor(int32: Int32(song.milliseconds)), song.artworkURL.map(text) ?? .missingValue,
            ]))
        case "spotify.position":
            guard state != .stopped else { return .success(.scriptList([text("stopped")])) }
            return .success(.scriptList([text(state.rawValue), song.map { text($0.id) } ?? .missingValue,
                                         NSAppleEventDescriptor(double: position)]))
        case "spotify.artwork":
            guard let song else { return .success(.scriptList([text("none"), .missingValue])) }
            guard let url = song.artworkURL else { return .success(.scriptList([text("none"), text(song.id)])) }
            return .success(.scriptList([text("url"), text(song.id), text(url)]))
        case "spotify.playpause":
            state = state == .playing ? .paused : .playing
        case "spotify.next":
            index = min(index + 1, queue.count - 1)
            position = 0
        case "spotify.previous":
            index = max(index - 1, 0)
            position = 0
        case "spotify.seek":
            if let range = script.source.range(of: #"set player position to [0-9.]+"#, options: .regularExpression),
               let value = script.source[range].split(separator: " ").last.flatMap({ Double($0) }) {
                position = value
            }
        default:
            break
        }
        return .success(text("ok"))
    }

    /// Spotify's `com.spotify.client.PlaybackStateChanged` userInfo.
    static func notification(_ song: Song, state: String, position: TimeInterval) -> [AnyHashable: Any] {
        [
            "Player State": state, "Name": song.name, "Artist": song.artist, "Album": song.album,
            "Track ID": song.id, "Duration": NSNumber(value: Int(song.milliseconds)),
            "Playback Position": NSNumber(value: position), "Has Artwork": true,
        ]
    }

    private func text(_ value: String) -> NSAppleEventDescriptor { NSAppleEventDescriptor(string: value) }
}

/// One runner for the monitor, routing each script to its player -- past
/// macOS's Automation prompt when `consent` has one waiting.
final class FakePlayers: AppleScriptRunning {
    let music: FakeMusicApp
    let spotify: FakeSpotifyApp
    let consent: FakeAutomationPrompt?
    /// Every script sent, whether or not it got past the prompt.
    private(set) var sent: [PlayerScript] = []

    init(music: FakeMusicApp, spotify: FakeSpotifyApp, consent: FakeAutomationPrompt? = nil) {
        self.music = music
        self.spotify = spotify
        self.consent = consent
    }

    func sent(to bundleID: String) -> [PlayerScript] {
        sent.filter { $0.name.hasPrefix(bundleID == "com.spotify.client" ? "spotify." : "music.") }
    }

    func clearSent() { sent.removeAll() }

    func run(_ script: PlayerScript) -> Result<NSAppleEventDescriptor, ScriptFailure> {
        sent.append(script)
        let isSpotify = script.name.hasPrefix("spotify.")
        // A script's own "is not running" check sends nothing, so raises nothing.
        if isSpotify ? spotify.running : music.running,
           let failure = consent?.screen(script, for: isSpotify ? "com.spotify.client" : "com.apple.Music") {
            return .failure(failure)
        }
        return isSpotify ? spotify.run(script) : music.run(script)
    }
}

/// macOS's Automation prompt as a real Apple event meets it. For a player
/// macOS has no decision on record for (`undecided`), the event waits on the
/// prompt for as long as its own `with timeout` allows -- the test clock
/// moves on by that much -- then fails with -1712, and the prompt goes with
/// it. An answer given within that time is on record from then on.
final class FakeAutomationPrompt: @unchecked Sendable {
    struct Answer {
        var allow: Bool
        /// Seconds the user takes once the prompt is up.
        var after: TimeInterval
    }

    let clock: TestClock
    /// Players macOS would ask about on their next Apple event.
    var undecided: Set<String> = []
    /// Players the user refused: their events fail at once, unprompted.
    var refused: Set<String> = []
    /// How the user answers each prompt for a player. Unlisted, nobody does.
    var answers: [String: Answer] = [:]
    /// Runs while a prompt is up, before it is answered or withdrawn.
    var whileShown: ((String) -> Void)?
    /// Every prompt raised, and how long its event was prepared to wait.
    private(set) var shown: [(bundleID: String, timeout: TimeInterval)] = []

    init(clock: TestClock) {
        self.clock = clock
    }

    func prompts(_ bundleID: String) -> Int {
        shown.filter { $0.bundleID == bundleID }.count
    }

    /// The failure the event meets, or nil to let it through to the player.
    func screen(_ script: PlayerScript, for bundleID: String) -> ScriptFailure? {
        if refused.contains(bundleID) { return ScriptFailure(code: AppleEventCode.notPermitted) }
        guard undecided.contains(bundleID) else { return nil }
        let timeout = Self.timeout(of: script)
        shown.append((bundleID, timeout))
        whileShown?(bundleID)
        guard let answer = answers[bundleID], answer.after < timeout else {
            clock.now += timeout
            return ScriptFailure(code: AppleEventCode.timedOut)
        }
        clock.now += answer.after
        undecided.remove(bundleID)
        guard answer.allow else {
            refused.insert(bundleID)
            return ScriptFailure(code: AppleEventCode.notPermitted)
        }
        return nil
    }

    /// The script's `with timeout`, or AppleScript's two minutes without one.
    static func timeout(of script: PlayerScript) -> TimeInterval {
        guard let range = script.source.range(of: #"with timeout of [0-9]+ seconds"#, options: .regularExpression),
              let seconds = script.source[range].split(separator: " ").dropFirst(3).first.flatMap({ TimeInterval($0) })
        else { return 120 }
        return seconds
    }
}

@MainActor
final class FakePlayerEnvironment: PlayerEnvironment {
    var running: Set<String>
    var installed: Set<String>
    private(set) var events: PlayerEnvironmentEvents?
    private(set) var observedNotifications: [String] = []

    init(running: Set<String>, installed: Set<String>? = nil) {
        self.running = running
        self.installed = installed ?? ["com.apple.Music", "com.spotify.client"]
    }

    func isRunning(_ bundleID: String) -> Bool { running.contains(bundleID) }
    func isInstalled(_ bundleID: String) -> Bool { installed.contains(bundleID) }

    func startObserving(notifications: [String], events: PlayerEnvironmentEvents) {
        observedNotifications = notifications
        self.events = events
    }

    func launch(_ bundleID: String) {
        running.insert(bundleID)
        events?.launched(bundleID)
    }

    func terminate(_ bundleID: String) {
        running.remove(bundleID)
        events?.terminated(bundleID)
    }

    func post(_ name: String, _ userInfo: [AnyHashable: Any]?) {
        events?.notification(name, userInfo)
    }

    func activate() { events?.activated() }
}

/// Runs the monitor's queued work only when a test says so, in order, on
/// the main thread.
///
/// By default script work and main-actor work share one queue, so a reply
/// is handled only after the jobs queued before it. `split` keeps them
/// apart, as in the app: the main actor handles a reply as soon as it is
/// posted, while the script queue may still be busy with the next Apple
/// event. Races between the two live in that gap.
final class ManualScheduler: NowPlayingScheduler, @unchecked Sendable {
    let split: Bool
    private var jobs: [() -> Void] = []
    private var mainJobs: [() -> Void] = []
    private var delayed: [() -> Void] = []

    init(split: Bool = false) {
        self.split = split
    }

    var pending: Int { jobs.count + mainJobs.count }

    func background(_ work: @escaping @Sendable () -> Void) { jobs.append(work) }

    func permission(_ work: @escaping @Sendable () -> Void) { jobs.append(work) }

    func main(_ work: @escaping @MainActor @Sendable () -> Void) {
        let job = { MainActor.assumeIsolated { work() } }
        if split { mainJobs.append(job) } else { jobs.append(job) }
    }

    func after(_ delay: TimeInterval, _ work: @escaping @MainActor @Sendable () -> Void) {
        delayed.append { MainActor.assumeIsolated { work() } }
    }

    /// Runs queued work, including work it queues, until none is left.
    /// Split, the main actor catches up after every background job.
    func drain(file: StaticString = #filePath, line: UInt = #line) {
        var steps = 0
        while !jobs.isEmpty || !mainJobs.isEmpty {
            if !mainJobs.isEmpty { mainJobs.removeFirst()() } else { jobs.removeFirst()() }
            steps += 1
            if steps > 10_000 { XCTFail("Scheduler did not settle", file: file, line: line); return }
        }
    }

    /// Runs only the next queued job.
    func step() {
        guard !jobs.isEmpty else { return }
        jobs.removeFirst()()
    }

    /// Split: runs the next script-queue job alone.
    func runBackground() {
        guard !jobs.isEmpty else { return }
        jobs.removeFirst()()
    }

    /// Split: handles every reply posted so far, and any they post.
    func runMain() {
        while !mainJobs.isEmpty { mainJobs.removeFirst()() }
    }

    func fireDelayed() {
        let due = delayed
        delayed.removeAll()
        due.forEach { $0() }
        drain()
    }
}

final class TestClock: @unchecked Sendable {
    var now: TimeInterval = 1_000
}

/// AEDeterminePermissionToAutomateTarget, answered from plain state.
final class PermissionStub: @unchecked Sendable {
    /// What every player reports unless `statuses` names it.
    var status: OSStatus = 0
    var statuses: [String: OSStatus] = [:]
    /// How the user answers macOS's prompt for a player: 0 for OK, -1743
    /// for Don't Allow. Unlisted, the prompt changes nothing.
    var answers: [String: OSStatus] = [:]
    /// Runs while a check that asks is blocked on macOS's prompt.
    var whileAsking: ((String) -> Void)?
    private(set) var asks: [Bool] = []
    private(set) var requests: [(bundleID: String, ask: Bool)] = []

    func check(_ bundleID: String, _ ask: Bool) -> OSStatus {
        asks.append(ask)
        requests.append((bundleID, ask))
        if ask { whileAsking?(bundleID) }
        if ask, let answer = answers[bundleID] { statuses[bundleID] = answer }
        return statuses[bundleID] ?? status
    }

    /// How often macOS's prompt was raised for a player.
    func prompts(_ bundleID: String) -> Int {
        requests.filter { $0.bundleID == bundleID && $0.ask }.count
    }
}

/// Counts calls from any thread.
final class CallCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var calls: [String] = []

    func record(_ value: String) {
        lock.lock(); defer { lock.unlock() }
        calls.append(value)
    }

    var values: [String] {
        lock.lock(); defer { lock.unlock() }
        return calls
    }
}

@MainActor
final class PlayerHarness {
    /// The queue order a harness gets unless a test chooses: the split-queue
    /// suites turn this on to run every player test in the app's order too.
    nonisolated(unsafe) static var splitQueuesByDefault = false

    let music: FakeMusicApp
    let spotify: FakeSpotifyApp
    let environment: FakePlayerEnvironment
    let scheduler: ManualScheduler
    let clock: TestClock
    let permission: PermissionStub
    /// macOS's prompt as Apple events meet it. Nothing is undecided unless a test says so.
    let consent: FakeAutomationPrompt
    let players: FakePlayers
    let monitor: NowPlayingMonitor

    init(music: FakeMusicApp = FakeMusicApp(), spotify: FakeSpotifyApp = FakeSpotifyApp(),
         running: Set<String> = ["com.apple.Music"], services: NowPlayingServices = .offline, split: Bool? = nil) {
        let environment = FakePlayerEnvironment(running: running)
        let scheduler = ManualScheduler(split: split ?? Self.splitQueuesByDefault)
        let clock = TestClock(), permission = PermissionStub()
        let consent = FakeAutomationPrompt(clock: clock)
        let players = FakePlayers(music: music, spotify: spotify, consent: consent)
        self.music = music
        self.spotify = spotify
        self.environment = environment
        self.scheduler = scheduler
        self.clock = clock
        self.permission = permission
        self.consent = consent
        self.players = players
        monitor = NowPlayingMonitor(runner: players, environment: environment,
                                    scheduler: scheduler, permission: { permission.check($0, $1) },
                                    services: services, uptime: { clock.now }, pollInterval: nil)
    }

    func start(promptsExplained: Bool = false) {
        monitor.start(promptsExplained: promptsExplained)
        scheduler.drain()
    }

    func postMusic(_ song: FakeMusicApp.Song, _ state: String) {
        environment.post("com.apple.Music.playerInfo", FakeMusicApp.notification(song, state: state))
    }

    func postSpotify(_ song: FakeSpotifyApp.Song, _ state: String, at position: TimeInterval) {
        environment.post("com.spotify.client.PlaybackStateChanged",
                         FakeSpotifyApp.notification(song, state: state, position: position))
    }

    /// Waits for main-actor tasks (artwork and cover lookups) to finish.
    func settle(until condition: () -> Bool, timeout: TimeInterval = 2) async {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() && Date() < deadline {
            try? await Task.sleep(nanoseconds: 5_000_000)
            scheduler.drain()
        }
    }
}
