import AppKit
import Combine
import CoreServices
import ImageIO

/// Whether this process is the XCTest host. The same check gates the app
/// delegate's start-up; the live script runner and environment use it too,
/// so a test can never message the user's real players.
enum AppRuntime {
    static var isUnitTesting: Bool {
        ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil
            || NSClassFromString("XCTestCase") != nil
    }
}

struct ScriptFailure: Error, Equatable, Sendable {
    let code: Int
}

/// Executes player scripts. Injected so state transitions can be tested
/// without sending a single real Apple event.
protocol AppleScriptRunning: AnyObject {
    func run(_ script: PlayerScript) -> Result<NSAppleEventDescriptor, ScriptFailure>
}

/// NSAppleScript, compiled once per script and reused: compiling on every
/// poll costs far more than executing. Confined to the monitor's script queue.
final class LiveAppleScriptRunner: AppleScriptRunning {
    /// A script and its consent-waiting copy share a name, not a source.
    private struct Key: Hashable {
        let name: String
        let waitsForConsent: Bool
    }

    private var compiled: [Key: NSAppleScript] = [:]

    func run(_ script: PlayerScript) -> Result<NSAppleEventDescriptor, ScriptFailure> {
        guard !AppRuntime.isUnitTesting else {
            return .failure(ScriptFailure(code: AppleEventCode.processNotFound))
        }
        let key = Key(name: script.name, waitsForConsent: script.waitsForConsent)
        let appleScript: NSAppleScript
        if script.cacheable, let cached = compiled[key] {
            appleScript = cached
        } else {
            guard let fresh = NSAppleScript(source: script.source) else { return .failure(ScriptFailure(code: -2700)) }
            if script.cacheable { compiled[key] = fresh }
            appleScript = fresh
        }
        var errorInfo: NSDictionary?
        let result = appleScript.executeAndReturnError(&errorInfo)
        if let errorInfo {
            let code = (errorInfo[NSAppleScript.errorNumber] as? NSNumber)?.intValue ?? -2700
            return .failure(ScriptFailure(code: code))
        }
        return .success(result)
    }
}

/// A full reading plus a position sample that belongs to the same track.
struct RefreshReading: Sendable {
    var read: PlayerResponse<PlayerSnapshot>
    var sample: PlayerResponse<PositionSample>?
}

/// What the player's own replies have shown this session about Lunalyr's
/// macOS Automation consent. A permission check answers from what macOS has
/// on record, and after an update with a different ad-hoc signature that
/// can be the previous build's grant while the first real Apple event still
/// raises the prompt. Only a reply proves consent. Consent is Lunalyr's with
/// the player app, not with one launch of it, so it outlives a relaunch.
enum PlayerConsent: Equatable, Sendable {
    /// No reply yet: the next event may raise macOS's prompt, so it waits
    /// `PlayerScriptSource.consentTimeout` for the user to answer.
    case unproven
    case proven
    /// macOS wants an answer it has not got: an event waited out its
    /// consent timeout unanswered, and the prompt went with it, or a check
    /// found the player undecided. Nothing is sent until the user is asked:
    /// by Allow Access, or by playing something in the player.
    case unanswered
    /// Refused (-1743). Nothing is sent until a check finds it allowed.
    case refused
}

/// The only code that sends Apple events. Scripts run solely on the
/// monitor's serial script queue, so the runner's compile cache needs no
/// lock; `accessStatus` shares no state and runs on the permission queue.
///
/// Every send first asks the consent ledger. While a player's consent is
/// unproven the event waits for macOS's prompt to be answered; while its
/// prompt went unanswered or was refused nothing is sent; after a proven
/// player times out (hung, or busy with a dialog of its own) polling backs
/// off until it answers or posts a notification.
final class PlayerConnection: @unchecked Sendable {
    private let runner: AppleScriptRunning
    private let permission: @Sendable (String, Bool) -> OSStatus
    let uptime: @Sendable () -> TimeInterval
    private let ledger = ConsentLedger()

    init(runner: AppleScriptRunning, permission: @escaping @Sendable (String, Bool) -> OSStatus,
         uptime: @escaping @Sendable () -> TimeInterval) {
        self.runner = runner
        self.permission = permission
        self.uptime = uptime
    }

