import AppKit
import Combine

/// Now-playing state from the music players' public AppleScript
/// dictionaries. Track changes arrive as distributed notifications;
/// playback position is re-anchored on a slow timer and extrapolated in
/// between, so word-level lyric sync stays fluid without firing an Apple
/// event on every frame.
///
/// - Nothing is ever sent to a player that is not already running
///   (AppleScript would launch it), and the timer runs only while one is.
/// - Nor to one macOS has not yet asked the user about, until that question
///   is expected (see `prepare`): an app that is merely open must not raise
///   "Lunalyr wants to control Spotify" out of the blue.
/// - A paused track stays current: pausing is not "nothing playing".
/// - When macOS Automation access is off, polling stops and the UI can say
///   so instead of looking idle.
@MainActor
final class NowPlayingMonitor: ObservableObject {
    static let shared = NowPlayingMonitor()

    struct Track: Equatable, Sendable {
        var title: String
        var artist: String
        var duration: Double
        var genre: String?
        var album: String?
        /// Music's persistent ID or the player's own track identifier, when
        /// known. Not part of `==`: lyric identity is the recording.
        var persistentID: String? = nil
        /// Live radio and other continuous streams: duration is 0, there is
        /// nothing to scrub and nothing to look lyrics up for.
        var isStream = false
        /// A player's advert. Handled like a stream -- no lookups, no
        /// scrubber -- but named as what it is, not as something live.
        var isAdvert = false

        static func == (lhs: Track, rhs: Track) -> Bool {
            lhs.title == rhs.title && lhs.artist == rhs.artist &&
                lhs.album == rhs.album && abs(lhs.duration - rhs.duration) < 0.5
        }

        /// The same recording and, where both carry one, the same identifier.
        func isSameTrack(as other: Track) -> Bool {
            self == other && (persistentID == nil || other.persistentID == nil || persistentID == other.persistentID)
        }
    }

    @Published private(set) var current: Track? {
        didSet {
            if current == nil {
                if idleSince == nil { idleSince = Date() }
            } else {
                idleSince = nil
            }
        }
    }
    /// When the wallpaper's idle notice (`idleTitle`) went up -- playback
    /// stopped, or, while nothing was showing, the notice came to name a
    /// refused player or that player started playing -- so it can fade out
    /// rather than sit on the wallpaper indefinitely. The text backing and
    /// the frame clock count from it too.
    @Published private(set) var idleSince: Date?
    /// The most recent cover. Across a track change it keeps the outgoing
    /// cover until the new one resolves, so wallpaper colours never flash
    /// to the fallback palette. Anything that *shows* a cover should use
    /// `currentTrackArtwork`, which is never another song's.
    @Published private(set) var artwork: NSImage?
    /// The track `artwork` belongs to.
    @Published private(set) var artworkTrack: Track?
    @Published private(set) var isPlaying = false
    /// Apple Music's animated cover, for the minority of albums that have one.
    @Published private(set) var motionArtwork: MotionArtworkProvider.Motion?
    @Published private(set) var isLookingUpMotion = false
    /// The player `current` comes from.
    @Published private(set) var activePlayer: PlayerKind?
    /// What macOS Automation allows, per player, once known.
    @Published private(set) var automation: [PlayerKind: AutomationAccess] = [:]
    @Published private(set) var runningPlayers: Set<PlayerKind> = []
    /// Browsers that answered, but whose music tabs could not be read
    /// because "Allow JavaScript from Apple Events" is off in them.
    @Published private(set) var javaScriptOff: Set<PlayerKind> = []
    /// The players Lunalyr follows (Settings › Player); others are never sent anything.
    @Published private(set) var followedPlayers: [PlayerKind] = PlayerKind.allCases
    /// Whether the re-sync timer is (meant to be) running.
    private(set) var isPolling = false

    var currentTrackArtwork: NSImage? {
        guard let current, let artworkTrack, artworkTrack.isSameTrack(as: current) else { return nil }
        return artwork
    }

    /// Followed browsers with a music tab open that cannot be read until the
    /// user allows JavaScript from Apple Events (`WebScriptingNotice`).
    var webScriptingBlocked: [PlayerKind] {
        followedPlayers.filter { runningPlayers.contains($0) && javaScriptOff.contains($0) && !isBlocked($0) }
    }

    /// Running players that macOS will not let Lunalyr read.
    var blockedPlayers: [PlayerKind] {
        followedPlayers.filter { runningPlayers.contains($0) && isBlocked($0) }
    }

    /// Running players the user has refused Lunalyr in Privacy & Security.
    /// Players macOS simply has not asked about yet are not among them:
    /// playing something in one, or Allow Access in the menu, asks.
    var deniedPlayers: [PlayerKind] {
        blockedPlayers.filter { automation[$0] == .denied }
    }

    /// The refused player the idle notice names, when it may be the one
    /// playing: its own notifications -- which reach Lunalyr however
    /// Automation is set -- last said it was playing, or no player Lunalyr
    /// can read is running beside it. A player refused on purpose and not
    /// used with Lunalyr is otherwise not pointed out every time the other
    /// one stops.
    var idleRefusal: PlayerKind? {
        let denied = deniedPlayers
        if let playing = denied.first(where: { saysPlaying[$0] == true }) { return playing }
        return followedPlayers.contains(where: mayRead) ? nil : denied.first
    }

    /// The player to follow on its own instead of `kind`, which macOS
    /// refuses or has not asked about: someone who doesn't use `kind` with
    /// Lunalyr can then stop being told about it whenever nothing plays.
    /// Nil when `kind` is the only player followed, or the other one is not
    /// installed or is refused too: following it alone would gain nothing.
    func playerToFollowInstead(of kind: PlayerKind) -> PlayerKind? {
        let others = followedPlayers.filter {
            $0 != kind && automation[$0] != .denied && environment.isInstalled($0.bundleID)
        }
        return others.count == 1 ? others.first : nil
    }

    /// The player the transport buttons control: the one on screen, else
    /// the most recently used player that is running and readable.
    var transportTarget: PlayerKind? {
        if let activePlayer, isPollable(activePlayer) { return activePlayer }
        return pollablePlayers.max { (activity[$0] ?? -.infinity) < (activity[$1] ?? -.infinity) }
    }

