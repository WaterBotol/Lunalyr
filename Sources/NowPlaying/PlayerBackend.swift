import AppKit

/// A media player Lunalyr can follow. Each is driven only through its own
/// public AppleScript dictionary and the notifications it broadcasts.
///
/// AppleScript launches any application it addresses. Every script here
/// therefore checks that its player is already running before it sends
/// anything -- the 0.4.x poller did not, and reopened Music every two
/// seconds after the user quit it.
enum PlayerKind: String, CaseIterable, Identifiable, Sendable {
    case appleMusic
    case spotify
    /// Local-file players with an AppleScript dictionary.
    case vlc
    case swinsian
    case vox
    /// Browsers, for YouTube Music, SoundCloud and Amazon Music tabs: read
    /// by running a small script in the page (`WebPlayerBackend`), which
    /// each browser allows only with "Allow JavaScript from Apple Events".
    case safari
    case chrome
    case arc
    case brave
    case edge

    var id: String { rawValue }

    /// The kinds of source, which decide where a player is offered.
    enum Family: Sendable {
        /// A music app with its own library or service (Music, Spotify).
        case musicApp
        /// A player of files on this Mac.
        case localFiles
        /// A browser playing a web player.
        case browser
    }

    var family: Family {
        switch self {
        case .appleMusic, .spotify: return .musicApp
        case .vlc, .swinsian, .vox: return .localFiles
        case .safari, .chrome, .arc, .brave, .edge: return .browser
        }
    }

    var isBrowser: Bool { family == .browser }

    var bundleID: String {
        switch self {
        case .appleMusic: return "com.apple.Music"
        case .spotify: return "com.spotify.client"
        case .vlc: return "org.videolan.vlc"
        case .swinsian: return "com.swinsian.Swinsian"
        case .vox: return "com.coppertino.Vox"
        case .safari: return "com.apple.Safari"
        case .chrome: return "com.google.Chrome"
        case .arc: return "company.thebrowser.Browser"
        case .brave: return "com.brave.Browser"
        case .edge: return "com.microsoft.edgemac"
        }
    }

    /// The app's own name, as Finder and System Settings show it.
    var displayName: String {
        switch self {
        case .appleMusic: return "Music"
        case .spotify: return "Spotify"
        case .vlc: return "VLC"
        case .swinsian: return "Swinsian"
        case .vox: return "VOX"
        case .safari: return "Safari"
        case .chrome: return "Google Chrome"
        case .arc: return "Arc"
        case .brave: return "Brave Browser"
        case .edge: return "Microsoft Edge"
        }
    }

    /// Distributed notification the player posts on every track or state
    /// change. The others say nothing, so they are polled (`pollsForChanges`).
    var notificationName: String? {
        switch self {
        case .appleMusic: return "com.apple.Music.playerInfo"
        case .spotify: return "com.spotify.client.PlaybackStateChanged"
        default: return nil
        }
    }

    /// A player that announces nothing: a track starting or changing is
    /// noticed only by asking, so it is asked more often while idle.
    var pollsForChanges: Bool { notificationName == nil }

    /// The app whose processes play its audio, for telling whether it plays
    /// on this Mac. Browsers play from shared engine processes (WebKit's GPU
    /// process for Safari) that cannot be told apart reliably, so nil.
    var outputBundleID: String? { isBrowser ? nil : bundleID }

    var backend: any PlayerBackend {
        switch self {
        case .appleMusic: return MusicBackend()
        case .spotify: return SpotifyBackend()
        case .vlc: return VLCBackend()
        case .swinsian: return SwinsianBackend()
        case .vox: return VoxBackend()
        case .safari, .chrome, .arc, .brave, .edge: return WebPlayerBackend(browser: self)
        }
    }

    /// Players followed under Automatic. Browsers only when web players are
    /// turned on (`PlayerOptions.followsWebPlayers`): every Mac has Safari
    /// open, and following it unasked would raise "Lunalyr wants to control
    /// Safari" for someone who never plays music in it.
    static var apps: [PlayerKind] { allCases.filter { !$0.isBrowser } }

    static var browsers: [PlayerKind] { allCases.filter(\.isBrowser) }

    /// "Music", "Music or Spotify"... Browsers are named once, as the web
    /// players they stand for.
    static func names(_ kinds: [PlayerKind]) -> String {
        var names = kinds.filter { !$0.isBrowser }.map(\.displayName)
        if kinds.contains(where: \.isBrowser) { names.append("a web player") }
        return names.joined(separator: " or ")
    }
}