    func read(_ backend: any PlayerBackend) -> PlayerResponse<PlayerSnapshot> {
        switch exchange(backend.readScript, with: backend.kind, polling: true).outcome {
        case .success(let reply): return backend.parseRead(reply)
        case .failure(let failure): return PlayerResponse(failureCode: failure.code)
        }
    }

    /// A round trip takes a while. Stamping the anchor when the call returns
    /// back-dates playback by that much and drags every lyric late; the
    /// player sampled the value somewhere inside the call, so the midpoint
    /// is the better estimate. Not for an event that may have waited on
    /// macOS's prompt: the player answered once the user had.
    func samplePosition(_ backend: any PlayerBackend) -> PlayerResponse<PositionSample> {
        let startedAt = uptime()
        let (outcome, probed) = exchange(backend.positionScript, with: backend.kind, polling: true)
        let finishedAt = uptime()
        let sampledAt = probed ? finishedAt : startedAt + max(0, finishedAt - startedAt) / 2
        switch outcome {
        case .success(let reply): return backend.parsePosition(reply, at: sampledAt)
        case .failure(let failure): return PlayerResponse(failureCode: failure.code)
        }
    }

    /// Metadata, then the position. If the identifiers disagree the track
    /// changed between the two, and the metadata is read once more.
    func refresh(_ backend: any PlayerBackend) -> RefreshReading {
        var retried = false
        while true {
            let read = self.read(backend)
            guard let track = read.value?.track else { return RefreshReading(read: read, sample: nil) }
            let sample = samplePosition(backend)
            if !retried, let sampled = sample.value?.trackID, let known = track.persistentID, sampled != known {
                retried = true
                continue
            }
            return RefreshReading(read: read, sample: sample)
        }
    }

    func artwork(_ backend: any PlayerBackend) -> PlayerResponse<ArtworkReply> {
        var last = PlayerResponse<ArtworkReply>.value(ArtworkReply(trackID: nil, reading: .none))
        for script in backend.artworkScripts {
            let response: PlayerResponse<ArtworkReply>
            switch exchange(script, with: backend.kind, polling: true).outcome {
            case .success(let reply): response = backend.parseArtwork(reply)
            case .failure(let failure): response = PlayerResponse(failureCode: failure.code)
            }
            switch response {
            case .value(let reply):
                if case .image(let data) = reply.reading, !Self.isDecodableImage(data) {
                    last = .value(ArtworkReply(trackID: reply.trackID, reading: .none))
                    continue
                }
                return response
            case .failed:
                last = response
            case .notRunning, .blocked:
                return response
            }
        }
        return last
    }

    /// A command or seek the user asked for at `requestedAt` (`uptime`).
    /// Never held back by a back-off; but one that waited behind other
    /// events for longer than `commandDeadline` -- behind an event waiting
    /// on macOS's prompt, say -- is not sent at all (`notSent`): a skip or a
    /// seek landing long after the press is worse than one that never does.
    func perform(_ script: PlayerScript, on kind: PlayerKind, requestedAt: TimeInterval) -> PlayerResponse<Bool> {
        guard uptime() - requestedAt <= Self.commandDeadline else { return .failed(AppleEventCode.notSent) }
        switch exchange(script, with: kind, polling: false).outcome {
        case .success(let reply): return reply.scriptText == "notRunning" ? .notRunning : .value(true)
        case .failure(let failure): return PlayerResponse(failureCode: failure.code)
        }
    }

    /// Seconds a command may wait to be sent: more than the re-syncs queued
    /// ahead of it normally take, less than a hung player's timeout or
    /// anyone's answer to macOS's prompt.
    static let commandDeadline: TimeInterval = 3

    /// AEDeterminePermissionToAutomateTarget. Sends the player nothing; with
    /// `ask` it blocks while macOS's prompt is up -- for as long as the user
    /// takes, unlike an Apple event, which gives up at its timeout.
    func accessStatus(_ kind: PlayerKind, ask: Bool) -> PlayerAccessStatus {
        PlayerAccessStatus(status: permission(kind.bundleID, ask))
    }

    // MARK: - Consent

    func consent(_ kind: PlayerKind) -> PlayerConsent { ledger.consent(kind) }