    var canControlPlayback: Bool { transportTarget != nil }

    /// macOS may be showing its Automation prompt for a player; the transport
    /// waits (and its buttons say so) until the person answers it.
    var awaitsConsent: Bool { connection.queueAwaitsConsent }

    /// "Play a song in Music", naming the players Lunalyr follows that are
    /// installed (all of them followed if none is).
    var idlePrompt: String {
        let installed = followedPlayers.filter { environment.isInstalled($0.bundleID) }
        return "Play a song in \(PlayerKind.names(installed.isEmpty ? followedPlayers : installed))"
    }

    /// What the wallpaper, its hover controls and VoiceOver say while
    /// nothing is current: that macOS keeps Lunalyr from reading the player
    /// that may be playing (`idleRefusal`), and otherwise "Nothing playing".
    /// A player macOS has not asked about yet is not named: playing
    /// something in it asks, and the menu offers Allow Access.
    var idleTitle: String {
        idleRefusal.map { "Lunalyr can’t read \($0.displayName)" } ?? "Nothing playing"
    }

    /// The way out, for a line under `idleTitle`; nil unless it names a refused player.
    var idleDetail: String? {
        idleRefusal == nil ? nil : "Allow it in System Settings › Privacy & Security › Automation"
    }

    private let connection: PlayerConnection
    private let environment: PlayerEnvironment
    private let scheduler: NowPlayingScheduler
    private let services: NowPlayingServices
    private let uptime: @Sendable () -> TimeInterval
    private let pollInterval: TimeInterval?
    private let gate = RequestGate()
    private let epochs = PlayerEpochs()
    /// Moves on with every cover requested, so a reply about an earlier
    /// track is recognised however late it lands.
    private let artworkGeneration = Generation()

    private var started = false
    private var snapshots: [PlayerKind: PlayerSnapshot] = [:]
    /// When each player last started playing, changed track or was commanded.
    private var activity: [PlayerKind: TimeInterval] = [:]
    /// Players Lunalyr may send Apple events to while they run: macOS has
    /// already decided (or cannot say), or its prompt is expected now.
    private var cleared: Set<PlayerKind> = []
    /// Players whose undecided permission may be asked about now: the
    /// first-run welcome explained the prompt, the user chose the player
    /// or started playing something in it.
    private var mayAsk: Set<PlayerKind> = []
    /// Players macOS's prompt was raised for unasked this session, so a
    /// prompt that could not be shown is not retried on every notification,
    /// only when the user starts playing in the player again.
    private var asked: Set<PlayerKind> = []
    /// Players whose prompt is on screen now: the answer settles them.
    private var asking: Set<PlayerKind> = []
    /// Whether each running player's own notifications last said it was
    /// playing. For a refused player that is all Lunalyr can know, and it
    /// decides whether the idle notice names it (`idleRefusal`), so it is
    /// published with the rest.
    @Published private var saysPlaying: [PlayerKind: Bool] = [:]
    /// Players a permission check has just found allowed, or could not say
    /// about, until an Apple event sent to them is answered.
    private var vouched: Set<PlayerKind> = []
    /// Players macOS refused an Apple event straight after their check had
    /// found them allowed, and whose check still says so (`confirmRefusal`).
    /// It would only say so again, and the read after it be refused again,
    /// so their notifications stop re-checking; Lunalyr becoming active, or
    /// the player relaunching, still re-checks.
    private var contradicted: Set<PlayerKind> = []
    /// Players whose last notification since they launched said they were
    /// playing. "Playing" after anything else is someone starting playback,
    /// not the player moving on to its next track.
    private var notifiedPlaying: Set<PlayerKind> = []
    /// A seek per player that has not reached the player yet. Until it has,
    /// every position the player reports is from before it.
    private var seeking: [PlayerKind: Int] = [:]
    private var seeks = 0
    private var clock = PlaybackClock()
    /// The anchor is a guess (a new track assumed to start at zero) rather
    /// than a sample, so the next sample replaces it instead of easing in.
    private var anchorIsEstimate = false
    private var timer: Timer?
    private var ticks = 0
    /// Every display asleep, locked or showing the screen saver.
    private var screensHidden = false
    /// When the player on screen last stopped playing (uptime), while it
    /// stays paused: after `restingAfter` a player that announces its own
    /// changes is re-read like the background ones, not every tick.
    private var pausedSince: TimeInterval?
    private var artworkTask: Task<Void, Never>?
    private var artworkTarget: Track?
    private var motionLookup: Task<Void, Never>?
    /// The album whose lookup `motionArtwork` reflects.
    private var motionKey: String?
    private var motionLookupKey: String?
    private var cancellables: Set<AnyCancellable> = []

    /// Every player re-sync tick; idle or background players every fifth.
    private static let idlePollEvery = 5
    /// Every other tick for idle players that post no notifications.
    private static let quietPollEvery = 2
    /// How long the player on screen stays paused before its re-sync slows
    /// to `idlePollEvery`. Music and Spotify announce play, pause and track
    /// changes themselves; the re-sync of a paused player only catches a
    /// scrub, so a long pause need not wake the player every two seconds.
    static let restingAfter: TimeInterval = 60
    /// Nudges lyrics slightly ahead of the raw clock. An Apple event only
    /// tells us where the player *was*, and the audio itself is buffered a
    /// little behind that, so a small lead lands the highlight on the beat
    /// instead of just after it.
    static let syncLead: TimeInterval = 0.16

    init(runner: AppleScriptRunning = LiveAppleScriptRunner(),
         environment: PlayerEnvironment? = nil,
         scheduler: NowPlayingScheduler = LiveNowPlayingScheduler(),
         permission: @escaping @Sendable (String, Bool) -> OSStatus = { LivePlayerEnvironment.automationPermission($0, $1) },
         services: NowPlayingServices? = nil,
         uptime: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
         pollInterval: TimeInterval? = 2.0) {
        connection = PlayerConnection(runner: runner, permission: permission, uptime: uptime)
        self.environment = environment ?? LivePlayerEnvironment()
        self.scheduler = scheduler
        self.services = services ?? .live
        self.uptime = uptime
        self.pollInterval = pollInterval
    }

