import AppKit
import CoreGraphics

/// Settings › Music video › "Show the video on". Until the user picks, the
/// video follows "Heavy scenes on main display only", as before; a pick
/// overrides that for the video alone.
enum MusicVideoDisplayChoice: Hashable, Sendable {
    case followHeavyScenes
    case mainOnly
    case all
    /// One display, by its stable UUID (`CGDisplayCreateUUIDFromDisplayID`,
    /// not its index or `CGDirectDisplayID`, which change between plugs and
    /// restarts). `name` is what it was called when picked, for when it is
    /// unplugged; equality and hashing use the UUID only.
    case display(uuid: String, name: String)

    static let key = "custom.musicVideoDisplays"

    init(stored: Any?) {
        guard let stored = stored as? [String: String] else { self = .followHeavyScenes; return }
        switch stored["mode"] {
        case "main": self = .mainOnly
        case "all": self = .all
        case "display":
            if let uuid = stored["uuid"], !uuid.isEmpty {
                self = .display(uuid: uuid, name: stored["name"] ?? "")
            } else {
                self = .followHeavyScenes
            }
        default: self = .followHeavyScenes
        }
    }

    /// What is written to defaults; nil (no key) while following heavy scenes.
    var stored: [String: String]? {
        switch self {
        case .followHeavyScenes: return nil
        case .mainOnly: return ["mode": "main"]
        case .all: return ["mode": "all"]
        case .display(let uuid, let name): return ["mode": "display", "uuid": uuid, "name": name]
        }
    }

    static func == (a: Self, b: Self) -> Bool {
        switch (a, b) {
        case (.followHeavyScenes, .followHeavyScenes), (.mainOnly, .mainOnly), (.all, .all): return true
        case let (.display(x, _), .display(y, _)): return x == y
        default: return false
        }
    }

    func hash(into hasher: inout Hasher) {
        switch self {
        case .followHeavyScenes: hasher.combine(0)
        case .mainOnly: hasher.combine(1)
        case .all: hasher.combine(2)
        case .display(let uuid, _): hasher.combine(3); hasher.combine(uuid)
        }
    }
}

/// A connected display, as the menu lists it.
struct MusicVideoDisplay: Hashable, Sendable {
    var id: CGDirectDisplayID
    var uuid: String
    var name: String
}

enum MusicVideoDisplays {
    /// The displays connected now, by their localized names.
    @MainActor
    static func connected() -> [MusicVideoDisplay] {
        NSScreen.screens.compactMap { screen in
            guard let id = screen.versoDisplayID, let uuid = uuid(of: id) else { return nil }
            return MusicVideoDisplay(id: id, uuid: uuid, name: screen.localizedName)
        }
    }

    /// The display's stable UUID, the same after unplugging or restarting.
    static func uuid(of id: CGDirectDisplayID) -> String? {
        guard let uuid = CGDisplayCreateUUIDFromDisplayID(id)?.takeRetainedValue() else { return nil }
        return CFUUIDCreateString(nil, uuid) as String?
    }

    /// The display a single-display pick lands on: the picked one while it
    /// is connected, otherwise the main display.
    static func target(uuid: String, connected: [MusicVideoDisplay], main: CGDirectDisplayID) -> CGDirectDisplayID {
        connected.first { $0.uuid == uuid }?.id ?? main
    }

    /// Whether the wallpaper on `display` plays the video.
    static func shows(_ choice: MusicVideoDisplayChoice, heavyScenesOnMainDisplayOnly: Bool,
                      display: CGDirectDisplayID, main: CGDirectDisplayID, connected: [MusicVideoDisplay]) -> Bool {
        switch choice {
        case .followHeavyScenes: return !heavyScenesOnMainDisplayOnly || display == main
        case .mainOnly: return display == main
        case .all: return true
        case .display(let uuid, _): return display == target(uuid: uuid, connected: connected, main: main)
        }
    }

    /// The menu: the three general choices, each connected display, and a
    /// picked display that is unplugged (so the menu still shows the pick).
    static func options(selected: MusicVideoDisplayChoice, connected: [MusicVideoDisplay]) -> [MusicVideoDisplayChoice] {
        var options: [MusicVideoDisplayChoice] = [.followHeavyScenes, .mainOnly, .all]
        options += connected.map { .display(uuid: $0.uuid, name: $0.name) }
        if case .display = selected, !options.contains(selected) { options.append(selected) }
        return options
    }

    static func title(_ choice: MusicVideoDisplayChoice, heavyScenesOnMainDisplayOnly: Bool,
                      connected: [MusicVideoDisplay]) -> String {
        switch choice {
        case .followHeavyScenes:
            return heavyScenesOnMainDisplayOnly ? "Like heavy scenes (main display)" : "Like heavy scenes (all displays)"
        case .mainOnly: return "Main display only"
        case .all: return "All displays"
        case .display(let uuid, let name):
            // The display's current name when connected.
            if let live = connected.first(where: { $0.uuid == uuid }) { return live.name }
            return name.isEmpty ? "A display that isn’t connected" : name
        }
    }

    /// A line under the menu, when there is something to say.
    static func caption(_ choice: MusicVideoDisplayChoice, heavyScenesOnMainDisplayOnly: Bool,
                        connected: [MusicVideoDisplay], fallback: String) -> String? {
        let several = connected.count > 1
        switch choice {
        case .followHeavyScenes:
            guard heavyScenesOnMainDisplayOnly else {
                return several ? "Every display plays the video, muted and kept to the song." : nil
            }
            return "“Heavy scenes on main display only” is on, so other displays show \(fallback). Choose All displays or one display to change that for the video."
        case .mainOnly:
            return several ? "Other displays show \(fallback)." : nil
        case .all:
            return several ? "Every display plays the video, muted and kept to the song." : nil
        case .display(let uuid, let name):
            if connected.contains(where: { $0.uuid == uuid }) {
                return several ? "Other displays show \(fallback)." : nil
            }
            let called = name.isEmpty ? "The chosen display" : name
            return "\(called) isn’t connected, so the main display shows the video for now."
        }
    }
}