/// What macOS Automation (Privacy & Security › Automation) allows for one
/// player, as the last Apple event or permission check reported it.
enum AutomationAccess: Equatable, Sendable {
    case granted
    /// The user turned access off; every Apple event fails with -1743.
    case denied
    /// macOS has not asked the user yet (-1744).
    case needsConsent
}

/// A permission check that never prompts, for status displays.
enum PlayerAccessStatus: Equatable, Sendable {
    case notInstalled, notRunning, granted, denied, notDetermined, unknown

    init(status: OSStatus) {
        switch Int(status) {
        case 0: self = .granted
        case AppleEventCode.notPermitted: self = .denied
        case AppleEventCode.wouldRequireConsent: self = .notDetermined
        case AppleEventCode.processNotFound: self = .notRunning
        default: self = .unknown
        }
    }
}

enum AppleEventCode {
    /// errAEEventNotPermitted: Automation access is turned off.
    static let notPermitted = -1743
    /// errAEEventWouldRequireUserConsent: macOS has not asked yet.
    static let wouldRequireConsent = -1744
    /// procNotFound: the target is not running.
    static let processNotFound = -600
    /// connectionInvalid: the target quit while the event was in flight.
    static let connectionInvalid = -609
    /// errAETimeout.
    static let timedOut = -1712
    /// errAENoSuchObject, e.g. "Can't get current track".
    static let noSuchObject = -1728
    /// Lunalyr's own, never an Apple event's: the script was not sent at
    /// all -- a re-sync held back while a player that timed out is polled
    /// less often, or a command that waited too long (`PlayerConnection`).
    /// Positive, so no macOS error can be mistaken for it.
    static let notSent = 1
}

/// One consistent reading of a player.
struct PlayerSnapshot: Equatable, Sendable {
    enum State: String, Equatable, Sendable {
        case playing, paused, stopped
    }

    var state: State
    /// Absent when stopped or when nothing is loaded.
    var track: NowPlayingMonitor.Track?
    /// Seconds into the track, when the source carried it (Spotify's
    /// notifications do; Music's do not).
    var position: TimeInterval?
    /// Cover image address, for players that publish one.
    var artworkURL: URL?

    init(state: State, track: NowPlayingMonitor.Track? = nil, position: TimeInterval? = nil, artworkURL: URL? = nil) {
        self.state = state
        self.track = state == .stopped ? nil : track
        self.position = position
        self.artworkURL = artworkURL
    }
}

/// A playback position together with the moment it most likely refers to.
struct PositionSample: Equatable, Sendable {
    var state: PlayerSnapshot.State
    var trackID: String?
    var position: TimeInterval?
    /// Monotonic seconds (the monitor's clock) at which the player sampled it.
    var time: TimeInterval
}

/// What a player reported for its current track's cover.
enum ArtworkReading: Equatable, Sendable {
    case image(Data)
    case url(URL)
    case none
}

/// The outcome of one exchange with a player.
enum PlayerResponse<Value: Sendable>: Sendable {
    case value(Value)
    /// The player is not running (or quit during the exchange).
    case notRunning
    /// macOS Automation access is off or undecided for this player.
    case blocked(AutomationAccess)
    /// A browser answered, but its "Allow JavaScript from Apple Events" is
    /// off, so no web player in it can be read. Only the user can turn it
    /// on; Lunalyr says how (`WebScriptingNotice`) and never touches it.
    case javaScriptOff
    /// Anything else: timeouts, a busy player, an unexpected reply.
    case failed(Int)

    init(failureCode code: Int) {
        switch code {
        case AppleEventCode.notPermitted: self = .blocked(.denied)
        case AppleEventCode.wouldRequireConsent: self = .blocked(.needsConsent)
        case AppleEventCode.processNotFound, AppleEventCode.connectionInvalid: self = .notRunning
        default: self = .failed(code)
        }
    }

    var value: Value? {
        if case .value(let value) = self { return value }
        return nil
    }
}

extension PlayerResponse: Equatable where Value: Equatable {}

enum TransportCommand: Sendable {
    case playPause, next, previous
}

/// A script and the name tests and the compile cache know it by.
struct PlayerScript: Hashable, Sendable {
    let name: String
    let source: String
    /// Compiled once and reused. Sources that embed a value (a seek
    /// target) are not cached, or the cache would grow without bound.
    var cacheable = true
    /// Whether this copy waits out macOS's consent prompt (`waitingForConsent`).
    private(set) var waitsForConsent = false