    /// Starts following the players. The app delegate calls this after
    /// launch -- after the first-run welcome has explained the permission
    /// prompt, when `promptsExplained` lets macOS ask about the players
    /// open now -- and never inside the XCTest host.
    func start(promptsExplained: Bool = false) {
        guard !started else { return }
        started = true
        if promptsExplained {
            mayAsk.formUnion(PlayerKind.allCases.filter { environment.isRunning($0.bundleID) })
        }
        environment.startObserving(
            notifications: PlayerKind.allCases.compactMap(\.notificationName),
            events: PlayerEnvironmentEvents(
                launched: { [weak self] id in self?.applicationLaunched(id) },
                terminated: { [weak self] id in self?.applicationTerminated(id) },
                notification: { [weak self] name, info in self?.playerNotification(named: name, userInfo: info) },
                activated: { [weak self] in self?.recheckAutomation() }
            )
        )
        services.motionSettingsChanged
            .sink { [weak self] in self?.updateMotionArtwork() }
            .store(in: &cancellables)
        // Delivers the current choice at once, then each change.
        services.followedPlayers
            .sink { [weak self] players in self?.follow(players) }
            .store(in: &cancellables)
        services.screensHidden
            .removeDuplicates()
            .sink { [weak self] hidden in self?.setScreensHidden(hidden) }
            .store(in: &cancellables)
        runningPlayers = Set(followedPlayers.filter { environment.isRunning($0.bundleID) })
        if runningPlayers.isEmpty { clearPlayback() }
        for kind in followedPlayers where runningPlayers.contains(kind) { prepare(kind) }
        updatePolling()
    }

    /// Smoothly extrapolated playback position, safe to call every frame.
    func playbackTime() -> TimeInterval {
        guard let position = clock.position(at: uptime()) else { return 0 }
        return clock.isRunning ? position + Self.syncLead : position
    }

    /// The position itself, without the lyrics' lead: for the music video,
    /// whose offsets are measured against the song, not the highlight.
    func playbackPosition() -> TimeInterval {
        clock.position(at: uptime()) ?? 0
    }

    /// Re-reads every player Lunalyr may currently ask.
    func refresh() {
        for kind in pollablePlayers { requestRead(kind) }
    }

    /// The monitor's own serial script queue, lent to the desktop player:
    /// NSAppleScript is not documented as safe on two threads at once, so
    /// every Apple event Lunalyr sends goes out from this one queue.
    nonisolated var scriptLane: ScriptLane {
        let connection = connection, scheduler = scheduler
        return ScriptLane { script, kind, polling, done in
            scheduler.background { done(connection.run(script, on: kind, polling: polling)) }
        }
    }

    // MARK: - Transport

    func playPause() { send(.playPause) }

    func nextTrack() { send(.next) }

    func previousTrack() { send(.previous) }

    func seek(to seconds: TimeInterval) {
        guard let kind = activePlayer, isPollable(kind), let current, !current.isStream, seconds.isFinite,
              !connection.queueAwaitsConsent else { return }
        let target = min(max(seconds, 0), current.duration > 0 ? current.duration : seconds)
        let now = uptime()
        // Anchor optimistically so the lyric highlight jumps immediately
        // rather than waiting for the next re-sync. Until the seek's own
        // reply is back, every position the player reports was sampled
        // before it took effect -- by a re-sync running now, or queued
        // ahead of the seek on the script queue -- and is ignored, however
        // quickly the main actor gets to it. The script queue is serial and
        // replies are handled in order, so anything handled after the seek's
        // reply was sampled after the seek. Not while an event may be showing
        // macOS's prompt (above): the seek would wait behind it, and the
        // highlight would sit where the player is not until the user answered.
        seeks &+= 1
        let serial = seeks
        seeking[kind] = serial
        epochs.advance(kind)
        clock.sync(to: target, sampledAt: now, running: isPlaying, now: now, continuous: false)
        SyncLog.seek(to: target, track: current.title)
        anchorIsEstimate = false
        let script = kind.backend.seekScript(to: target)
        let connection = connection, epochs = epochs, scheduler = scheduler
        scheduler.background { [weak self] in
            let response = connection.perform(script, on: kind, requestedAt: now)
            epochs.advance(kind)
            scheduler.main { [weak self] in
                guard let self else { return }
                // A later seek keeps its own hold until its reply is back.
                if self.seeking[kind] == serial { self.seeking[kind] = nil }
                // Too late to send: a fresh sample puts the anchor back where the player is.
                guard self.accept(response, from: kind) != nil || response == .failed(AppleEventCode.notSent) else { return }
                self.requestPosition(kind)
            }
        }
    }

    private func send(_ command: TransportCommand) {
        // Behind an event that may be showing macOS's prompt, the command
        // would reach the player only once the user answered it.
        guard let kind = transportTarget, !connection.queueAwaitsConsent else { return }
        let pressedAt = uptime()
        activity[kind] = pressedAt
        let script = kind.backend.commandScript(command)
        let connection = connection, scheduler = scheduler
        scheduler.background { [weak self] in
            let response = connection.perform(script, on: kind, requestedAt: pressedAt)
            scheduler.main { [weak self] in
                guard let self, self.accept(response, from: kind) != nil else { return }
                // The player's notification normally arrives first; this
                // covers one that does not, and coalesces with it otherwise.
                self.requestRead(kind)
            }
        }
    }

    // MARK: - Automation permission

    /// Re-checks players macOS refused or has not been asked about, without
    /// prompting: called when Lunalyr becomes active and when the menu or
    /// Settings opens, since the user may just have changed Privacy & Security.
    func recheckAutomation() {
        for kind in followedPlayers where runningPlayers.contains(kind) && (isBlocked(kind) || !cleared.contains(kind)) {
            checkAutomation(kind, ask: false)
        }
    }

    /// Shows macOS's own permission prompt for a player it has not asked about.
    func requestAutomationAccess(for kind: PlayerKind) {
        guard runningPlayers.contains(kind) else { return }
        mayAsk.insert(kind)
        asked.insert(kind)
        checkAutomation(kind, ask: true)
    }