    /// Whether an event that may be showing macOS's prompt for this player
    /// is in flight. Its outcome settles the player; nothing else should ask.
    func isAwaitingConsent(_ kind: PlayerKind) -> Bool { ledger.isProbing(kind) }

    /// Whether such an event is in flight for any player. It holds the
    /// script queue until the user answers or the prompt goes, so a button
    /// pressed now would reach its player only then.
    var queueAwaitsConsent: Bool { ledger.isProbingAny }

    /// Takes in a permission check's answer, and whether it may move the
    /// player on. A check reporting the player allowed lifts a refusal (the
    /// user changed System Settings) but lifts an unanswered prompt only if
    /// it asked: a silent check can report an old build's grant while the
    /// next event would raise the prompt again. One reporting the player
    /// undecided holds every send until the user is asked, whether that is
    /// now or once it is expected, so no event raises the prompt meanwhile.
    func admits(_ status: PlayerAccessStatus, prompted: Bool, for kind: PlayerKind) -> Bool {
        ledger.admits(status, prompted: prompted, for: kind)
    }

    /// The player quit. Its consent carries over to its next launch --
    /// proven, refused or waiting on the user -- but the back-off, and
    /// replies still on their way, belonged to this one.
    func reset(_ kind: PlayerKind) { ledger.reset(kind) }

    /// The player posted a notification, so it is up: a re-sync held back
    /// after a timeout may go now, such as the read of a new track's cover.
    func resume(_ kind: PlayerKind) { ledger.resume(kind) }

    /// Runs a script unless the ledger holds it back, with the consent
    /// timeout while the player's consent is unproven. `probed`: it did.
    private func exchange(_ script: PlayerScript, with kind: PlayerKind,
                          polling: Bool) -> (outcome: Result<NSAppleEventDescriptor, ScriptFailure>, probed: Bool) {
        switch ledger.begin(kind, polling: polling, now: uptime()) {
        case .withhold(let failure):
            return (.failure(failure), false)
        case .send(let ticket):
            let outcome = runner.run(ticket.probing ? script.waitingForConsent : script)
            return (ledger.finish(ticket, outcome, now: uptime()), ticket.probing)
        }
    }

    static func isDecodableImage(_ data: Data) -> Bool {
        guard !data.isEmpty, let source = CGImageSourceCreateWithData(data as CFData, nil) else { return false }
        return CGImageSourceGetCount(source) > 0
    }
}

/// Per player: its consent as replies have shown it, whether an event that
/// may be showing macOS's prompt is in flight, and the back-off after a
/// timeout. The script queue consults it before every send; the main actor
/// moves it on after a permission check, a notification, or when the
/// player quits.
private final class ConsentLedger: @unchecked Sendable {
    struct Ticket {
        let kind: PlayerKind
        /// The event may raise macOS's prompt and waits for the answer.
        let probing: Bool
        /// Outcomes of events sent before a reset are about a launch that has quit.
        let generation: Int
    }

    enum Attempt {
        case send(Ticket)
        /// Not sent; reported as this failure instead.
        case withhold(ScriptFailure)
    }

    private struct Entry {
        var consent = PlayerConsent.unproven
        var generation = 0
        var probing = false
        /// Timeouts in a row from a proven player, and when polling resumes.
        var timeouts = 0
        var retryAt = -TimeInterval.infinity
    }

    private let lock = NSLock()
    private var entries: [PlayerKind: Entry] = [:]

    /// Polling waits 4, 8, 16, 32 then 60 seconds after successive timeouts.
    static func retryDelay(afterTimeouts count: Int) -> TimeInterval {
        min(60, 2 * pow(2, Double(min(max(count, 1), 6))))
    }

    func consent(_ kind: PlayerKind) -> PlayerConsent {
        lock.lock(); defer { lock.unlock() }
        return entries[kind]?.consent ?? .unproven
    }