    init(name: String, source: String, cacheable: Bool = true) {
        self.name = name
        self.source = source
        self.cacheable = cacheable
    }

    /// The same script, waiting `PlayerScriptSource.consentTimeout` for each
    /// reply instead of a few seconds. For the first exchange with a player
    /// whose consent no reply has proven yet: if macOS asks, the event waits
    /// on its prompt, and the prompt stays up only as long as the event does.
    var waitingForConsent: PlayerScript {
        var script = PlayerScript(name: name, source: PlayerScriptSource.withTimeout(PlayerScriptSource.consentTimeout, source),
                                  cacheable: cacheable)
        script.waitsForConsent = true
        return script
    }
}

/// One player's scripts and the parsing of its replies. Backends hold no
/// state; everything that talks to a player goes through `PlayerConnection`.
protocol PlayerBackend: Sendable {
    var kind: PlayerKind { get }
    /// State and complete track metadata, read from one track reference so
    /// the fields cannot straddle a track change.
    var readScript: PlayerScript { get }
    /// State, track identifier and position only: the cheap re-sync.
    var positionScript: PlayerScript { get }
    /// Tried in order until one yields a decodable image or an address.
    var artworkScripts: [PlayerScript] { get }
    /// Whether this player's tracks may be looked up in Apple's public
    /// catalogue: the fallback cover search (itunes.apple.com) and animated
    /// covers (music.apple.com). Apple Music only -- what someone plays in
    /// another service is not sent to Apple.
    var usesAppleCatalogue: Bool { get }
    func commandScript(_ command: TransportCommand) -> PlayerScript
    func seekScript(to seconds: TimeInterval) -> PlayerScript
    func parseRead(_ reply: NSAppleEventDescriptor) -> PlayerResponse<PlayerSnapshot>
    func parsePosition(_ reply: NSAppleEventDescriptor, at time: TimeInterval) -> PlayerResponse<PositionSample>
    func parseArtwork(_ reply: NSAppleEventDescriptor) -> PlayerResponse<ArtworkReply>
    /// A snapshot built from the player's own notification, or nil when it
    /// does not carry enough to trust without asking the player.
    func snapshot(fromNotification userInfo: [AnyHashable: Any]?) -> PlayerSnapshot?
}

/// A cover reply, tagged with the track it belongs to.
struct ArtworkReply: Equatable, Sendable {
    var trackID: String?
    var reading: ArtworkReading
}

/// AppleScript text shared by the backends.
enum PlayerScriptSource {
    /// Errors a missing or unsupported property raises. Anything else -- the
    /// player quitting (-609), access being turned off (-1743), a timeout --
    /// ends the script, so it can never carry on against a closed app and
    /// reopen it.
    static let tolerated = "{-1728, -1700, -1708, -1719, -10000}"

    /// Seconds a script waits for each reply from a player whose consent a
    /// reply has already proven, so a hung player cannot hold the script
    /// queue for AppleScript's default two minutes.
    static let replyTimeout = 6

    /// Seconds the first exchange with a player waits while no reply has
    /// proven its consent: AppleScript's own default. If macOS has to ask,
    /// the Apple event waits on its "Lunalyr wants to control …" prompt, and
    /// the prompt lasts only as long as the event: with `replyTimeout` it
    /// was withdrawn after six seconds and every poll raised it again.
    static let consentTimeout = 120

    /// A read that returns `{"notRunning"}` instead of launching the player.
    static func query(_ bundleID: String, timeout: Int = replyTimeout, _ body: String) -> String {
        """
        if application id "\(bundleID)" is not running then return {"notRunning"}
        tell application id "\(bundleID)"
        with timeout of \(timeout) seconds
        \(body)
        end timeout
        end tell
        """
    }

    static func command(_ bundleID: String, _ body: String) -> String {
        """
        if application id "\(bundleID)" is not running then return "notRunning"
        tell application id "\(bundleID)"
        with timeout of \(replyTimeout) seconds
        \(body)
        end timeout
        end tell
        return "ok"
        """
    }

    /// `source` with every `with timeout` block waiting `seconds`.
    static func withTimeout(_ seconds: Int, _ source: String) -> String {
        source.replacingOccurrences(of: #"with timeout of [0-9]+ seconds"#, with: "with timeout of \(seconds) seconds",
                                    options: .regularExpression)
    }