    /// For status displays such as the welcome window. Never prompts.
    func accessStatus(for kind: PlayerKind) async -> PlayerAccessStatus {
        guard environment.isInstalled(kind.bundleID) else { return .notInstalled }
        guard environment.isRunning(kind.bundleID) else { return .notRunning }
        let connection = connection, scheduler = scheduler
        return await withCheckedContinuation { continuation in
            scheduler.permission { continuation.resume(returning: connection.accessStatus(kind, ask: false)) }
        }
    }

    /// Readies a running player before Lunalyr sends it anything. One
    /// already cleared is simply read. Otherwise macOS is asked, silently,
    /// what the user decided: the first Apple event to an undecided player
    /// raises "Lunalyr wants to control …", which for a player the user is
    /// not using would come out of the blue. So an undecided player waits
    /// until the prompt is expected -- the first-run welcome explained it,
    /// the user chose that player or started playing something in it -- and
    /// meanwhile the menu offers Allow Access.
    private func prepare(_ kind: PlayerKind) {
        guard started, followedPlayers.contains(kind), runningPlayers.contains(kind) else { return }
        if cleared.contains(kind) {
            requestRead(kind)
        } else {
            checkAutomation(kind, ask: false)
        }
    }

    private func checkAutomation(_ kind: PlayerKind, ask: Bool) {
        // macOS's prompt may be up already, raised by an ask or by an event
        // still in flight: its answer settles the player. Never a second one.
        if ask, asking.contains(kind) || connection.isAwaitingConsent(kind) { return }
        // Separate keys: a queued silent re-check must not swallow a request to ask.
        let key = "access.\(ask ? "ask." : "")\(kind.rawValue)"
        guard gate.enter(key) else { return }
        if ask { asking.insert(kind) }
        let connection = connection, gate = gate, scheduler = scheduler
        scheduler.permission { [weak self] in
            gate.begin(key)
            let status = connection.accessStatus(kind, ask: ask)
            scheduler.main { [weak self] in self?.receiveAccess(status, for: kind, prompted: ask) }
        }
    }

    /// Asks macOS, silently, whether it now agrees with a refusal an Apple
    /// event ran into straight after a check had found the player allowed.
    /// It usually does: the user has just answered its prompt with Don't
    /// Allow, say. If the check still says allowed, it can't be relied on to
    /// say when the refusal ends, so the player's notifications stop asking.
    private func confirmRefusal(_ kind: PlayerKind) {
        let key = "access.confirm.\(kind.rawValue)"
        guard gate.enter(key) else { return }
        let connection = connection, gate = gate, scheduler = scheduler
        scheduler.permission { [weak self] in
            gate.begin(key)
            let status = connection.accessStatus(kind, ask: false)
            scheduler.main { [weak self] in
                guard let self, self.runningPlayers.contains(kind), self.automation[kind] == .denied,
                      status == .granted || status == .unknown else { return }
                self.contradicted.insert(kind)
            }
        }
    }

    private func receiveAccess(_ status: PlayerAccessStatus, for kind: PlayerKind, prompted: Bool) {
        if prompted { asking.remove(kind) }
        guard started, followedPlayers.contains(kind), runningPlayers.contains(kind) else { return }
        guard connection.admits(status, prompted: prompted, for: kind) else {
            // macOS's prompt went unanswered and was withdrawn with the event
            // that raised it. A silent check cannot say whether the next event
            // would raise it again, so only Allow Access, or the user playing
            // something in the player (`awaitAccess`), asks again.
            if !asking.contains(kind), automation[kind] != .needsConsent { automation[kind] = .needsConsent }
            return
        }
        switch status {
        case .granted:
            // macOS's word; the reply to the read below says whether its
            // Apple events agree.
            vouched.insert(kind)
            setAutomation(.granted, for: kind)
            clear(kind)
        case .denied:
            // The check agrees with the refusal, so it can tell when that ends.
            contradicted.remove(kind)
            setAutomation(.denied, for: kind)
            if snapshots[kind] != nil { forget(kind) } else { updatePolling() }
        case .notDetermined:
            if !prompted, asking.contains(kind) {
                // A silent check that landed while macOS's prompt is up.
                break
            }
            if !prompted, mayAsk.contains(kind), !asked.contains(kind) {
                // Expected now: let macOS ask.
                asked.insert(kind)
                checkAutomation(kind, ask: true)
            } else {
                setAutomation(.needsConsent, for: kind)
                if snapshots[kind] != nil { forget(kind) } else { updatePolling() }
            }
        case .unknown:
            // macOS would not say. Carry on as before the check existed: the
            // first reply reports a refusal if there is one.
            vouched.insert(kind)
            clear(kind)
        case .notRunning, .notInstalled:
            break
        }
    }

    /// Records what macOS allows for a running player. Without the idle
    /// notice going up when it comes to name a refused player, one already
    /// refused at launch would leave the desktop blank while its music
    /// plays. Finding the same refusal again, on a re-check, leaves the
    /// notice to fade as it was.
    @discardableResult
    private func setAutomation(_ access: AutomationAccess, for kind: PlayerKind) -> Bool {
        // A late reply from a player that has quit: its next run is checked afresh.
        guard runningPlayers.contains(kind), automation[kind] != access else { return false }
        let named = idleRefusal
        automation[kind] = access
        refreshIdleNotice(naming: named)
        return true
    }

    /// Puts the wallpaper's idle notice up again while nothing is current:
    /// when it now names a refused player it did not (`previous`), or when
    /// `started`, the player it names, has just started playing -- that is
    /// when someone looks for its lyrics, though the notice faded long ago.
    private func refreshIdleNotice(naming previous: PlayerKind?, started: PlayerKind? = nil) {
        guard current == nil, let named = idleRefusal, named != previous || named == started else { return }
        idleSince = Date()
    }

    /// What a player's own notification said. Those reach Lunalyr however
    /// Automation is set, so for a refused player they are all there is.
    private func noteNotified(_ kind: PlayerKind, playing: Bool) {
        let named = idleRefusal
        let started = playing && saysPlaying[kind] != true
        if saysPlaying[kind] != playing { saysPlaying[kind] = playing }
        refreshIdleNotice(naming: named, started: started ? kind : nil)
    }

