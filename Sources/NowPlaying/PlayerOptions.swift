import Combine
import SwiftUI

/// Which players Lunalyr follows. Automatic follows whichever is playing;
/// choosing one means the other is never sent anything at all.
@MainActor
final class PlayerOptions: ObservableObject {
    static let shared = PlayerOptions()
    static let key = "custom.player"

    enum Choice: String, CaseIterable, Identifiable {
        case automatic, appleMusic, spotify

        var id: String { rawValue }

        var title: String {
            switch self {
            case .automatic: return "Automatic"
            case .appleMusic: return "Apple Music"
            case .spotify: return "Spotify"
            }
        }

        var players: [PlayerKind] {
            switch self {
            case .automatic: return PlayerKind.allCases
            case .appleMusic: return [.appleMusic]
            case .spotify: return [.spotify]
            }
        }

        /// The choice that follows `kind` and nothing else.
        static func only(_ kind: PlayerKind) -> Choice {
            switch kind {
            case .appleMusic: return .appleMusic
            case .spotify: return .spotify
            }
        }
    }

    @Published var choice: Choice {
        didSet { defaults.set(choice.rawValue, forKey: Self.key) }
    }

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        choice = Choice(rawValue: defaults.string(forKey: Self.key) ?? "") ?? .automatic
    }

    /// The players to follow, as they change.
    var playersPublisher: AnyPublisher<[PlayerKind], Never> {
        $choice.map(\.players).removeDuplicates().eraseToAnyPublisher()
    }
}

/// "Player: Automatic / Apple Music / Spotify", for the menu and Settings.
struct PlayerChoicePicker: View {
    @ObservedObject private var options = PlayerOptions.shared

    var body: some View {
        Picker("Player", selection: $options.choice) {
            ForEach(PlayerOptions.Choice.allCases) { Text($0.title).tag($0) }
        }
        .pickerStyle(.menu)
        .help("Which music player Lunalyr follows")
    }
}
