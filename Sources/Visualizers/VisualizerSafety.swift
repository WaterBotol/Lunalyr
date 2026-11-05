import AppKit
import MediaAccessibility

/// How strongly one frame of a visualiser may react to transients.
///
/// Two system settings ask for calmer visuals: Reduce Motion, which SwiftUI
/// hands each view through its environment, and Dim Flashing Lights
/// (Accessibility › Display), which only MediaAccessibility reports. Either
/// one removes beat flashes, slows pattern changes and caps how quickly the
/// mean brightness may rise. Only Reduce Motion also slows the scene's
/// overall motion, which is what it asks for.
struct VisualizerResponse: Equatable, Sendable {
    var reduceMotion = false
    var dimFlashingLights = false
    /// The Punch control: transient strength, where 1 is the shipped look.
    var punch = 1.0

    /// Beat flashes are removed and brightness changes are rate-limited.
    var calm: Bool { reduceMotion || dimFlashingLights }
    /// Multiplies the scene clock. Reduce Motion keeps phrasing, not travel.
    var motionScale: Double { reduceMotion ? 0.15 : 1 }
    /// Multiplies beat, treble-beat and flux before each scene's own scaling.
    var transientScale: Float { calm ? 0 : Float(VisualizerPreferences.clampedPunch(punch)) }
    /// Pattern morphs and modal changes run at this fraction of real time.
    var patternRate: Float { calm ? 0.25 : 1 }

    init(reduceMotion: Bool = false, dimFlashingLights: Bool = false, punch: Double = 1) {
        self.reduceMotion = reduceMotion
        self.dimFlashingLights = dimFlashingLights
        self.punch = punch.isFinite ? punch : 1
    }

    /// The live system state for a view whose environment reports Reduce Motion.
    @MainActor
    static func current(reduceMotion: Bool) -> Self {
        Self(reduceMotion: reduceMotion, dimFlashingLights: VisualizerSafety.shared.dimFlashingLights,
             punch: VisualizerPreferences.shared.punch)
    }
}

/// Block-based notification observers that are removed when their owner goes
/// away. A view or main-actor object cannot reach its own state from its
/// nonisolated deinit, so it holds one of these.
final class NotificationTokens {
    private var tokens: [(center: NotificationCenter, token: NSObjectProtocol)] = []

    var count: Int { tokens.count }

    func observe(_ name: Notification.Name, object: Any? = nil, on center: NotificationCenter = .default,
                 using block: @escaping @Sendable (Notification) -> Void) {
        tokens.append((center, center.addObserver(forName: name, object: object, queue: .main, using: block)))
    }

    func removeAll() {
        for entry in tokens { entry.center.removeObserver(entry.token) }
        tokens.removeAll()
    }

    deinit { removeAll() }
}

/// Observes the system Dim Flashing Lights preference (macOS 13.3+).
@MainActor
final class VisualizerSafety: ObservableObject {
    static let shared = VisualizerSafety()
    static let changedNotification = Notification.Name(kMADimFlashingLightsChangedNotification as String)

    @Published private(set) var dimFlashingLights: Bool
    private let read: () -> Bool
    private let observers = NotificationTokens()

    /// `read` is injectable so tests never depend on this Mac's setting.
    init(read: @escaping () -> Bool = { MADimFlashingLightsEnabled() }, observe: Bool = true) {
        self.read = read
        dimFlashingLights = read()
        guard observe else { return }
        // MediaAccessibility posts its change to the local centre, which is
        // NotificationCenter.default. The display-options notification is a
        // second trigger in case the preference changes without it.
        observers.observe(Self.changedNotification) { [weak self] _ in
            Task { @MainActor [weak self] in self?.refresh() }
        }
        observers.observe(NSWorkspace.accessibilityDisplayOptionsDidChangeNotification,
                          on: NSWorkspace.shared.notificationCenter) { [weak self] _ in
            Task { @MainActor [weak self] in self?.refresh() }
        }
    }

    func refresh() {
        let value = read()
        if value != dimFlashingLights { dimFlashingLights = value }
    }
}

/// Caps how fast the mean luminance of a visualiser may rise.
///
/// WCAG 2.3.1 counts a flash as a pair of opposing changes of at least 10% of
/// the maximum relative luminance. Limiting every rise to `maxRisePerSecond`
/// bounds the number of such pairs to 2.5 a second for the whole frame, while
/// falls stay immediate so the limiter never adds a dark flash of its own.
/// The Metal kernel (ExposureLimiter.metal) and the WebKit limiter script
/// implement this same model; this copy documents and tests it.
struct ExposureLimiter: Sendable {
    /// Relative luminance per second, in 0...1 linear units.
    static let maxRisePerSecond: Float = 0.25
    /// Longest step one frame may claim, so a stall cannot release a flash.
    static let maxStep: Float = 0.1

    /// The displayed mean. A new surface starts from black and fades in;
    /// nil adopts the next measurement as-is.
    private(set) var shown: Float? = 0

    /// Returns the gain to multiply sRGB-encoded output by so the displayed
    /// mean follows the rate-limited target. `mean` is linear luminance.
    mutating func gain(forMean mean: Float, dt: Float) -> Float {
        let mean = mean.isFinite ? max(0, mean) : 0
        let step = Self.maxRisePerSecond * min(Self.maxStep, max(0, dt.isFinite ? dt : 0))
        let target = shown.map { min(mean, $0 + step) } ?? mean
        shown = target
        guard mean > 0.0001 else { return 1 }
        return pow(min(1, target / mean), 1 / 2.2)
    }

    /// The limiter just turned on: take the current brightness as it is
    /// rather than dimming what is already on screen.
    mutating func adopt() { shown = nil }

    /// Linear relative luminance of an sRGB-encoded colour.
    static func luminance(red: Float, green: Float, blue: Float) -> Float {
        func linear(_ c: Float) -> Float { c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4) }
        return 0.2126 * linear(red) + 0.7152 * linear(green) + 0.0722 * linear(blue)
    }
}