    /// Forgets what macOS allowed a player, and what it said: that was a run
    /// of it that has quit, or a player no longer followed. The next is
    /// checked afresh -- the user may have changed Privacy & Security in
    /// between -- and a refusal found then puts the idle notice up again.
    private func forgetAccess(_ kind: PlayerKind) {
        if automation[kind] != nil { automation[kind] = nil }
        if saysPlaying[kind] != nil { saysPlaying[kind] = nil }
        vouched.remove(kind)
        contradicted.remove(kind)
    }

    /// Lets Lunalyr send a player Apple events from now on, and reads it.
    private func clear(_ kind: PlayerKind) {
        cleared.insert(kind)
        updatePolling()
        requestRead(kind)
    }

    // MARK: - System events

    func playerNotification(named name: String, userInfo: [AnyHashable: Any]?) {
        guard started, let kind = PlayerKind.allCases.first(where: { $0.notificationName == name }),
              followedPlayers.contains(kind) else { return }
        if !runningPlayers.contains(kind), environment.isRunning(kind.bundleID) {
            runningPlayers.insert(kind)
            updatePolling()
        }
        // A late notification from a player that has quit describes a
        // player that is gone, and one that is never polled again.
        guard runningPlayers.contains(kind) else { return }
        let backend = kind.backend
        let playing = backend.notificationSaysPlaying(userInfo)
        noteNotified(kind, playing: playing)
        let startedPlaying = playing && !notifiedPlaying.contains(kind)
        if playing { notifiedPlaying.insert(kind) } else { notifiedPlaying.remove(kind) }
        // Not cleared yet, or refused or blocked since: its contents wait for
        // the read that follows access.
        guard cleared.contains(kind), !isBlocked(kind) else {
            awaitAccess(kind, playing: playing, startedPlaying: startedPlaying)
            return
        }
        // It is up, so a re-sync held back after a timeout may go now.
        connection.resume(kind)
        epochs.advance(kind)
        guard var snapshot = backend.snapshot(fromNotification: userInfo) else {
            requestRead(kind)
            return
        }
        if let known = snapshots[kind]?.track, let track = snapshot.track, track == known {
            // The same recording keeps the identifier already known: a pause
            // must never look like a new track, which restarts the clock.
            snapshot.track?.persistentID = known.persistentID ?? track.persistentID
        }
        // A position from before a pending seek would pull the highlight back.
        let position = seeking[kind] == nil ? snapshot.position : nil
        apply(snapshot, from: kind, fix: position.map { PositionFix(position: $0, time: uptime(), estimate: false) })
    }

    /// A notification from a player Lunalyr may not address, yet or any
    /// more. Its contents wait for the read that follows access; starting
    /// playback in it is what makes macOS's prompt expected.
    private func awaitAccess(_ kind: PlayerKind, playing: Bool, startedPlaying: Bool = false) {
        if playing { mayAsk.insert(kind) }
        switch automation[kind] {
        case .needsConsent?:
            // Once unasked; then again each time the user starts playing in
            // it, should the prompt have gone unanswered -- with another
            // player's song on screen the menu offers no Allow Access. The
            // player moving on to its next track asks nothing.
            guard mayAsk.contains(kind), !asked.contains(kind) || startedPlaying else { return }
            asked.insert(kind)
            checkAutomation(kind, ask: true)
        case .denied?:
            // Only System Settings can lift a refusal, and nothing says when
            // it has: the user may switch access on and go straight back to
            // the player without touching Lunalyr. So the player's own
            // notifications check again -- silently, which sends the player
            // nothing and cannot prompt, and a burst of them shares one
            // check through the gate. Not where the check has said yes and
            // the player refused all the same (`contradicted`): each
            // notification would cost a refused read.
            guard !contradicted.contains(kind) else { return }
            checkAutomation(kind, ask: false)
        case .granted?, nil:
            // Not checked since it (re)launched; this coalesces with a check
            // already queued, whose answer then asks if it has to.
            checkAutomation(kind, ask: false)
        }
    }

    private func applicationLaunched(_ bundleID: String) {
        guard let kind = PlayerKind.allCases.first(where: { $0.bundleID == bundleID }),
              followedPlayers.contains(kind) else { return }
        if !runningPlayers.contains(kind) { runningPlayers.insert(kind) }
        // Let it finish launching before the first question.
        scheduler.after(1.5) { [weak self] in self?.prepare(kind) }
        updatePolling()
    }

    /// Switches which players are followed. Dropped players are forgotten
    /// at once and never sent anything again.
    private func follow(_ players: [PlayerKind]) {
        let ordered = PlayerKind.allCases.filter(players.contains)
        guard ordered != followedPlayers else { return }
        let dropped = followedPlayers.filter { !ordered.contains($0) }
        let droppedState = dropped.contains { snapshots[$0] != nil }
        followedPlayers = ordered
        for kind in dropped {
            epochs.advance(kind)
            snapshots[kind] = nil
            activity[kind] = nil
            seeking[kind] = nil
            cleared.remove(kind)
            forgetAccess(kind)
            javaScriptOff.remove(kind)
        }
        // Choosing one player in particular is asking Lunalyr to read it.
        if ordered.count == 1 { mayAsk.formUnion(ordered) }
        let running = Set(ordered.filter { environment.isRunning($0.bundleID) })
        if runningPlayers != running { runningPlayers = running }
        if droppedState { publish(fix: nil) }
        for kind in ordered where running.contains(kind) { prepare(kind) }
        updatePolling()
    }

    private func applicationTerminated(_ bundleID: String) {
        guard let kind = PlayerKind.allCases.first(where: { $0.bundleID == bundleID }) else { return }
        if runningPlayers.contains(kind) { runningPlayers.remove(kind) }
        // Checked again when it next launches: the user may change
        // Privacy & Security in between.
        cleared.remove(kind)
        forgetAccess(kind)
        javaScriptOff.remove(kind)
        notifiedPlaying.remove(kind)
        connection.reset(kind)
        forget(kind)
    }

    // MARK: - Reading players