    func isProbing(_ kind: PlayerKind) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return entries[kind]?.probing ?? false
    }

    var isProbingAny: Bool {
        lock.lock(); defer { lock.unlock() }
        return entries.values.contains { $0.probing }
    }

    func begin(_ kind: PlayerKind, polling: Bool, now: TimeInterval) -> Attempt {
        lock.lock(); defer { lock.unlock() }
        var entry = entries[kind, default: Entry()]
        switch entry.consent {
        case .unanswered:
            return .withhold(ScriptFailure(code: AppleEventCode.wouldRequireConsent))
        case .refused:
            return .withhold(ScriptFailure(code: AppleEventCode.notPermitted))
        case .proven:
            // Held back, so not reported as a timeout: each of those has
            // the monitor check whether macOS still has consent on record.
            if polling, now < entry.retryAt { return .withhold(ScriptFailure(code: AppleEventCode.notSent)) }
        case .unproven:
            entry.probing = true
        }
        entries[kind] = entry
        return .send(Ticket(kind: kind, probing: entry.probing, generation: entry.generation))
    }

    /// Records what the event showed, and returns its outcome: a probe that
    /// timed out is reported as consent still needed, not as a busy player.
    func finish(_ ticket: Ticket, _ outcome: Result<NSAppleEventDescriptor, ScriptFailure>,
                now: TimeInterval) -> Result<NSAppleEventDescriptor, ScriptFailure> {
        lock.lock(); defer { lock.unlock() }
        guard var entry = entries[ticket.kind], entry.generation == ticket.generation else { return outcome }
        defer { entries[ticket.kind] = entry }
        if ticket.probing { entry.probing = false }
        switch outcome {
        case .success(let reply):
            // The script's own "not running" answer addressed nothing.
            guard reply.scriptItems.first?.scriptText != "notRunning" else { return outcome }
            entry.consent = .proven
            entry.timeouts = 0
            entry.retryAt = -.infinity
        case .failure(let failure):
            switch failure.code {
            case AppleEventCode.timedOut where ticket.probing:
                // Unanswered, or a player that never answered at all: either
                // way consent is still pending, and asking again is the user's
                // call. (A check may have found it undecided meanwhile.)
                if entry.consent == .unproven { entry.consent = .unanswered }
                guard entry.consent == .unanswered else { return outcome }
                return .failure(ScriptFailure(code: AppleEventCode.wouldRequireConsent))
            case AppleEventCode.timedOut where entry.consent == .proven:
                entry.timeouts += 1
                entry.retryAt = now + Self.retryDelay(afterTimeouts: entry.timeouts)
            case AppleEventCode.wouldRequireConsent:
                entry.consent = .unanswered
            case AppleEventCode.notPermitted:
                entry.consent = .refused
            default:
                break
            }
        }
        return outcome
    }

    func admits(_ status: PlayerAccessStatus, prompted: Bool, for kind: PlayerKind) -> Bool {
        lock.lock(); defer { lock.unlock() }
        var entry = entries[kind, default: Entry()]
        defer { entries[kind] = entry }
        switch status {
        case .denied:
            entry.consent = .refused
        case .granted, .unknown:
            switch entry.consent {
            case .unanswered:
                guard prompted else { return false }
                entry.consent = .unproven
            case .refused:
                entry.consent = .unproven
            case .unproven, .proven:
                break
            }
        case .notDetermined:
            // Not on record (any more): the next event would raise the
            // prompt, so none goes until the user is asked.
            entry.consent = .unanswered
        case .notRunning, .notInstalled:
            break
        }
        return true
    }

    func reset(_ kind: PlayerKind) {
        lock.lock(); defer { lock.unlock() }
        let old = entries[kind] ?? Entry()
        entries[kind] = Entry(consent: old.consent, generation: old.generation &+ 1)
    }

    /// Ends a back-off early. The count of timeouts stays: should the
    /// player time out again, the next wait is longer still.
    func resume(_ kind: PlayerKind) {
        lock.lock(); defer { lock.unlock() }
        guard let entry = entries[kind], entry.retryAt > -.infinity else { return }
        entries[kind]?.retryAt = -.infinity
    }
}

/// Coalesces requests: at most one queued job per key, besides the one
/// running. A burst of skips therefore costs one read of the latest track
/// rather than one read per notification.
final class RequestGate: @unchecked Sendable {
    private let lock = NSLock()
    private var queued = Set<String>()

    /// True when the caller should schedule the job.
    func enter(_ key: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return queued.insert(key).inserted
    }