    /// Reads an expression that may legitimately be unavailable, leaving
    /// `missing value` rather than failing the whole script.
    static func optional(_ variable: String, _ expression: String, tolerating codes: String = tolerated) -> String {
        """
        set \(variable) to missing value
        try
        set \(variable) to \(expression)
        on error number errorNumber
        if errorNumber is not in \(codes) then error number errorNumber
        end try
        """
    }

    /// `tolerated`, and an undefined variable (-2753): what a term a player's
    /// version lacks compiles to. For players whose dictionaries Lunalyr
    /// could not check against every version.
    static let toleratedUnknownTerms = "{-1728, -1700, -1708, -1719, -10000, -2753}"

    /// The player state as plain text. Comparing enumerators inside the
    /// tell block does not depend on how the dictionary coerces them.
    static let state = """
    set playerState to player state
    if playerState is stopped then return {"stopped"}
    set stateName to "playing"
    if playerState is paused then set stateName to "paused"
    """

    /// Locale-independent decimal for a script literal.
    static func number(_ value: TimeInterval) -> String {
        String(format: "%.3f", locale: Locale(identifier: "en_US_POSIX"), value.isFinite ? max(0, value) : 0)
    }
}

/// Four-character codes used when reading Apple event replies.
enum DescriptorCode {
    static let list: DescType = 0x6C69_7374          // 'list'
    static let type: DescType = 0x7479_7065          // 'type'
    static let double: DescType = 0x646F_7562        // 'doub'
    static let missingValue: OSType = 0x6D73_6E67    // 'msng'
    static let coreSuite: AEEventClass = 0x636F_7265 // 'core'
    static let getData: AEEventID = 0x6765_7464      // 'getd'
}

extension NSAppleEventDescriptor {
    /// A list reply's items; any other reply as a single item.
    var scriptItems: [NSAppleEventDescriptor] {
        guard descriptorType == DescriptorCode.list else { return [self] }
        guard numberOfItems > 0 else { return [] }
        return (1...numberOfItems).compactMap { atIndex($0) }
    }

    var isMissingValue: Bool {
        descriptorType == DescriptorCode.type && typeCodeValue == DescriptorCode.missingValue
    }

    var scriptText: String? {
        isMissingValue ? nil : stringValue
    }

    var scriptNumber: Double? {
        guard !isMissingValue else { return nil }
        if let number = coerce(toDescriptorType: DescriptorCode.double) { return number.doubleValue }
        return stringValue.flatMap { Double($0.trimmingCharacters(in: .whitespaces)) }
    }

    /// Builds a list reply, for tests and fakes.
    static func scriptList(_ items: [NSAppleEventDescriptor]) -> NSAppleEventDescriptor {
        let list = NSAppleEventDescriptor.list()
        for item in items { list.insert(item, at: list.numberOfItems + 1) }
        return list
    }

    static var missingValue: NSAppleEventDescriptor {
        NSAppleEventDescriptor(typeCode: DescriptorCode.missingValue)
    }
}

extension Int {
    /// A number from a script reply as a whole number, cut toward zero; nil
    /// for NaN, infinity or anything out of range, where `Int(_:)` traps.
    init?(scriptValue value: Double?) {
        guard let value, value.isFinite, let whole = Int(exactly: value.rounded(.towardZero)) else { return nil }
        self = whole
    }
}

extension PlayerBackend {
    /// Whether the player's own notification says it started or carries on
    /// playing. Both players post "Player State" = "Playing".
    func notificationSaysPlaying(_ userInfo: [AnyHashable: Any]?) -> Bool {
        (userInfo?["Player State"] as? String)?.lowercased() == "playing"
    }

    /// The leading status word every query script returns first.
    func status(of reply: NSAppleEventDescriptor) -> (items: [NSAppleEventDescriptor], word: String?) {
        let items = reply.scriptItems
        return (items, items.first?.scriptText)
    }

    func parsePositionReply(_ reply: NSAppleEventDescriptor, at time: TimeInterval) -> PlayerResponse<PositionSample> {
        let (items, word) = status(of: reply)
        switch word {
        case "notRunning": return .notRunning
        case "stopped": return .value(PositionSample(state: .stopped, trackID: nil, position: nil, time: time))
        default:
            guard let word, let state = PlayerSnapshot.State(rawValue: word), items.count >= 3 else { return .failed(-1) }
            let id = items[1].scriptText.flatMap { $0.isEmpty ? nil : $0 }
            let position = items[2].scriptNumber.flatMap { $0.isFinite && $0 >= 0 ? $0 : nil }
            return .value(PositionSample(state: state, trackID: id, position: position, time: time))
        }
    }
}