    private struct PositionFix {
        var position: TimeInterval
        var time: TimeInterval
        var estimate: Bool
    }

    private var pollablePlayers: [PlayerKind] { followedPlayers.filter(isPollable) }

    private func isPollable(_ kind: PlayerKind) -> Bool {
        started && followedPlayers.contains(kind) && runningPlayers.contains(kind)
            && cleared.contains(kind) && !isBlocked(kind)
    }

    private func isBlocked(_ kind: PlayerKind) -> Bool {
        switch automation[kind] {
        case .denied?, .needsConsent?: return true
        default: return false
        }
    }

    /// Followed, running, and neither refused nor waiting on macOS's question.
    private func mayRead(_ kind: PlayerKind) -> Bool {
        followedPlayers.contains(kind) && runningPlayers.contains(kind) && !isBlocked(kind)
    }

    private func requestRead(_ kind: PlayerKind) {
        let key = "read.\(kind.rawValue)"
        guard isPollable(kind), gate.enter(key) else { return }
        let backend = kind.backend, connection = connection, gate = gate, epochs = epochs, scheduler = scheduler
        scheduler.background { [weak self] in
            gate.begin(key)
            let epoch = epochs.value(kind)
            let reading = connection.refresh(backend)
            scheduler.main { [weak self] in self?.receive(reading, from: kind, epoch: epoch) }
        }
    }

    private func requestPosition(_ kind: PlayerKind) {
        let key = "position.\(kind.rawValue)"
        guard isPollable(kind), gate.enter(key) else { return }
        let backend = kind.backend, connection = connection, gate = gate, epochs = epochs, scheduler = scheduler
        scheduler.background { [weak self] in
            gate.begin(key)
            let epoch = epochs.value(kind)
            let sample = connection.samplePosition(backend)
            scheduler.main { [weak self] in self?.receive(sample, from: kind, epoch: epoch) }
        }
    }

    /// Handles the parts of a reply that are not data and returns the data.
    private func accept<Value>(_ response: PlayerResponse<Value>, from kind: PlayerKind) -> Value? {
        switch response {
        case .value(let value):
            // Answered: access works, whatever a check said before.
            vouched.remove(kind)
            contradicted.remove(kind)
            if javaScriptOff.contains(kind) { javaScriptOff.remove(kind) }
            if setAutomation(.granted, for: kind) { updatePolling() }
            return value
        case .javaScriptOff:
            // The browser answered, so Automation is allowed; its pages are
            // not. Idle until the user turns the setting on: it is still
            // asked now and then, and the first reading clears this.
            vouched.remove(kind)
            contradicted.remove(kind)
            if !javaScriptOff.contains(kind) { javaScriptOff.insert(kind) }
            if setAutomation(.granted, for: kind) { updatePolling() }
            if snapshots[kind]?.track != nil || snapshots[kind] == nil {
                epochs.advance(kind)
                snapshots[kind] = PlayerSnapshot(state: .stopped)
                activity[kind] = nil
                seeking[kind] = nil
                publish(fix: nil)
            }
            return nil
        case .notRunning:
            if !environment.isRunning(kind.bundleID), runningPlayers.contains(kind) { runningPlayers.remove(kind) }
            forget(kind)
            return nil
        case .blocked(let access):
            // Refused straight after a check found the player allowed: does
            // the check agree now?
            if vouched.remove(kind) != nil, access == .denied { confirmRefusal(kind) }
            // Mid-song, clearing the track below puts the notice up.
            setAutomation(access, for: kind)
            forget(kind)
            return nil
        case .failed(let code):
            // Busy or timed out: keep what is on screen and try again later.
            // A player that answered before also times out once macOS has
            // lost Lunalyr's consent, its prompt gone with the event: a
            // silent check finds out, and an undecided player then waits to
            // be asked instead of raising the prompt at every re-sync.
            if code == AppleEventCode.timedOut { checkAutomation(kind, ask: false) }
            return nil
        }
    }

    private func receive(_ reading: RefreshReading, from kind: PlayerKind, epoch: Int) {
        guard let snapshot = accept(reading.read, from: kind) else { return }
        let sample = reading.sample.flatMap { accept($0, from: kind) }
        // Something newer arrived while this was in flight: a notification,
        // a seek, or the sample finding the player gone or refused.
        guard epoch == epochs.value(kind), isPollable(kind) else { return }
        var fix: PositionFix?
        // Sampled before a pending seek reached the player: the track and
        // state still hold, the position does not.
        if seeking[kind] == nil, let sample, sample.state == snapshot.state, let position = sample.position {
            fix = PositionFix(position: position, time: sample.time, estimate: false)
        }
        apply(snapshot, from: kind, fix: fix)
    }

    private func receive(_ response: PlayerResponse<PositionSample>, from kind: PlayerKind, epoch: Int) {
        guard let sample = accept(response, from: kind), epoch == epochs.value(kind), isPollable(kind) else { return }
        // Sampled before a pending seek reached the player. The seek's own
        // reply asks again.
        guard seeking[kind] == nil else { return }
        guard var snapshot = snapshots[kind], let track = snapshot.track else {
            if sample.state != .stopped {
                // Playing, or paused, without our knowing what: read it properly.
                requestRead(kind)
            } else if snapshots[kind] == nil {
                // Known to be idle, so it drops to the slower poll.
                apply(PlayerSnapshot(state: .stopped), from: kind, fix: nil)
            }
            return
        }
        if sample.state == .stopped {
            apply(PlayerSnapshot(state: .stopped), from: kind, fix: nil)
            return
        }
        if let sampled = sample.trackID, let known = track.persistentID, sampled != known {
            // The track changed without a notification.
            requestRead(kind)
            return
        }
        if track.persistentID == nil, let sampled = sample.trackID {
            // Its source carried no identifier; adopt the player's.
            snapshot.track?.persistentID = sampled
        }
        snapshot.state = sample.state
        apply(snapshot, from: kind, fix: sample.position.map { PositionFix(position: $0, time: sample.time, estimate: false) })
    }