    /// Called as the job starts, so requests arriving meanwhile schedule a fresh one.
    func begin(_ key: String) {
        lock.lock(); defer { lock.unlock() }
        queued.remove(key)
    }
}

/// Advanced whenever something newer than a script reply can be (a player
/// notification, a seek, the player quitting), so a reply already in flight
/// is not applied over it.
final class PlayerEpochs: @unchecked Sendable {
    private let lock = NSLock()
    private var values: [PlayerKind: Int] = [:]

    func value(_ kind: PlayerKind) -> Int {
        lock.lock(); defer { lock.unlock() }
        return values[kind, default: 0]
    }

    func advance(_ kind: PlayerKind) {
        lock.lock(); defer { lock.unlock() }
        values[kind, default: 0] &+= 1
    }
}

/// A counter the main actor moves on and a queued job reads, so a job made
/// obsolete while it waited can skip its Apple events and its reply can be
/// recognised as stale.
final class Generation: @unchecked Sendable {
    private let lock = NSLock()
    private var current = 0

    var value: Int {
        lock.lock(); defer { lock.unlock() }
        return current
    }

    @discardableResult
    func advance() -> Int {
        lock.lock(); defer { lock.unlock() }
        current &+= 1
        return current
    }
}

/// Where the monitor's work runs. Live: Apple events on one serial queue,
/// permission checks on another, state on the main actor. Tests drive all
/// of them by hand.
protocol NowPlayingScheduler: AnyObject, Sendable {
    func background(_ work: @escaping @Sendable () -> Void)
    /// Permission checks. One that asks blocks until the user answers
    /// macOS's prompt, so these never share the script queue: a prompt for
    /// one player must not hold up the other player's re-sync.
    func permission(_ work: @escaping @Sendable () -> Void)
    func main(_ work: @escaping @MainActor @Sendable () -> Void)
    func after(_ delay: TimeInterval, _ work: @escaping @MainActor @Sendable () -> Void)
}

final class LiveNowPlayingScheduler: NowPlayingScheduler, @unchecked Sendable {
    private let queue = DispatchQueue(label: "com.akki.verso.applescript", qos: .userInitiated)
    private let permissionQueue = DispatchQueue(label: "com.akki.verso.automation", qos: .userInitiated)

    func background(_ work: @escaping @Sendable () -> Void) {
        queue.async(execute: work)
    }

    func permission(_ work: @escaping @Sendable () -> Void) {
        permissionQueue.async(execute: work)
    }

    func main(_ work: @escaping @MainActor @Sendable () -> Void) {
        DispatchQueue.main.async { MainActor.assumeIsolated { work() } }
    }

    func after(_ delay: TimeInterval, _ work: @escaping @MainActor @Sendable () -> Void) {
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { MainActor.assumeIsolated { work() } }
    }
}

/// System events the monitor follows.
struct PlayerEnvironmentEvents {
    var launched: @MainActor (String) -> Void
    var terminated: @MainActor (String) -> Void
    var notification: @MainActor (String, [AnyHashable: Any]?) -> Void
    var activated: @MainActor () -> Void
}

/// What the monitor needs from the system, so tests can stand in for it.
@MainActor
protocol PlayerEnvironment: AnyObject {
    func isRunning(_ bundleID: String) -> Bool
    func isInstalled(_ bundleID: String) -> Bool
    func startObserving(notifications: [String], events: PlayerEnvironmentEvents)
}

@MainActor
final class LivePlayerEnvironment: NSObject, PlayerEnvironment {
    private var events: PlayerEnvironmentEvents?
    private var observers: [NSObjectProtocol] = []

    func isRunning(_ bundleID: String) -> Bool {
        guard !AppRuntime.isUnitTesting else { return false }
        return NSWorkspace.shared.runningApplications.contains {
            $0.bundleIdentifier == bundleID && !$0.isTerminated
        }
    }

