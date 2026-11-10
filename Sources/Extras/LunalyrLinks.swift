import AppKit

/// Something Lunalyr can be asked to do from outside: a `lunalyr://` link,
/// a Shortcuts action or a global shortcut all end up here.
enum LunalyrCommand: String, CaseIterable, Sendable {
    case toggleLyrics, syncEarlier, syncLater, cycleLayout, lyricCard, pauseWallpaper, openSettings

    /// Runs the command exactly as its menu item or global shortcut does.
    @MainActor
    func run() {
        let extras = LunalyrExtras.shared
        switch self {
        case .toggleLyrics: extras.toggleLyrics()
        case .syncEarlier: extras.nudgeSync(by: 0.1)
        case .syncLater: extras.nudgeSync(by: -0.1)
        case .cycleLayout: extras.cycleLayout()
        case .lyricCard: extras.copyLyricCard()
        case .pauseWallpaper: extras.toggleWallpaperPause()
        case .openSettings: LunalyrNavigation.shared.openSettings()
        }
    }
}

/// A `lunalyr://` link, parsed. Pure: no preferences, no network.
///
/// - `lunalyr://join/<room>`: listen along with a room (asked about first)
/// - `lunalyr://toggle`: show or hide the lyrics
/// - `lunalyr://card`: a lyric card of the current line
/// - `lunalyr://settings`: open Settings
///
/// A link can come from any web page or message, so anything else -- another
/// scheme, extra path parts, a query, a user or port, a room that is not
/// exactly ten lower-case letters or digits -- is ignored.
enum LunalyrLink: Equatable, Sendable {
    case join(room: String)
    case command(LunalyrCommand)

    static let scheme = "lunalyr"

    init?(_ url: URL) {
        guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
              components.scheme?.lowercased() == Self.scheme,
              components.user == nil, components.password == nil, components.port == nil,
              components.query == nil, components.fragment == nil,
              let host = components.host?.lowercased(), !host.isEmpty else { return nil }
        // Raw path, so a percent-encoded room never sneaks past the check.
        var parts = components.percentEncodedPath.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        // "/abc" splits to ["", "abc"]; a single trailing slash is allowed.
        if parts.first == "" { parts.removeFirst() }
        if parts.last == "" { parts.removeLast() }
        switch host {
        case "join":
            guard parts.count == 1, ListenProtocol.isValidRoom(parts[0]) else { return nil }
            self = .join(room: parts[0])
        case "toggle", "card", "settings":
            guard parts.isEmpty else { return nil }
            self = .command(host == "toggle" ? .toggleLyrics : host == "card" ? .lyricCard : .openSettings)
        default:
            return nil
        }
    }

    /// The link for a room, as the web listen-along page offers it.
    static func joinURL(room: String) -> URL? {
        ListenProtocol.isValidRoom(room) ? URL(string: "\(scheme)://join/\(room)") : nil
    }
}

/// Acts on opened `lunalyr://` links. What each link does is injected, so
/// tests route links without touching the app.
@MainActor
final class LunalyrLinkRouter {
    static let shared = LunalyrLinkRouter()

    struct Actions {
        /// Asks before following; false only when no relay is set.
        var join: @MainActor (_ room: String) -> Bool
        var run: @MainActor (LunalyrCommand) -> Void
        var note: @MainActor (_ text: String, _ symbol: String) -> Void
    }

    private let actions: Actions
    /// Links that arrive while launching wait until the app has started.
    private var ready = false
    private var waiting: [LunalyrLink] = []

    init(actions: Actions = .live, ready: Bool = false) {
        self.actions = actions
        self.ready = ready
    }

    /// Handles every link it understands and ignores the rest.
    func open(_ urls: [URL]) {
        for url in urls {
            guard let link = LunalyrLink(url) else { continue }
            if ready { route(link) } else { waiting.append(link) }
        }
    }

    /// The app has started: run what arrived with the launch.
    func becameReady() {
        guard !ready else { return }
        ready = true
        let pending = waiting
        waiting = []
        pending.forEach(route)
    }

    private func route(_ link: LunalyrLink) {
        switch link {
        case .join(let room):
            if !actions.join(room) {
                actions.note("To listen along, add a listen-along relay in Settings \u{203A} Share", "person.2")
            }
        case .command(let command):
            actions.run(command)
        }
    }
}

extension LunalyrLinkRouter.Actions {
    static var live: Self {
        Self(join: { room in
                 let listen = ListenAlong.shared
                 guard listen.isAvailable else { return false }
                 // Asks "Listen along with room …?" first; one's own room is ignored.
                 listen.requestJoin(code: room)
                 return true
             },
             run: { $0.run() },
             note: { LunalyrExtras.shared.post($0, symbol: $1) })
    }
}