    private func apply(_ incoming: PlayerSnapshot, from kind: PlayerKind, fix: PositionFix?) {
        var snapshot = incoming
        let previous = snapshots[kind]
        if let old = previous?.track, var track = snapshot.track, track.isSameTrack(as: old) {
            // Keep what this source could not carry.
            if track.persistentID == nil { track.persistentID = old.persistentID }
            if track.genre == nil { track.genre = old.genre }
            snapshot.track = track
            if snapshot.artworkURL == nil { snapshot.artworkURL = previous?.artworkURL }
        }
        if snapshot.state == .playing, let track = snapshot.track,
           previous?.state != .playing || !(previous?.track.map { track.isSameTrack(as: $0) } ?? false) {
            activity[kind] = uptime()
        }
        snapshots[kind] = snapshot
        publish(fix: fix.map { (kind, $0) })
    }

    private func forget(_ kind: PlayerKind) {
        // Replies still in flight describe a player that is gone.
        epochs.advance(kind)
        snapshots[kind] = nil
        activity[kind] = nil
        seeking[kind] = nil
        publish(fix: nil)
        updatePolling()
    }

    /// The one place published playback state changes.
    private func publish(fix: (kind: PlayerKind, fix: PositionFix)?) {
        let previousPlayer = activePlayer
        let previousTrack = current
        let wasRunning = clock.isRunning
        guard let kind = Self.choosePlayer(from: snapshots, activity: activity, among: followedPlayers),
              let snapshot = snapshots[kind], let track = snapshot.track else {
            if activePlayer != nil { activePlayer = nil }
            clearPlayback()
            return
        }
        if activePlayer != kind { activePlayer = kind }
        let isNewTrack = previousPlayer != kind || !(previousTrack.map { track.isSameTrack(as: $0) } ?? false)
        if !Self.identical(current, track) { current = track }
        let running = snapshot.state == .playing
        let now = uptime()
        if let fix, fix.kind == kind {
            let shown = isNewTrack || !(wasRunning && running) ? nil : clock.position(at: now)
            let continuous = !isNewTrack && wasRunning && running && !anchorIsEstimate && !fix.fix.estimate
            clock.sync(to: fix.fix.position, sampledAt: fix.fix.time, running: running, now: now,
                       continuous: continuous)
            SyncLog.clockMoved(from: shown, to: fix.fix.position + max(0, now - fix.fix.time), player: kind.rawValue,
                               track: track.title, continuous: continuous, estimate: fix.fix.estimate)
            anchorIsEstimate = fix.fix.estimate
        } else if isNewTrack {
            // A new track starts at its beginning; the sample confirms where.
            clock.sync(to: 0, sampledAt: now, running: running, now: now, continuous: false)
            anchorIsEstimate = true
            requestPosition(kind)
        } else if running != wasRunning {
            // Pause or resume from what is on screen; the sample corrects it.
            clock.setRunning(running, now: now)
            requestPosition(kind)
        }
        if isPlaying != running { isPlaying = running }
        if running || isNewTrack { pausedSince = running ? nil : now } else if pausedSince == nil { pausedSince = now }
        if isNewTrack {
            loadArtwork(for: track, from: kind)
            updateMotionArtwork()
        }
    }

    private func clearPlayback() {
        if current != nil { current = nil } else if idleSince == nil { idleSince = Date() }
        if isPlaying { isPlaying = false }
        pausedSince = nil
        clock.reset()
        anchorIsEstimate = false
        artworkGeneration.advance()
        artworkTask?.cancel()
        artworkTask = nil
        artworkTarget = nil
        if artworkTrack != nil { artworkTrack = nil }
        if artwork != nil { artwork = nil }
        cancelMotionLookup()
        motionKey = nil
        if motionArtwork != nil { motionArtwork = nil }
    }

    /// The player shown: the one playing (the most recently started, if
    /// several are), else the most recently active one with a track loaded.
    nonisolated static func choosePlayer(from snapshots: [PlayerKind: PlayerSnapshot],
                                         activity: [PlayerKind: TimeInterval],
                                         among eligible: [PlayerKind]) -> PlayerKind? {
        let loaded = eligible.filter { snapshots[$0]?.track != nil }
        let playing = loaded.filter { snapshots[$0]?.state == .playing }
        func mostRecent(_ kinds: [PlayerKind]) -> PlayerKind? {
            kinds.max { (activity[$0] ?? -.infinity) < (activity[$1] ?? -.infinity) }
        }
        return mostRecent(playing) ?? mostRecent(loaded)
    }

    private static func identical(_ old: Track?, _ new: Track) -> Bool {
        guard let old else { return false }
        return old == new && old.duration == new.duration && old.genre == new.genre
            && old.persistentID == new.persistentID && old.isStream == new.isStream
    }

    // MARK: - Re-sync timer

    /// How much slower the re-sync runs while every display is asleep or
    /// locked: nothing shows the lyrics then, and the players still announce
    /// play, pause and track changes themselves.
    static let hiddenPollFactor: TimeInterval = 5

    /// The re-sync timer's interval, nil while it is not running.
    var resyncInterval: TimeInterval? { timer?.timeInterval }

    private func setScreensHidden(_ hidden: Bool) {
        guard hidden != screensHidden else { return }
        screensHidden = hidden
        // Restart the timer at the new pace.
        if isPolling {
            isPolling = false
            updatePolling()
        }
        // Back on screen: catch up now rather than at the next tick.
        if !hidden { refresh() }
    }