    func isInstalled(_ bundleID: String) -> Bool {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) != nil
    }

    func startObserving(notifications: [String], events: PlayerEnvironmentEvents) {
        guard !AppRuntime.isUnitTesting, self.events == nil else { return }
        self.events = events
        let workspace = NSWorkspace.shared.notificationCenter
        for (name, isLaunch) in [(NSWorkspace.didLaunchApplicationNotification, true),
                                 (NSWorkspace.didTerminateApplicationNotification, false)] {
            observers.append(workspace.addObserver(forName: name, object: nil, queue: .main) { [weak self] note in
                guard let id = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?
                    .bundleIdentifier else { return }
                MainActor.assumeIsolated {
                    if isLaunch { self?.events?.launched(id) } else { self?.events?.terminated(id) }
                }
            })
        }
        observers.append(NotificationCenter.default.addObserver(
            forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.events?.activated() }
        })
        // Delivered immediately: AppKit otherwise holds distributed
        // notifications while the app is inactive, which a menu-bar app
        // almost always is.
        let distributed = DistributedNotificationCenter.default()
        for name in notifications {
            distributed.addObserver(self, selector: #selector(playerNotification(_:)),
                                    name: Notification.Name(name), object: nil,
                                    suspensionBehavior: .deliverImmediately)
        }
    }

    @objc nonisolated private func playerNotification(_ note: Notification) {
        let name = note.name.rawValue
        let info = note.userInfo
        let deliver = { @MainActor [weak self] in self?.events?.notification(name, info) }
        if Thread.isMainThread {
            MainActor.assumeIsolated { deliver() }
        } else {
            DispatchQueue.main.async { MainActor.assumeIsolated { deliver() } }
        }
    }

    /// Never prompts unless asked to. Requires the player to be running.
    nonisolated static func automationPermission(_ bundleID: String, _ askUserIfNeeded: Bool) -> OSStatus {
        guard !AppRuntime.isUnitTesting else { return OSStatus(AppleEventCode.processNotFound) }
        let target = NSAppleEventDescriptor(bundleIdentifier: bundleID)
        return AEDeterminePermissionToAutomateTarget(target.aeDesc, DescriptorCode.coreSuite,
                                                     DescriptorCode.getData, askUserIfNeeded)
    }
}

/// Network and settings collaborators, injectable for tests.
struct NowPlayingServices {
    /// Public catalogue cover for tracks whose player exposes none.
    var catalogueArtwork: @Sendable (NowPlayingMonitor.Track) async -> Data?
    /// A cover the player published by address.
    var downloadArtwork: @Sendable (URL) async -> Data?
    var motionArtwork: @Sendable (NowPlayingMonitor.Track) async -> MotionArtworkProvider.Motion?
    /// Whether an animated cover could be shown for this track at all.
    var allowsMotionLookup: @MainActor (NowPlayingMonitor.Track) -> Bool
    /// Fires after changes that can flip `allowsMotionLookup`.
    var motionSettingsChanged: AnyPublisher<Void, Never>
    /// The players to follow: the current choice first, then each change.
    var followedPlayers: AnyPublisher<[PlayerKind], Never>

    @MainActor static var live: NowPlayingServices {
        NowPlayingServices(
            catalogueArtwork: { track in
                await ArtworkProvider.shared.artworkData(title: track.title, artist: track.artist,
                                                         album: track.album, duration: track.duration)
            },
            downloadArtwork: { url in await ArtworkProvider.shared.imageData(at: url) },
            motionArtwork: { track in
                await MotionArtworkProvider.shared.motion(album: track.album, artist: track.artist, title: track.title)
            },
            allowsMotionLookup: { track in
                // Animated covers only ever play in Match the music.
                let settings = Settings.shared
                return settings.motionArtworkEnabled && settings.backgroundMode == .auto
                    && !settings.excludesMotion(artist: track.artist, album: track.album)
            },
            motionSettingsChanged: Settings.shared.objectWillChange
                .debounce(for: .milliseconds(150), scheduler: RunLoop.main)
                .map { _ in () }
                .eraseToAnyPublisher(),
            followedPlayers: PlayerOptions.shared.playersPublisher
        )
    }

    static var offline: NowPlayingServices {
        NowPlayingServices(
            catalogueArtwork: { _ in nil }, downloadArtwork: { _ in nil }, motionArtwork: { _ in nil },
            allowsMotionLookup: { _ in false }, motionSettingsChanged: Empty().eraseToAnyPublisher(),
            followedPlayers: Just(PlayerKind.allCases).eraseToAnyPublisher()
        )
    }
}
