import Foundation

/// Whether a bundled web scene (or Star Trails' sky) takes the album cover's
/// colours or keeps its own. Light 3D keeps its existing `custom.light3DColours` setting.
enum VisualizerSceneColours: String, CaseIterable, Identifiable {
    case album, original
    var id: Self { self }
}

/// The visualiser engine's own preferences. Keys are prefixed `custom.` like
/// the rest of Lunalyr's additions; defaults are `ShippedDefaults`.
@MainActor
final class VisualizerPreferences: ObservableObject {
    static let shared = VisualizerPreferences()
    nonisolated static let punchRange: ClosedRange<Double> = 0...2

    enum Key {
        static let punch = "custom.visualizerPunch"
        static let wavefieldColours = "custom.wavefieldColours"
        static let wavecoreColours = "custom.wavecoreColours"
        static let starTrailsColours = "custom.starTrailsColours"
        static let heavyScenesOnMainDisplay = "custom.visualizerHeavyScenesOnMainDisplay"
    }

    private let defaults: UserDefaults

    /// Transient strength: 1 is the shipped beat response, 0 removes it.
    /// Reduce Motion and Dim Flashing Lights override it with 0. Readers
    /// clamp it too (see `VisualizerResponse`), so only the stored value is
    /// normalised here.
    @Published var punch: Double {
        didSet { defaults.set(Self.clampedPunch(punch), forKey: Key.punch) }
    }
    @Published var wavefieldColours: VisualizerSceneColours {
        didSet { defaults.set(wavefieldColours.rawValue, forKey: Key.wavefieldColours) }
    }
    @Published var wavecoreColours: VisualizerSceneColours {
        didSet { defaults.set(wavecoreColours.rawValue, forKey: Key.wavecoreColours) }
    }
    /// Star Trails' sky: the album cover's colours, or the original violet nebula.
    @Published var starTrailsColours: VisualizerSceneColours {
        didSet { defaults.set(starTrailsColours.rawValue, forKey: Key.starTrailsColours) }
    }
    /// Web scenes cost a WebKit process and a WebGL scene per display. When
    /// on, other displays show the closest native scene instead.
    @Published var heavyScenesOnMainDisplayOnly: Bool {
        didSet { defaults.set(heavyScenesOnMainDisplayOnly, forKey: Key.heavyScenesOnMainDisplay) }
    }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        punch = Self.clampedPunch(defaults.object(forKey: Key.punch) as? Double ?? ShippedDefaults.visualizerPunch)
        wavefieldColours = Self.storedWavefieldColours(defaults)
        wavecoreColours = Self.storedWavecoreColours(defaults)
        starTrailsColours = VisualizerSceneColours(rawValue: defaults.string(forKey: Key.starTrailsColours) ?? "") ?? .album
        heavyScenesOnMainDisplayOnly = defaults.object(forKey: Key.heavyScenesOnMainDisplay) as? Bool
            ?? ShippedDefaults.heavyScenesOnMainDisplayOnly
    }

    /// The scene choices a monitor's look keeps (`MonitorLookKeys.wallpaper`):
    /// the web scenes' colours, as each display runs its own scene. Beat
    /// punch and Star Trails' sky are not: every display draws one shared
    /// simulation with them.
    static let lookKeys = [Key.wavefieldColours, Key.wavecoreColours]

    /// Rereads the scene choices, after Settings moved to another monitor,
    /// as `init` reads them.
    func reload() {
        let wavefield = Self.storedWavefieldColours(defaults)
        if wavefieldColours != wavefield { wavefieldColours = wavefield }
        let wavecore = Self.storedWavecoreColours(defaults)
        if wavecoreColours != wavecore { wavecoreColours = wavecore }
    }

    // One reader per value, shared by `init` and `reload`.

    private static func storedWavefieldColours(_ defaults: UserDefaults) -> VisualizerSceneColours {
        VisualizerSceneColours(rawValue: defaults.string(forKey: Key.wavefieldColours) ?? "")
            ?? ShippedDefaults.wavefieldColours
    }

    /// Unchanged in November 2026: the album's colours.
    private static func storedWavecoreColours(_ defaults: UserDefaults) -> VisualizerSceneColours {
        VisualizerSceneColours(rawValue: defaults.string(forKey: Key.wavecoreColours) ?? "") ?? .album
    }

    /// Takes on the app's choices that no monitor keeps for itself.
    func follow(appPreferencesOf source: VisualizerPreferences) {
        if punch != source.punch { punch = source.punch }
        if starTrailsColours != source.starTrailsColours { starTrailsColours = source.starTrailsColours }
        if heavyScenesOnMainDisplayOnly != source.heavyScenesOnMainDisplayOnly {
            heavyScenesOnMainDisplayOnly = source.heavyScenesOnMainDisplayOnly
        }
    }

    /// The colour choice for a web engine; nil when it has no choice here.
    func colours(for engine: WebVisualizerEngine) -> VisualizerSceneColours? {
        switch engine {
        case .wavefield: return wavefieldColours
        case .wavecore: return wavecoreColours
        case .light3D: return nil
        }
    }

    func setColours(_ colours: VisualizerSceneColours, for engine: WebVisualizerEngine) {
        switch engine {
        case .wavefield: wavefieldColours = colours
        case .wavecore: wavecoreColours = colours
        case .light3D: break
        }
    }

    nonisolated static func clampedPunch(_ value: Double) -> Double {
        value.isFinite ? min(punchRange.upperBound, max(punchRange.lowerBound, value)) : 1
    }
}