    private func updatePolling() {
        let shouldPoll = started && !pollablePlayers.isEmpty
        guard shouldPoll != isPolling else { return }
        isPolling = shouldPoll
        timer?.invalidate()
        timer = nil
        guard shouldPoll, let pollInterval else { return }
        let interval = screensHidden ? pollInterval * Self.hiddenPollFactor : pollInterval
        let timer = Timer(timeInterval: interval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        timer.tolerance = interval * 0.1
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    /// The re-sync: the player on screen every tick -- paused too, in case
    /// it was scrubbed -- and so is one not yet read successfully (say, its
    /// first read timed out while it was busy); the others now and then, in
    /// case a notification went missing.
    func tick() {
        ticks &+= 1
        let resting = pausedSince.map { uptime() - $0 >= Self.restingAfter } ?? false
        // Players that announce nothing are asked more often while idle:
        // asking is the only way to notice them start.
        for kind in pollablePlayers
        where (kind == activePlayer && !(resting && !kind.pollsForChanges)) || snapshots[kind] == nil
            || ticks % (kind.pollsForChanges ? Self.quietPollEvery : Self.idlePollEvery) == 0 {
            requestPosition(kind)
        }
    }

    // MARK: - Artwork

    /// Every cover request belongs to one generation, and only a reply from
    /// the latest is shown. A track change while a cover is being read
    /// would otherwise let the previous song's cover (or its lack of one,
    /// and the catalogue search that starts) land on the new song -- and
    /// the new song's own reply would then find nothing waiting for it.
    private func loadArtwork(for track: Track, from kind: PlayerKind) {
        artworkTask?.cancel()
        artworkTask = nil
        artworkTarget = track
        let generation = artworkGeneration.advance()
        if let url = snapshots[kind]?.artworkURL {
            downloadArtwork(url, for: track, generation: generation)
        } else {
            requestArtworkRead(kind, generation: generation)
        }
    }

    private func requestArtworkRead(_ kind: PlayerKind, generation: Int) {
        guard isPollable(kind) else { return }
        let backend = kind.backend, connection = connection, scheduler = scheduler
        let artworkGeneration = artworkGeneration
        scheduler.background { [weak self] in
            // Superseded while it waited: a newer track's read is queued
            // behind it, so a burst of skips reads one cover.
            guard artworkGeneration.value == generation else { return }
            let reply = connection.artwork(backend)
            // Decoded here, off the main thread.
            let decoded: CGImage?
            if case .value(let read) = reply, case .image(let data) = read.reading {
                decoded = ArtworkImage.decode(data)
            } else {
                decoded = nil
            }
            scheduler.main { [weak self] in
                self?.receiveArtwork(reply, decoded: decoded, from: kind, generation: generation)
            }
        }
    }

    private func isCurrentArtwork(_ generation: Int) -> Bool {
        generation == artworkGeneration.value
    }

    private func receiveArtwork(_ response: PlayerResponse<ArtworkReply>, decoded: CGImage?, from kind: PlayerKind,
                                generation: Int) {
        let reply = accept(response, from: kind)
        guard isCurrentArtwork(generation), activePlayer == kind, let target = artworkTarget, let current,
              target.isSameTrack(as: current) else { return }
        // Read after the player had moved on without saying so yet: that
        // track's own load follows its notification or the next re-sync.
        if let read = reply?.trackID, let known = current.persistentID, read != known { return }
        switch reply?.reading {
        case .image?:
            if let decoded {
                setArtwork(ArtworkImage.image(decoded), for: current)
            } else {
                searchCatalogue(for: current, from: kind, generation: generation)
            }
        case .url(let url)?:
            downloadArtwork(url, for: current, generation: generation)
        case .none?, nil:
            searchCatalogue(for: current, from: kind, generation: generation)
        }
    }

    private func setArtwork(_ image: NSImage?, for track: Track) {
        // A download or search still running must not replace this later.
        artworkTask?.cancel()
        artworkTask = nil
        artworkTarget = nil
        artworkTrack = track
        artwork = image
    }

    private func downloadArtwork(_ url: URL, for track: Track, generation: Int) {
        artworkTask?.cancel()
        let download = services.downloadArtwork
        artworkTask = Task { [weak self] in
            let data = await download(url)
            let decoded = await ArtworkImage.decodeDetached(data)
            guard let self, !Task.isCancelled, self.isCurrentArtwork(generation),
                  let current = self.current, track.isSameTrack(as: current) else { return }
            self.setArtwork(decoded.map(ArtworkImage.image), for: current)
        }
    }

    /// A catalogue search is only a fallback, and a fuzzy one: "Songs from
    /// the Big Chair" and its Super Deluxe reissue are different covers for
    /// the same song title. Streams are never searched, and neither are
    /// players other than Apple Music (`usesAppleCatalogue`).
    private func searchCatalogue(for track: Track, from kind: PlayerKind, generation: Int) {
        guard kind.backend.usesAppleCatalogue, !track.isStream else {
            setArtwork(nil, for: track)
            return
        }
        artworkTask?.cancel()
        let search = services.catalogueArtwork
        artworkTask = Task { [weak self] in
            let data = await search(track)
            let decoded = await ArtworkImage.decodeDetached(data)
            guard let self, !Task.isCancelled, self.isCurrentArtwork(generation),
                  let current = self.current, track.isSameTrack(as: current) else { return }
            // NSImage remains on the main actor; only immutable image bytes
            // and the decoded CGImage cross the provider's boundary.
            self.setArtwork(decoded.map(ArtworkImage.image), for: current)
        }
    }

    // MARK: - Animated covers

    /// Looks a cover up only where one could be shown, and keeps the
    /// current one until a different one arrives: the next song from the
    /// same album must not cut away from it and back. Animated covers are
    /// Apple Music's, looked up only for songs playing in Music.
    private func updateMotionArtwork() {
        guard let track = current, !track.isStream, let kind = activePlayer, kind.backend.usesAppleCatalogue,
              services.allowsMotionLookup(track) else {
            cancelMotionLookup()
            motionKey = nil
            if motionArtwork != nil { motionArtwork = nil }
            return
        }
        let key = MotionArtworkProvider.cacheKey(album: track.album, artist: track.artist, title: track.title)
        if key == motionKey {
            cancelMotionLookup()
            return
        }
        guard key != motionLookupKey else { return }
        cancelMotionLookup()
        motionLookupKey = key
        isLookingUpMotion = true
        let lookup = services.motionArtwork
        motionLookup = Task { [weak self] in
            let motion = await lookup(track)
            guard let self, !Task.isCancelled, self.motionLookupKey == key else { return }
            self.motionLookupKey = nil
            self.motionLookup = nil
            self.isLookingUpMotion = false
            self.motionKey = key
            if self.motionArtwork != motion { self.motionArtwork = motion }
        }
    }

    private func cancelMotionLookup() {
        motionLookup?.cancel()
        motionLookup = nil
        motionLookupKey = nil
        if isLookingUpMotion { isLookingUpMotion = false }
    }
}
