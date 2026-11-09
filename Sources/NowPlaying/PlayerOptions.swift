import AppKit
import Combine
import SwiftUI

/// Which players Lunalyr follows. Automatic follows whichever is playing;
/// choosing one means the others are never sent anything at all.
@MainActor
final class PlayerOptions: ObservableObject {
    static let shared = PlayerOptions()
    static let key = "custom.player"
    static let webKey = "custom.player.webPlayers"

    enum Choice: String, CaseIterable, Identifiable {
        case automatic, appleMusic, spotify, vlc, swinsian, vox, safari, chrome, arc, brave, edge

        var id: String { rawValue }

        /// The one player this choice follows; nil for Automatic.
        var kind: PlayerKind? {
            switch self {
            case .automatic: return nil
            case .appleMusic: return .appleMusic
            case .spotify: return .spotify
            case .vlc: return .vlc
            case .swinsian: return .swinsian
            case .vox: return .vox
            case .safari: return .safari
            case .chrome: return .chrome
            case .arc: return .arc
            case .brave: return .brave
            case .edge: return .edge
            }
        }

        var title: String {
            guard let kind else { return "Automatic" }
            switch kind {
            case .appleMusic: return "Apple Music"
            case .chrome: return "Web in Chrome"
            case .brave: return "Web in Brave"
            case .edge: return "Web in Edge"
            case .safari, .arc: return "Web in \(kind.displayName)"
            default: return kind.displayName
            }
        }

        /// The players this choice follows, web players aside: Automatic's
        /// browsers depend on `followsWebPlayers` (`PlayerOptions.players`).
        var players: [PlayerKind] {
            kind.map { [$0] } ?? PlayerKind.apps
        }

        /// The choice that follows `kind` and nothing else.
        static func only(_ kind: PlayerKind) -> Choice {
            allCases.first { $0.kind == kind } ?? .automatic
        }

        /// What a picker offers: Automatic, each installed player, and the
        /// current choice even if its app has gone. Music and Spotify always,
        /// as before; browsers only while web players are on or chosen.
        static func offered(installed: Set<PlayerKind>, current: Choice, webPlayers: Bool) -> [Choice] {
            allCases.filter { choice in
                guard choice != current, let kind = choice.kind else { return true }
                switch kind.family {
                case .musicApp: return true
                case .localFiles: return installed.contains(kind)
                case .browser: return webPlayers && installed.contains(kind)
                }
            }
        }
    }

    @Published var choice: Choice {
        didSet { defaults.set(choice.rawValue, forKey: Self.key) }
    }

    /// Whether Automatic also follows YouTube Music, SoundCloud and Amazon
    /// Music in browsers. Off by default: following a browser asks macOS for
    /// permission to control it, which someone who doesn't play music in it
    /// shouldn't be asked.
    @Published var followsWebPlayers: Bool {
        didSet { defaults.set(followsWebPlayers, forKey: Self.webKey) }
    }

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        choice = Choice(rawValue: defaults.string(forKey: Self.key) ?? "") ?? .automatic
        followsWebPlayers = defaults.bool(forKey: Self.webKey)
    }

    /// The players a choice follows.
    nonisolated static func players(for choice: Choice, webPlayers: Bool) -> [PlayerKind] {
        guard choice == .automatic else { return choice.players }
        return webPlayers ? PlayerKind.allCases : PlayerKind.apps
    }

    /// The players followed now.
    var followedPlayers: [PlayerKind] { Self.players(for: choice, webPlayers: followsWebPlayers) }

    /// The players to follow, as they change.
    var playersPublisher: AnyPublisher<[PlayerKind], Never> {
        $choice.combineLatest($followsWebPlayers)
            .map { Self.players(for: $0, webPlayers: $1) }
            .removeDuplicates()
            .eraseToAnyPublisher()
    }

    /// Players installed on this Mac. Never asks anything of them.
    static var installedPlayers: Set<PlayerKind> {
        Set(PlayerKind.allCases.filter { NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0.bundleID) != nil })
    }
}

/// "Player: Automatic / Apple Music / Spotify / …", for the menu and Settings.
struct PlayerChoicePicker: View {
    @ObservedObject private var options = PlayerOptions.shared

    var body: some View {
        Picker("Player", selection: $options.choice) {
            ForEach(PlayerOptions.Choice.offered(installed: PlayerOptions.installedPlayers, current: options.choice,
                                                 webPlayers: options.followsWebPlayers)) { Text($0.title).tag($0) }
        }
        .pickerStyle(.menu)
        .help("Which music player Lunalyr follows")
    }
}
